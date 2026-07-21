using System.Runtime.InteropServices;

namespace EzyImageViewer.Capture.Snipping;

/// <summary>Process package identity probe: the official capture path only functions when the
/// process runs from an MSIX package (the OS routes the redirect callback by identity).</summary>
public static class PackageIdentity
{
    private const int AppModelErrorNoPackage = 15700;

    public static bool HasIdentity { get; } = Probe();

    private static bool Probe()
    {
        var length = 0u;
        return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);
}
