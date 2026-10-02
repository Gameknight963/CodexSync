using CodexSync.Core;
using Newtonsoft.Json.Linq;

namespace CodexSync.Tests;

public sealed class SessionMappingStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "CodexSync.Tests", Guid.NewGuid().ToString("N"));
    private string MappingPath => Path.Combine(directory, "machine", "mappings.json");
    private string Folder => Path.Combine(directory, "checkout");

    [Fact]
    public async Task MappingsSurviveReloadAndUpdatesLeaveOtherSessionsIntact()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        SessionMappingStore store = new(MappingPath);
        Assert.Null(await store.GetAsync(first));
        Assert.False(File.Exists(MappingPath));

        await store.SetAsync(first, Folder);
        await store.SetAsync(second, Folder + "2");
        await store.SetAsync(first, Folder + "3");
        SessionMappingStore reloaded = new(MappingPath);
        Assert.Equal(Folder + "3", await reloaded.GetAsync(first));
        Assert.Equal(Folder + "2", await reloaded.GetAsync(second));
        Assert.True(await reloaded.RemoveAsync(first));
        Assert.False(await reloaded.RemoveAsync(first));
        Assert.Null(await store.GetAsync(first));
        Assert.Equal(Folder + "2", await store.GetAsync(second));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"version\":2,\"sessions\":{}}")]
    [InlineData("{\"version\":99999999999999999999999999999,\"sessions\":{}}")]
    [InlineData("{\"version\":1,\"sessions\":[]}")]
    [InlineData("{\"version\":1,\"sessions\":{\"bad-id\":\"/repo\"}}")]
    public async Task InvalidExistingFilesAreNeverOverwritten(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MappingPath)!);
        await File.WriteAllTextAsync(MappingPath, json);
        SessionMappingStore store = new(MappingPath);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SetAsync(Guid.NewGuid(), Folder));
        Assert.Equal(json, await File.ReadAllTextAsync(MappingPath));
    }

    [Fact]
    public async Task UnknownManifestFieldsSurviveUpdates()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MappingPath)!);
        await File.WriteAllTextAsync(MappingPath, """{"version":1,"sessions":{},"future":{"value":17}}""");
        await new SessionMappingStore(MappingPath).SetAsync(Guid.NewGuid(), Folder);
        JObject document = JObject.Parse(await File.ReadAllTextAsync(MappingPath));
        Assert.Equal(17, document["future"]!["value"]!.Value<int>());
    }

    [Fact]
    public async Task InvalidArgumentsDoNotCreateFiles()
    {
        SessionMappingStore store = new(MappingPath);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(Guid.Empty, Folder));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(Guid.NewGuid(), "relative/folder"));
        Assert.False(File.Exists(MappingPath));
    }

    [Fact]
    public async Task CancelledUpdateLeavesExistingMappingIntact()
    {
        Guid id = Guid.NewGuid();
        SessionMappingStore store = new(MappingPath);
        await store.SetAsync(id, Folder);
        string before = await File.ReadAllTextAsync(MappingPath);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SetAsync(id, Folder + "2", cancellation.Token));
        Assert.Equal(before, await File.ReadAllTextAsync(MappingPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(MappingPath)!, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
