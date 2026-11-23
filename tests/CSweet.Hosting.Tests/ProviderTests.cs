using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace CSweet.Hosting.Tests;

public class ProviderTests
{
    private sealed class Nas : INasRpc
    {
        public List<string> Calls { get; } = [];
        public bool Shared { get; set; }
        public bool OrphanDisk { get; set; }
        public bool ForeignDisk { get; set; }
        private bool cloned, running;
        private string? seed;
        public Task<JsonElement> CallAsync(string method, CancellationToken ct, params object[] parameters)
        {
            Calls.Add(method);
            object Vm(bool template)
            {
                var path = template || Shared ? "/dev/zvol/p/template" :
                    ForeignDisk ? "/dev/zvol/p/template_other" : "/dev/zvol/p/template_hqfixture";
                List<object> devices = [new { attributes = new { dtype = "DISK", path } }];
                if (!template && seed != null) devices.Add(new { attributes = new { dtype = "CDROM", path = seed } });
                return new { id = template ? 1 : 2, status = new { state = !template && running ? "RUNNING" : "STOPPED" }, devices };
            }
            object? result = null;
            switch (method)
            {
                case "vm.query":
                    var template = (string)((object[][])parameters[0])[0][2] == "ubuntu";
                    result = template || cloned ? new[] { Vm(template) } : Array.Empty<object>();
                    break;
                case "vm.device.disk_choices":
                    var disks = new Dictionary<string,string> { ["/dev/zvol/p/template"] = "p/template" };
                    if (cloned || OrphanDisk) disks["/dev/zvol/p/template_hqfixture"] = "p/template_hqfixture";
                    result = disks;
                    break;
                case "vm.clone": cloned = true; throw new IOException("Lost reply");
                case "vm.device.create":
                    seed = JsonSerializer.SerializeToElement(parameters[0]).GetProperty("attributes").GetProperty("path").GetString();
                    break;
                case "vm.start": running = true; break;
                case "vm.stop": running = false; break;
                case "vm.delete": cloned = false; result = true; break;
                default: throw new InvalidOperationException(method);
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(result));
        }
    }

    [Fact]
    public async Task TrueNas_rediscovers_clone_and_refuses_shared_disk_deletion()
    {
        var rpc = new Nas();
        Task<bool> Run(bool remove = false) => TrueNasProvider.ReconcileAsync(rpc, new("hqfixture", "ubuntu"), "/mnt/p/media", remove, default);
        await Assert.ThrowsAsync<IOException>(() => Run());
        await Run();
        await Run();
        Assert.Single(rpc.Calls, m => m == "vm.clone");
        Assert.Single(rpc.Calls, m => m == "vm.device.create");
        Assert.Single(rpc.Calls, m => m == "vm.start");
        rpc.Shared = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(true));
        Assert.DoesNotContain("vm.delete", rpc.Calls);
        rpc.Shared = false;
        rpc.ForeignDisk = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(true));
        Assert.DoesNotContain("vm.delete", rpc.Calls);
        rpc.ForeignDisk = false;
        Assert.False(await Run(true));
        Assert.True(await Run(true));
        Assert.True(await Run(true));
        Assert.Single(rpc.Calls, m => m == "vm.delete");
        rpc.OrphanDisk = true;
        Assert.False(await Run(true));
    }

    [Fact]
    public void Cleanup_removes_this_orders_seed_generations_only()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            string[] mine = ["hqone.iso", "hqone-release-0123456789ab.iso", "hqone-before-abcdef012345.iso", ".hqone-87413230be384fe78443656d035457ef.tmp"];
            string[] others = ["hqone2.iso", "hqone2-before-abcdef012345.iso", "ubuntu.iso"];
            foreach (var file in mine.Concat(others)) File.WriteAllText(Path.Combine(dir.FullName, file), "fixture");
            var seeds = new SeedMedia(dir.FullName);
            seeds.Remove("hqone");
            seeds.Remove("hqone");
            Assert.All(mine, name => Assert.False(File.Exists(Path.Combine(dir.FullName, name))));
            Assert.All(others, name => Assert.True(File.Exists(Path.Combine(dir.FullName, name))));
        }
        finally { dir.Delete(true); }
    }

    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(reply(request));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Readiness_validates_nonce_and_owner(bool correctOwner)
    {
        var id = Guid.NewGuid();
        var row = new Deployment(new(id, "buyer", DateTimeOffset.UtcNow, 0, 0, 0), "pool", "ubuntu", "digest", "allocation", Wire.RandomHex(32), "verifying");
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal(row.Host, request.RequestUri.Host);
            if (request.RequestUri.AbsolutePath == "/health") return new(HttpStatusCode.OK);
            var nonce = request.RequestUri.Query["?nonce=".Length..];
            Assert.Equal(32, nonce.Length);
            var message = string.Join('\n', id.ToString(), correctOwner ? "buyer" : "another", row.Host, row.Release, nonce);
            var proof = Convert.ToHexStringLower(HMACSHA256.HashData(Convert.FromHexString(row.ProofKey), Encoding.UTF8.GetBytes(message)));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { proof }) };
        }));
        Assert.Equal(correctOwner, await new ReadinessProbe(http).VerifyAsync(row, default));
    }

    [Fact]
    public void Private_image_seed_selects_a_local_tag_bound_to_the_purchased_digest()
    {
        var digest = new string('a', 64);
        var release = "ghcr.io/crosswiredstudios/csweet-headquarters-hosted@sha256:" + digest;
        var guest = new GuestBootstrap("id", "buyer", "hq-test.c-sweet.app", release, "proof", "tenant-token");
        var data = JsonSerializer.Deserialize<JsonElement>(SeedMedia.UserData(guest).Split('\n', 2)[1]);
        var files = data.GetProperty("write_files").EnumerateArray().ToArray();
        var local = files.Single(f => f.GetProperty("path").GetString() == "/opt/csweet-hosting/local-image");
        Assert.Equal("csweet-headquarters:sha256-" + digest,
            Encoding.UTF8.GetString(Convert.FromBase64String(local.GetProperty("content").GetString()!)));
        Assert.Null(SeedMedia.LocalImageTag("example.com/other@sha256:" + digest));
        Assert.Throws<InvalidOperationException>(() => SeedMedia.LocalImageTag(release + "x"));
    }

    [Fact]
    public async Task Linux_image_generates_readable_NoCloud_media()
    {
        if (!OperatingSystem.IsLinux()) return; // Exercised in the Linux Docker test stage.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var seed = new SeedMedia(dir.FullName);
            var guest = new GuestBootstrap(Guid.NewGuid().ToString(), "buyer", "hq-test.c-sweet.app", "digest", "proof", "tenant-token");
            await seed.WriteAsync("hqfixture", guest, default);
            async Task<string> Read(string filename)
            {
                var start = new ProcessStartInfo("isoinfo") { RedirectStandardOutput = true };
                foreach (var arg in new[] { "-R", "-i", Path.Combine(dir.FullName, "hqfixture.iso"), "-x", "/" + filename }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.Equal(0, process.ExitCode);
                return output;
            }
            Assert.Equal(guest.Deployment, JsonSerializer.Deserialize<JsonElement>(await Read("meta-data")).GetProperty("instance-id").GetString());
            var userData = await Read("user-data");
            Assert.StartsWith("#cloud-config\n", userData);
            var files = JsonSerializer.Deserialize<JsonElement>(userData.Split('\n', 2)[1]).GetProperty("write_files");
            var actual = JsonSerializer.Deserialize<GuestBootstrap>(Convert.FromBase64String(files[0].GetProperty("content").GetString()!), Wire.Json);
            Assert.Equal(guest, actual);
            Assert.Contains("csweet-bootstrap.service", userData);
            seed.Remove("hqfixture");
            Assert.Empty(dir.GetFileSystemInfos());
        }
        finally { dir.Delete(true); }
    }
}
