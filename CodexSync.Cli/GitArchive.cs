using System.Diagnostics;

namespace CodexSync.Cli;

internal sealed class GitArchive(string directory)
{
    public async Task PullAsync()
    {
        string root = (await RunAsync("rev-parse", "--show-toplevel")).Trim();
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("The configured archive must be the root of its own Git repository.");
        if (!string.IsNullOrWhiteSpace(await RunAsync("status", "--porcelain")))
            throw new IOException("The archive has uncommitted changes. Commit or stash them before sync.");
        await RunAsync("pull", "--ff-only");
    }

    public async Task PushAsync()
    {
        List<string> paths = new();
        if (Directory.Exists(Path.Combine(directory, "sessions"))) paths.Add("sessions");
        if (Directory.Exists(Path.Combine(directory, "conflicts"))) paths.Add("conflicts");
        if (paths.Count > 0)
        {
            await RunAsync(new[] { "add", "--" }.Concat(paths).ToArray());
            if (!string.IsNullOrWhiteSpace(await RunAsync("diff", "--cached", "--name-only")))
                await RunAsync("commit", "-m", "Sync Codex session histories");
        }
        await RunAsync("push");
    }

    private async Task<string> RunAsync(params string[] arguments)
    {
        ProcessStartInfo start = new("git")
        {
            WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(120));
        using Process process = Process.Start(start) ?? throw new IOException("Could not start Git.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new IOException("Git timed out after two minutes.");
        }
        string stdout = await output;
        string stderr = await error;
        if (process.ExitCode != 0) throw new IOException($"git {arguments[0]} failed: {stderr.Trim()} {stdout.Trim()}");
        return stdout;
    }
}
