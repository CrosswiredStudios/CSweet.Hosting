using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CSweet.Hosting;

internal static class Endpoints
{
    public static void Map(WebApplication app, string serviceKey)
    {
        var credential = SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + serviceKey));
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/v1/deployments"))
            {
                var actual = SHA256.HashData(Encoding.UTF8.GetBytes(ctx.Request.Headers.Authorization.ToString()));
                if (!CryptographicOperations.FixedTimeEquals(actual, credential))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }
            await next(ctx);
        });
        app.MapGet("/health", () => Results.Ok(new { status = "up", version = Environment.GetEnvironmentVariable("HOSTING_RELEASE_VERSION") }));
        app.MapPut("/v1/deployments/{order}", Put);
        app.MapDelete("/v1/deployments/{order}", Delete);
    }

    private static async Task<IResult> Put(Guid order, HttpRequest http, DeploymentStore store,
        ReleaseTarget target, DeploymentWorker worker, CancellationToken ct)
    {
        Purchase? request;
        try { request = await JsonSerializer.DeserializeAsync<Purchase>(http.Body, Wire.Json, ct); }
        catch (JsonException) { return Results.BadRequest(); }
        if (request is null || request.OrderId != order || string.IsNullOrWhiteSpace(request.OwnerId) ||
            request.PaidAt == default || request.LinkedOffices is < 0 or > 999 ||
            request.StandardOffices is < 0 or > 999 || request.TeamOffices is < 0 or > 999)
            return Results.BadRequest();
        if (request.LinkedOffices + request.StandardOffices + request.TeamOffices != 0)
            return Results.UnprocessableEntity("This pilot provisions Headquarters only");
        var row = store.Accept(request, target);
        if (row is null) return Results.StatusCode(410);
        if (row.Purchase != request) return Results.Conflict();
        if (row.Phase is "retired" or "retiring") return Results.StatusCode(410);
        bool ready = row.Phase == "ready";
        if (!ready) worker.Wake();
        return Results.Ok(new { orderId = order, state = ready ? "Ready" : "Setting up",
            workspaceUrl = ready ? $"https://{row.Host}/" : null });
    }

    private static IResult Delete(Guid order, DeploymentStore store, DeploymentWorker worker)
    {
        if (order == Guid.Empty) return Results.BadRequest();
        if (!store.BeginRetirement(order)) return Results.NoContent();
        worker.Wake();
        return Results.Accepted();
    }
}
