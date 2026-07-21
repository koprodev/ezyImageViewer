using EzyImageViewer.Infrastructure;
using Xunit;

namespace EzyImageViewer.Tests.Infrastructure;

/// <summary>§10 저장 정책: a save either fully lands or leaves the previous file untouched.</summary>
public sealed class AtomicFileWriterTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"ezy-atomic-{Guid.NewGuid():N}");

    public AtomicFileWriterTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_directory))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Write_CreatesAndThenReplaces_LeavingNoTempFiles()
    {
        var path = Path.Combine(_directory, "target.bin");

        AtomicFileWriter.Write(path, [1, 2, 3]);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));

        AtomicFileWriter.Write(path, [9, 8]);
        Assert.Equal(new byte[] { 9, 8 }, File.ReadAllBytes(path));
        Assert.Single(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public void Write_StreamCallbackCommitsAllSegmentsWithoutAStagingBuffer()
    {
        var path = Path.Combine(_directory, "streamed.bin");

        AtomicFileWriter.Write(path, stream =>
        {
            stream.Write(new byte[] { 1, 2 });
            stream.Write(new byte[] { 3, 4 });
        });

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(path));
        Assert.Single(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public void Write_StreamCallbackFailureKeepsPreviousContentAndRemovesTempFile()
    {
        var path = Path.Combine(_directory, "stream-failure.bin");
        AtomicFileWriter.Write(path, [1, 2, 3]);

        var error = Record.Exception(() => AtomicFileWriter.Write(path, stream =>
        {
            stream.Write(new byte[] { 9, 8 });
            throw new InvalidOperationException("injected failure");
        }));

        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        Assert.Single(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public void Write_FailureKeepsThePreviousContent()
    {
        var path = Path.Combine(_directory, "protected.bin");
        AtomicFileWriter.Write(path, [1, 2, 3]);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var error = Record.Exception(() => AtomicFileWriter.Write(path, [9]));
            Assert.True(error is IOException or UnauthorizedAccessException, $"unexpected {error}");
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }
}
