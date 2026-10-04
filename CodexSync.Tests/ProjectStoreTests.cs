using CodexSync.Core;
using Newtonsoft.Json.Linq;

namespace CodexSync.Tests;

public sealed class ProjectStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CodexSync.Tests", Guid.NewGuid().ToString("N"));
    private string Archive => Path.Combine(root, "archive");

    private (ProjectStore Projects, SessionMappingStore Mappings) Machine(string name)
    {
        string local = Path.Combine(root, name);
        Directory.CreateDirectory(local);
        SessionMappingStore mappings = new(Path.Combine(local, "mappings.json"));
        return (new ProjectStore(Path.Combine(local, "machine.json"), Archive, mappings), mappings);
    }

    [Fact]
    public async Task ProjectRegistrationAndNewMappingsDoNotIncludeChatsOrDirtyArchive()
    {
        (ProjectStore projects, SessionMappingStore mappings) = Machine("a");
        await projects.AddProjectAsync("app", root, "git@github.com:Owner/App.git");
        Guid setupChat = Guid.NewGuid();
        await mappings.SetAsync(setupChat, root);
        ProjectCatalog catalog = await projects.GetAsync();
        Assert.Single(catalog.Projects);
        Assert.Empty(catalog.Sessions);
        Assert.False(Directory.Exists(Archive));
        Assert.Equal(root, await projects.ResolveFolderAsync(setupChat));
        PendingChanges pending = await projects.PublishPendingAsync();
        await projects.AcknowledgeAsync(pending);
        Assert.Empty((await projects.GetAsync()).Sessions);
    }

    [Fact]
    public async Task SharedSelectionResolvesOnAnotherMachineWithoutPerSessionMappings()
    {
        (ProjectStore first, _) = Machine("a");
        string firstCheckout = Path.Combine(root, "checkout-a");
        string secondCheckout = Path.Combine(root, "checkout-b");
        Directory.CreateDirectory(firstCheckout);
        Directory.CreateDirectory(Path.Combine(secondCheckout, "src", "app"));
        await first.AddProjectAsync("app", firstCheckout, "git@github.com:Owner/App.git");
        Guid chat = Guid.NewGuid();
        await first.IncludeAsync(chat, "app", "src/app");
        Assert.False(Directory.Exists(Archive));
        PendingChanges published = await first.PublishPendingAsync();
        await first.AcknowledgeAsync(published);
        string json = await File.ReadAllTextAsync(Path.Combine(Archive, ProjectStore.ManifestName));
        Assert.DoesNotContain(firstCheckout, json);
        Assert.DoesNotContain("checkout-a", json);
        (ProjectStore second, SessionMappingStore mappings) = Machine("b");
        Assert.Null(await second.ResolveFolderAsync(chat));
        await second.AddProjectAsync("app", secondCheckout, "https://github.com/owner/app.git");
        Assert.Equal((await first.GetAsync()).Projects[0].Id, (await second.GetAsync()).Projects[0].Id);
        Assert.Equal(Path.Combine(secondCheckout, "src", "app"), await second.ResolveFolderAsync(chat));
        Assert.Null(await mappings.GetAsync(chat));
        Assert.True((await second.GetAsync()).Sessions.Single().Included);
        await mappings.SetAsync(chat, firstCheckout);
        Assert.Equal(firstCheckout, await second.ResolveFolderAsync(chat));
    }

    [Fact]
    public async Task LegacyMappingsStaySelectedButNewMappingsDoNotAutoEnroll()
    {
        (ProjectStore projects, SessionMappingStore mappings) = Machine("a");
        Guid old = Guid.NewGuid();
        Guid fresh = Guid.NewGuid();
        await mappings.SetAsync(old, root);
        await projects.InitializeAsync();
        await mappings.SetAsync(fresh, root);
        Assert.Equal(old, (await projects.GetAsync()).Sessions.Single().Id);
        await projects.IncludeAsync(fresh);
        PendingChanges published = await projects.PublishPendingAsync();
        await projects.AcknowledgeAsync(published);
        Assert.Equal(2, (await projects.GetAsync()).Sessions.Count);
        Assert.True((await projects.GetAsync()).Sessions.All(session => session.Included));
    }

    [Fact]
    public async Task SharedExclusionIsNotUndoneByLegacyMigrationElsewhere()
    {
        (ProjectStore first, SessionMappingStore firstMappings) = Machine("a");
        Guid id = Guid.NewGuid();
        await firstMappings.SetAsync(id, root);
        await first.ExcludeAsync(id);
        PendingChanges changes = await first.PublishPendingAsync();
        await first.AcknowledgeAsync(changes);
        (ProjectStore second, SessionMappingStore secondMappings) = Machine("b");
        await secondMappings.SetAsync(id, root);
        Assert.False((await second.GetAsync()).Sessions.Single().Included);
        await second.PublishPendingAsync();
        Assert.False((await first.GetAsync()).Sessions.Single().Included);
        Assert.Equal(root, await secondMappings.GetAsync(id));
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("/absolute")]
    [InlineData("C:\\absolute")]
    [InlineData("src/../../other")]
    public async Task SubfoldersCannotEscapeProject(string subfolder)
    {
        (ProjectStore projects, _) = Machine("a");
        await projects.AddProjectAsync("app", root, "https://github.com/owner/app");
        await Assert.ThrowsAsync<InvalidDataException>(() => projects.IncludeAsync(Guid.NewGuid(), "app", subfolder));
        Assert.Empty((await projects.GetAsync()).Sessions);
    }

    [Fact]
    public async Task AcknowledgementDoesNotEraseSetupChangesMadeAfterPublishing()
    {
        (ProjectStore projects, SessionMappingStore mappings) = Machine("a");
        Guid id = Guid.NewGuid();
        await projects.InitializeAsync();
        await mappings.SetAsync(id, root);
        await projects.IncludeAsync(id);
        PendingChanges changes = await projects.PublishPendingAsync();
        await projects.ExcludeAsync(id);
        await projects.AcknowledgeAsync(changes);
        Assert.False((await projects.GetAsync()).Sessions.Single().Included);
        await projects.PublishPendingAsync();
        Assert.False(JObject.Parse(await File.ReadAllTextAsync(Path.Combine(Archive, ProjectStore.ManifestName)))["sessions"]![id.ToString("D")]!.Value<bool>("included"));
    }

    [Fact]
    public async Task DifferentRepositoriesCannotShareANameAndUnknownManifestFieldsSurvive()
    {
        (ProjectStore projects, _) = Machine("a");
        Directory.CreateDirectory(Archive);
        await File.WriteAllTextAsync(Path.Combine(Archive, ProjectStore.ManifestName), """{"version":1,"projects":{},"sessions":{},"future":{"value":17}}""");
        await projects.AddProjectAsync("app", root, "https://github.com/owner/app");
        await Assert.ThrowsAsync<InvalidDataException>(() => projects.AddProjectAsync("app", root, "https://github.com/other/app"));
        await projects.PublishPendingAsync();
        Assert.Equal(17, JObject.Parse(await File.ReadAllTextAsync(Path.Combine(Archive, ProjectStore.ManifestName)))["future"]!["value"]!.Value<int>());
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
