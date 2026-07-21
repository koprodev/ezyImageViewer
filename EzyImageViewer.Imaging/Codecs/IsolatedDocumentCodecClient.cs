using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using EzyImageViewer.CodecProtocol;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Imaging.Codecs.Isolation;

namespace EzyImageViewer.Imaging.Codecs;

internal sealed record IsolatedCodecDecodedImage(
    byte[] Pixels,
    int PixelLength,
    int Width,
    int Height,
    int Stride,
    PixelSize NativeSize,
    int PageCount,
    string? Diagnostic = null);

internal interface IIsolatedDocumentCodecClient
{
    string RendererVersion { get; }

    Task<IsolatedCodecDecodedImage> DecodeAsync(
        Stream input,
        CodecFormat format,
        int pageIndex,
        int targetMaxDimension,
        CancellationToken cancellationToken);
}

internal interface ICodecProfilePathResolver
{
    AppContainerProfileInfo GetProfileInfo(IsolatedCodecProcessPolicy policy);
}

internal sealed class CodecProfilePathResolver : ICodecProfilePathResolver
{
    public AppContainerProfileInfo GetProfileInfo(IsolatedCodecProcessPolicy policy) =>
        AppContainerProfileAccess.GetProfileInfo(policy);
}

internal sealed class IsolatedDocumentCodecClient : IIsolatedDocumentCodecClient
{
    private const int MaxInlineInputBytes = 64 * 1024 * 1024;
    private const int MaxDiagnosticBytes = CodecBoundaryLimits.MaxDiagnosticBytes;
    private const int MaxStandardErrorBytes = 4 * 1024;
    private const uint ForcedTerminationExitCode = 0xE000_0002;

    private static readonly CodecProtocolLimits ProtocolLimits = new(
        InputLimits.Default.MaxFileBytes,
        CodecBoundaryLimits.MaxPayloadBytes,
        MaxDiagnosticBytes,
        InputLimits.Default.MaxDimension,
        InputLimits.Default.MaxFrameCount,
        InputLimits.Default.HardMaxPixels);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PackageRequestGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IsolatedCodecHostConfiguration _configuration;
    private readonly IIsolatedCodecProcessLauncher _launcher;
    private readonly ICodecPackageDataResetter _dataResetter;
    private readonly ICodecProfilePathResolver _profilePathResolver;
    private readonly SemaphoreSlim _requestGate;
    private readonly IsolatedCodecProcessPolicy _processPolicy;

    public IsolatedDocumentCodecClient(IsolatedCodecHostConfiguration configuration)
        : this(
            configuration,
            new ClassicAppContainerProcessLauncher(TimeProvider.System),
            new ApplicationDataCodecPackageDataResetter(),
            new CodecProfilePathResolver())
    {
    }

    internal IsolatedDocumentCodecClient(
        IsolatedCodecHostConfiguration configuration,
        IIsolatedCodecProcessLauncher launcher,
        ICodecPackageDataResetter dataResetter,
        ICodecProfilePathResolver profilePathResolver)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _dataResetter = dataResetter ?? throw new ArgumentNullException(nameof(dataResetter));
        _profilePathResolver = profilePathResolver
            ?? throw new ArgumentNullException(nameof(profilePathResolver));
        _requestGate = PackageRequestGates.GetOrAdd(
            configuration.PackageFamilyName,
            static _ => new SemaphoreSlim(1, 1));
        _processPolicy = new IsolatedCodecProcessPolicy(
            AppContainerName: configuration.PackageFamilyName,
            ProfileSource: AppContainerProfileSource.ExistingPackage,
            AppContainerDisplayName: "ezy Image Viewer Codec Host",
            AppContainerDescription: "One-shot isolated PDF and PSD decoder.",
            Capabilities: AppContainerCapabilities.CodeGeneration,
            WallClockDeadline: TimeSpan.FromSeconds(35),
            PerProcessUserTimeLimit: TimeSpan.FromSeconds(30),
            ProcessMemoryLimitBytes: 1024L * 1024 * 1024,
            MaxStandardInputBytes: CodecWireProtocol.RequestHeaderSize + MaxInlineInputBytes,
            MaxStandardOutputBytes: CodecBoundaryLimits.MaxStandardOutputBytes,
            MaxStandardErrorBytes,
            ForcedTerminationExitCode);
        _processPolicy.Validate();
    }

    public string RendererVersion => _configuration.PackageVersion;

    public async Task<IsolatedCodecDecodedImage> DecodeAsync(
        Stream input,
        CodecFormat format,
        int pageIndex,
        int targetMaxDimension,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (format is not (CodecFormat.Pdf or CodecFormat.Psd))
            throw new ArgumentOutOfRangeException(nameof(format));
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        if (targetMaxDimension < 0 || targetMaxDimension > ProtocolLimits.MaxDimension)
            throw new ArgumentOutOfRangeException(nameof(targetMaxDimension));
        if (!input.CanRead || !input.CanSeek)
            throw new CorruptImageException("The isolated codec input must be a readable, seekable stream.");
        if (input.Length <= 0 || input.Length > ProtocolLimits.MaxInputBytes)
            throw new SecurityLimitExceededException("The isolated codec input exceeds its byte limit.");

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var profile = _profilePathResolver.GetProfileInfo(_processPolicy);
                await _dataResetter.ClearAsync(
                        _configuration.PackageFamilyName,
                        profile,
                        cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return await ExecuteCoreAsync(
                            input,
                            format,
                            pageIndex,
                            targetMaxDimension,
                            profile,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    await _dataResetter.ClearAsync(
                            _configuration.PackageFamilyName,
                            profile,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ImageRejectedException)
            {
                throw;
            }
            catch (TimeoutException ex)
            {
                throw new SecurityLimitExceededException(
                    "The isolated document decoder exceeded its time limit.", ex);
            }
            catch (OutOfMemoryException ex)
            {
                throw new SecurityLimitExceededException(
                    "The isolated document decoder exceeded its memory limit.", ex);
            }
            catch (Exception ex)
            {
                throw new CodecUnavailableException(
                    "The isolated document decoder could not complete safely.", ex);
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<IsolatedCodecDecodedImage> ExecuteCoreAsync(
        Stream input,
        CodecFormat format,
        int pageIndex,
        int targetMaxDimension,
        AppContainerProfileInfo profile,
        CancellationToken cancellationToken)
    {
        var request = new CodecRequest(
            Guid.NewGuid(),
            CreateNonce(),
            CodecOperation.Decode,
            format,
            input is FileStream
                ? CodecInputTransport.InheritedReadHandle
                : CodecInputTransport.Inline,
            input.Length,
            InputHandle: 0,
            pageIndex,
            TargetWidth: targetMaxDimension,
            TargetHeight: targetMaxDimension);

        SafeFileHandle? synchronousSource = null;
        try
        {
            IsolatedCodecProcessRequest processRequest;
            if (request.InputTransport == CodecInputTransport.InheritedReadHandle)
            {
                synchronousSource = OpenSynchronousReadHandle((FileStream)input, request.InputLength);
                processRequest = CreateInheritedProcessRequest(
                    synchronousSource,
                    request,
                    profile);
            }
            else
            {
                processRequest = await CreateInlineProcessRequestAsync(
                        input,
                        request,
                        profile,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var result = await _launcher.ExecuteAsync(
                    processRequest,
                    _processPolicy,
                    cancellationToken)
                .ConfigureAwait(false);
            return await ReadResponseAsync(request, result, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            synchronousSource?.Dispose();
        }
    }

    private IsolatedCodecProcessRequest CreateInheritedProcessRequest(
        SafeFileHandle inputHandle,
        CodecRequest request,
        AppContainerProfileInfo profile) => new(
        _configuration.HostExecutablePath,
        _configuration.WorkingDirectory,
        Arguments: [],
        Environment: CreateMinimalEnvironment(profile),
        StandardInput: ReadOnlyMemory<byte>.Empty,
        InheritedSource: new InheritedReadOnlySource(
            inputHandle,
            (childHandle, cancellationToken) => CreateInheritedControlMessageAsync(
                request,
                childHandle,
                cancellationToken)));

    private static SafeFileHandle OpenSynchronousReadHandle(
        FileStream input,
        long expectedLength)
    {
        if (!Path.IsPathFullyQualified(input.Name))
            throw new IOException("The file-backed codec source does not expose an absolute path.");
        var handle = File.OpenHandle(
            input.Name,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.SequentialScan);
        try
        {
            if (RandomAccess.GetLength(handle) != expectedLength)
                throw new IOException("The codec source length changed before handle inheritance.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private async Task<IsolatedCodecProcessRequest> CreateInlineProcessRequestAsync(
        Stream input,
        CodecRequest request,
        AppContainerProfileInfo profile,
        CancellationToken cancellationToken)
    {
        if (request.InputLength > MaxInlineInputBytes)
        {
            throw new SecurityLimitExceededException(
                $"In-memory document input exceeds the {MaxInlineInputBytes:N0}-byte isolated transfer limit.");
        }

        var originalPosition = input.Position;
        try
        {
            input.Position = 0;
            await using var wire = new MemoryStream(
                checked(CodecWireProtocol.RequestHeaderSize + (int)request.InputLength));
            await CodecWireProtocol.WriteRequestAsync(
                    wire,
                    request,
                    input,
                    ProtocolLimits,
                    cancellationToken)
                .ConfigureAwait(false);
            return new IsolatedCodecProcessRequest(
                _configuration.HostExecutablePath,
                _configuration.WorkingDirectory,
                Arguments: [],
                Environment: CreateMinimalEnvironment(profile),
                StandardInput: wire.ToArray());
        }
        finally
        {
            input.Position = originalPosition;
        }
    }

    private static async ValueTask<ReadOnlyMemory<byte>> CreateInheritedControlMessageAsync(
        CodecRequest request,
        nint childHandle,
        CancellationToken cancellationToken)
    {
        if (childHandle == nint.Zero)
            throw new InvalidDataException("The inherited codec input handle is empty.");
        var inheritedRequest = request with
        {
            InputHandle = unchecked((ulong)(nuint)childHandle),
        };
        await using var wire = new MemoryStream(CodecWireProtocol.RequestHeaderSize);
        await CodecWireProtocol.WriteRequestAsync(
                wire,
                inheritedRequest,
                inlineInput: null,
                ProtocolLimits,
                cancellationToken)
            .ConfigureAwait(false);
        return wire.ToArray();
    }

    private static async Task<IsolatedCodecDecodedImage> ReadResponseAsync(
        CodecRequest request,
        IsolatedCodecProcessResult processResult,
        CancellationToken cancellationToken)
    {
        if (processResult.ExitCode != 0)
        {
            throw new CodecUnavailableException(
                $"The isolated document decoder exited with code 0x{processResult.ExitCode:X8}.");
        }
        if (processResult.StandardError.Length != 0)
            throw new CodecUnavailableException("The isolated document decoder emitted unexpected error output.");

        var output = processResult.StandardOutput;
        await using var wire = output.OpenReadStream();
        var response = await CodecWireProtocol.ReadResponseAsync(
                wire,
                ProtocolLimits,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.RequestId != request.RequestId
            || response.Nonce != request.Nonce
            || response.Operation != request.Operation
            || response.Format != request.Format)
        {
            throw new InvalidDataException("The isolated codec response correlation is invalid.");
        }

        var payloadOffset = wire.Position;
        if (response.PayloadLength != wire.Length - payloadOffset)
            throw new InvalidDataException("The isolated codec response contains truncated or trailing bytes.");
        ThrowForFailure(response, cancellationToken);
        if (response.PayloadLength > int.MaxValue)
            throw new InvalidDataException("The isolated codec payload cannot be represented in memory.");

        var payloadLength = checked((int)response.PayloadLength);
        // Retain the growable stdout backing array; an exact copy would transiently duplicate
        // the payload allocation at the 192 MiB boundary.
        var pixels = output.RetainSliceInPlace(checked((int)payloadOffset), payloadLength);
        return new IsolatedCodecDecodedImage(
            pixels,
            payloadLength,
            response.Width,
            response.Height,
            response.Stride,
            new PixelSize(response.NativeWidth, response.NativeHeight),
            response.PageCount,
            response.Diagnostic);
    }

    private static void ThrowForFailure(
        CodecResponse response,
        CancellationToken cancellationToken)
    {
        if (response.Result == CodecResultCode.Success)
            return;
        throw response.Result switch
        {
            CodecResultCode.CorruptInput or CodecResultCode.InvalidRequest =>
                new CorruptImageException("The document codec rejected the input."),
            CodecResultCode.PasswordRequired =>
                new ProtectedDocumentException("The document requires credentials."),
            CodecResultCode.UnsupportedOperation or CodecResultCode.UnsupportedFormat =>
                new UnsupportedFormatException("The document codec does not support this input."),
            CodecResultCode.ResourceLimitExceeded or CodecResultCode.DeadlineExceeded =>
                new SecurityLimitExceededException("The document exceeded codec resource limits."),
            CodecResultCode.AccessDenied =>
                new ProtectedDocumentException("The document codec could not read the input."),
            CodecResultCode.Canceled when cancellationToken.IsCancellationRequested =>
                new OperationCanceledException(cancellationToken),
            CodecResultCode.Canceled or CodecResultCode.CodecUnavailable or CodecResultCode.InternalError =>
                new CodecUnavailableException("The document codec is unavailable."),
            _ => new CodecUnavailableException("The document codec failed closed."),
        };
    }

    private IReadOnlyDictionary<string, string> CreateMinimalEnvironment(
        AppContainerProfileInfo profile)
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")
            ?? throw new InvalidOperationException("SystemRoot is unavailable.");
        var systemDirectory = Path.Combine(systemRoot, "System32");
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = systemRoot,
            ["WINDIR"] = systemRoot,
            ["PATH"] = string.Join(Path.PathSeparator, _configuration.WorkingDirectory, systemDirectory),
            ["LOCALAPPDATA"] = profile.LocalAppDataPath,
            ["APPDATA"] = profile.LocalAppDataPath,
            ["USERPROFILE"] = profile.LocalAppDataPath,
            ["TEMP"] = profile.TempPath,
            ["TMP"] = profile.TempPath,
            ["OS"] = "Windows_NT",
            ["PROCESSOR_ARCHITECTURE"] = "AMD64",
            ["DOTNET_EnableDiagnostics"] = "0",
            ["COMPlus_EnableDiagnostics"] = "0",
            ["CORECLR_ENABLE_PROFILING"] = "0",
            ["COR_ENABLE_PROFILING"] = "0",
            ["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1",
        };
        AddEnvironmentIfAvailable(environment, "ProgramData");
        AddEnvironmentIfAvailable(environment, "ProgramFiles");
        AddEnvironmentIfAvailable(environment, "CommonProgramFiles");
        AddEnvironmentIfAvailable(environment, "NUMBER_OF_PROCESSORS");
        return environment;
    }

    private static void AddEnvironmentIfAvailable(
        IDictionary<string, string> environment,
        string name)
    {
        if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            environment[name] = value;
    }

    private static ulong CreateNonce()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        ulong nonce;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            nonce = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        }
        while (nonce == 0);
        return nonce;
    }
}
