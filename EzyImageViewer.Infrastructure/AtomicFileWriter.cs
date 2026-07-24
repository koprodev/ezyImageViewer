using System.Security.AccessControl;

namespace EzyImageViewer.Infrastructure;

/// <summary>완료 파일에 적용할 접근 제어.</summary>
public enum AtomicFileProtection
{
    /// <summary>일반 Windows 파일처럼 폴더 ACL 상속.</summary>
    Inherited,

    /// <summary>앱 데이터 트리가 요구하는 현재 사용자 + SYSTEM 명시 ACL 적용.</summary>
    CurrentUserAndSystem,
}

/// <summary>같은 폴더 임시 파일에 쓰고 flush 성공 뒤 이름 변경해 찢어진 대상 방지.</summary>
public static class AtomicFileWriter
{
    private const string TempSuffix = ".tmp";
    private const int TempTokenLength = 32;

    /// <summary>원자 쓰기가 만든 형제 임시 파일 식별. 디렉터리 순회자가 실제 내용과 구분.</summary>
    public static bool IsTempFileName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (fileName.Length == 0
            || fileName[0] != '.'
            || !fileName.EndsWith(TempSuffix, StringComparison.Ordinal))
            return false;
        var body = fileName.AsSpan(0, fileName.Length - TempSuffix.Length);
        var separator = body.LastIndexOf('.');
        if (separator < 1)
            return false;
        var token = body[(separator + 1)..];
        return token.Length == TempTokenLength && Guid.TryParseExact(token, "N", out _);
    }

    public static void Write(
        string path,
        byte[] bytes,
        AtomicFileProtection protection = AtomicFileProtection.Inherited)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);

        WriteCore(path, stream => stream.Write(bytes), protection);
    }

    public static void Write(
        string path,
        Action<Stream> writeContent,
        AtomicFileProtection protection = AtomicFileProtection.Inherited)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(writeContent);

        WriteCore(path, writeContent, protection);
    }

    private static void WriteCore(
        string path, Action<Stream> writeContent, AtomicFileProtection protection)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
            throw new IOException($"Cannot resolve the directory of '{path}'.");
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(
            directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}{TempSuffix}");
        try
        {
            using (var stream = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                writeContent(stream);
                stream.Flush(flushToDisk: true);
            }
            // 이름 변경은 임시 파일 ACL도 옮기므로 명시 보호 ACL을 미리 적용.
            ApplyProtection(temp, protection);
            File.Move(temp, fullPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 남은 임시 파일은 부차적. 원래 오류가 우선.
            }
            throw;
        }
    }

    private static void ApplyProtection(string temp, AtomicFileProtection protection)
    {
        if (protection != AtomicFileProtection.CurrentUserAndSystem || !OperatingSystem.IsWindows())
            return;
        new FileInfo(temp).SetAccessControl(
            AppDataSecurity.CreateProtectedFileSecurityForCurrentUser());
    }
}
