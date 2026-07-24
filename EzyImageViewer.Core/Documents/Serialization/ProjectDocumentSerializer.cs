using System.Text.Json;
using System.Text.Json.Serialization;
using EzyImageViewer.Core.Documents.Layers;

namespace EzyImageViewer.Core.Documents.Serialization;

public sealed record ProjectPageState(DocumentState State, Guid? ActiveLayerId);

public sealed record ProjectDocumentState(
    IReadOnlyList<ProjectPageState> Pages,
    int ActivePageIndex);

/// <summary>v1/v2 단일 페이지 마이그레이션을 지원하는 v3 페이지 봉투.</summary>
public static class ProjectDocumentSerializer
{
    public const int MaxPages = 10_000;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Write(
        IReadOnlyList<ProjectPageState> pages,
        int activePageIndex)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count is < 1 or > MaxPages)
            throw new InvalidDataException($"Project must contain 1..{MaxPages:N0} page states.");
        ArgumentOutOfRangeException.ThrowIfNegative(activePageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(activePageIndex, pages.Count);

        var dtoPages = new PageDto[pages.Count];
        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index] ?? throw new InvalidDataException("Project contains a null page state.");
            using var fragment = JsonDocument.Parse(
                DocumentStateSerializer.Write(page.State, page.ActiveLayerId));
            dtoPages[index] = new PageDto { Document = fragment.RootElement.Clone() };
        }
        var json = JsonSerializer.Serialize(new ProjectDto
        {
            ActivePageIndex = activePageIndex,
            Pages = dtoPages,
        }, Options);
        if (json.Length > DocumentStateSerializer.MaxJsonChars)
            throw new InvalidDataException("Project document envelope exceeds the JSON size limit.");
        return json;
    }

    public static ProjectDocumentState Read(string json, int schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (schemaVersion is < 1 or > ProjectManifest.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported document schema version {schemaVersion}.");
        if (schemaVersion <= 2)
        {
            var state = DocumentStateSerializer.Read(json, schemaVersion, out var activeLayerId);
            return new ProjectDocumentState([new ProjectPageState(state, activeLayerId)], 0);
        }
        if (json.Length > DocumentStateSerializer.MaxJsonChars)
            throw new InvalidDataException("Project document envelope exceeds the JSON size limit.");

        ProjectDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ProjectDto>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Project document envelope is malformed.", ex);
        }
        if (dto?.Pages is not { Count: >= 1 and <= MaxPages } pages)
            throw new InvalidDataException($"Project must contain 1..{MaxPages:N0} page states.");
        if (dto.ActivePageIndex < 0 || dto.ActivePageIndex >= pages.Count)
            throw new InvalidDataException("Project active page index is outside the page list.");

        var result = new ProjectPageState[pages.Count];
        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index];
            if (page?.Document.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Project contains a missing or invalid page document.");
            var state = DocumentStateSerializer.Read(
                page.Document.GetRawText(), 2, out var activeLayerId);
            result[index] = new ProjectPageState(state, activeLayerId);
        }
        return new ProjectDocumentState(result, dto.ActivePageIndex);
    }

    private sealed class ProjectDto
    {
        public int ActivePageIndex { get; init; }
        public required IReadOnlyList<PageDto?> Pages { get; init; }
    }

    private sealed class PageDto
    {
        public JsonElement Document { get; init; }
    }
}
