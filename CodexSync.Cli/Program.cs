using CodexSync.Core;
using CodexSync.Cli;

try
{
    List<string> arguments = new(args);
    string? mappingOverride = TakeOption(arguments, "--mapping-file");
    string? sessionsOverride = TakeOption(arguments, "--sessions-dir");
    string codexHome = Path.GetFullPath(TakeOption(arguments, "--codex-home") ??
        Environment.GetEnvironmentVariable("CODEX_HOME") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
    bool fullPaths = arguments.Remove("--full-paths");
    if (arguments.Count == 0 || arguments[0] is "--help" or "-h" or "help")
    {
        Console.WriteLine("""
            Usage:
              codexsync list [--full-paths]
              codexsync map <session-id> <local-folder>
              codexsync mapping-path
              codexsync import <session-file>
              codexsync export <session-id> <archive-folder>

            Options:
              --mapping-file <path>  Override the machine-local mapping file.
              --sessions-dir <path>  Override the directory scanned by list or export.
              --codex-home <path>    Override the Codex home for list, import, or export.
              --full-paths           Show complete paths in list instead of shortening them.
            """);
        return 0;
    }

    string mappingPath = Path.GetFullPath(mappingOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexSync", "mappings.json"));
    SessionMappingStore store = new(mappingPath);
    if (fullPaths && arguments[0] != "list")
        throw new ArgumentException("--full-paths is only supported by list.");

    switch (arguments[0])
    {
        case "export" when arguments.Count == 3:
            if (!Guid.TryParse(arguments[1], out Guid exportId) || exportId == Guid.Empty)
                throw new ArgumentException("The session ID must be a non-empty UUID.");
            string exportedPath = await SessionExporter.ExportAsync(exportId,
                sessionsOverride ?? Path.Combine(codexHome, "sessions"), arguments[2]);
            Console.WriteLine($"Exported: {exportedPath}");
            return 0;
        case "import" when arguments.Count == 2:
            if (sessionsOverride is not null)
                throw new ArgumentException("Use --codex-home for import; --sessions-dir only applies to list or export.");
            SessionMetadata importedMetadata = await SessionReader.ReadMetadataAsync(arguments[1]);
            string importedPath = await SessionImporter.ImportAsync(arguments[1],
                Path.Combine(codexHome, "sessions"), store);
            Console.WriteLine($"Imported: {importedPath}");
            try
            {
                await CodexRegistration.RegisterAsync(importedMetadata.Id,
                    (await store.GetAsync(importedMetadata.Id))!, codexHome);
            }
            catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or Newtonsoft.Json.JsonException)
            {
                throw new IOException($"The file was imported, but Codex registration failed: {exception.Message} " +
                    $"Retry using codex resume {importedMetadata.Id:D} -C \"{await store.GetAsync(importedMetadata.Id)}\" with CODEX_HOME set to \"{codexHome}\".", exception);
            }
            Console.WriteLine("Registered with Codex in the mapped folder.");
            return 0;
        case "mapping-path" when arguments.Count == 1:
            Console.WriteLine(mappingPath);
            return 0;
        case "map" when arguments.Count == 3:
            if (!Guid.TryParse(arguments[1], out Guid id) || id == Guid.Empty)
                throw new ArgumentException("The session ID must be a non-empty UUID.");
            string folder = Path.GetFullPath(arguments[2]);
            if (!Directory.Exists(folder))
                throw new ArgumentException($"The local folder does not exist: {folder}");
            await store.SetAsync(id, folder);
            Console.WriteLine($"{id:D} -> {folder}");
            return 0;
        case "list" when arguments.Count == 1:
            string sessionsPath = Path.GetFullPath(sessionsOverride ?? Path.Combine(codexHome, "sessions"));
            if (!Directory.Exists(sessionsPath))
                throw new DirectoryNotFoundException($"The sessions directory does not exist: {sessionsPath}");

            List<(string Id, string SavedFolder, string? LocalFolder)> rows = new();
            int failures = 0;
            foreach (string file in Directory.EnumerateFiles(sessionsPath, "*.jsonl", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                SessionMetadata metadata;
                try
                {
                    metadata = await SessionReader.ReadMetadataAsync(file);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Cannot read {file}: {exception.Message}");
                    failures++;
                    continue;
                }
                string? localFolder = await store.GetAsync(metadata.Id);
                rows.Add((metadata.Id.ToString("D"), FormatPath(metadata.WorkingDirectory, fullPaths),
                    localFolder is null ? null : FormatPath(localFolder, fullPaths)));
            }
            int idWidth = Math.Max("Session ID".Length, rows.Select(row => row.Id.Length).DefaultIfEmpty(0).Max());
            int savedWidth = Math.Max("Saved folder".Length, rows.Select(row => row.SavedFolder.Length).DefaultIfEmpty(0).Max());
            Console.WriteLine($"{"Session ID".PadRight(idWidth)}  {"Saved folder".PadRight(savedWidth)}  Local folder");
            foreach ((string idText, string savedFolder, string? localFolder) in rows)
            {
                Console.Write(idText.PadRight(idWidth) + "  ");
                WriteColored(savedFolder.PadRight(savedWidth), ConsoleColor.DarkGray);
                Console.Write("  ");
                WriteColored(localFolder ?? "(unmapped)", localFolder is null ? ConsoleColor.Yellow : ConsoleColor.Green);
                Console.WriteLine();
            }
            return failures == 0 ? 0 : 1;
        default:
            throw new ArgumentException("Unknown command or incorrect arguments. Use --help for usage.");
    }
}
catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Error: {exception.Message}");
    return 1;
}

static string? TakeOption(List<string> arguments, string name)
{
    int index = arguments.IndexOf(name);
    if (index < 0) return null;
    if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        throw new ArgumentException($"{name} requires a path.");
    string value = arguments[index + 1];
    arguments.RemoveRange(index, 2);
    if (arguments.Contains(name)) throw new ArgumentException($"{name} may only be specified once.");
    return value;
}

static string Display(string value) => value.Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");

static string FormatPath(string path, bool fullPaths)
{
    string value = Display(path);
    const int maximumWidth = 60;
    if (fullPaths || value.Length <= maximumWidth) return value;
    // Preserve the path's beginning and give more space to its distinguishing ending.
    const int prefixLength = 20;
    const int suffixLength = maximumWidth - prefixLength - 3;
    return value[..prefixLength] + "..." + value[^suffixLength..];
}

static void WriteColored(string value, ConsoleColor color)
{
    if (Console.IsOutputRedirected || Environment.GetEnvironmentVariable("NO_COLOR") is { Length: > 0 })
    {
        Console.Write(value);
        return;
    }

    ConsoleColor previousColor = Console.ForegroundColor;
    try
    {
        Console.ForegroundColor = color;
        Console.Write(value);
    }
    finally
    {
        Console.ForegroundColor = previousColor;
    }
}
