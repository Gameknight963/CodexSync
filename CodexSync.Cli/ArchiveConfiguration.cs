using Newtonsoft.Json.Linq;

namespace CodexSync.Cli;

internal sealed class ArchiveConfiguration(string path)
{
    public async Task<string> GetAsync()
    {
        if (!File.Exists(path)) throw new IOException("No archive configured. Use archive <folder> first.");
        JObject document = JObject.Parse(await File.ReadAllTextAsync(path));
        if (document["version"]?.Type != JTokenType.Integer || document["version"]!.ToString() != "1" ||
            document["archive"]?.Type != JTokenType.String ||
            !Path.IsPathFullyQualified(document.Value<string>("archive")!))
            throw new InvalidDataException("Invalid archive configuration.");
        return document.Value<string>("archive")!;
    }

    public async Task SetAsync(string folder)
    {
        string absolute = Path.GetFullPath(folder);
        if (!Directory.Exists(absolute)) throw new DirectoryNotFoundException($"The archive folder does not exist: {absolute}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, new JObject { ["version"] = 1, ["archive"] = absolute }.ToString());
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
