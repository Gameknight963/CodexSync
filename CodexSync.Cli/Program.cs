using CodexSync.Core;

try
{
    List<string> arguments = new(args);
    string? mappingOverride = TakeOption(arguments, "--mapping-file");
    string? sessionsOverride = TakeOption(arguments, "--sessions-dir");
    if (arguments.Count == 0 || arguments[0] is "--help" or "-h" or "help")
    {
        Console.WriteLine("""
            Usage:
              codexsync list
              codexsync map <session-id> <local-folder>
              codexsync mapping-path

            Options:
              --mapping-file <path>  Override the machine-local mapping file.
              --sessions-dir <path>  Override the directory scanned by list.

            list reads session metadata only; it does not modify Codex files.
            """);
        return 0;
    }

    string mappingPath = Path.GetFullPath(mappingOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexSync", "mappings.json"));
    SessionMappingStore store = new(mappingPath);

    switch (arguments[0])
    {
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
            string codexHome = Environment.GetEnvironmentVariable("CODEX_HOME") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            string sessionsPath = Path.GetFullPath(sessionsOverride ?? Path.Combine(codexHome, "sessions"));
            if (!Directory.Exists(sessionsPath))
                throw new DirectoryNotFoundException($"The sessions directory does not exist: {sessionsPath}");

            Console.WriteLine("Session ID\tSaved folder\tLocal folder");
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
                Console.WriteLine($"{metadata.Id:D}\t{Display(metadata.WorkingDirectory)}\t{Display(localFolder ?? "(unmapped)")}");
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
