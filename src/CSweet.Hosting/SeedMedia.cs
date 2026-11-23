using System.Diagnostics;
using System.Text.Json;

namespace CSweet.Hosting;

internal sealed class SeedMedia(string media)
{
    public void Remove(string name) => File.Delete(Path.Combine(media, name + ".iso"));

    internal static string? LocalImageTag(string release)
    {
        const string prefix = "ghcr.io/crosswiredstudios/csweet-headquarters-hosted@sha256:";
        if (!release.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var digest = release[prefix.Length..];
        if (digest.Length != 64 || !digest.All(c => char.IsAsciiHexDigitLower(c)))
            throw new InvalidOperationException("Invalid Headquarters digest");
        return "csweet-headquarters:sha256-" + digest;
    }

    internal static string UserData(GuestBootstrap guest)
    {
        static object FileEntry(string path, byte[] data, string permissions = "0600") =>
            new { path, permissions, encoding = "b64", content = Convert.ToBase64String(data) };
        List<object> files = [FileEntry("/etc/csweet-hosting/deployment.json", JsonSerializer.SerializeToUtf8Bytes(guest, Wire.Json))];
        if (LocalImageTag(guest.Release) is { } tag)
            files.Add(FileEntry("/opt/csweet-hosting/local-image", System.Text.Encoding.ASCII.GetBytes(tag)));
        foreach (var name in new[] { "bootstrap.py", "bootstrap.sh", "bootstrap.service" })
        {
            using var stream = typeof(SeedMedia).Assembly.GetManifestResourceStream("linux." + name)
                ?? throw new InvalidOperationException("Missing guest bootstrap");
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            var path = name == "bootstrap.service" ? "/etc/systemd/system/csweet-bootstrap.service" : "/opt/csweet-hosting/" + name;
            files.Add(FileEntry(path, bytes.ToArray(), name == "bootstrap.sh" ? "0700" : "0600"));
        }
        string[][] commands = [["systemctl", "daemon-reload"], ["systemctl", "enable", "--now", "--no-block", "csweet-bootstrap.service"]];
        return "#cloud-config\n" + JsonSerializer.Serialize(new { ssh_pwauth = false, disable_root = true, write_files = files, runcmd = commands });
    }

    public async Task WriteAsync(string name, GuestBootstrap guest, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Build NoCloud media in the Linux Hosting container.");
        var scratch = Directory.CreateTempSubdirectory("csweet-seed-");
        var temporaryIso = Path.Combine(media, $".{name}-{Guid.NewGuid():N}.tmp");
        try
        {
            var metadata = new Dictionary<string, string> { ["instance-id"] = guest.Deployment, ["local-hostname"] = guest.Hostname.Split('.')[0] };
            await File.WriteAllTextAsync(Path.Combine(scratch.FullName, "meta-data"), JsonSerializer.Serialize(metadata), ct);
            await File.WriteAllTextAsync(Path.Combine(scratch.FullName, "user-data"), UserData(guest), ct);
            // Stream ISO bytes directly into a private sibling file so rename is atomic on the mounted share.
            var start = new ProcessStartInfo("/bin/bash") { WorkingDirectory = scratch.FullName, RedirectStandardOutput = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "create-seed.sh"));
            if (LocalImageTag(guest.Release) is { } tag)
            {
                start.ArgumentList.Add(guest.Release);
                start.ArgumentList.Add(tag);
            }
            using (var output = new FileStream(temporaryIso, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            }))
            using (var process = Process.Start(start) ?? throw new IOException("Cannot start genisoimage"))
            {
                try
                {
                    await process.StandardOutput.BaseStream.CopyToAsync(output, ct);
                    await process.WaitForExitAsync(ct);
                    if (process.ExitCode != 0) throw new IOException("Seed ISO generation failed");
                    output.Flush(flushToDisk: true);
                }
                finally
                {
                    if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
                }
            }
            File.Move(temporaryIso, Path.Combine(media, name + ".iso"), overwrite: true);
        }
        finally { File.Delete(temporaryIso); scratch.Delete(recursive: true); }
    }
}
