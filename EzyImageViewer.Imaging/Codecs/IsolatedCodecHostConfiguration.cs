namespace EzyImageViewer.Imaging.Codecs;

/// <summary>Installed framework-package identity used by the out-of-process document codecs.</summary>
public sealed record IsolatedCodecHostConfiguration
{
    public const string PackageName = "GRTech.ezyImageViewer.CodecHost";
    public const string HostExecutableFileName = "EzyImageViewer.CodecHost.exe";

    public IsolatedCodecHostConfiguration(
        string packageFamilyName,
        string hostExecutablePath,
        string packageVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFamilyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageVersion);
        if (packageFamilyName.Length > 64)
            throw new ArgumentOutOfRangeException(nameof(packageFamilyName));
        if (!Path.IsPathFullyQualified(hostExecutablePath))
            throw new ArgumentException("The codec host executable path must be absolute.", nameof(hostExecutablePath));
        if (!string.Equals(
                Path.GetFileName(hostExecutablePath),
                HostExecutableFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The codec host executable name is invalid.", nameof(hostExecutablePath));
        }
        if (!File.Exists(hostExecutablePath))
            throw new FileNotFoundException("The installed codec host executable was not found.", hostExecutablePath);

        PackageFamilyName = packageFamilyName;
        HostExecutablePath = Path.GetFullPath(hostExecutablePath);
        PackageVersion = packageVersion;
    }

    public string PackageFamilyName { get; }
    public string HostExecutablePath { get; }
    public string PackageVersion { get; }
    internal string WorkingDirectory => Path.GetDirectoryName(HostExecutablePath)
        ?? throw new InvalidOperationException("The codec host path has no parent directory.");
}
