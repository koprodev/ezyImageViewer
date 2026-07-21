namespace EzyImageViewer.Core.Documents.Layers;

/// <summary>
/// An ordered container of annotation objects (UR-007): visibility, lock and paint order apply to
/// the whole group, Photoshop-style. Immutable like every document value; index 0 inside
/// <see cref="Annotations"/> is farthest back within the layer.
/// </summary>
public sealed record AnnotationLayer
{
    /// <summary>Deterministic id of the initial layer, shared by Empty documents and v1 migration
    /// so a pristine state stays a value and migrated fixtures are reproducible.</summary>
    public static readonly Guid InitialLayerId = new("1b48d9e6-6f3b-4e7b-9c5a-000000000001");

    public required Guid Id { get; init; }

    /// <summary>Empty means "unnamed": the UI supplies a localized positional fallback.</summary>
    public string Name { get; init; } = "";

    public bool IsVisible { get; init; } = true;

    public bool IsLocked { get; init; }

    public IReadOnlyList<Annotation> Annotations { get; init; } = [];

    public long EstimatedRetainedBytes
    {
        get
        {
            var total = 64L + ((long)Name.Length * sizeof(char));
            foreach (var annotation in Annotations)
                total = checked(total + annotation.EstimatedRetainedBytes);
            return total;
        }
    }

    public int IndexOf(Guid annotationId)
    {
        for (var i = 0; i < Annotations.Count; i++)
        {
            if (Annotations[i].Id == annotationId)
                return i;
        }
        return -1;
    }
}
