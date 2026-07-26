namespace EzyImageViewer.Infrastructure;

public enum RenameValidation
{
    Valid,
    Unchanged,
    Empty,
    InvalidCharacters,
    ReservedName,
    TooLong,
    TargetExists,
}

/// <summary>
/// 파일 이름 바꾸기 규칙. 셸에 넘기기 전에 여기서 걸러야 사용자가 이유를 즉시 안다.
/// Windows가 조용히 거부하거나 엉뚱한 경로를 만드는 입력을 전부 막는 게 목적.
/// </summary>
public static class FileRenamePolicy
{
    /// <summary>NTFS 파일 이름 성분 한도.</summary>
    public const int MaximumNameLength = 255;

    // 확장자를 붙여도 예약어로 남는 장치 이름. NUL.png 같은 이름은 만들어지지 않는다.
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// 새 이름을 검사한다. <paramref name="currentPath"/>는 전체 경로,
    /// <paramref name="candidate"/>는 확장자를 포함한 파일 이름이다.
    /// </summary>
    public static RenameValidation Validate(string currentPath, string? candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPath);
        if (string.IsNullOrWhiteSpace(candidate))
            return RenameValidation.Empty;

        var name = candidate.Trim();
        if (name.Length > MaximumNameLength)
            return RenameValidation.TooLong;
        // 경로 구분자·와일드카드가 섞이면 이름 변경이 아니라 이동이 된다. 이름만 받는다.
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return RenameValidation.InvalidCharacters;
        if (name is "." or "..")
            return RenameValidation.InvalidCharacters;
        // 앞뒤 공백은 위에서 다듬었다. 남은 끝점은 Windows가 조용히 떼어 내 의도와 다른 이름이 된다.
        if (name.EndsWith('.'))
            return RenameValidation.InvalidCharacters;

        var stem = Path.GetFileNameWithoutExtension(name);
        if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
            return RenameValidation.ReservedName;

        var currentName = Path.GetFileName(currentPath);
        if (string.Equals(currentName, name, StringComparison.Ordinal))
            return RenameValidation.Unchanged;

        var directory = Path.GetDirectoryName(Path.GetFullPath(currentPath));
        if (string.IsNullOrEmpty(directory))
            return RenameValidation.InvalidCharacters;

        var target = Path.Combine(directory, name);
        // 대소문자만 바꾸는 이름 변경은 같은 파일을 가리키므로 충돌이 아니다.
        var caseOnly = string.Equals(currentName, name, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
            return RenameValidation.TargetExists;

        return RenameValidation.Valid;
    }

    /// <summary>검사를 통과한 이름의 최종 경로. 앞뒤 공백은 제거한 이름을 쓴다.</summary>
    public static string ResolveTargetPath(string currentPath, string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        var directory = Path.GetDirectoryName(Path.GetFullPath(currentPath))
            ?? throw new InvalidOperationException("The current path has no directory.");
        return Path.Combine(directory, candidate.Trim());
    }
}
