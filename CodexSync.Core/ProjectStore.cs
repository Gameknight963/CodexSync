using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexSync.Core;

public sealed record SyncProject(Guid Id, string Name, string Repository, string? LocalFolder);
public sealed record SelectedSession(Guid Id, bool Included, Guid? ProjectId, string Subfolder);
public sealed record ProjectCatalog(IReadOnlyList<SyncProject> Projects, IReadOnlyList<SelectedSession> Sessions);
public sealed record PendingChanges(JObject Projects, JObject Sessions);

// Setup writes only private intent. Sync publishes it after pulling the shared archive.
public sealed class ProjectStore(string machinePath, string? archiveDirectory, SessionMappingStore mappings)
{
    public const string ManifestName = "codexsync.json";
    private string ManifestPath => Path.Combine(archiveDirectory ?? throw new InvalidDataException("No archive configured."), ManifestName);

    public async Task InitializeAsync()
    {
        if (!File.Exists(machinePath)) await SaveAsync(machinePath, await LoadMachineAsync());
    }

    public async Task<ProjectCatalog> GetAsync()
    {
        JObject machine = await LoadMachineAsync();
        JObject shared = await LoadSharedAsync();
        Apply(shared, machine);
        ValidateManifest(shared);
        return Catalog(shared, machine);
    }

    public async Task AddProjectAsync(string name, string folder, string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Any(char.IsControl)) throw new ArgumentException("Project names cannot contain control characters.");
        string local = Path.GetFullPath(folder);
        if (!Directory.Exists(local)) throw new DirectoryNotFoundException($"Project folder does not exist: {local}");
        string identity = NormalizeRepository(repository);
        JObject machine = await LoadMachineAsync();
        JObject shared = await LoadSharedAsync();
        Apply(shared, machine);
        ValidateManifest(shared);
        JProperty? existing = ((JObject)shared["projects"]!).Properties().FirstOrDefault(
            property => property.Value.Value<string>("repository") == identity);
        Guid id = existing is null ? new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(identity))[..16]) : Guid.Parse(existing.Name);
        JProperty? named = ((JObject)shared["projects"]!).Properties().FirstOrDefault(
            property => string.Equals(property.Value.Value<string>("name"), name, StringComparison.OrdinalIgnoreCase));
        if (named is not null && named.Name != id.ToString("D"))
            throw new InvalidDataException($"Project name '{name}' belongs to another repository.");
        if (existing is not null && !string.Equals(existing.Value.Value<string>("name"), name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"This repository is already project '{existing.Value.Value<string>("name")}'. Use that name.");
        if (existing is null)
            machine["pendingProjects"]![id.ToString("D")] = new JObject { ["name"] = name, ["repository"] = identity };
        machine["projects"]![id.ToString("D")] = local;
        await SaveAsync(machinePath, machine);
    }

    public async Task IncludeAsync(Guid sessionId, string? projectName = null, string subfolder = ".")
    {
        ValidateId(sessionId.ToString("D"));
        string relative = NormalizeSubfolder(subfolder);
        JObject machine = await LoadMachineAsync();
        JObject shared = await LoadSharedAsync();
        Apply(shared, machine);
        ValidateManifest(shared);
        Guid? projectId = null;
        if (projectName is not null)
        {
            SyncProject project = Catalog(shared, machine).Projects.SingleOrDefault(
                item => string.Equals(item.Name, projectName, StringComparison.OrdinalIgnoreCase)) ??
                throw new InvalidDataException($"Unknown project '{projectName}'. Use project add first.");
            projectId = project.Id;
        }
        else
        {
            if (relative != ".") throw new ArgumentException("--subfolder requires --project.");
            if (await mappings.GetAsync(sessionId) is null)
                throw new InvalidDataException("Include without a project requires a session folder mapping. Use map first.");
        }
        machine["pendingSessions"]![sessionId.ToString("D")] = SessionEntry(true, projectId, relative);
        await SaveAsync(machinePath, machine);
    }

    public async Task ExcludeAsync(Guid sessionId)
    {
        ValidateId(sessionId.ToString("D"));
        JObject machine = await LoadMachineAsync();
        JObject shared = await LoadSharedAsync();
        Apply(shared, machine);
        JObject entry = shared["sessions"]![sessionId.ToString("D")] is JObject previous
            ? (JObject)previous.DeepClone() : SessionEntry(false, null, ".");
        entry["included"] = false;
        entry.Remove("legacy");
        machine["pendingSessions"]![sessionId.ToString("D")] = entry;
        await SaveAsync(machinePath, machine);
    }

    public async Task<string?> ResolveFolderAsync(Guid sessionId)
    {
        string? explicitFolder = await mappings.GetAsync(sessionId);
        if (explicitFolder is not null) return explicitFolder;
        ProjectCatalog catalog = await GetAsync();
        SelectedSession? session = catalog.Sessions.SingleOrDefault(item => item.Id == sessionId);
        SyncProject? project = catalog.Projects.SingleOrDefault(item => item.Id == session?.ProjectId);
        return project?.LocalFolder is { } root
            ? Path.GetFullPath(Path.Combine(root, session!.Subfolder.Replace('/', Path.DirectorySeparatorChar))) : null;
    }

    public async Task<PendingChanges> PublishPendingAsync()
    {
        JObject machine = await LoadMachineAsync();
        // Persist the one-time legacy migration before shared data is published.
        if (!File.Exists(machinePath)) await SaveAsync(machinePath, machine);
        JObject shared = await LoadSharedAsync();
        JObject original = (JObject)shared.DeepClone();
        Apply(shared, machine);
        ValidateManifest(shared);
        if (!JToken.DeepEquals(original, shared)) await SaveAsync(ManifestPath, shared);
        return new PendingChanges((JObject)machine["pendingProjects"]!.DeepClone(), (JObject)machine["pendingSessions"]!.DeepClone());
    }

    public async Task AcknowledgeAsync(PendingChanges published)
    {
        JObject machine = await LoadMachineAsync();
        foreach ((string key, JObject values) in new[] { ("pendingProjects", published.Projects), ("pendingSessions", published.Sessions) })
            foreach (JProperty property in values.Properties())
                if (JToken.DeepEquals(machine[key]![property.Name], property.Value))
                    ((JObject)machine[key]!).Remove(property.Name);
        await SaveAsync(machinePath, machine);
    }

    private async Task<JObject> LoadMachineAsync()
    {
        JObject machine = await LoadAsync(machinePath, new JObject
        {
            ["version"] = 1, ["projects"] = new JObject(), ["pendingProjects"] = new JObject(), ["pendingSessions"] = new JObject()
        });
        RequireObject(machine, "projects");
        RequireObject(machine, "pendingProjects");
        RequireObject(machine, "pendingSessions");
        foreach (JProperty property in ((JObject)machine["projects"]!).Properties())
        {
            ValidateId(property.Name);
            if (property.Value.Type != JTokenType.String || !Path.IsPathFullyQualified(property.Value.Value<string>()!))
                throw new InvalidDataException("Invalid local project folder.");
        }
        JObject pending = new() { ["version"] = 1, ["projects"] = machine["pendingProjects"]!.DeepClone(), ["sessions"] = new JObject() };
        ValidateManifest(pending);
        foreach (JProperty property in ((JObject)machine["pendingSessions"]!).Properties()) ValidateSession(property);
        if (!File.Exists(machinePath))
            foreach ((Guid id, string _) in await mappings.GetAllAsync())
            {
                JObject legacy = SessionEntry(true, null, ".");
                legacy["legacy"] = true;
                machine["pendingSessions"]![id.ToString("D")] = legacy;
            }
        return machine;
    }

    private async Task<JObject> LoadSharedAsync()
    {
        JObject empty = new() { ["version"] = 1, ["projects"] = new JObject(), ["sessions"] = new JObject() };
        JObject shared = archiveDirectory is null ? empty : await LoadAsync(ManifestPath, empty);
        ValidateManifest(shared);
        return shared;
    }

    private static void Apply(JObject shared, JObject machine)
    {
        foreach (JProperty property in ((JObject)machine["pendingProjects"]!).Properties())
        {
            if (shared["projects"]![property.Name] is JObject existing &&
                existing.Value<string>("repository") != property.Value.Value<string>("repository"))
                throw new InvalidDataException("A pending project conflicts with the shared project identity.");
            shared["projects"]![property.Name] = property.Value.DeepClone();
        }
        foreach (JProperty property in ((JObject)machine["pendingSessions"]!).Properties())
        {
            if (property.Value["legacy"]?.Value<bool>() == true && shared["sessions"]![property.Name] is not null) continue;
            JObject entry = (JObject)property.Value.DeepClone();
            entry.Remove("legacy");
            shared["sessions"]![property.Name] = entry;
        }
    }

    private static ProjectCatalog Catalog(JObject shared, JObject machine) => new(
        ((JObject)shared["projects"]!).Properties().Select(property => new SyncProject(Guid.Parse(property.Name),
            property.Value.Value<string>("name")!, property.Value.Value<string>("repository")!,
            machine["projects"]![property.Name]?.Value<string>())).ToArray(),
        ((JObject)shared["sessions"]!).Properties().Select(property => new SelectedSession(Guid.Parse(property.Name),
            property.Value.Value<bool>("included"), property.Value["projectId"]?.Type == JTokenType.String
                ? Guid.Parse(property.Value.Value<string>("projectId")!) : null, property.Value.Value<string>("subfolder")!)).ToArray());

    private static JObject SessionEntry(bool included, Guid? project, string relative) => new()
    {
        ["included"] = included, ["projectId"] = project?.ToString("D"), ["subfolder"] = relative
    };

    private static void ValidateManifest(JObject document)
    {
        RequireObject(document, "projects");
        RequireObject(document, "sessions");
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> repositories = new(StringComparer.Ordinal);
        foreach (JProperty property in ((JObject)document["projects"]!).Properties())
        {
            ValidateId(property.Name);
            if (property.Value is not JObject project || project["name"]?.Type != JTokenType.String ||
                string.IsNullOrWhiteSpace(project.Value<string>("name")) || project.Value<string>("name")!.Any(char.IsControl) ||
                project["repository"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace(project.Value<string>("repository")) ||
                !names.Add(project.Value<string>("name")!) || !repositories.Add(project.Value<string>("repository")!))
                throw new InvalidDataException("Invalid or duplicate project definition.");
        }
        foreach (JProperty property in ((JObject)document["sessions"]!).Properties())
        {
            ValidateSession(property);
            string? project = property.Value.Value<string>("projectId");
            if (project is not null && document["projects"]![project] is null)
                throw new InvalidDataException($"Session {property.Name} references an unknown project.");
        }
    }

    private static void ValidateSession(JProperty property)
    {
        ValidateId(property.Name);
        if (property.Value is not JObject entry || entry["included"]?.Type != JTokenType.Boolean ||
            entry["subfolder"]?.Type != JTokenType.String ||
            NormalizeSubfolder(entry.Value<string>("subfolder")!) != entry.Value<string>("subfolder") ||
            entry["projectId"]?.Type is not (JTokenType.Null or JTokenType.String))
            throw new InvalidDataException("Invalid session selection.");
        if (entry["projectId"]?.Type == JTokenType.String) ValidateId(entry.Value<string>("projectId")!);
        if (entry["legacy"] is not null && entry["legacy"]!.Type != JTokenType.Boolean)
            throw new InvalidDataException("Invalid legacy selection marker.");
    }

    public static string NormalizeSubfolder(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string value = folder.Replace('\\', '/');
        if (value == ".") return value;
        if (value.StartsWith('/') || value.Contains(':') || value.Any(char.IsControl) ||
            value.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("Subfolder must be a relative path within the project, or '.'.");
        return value;
    }

    public static string NormalizeRepository(string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        string value = repository.Trim();
        if (!value.Contains("://") && value.Contains('@') && value.IndexOf(':') is int separator && separator > value.IndexOf('@'))
            value = "ssh://" + value[..separator] + "/" + value[(separator + 1)..];
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || string.IsNullOrEmpty(uri.Host) ||
            uri.Scheme is not ("https" or "http" or "ssh" or "git") || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("A project requires a Git remote URL (HTTPS or SSH).");
        string path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("The repository URL needs a repository path.");
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) path = path.ToLowerInvariant();
        string port = uri.IsDefaultPort || uri.Port is 22 or 80 or 443 ? "" : ":" + uri.Port;
        return "https://" + uri.Host.ToLowerInvariant() + port + "/" + path;
    }

    private static void ValidateId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid id) || id == Guid.Empty || value != id.ToString("D"))
            throw new InvalidDataException("IDs must be canonical, non-empty UUIDs.");
    }

    private static void RequireObject(JObject document, string key)
    {
        if (document[key] is not JObject) throw new InvalidDataException($"Configuration requires a {key} object.");
    }

    private static async Task<JObject> LoadAsync(string path, JObject fallback)
    {
        if (!File.Exists(path)) return fallback;
        JObject document;
        try { document = JObject.Parse(await File.ReadAllTextAsync(path), new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }); }
        catch (JsonException error) { throw new InvalidDataException("Invalid project configuration JSON.", error); }
        if (document["version"]?.Type != JTokenType.Integer || document["version"]!.ToString() != "1")
            throw new InvalidDataException("Unsupported project configuration version.");
        return document;
    }

    private static async Task SaveAsync(string path, JObject document)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, document.ToString(Formatting.Indented));
            File.Move(temporary, full, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
