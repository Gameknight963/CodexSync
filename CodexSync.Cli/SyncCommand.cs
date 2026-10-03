using CodexSync.Core;

namespace CodexSync.Cli;

internal static class SyncCommand
{
    public static async Task<int> RunAsync(string archive, string codexHome, SessionMappingStore mappings,
        string conflictDirectory)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(archive), Path.GetFullPath(codexHome));
        string reverse = Path.GetRelativePath(Path.GetFullPath(codexHome), Path.GetFullPath(archive));
        static bool IsInside(string path) => !Path.IsPathRooted(path) && path != ".." &&
            !path.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        if (IsInside(relative) || IsInside(reverse))
            throw new IOException("The archive and Codex home must be separate directories.");
        string lockDirectory = Path.Combine(Path.GetTempPath(), "CodexSync", "locks");
        Directory.CreateDirectory(lockDirectory);
        string lockKey = Path.GetFullPath(archive);
        if (OperatingSystem.IsWindows()) lockKey = lockKey.ToUpperInvariant();
        string lockFile = Path.Combine(lockDirectory, Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(lockKey))) + ".lock");
        using FileStream syncLock = new(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        GitArchive git = new(archive);
        IReadOnlyDictionary<Guid, string> selected = await mappings.GetAllAsync();
        if (selected.Count == 0) throw new IOException("No sessions selected. Map a session to include it in sync.");
        await git.PullAsync();
        string sessions = Path.Combine(codexHome, "sessions");
        int failures = 0;
        foreach ((Guid id, string folder) in selected.OrderBy(entry => entry.Key))
        {
            try
            {
                string shared = Path.Combine(archive, "sessions", $"{id:D}.jsonl");
                try { await SessionExporter.ExportAsync(id, sessions, archive); }
                catch (FileNotFoundException) when (File.Exists(shared)) { }
                catch (DirectoryNotFoundException) when (File.Exists(shared)) { }
                await SessionImporter.ImportAsync(shared, sessions, mappings, conflictDirectory: conflictDirectory);
                await CodexRegistration.RegisterAsync(id, folder, codexHome);
                Console.WriteLine($"Synced: {id:D}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                System.ComponentModel.Win32Exception or Newtonsoft.Json.JsonException)
            {
                Console.Error.WriteLine($"Session {id:D}: {exception.Message}");
                failures++;
            }
        }
        await git.PushAsync();
        return failures == 0 ? 0 : 1;
    }
}
