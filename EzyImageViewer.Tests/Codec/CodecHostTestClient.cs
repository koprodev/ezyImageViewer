using System.Diagnostics;
using EzyImageViewer.CodecProtocol;

namespace EzyImageViewer.Tests.Codec;

internal static class CodecHostTestClient
{
    private static readonly CodecProtocolLimits Limits = new(
        maxInputBytes: 512L * 1024 * 1024,
        maxPayloadBytes: 192L * 1024 * 1024,
        maxDiagnosticBytes: 1024,
        maxDimension: 65_500,
        maxPageCount: 10_000,
        maxPixelCount: 500_000_000);

    public static async Task<CodecHostTestResult> RunAsync(
        CodecRequest request,
        byte[] input,
        TimeSpan? timeout = null,
        ReadOnlyMemory<byte> trailingInput = default)
    {
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Debug"
            : "Release";
        var executable = Path.Combine(
            FindRepositoryRoot(),
            "EzyImageViewer.CodecHost",
            "bin",
            configuration,
            "net10.0",
            "win-x64",
            "EzyImageViewer.CodecHost.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("The CodecHost test executable was not built.", executable);

        await using var standardInput = new MemoryStream();
        await CodecWireProtocol.WriteRequestAsync(
            standardInput,
            request,
            request.InputTransport == CodecInputTransport.Inline
                ? new MemoryStream(input, writable: false)
                : null,
            Limits,
            CancellationToken.None);
        if (!trailingInput.IsEmpty)
            await standardInput.WriteAsync(trailingInput);
        standardInput.Position = 0;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        if (!process.Start())
            throw new InvalidOperationException("CodecHost could not be started.");

        await using var standardOutput = new MemoryStream();
        var outputTask = process.StandardOutput.BaseStream.CopyToAsync(standardOutput);
        var errorTask = process.StandardError.ReadToEndAsync();
        await standardInput.CopyToAsync(process.StandardInput.BaseStream);
        await process.StandardInput.BaseStream.FlushAsync();
        process.StandardInput.Close();

        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("CodecHost did not finish within the test deadline.");
        }

        await outputTask;
        var standardError = await errorTask;
        if (process.ExitCode != 0)
            return new CodecHostTestResult(process.ExitCode, null, [], standardError);

        standardOutput.Position = 0;
        var response = await CodecWireProtocol.ReadResponseAsync(
            standardOutput,
            Limits,
            CancellationToken.None);
        await using var payload = new MemoryStream();
        await CodecWireProtocol.CopyResponsePayloadAsync(
            standardOutput,
            payload,
            response,
            Limits,
            CancellationToken.None);
        if (standardOutput.Position != standardOutput.Length)
            throw new InvalidDataException("CodecHost emitted trailing response bytes.");
        if (response.RequestId != request.RequestId || response.Nonce != request.Nonce)
            throw new InvalidDataException("CodecHost response correlation did not match the request.");
        if (response.Operation != request.Operation || response.Format != request.Format)
            throw new InvalidDataException("CodecHost response operation or format did not match the request.");
        return new CodecHostTestResult(
            process.ExitCode,
            response,
            payload.ToArray(),
            standardError);
    }

    public static CodecRequest Request(
        CodecOperation operation,
        CodecFormat format,
        long inputLength,
        int pageIndex = -1,
        int targetWidth = 0,
        int targetHeight = 0) => new(
            Guid.NewGuid(),
            Nonce: 0xB1B2B3B4B5B6B7B8,
            operation,
            format,
            CodecInputTransport.Inline,
            inputLength,
            InputHandle: 0,
            pageIndex,
            targetWidth,
            targetHeight);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EzyImageViewer.slnx")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }
}

internal sealed record CodecHostTestResult(
    int ExitCode,
    CodecResponse? Response,
    byte[] Payload,
    string StandardError);
