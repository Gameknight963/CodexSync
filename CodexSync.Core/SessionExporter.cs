namespace CodexSync.Core;

public static class SessionExporter
{
    public static async Task<string> ExportAsync(Guid sessionId, string sessionsDirectory,
        string archiveDirectory, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("A session ID cannot be empty.", nameof(sessionId));
        string sessionsRoot = Path.GetFullPath(sessionsDirectory);
        string archiveRoot = Path.GetFullPath(archiveDirectory);
        string relativeArchive = Path.GetRelativePath(sessionsRoot, archiveRoot);
        if (relativeArchive == "." || (!Path.IsPathRooted(relativeArchive) &&
            relativeArchive != ".." && !relativeArchive.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            throw new ArgumentException("The archive must be outside the live sessions directory.", nameof(archiveDirectory));
        if (!Directory.Exists(sessionsRoot))
            throw new DirectoryNotFoundException($"The sessions directory does not exist: {sessionsRoot}");

        string? sourcePath = null;
        foreach (string file in Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories))
        {
            SessionMetadata metadata = await SessionReader.ReadMetadataAsync(file, cancellationToken);
            if (metadata.Id != sessionId) continue;
            if (sourcePath is not null)
                throw new IOException($"Multiple local files have session ID {sessionId:D}; export is ambiguous.");
            sourcePath = file;
        }
        if (sourcePath is null)
            throw new FileNotFoundException($"Session {sessionId:D} was not found in {sessionsRoot}.");

        string destinationDirectory = Path.Combine(archiveRoot, "sessions");
        string destination = Path.Combine(destinationDirectory, $"{sessionId:D}.jsonl");
        if (File.Exists(destination))
            throw new IOException($"An export already exists at {destination}. Export will not overwrite it.");
        Directory.CreateDirectory(destinationDirectory);
        string temporary = Path.Combine(destinationDirectory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            await using (FileStream target = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                // Bound the snapshot: an active conversation may continue appending while we copy.
                long remaining = source.Length;
                byte[] buffer = new byte[81920];
                while (remaining > 0)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), cancellationToken);
                    if (read == 0) throw new IOException("The session was truncated during export. Retry after it stops changing.");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    remaining -= read;
                }
            }

            // Validate the copied snapshot rather than publishing a partial JSON record.
            SessionMetadata snapshot = await SessionReader.ReadMetadataAsync(temporary, cancellationToken);
            if (snapshot.Id != sessionId) throw new IOException("The session identity changed during export.");
            using (StreamReader reader = File.OpenText(temporary))
            {
                await foreach (Newtonsoft.Json.Linq.JObject record in SessionReader.ReadRecordsAsync(reader, cancellationToken)) { }
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
