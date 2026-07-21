using EzyImageViewer.CodecProtocol;

namespace EzyImageViewer.CodecHost;

internal static class CodecHostApplication
{
    private const string ErrorPrefix = "EIV_CODEC_HOST:";

    public static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments.Length != 0)
            return ReportFailure(HostExitCode.InvalidArguments, "arguments-not-allowed");

        try
        {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();

            CodecRequest request;
            try
            {
                request = await CodecWireProtocol.ReadRequestAsync(
                    input,
                    CodecHostPolicy.ProtocolLimits,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                return ReportFailure(HostExitCode.MalformedProtocol, "malformed-request");
            }

            var response = await CodecHostRequestHandler.HandleAsync(
                request,
                input,
                CancellationToken.None).ConfigureAwait(false);
            var trailingBuffer = new byte[1];
            if (await input.ReadAsync(trailingBuffer, CancellationToken.None).ConfigureAwait(false) != 0)
                response = CodecHostRequestHandler.RejectTrailingInput(request);
            using var payload = response.OpenPayloadStream();
            await CodecWireProtocol.WriteResponseAsync(
                output,
                response.Header,
                payload,
                CodecHostPolicy.ProtocolLimits,
                CancellationToken.None).ConfigureAwait(false);
            await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return HostExitCode.Success;
        }
        catch (IOException)
        {
            return ReportFailure(HostExitCode.StandardIoFailure, "standard-io-failure");
        }
        catch (Exception)
        {
            return ReportFailure(HostExitCode.UnexpectedFailure, "unexpected-failure");
        }
    }

    private static int ReportFailure(int exitCode, string token)
    {
        try
        {
            Console.Error.WriteLine($"{ErrorPrefix}{token}");
        }
        catch (IOException)
        {
        }
        return exitCode;
    }
}
