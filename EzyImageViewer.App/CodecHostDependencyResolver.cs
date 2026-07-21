using System.Runtime.InteropServices;
using EzyImageViewer.Capture.Snipping;
using EzyImageViewer.Imaging.Codecs;
using Windows.ApplicationModel;

namespace EzyImageViewer.App;

internal static class CodecHostDependencyResolver
{
    public static IsolatedCodecHostConfiguration? TryResolve()
    {
        if (!PackageIdentity.HasIdentity)
            return null;

        try
        {
            var dependency = Package.Current.Dependencies.FirstOrDefault(package =>
                string.Equals(
                    package.Id.Name,
                    IsolatedCodecHostConfiguration.PackageName,
                    StringComparison.Ordinal));
            if (dependency is null)
                return null;

            var version = dependency.Id.Version;
            return new IsolatedCodecHostConfiguration(
                dependency.Id.FamilyName,
                Path.Combine(
                    dependency.InstalledLocation.Path,
                    IsolatedCodecHostConfiguration.HostExecutableFileName),
                $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}");
        }
        catch (Exception ex) when (ex is COMException
            or InvalidOperationException
            or UnauthorizedAccessException
            or IOException
            or ArgumentException)
        {
            return null;
        }
    }
}
