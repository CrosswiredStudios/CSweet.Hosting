using System.Text.Json;

namespace CSweet.Hosting;

internal sealed class TrueNasProvider(TrueNasConnection connection, NasSettings settings, SeedMedia seeds) : IComputeProvider
{
    public Task PrepareAsync(Allocation allocation, GuestBootstrap guest, CancellationToken ct) =>
        seeds.WriteAsync(allocation.Name, guest, ct);

    public Task EnsureRunningAsync(Allocation allocation, CancellationToken ct) =>
        connection.RunAsync((rpc, token) => ReconcileAsync(rpc, allocation, settings.NasMedia, false, token), ct);

    public async Task<bool> RemoveAsync(Allocation allocation, CancellationToken ct)
    {
        if (!await connection.RunAsync((rpc, token) => ReconcileAsync(rpc, allocation, settings.NasMedia, true, token), ct))
            return false;
        seeds.Remove(allocation.Name);
        return true;
    }

    internal static async Task<JsonElement?> FindAsync(INasRpc rpc, string method, string field, object value, CancellationToken ct)
    {
        object[][] filters = [[field, "=", value]];
        var matches = await rpc.CallAsync(method, ct, (object)filters);
        return matches.GetArrayLength() switch
        {
            0 => null, 1 => matches[0], _ => throw new InvalidOperationException("Ambiguous NAS resource")
        };
    }

    private static int Id(JsonElement vm) => vm.GetProperty("id").GetInt32();
    private static string State(JsonElement vm) => vm.GetProperty("status").GetProperty("state").GetString()!;
    private static IEnumerable<JsonElement> Devices(JsonElement vm) =>
        vm.GetProperty("devices").EnumerateArray().Select(d => d.GetProperty("attributes"));
    private static string? Type(JsonElement device) => device.GetProperty("dtype").GetString();

    private static string Disk(JsonElement vm, bool allowSeed)
    {
        var devices = Devices(vm).ToList();
        if (devices.Any(d => Type(d) is not ("DISK" or "NIC" or "DISPLAY") && !(allowSeed && Type(d) == "CDROM")))
            throw new InvalidOperationException("Unexpected devices on managed VM");
        var disk = devices.Single(d => Type(d) == "DISK").GetProperty("path").GetString();
        return disk?.StartsWith("/dev/zvol/", StringComparison.Ordinal) == true
            ? disk : throw new InvalidOperationException("Expected a dedicated zvol");
    }

    internal static async Task<bool> ReconcileAsync(INasRpc rpc, Allocation allocation, string media, bool remove, CancellationToken ct)
    {
        if (allocation.Name == allocation.Template) throw new InvalidOperationException("Allocation matches template");
        var guest = await FindAsync(rpc, "vm.query", "name", allocation.Name, ct);
        var template = await FindAsync(rpc, "vm.query", "name", allocation.Template, ct)
            ?? throw new InvalidOperationException("Template unavailable");
        var source = Disk(template, false);
        var ownedDisk = source + "_" + allocation.Name;
        async Task<bool> DiskRemovedAsync()
        {
            var choices = await rpc.CallAsync("vm.device.disk_choices", ct);
            // The template must also be visible or an empty inventory proves nothing.
            return choices.TryGetProperty(source, out _) && !choices.TryGetProperty(ownedDisk, out _);
        }
        if (guest == null && remove) return await DiskRemovedAsync();
        if (guest == null)
        {
            if (State(template) != "STOPPED") throw new InvalidOperationException("Template must be stopped");
            if (!(await rpc.CallAsync("vm.clone", ct, Id(template), allocation.Name)).GetBoolean())
                throw new InvalidOperationException("Clone incomplete");
            guest = await FindAsync(rpc, "vm.query", "name", allocation.Name, ct)
                ?? throw new InvalidOperationException("Waiting for cloned VM");
        }
        var vm = guest.Value;
        if (Id(vm) == Id(template) || Disk(vm, true) == source)
            throw new InvalidOperationException("Cannot verify clone isolation");
        var state = State(vm);
        if (remove)
        {
            if (Disk(vm, true) != ownedDisk)
                throw new InvalidOperationException("Refusing to remove a disk outside this allocation");
            if (state == "RUNNING") await rpc.CallAsync("vm.stop", ct, Id(vm));
            if (state != "STOPPED") return false;
            return (await rpc.CallAsync("vm.delete", ct, Id(vm), new { zvols = true })).GetBoolean() && await DiskRemovedAsync();
        }
        string path = $"{media}/{allocation.Name}.iso";
        var discs = Devices(vm).Where(d => Type(d) == "CDROM").ToArray();
        if (discs.Length == 0 && state == "STOPPED")
            await rpc.CallAsync("vm.device.create", ct, new { vm = Id(vm), order = 1000, attributes = new { dtype = "CDROM", path } });
        else if (discs.Length != 1 || discs[0].GetProperty("path").GetString() != path)
            throw new InvalidOperationException("Unexpected deployment media");
        if (state == "STOPPED") await rpc.CallAsync("vm.start", ct, Id(vm));
        else if (state != "RUNNING") throw new InvalidOperationException("Waiting for VM state");
        return true;
    }
}
