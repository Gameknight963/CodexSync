using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace CodexSync.Core;

public enum HistoryRelationship { Equal, IncomingExtendsExisting, ExistingExtendsIncoming, Diverged }

public static class SessionHistory
{
    public static async Task<HistoryRelationship> CompareAsync(string existing, string incoming,
        CancellationToken cancellationToken = default)
    {
        using StreamReader left = Open(existing);
        using StreamReader right = Open(incoming);
        await using IAsyncEnumerator<JObject> a = RelevantRecords(left, cancellationToken).GetAsyncEnumerator();
        await using IAsyncEnumerator<JObject> b = RelevantRecords(right, cancellationToken).GetAsyncEnumerator();
        bool different = false;
        bool leftExtra = false;
        bool rightExtra = false;
        while (true)
        {
            bool hasLeft = await a.MoveNextAsync();
            bool hasRight = await b.MoveNextAsync();
            if (!hasLeft && !hasRight) break;
            if (hasLeft && hasRight)
                different |= !JToken.DeepEquals(Normalize(a.Current), Normalize(b.Current));
            else if (hasLeft) leftExtra = true;
            else rightExtra = true;
        }
        if (different) return HistoryRelationship.Diverged;
        if (leftExtra) return HistoryRelationship.ExistingExtendsIncoming;
        if (rightExtra) return HistoryRelationship.IncomingExtendsExisting;
        return HistoryRelationship.Equal;
    }

    private static async IAsyncEnumerable<JObject> RelevantRecords(TextReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (JObject record in SessionReader.ReadRecordsAsync(reader, cancellationToken))
            if (!IsLocalSettings(record)) yield return record;
    }

    // Codex appends these when a client resumes with local settings, without a conversation turn.
    private static bool IsLocalSettings(JObject record) => record["type"]?.Type == JTokenType.String &&
        record.Value<string>("type") == "event_msg" && record["payload"] is JObject payload &&
        payload["type"]?.Type == JTokenType.String && payload.Value<string>("type") == "thread_settings_applied";

    private static JObject Normalize(JObject record)
    {
        JObject result = (JObject)record.DeepClone();
        string? type = result["type"]?.Type == JTokenType.String ? result.Value<string>("type") : null;
        if (type is "session_meta" or "turn_context" && result["payload"] is JObject payload)
            payload.Remove("cwd");
        if (type == "world_state" &&
            result["payload"]?["state"]?["environments"]?["environments"]?["local"] is JObject local)
            local.Remove("cwd");
        return result;
    }

    // Retain the shared prefix verbatim so local path adaptations don't churn the archive.
    public static async Task RetainSharedPrefixAsync(string existing, string incoming,
        CancellationToken cancellationToken = default)
    {
        string combined = incoming + ".merge";
        try
        {
            using StreamReader left = Open(existing);
            using StreamReader right = Open(incoming);
            File.Copy(existing, combined);
            await using StreamWriter writer = new(combined, append: true);
            int prefixRecords = 0;
            while (await left.ReadLineAsync(cancellationToken) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line) && !IsLocalSettings(JObject.Parse(line))) prefixRecords++;
            }
            using (FileStream ending = new(existing, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (ending.Length > 0)
                {
                    ending.Seek(-1, SeekOrigin.End);
                    if (ending.ReadByte() != '\n') await writer.WriteLineAsync();
                }
            }
            int skipped = 0;
            while (await right.ReadLineAsync(cancellationToken) is { } line)
            {
                if (skipped < prefixRecords)
                {
                    if (!string.IsNullOrWhiteSpace(line) && !IsLocalSettings(JObject.Parse(line))) skipped++;
                    continue;
                }
                await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
            }
            await writer.DisposeAsync();
            left.Dispose();
            right.Dispose();
            File.Move(combined, incoming, overwrite: true);
        }
        finally { if (File.Exists(combined)) File.Delete(combined); }
    }

    public static async Task<string> PreserveConflictAsync(Guid id, string existing, string incoming,
        string conflictRoot, CancellationToken cancellationToken = default)
    {
        string directory = Path.Combine(Path.GetFullPath(conflictRoot), id.ToString("D"));
        Directory.CreateDirectory(directory);
        foreach (string source in new[] { existing, incoming })
        {
            byte[] bytes = await File.ReadAllBytesAsync(source, cancellationToken);
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            string destination = Path.Combine(directory, hash + ".jsonl");
            if (File.Exists(destination)) continue;
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
                File.Move(temporary, destination, overwrite: false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return directory;
    }

    internal static StreamReader Open(string path) => new(new FileStream(path,
        FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));

    internal static async Task<string> FingerprintAsync(string path, CancellationToken cancellationToken)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}
