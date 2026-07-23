using EzyImageViewer.Core.Documents.Layers;

namespace EzyImageViewer.Core.Commands;

/// <summary>Which pipeline edit a <see cref="TransformCommand"/> carries. This — not the display
/// name — is the structured half of the merge key (§7.8 coalescing).</summary>
public enum TransformEditKind
{
    Crop,
    Rotate,
    Flip,
    Resize,
    Erase,
}

/// <summary>
/// Replaces the document's background transform (FR-EDIT-001~004). One command type for every op
/// kind: both endpoints are whole pipelines a few dozen bytes each, so inversion is exact and the
/// history budget stays trivial. Apply/Revert verify the state they run against — a transform
/// swapped underneath (wrong document, wrong branch) fails loudly instead of corrupting.
/// </summary>
public sealed class TransformCommand : IEditCommand
{
    public TransformCommand(TransformEditKind kind, BackgroundTransform before, BackgroundTransform after, long gestureId = 0)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        Kind = kind;
        Before = before;
        After = after;
        GestureId = gestureId;
    }

    public TransformEditKind Kind { get; }

    public BackgroundTransform Before { get; }

    public BackgroundTransform After { get; }

    /// <summary>Identity of the authoring UI gesture; zero means "never coalesce".</summary>
    public long GestureId { get; }

    public string Name => $"Transform.{Kind}";

    public long EstimatedRetainedBytes => Before.EstimatedRetainedBytes + After.EstimatedRetainedBytes;

    public object? MergeKey => GestureId == 0 ? null : new TransformMergeKey(Kind, GestureId);

    public DocumentState Apply(DocumentState state) => Retarget(state, Before, After);

    public DocumentState Revert(DocumentState state) => Retarget(state, After, Before);

    private static DocumentState Retarget(DocumentState state, BackgroundTransform expected, BackgroundTransform next)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!expected.Equals(state.Transform))
            throw new InvalidOperationException("Transform command does not match the state it runs against.");
        return state.WithTransform(next);
    }

    private readonly record struct TransformMergeKey(TransformEditKind Kind, long GestureId);
}
