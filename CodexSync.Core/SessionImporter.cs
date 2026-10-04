using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexSync.Core;

public static class SessionImporter
{
    public static async Task<string> ImportAsync(string sourcePath, string sessionsDirectory,
        SessionMappingStore mappings, CancellationToken cancellationToken = default,
        string? conflictDirectory = null, string? localFolderOverride = null)
    {
        SessionMetadata metadata = await SessionReader.ReadMetadataAsync(sourcePath, cancellationToken);
        string localFolder = localFolderOverride ?? await mappings.GetAsync(metadata.Id, cancellationToken) ??
            throw new InvalidDataException($"Session {metadata.Id:D} has no local folder mapping. Use map first.");
        if (!Directory.Exists(localFolder))
            throw new DirectoryNotFoundException($"The mapped folder does not exist: {localFolder}");
        if (!Path.IsPathFullyQualified(localFolder)) throw new ArgumentException("A local folder must be absolute.");

        string root = Path.GetFullPath(sessionsDirectory);
        string? existingPath = null;
        if (Directory.Exists(root))
        {
            foreach (string existing in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                SessionMetadata other = await SessionReader.ReadMetadataAsync(existing, cancellationToken);
                if (other.Id == metadata.Id)
                {
                    if (existingPath is not null) throw new IOException($"Multiple local files have session ID {metadata.Id:D}.");
                    existingPath = existing;
                }
            }
        }

        using FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using StreamReader reader = new(source);
        string? temporaryPath = null;
        string? destination = null;
        StreamWriter? writer = null;
        try
        {
            bool first = true;
            await foreach (JObject record in SessionReader.ReadRecordsAsync(reader, cancellationToken))
            {
                if (first)
                {
                    // Revalidate the snapshot opened for the actual copy.
                    using StringReader metadataReader = new(record.ToString(Formatting.None));
                    SessionMetadata snapshot = await SessionReader.ReadMetadataAsync(metadataReader, cancellationToken);
                    if (snapshot.Id != metadata.Id || snapshot.WorkingDirectory != metadata.WorkingDirectory)
                        throw new IOException("The source metadata changed during import.");
                    string? timestamp = snapshot.Payload.Value<string>("timestamp") ?? record.Value<string>("timestamp");
                    if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out DateTimeOffset created))
                        throw new InvalidDataException("Session metadata requires a valid timestamp for import.");
                    string directory = Path.Combine(root, created.ToString("yyyy", CultureInfo.InvariantCulture),
                        created.ToString("MM", CultureInfo.InvariantCulture), created.ToString("dd", CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(directory);
                    destination = Path.Combine(directory,
                        $"rollout-{created.ToString("yyyy-MM-ddTHH-mm-ss", CultureInfo.InvariantCulture)}-{metadata.Id:D}.jsonl");
                    temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
                    writer = new StreamWriter(new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None));
                    first = false;
                }

                // Only structural working-directory fields change. Messages and tool history stay historical.
                string? type = record.Value<string>("type");
                if (type is "session_meta" or "turn_context" && record["payload"] is JObject payload)
                    payload["cwd"] = localFolder;
                if (type == "world_state" &&
                    record["payload"]?["state"]?["environments"]?["environments"]?["local"] is JObject local &&
                    local["cwd"] is not null)
                    local["cwd"] = localFolder;
                await writer!.WriteLineAsync(record.ToString(Formatting.None).AsMemory(), cancellationToken);
            }
            if (writer is null) throw new InvalidDataException("The session file is empty.");
            await writer.DisposeAsync();
            writer = null;
            cancellationToken.ThrowIfCancellationRequested();
            if (existingPath is not null)
            {
                // Hold a handle denying writers on Windows while comparing and replacing.
                using FileStream guard = new(existingPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                string fingerprint = await SessionHistory.FingerprintAsync(existingPath, cancellationToken);
                HistoryRelationship relationship = await SessionHistory.CompareAsync(existingPath, temporaryPath!, cancellationToken);
                if (relationship is HistoryRelationship.Equal or HistoryRelationship.ExistingExtendsIncoming)
                    return existingPath;
                if (relationship == HistoryRelationship.Diverged)
                {
                    string conflict = await SessionHistory.PreserveConflictAsync(metadata.Id, existingPath, temporaryPath!,
                        conflictDirectory ?? Path.Combine(Path.GetDirectoryName(root)!, "sync-conflicts"), cancellationToken);
                    throw new IOException($"Session {metadata.Id:D} diverged. Both versions were preserved in {conflict}.");
                }
                if (fingerprint != await SessionHistory.FingerprintAsync(existingPath, cancellationToken))
                    throw new IOException("The local session changed during import. Retry after it stops changing.");
                guard.Dispose();
                File.Move(temporaryPath!, existingPath, overwrite: true);
                return existingPath;
            }
            File.Move(temporaryPath!, destination!, overwrite: false);
            return destination!;
        }
        finally
        {
            if (writer is not null) await writer.DisposeAsync();
            if (temporaryPath is not null && File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
