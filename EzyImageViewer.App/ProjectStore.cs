using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Documents.Serialization;

namespace EzyImageViewer.App;

/// <summary>Everything a project open yields; the embedded source stays around for re-save.</summary>
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
/// .ezyimg composition for the app (FR-OUT-009): the container carries the manifest, the v2
/// document (with the active-layer hint), a preview and the embedded background source (§7.10
/// embedded-source; external source links are a later option).
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
