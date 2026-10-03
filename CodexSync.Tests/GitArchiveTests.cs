using System.Diagnostics;
using CodexSync.Cli;

namespace CodexSync.Tests;

public sealed class GitArchiveTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CodexSync.Tests", Guid.NewGuid().ToString("N"));

    private async Task<(string A, string B)> SetupAsync()
    {
        Directory.CreateDirectory(root);
        string remote = Path.Combine(root, "remote.git");
        string a = Path.Combine(root, "a");
        string b = Path.Combine(root, "b");
        await GitAsync(root, "init", "--bare", "--initial-branch=main", remote);
        await GitAsync(root, "clone", remote, a);
        await ConfigureAsync(a);
        await File.WriteAllTextAsync(Path.Combine(a, ".gitattributes"), "*.jsonl -text\n");
        await GitAsync(a, "add", ".gitattributes");
        await GitAsync(a, "commit", "-m", "Initialize archive");
        await GitAsync(a, "push", "-u", "origin", "main");
        await GitAsync(root, "clone", remote, b);
        await ConfigureAsync(b);
        return (a, b);
    }

    [Fact]
    public async Task PushAndPullTransferSnapshotsAndNoOpDoesNotCreateACommit()
    {
        (string a, string b) = await SetupAsync();
        GitArchive first = new(a);
        GitArchive second = new(b);
        await first.PullAsync();
        Directory.CreateDirectory(Path.Combine(a, "sessions"));
        await File.WriteAllTextAsync(Path.Combine(a, "sessions", "sample.jsonl"), "{\"text\":\"shared\"}\n");
        await first.PushAsync();
        await second.PullAsync();
        Assert.Equal("{\"text\":\"shared\"}\n", await File.ReadAllTextAsync(Path.Combine(b, "sessions", "sample.jsonl")));
        string before = await GitAsync(b, "rev-parse", "HEAD");
        await second.PushAsync();
        Assert.Equal(before, await GitAsync(b, "rev-parse", "HEAD"));
        Assert.Equal("", (await GitAsync(b, "status", "--porcelain")).Trim());
    }

    [Fact]
    public async Task DirtyArchivesAndNestedFoldersAreRejected()
    {
        (string a, _) = await SetupAsync();
        await File.WriteAllTextAsync(Path.Combine(a, "unrelated.txt"), "do not commit automatically");
        await Assert.ThrowsAsync<IOException>(() => new GitArchive(a).PullAsync());
        string nested = Path.Combine(a, "nested");
        Directory.CreateDirectory(nested);
        await Assert.ThrowsAsync<IOException>(() => new GitArchive(nested).PullAsync());
        Assert.True(File.Exists(Path.Combine(a, "unrelated.txt")));
    }

    [Fact]
    public async Task DivergentGitCommitsAreNotTextMerged()
    {
        (string a, string b) = await SetupAsync();
        foreach (string repository in new[] { a, b })
        {
            await File.WriteAllTextAsync(Path.Combine(repository, "branch.txt"), repository);
            await GitAsync(repository, "add", "branch.txt");
            await GitAsync(repository, "commit", "-m", "Independent continuation");
        }
        await GitAsync(a, "push");
        string before = await GitAsync(b, "rev-parse", "HEAD");
        await Assert.ThrowsAsync<IOException>(() => new GitArchive(b).PullAsync());
        Assert.Equal(before, await GitAsync(b, "rev-parse", "HEAD"));
        Assert.Equal("", (await GitAsync(b, "status", "--porcelain")).Trim());
        Assert.False(File.Exists(Path.Combine(b, ".git", "MERGE_HEAD")));
    }

    private static async Task ConfigureAsync(string directory)
    {
        await GitAsync(directory, "config", "user.name", "CodexSync Test");
        await GitAsync(directory, "config", "user.email", "test@example.invalid");
        await GitAsync(directory, "config", "commit.gpgsign", "false");
    }

    private static async Task<string> GitAsync(string directory, params string[] arguments)
    {
        ProcessStartInfo start = new("git")
        {
            WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await stderr);
        return await stdout;
    }

    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }
}
