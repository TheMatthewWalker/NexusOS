using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed record GuardianAuditEntry(DateTimeOffset Timestamp, string PlanId, string Event, string PreviousHash, string Hash);

public sealed class GuardianAuditStore(NexusStatePaths paths)
{
    private readonly string _path = paths.PathFor("guardian-audit.jsonl");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<GuardianAuditEntry> RecordApprovalAsync(GuardianPlan plan, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var previous = "GENESIS";
            if (File.Exists(_path))
            {
                foreach (var line in await File.ReadAllLinesAsync(_path, ct))
                {
                    var record = JsonSerializer.Deserialize<GuardianAuditEntry>(line)
                        ?? throw new InvalidDataException("Invalid Guardian audit entry.");
                    var expected = Hash(record.Timestamp, record.PlanId, record.Event, record.PreviousHash);
                    if (record.PreviousHash != previous || expected != record.Hash)
                        throw new InvalidDataException("Guardian audit chain verification failed.");
                    previous = record.Hash;
                }
            }
            var timestamp = DateTimeOffset.UtcNow;
            const string eventName = "approved-preview-only";
            var entry = new GuardianAuditEntry(timestamp, plan.Id, eventName, previous,
                Hash(timestamp, plan.Id, eventName, previous));
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + "\n");
            using (var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                NexusStatePaths.RestrictPermissions(_path, false);
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
            }
            return entry;
        }
        finally { _gate.Release(); }
    }

    private static string Hash(DateTimeOffset time, string id, string evt, string previous)
    {
        var input = Encoding.UTF8.GetBytes($"{time:O}|{id}|{evt}|{previous}");
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }
}
