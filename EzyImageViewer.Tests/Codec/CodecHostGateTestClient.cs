using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using EzyImageViewer.CodecProtocol;

namespace EzyImageViewer.Tests.Codec;

internal static class CodecHostGateTestClient
{
    private static int MaxTestOutputBytes => checked((int)(
        CodecWireProtocol.ResponseHeaderSize
        + Limits.MaxPayloadBytes
        + Limits.MaxDiagnosticBytes));

    public static CodecProtocolLimits Limits { get; } = new(
        maxInputBytes: 512L * 1024 * 1024,
        maxPayloadBytes: 192L * 1024 * 1024,
        maxDiagnosticBytes: 1024,
        maxDimension: 65_500,
        maxPageCount: 10_000,
        maxPixelCount: 500_000_000);

    public static async Task<byte[]> EncodeRequestAsync(CodecRequest request, byte[] input)
    {
        await using var wire = new MemoryStream();
        await CodecWireProtocol.WriteRequestAsync(
            wire,
            request,
            request.InputTransport == CodecInputTransport.Inline
                ? new MemoryStream(input, writable: false)
                : null,
            Limits,
            CancellationToken.None);
        return wire.ToArray();
    }

    public static async Task<CodecHostGateResult> RunAsync(
        CodecRequest request,
        byte[] input,
        TimeSpan? timeout = null)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var raw = await RunRawAsync(
            await EncodeRequestAsync(request, input),
            timeout ?? TimeSpan.FromSeconds(10));
        if (raw.ExitCode != 0)
            return new(raw, null, [], Stopwatch.GetElapsedTime(startedAt));

        await using var output = new MemoryStream(raw.StandardOutput, writable: false);
        var response = await CodecWireProtocol.ReadResponseAsync(
            output,
            Limits,
            CancellationToken.None);
        await using var payload = new MemoryStream();
        await CodecWireProtocol.CopyResponsePayloadAsync(
            output,
            payload,
            response,
            Limits,
            CancellationToken.None);
        if (output.Position != output.Length)
            throw new InvalidDataException("CodecHost emitted trailing response bytes.");
        if (response.RequestId != request.RequestId
            || response.Nonce != request.Nonce
            || response.Operation != request.Operation
            || response.Format != request.Format)
        {
            throw new InvalidDataException("CodecHost response correlation did not match the request.");
        }
        return new(
            raw,
            response,
            payload.ToArray(),
            Stopwatch.GetElapsedTime(startedAt));
    }

    public static async Task<CodecHostRawGateResult> RunRawAsync(
        ReadOnlyMemory<byte> standardInput,
        TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (standardInput.Length > Limits.MaxInputBytes + CodecWireProtocol.RequestHeaderSize)
            throw new InvalidDataException("CodecHost test input exceeded the protocol boundary.");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(FindHostExecutable())
            {
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        var elapsed = Stopwatch.StartNew();
        if (!process.Start())
            throw new InvalidOperationException("CodecHost could not be started.");
        var processHandle = process.Handle;

        using var deadline = new CancellationTokenSource(timeout);
        var outputTask = ReadBoundedAsync(
            process.StandardOutput.BaseStream,
            MaxTestOutputBytes);
        var errorTask = ReadBoundedAsync(process.StandardError.BaseStream, maxBytes: 4096);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(standardInput, deadline.Token);
            await process.StandardInput.BaseStream.FlushAsync(deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"CodecHost did not finish within {timeout}.");
        }
        elapsed.Stop();
        var standardOutput = await outputTask;
        var standardError = await errorTask;
        var memory = ReadPeakMemory(processHandle);
        return new(
            process.ExitCode,
            standardOutput,
            System.Text.Encoding.UTF8.GetString(standardError),
            elapsed.Elapsed,
            memory.PeakWorkingSetBytes,
            memory.PeakCommitBytes);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, int maxBytes)
    {
        await using var destination = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer);
            if (read == 0)
                return destination.ToArray();
            if (destination.Length + read > maxBytes)
                throw new InvalidDataException("CodecHost output exceeded the test boundary.");
            await destination.WriteAsync(buffer.AsMemory(0, read));
        }
    }

    private static string FindHostExecutable()
    {
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Debug"
            : "Release";
        var path = Path.Combine(
            FindRepositoryRoot(),
            "EzyImageViewer.CodecHost",
            "bin",
            configuration,
            "net10.0",
            "win-x64",
            "EzyImageViewer.CodecHost.exe");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("The CodecHost test executable was not built.", path);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EzyImageViewer.slnx")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException(
            "Repository root was not found from the test output directory.");
    }

    private static CodecHostPeakMemory ReadPeakMemory(nint processHandle)
    {
        var counters = new ProcessMemoryCounters
        {
            Size = checked((uint)Marshal.SizeOf<ProcessMemoryCounters>()),
        };
        if (!GetProcessMemoryInfo(processHandle, ref counters, counters.Size))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var result = new CodecHostPeakMemory(
            checked((long)counters.PeakWorkingSetSize),
            checked((long)counters.PeakPagefileUsage));
        if (result.PeakWorkingSetBytes <= 0 || result.PeakCommitBytes <= 0)
            throw new InvalidDataException("CodecHost peak memory counters were not populated.");
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(
        nint process,
        ref ProcessMemoryCounters counters,
        uint size);
}

internal sealed record CodecHostRawGateResult(
    int ExitCode,
    byte[] StandardOutput,
    string StandardError,
    TimeSpan Elapsed,
    long PeakWorkingSetBytes,
    long PeakCommitBytes);

internal sealed record CodecHostGateResult(
    CodecHostRawGateResult Process,
    CodecResponse? Response,
    byte[] Payload,
    TimeSpan EndToEndElapsed);

internal readonly record struct CodecHostPeakMemory(
    long PeakWorkingSetBytes,
    long PeakCommitBytes);
