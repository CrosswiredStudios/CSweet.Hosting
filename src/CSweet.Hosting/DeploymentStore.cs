using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CSweet.Hosting;

internal sealed class DeploymentStore : IDisposable
{
    private readonly string connectionString;
    private readonly FileStream writerLock;

    public DeploymentStore(string directory)
    {
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "deployments.db"), Pooling = false
        }.ToString();
        writerLock = new FileStream(Path.Combine(directory, "service.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            // Preserve the original SQLite layout and JSON request data across the port.
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS deployments (
                    id TEXT PRIMARY KEY, request TEXT NOT NULL, provider TEXT NOT NULL, template TEXT NOT NULL,
                    release TEXT NOT NULL, name TEXT NOT NULL UNIQUE, proof_key TEXT NOT NULL, phase TEXT NOT NULL)
                """;
            cmd.ExecuteNonQuery();
        }
        catch { writerLock.Dispose(); throw; }
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    public Deployment Accept(Purchase purchase, ReleaseTarget target)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO deployments(id,request,provider,template,release,name,proof_key,phase)
            VALUES($id,$request,$pool,$template,$release,$name,$key,'pending') ON CONFLICT(id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("$id", purchase.OrderId.ToString());
        cmd.Parameters.AddWithValue("$request", JsonSerializer.Serialize(purchase, Wire.Json));
        cmd.Parameters.AddWithValue("$pool", target.Pool);
        cmd.Parameters.AddWithValue("$template", target.Template);
        cmd.Parameters.AddWithValue("$release", target.Image);
        cmd.Parameters.AddWithValue("$name", $"hq{Guid.NewGuid():N}");
        cmd.Parameters.AddWithValue("$key", Wire.RandomHex(32));
        cmd.ExecuteNonQuery();
        return Find(purchase.OrderId)!;
    }

    public Deployment? Find(Guid order)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT request,provider,template,release,name,proof_key,phase FROM deployments WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", order.ToString());
        using var row = cmd.ExecuteReader();
        if (!row.Read()) return null;
        return new(JsonSerializer.Deserialize<Purchase>(row.GetString(0), Wire.Json)!,
            row.GetString(1), row.GetString(2), row.GetString(3), row.GetString(4), row.GetString(5), row.GetString(6));
    }

    public IReadOnlyList<Guid> Outstanding()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id FROM deployments WHERE phase NOT IN ('ready','retired') ORDER BY rowid";
        using var rows = cmd.ExecuteReader();
        List<Guid> orders = [];
        while (rows.Read()) orders.Add(Guid.Parse(rows.GetString(0)));
        return orders;
    }

    public bool Transition(Guid order, string from, string to)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE deployments SET phase=$to WHERE id=$id AND phase=$from";
        cmd.Parameters.AddWithValue("$id", order.ToString());
        cmd.Parameters.AddWithValue("$from", from);
        cmd.Parameters.AddWithValue("$to", to);
        return cmd.ExecuteNonQuery() == 1;
    }

    public void Dispose() => writerLock.Dispose();
}
