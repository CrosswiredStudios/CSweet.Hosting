using System.Security.Cryptography;
using System.Text.Json;

namespace CSweet.Hosting;

internal sealed record Purchase(Guid OrderId, string OwnerId, DateTimeOffset PaidAt,
    int LinkedOffices, int StandardOffices, int TeamOffices);

internal sealed record Deployment(Purchase Purchase, string Pool, string Template, string Release,
    string Name, string ProofKey, string Phase)
{
    public Guid Id => Purchase.OrderId;
    public string Host => $"hq-{Id:N}.c-sweet.app";
}

internal sealed record ReleaseTarget(string Pool, string Template, string Image);
internal sealed record Allocation(string Name, string Template);
internal sealed record GuestBootstrap(string Deployment, string Owner, string Hostname, string Release,
    string ProofKey, string TunnelToken);

internal interface IComputeProvider
{
    Task PrepareAsync(Allocation allocation, GuestBootstrap guest, CancellationToken ct);
    Task EnsureRunningAsync(Allocation allocation, CancellationToken ct);
    Task<bool> RemoveAsync(Allocation allocation, CancellationToken ct);
}

internal interface IWorkspaceIngress
{
    Task<string> EnsureAsync(string name, string host, CancellationToken ct);
    Task RemoveAsync(string name, string host, CancellationToken ct);
}

internal interface IReadinessProbe
{
    Task<bool> VerifyAsync(Deployment deployment, CancellationToken ct);
}

internal static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true
    };

    public static string RandomHex(int bytes) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));
}
