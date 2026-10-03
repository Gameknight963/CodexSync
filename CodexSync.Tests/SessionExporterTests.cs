using CodexSync.Core;

namespace CodexSync.Tests;

public sealed class SessionExporterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CodexSync.Tests", Guid.NewGuid().ToString("N"));
    private readonly Guid id = Guid.NewGuid();

    private async Task<(string Source, string Sessions, string Archive)> SetupAsync()
    {
        string sessions = Path.Combine(root, "local");
        Directory.CreateDirectory(sessions);
        string source = Path.Combine(sessions, "arbitrary-name.jsonl");
        string metadata = "{\"type\":\"session_meta\",\"payload\":{\"id\":\"" + id.ToString("D") +
            "\",\"cwd\":\"/foreign/project\",\"future\":42,\"timestamp\":\"2026-10-02T12:00:00Z\"}}";
        await File.WriteAllTextAsync(source, metadata + "\r\n" +
            """{"type":"future_record","payload":{"text":"héllo /foreign/project"}}""" + "\r\n");
        return (source, sessions, Path.Combine(root, "archive"));
    }

    [Fact]
    public async Task ExportPreservesBytesAndCanBeImportedWithAnotherFolderMapping()
    {
        (string source, string sessions, string archive) = await SetupAsync();
        byte[] original = await File.ReadAllBytesAsync(source);
        string exported = await SessionExporter.ExportAsync(id, sessions, archive);
        Assert.Equal(Path.Combine(archive, "sessions", $"{id:D}.jsonl"), exported);
        Assert.Equal(original, await File.ReadAllBytesAsync(exported));
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        Assert.Single(Directory.GetFiles(archive, "*", SearchOption.AllDirectories));
        SessionMappingStore mappings = new(Path.Combine(root, "machine.json"));
        await mappings.SetAsync(id, root);
        string imported = await SessionImporter.ImportAsync(exported, Path.Combine(root, "other-machine"), mappings);
        Assert.Equal(root, (await SessionReader.ReadMetadataAsync(imported)).WorkingDirectory);
        await Assert.ThrowsAsync<IOException>(() => SessionExporter.ExportAsync(id, sessions, archive));
        Assert.Equal(original, await File.ReadAllBytesAsync(exported));
    }

    [Fact]
    public async Task PartialRecordIsNotPublished()
    {
        (string source, string sessions, string archive) = await SetupAsync();
        await File.AppendAllTextAsync(source, "{\"type\":");
        await Assert.ThrowsAsync<InvalidDataException>(() => SessionExporter.ExportAsync(id, sessions, archive));
        Assert.Empty(Directory.GetFiles(archive, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MissingAndAmbiguousSessionsDoNotCreateAnArchive()
    {
        (string source, string sessions, string archive) = await SetupAsync();
        await Assert.ThrowsAsync<FileNotFoundException>(() => SessionExporter.ExportAsync(Guid.NewGuid(), sessions, archive));
        File.Copy(source, Path.Combine(sessions, "duplicate.jsonl"));
        await Assert.ThrowsAsync<IOException>(() => SessionExporter.ExportAsync(id, sessions, archive));
        Assert.False(Directory.Exists(archive));
    }

    [Fact]
    public async Task ArchiveInsideLiveSessionsIsRejected()
    {
        (_, string sessions, _) = await SetupAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => SessionExporter.ExportAsync(id, sessions, sessions));
        await Assert.ThrowsAsync<ArgumentException>(() => SessionExporter.ExportAsync(id, sessions, Path.Combine(sessions, "archive")));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
