using CodexSync.Core;
using Newtonsoft.Json.Linq;

namespace CodexSync.Tests;

public class SessionReaderTests
{
    private const string Metadata = """
        {"type":"session_meta","payload":{"id":"01a0bafd-a912-7f71-9966-bdc18871698c","cwd":"C:\\Users\\other\\projects\\gflat","future":{"enabled":true}}}
        """;

    [Fact]
    public async Task MetadataPreservesForeignPathsAndUnknownFieldsWithoutReadingTheBody()
    {
        using StringReader reader = new("\n" + Metadata + "\nnot valid json");
        SessionMetadata metadata = await SessionReader.ReadMetadataAsync(reader);

        Assert.Equal(Guid.Parse("01a0bafd-a912-7f71-9966-bdc18871698c"), metadata.Id);
        Assert.Equal(@"C:\Users\other\projects\gflat", metadata.WorkingDirectory);
        Assert.True(metadata.Payload["future"]!["enabled"]!.Value<bool>());
        Assert.Equal("not valid json", await reader.ReadLineAsync());
    }

    [Fact]
    public async Task RecordsPreserveTimestampStringsAndUnknownRecordTypes()
    {
        using StringReader reader = new(Metadata + "\r\n\r\n" +
            """{"type":"future_record","timestamp":"2026-10-02T17:55:54.123456Z","payload":{"text":"a\nb"}}""");
        List<JObject> records = new();
        await foreach (JObject record in SessionReader.ReadRecordsAsync(reader)) records.Add(record);

        Assert.Equal(2, records.Count);
        Assert.Equal(JTokenType.String, records[1]["timestamp"]!.Type);
        Assert.Equal("2026-10-02T17:55:54.123456Z", records[1].Value<string>("timestamp"));
        Assert.Equal("a\nb", records[1]["payload"]!.Value<string>("text"));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("{} {}")]
    [InlineData("{\"type\":\"a\",\"type\":\"b\"}")]
    public async Task InvalidRecordsReportPhysicalLineNumber(string invalid)
    {
        using StringReader reader = new(Metadata + "\n\n" + invalid);
        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (JObject record in SessionReader.ReadRecordsAsync(reader)) { }
        });
        Assert.Contains("line 3", exception.Message);
        Assert.NotNull(exception.InnerException);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"type\":\"response_item\"}")]
    [InlineData("{\"type\":{}}")]
    [InlineData("{\"type\":\"session_meta\",\"payload\":{\"id\":\"bad\",\"cwd\":\"/repo\"}}")]
    [InlineData("{\"type\":\"session_meta\",\"payload\":{\"id\":\"01a0bafd-a912-7f71-9966-bdc18871698c\",\"cwd\":42}}")]
    public async Task InvalidMetadataIsRejected(string input)
    {
        using StringReader reader = new(input);
        await Assert.ThrowsAsync<InvalidDataException>(() => SessionReader.ReadMetadataAsync(reader));
    }

    [Fact]
    public async Task ReadingSupportsCancellation()
    {
        using StringReader reader = new(Metadata);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SessionReader.ReadMetadataAsync(reader, cancellation.Token));
    }
}
