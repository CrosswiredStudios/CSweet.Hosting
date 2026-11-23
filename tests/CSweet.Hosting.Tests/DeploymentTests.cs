using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CSweet.Hosting.Tests;

public class DeploymentTests
{
    private sealed class Provider : IComputeProvider
    {
        public List<Allocation> Attempts { get; } = [];
        public bool Fail { get; set; }
        public bool Removed { get; private set; }
        public Task PrepareAsync(Allocation allocation, GuestBootstrap guest, CancellationToken ct) => Task.CompletedTask;
        public Task EnsureRunningAsync(Allocation allocation, CancellationToken ct)
        {
            Attempts.Add(allocation);
            if (Fail) throw new IOException("Clone reply lost");
            return Task.CompletedTask;
        }
        public Task<bool> RemoveAsync(Allocation allocation, CancellationToken ct)
        {
            Removed = true;
            return Task.FromResult(true);
        }
    }

    private sealed class Edge : IWorkspaceIngress, IReadinessProbe
    {
        public bool Ready { get; set; }
        public Task<string> EnsureAsync(string name, string host, CancellationToken ct) => Task.FromResult("guest-token");
        public Task RemoveAsync(string name, string host, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> VerifyAsync(Deployment deployment, CancellationToken ct) => Task.FromResult(Ready);
    }

    private static async Task<WebApplication> Start(string dir, ReleaseTarget target, Provider original, Provider replacement, Edge edge)
    {
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseTestServer();
        b.Services.AddSingleton(_ => new DeploymentStore(dir));
        b.Services.AddSingleton(target);
        b.Services.AddKeyedSingleton<IComputeProvider>("nas", original);
        b.Services.AddKeyedSingleton<IComputeProvider>("other", replacement);
        b.Services.AddSingleton<IWorkspaceIngress>(edge);
        b.Services.AddSingleton<IReadinessProbe>(edge);
        // Each test advances reconciliation explicitly, including the point at which the service restarts.
        b.Services.AddSingleton<DeploymentWorker>();
        var app = b.Build();
        Endpoints.Map(app, "test-key");
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Market_replay_recovers_original_pool_and_exposes_only_verified_workspace()
    {
        var dir = Directory.CreateTempSubdirectory();
        var original = new Provider { Fail = true };
        var next = new Provider();
        var edge = new Edge();
        var id = Guid.NewGuid();
        var path = $"/v1/deployments/{id}";
        var request = new { orderId = id, ownerId = "buyer", paidAt = DateTimeOffset.UtcNow, linkedOffices = 0, standardOffices = 0, teamOffices = 0 };
        try
        {
            await using (var app = await Start(dir.FullName, new("nas", "template", "release-A"), original, next, edge))
            {
                using var http = app.GetTestClient();
                Assert.Equal(HttpStatusCode.Unauthorized, (await http.PutAsJsonAsync(path, request)).StatusCode);
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");
                Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync(path, new { orderId = id, ownerId = "buyer" })).StatusCode);
                Assert.Equal(HttpStatusCode.UnprocessableEntity, (await http.PutAsJsonAsync(path, request with { teamOffices = 1 })).StatusCode);
                var replies = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => http.PutAsJsonAsync(path, request)));
                foreach (var reply in replies)
                {
                    reply.EnsureSuccessStatusCode();
                    var body = await reply.Content.ReadFromJsonAsync<JsonElement>();
                    Assert.Equal("Setting up", body.GetProperty("state").GetString());
                    Assert.Null(body.GetProperty("workspaceUrl").GetString());
                }
                var worker = app.Services.GetRequiredService<DeploymentWorker>();
                await Assert.ThrowsAsync<IOException>(() => worker.ReconcileAsync(id, default));
            }
            await using var fresh = await Start(dir.FullName, new("other", "new-template", "release-B"), original, next, edge);
            using var market = fresh.GetTestClient();
            market.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");
            var reconciler = fresh.Services.GetRequiredService<DeploymentWorker>();
            var store = fresh.Services.GetRequiredService<DeploymentStore>();
            Assert.Contains(id, store.Outstanding());
            Assert.Equal("release-A", store.Find(id)!.Release);
            Assert.Equal(HttpStatusCode.Conflict, (await market.PutAsJsonAsync(path, request with { ownerId = "someone-else" })).StatusCode);
            original.Fail = false;
            await reconciler.ReconcileAsync(id, default);
            Assert.Equal(original.Attempts[0], original.Attempts[1]);
            Assert.Empty(next.Attempts);
            Assert.Equal("verifying", store.Find(id)!.Phase);
            edge.Ready = true;
            await reconciler.ReconcileAsync(id, default);
            var ready = await (await market.PutAsJsonAsync(path, request)).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(id, ready.GetProperty("orderId").GetGuid());
            Assert.Equal("Ready", ready.GetProperty("state").GetString());
            Assert.Equal($"https://hq-{id:N}.c-sweet.app/", ready.GetProperty("workspaceUrl").GetString());
            Assert.Equal(HttpStatusCode.Accepted, (await market.DeleteAsync(path)).StatusCode);
            await reconciler.ReconcileAsync(id, default);
            Assert.True(original.Removed);
            Assert.Equal(HttpStatusCode.NoContent, (await market.DeleteAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Gone, (await market.PutAsJsonAsync(path, request)).StatusCode);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Existing_python_row_preserves_allocation_and_compares_purchase_values()
    {
        var temp = Directory.CreateTempSubdirectory();
        try
        {
            var id = Guid.NewGuid();
            using var store = new DeploymentStore(temp.FullName);
            using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(temp.FullName, "deployments.db")};Pooling=False");
            db.Open();
            using var sql = db.CreateCommand();
            sql.CommandText = "INSERT INTO deployments VALUES ($id,$json,'nas','old-template','old-image','hqexisting','key','verifying')";
            sql.Parameters.AddWithValue("$id", id.ToString());
            sql.Parameters.AddWithValue("$json", $$"""{"linkedOffices": 0, "orderId": "{{id}}", "ownerId": "maya", "paidAt": "2026-11-20T10:00:00Z", "standardOffices": 0, "teamOffices": 0}""");
            sql.ExecuteNonQuery();
            var purchase = new Purchase(id, "maya", DateTimeOffset.Parse("2026-11-20T10:00:00+00:00"), 0, 0, 0);
            var row = store.Accept(purchase, new("future", "new", "new"));
            Assert.Equal(purchase, row.Purchase);
            Assert.Equal("hqexisting", row.Name);
            Assert.Equal("nas", row.Pool);
            Assert.Equal("old-image", row.Release);
            Assert.Equal("verifying", row.Phase);
        }
        finally { temp.Delete(true); }
    }
}
