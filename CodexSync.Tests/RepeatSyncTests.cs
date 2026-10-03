using CodexSync.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexSync.Tests;

public sealed class RepeatSyncTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CodexSync.Tests", Guid.NewGuid().ToString("N"));
    private readonly Guid id = Guid.NewGuid();

    private async Task<(string Original, string Archive, string Local, SessionMappingStore Mappings)> SetupAsync()
    {
        string original = Path.Combine(root, "first", "sessions");
        Directory.CreateDirectory(original);
        JObject metadata = new()
        {
            ["type"] = "session_meta", ["timestamp"] = "2026-10-02T12:00:00Z",
            ["payload"] = new JObject { ["id"] = id.ToString("D"), ["cwd"] = "/original/project" }
        };
        await File.WriteAllTextAsync(Path.Combine(original, "source.jsonl"), metadata.ToString(Formatting.None) + "\n" +
            """{"type":"response_item","payload":{"text":"common history"}}""" + "\n");
        SessionMappingStore mappings = new(Path.Combine(root, "mappings.json"));
        await mappings.SetAsync(id, root);
        return (original, Path.Combine(root, "archive"), Path.Combine(root, "second", "sessions"), mappings);
    }

    [Fact]
    public async Task ContinuationsTravelBothDirectionsWithoutPathChurnOrRollback()
    {
        (string original, string archive, string local, SessionMappingStore mappings) = await SetupAsync();
        string shared = await SessionExporter.ExportAsync(id, original, archive);
        byte[] sharedOriginal = await File.ReadAllBytesAsync(shared);
        string imported = await SessionImporter.ImportAsync(shared, local, mappings);
        Assert.Equal(shared, await SessionExporter.ExportAsync(id, local, archive));
        Assert.Equal(sharedOriginal, await File.ReadAllBytesAsync(shared));
        await File.AppendAllTextAsync(imported, """{"type":"response_item","payload":{"text":"second machine continuation"}}""" + "\n");
        await SessionExporter.ExportAsync(id, local, archive);
        Assert.StartsWith(System.Text.Encoding.UTF8.GetString(sharedOriginal), await File.ReadAllTextAsync(shared));
        Assert.Equal(HistoryRelationship.ExistingExtendsIncoming,
            await SessionHistory.CompareAsync(shared, Path.Combine(original, "source.jsonl")));
        await SessionExporter.ExportAsync(id, original, archive);
        Assert.Contains("second machine continuation", await File.ReadAllTextAsync(shared));
        await SessionImporter.ImportAsync(shared, original, mappings);
        Assert.Contains("second machine continuation", await File.ReadAllTextAsync(Path.Combine(original, "source.jsonl")));
        await File.AppendAllTextAsync(shared, """{"type":"response_item","payload":{"text":"remote continuation"}}""" + "\n");
        await SessionImporter.ImportAsync(shared, local, mappings);
        Assert.Contains("remote continuation", await File.ReadAllTextAsync(imported));
        Assert.Equal(root, (await SessionReader.ReadMetadataAsync(imported)).WorkingDirectory);
    }

    [Fact]
    public async Task DivergencePreservesBothVersionsAndLeavesOriginalsUnchanged()
    {
        (string original, string archive, string local, SessionMappingStore mappings) = await SetupAsync();
        string shared = await SessionExporter.ExportAsync(id, original, archive);
        string imported = await SessionImporter.ImportAsync(shared, local, mappings);
        await File.AppendAllTextAsync(shared, """{"type":"response_item","payload":{"text":"remote branch"}}""" + "\n");
        await File.AppendAllTextAsync(imported, """{"type":"response_item","payload":{"text":"local branch"}}""" + "\n");
        byte[] remote = await File.ReadAllBytesAsync(shared);
        byte[] machine = await File.ReadAllBytesAsync(imported);
        await Assert.ThrowsAsync<IOException>(() => SessionExporter.ExportAsync(id, local, archive));
        Assert.Equal(remote, await File.ReadAllBytesAsync(shared));
        Assert.Equal(machine, await File.ReadAllBytesAsync(imported));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(archive, "conflicts"), "*.jsonl", SearchOption.AllDirectories).Length);
        string conflicts = Path.Combine(root, "private-conflicts");
        await Assert.ThrowsAsync<IOException>(() => SessionImporter.ImportAsync(shared, local, mappings, conflictDirectory: conflicts));
        Assert.Equal(2, Directory.GetFiles(conflicts, "*.jsonl", SearchOption.AllDirectories).Length);
        Assert.Equal(remote, await File.ReadAllBytesAsync(shared));
        Assert.Equal(machine, await File.ReadAllBytesAsync(imported));
    }

    [Fact]
    public async Task LocalSettingsEventsDoNotCauseFalseDivergenceOrHideRealContinuations()
    {
        (string original, string archive, string local, SessionMappingStore mappings) = await SetupAsync();
        string shared = await SessionExporter.ExportAsync(id, original, archive);
        string imported = await SessionImporter.ImportAsync(shared, local, mappings);
        await File.AppendAllTextAsync(shared, """{"type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"cwd":"/old"}}}""" + "\n");
        await File.AppendAllTextAsync(imported, """{"type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"cwd":"/local"}}}""" + "\n");
        Assert.Equal(HistoryRelationship.Equal, await SessionHistory.CompareAsync(shared, imported));
        byte[] originalShared = await File.ReadAllBytesAsync(shared);
        await SessionExporter.ExportAsync(id, local, archive);
        Assert.Equal(originalShared, await File.ReadAllBytesAsync(shared));
        await File.AppendAllTextAsync(imported, """{"type":"response_item","payload":{"text":"real continuation"}}""" + "\n");
        await SessionExporter.ExportAsync(id, local, archive);
        Assert.Equal(HistoryRelationship.Equal, await SessionHistory.CompareAsync(shared, imported));
        Assert.Contains("real continuation", await File.ReadAllTextAsync(shared));
    }

    [Fact]
    public async Task CorruptIncomingRecordCannotBeTreatedAsAPrefix()
    {
        (string original, string archive, _, _) = await SetupAsync();
        string shared = await SessionExporter.ExportAsync(id, original, archive);
        await File.AppendAllTextAsync(Path.Combine(original, "source.jsonl"), "{broken");
        await Assert.ThrowsAsync<InvalidDataException>(() => SessionExporter.ExportAsync(id, original, archive));
        Assert.DoesNotContain("broken", await File.ReadAllTextAsync(shared));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
