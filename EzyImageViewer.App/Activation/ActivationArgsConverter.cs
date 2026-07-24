using EzyImageViewer.Core.Activation;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace EzyImageViewer.App.Activation;

public static class ActivationArgsConverter
{
    public static ActivationRequest Convert(AppActivationArguments args, bool initial = false)
    {
        switch (args.Kind)
        {
            case ExtendedActivationKind.File when args.Data is IFileActivatedEventArgs file:
            {
                var paths = file.Files
                    .Select(item => item.Path)
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToArray();
                if (paths.Length > 0)
                    return new FileActivation(paths, IsInitial: initial);
                break;
            }
            case ExtendedActivationKind.Protocol when args.Data is IProtocolActivatedEventArgs protocol:
                return new ProtocolActivation(protocol.Uri, initial);

            case ExtendedActivationKind.Launch when args.Data is ILaunchActivatedEventArgs launch:
            {
                var paths = ParseFilePaths(SplitCommandLine(launch.Arguments));
                if (paths.Count > 0)
                    return new FileActivation(paths, IsInitial: initial);
                break;
            }
        }
        return new LaunchActivation();
    }

    /// <summary>최초 프로세스 대체 경로. 비패키지 실행은 자체 명령줄을 정답으로 사용.</summary>
    public static ActivationRequest FromCommandLine(string[] args, bool initial = false)
    {
        var paths = ParseFilePaths(args);
        return paths.Count > 0
            ? new FileActivation(paths, IsInitial: initial)
            : new LaunchActivation();
    }

    /// <summary>
    /// 비패키지 실행 인수에는 argv[0](실행 파일 경로)도 들어올 수 있음.
    /// 파일 존재 여부만 보지 않고 열 수 있는 형식 목록으로 후보를 거름.
    /// </summary>
    private static List<string> ParseFilePaths(IEnumerable<string> rawArgs) =>
        rawArgs
            .Where(a => !a.StartsWith('-')
                && EzyImageViewer.Core.Imaging.ImageFormatCatalog
                    .ViewableExtensions.Contains(Path.GetExtension(a))
                && File.Exists(a))
            .Select(Path.GetFullPath)
            .ToList();

    private static IEnumerable<string> SplitCommandLine(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            yield break;
        // 따옴표만 챙기는 최소 분리. 파일 경로에 CommandLineToArgv 완전 복제까지는 과함.
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var ch in arguments)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (ch == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0)
            yield return current.ToString();
    }
}
