using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexSync.Cli;

internal static class CodexRegistration
{
    public static async Task RegisterAsync(Guid id, string folder, string codexHome)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        ProcessStartInfo start = new("codex")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("app-server");
        start.ArgumentList.Add("--stdio");
        start.Environment["CODEX_HOME"] = Path.GetFullPath(codexHome);
        using Process process = Process.Start(start) ?? throw new IOException("Could not start Codex.");
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await RequestAsync(process, 1, "initialize", new JObject
            {
                ["clientInfo"] = new JObject { ["name"] = "codexsync", ["version"] = "0.1.0" }
            }, timeout.Token);
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}");
            JObject read = await RequestAsync(process, 2, "thread/read", new JObject
            {
                ["threadId"] = id.ToString("D"), ["includeTurns"] = false
            }, timeout.Token);
            if (read["thread"]?["cwd"]?.Value<string>() == folder)
            {
                string? cursor = null;
                int requestId = 3;
                do
                {
                    JObject listing = await RequestAsync(process, requestId++, "thread/list", new JObject
                    {
                        ["cwd"] = folder, ["limit"] = 100, ["cursor"] = cursor,
                        ["useStateDbOnly"] = false,
                        ["sourceKinds"] = new JArray("cli", "vscode", "exec", "appServer", "unknown",
                            "subAgent", "subAgentReview", "subAgentCompact", "subAgentThreadSpawn", "subAgentOther")
                    }, timeout.Token);
                    if (listing["data"] is JArray data && data.Any(thread => thread["id"]?.Value<string>() == id.ToString("D")))
                        return;
                    cursor = listing.Value<string>("nextCursor");
                } while (cursor is not null);
            }
            JObject result = await RequestAsync(process, 1000000, "thread/resume", new JObject
            {
                ["threadId"] = id.ToString("D"), ["cwd"] = folder
            }, timeout.Token);
            if (result["thread"]?["id"]?.Value<string>() != id.ToString("D") ||
                result["thread"]?["cwd"]?.Value<string>() != folder)
                throw new IOException("Codex did not resume the expected session in its mapped folder.");
            // No turn is started: registration does not send a prompt or call a model.
        }
        catch (OperationCanceledException)
        {
            throw new IOException("Codex registration timed out after 30 seconds.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await stderr;
        }
    }

    private static async Task<JObject> RequestAsync(Process process, int id, string method,
        JObject parameters, CancellationToken cancellationToken)
    {
        JObject request = new() { ["id"] = id, ["method"] = method, ["params"] = parameters };
        await process.StandardInput.WriteLineAsync(request.ToString(Formatting.None).AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            JObject response = JObject.Parse(line);
            if (response["id"]?.Value<int>() != id) continue;
            if (response["error"] is JObject error)
                throw new IOException($"Codex {method} failed: {error.Value<string>("message")}");
            return response["result"] as JObject ?? throw new IOException("Codex returned an invalid response.");
        }
        throw new IOException("Codex exited before responding.");
    }
}
