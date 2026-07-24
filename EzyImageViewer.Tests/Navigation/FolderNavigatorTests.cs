using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Core.Navigation;
using Xunit;

namespace EzyImageViewer.Tests.Navigation;

public sealed class FolderNavigatorTests : IDisposable
{
    private readonly string _folder =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ezy-nav-{Guid.NewGuid():N}")).FullName;

    private string Touch(string name)
    {
        var path = Path.Combine(_folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1]);
        return path;
    }

    [Fact]
    public void AnchorTo_ScansSupportedFilesInNaturalOrder()
    {
        Touch("b10.png");
        var anchor = Touch("b2.png");
        Touch("a.jpg");
        Touch("readme.txt");

        var navigator = new FolderNavigator(ImageFormatCatalog.RasterExtensions);
        navigator.AnchorTo(anchor);

        Assert.Equal(3, navigator.Count);
        Assert.Equal(1, navigator.CurrentIndex); // 정렬 순서: a.jpg, b2.png, b10.png.
        Assert.True(navigator.CanMovePrevious);
        Assert.True(navigator.CanMoveNext);
        Assert.Equal("b10.png", Path.GetFileName(navigator.MoveNext()));
        Assert.False(navigator.CanMoveNext);
        Assert.Null(navigator.MoveNext());
        Assert.Equal("b2.png", Path.GetFileName(navigator.MovePrevious()));
        Assert.Equal("a.jpg", Path.GetFileName(navigator.MovePrevious()));
        Assert.Null(navigator.MovePrevious());
    }

    [Fact]
    public void Files_ExposeScanOrderAndMoveToJumpsToAnyEntry()
    {
        Touch("b10.png");
        Touch("b2.png");
        var anchor = Touch("a.jpg");

        var navigator = new FolderNavigator(ImageFormatCatalog.RasterExtensions);
        navigator.AnchorTo(anchor);

        Assert.Equal(
            ["a.jpg", "b2.png", "b10.png"],
            navigator.Files.Select(Path.GetFileName));
        Assert.Equal(0, navigator.CurrentIndex);

        Assert.Equal("b10.png", Path.GetFileName(navigator.MoveTo(2)));
        Assert.Equal(2, navigator.CurrentIndex);
        Assert.Equal("a.jpg", Path.GetFileName(navigator.MoveTo(0)));
        Assert.Equal(0, navigator.CurrentIndex);
    }

    [Fact]
    public void MoveTo_RejectsOutOfRangeWithoutMovingTheAnchor()
    {
        var anchor = Touch("a.png");
        Touch("b.png");

        var navigator = new FolderNavigator(ImageFormatCatalog.RasterExtensions);
        navigator.AnchorTo(anchor);

        Assert.Null(navigator.MoveTo(-1));
        Assert.Null(navigator.MoveTo(2));
        Assert.Equal(0, navigator.CurrentIndex);
        Assert.Equal(anchor, navigator.CurrentPath);
    }

    [Fact]
    public void MoveTo_ResolvesByPathAfterAnEarlierFileVanished()
    {
        var a = Touch("a.png");
        var b = Touch("b.png");
        var c = Touch("c.png");

        var navigator = new FolderNavigator(ImageFormatCatalog.RasterExtensions);
        navigator.AnchorTo(a);
        // 필름 스트립은 삭제 전 스캔으로 만들어져 인덱스 2가 더는 c.png를 가리키지 않음.
        File.Delete(b);
        File.Delete(c);

        Assert.Null(navigator.MoveTo(2));
        Assert.Equal(1, navigator.Count);
        Assert.Equal(a, navigator.CurrentPath);
    }

    [Fact]
    public void MoveNext_SkipsDeletedFileViaRescan()
    {
        var a = Touch("a.png");
        var b = Touch("b.png");
        Touch("c.png");

        var navigator = new FolderNavigator(ImageFormatCatalog.RasterExtensions);
        navigator.AnchorTo(a);
        File.Delete(b);

        Assert.Equal("c.png", Path.GetFileName(navigator.MoveNext()));
        Assert.Equal(2, navigator.Count);
    }

    [Fact]
    public void MoveNext_CurrentAndCandidateDeletedContinuesFromVanishedNaturalPosition()
    {
        var current = Touch("a.png");
        var candidate = Touch("b.png");
        Touch("c.png");

        var navigator = new FolderNavigator(ImageFormatCatalog.RasterExtensions);
        navigator.AnchorTo(current);
        File.Delete(current);
        File.Delete(candidate);

        Assert.Equal("c.png", Path.GetFileName(navigator.MoveNext()));
        Assert.Single(Directory.EnumerateFiles(_folder));
    }

    [Fact]
    public void SupportedExtensions_AreInjected()
    {
        var anchor = Touch("a.png");
        Touch("b.webp");

        var pngOnly = new FolderNavigator(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png" });
        pngOnly.AnchorTo(anchor);

        Assert.Equal(1, pngOnly.Count);
        Assert.Null(pngOnly.MoveNext());
    }

    [Fact]
    public void IncludeSubfolders_DefaultsOffAndExcludesDescendants()
    {
        var anchor = Touch("a.png");
        Touch(Path.Combine("nested", "b.png"));

        var navigator = new FolderNavigator(ImageFormatCatalog.RasterExtensions);
        navigator.AnchorTo(anchor);

        Assert.False(navigator.IncludeSubfolders);
        Assert.Equal(1, navigator.Count);
        Assert.Equal(anchor, navigator.CurrentPath);
    }

    [Fact]
    public void SetIncludeSubfolders_EnumeratesInNaturalRelativePathOrder()
    {
        var anchor = Touch("a.png");
        Touch(Path.Combine("set10", "image1.png"));
        Touch(Path.Combine("set2", "image10.png"));
        Touch(Path.Combine("set2", "image2.png"));
        Touch(Path.Combine("set2", "readme.txt"));

        var navigator = new FolderNavigator(ImageFormatCatalog.RasterExtensions);
        navigator.AnchorTo(anchor);
        navigator.SetIncludeSubfolders(true);

        var relativePaths = new List<string> { Path.GetRelativePath(_folder, navigator.CurrentPath!) };
        while (navigator.MoveNext() is { } path)
            relativePaths.Add(Path.GetRelativePath(_folder, path));

        Assert.True(navigator.IncludeSubfolders);
        Assert.Equal(
            [
                "a.png",
                Path.Combine("set2", "image2.png"),
                Path.Combine("set2", "image10.png"),
                Path.Combine("set10", "image1.png"),
            ],
            relativePaths);
    }

    [Fact]
    public void SetIncludeSubfolders_RescansAndKeepsCurrentPathWhenPossible()
    {
        Touch("a.png");
        var anchor = Touch("z.png");
        Touch(Path.Combine("set2", "c.png"));

        var navigator = new FolderNavigator(ImageFormatCatalog.RasterExtensions);
        navigator.AnchorTo(anchor);

        navigator.SetIncludeSubfolders(true);

        Assert.Equal(3, navigator.Count);
        Assert.Equal(anchor, navigator.CurrentPath);
        Assert.Equal(Path.Combine("set2", "c.png"),
            Path.GetRelativePath(_folder, navigator.MovePrevious()!));

        navigator.SetIncludeSubfolders(false);

        Assert.Equal(2, navigator.Count);
        Assert.Equal("a.png", Path.GetFileName(navigator.CurrentPath));
    }

    [Fact]
    public void SafetyLimits_BoundEnumerationWhilePreservingTheOpenedAnchor()
    {
        Touch("a.png");
        Touch("b.png");
        Touch("c.png");
        var anchor = Touch("z.png");
        var navigator = new FolderNavigator(
            ImageFormatCatalog.RasterExtensions,
            new FolderNavigatorOptions
            {
                MaximumFiles = 2,
                MaximumEntriesScanned = 3,
                MaximumDirectories = 1,
                MaximumDepth = 1,
            });

        navigator.AnchorTo(anchor);

        Assert.InRange(navigator.Count, 1, 3);
        Assert.Equal(anchor, navigator.CurrentPath);
    }

    [SymbolicLinkFact]
    public void SetIncludeSubfolders_SkipsReparsePointFilesAndDirectories()
    {
        var anchor = Touch("a.png");
        Directory.CreateSymbolicLink(Path.Combine(_folder, "cycle"), _folder);
        File.CreateSymbolicLink(Path.Combine(_folder, "linked.png"), anchor);

        var navigator = new FolderNavigator(ImageFormatCatalog.RasterExtensions);
        navigator.AnchorTo(anchor);
        navigator.SetIncludeSubfolders(true);

        Assert.Equal(1, navigator.Count);
        Assert.Equal(anchor, navigator.CurrentPath);
    }

    public sealed class SymbolicLinkFactAttribute : FactAttribute
    {
        public SymbolicLinkFactAttribute()
        {
            if (!CanCreateSymbolicLinks())
                Skip = "Symbolic links are unavailable; reparse-point traversal was not exercised.";
        }

        private static bool CanCreateSymbolicLinks()
        {
            var root = Path.Combine(Path.GetTempPath(), $"ezy-nav-link-probe-{Guid.NewGuid():N}");
            var targetDirectory = Path.Combine(root, "target");
            var targetFile = Path.Combine(root, "target.png");
            var directoryLink = Path.Combine(root, "directory-link");
            var fileLink = Path.Combine(root, "file-link.png");
            try
            {
                Directory.CreateDirectory(targetDirectory);
                File.WriteAllBytes(targetFile, [1]);
                Directory.CreateSymbolicLink(directoryLink, targetDirectory);
                File.CreateSymbolicLink(fileLink, targetFile);
                return true;
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or PlatformNotSupportedException)
            {
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(fileLink))
                        File.Delete(fileLink);
                    if (Directory.Exists(directoryLink))
                        Directory.Delete(directoryLink);
                    if (Directory.Exists(root))
                        Directory.Delete(root, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
