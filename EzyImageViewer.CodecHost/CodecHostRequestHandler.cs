using System.Text;
using EzyImageViewer.CodecProtocol;
using Microsoft.Win32.SafeHandles;

namespace EzyImageViewer.CodecHost;

internal static class CodecHostRequestHandler
{
    private static readonly byte[] ProbePayload = Encoding.ASCII.GetBytes("ezy-codec-host-b1");

    public static async Task<CodecHostResponse> HandleAsync(
        CodecRequest request,
        Stream input,
        CancellationToken cancellationToken)
    {
        if (request.Operation is CodecOperation.Inspect or CodecOperation.Decode)
            return await HandleDocumentAsync(request, input, cancellationToken).ConfigureAwait(false);

        if (!CodecTransportGuard.IsInline(request))
            return CreateResponse(
                request,
                CodecResultCode.UnsupportedOperation,
                diagnostic: "input-transport-unsupported");

        if (!HasDiagnosticEnvelope(request))
            return CreateResponse(
                request,
                CodecResultCode.InvalidRequest,
                diagnostic: "invalid-diagnostic-envelope");

        if (request.Operation == CodecOperation.Probe)
        {
            return request.InputLength == 0
                ? CreateResponse(request, CodecResultCode.Success, ProbePayload)
                : CreateResponse(
                    request,
                    CodecResultCode.InvalidRequest,
                    diagnostic: "probe-input-not-empty");
        }

#if CODEC_HOST_DIAGNOSTICS
        if (!TryMapDiagnostic(request.Operation, out var diagnosticOperation))
            return CreateResponse(
                request,
                CodecResultCode.UnsupportedOperation,
                diagnostic: "operation-not-implemented");

        if (!TryGetBodyLength(request, diagnosticOperation, out var bodyLength, out var error))
            return CreateResponse(request, error, diagnostic: "diagnostic-input-limit");

        byte[] body;
        try
        {
            using var bodyStream = new MemoryStream(bodyLength);
            await CodecWireProtocol.CopyInlineInputAsync(
                input,
                bodyStream,
                request,
                CodecHostPolicy.ProtocolLimits,
                cancellationToken).ConfigureAwait(false);
            body = bodyStream.ToArray();
        }
        catch (InvalidDataException)
        {
            return CreateResponse(
                request,
                CodecResultCode.InvalidRequest,
                diagnostic: "truncated-input");
        }

        try
        {
            var result = await DiagnosticOperationProcessor.ExecuteAsync(
                diagnosticOperation,
                body).ConfigureAwait(false);
            return CreateResponse(
                request,
                result.Succeeded ? CodecResultCode.Success : CodecResultCode.AccessDenied,
                result.Payload);
        }
        catch (InvalidDataException)
        {
            return CreateResponse(
                request,
                CodecResultCode.InvalidRequest,
                diagnostic: "invalid-diagnostic-input");
        }
        catch (OutOfMemoryException)
        {
            return CreateResponse(
                request,
                CodecResultCode.ResourceLimitExceeded,
                diagnostic: "allocation-denied");
        }
#else
        return CreateResponse(
            request,
            CodecResultCode.UnsupportedOperation,
            diagnostic: "diagnostics-disabled");
#endif
    }

    internal static CodecHostResponse RejectTrailingInput(CodecRequest request) =>
        CreateResponse(
            request,
            CodecResultCode.InvalidRequest,
            diagnostic: "trailing-input");

    private static async Task<CodecHostResponse> HandleDocumentAsync(
        CodecRequest request,
        Stream input,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = request.InputTransport switch
            {
                CodecInputTransport.Inline => await ReadInlineDocumentAsync(
                    request,
                    input,
                    cancellationToken).ConfigureAwait(false),
                CodecInputTransport.InheritedReadHandle => OpenInheritedDocument(request),
                _ => throw new InvalidDataException("Unknown document input transport."),
            };
            cancellationToken.ThrowIfCancellationRequested();
            document.Position = 0;
            return CodecDocumentProcessor.Process(request, document);
        }
        catch (InvalidDataException)
        {
            return CreateResponse(
                request,
                CodecResultCode.InvalidRequest,
                diagnostic: "truncated-input");
        }
        catch (OperationCanceledException)
        {
            return CreateResponse(
                request,
                CodecResultCode.Canceled,
                diagnostic: "request-canceled");
        }
        catch (OutOfMemoryException)
        {
            return CreateResponse(
                request,
                CodecResultCode.ResourceLimitExceeded,
                diagnostic: "input-memory-limit");
        }
        catch (UnauthorizedAccessException)
        {
            return CreateResponse(
                request,
                CodecResultCode.AccessDenied,
                diagnostic: "input-handle-access-denied");
        }
        catch (IOException)
        {
            return CreateResponse(
                request,
                CodecResultCode.InvalidRequest,
                diagnostic: "input-handle-invalid");
        }
    }

    private static async Task<Stream> ReadInlineDocumentAsync(
        CodecRequest request,
        Stream input,
        CancellationToken cancellationToken)
    {
        var document = request.InputLength <= 16 * 1024 * 1024
            ? new MemoryStream(checked((int)request.InputLength))
            : new MemoryStream();
        try
        {
            await CodecWireProtocol.CopyInlineInputAsync(
                input,
                document,
                request,
                CodecHostPolicy.ProtocolLimits,
                cancellationToken).ConfigureAwait(false);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static Stream OpenInheritedDocument(CodecRequest request)
    {
        var handle = new SafeFileHandle(
            unchecked((nint)request.InputHandle),
            ownsHandle: true);
        try
        {
            if (handle.IsInvalid)
                throw new InvalidDataException("Inherited input handle is invalid.");
            var document = new FileStream(
                handle,
                FileAccess.Read,
                bufferSize: 64 * 1024,
                isAsync: false);
            if (!document.CanSeek || document.Length != request.InputLength)
            {
                document.Dispose();
                throw new InvalidDataException("Inherited input length does not match the request.");
            }
            return document;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static bool HasDiagnosticEnvelope(CodecRequest request) =>
        request.Format == CodecFormat.None
        && request.PageIndex == -1
        && request.TargetWidth == 0
        && request.TargetHeight == 0;

#if CODEC_HOST_DIAGNOSTICS
    private static bool TryMapDiagnostic(
        CodecOperation operation,
        out DiagnosticOperation diagnosticOperation)
    {
        diagnosticOperation = operation switch
        {
            CodecOperation.DiagnosticEcho => DiagnosticOperation.Echo,
            CodecOperation.DiagnosticSleep => DiagnosticOperation.Sleep,
            CodecOperation.DiagnosticAllocate => DiagnosticOperation.Allocate,
            CodecOperation.DiagnosticTryNetwork => DiagnosticOperation.TryNetwork,
            CodecOperation.DiagnosticTryWriteOutsideTemp => DiagnosticOperation.TryWriteOutsideTemp,
            _ => 0,
        };
        return diagnosticOperation != 0;
    }

    private static bool TryGetBodyLength(
        CodecRequest request,
        DiagnosticOperation operation,
        out int bodyLength,
        out CodecResultCode error)
    {
        var expectedLength = operation switch
        {
            DiagnosticOperation.Echo => request.InputLength,
            DiagnosticOperation.Sleep => sizeof(int),
            DiagnosticOperation.Allocate => sizeof(long),
            DiagnosticOperation.TryNetwork => sizeof(int),
            DiagnosticOperation.TryWriteOutsideTemp => 0,
            _ => -1,
        };

        if (operation == DiagnosticOperation.Echo
            && request.InputLength > DiagnosticOperationProcessor.MaxEchoBytes)
        {
            bodyLength = 0;
            error = CodecResultCode.ResourceLimitExceeded;
            return false;
        }
        if (expectedLength < 0 || request.InputLength != expectedLength)
        {
            bodyLength = 0;
            error = CodecResultCode.InvalidRequest;
            return false;
        }

        bodyLength = checked((int)expectedLength);
        error = CodecResultCode.Success;
        return true;
    }
#endif

    private static CodecHostResponse CreateResponse(
        CodecRequest request,
        CodecResultCode result,
        ReadOnlyMemory<byte> payload = default,
        string? diagnostic = null)
    {
        var payloadBytes = payload.ToArray();
        var header = new CodecResponse(
            request.RequestId,
            request.Nonce,
            request.Operation,
            request.Format,
            result,
            Width: 0,
            Height: 0,
            Stride: 0,
            NativeWidth: 0,
            NativeHeight: 0,
            PageCount: 0,
            PayloadLength: payloadBytes.Length,
            diagnostic);
        return new CodecHostResponse(header, payloadBytes);
    }
}
