using CodexSync.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexSync.Tests;

public sealed class SessionImporterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CodexSync.Tests", Guid.NewGuid().ToString("N"));
    private readonly Guid id = Guid.NewGuid();

    private async Task<(string Source, string Sessions, string Folder, SessionMappingStore Store)> SetupAsync()
    {
        Directory.CreateDirectory(root);
        string folder = Path.Combine(root, "checkout");
        Directory.CreateDirectory(folder);
        string source = Path.Combine(root, "source.jsonl");
        JObject metadata = new()
        {
            ["type"] = "session_meta", ["timestamp"] = "2026-10-02T12:00:00Z",
            ["payload"] = new JObject { ["id"] = id.ToString("D"), ["cwd"] = "/foreign/project", ["future"] = 42 }
        };
        await File.WriteAllTextAsync(source, metadata.ToString(Formatting.None) + "\n" +
            """
            {"type":"turn_context","payload":{"cwd":"/foreign/project"}}
            {"type":"world_state","payload":{"state":{"environments":{"environments":{"local":{"cwd":"/another/project"}}}}}}
            {"type":"response_item","payload":{"text":"/foreign/project","cwd":"/foreign/project"}}
            {"type":"future_record","timestamp":"2026-10-02T12:00:00Z","payload":{"value":17}}
            """);
        SessionMappingStore store = new(Path.Combine(root, "mappings.json"));
        return (source, Path.Combine(root, "sessions"), folder, store);
    }

    [Fact]
    public async Task ImportChangesStructuralPathsAndPreservesHistoryAndSource()
    {
        (string source, string sessions, string folder, SessionMappingStore store) = await SetupAsync();
        string original = await File.ReadAllTextAsync(source);
        await store.SetAsync(id, folder);
        string imported = await SessionImporter.ImportAsync(source, sessions, store);
        Assert.Equal(original, await File.ReadAllTextAsync(source));
        Assert.Equal(folder, (await SessionReader.ReadMetadataAsync(imported)).WorkingDirectory);
        List<JObject> records = new();
        using StreamReader reader = File.OpenText(imported);
        await foreach (JObject record in SessionReader.ReadRecordsAsync(reader)) records.Add(record);
        Assert.Equal(42, records[0]["payload"]!["future"]!.Value<int>());
        Assert.Equal(folder, records[1]["payload"]!["cwd"]!.Value<string>());
        Assert.Equal(folder, records[2]["payload"]!["state"]!["environments"]!["environments"]!["local"]!["cwd"]!.Value<string>());
        Assert.Equal("/foreign/project", records[3]["payload"]!["text"]!.Value<string>());
        Assert.Equal("/foreign/project", records[3]["payload"]!["cwd"]!.Value<string>());
        Assert.Equal(JTokenType.String, records[4]["timestamp"]!.Type);
        await Assert.ThrowsAsync<IOException>(() => SessionImporter.ImportAsync(source, sessions, store));
        Assert.Single(Directory.GetFiles(sessions, "*.jsonl", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MissingMappingDoesNotCreateDestination()
    {
        (string source, string sessions, _, SessionMappingStore store) = await SetupAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => SessionImporter.ImportAsync(source, sessions, store));
        Assert.False(Directory.Exists(sessions));
    }

    [Fact]
    public async Task InvalidBodyLeavesNoPublishedFileOrTemporaryFile()
    {
        (string source, string sessions, string folder, SessionMappingStore store) = await SetupAsync();
        await store.SetAsync(id, folder);
        await File.AppendAllTextAsync(source, "\nnot json");
        await Assert.ThrowsAsync<InvalidDataException>(() => SessionImporter.ImportAsync(source, sessions, store));
        Assert.Empty(Directory.GetFiles(sessions, "*", SearchOption.AllDirectories));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
