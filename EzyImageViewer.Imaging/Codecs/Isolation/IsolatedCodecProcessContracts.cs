using Microsoft.Win32.SafeHandles;

namespace EzyImageViewer.Imaging.Codecs.Isolation;

[Flags]
internal enum AppContainerCapabilities
{
    None = 0,
    CodeGeneration = 1,
}

internal enum AppContainerProfileSource
{
    Classic = 0,
    ExistingPackage = 1,
}

internal delegate ValueTask<ReadOnlyMemory<byte>> IsolatedCodecStandardInputFactory(
    nint inheritedReadHandle,
    CancellationToken cancellationToken);

/// <summary>Caller-owned source handle and control-message factory for one inherited file.</summary>
internal sealed record InheritedReadOnlySource(
    SafeFileHandle Handle,
    IsolatedCodecStandardInputFactory CreateStandardInputAsync);

/// <summary>One-shot input passed to an isolated codec executable.</summary>
internal sealed record IsolatedCodecProcessRequest(
    string ExecutablePath,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    ReadOnlyMemory<byte> StandardInput,
    InheritedReadOnlySource? InheritedSource = null);

/// <summary>
/// Caller-owned resource policy. No product defaults live at the process boundary; the codec
/// integration must select and validate every budget before launching untrusted native code.
/// </summary>
internal sealed record IsolatedCodecProcessPolicy(
    string AppContainerName,
    AppContainerProfileSource ProfileSource,
    string AppContainerDisplayName,
    string AppContainerDescription,
    AppContainerCapabilities Capabilities,
    TimeSpan WallClockDeadline,
    TimeSpan PerProcessUserTimeLimit,
    long ProcessMemoryLimitBytes,
    int MaxStandardInputBytes,
    int MaxStandardOutputBytes,
    int MaxStandardErrorBytes,
    uint ForcedTerminationExitCode)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(AppContainerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(AppContainerDisplayName);
        ArgumentNullException.ThrowIfNull(AppContainerDescription);
        if (!Enum.IsDefined(ProfileSource))
            throw new ArgumentOutOfRangeException(nameof(ProfileSource));
        if (AppContainerName.Length > 64)
            throw new ArgumentOutOfRangeException(nameof(AppContainerName));
        if (AppContainerDisplayName.Length > 512)
            throw new ArgumentOutOfRangeException(nameof(AppContainerDisplayName));
        if (AppContainerDescription.Length > 2048)
            throw new ArgumentOutOfRangeException(nameof(AppContainerDescription));
        if ((Capabilities & ~AppContainerCapabilities.CodeGeneration) != 0)
            throw new ArgumentOutOfRangeException(nameof(Capabilities));
        if (WallClockDeadline <= TimeSpan.Zero || WallClockDeadline == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(WallClockDeadline));
        if (PerProcessUserTimeLimit <= TimeSpan.Zero
            || PerProcessUserTimeLimit == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(PerProcessUserTimeLimit));
        }
        if (ProcessMemoryLimitBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(ProcessMemoryLimitBytes));
        if (MaxStandardInputBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxStandardInputBytes));
        if (MaxStandardOutputBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxStandardOutputBytes));
        if (MaxStandardErrorBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxStandardErrorBytes));
    }
}

/// <summary>Captured pipe bytes backed by one owned growable array and an explicit valid length.</summary>
internal sealed class IsolatedCodecPipeCapture
{
    public static IsolatedCodecPipeCapture Empty { get; } = new([], 0);

    internal IsolatedCodecPipeCapture(byte[] buffer, int length)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(length));

        Buffer = buffer;
        Length = length;
    }

    internal byte[] Buffer { get; }
    public int Length { get; private set; }
    public bool IsEmpty => Length == 0;
    public ReadOnlyMemory<byte> Content => Buffer.AsMemory(0, Length);

    public MemoryStream OpenReadStream() =>
        new(Buffer, 0, Length, writable: false, publiclyVisible: true);

    /// <summary>Moves a retained slice without allocating another payload-sized array.</summary>
    public byte[] RetainSliceInPlace(int offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset > Length - length)
            throw new ArgumentOutOfRangeException(nameof(length));

        if (length > 0 && offset != 0)
            System.Buffer.BlockCopy(Buffer, offset, Buffer, 0, length);
        Length = length;
        return Buffer;
    }
}

internal sealed record IsolatedCodecProcessResult(
    int ExitCode,
    IsolatedCodecPipeCapture StandardOutput,
    IsolatedCodecPipeCapture StandardError)
{
    internal IsolatedCodecProcessResult(
        int exitCode,
        byte[] standardOutput,
        byte[] standardError)
        : this(
            exitCode,
            new IsolatedCodecPipeCapture(standardOutput, standardOutput.Length),
            new IsolatedCodecPipeCapture(standardError, standardError.Length))
    {
    }
}

internal interface IIsolatedCodecProcessLauncher
{
    Task<IsolatedCodecProcessResult> ExecuteAsync(
        IsolatedCodecProcessRequest request,
        IsolatedCodecProcessPolicy policy,
        CancellationToken cancellationToken);
}
