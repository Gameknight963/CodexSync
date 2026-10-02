using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexSync.Core;

/// <summary>A machine-local, versioned mapping file. Writers must be serialized by the caller.</summary>
public sealed class SessionMappingStore
{
    private readonly string path;

    public SessionMappingStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
    }

    public async Task<string?> GetAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        ValidateId(sessionId);
        JObject document = await LoadAsync(cancellationToken);
        return document["sessions"]![sessionId.ToString("D")]?.Value<string>();
    }

    public async Task SetAsync(Guid sessionId, string localFolder, CancellationToken cancellationToken = default)
    {
        ValidateId(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(localFolder);
        if (!Path.IsPathFullyQualified(localFolder))
            throw new ArgumentException("A local folder must be an absolute path.", nameof(localFolder));

        JObject document = await LoadAsync(cancellationToken);
        document["sessions"]![sessionId.ToString("D")] = Path.GetFullPath(localFolder);
        await SaveAsync(document, cancellationToken);
    }

    public async Task<bool> RemoveAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        ValidateId(sessionId);
        JObject document = await LoadAsync(cancellationToken);
        if (!((JObject)document["sessions"]!).Remove(sessionId.ToString("D")))
            return false;
        await SaveAsync(document, cancellationToken);
        return true;
    }

    private async Task<JObject> LoadAsync(CancellationToken cancellationToken)
    {
        string json;
        try { json = await File.ReadAllTextAsync(path, cancellationToken); }
        catch (FileNotFoundException) { return NewDocument(); }
        catch (DirectoryNotFoundException) { return NewDocument(); }

        JObject document;
        try
        {
            document = JObject.Parse(json, new JsonLoadSettings
            {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Invalid mapping file JSON.", exception);
        }

        if (document["version"]?.Type != JTokenType.Integer || document["version"]!.ToString() != "1")
            throw new InvalidDataException("Unsupported mapping file version.");
        if (document["sessions"] is not JObject sessions)
            throw new InvalidDataException("The mapping file requires a sessions object.");
        foreach (JProperty entry in sessions.Properties())
        {
            if (!Guid.TryParseExact(entry.Name, "D", out Guid id) || id == Guid.Empty ||
                entry.Name != id.ToString("D") || entry.Value.Type != JTokenType.String ||
                string.IsNullOrWhiteSpace(entry.Value.Value<string>()) ||
                !Path.IsPathFullyQualified(entry.Value.Value<string>()!))
                throw new InvalidDataException($"Invalid session mapping: {entry.Name}.");
        }
        return document;
    }

    private async Task SaveAsync(JObject document, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, document.ToString(Formatting.Indented), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static JObject NewDocument() => new()
    {
        ["version"] = 1,
        ["sessions"] = new JObject()
    };

    private static void ValidateId(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A session ID cannot be empty.", nameof(id));
    }
}
