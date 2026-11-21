using CSweet.Hosting;
using System.Text.RegularExpressions;

if (args.Length > 0 && !args[0].StartsWith('-'))
    return await TrueNasCommands.RunAsync(args);

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var target = new ReleaseTarget(Config.Required(config, "POOL_ID"), Config.Required(config, "TEMPLATE_NAME"),
    Config.Required(config, "HQ_IMAGE"));
if (!Regex.IsMatch(target.Image, @"\A[a-z0-9./:_-]+@sha256:[a-f0-9]{64}\z"))
    throw new InvalidOperationException("HQ_IMAGE must identify an immutable release digest.");

var nas = NasSettings.Load(config);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 8192);
builder.Services.AddSingleton(target);
builder.Services.AddSingleton(nas);
builder.Services.AddSingleton<TrueNasConnection>();
builder.Services.AddKeyedSingleton<IComputeProvider>(target.Pool, (sp, _) =>
    new TrueNasProvider(sp.GetRequiredService<TrueNasConnection>(), nas, new SeedMedia(nas.Media)));
builder.Services.AddSingleton(_ => new DeploymentStore("/state"));
builder.Services.AddHttpClient("workspace").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("cloudflare", client =>
{
    client.BaseAddress = new Uri("https://api.cloudflare.com/client/v4/");
    client.DefaultRequestHeaders.Authorization = new("Bearer", Config.Secret("cf_token"));
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<IWorkspaceIngress>(sp => new CloudflareIngress(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("cloudflare"),
    Config.Required(config, "CF_ACCOUNT_ID"), Config.Required(config, "CF_ZONE_ID")));
builder.Services.AddSingleton<IReadinessProbe>(sp => new ReadinessProbe(sp.GetRequiredService<IHttpClientFactory>().CreateClient("workspace")));
builder.Services.AddSingleton<DeploymentWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DeploymentWorker>());
var app = builder.Build();
Endpoints.Map(app, Config.Secret("service_key"));
await app.RunAsync();
return 0;

namespace CSweet.Hosting
{
    internal static class Config
    {
        public static string Required(IConfiguration cfg, string name) =>
            cfg[name] is { Length: > 0 } value ? value : throw new InvalidOperationException($"Missing {name}");

        public static string Secret(string name)
        {
            var value = File.ReadAllText(Path.Combine("/run/secrets", name)).Trim();
            return value.Length > 0 ? value : throw new InvalidOperationException($"Missing {name}");
        }
    }
}
