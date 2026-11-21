using System.Text.Json;

namespace CSweet.Hosting;

internal sealed class CloudflareIngress(HttpClient http, string account, string zone) : IWorkspaceIngress
{
    private readonly string tunnels = $"accounts/{account}/cfd_tunnel";
    private readonly string dns = $"zones/{zone}/dns_records";

    private async Task<JsonElement> Call(HttpMethod method, string path, CancellationToken ct, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (!envelope.GetProperty("success").GetBoolean())
            throw new InvalidOperationException("Cloudflare operation failed");
        return envelope.GetProperty("result");
    }

    private async Task<string?> Tunnel(string name, CancellationToken ct)
    {
        var result = await Call(HttpMethod.Get, $"{tunnels}?name={Uri.EscapeDataString(name)}&is_deleted=false", ct);
        foreach (var item in result.EnumerateArray())
            if (item.GetProperty("name").GetString() == name) return item.GetProperty("id").GetString();
        return null;
    }

    private static bool Matches(JsonElement record, string host, string target) =>
        record.GetProperty("type").GetString() == "CNAME" && record.GetProperty("name").GetString() == host &&
        record.GetProperty("content").GetString() == target;

    public async Task<string> EnsureAsync(string name, string host, CancellationToken ct)
    {
        var id = await Tunnel(name, ct);
        if (id == null)
        {
            var created = await Call(HttpMethod.Post, tunnels, ct, new { name, config_src = "cloudflare" });
            id = created.GetProperty("id").GetString()!;
        }
        object[] rules = [new { hostname = host, path = "^/internal(/.*)?$", service = "http_status:404" },
            new { hostname = host, service = "http://127.0.0.1:8181" }, new { service = "http_status:404" }];
        await Call(HttpMethod.Put, $"{tunnels}/{id}/configurations", ct, new { config = new { ingress = rules } });
        var target = $"{id}.cfargotunnel.com";
        var records = await Call(HttpMethod.Get, $"{dns}?name={host}", ct);
        if (records.GetArrayLength() == 0)
            await Call(HttpMethod.Post, dns, ct, new { type = "CNAME", name = host, content = target, proxied = true, ttl = 1 });
        else if (records.GetArrayLength() != 1 || !Matches(records[0], host, target) || !records[0].GetProperty("proxied").GetBoolean())
            throw new InvalidOperationException("Workspace hostname already has a different route");
        var token = (await Call(HttpMethod.Get, $"{tunnels}/{id}/token", ct)).GetString();
        return !string.IsNullOrWhiteSpace(token) ? token : throw new InvalidOperationException("Missing tenant tunnel token");
    }

    public async Task RemoveAsync(string name, string host, CancellationToken ct)
    {
        var id = await Tunnel(name, ct);
        if (id == null) return;
        var records = await Call(HttpMethod.Get, $"{dns}?name={host}", ct);
        foreach (var record in records.EnumerateArray())
            if (Matches(record, host, $"{id}.cfargotunnel.com"))
                await Call(HttpMethod.Delete, $"{dns}/{record.GetProperty("id").GetString()}", ct);
        await Call(HttpMethod.Delete, $"{tunnels}/{id}", ct);
    }
}
