using System.Runtime.InteropServices;

namespace EzyImageViewer.Capture.Snipping;

/// <summary>프로세스 패키지 ID 확인.
/// 공식 캡처는 MSIX 실행일 때만 동작하며 OS가 ID로 리디렉션 콜백을 전달.</summary>
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
