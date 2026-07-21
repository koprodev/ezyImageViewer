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

    /// <summary>Initial-process fallback: our own command line is authoritative for unpackaged launches.</summary>
    public static ActivationRequest FromCommandLine(string[] args, bool initial = false)
    {
        var paths = ParseFilePaths(args);
        return paths.Count > 0
            ? new FileActivation(paths, IsInitial: initial)
            : new LaunchActivation();
    }

    /// <summary>
    /// Unpackaged launch arguments can include argv[0] (the exe path itself), so candidates are
    /// filtered by the openable-format catalog, not just existence.
    /// </summary>
    private static List<string> ParseFilePaths(IEnumerable<string> rawArgs) =>
        rawArgs
            .Where(a => !a.StartsWith('-')
                && EzyImageViewer.Core.Imaging.ImageFormatCatalog.IsViewable(a)
                && File.Exists(a))
            .Select(Path.GetFullPath)
            .ToList();

    private static IEnumerable<string> SplitCommandLine(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            yield break;
        // Minimal quote-aware split; full CommandLineToArgv fidelity is not needed for file paths.
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
