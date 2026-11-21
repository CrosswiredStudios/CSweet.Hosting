using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CSweet.Hosting;

internal static class TrueNasCommands
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args[0] is not ("preflight" or "prepare-template"))
        {
            Console.Error.WriteLine("Commands: preflight | prepare-template --image FILE --sha256 HASH --dataset POOL/DATASET --nic INTERFACE [--memory MiB]");
            return 2;
        }
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var settings = NasSettings.Load(config);
        var name = Config.Required(config, "TEMPLATE_NAME");
        var nas = new TrueNasConnection(settings);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(60));
        if (args[0] == "preflight")
        {
            await nas.RunAsync(async (rpc, ct) =>
            {
                Console.WriteLine($"Virtualization: {await rpc.CallAsync("vm.supports_virtualization", ct)}");
                var bytes = (await rpc.CallAsync("vm.get_available_memory", ct)).GetInt64();
                Console.WriteLine($"Available guest memory (GiB): {bytes / (double)(1L << 30):F1}");
                var choices = await rpc.CallAsync("vm.device.nic_attach_choices", ct);
                Console.WriteLine("VM network choices: " + string.Join(", ", choices.EnumerateObject().Select(p => p.Name)));
                var vm = await TrueNasProvider.FindAsync(rpc, "vm.query", "name", name, ct);
                Console.WriteLine("Template: " + (vm?.GetProperty("status").GetProperty("state").GetString() ?? "not created"));
                return true;
            }, timeout.Token);
            return 0;
        }
        var options = new ConfigurationBuilder().AddCommandLine(args[1..]).Build();
        var image = Config.Required(options, "image");
        var sha = Config.Required(options, "sha256");
        var parent = Config.Required(options, "dataset");
        var nic = Config.Required(options, "nic");
        var memory = options.GetValue("memory", 6144);
        if (!Regex.IsMatch(image, @"\A[a-zA-Z0-9_.-]+\.qcow2\z") || !Regex.IsMatch(sha, @"\A[a-f0-9]{64}\z")
            || parent.Split('/').Any(s => !Regex.IsMatch(s, @"\A[a-zA-Z0-9_-]+\z")) ||
            !Regex.IsMatch(name, @"\A[A-Za-z][A-Za-z0-9_-]*\z") || memory <= 0)
            throw new ArgumentException("Invalid QCOW2 basename, digest, VM name, dataset or memory");
        using (var file = File.OpenRead(Path.Combine(settings.Media, image)))
        {
            byte[] header = new byte[4];
            file.ReadExactly(header);
            file.Position = 0;
            if (!header.SequenceEqual(new byte[] { 0x51, 0x46, 0x49, 0xfb }) ||
                Convert.ToHexStringLower(await SHA256.HashDataAsync(file, timeout.Token)) != sha)
                throw new InvalidDataException("Ubuntu QCOW2 verification failed");
        }
        await nas.RunAsync(async (rpc, ct) =>
        {
            var dataset = $"{parent}/{name}";
            var disk = "/dev/zvol/" + dataset;
            if (!(await rpc.CallAsync("vm.supports_virtualization", ct)).GetBoolean() ||
                !(await rpc.CallAsync("vm.device.nic_attach_choices", ct)).TryGetProperty(nic, out _))
                throw new InvalidOperationException("Virtualization or selected network unavailable");
            if (await TrueNasProvider.FindAsync(rpc, "vm.query", "name", name, ct) != null ||
                await TrueNasProvider.FindAsync(rpc, "pool.dataset.query", "id", dataset, ct) != null)
                throw new InvalidOperationException("Template or disk already exists");
            await rpc.CallAsync("pool.dataset.create", ct, new { name = dataset, type = "VOLUME", volsize = 64L << 30, sparse = true });
            var job = (await rpc.CallAsync("vm.device.convert", ct, new { source = $"{settings.NasMedia}/{image}", destination = disk })).GetInt64();
            while (true)
            {
                var status = await TrueNasProvider.FindAsync(rpc, "core.get_jobs", "id", job, ct);
                switch (status?.GetProperty("state").GetString())
                {
                    case "SUCCESS": break;
                    case "RUNNING": case "WAITING": await Task.Delay(3000, ct); continue;
                    default: throw new InvalidOperationException("Disk import incomplete. Review the TrueNAS job and template disk.");
                }
                break;
            }
            object[] devices = [new { attributes = new { dtype = "DISK", path = disk, type = "VIRTIO" }, order = 10 },
                new { attributes = new { dtype = "NIC", type = "VIRTIO", nic_attach = nic }, order = 20 }];
            await rpc.CallAsync("vm.create", ct, new { name, memory, vcpus = 1, cores = 2, bootloader = "UEFI",
                autostart = false, time = "UTC", devices });
            return true;
        }, timeout.Token);
        Console.WriteLine($"Created stopped template {name}.");
        return 0;
    }
}
