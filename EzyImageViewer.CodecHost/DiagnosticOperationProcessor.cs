using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EzyImageViewer.CodecHost;

internal enum DiagnosticOperation
{
    Probe = 1,
    Echo = 2,
    Sleep = 3,
    Allocate = 4,
    TryNetwork = 5,
    TryWriteOutsideTemp = 6,
}

internal readonly record struct DiagnosticOperationResult(bool Succeeded, byte[] Payload)
{
    public static DiagnosticOperationResult Success(byte[]? payload = null) =>
        new(true, payload ?? []);

    public static DiagnosticOperationResult Denied() => new(false, []);
}

internal static class DiagnosticOperationProcessor
{
    internal const int MaxEchoBytes = 1024 * 1024;
    internal const int MaxSleepMilliseconds = 120_000;
    internal const long MaxAllocationBytes = 512L * 1024 * 1024;
    private const int AllocationChunkBytes = 1024 * 1024;
    private static readonly byte[] ProbePayload = Encoding.ASCII.GetBytes("ezy-codec-host-b1");

    public static async Task<DiagnosticOperationResult> ExecuteAsync(
        DiagnosticOperation operation,
        ReadOnlyMemory<byte> payload)
    {
        return operation switch
        {
            DiagnosticOperation.Probe => Probe(payload),
            DiagnosticOperation.Echo => Echo(payload),
            DiagnosticOperation.Sleep => await SleepAsync(payload).ConfigureAwait(false),
            DiagnosticOperation.Allocate => Allocate(payload),
            DiagnosticOperation.TryNetwork => await TryNetworkAsync(payload).ConfigureAwait(false),
            DiagnosticOperation.TryWriteOutsideTemp => TryWriteOutsideTemp(payload),
            _ => throw new InvalidDataException("Unknown diagnostic operation."),
        };
    }

    private static DiagnosticOperationResult Probe(ReadOnlyMemory<byte> payload)
    {
        RequireEmpty(payload);
        return DiagnosticOperationResult.Success(ProbePayload.ToArray());
    }

    private static DiagnosticOperationResult Echo(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > MaxEchoBytes)
            throw new InvalidDataException("Echo payload exceeds its operation limit.");
        return DiagnosticOperationResult.Success(payload.ToArray());
    }

    private static async Task<DiagnosticOperationResult> SleepAsync(ReadOnlyMemory<byte> payload)
    {
        var milliseconds = ReadInt32(payload);
        if (milliseconds is < 0 or > MaxSleepMilliseconds)
            throw new InvalidDataException("Sleep duration is outside its operation limit.");
        await Task.Delay(milliseconds).ConfigureAwait(false);
        return DiagnosticOperationResult.Success();
    }

    private static DiagnosticOperationResult Allocate(ReadOnlyMemory<byte> payload)
    {
        var requestedBytes = ReadInt64(payload);
        if (requestedBytes is < 0 or > MaxAllocationBytes)
            throw new InvalidDataException("Allocation size is outside its operation limit.");

        var chunks = new List<byte[]>();
        var remaining = requestedBytes;
        while (remaining > 0)
        {
            var chunkLength = checked((int)Math.Min(remaining, AllocationChunkBytes));
            var chunk = GC.AllocateUninitializedArray<byte>(chunkLength);
            TouchEveryPage(chunk);
            chunks.Add(chunk);
            remaining -= chunkLength;
        }

        GC.KeepAlive(chunks);
        var response = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(response, requestedBytes);
        return DiagnosticOperationResult.Success(response);
    }

    private static async Task<DiagnosticOperationResult> TryNetworkAsync(ReadOnlyMemory<byte> payload)
    {
        var port = ReadInt32(payload);
        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
            throw new InvalidDataException("Port is outside the valid range.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
            return DiagnosticOperationResult.Success([1]);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return DiagnosticOperationResult.Success([0]);
        }
    }

    private static DiagnosticOperationResult TryWriteOutsideTemp(ReadOnlyMemory<byte> payload)
    {
        RequireEmpty(payload);
        var probePath = Path.Combine(
            AppContext.BaseDirectory,
            $".codec-host-write-probe-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(probePath, [0x45, 0x49, 0x56]);
            return DiagnosticOperationResult.Success([1]);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return DiagnosticOperationResult.Success([0]);
        }
        finally
        {
            try
            {
                File.Delete(probePath);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
            }
        }
    }

    private static int ReadInt32(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length != sizeof(int))
            throw new InvalidDataException("Operation requires one Int32 payload.");
        return BinaryPrimitives.ReadInt32LittleEndian(payload.Span);
    }

    private static long ReadInt64(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length != sizeof(long))
            throw new InvalidDataException("Operation requires one Int64 payload.");
        return BinaryPrimitives.ReadInt64LittleEndian(payload.Span);
    }

    private static void RequireEmpty(ReadOnlyMemory<byte> payload)
    {
        if (!payload.IsEmpty)
            throw new InvalidDataException("Operation payload must be empty.");
    }

    private static void TouchEveryPage(Span<byte> memory)
    {
        const int pageSize = 4096;
        for (var offset = 0; offset < memory.Length; offset += pageSize)
            memory[offset] = 1;
        if (!memory.IsEmpty)
            memory[^1] = 1;
    }
}
