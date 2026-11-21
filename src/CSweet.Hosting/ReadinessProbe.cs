using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CSweet.Hosting;

internal sealed class ReadinessProbe(HttpClient http) : IReadinessProbe
{
    public async Task<bool> VerifyAsync(Deployment row, CancellationToken ct)
    {
        var origin = $"https://{row.Host}";
        using var health = await http.GetAsync(origin + "/health", ct);
        if (health.StatusCode != HttpStatusCode.OK) return false;
        var nonce = Wire.RandomHex(16);
        using var result = await http.GetAsync($"{origin}/api/auth/hosted/readiness?nonce={nonce}", ct);
        if (result.StatusCode != HttpStatusCode.OK) return false;
        var body = await result.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (!body.TryGetProperty("proof", out var proof) || proof.GetString() is not { Length: 64 } actual) return false;
        var message = string.Join('\n', row.Id.ToString(), row.Purchase.OwnerId, row.Host, row.Release, nonce);
        var expected = HMACSHA256.HashData(Convert.FromHexString(row.ProofKey), Encoding.UTF8.GetBytes(message));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Convert.ToHexStringLower(expected)), Encoding.ASCII.GetBytes(actual));
    }
}
