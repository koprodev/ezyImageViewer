using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Documents.Serialization;

namespace EzyImageViewer.App;

/// <summary>프로젝트 열기 결과 묶음. 포함된 원본은 다시 저장할 때까지 유지.</summary>
public sealed record ProjectOpenData(
    ProjectDocumentState Document,
    string? Path,
    string SourceName,
    byte[] SourceBytes)
{
    public DocumentState State => Document.Pages[Document.ActivePageIndex].State;
    public Guid? ActiveLayerId => Document.Pages[Document.ActivePageIndex].ActiveLayerId;
}

/// <summary>
/// 앱용 .ezyimg 구성(FR-OUT-009).
/// 컨테이너에 매니페스트·활성 레이어 힌트가 든 v2 문서·미리보기·포함 배경 원본을 담음.
/// 외부 원본 링크는 나중 문제(§7.10).
/// </summary>
public static class ProjectStore
{
    public const string Extension = ".ezyimg";

    public static bool IsProjectPath(string path) =>
        string.Equals(System.IO.Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);

    public static ProjectOpenData Read(string path)
    {
        using var stream = File.OpenRead(path);
        return ReadCore(stream, path);
    }

    public static ProjectOpenData Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var stream = new MemoryStream(bytes, writable: false);
        return ReadCore(stream, path: null);
    }

    private static ProjectOpenData ReadCore(Stream stream, string? path)
    {
        var project = EzyProjectArchive.Read(stream);
        var document = ProjectDocumentSerializer.Read(
            project.DocumentJson, project.Manifest.SchemaVersion);
        if (project.SourceBytes is not { Length: > 0 } source
            || string.IsNullOrEmpty(project.SourceName))
            throw new InvalidDataException("Project has no embedded background source.");
        return new ProjectOpenData(document, path, project.SourceName, source);
    }

    public static byte[] Build(
        DocumentState state,
        Guid? activeLayerId,
        string sourceName,
        byte[] sourceBytes,
        byte[]? previewPng)
    {
        return Build(
            [new ProjectPageState(state, activeLayerId)],
            activePageIndex: 0,
            sourceName,
            sourceBytes,
            previewPng);
    }

    public static byte[] Build(
        IReadOnlyList<ProjectPageState> pages,
        int activePageIndex,
        string sourceName,
        byte[] sourceBytes,
        byte[]? previewPng)
    {
        var version = typeof(ProjectStore).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        using var buffer = new MemoryStream();
        EzyProjectArchive.Write(buffer, new EzyProject
        {
            Manifest = ProjectManifest.Create(version),
            DocumentJson = ProjectDocumentSerializer.Write(pages, activePageIndex),
            PreviewPng = previewPng,
            SourceName = sourceName,
            SourceBytes = sourceBytes,
        });
        return buffer.ToArray();
    }
}
