namespace EzyImageViewer.Core.Navigation;

public sealed record FolderNavigatorOptions
{
    public int MaximumFiles { get; init; } = 20_000;
    public int MaximumEntriesScanned { get; init; } = 100_000;
    public int MaximumDirectories { get; init; } = 4_096;
    public int MaximumDepth { get; init; } = 64;
}

/// <summary>
/// Prev/next traversal over one folder's supported files in natural order (FR-NAV-001/002),
/// with an opt-in recursive mode that orders relative paths and rejects reparse points (FR-NAV-004).
/// Deleted/renamed files are handled by rescanning on a miss (FR-NAV-003 basic tier;
/// live FileSystemWatcher tracking is deferred). Not thread-safe; owned by one window.
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

    /// <summary>Anchors navigation on an opened file; scans its folder.</summary>
    public void AnchorTo(string filePath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!StringComparer.OrdinalIgnoreCase.Equals(_folder, folder))
        {
            // Never retain entries from a different folder if the new scan becomes unreadable.
            _files = [];
            _index = -1;
        }
        _folder = folder;
        Rescan(preferredPath: filePath);
    }

    public string? MoveNext() => Move(+1);
    public string? MovePrevious() => Move(-1);

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
            // A file vanished: rescan and retry once from the current anchor.
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
            // A same-folder refresh keeps its last known list; AnchorTo cleared cross-folder state.
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
