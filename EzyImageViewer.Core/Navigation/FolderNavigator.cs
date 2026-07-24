namespace EzyImageViewer.Core.Navigation;

public sealed record FolderNavigatorOptions
{
    public int MaximumFiles { get; init; } = 20_000;
    public int MaximumEntriesScanned { get; init; } = 100_000;
    public int MaximumDirectories { get; init; } = 4_096;
    public int MaximumDepth { get; init; } = 64;
}

/// <summary>
/// 폴더의 지원 파일을 자연 정렬로 이전·다음 탐색.
/// 선택적 재귀는 상대 경로 정렬과 재분석 지점 차단, 누락 파일은 한 번 재스캔.
/// 창 하나가 소유하며 스레드 안전하지 않음.
/// </summary>
public sealed class FolderNavigator
{
    private readonly IReadOnlySet<string> _extensions;
    private readonly FolderNavigatorOptions _options;
    private List<string> _files = [];
    private string? _folder;
    private int _index = -1;
    private bool _includeSubfolders;

    public FolderNavigator(
        IReadOnlySet<string> supportedExtensions,
        FolderNavigatorOptions? options = null)
    {
        _extensions = supportedExtensions ?? throw new ArgumentNullException(nameof(supportedExtensions));
        _options = options ?? new FolderNavigatorOptions();
        if (_options.MaximumFiles is < 1 or > 1_000_000
            || _options.MaximumEntriesScanned < _options.MaximumFiles
            || _options.MaximumEntriesScanned > 10_000_000
            || _options.MaximumDirectories is < 1 or > 100_000
            || _options.MaximumDepth is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    public int Count => _files.Count;
    public int CurrentIndex => _index;

    /// <summary>임의 항목 이동용 스캔 순서. 재스캔은 새 목록을 게시해 기존 스냅샷 유지.</summary>
    public IReadOnlyList<string> Files => _files;

    public bool IncludeSubfolders => _includeSubfolders;
    public bool CanMovePrevious => _index > 0;
    public bool CanMoveNext => _index >= 0 && _index < _files.Count - 1;
    public string? CurrentPath => _index >= 0 && _index < _files.Count ? _files[_index] : null;

    public void SetIncludeSubfolders(bool includeSubfolders)
    {
        if (_includeSubfolders == includeSubfolders)
            return;

        var preferredPath = CurrentPath;
        _includeSubfolders = includeSubfolders;
        Rescan(preferredPath);
    }

    /// <summary>열린 파일을 기준점으로 두고 해당 폴더 스캔.</summary>
    public void AnchorTo(string filePath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!StringComparer.OrdinalIgnoreCase.Equals(_folder, folder))
        {
            // 새 폴더를 못 읽어도 이전 폴더 항목은 절대 남기지 않음.
            _files = [];
            _index = -1;
        }
        _folder = folder;
        Rescan(preferredPath: filePath);
    }

    public string? MoveNext() => Move(+1);
    public string? MovePrevious() => Move(-1);

    /// <summary>스캔 항목으로 즉시 이동. 사라졌으면 한 번 재스캔 후 경로로 다시 찾음.</summary>
    public string? MoveTo(int index)
    {
        if (_folder is null || index < 0 || index >= _files.Count)
            return null;

        var target = _files[index];
        if (File.Exists(target))
        {
            _index = index;
            return target;
        }

        Rescan(preferredPath: CurrentPath);
        var moved = _files.FindIndex(path => string.Equals(
            path, target, StringComparison.OrdinalIgnoreCase));
        if (moved < 0 || !File.Exists(_files[moved]))
            return null;
        _index = moved;
        return _files[moved];
    }

    private string? Move(int direction)
    {
        if (_folder is null || _files.Count == 0)
            return null;

        var next = _index + direction;
        while (next >= 0 && next < _files.Count)
        {
            if (File.Exists(_files[next]))
            {
                _index = next;
                return _files[next];
            }
            // 파일이 사라졌으면 현재 기준점에서 한 번 재스캔·재시도.
            var previousPath = CurrentPath;
            Rescan(preferredPath: previousPath);
            if (previousPath is null)
                return null;
            var exactIndex = _files.FindIndex(path => string.Equals(
                path,
                previousPath,
                StringComparison.OrdinalIgnoreCase));
            next = exactIndex >= 0
                ? exactIndex + direction
                : FindInsertionMoveIndex(previousPath, direction);
            if (next < 0 || next >= _files.Count || !File.Exists(_files[next]))
                return null;
            _index = next;
            return _files[next];
        }
        return null;
    }

    private void Rescan(string? preferredPath)
    {
        if (_folder is null || !Directory.Exists(_folder))
        {
            _files = [];
            _index = -1;
            return;
        }

        try
        {
            _files = _includeSubfolders
                ? EnumerateFilesRecursively(_folder)
                    .Select(path => (Path: path, RelativePath: Path.GetRelativePath(_folder, path)))
                    .OrderBy(item => item.RelativePath, NaturalStringComparer.Instance)
                    .ThenBy(item => item.RelativePath, StringComparer.Ordinal)
                    .Select(item => item.Path)
                    .ToList()
                : Directory.EnumerateFiles(_folder)
                    .Take(_options.MaximumEntriesScanned)
                    .Where(f => _extensions.Contains(Path.GetExtension(f)))
                    .Take(_options.MaximumFiles)
                    .OrderBy(Path.GetFileName, NaturalStringComparer.Instance)
                    .ThenBy(Path.GetFileName, StringComparer.Ordinal)
                    .ToList();
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            // 같은 폴더 새로 고침은 마지막 목록 유지. 다른 폴더 상태는 AnchorTo가 정리.
            return;
        }

        if (preferredPath is not null)
        {
            var preferredFullPath = Path.GetFullPath(preferredPath);
            if (File.Exists(preferredFullPath)
                && _extensions.Contains(Path.GetExtension(preferredFullPath))
                && (_includeSubfolders || StringComparer.OrdinalIgnoreCase.Equals(
                    Path.GetDirectoryName(preferredFullPath), _folder))
                && !_files.Contains(preferredFullPath, StringComparer.OrdinalIgnoreCase))
            {
                _files.Add(preferredFullPath);
                _files = _includeSubfolders
                    ? _files.OrderBy(
                            path => Path.GetRelativePath(_folder, path),
                            NaturalStringComparer.Instance)
                        .ThenBy(path => path, StringComparer.Ordinal)
                        .ToList()
                    : _files.OrderBy(Path.GetFileName, NaturalStringComparer.Instance).ToList();
            }
        }

        _index = preferredPath is null
            ? (_files.Count > 0 ? 0 : -1)
            : _files.FindIndex(f => string.Equals(f, Path.GetFullPath(preferredPath), StringComparison.OrdinalIgnoreCase));
        if (_index < 0 && _files.Count > 0)
            _index = 0;
    }

    private int FindInsertionMoveIndex(string previousPath, int direction)
    {
        if (direction > 0)
            return _files.FindIndex(path => CompareNavigationPaths(path, previousPath) > 0);

        return _files.FindLastIndex(path => CompareNavigationPaths(path, previousPath) < 0);
    }

    private int CompareNavigationPaths(string left, string right)
    {
        var leftKey = _includeSubfolders
            ? Path.GetRelativePath(_folder!, left)
            : Path.GetFileName(left);
        var rightKey = _includeSubfolders
            ? Path.GetRelativePath(_folder!, right)
            : Path.GetFileName(right);
        var natural = NaturalStringComparer.Instance.Compare(leftKey, rightKey);
        return natural != 0
            ? natural
            : StringComparer.Ordinal.Compare(leftKey, rightKey);
    }

    private IEnumerable<string> EnumerateFilesRecursively(string root)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var scannedEntries = 0;
        var visitedDirectories = 0;
        var returnedFiles = 0;

        while (pending.TryPop(out var pendingFolder)
            && scannedEntries < _options.MaximumEntriesScanned
            && visitedDirectories < _options.MaximumDirectories
            && returnedFiles < _options.MaximumFiles)
        {
            visitedDirectories++;
            IEnumerator<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(pendingFolder.Path).GetEnumerator();
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or System.Security.SecurityException)
            {
                continue;
            }

            using (entries)
            {
                while (scannedEntries < _options.MaximumEntriesScanned
                    && returnedFiles < _options.MaximumFiles)
                {
                    string entry;
                    try
                    {
                        if (!entries.MoveNext())
                            break;
                        entry = entries.Current;
                    }
                    catch (Exception ex) when (ex is IOException
                                               or UnauthorizedAccessException
                                               or System.Security.SecurityException)
                    {
                        break;
                    }
                    scannedEntries++;

                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(entry);
                    }
                    catch (Exception ex) when (ex is IOException
                                               or UnauthorizedAccessException
                                               or System.Security.SecurityException)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        continue;

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (pendingFolder.Depth < _options.MaximumDepth
                            && visitedDirectories + pending.Count < _options.MaximumDirectories)
                            pending.Push((entry, pendingFolder.Depth + 1));
                    }
                    else if (_extensions.Contains(Path.GetExtension(entry)))
                    {
                        returnedFiles++;
                        yield return entry;
                    }
                }
            }
        }
    }
}
