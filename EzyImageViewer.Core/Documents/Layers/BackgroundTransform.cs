namespace EzyImageViewer.Core.Documents.Layers;

/// <summary>
/// The document's non-destructive background edit: an ordered, immutable op pipeline. Source pixels
/// are never touched (SSOT source-protection principle); compositing applies the derived matrix at
/// paint/export time. Value equality is by op sequence, which is what lets a command verify it is
/// applied to the state it was built against.
/// </summary>
public sealed record BackgroundTransform
{
    public static BackgroundTransform Identity { get; } = new();

    /// <summary>Pipeline order = user edit order. Op k is defined in the output space of ops 0..k-1.
    /// Privately constructed: equality caches and reference-keyed consumers rely on true immutability,
    /// so no caller may inject (and later mutate) its own list.</summary>
    public IReadOnlyList<TransformOp> Ops { get; private init; } = [];

    public bool IsIdentity => Ops.Count == 0;

    public BackgroundTransform Append(TransformOp op)
    {
        ArgumentNullException.ThrowIfNull(op);
        return new BackgroundTransform { Ops = [.. Ops, op] };
    }

    /// <summary>History accounting (FR-HIST-002): list overhead plus the fixed per-op payload.</summary>
    public long EstimatedRetainedBytes => 24 + (Ops.Count * TransformOp.EstimatedRetainedBytes);

    public bool Equals(BackgroundTransform? other) =>
        other is not null && (ReferenceEquals(this, other) || Ops.SequenceEqual(other.Ops));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Ops.Count);
        foreach (var op in Ops)
            hash.Add(op);
        return hash.ToHashCode();
    }
}
