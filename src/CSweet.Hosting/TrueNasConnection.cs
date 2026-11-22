using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace CSweet.Hosting;

internal sealed record NasSettings(Uri Url, string CaFile, string Username, string ApiKey, string NasMedia, string Media)
{
    public static NasSettings Load(IConfiguration cfg)
    {
        var url = new Uri(Config.Required(cfg, "TRUENAS_URL"));
        var media = Config.Required(cfg, "NAS_MEDIA_PATH").TrimEnd('/');
        if (url.Scheme != "wss" || !media.StartsWith("/mnt/", StringComparison.Ordinal) || media.Split('/').Contains(".."))
            throw new InvalidOperationException("Set a WSS NAS endpoint and an absolute /mnt/ media path.");
        return new(url, "/run/secrets/nas_ca", cfg["TRUENAS_USERNAME"] ?? "csweet_hosting", Config.Secret("nas_key"), media, "/media/csweet");
    }
}

internal interface INasRpc
{
    Task<JsonElement> CallAsync(string method, CancellationToken ct, params object[] parameters);
}

internal sealed class TrueNasConnection(NasSettings settings)
{
    public async Task<T> RunAsync<T>(Func<INasRpc, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        using var root = X509Certificate2.CreateFromPem(File.ReadAllText(settings.CaFile));
        var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck, DisableCertificateDownloads = true };
        policy.CustomTrustStore.Add(root);
        using var transport = new HttpMessageInvoker(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = policy }
        });
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(settings.Url, transport, ct);
        var rpc = new NasRpc(socket);
        var login = await rpc.CallAsync("auth.login_ex", ct, new
        {
            mechanism = "API_KEY_PLAIN",
            username = settings.Username,
            api_key = settings.ApiKey,
            login_options = new { user_info = false }
        });
        if (login.ValueKind != JsonValueKind.Object ||
            !login.TryGetProperty("response_type", out var response) ||
            !response.ValueEquals("SUCCESS"))
            throw new InvalidOperationException("TrueNAS authentication failed");
        return await action(rpc, ct);
    }
}

internal sealed class NasRpc(ClientWebSocket socket) : INasRpc
{
    private int sequence;
    public async Task<JsonElement> CallAsync(string method, CancellationToken ct, params object[] parameters)
    {
        int id = ++sequence;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, method, @params = parameters });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(100));
        await socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        var buffer = new byte[8192];
        while (true)
        {
            using var message = new MemoryStream();
            ValueWebSocketReceiveResult part;
            do
            {
                part = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                if (part.MessageType != WebSocketMessageType.Text || message.Length + part.Count > 4 * 1024 * 1024)
                    throw new IOException("Invalid or interrupted NAS response");
                message.Write(buffer, 0, part.Count);
            } while (!part.EndOfMessage);
            using var doc = JsonDocument.Parse(message.ToArray());
            var body = doc.RootElement;
            if (!body.TryGetProperty("id", out var received) || received.GetInt32() != id) continue;
            if (body.TryGetProperty("error", out var error))
                throw new InvalidOperationException($"TrueNAS {method} failed ({error.GetProperty("code")})");
            return body.GetProperty("result").Clone();
        }
    }
}
