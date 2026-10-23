using Microsoft.Data.Sqlite;

// Schema steps for Core's per-user database. Each step runs once, in one
// transaction with its completion marker in `metadata`, so a failure part-way
// leaves the previous schema untouched and the step runs again next start.
public static class SchemaMigration
{
    // Test-only fault injection: returns true to abort the named step before it is marked done.
    internal static Func<string, bool>? Fault;

    public static void Apply(SqliteConnection connection, string marker, string ddl)
    {
        using var transaction = connection.BeginTransaction();
        using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "SELECT COUNT(*) FROM metadata WHERE key=$marker";
        check.Parameters.AddWithValue("$marker", marker);
        if (Convert.ToInt64(check.ExecuteScalar()) > 0) { transaction.Commit(); return; }
        using (var apply = connection.CreateCommand())
        {
            apply.Transaction = transaction;
            apply.CommandText = ddl;
            apply.ExecuteNonQuery();
        }
        if (Fault?.Invoke(marker) == true)
            throw new IOException("Injected schema migration fault.");
        using var mark = connection.CreateCommand();
        mark.Transaction = transaction;
        mark.CommandText = "INSERT INTO metadata(key,value) VALUES($marker,'1')";
        mark.Parameters.AddWithValue("$marker", marker);
        mark.ExecuteNonQuery();
        transaction.Commit();
    }
}
