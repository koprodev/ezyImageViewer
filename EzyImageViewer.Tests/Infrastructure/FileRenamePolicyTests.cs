using EzyImageViewer.Infrastructure;
using Xunit;

namespace EzyImageViewer.Tests.Infrastructure;

public sealed class FileRenamePolicyTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("ezy-rename").FullName;

    private string CreateFile(string name)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void Validate_AcceptsAPlainRename()
    {
        var path = CreateFile("before.png");
        Assert.Equal(RenameValidation.Valid, FileRenamePolicy.Validate(path, "after.png"));
    }

    [Fact]
    public void Validate_ReportsUnchangedForTheSameName()
    {
        var path = CreateFile("same.png");
        Assert.Equal(RenameValidation.Unchanged, FileRenamePolicy.Validate(path, "same.png"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_RejectsBlankNames(string? candidate)
    {
        var path = CreateFile("blank.png");
        Assert.Equal(RenameValidation.Empty, FileRenamePolicy.Validate(path, candidate));
    }

    /// <summary>경로 구분자가 통과하면 이름 변경이 조용히 다른 폴더로의 이동이 된다.</summary>
    [Theory]
    [InlineData("sub/other.png")]
    [InlineData(@"sub\other.png")]
    [InlineData("what?.png")]
    [InlineData("a<b>.png")]
    [InlineData("pipe|name.png")]
    [InlineData(".")]
    [InlineData("..")]
    public void Validate_RejectsNamesThatWouldEscapeTheFolder(string candidate)
    {
        var path = CreateFile("guard.png");
        Assert.Equal(RenameValidation.InvalidCharacters, FileRenamePolicy.Validate(path, candidate));
    }

    /// <summary>Windows가 끝점을 조용히 떼어 내 사용자가 입력한 이름과 달라진다.</summary>
    [Theory]
    [InlineData("trailing.")]
    [InlineData("trailing . ")]
    public void Validate_RejectsATrailingPeriod(string candidate)
    {
        var path = CreateFile("trail.png");
        Assert.Equal(RenameValidation.InvalidCharacters, FileRenamePolicy.Validate(path, candidate));
    }

    /// <summary>붙여넣기로 딸려 온 앞뒤 공백은 거부하지 않고 다듬는다. 저장 경로도 같은 이름을 쓴다.</summary>
    [Fact]
    public void Validate_TrimsSurroundingWhitespaceInsteadOfRejecting()
    {
        var path = CreateFile("pad.png");

        Assert.Equal(RenameValidation.Valid, FileRenamePolicy.Validate(path, "  padded.png  "));
        Assert.Equal(
            Path.Combine(_folder, "padded.png"),
            FileRenamePolicy.ResolveTargetPath(path, "  padded.png  "));
    }

    [Theory]
    [InlineData("NUL.png")]
    [InlineData("con.jpg")]
    [InlineData("COM1")]
    public void Validate_RejectsReservedDeviceNames(string candidate)
    {
        var path = CreateFile("reserved.png");
        Assert.Equal(RenameValidation.ReservedName, FileRenamePolicy.Validate(path, candidate));
    }

    [Fact]
    public void Validate_RejectsNamesOverTheComponentLimit()
    {
        var path = CreateFile("long.png");
        var candidate = new string('a', FileRenamePolicy.MaximumNameLength + 1) + ".png";
        Assert.Equal(RenameValidation.TooLong, FileRenamePolicy.Validate(path, candidate));
    }

    [Fact]
    public void Validate_RejectsAnExistingTarget()
    {
        var path = CreateFile("source.png");
        CreateFile("taken.png");
        Assert.Equal(RenameValidation.TargetExists, FileRenamePolicy.Validate(path, "taken.png"));
    }

    /// <summary>대소문자만 바꾸는 것은 같은 파일이라 충돌이 아니다.</summary>
    [Fact]
    public void Validate_AllowsACaseOnlyRename()
    {
        var path = CreateFile("Case.png");
        Assert.Equal(RenameValidation.Valid, FileRenamePolicy.Validate(path, "CASE.png"));
    }

    [Fact]
    public void ResolveTargetPath_KeepsTheFolderAndTrimsTheName()
    {
        var path = CreateFile("resolve.png");
        var target = FileRenamePolicy.ResolveTargetPath(path, "  renamed.png  ");

        Assert.Equal(Path.Combine(_folder, "renamed.png"), target);
        Assert.Equal(Path.GetDirectoryName(path), Path.GetDirectoryName(target));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 테스트 결과와 무관.
        }
    }
}
