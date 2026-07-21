using EzyImageViewer.Core.Documents.Layers;

namespace EzyImageViewer.Core.Commands;

/// <summary>
/// One undoable document edit (FR-HIST-001). Contract: <see cref="Apply"/> and <see cref="Revert"/>
/// are pure functions of the state passed in — no I/O, no capture of live document instances — so
/// replaying either direction is exact and a failed command leaves state and history untouched.
/// Payload shape is unconstrained; <see cref="EstimatedRetainedBytes"/> is what the history budget
/// enforces (ADR-0008).
/// </summary>
public interface IEditCommand
{
    /// <summary>Stable, non-localized identifier for diagnostics and history inspection.
    /// Display only — never a coalescing identity (that is <see cref="MergeKey"/>).</summary>
    string Name { get; }

    /// <summary>Bytes this command retains while it sits in the undo or redo stack (FR-HIST-002).</summary>
    long EstimatedRetainedBytes { get; }

    /// <summary>
    /// Structured coalescing identity (§7.8): a command replaces the newest history entry only when
    /// both carry equal non-null keys — same kind, same target, same authoring gesture. Null means
    /// this command never coalesces.
    /// </summary>
    object? MergeKey { get; }

    DocumentState Apply(DocumentState state);

    DocumentState Revert(DocumentState state);
}
