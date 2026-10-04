using System.Diagnostics;

namespace CodexSync.Cli;

internal static class GitProject
{
    public static async Task<(string Folder, string Remote)> DescribeAsync(string folder)
    {
        string root = Path.GetFullPath((await RunAsync(folder, "rev-parse", "--show-toplevel")).Trim());
        string remote = (await RunAsync(root, "remote", "get-url", "origin")).Trim();
        return (root, remote);
    }

    private static async Task<string> RunAsync(string folder, params string[] arguments)
    {
        ProcessStartInfo start = new("git")
        {
            WorkingDirectory = Path.GetFullPath(folder), UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new IOException("Could not start Git.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException($"git {arguments[0]} failed: {(await stderr).Trim()}");
        return await stdout;
    }
}
