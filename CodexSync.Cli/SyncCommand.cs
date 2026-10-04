using CodexSync.Core;

namespace CodexSync.Cli;

internal static class SyncCommand
{
    public static async Task<int> RunAsync(string archive, string codexHome, SessionMappingStore mappings,
        string conflictDirectory, ProjectStore projects)
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
        await git.PullAsync();
        PendingChanges pending = await projects.PublishPendingAsync();
        ProjectCatalog catalog = await projects.GetAsync();
        SelectedSession[] selected = catalog.Sessions.Where(session => session.Included).OrderBy(session => session.Id).ToArray();
        string sessions = Path.Combine(codexHome, "sessions");
        int failures = 0;
        foreach (SelectedSession selection in selected)
        {
            Guid id = selection.Id;
            try
            {
                string folder = await projects.ResolveFolderAsync(id) ??
                    throw new InvalidDataException("No local folder for this session. Bind its project using project add, or set a session override using map.");
                if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"The local folder does not exist: {folder}");
                string shared = Path.Combine(archive, "sessions", $"{id:D}.jsonl");
                try { await SessionExporter.ExportAsync(id, sessions, archive); }
                catch (FileNotFoundException) when (File.Exists(shared)) { }
                catch (DirectoryNotFoundException) when (File.Exists(shared)) { }
                await SessionImporter.ImportAsync(shared, sessions, mappings, conflictDirectory: conflictDirectory, localFolderOverride: folder);
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
        if (selected.Length == 0) Console.WriteLine("No chats included. Project and selection changes will still be published.");
        await git.PushAsync();
        await projects.AcknowledgeAsync(pending);
        return failures == 0 ? 0 : 1;
    }
}
