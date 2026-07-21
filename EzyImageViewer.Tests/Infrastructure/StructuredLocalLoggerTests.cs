using System.Text.Json;
using EzyImageViewer.Infrastructure;
using Xunit;

namespace EzyImageViewer.Tests.Infrastructure;

public sealed class StructuredLocalLoggerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ezy-log-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Write_HashesDocumentPathAndOmitsExceptionMessageAndContent()
    {
        var logs = Path.Combine(_directory, "logs");
        var protector = new PrivacyPathProtector(Enumerable.Range(0, 32)
            .Select(value => (byte)value).ToArray());
        var logger = new StructuredLocalLogger(
            logs,
            new StructuredLocalLoggerOptions { ApplicationVersion = "1.0.0" },
            pathProtector: protector);
        var documentPath = Path.Combine(_directory, "private", "customer-secret.png");

        Assert.True(logger.TryWrite(
            LocalLogLevel.Error,
            new StructuredLogEvent
            {
                Name = "decode.failed",
                ErrorCode = "E_INVALID_DATA",
                Renderer = "Skia",
                Format = "PNG",
                ElapsedMilliseconds = 42,
                DocumentPath = documentPath,
            },
            new IOException($"confidential-content at {documentPath}")));

        var text = File.ReadAllText(Assert.Single(Directory.EnumerateFiles(logs)));
        Assert.Contains(protector.HashPath(documentPath), text, StringComparison.Ordinal);
        Assert.DoesNotContain(documentPath, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("customer-secret.png", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("confidential-content", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IOException", text, StringComparison.Ordinal);
        Assert.Contains("operatingSystem", text, StringComparison.Ordinal);
        Assert.Equal("<path:redacted>", PrivacyPathProtector.Redact(documentPath));
    }

    [Fact]
    public void Write_RollsBySizeAndEnforcesAgeAndFileCountRetention()
    {
        var logs = Path.Combine(_directory, "logs");
        Directory.CreateDirectory(logs);
        var oldPath = Path.Combine(logs, "ezy-20200101-000.jsonl");
        File.WriteAllText(oldPath, "old");
        File.SetLastWriteTimeUtc(oldPath, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var logger = new StructuredLocalLogger(logs, new StructuredLocalLoggerOptions
        {
            ApplicationVersion = "1.0.0",
            MaximumFileBytes = 512,
            MaximumFiles = 2,
            Retention = TimeSpan.FromDays(2),
        });

        for (var index = 0; index < 12; index++)
        {
            Assert.True(logger.TryWrite(LocalLogLevel.Information, new StructuredLogEvent
            {
                Name = $"render.{index}",
                Renderer = "Skia",
                Format = "PNG",
                ElapsedMilliseconds = index,
            }));
        }

        var files = Directory.EnumerateFiles(logs, "ezy-*.jsonl").ToList();
        Assert.InRange(files.Count, 1, 2);
        Assert.DoesNotContain(oldPath, files);
        Assert.All(files, path => Assert.InRange(new FileInfo(path).Length, 1, 512));
    }

    [Fact]
    public async Task ConcurrentWrites_ProduceCompleteJsonLines()
    {
        var logs = Path.Combine(_directory, "logs");
        var logger = new StructuredLocalLogger(logs, new StructuredLocalLoggerOptions
        {
            ApplicationVersion = "1.0.0",
            MaximumFileBytes = 1024 * 1024,
        });

        await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(() =>
            Assert.True(logger.TryWrite(LocalLogLevel.Warning, new StructuredLogEvent
            {
                Name = $"decode.{index}",
                ErrorCode = "E_CORRUPT",
            })))));

        var lines = Directory.EnumerateFiles(logs, "*.jsonl")
            .SelectMany(File.ReadAllLines)
            .ToList();
        Assert.Equal(100, lines.Count);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }
    }

    [Fact]
    public void UnsafeFreeTextToken_IsRejectedBeforeItCanReachDisk()
    {
        var logs = Path.Combine(_directory, "logs");
        var logger = new StructuredLocalLogger(logs);

        Assert.Throws<ArgumentException>(() => logger.TryWrite(
            LocalLogLevel.Error,
            new StructuredLogEvent
            {
                Name = "decode.failed",
                Renderer = Path.Combine(_directory, "private.png"),
            }));
        Assert.False(Directory.Exists(logs));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
