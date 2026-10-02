using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexSync.Core;

public sealed record SessionMetadata(Guid Id, string WorkingDirectory, JObject Payload);

public static class SessionReader
{
    // The caller owns the reader. Records retain unknown properties for future Codex versions.
    public static async IAsyncEnumerable<JObject> ReadRecordsAsync(
        TextReader reader, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        int lineNumber = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JObject record;
            try
            {
                using JsonTextReader jsonReader = new(new StringReader(line))
                {
                    DateParseHandling = DateParseHandling.None
                };
                record = JObject.Load(jsonReader, new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                });
                if (jsonReader.Read())
                    throw new JsonReaderException("Unexpected content after the record.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"Invalid session JSON at line {lineNumber}.", exception);
            }

            yield return record;
        }
    }

    // Metadata inspection deliberately stops before the conversation body.
    public static async Task<SessionMetadata> ReadMetadataAsync(
        TextReader reader, CancellationToken cancellationToken = default)
    {
        await foreach (JObject? record in ReadRecordsAsync(reader, cancellationToken))
        {
            if (record["type"]?.Type != JTokenType.String || record.Value<string>("type") != "session_meta")
                throw new InvalidDataException("The first session record must be session_meta.");

            if (record["payload"] is not JObject payload ||
                payload["id"]?.Type != JTokenType.String ||
                !Guid.TryParse(payload.Value<string>("id"), out Guid id) ||
                id == Guid.Empty ||
                payload["cwd"]?.Type != JTokenType.String ||
                string.IsNullOrWhiteSpace(payload.Value<string>("cwd")))
                throw new InvalidDataException("Session metadata requires a session UUID and working directory.");

            return new SessionMetadata(id, payload.Value<string>("cwd")!, payload);
        }

        throw new InvalidDataException("The session file is empty.");
    }

    public static async Task<SessionMetadata> ReadMetadataAsync(
        string path, CancellationToken cancellationToken = default)
    {
        using StreamReader? reader = File.OpenText(path);
        return await ReadMetadataAsync(reader, cancellationToken);
    }
}
