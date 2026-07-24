using EzyImageViewer.Core.Documents.Layers;

namespace EzyImageViewer.Core.Commands;

/// <summary><see cref="TransformCommand"/>가 품은 파이프라인 편집 종류.
/// 표시 이름이 아닌 이 값이 병합 키의 구조 절반(§7.8).</summary>
public enum TransformEditKind
{
    Crop,
    Rotate,
    Flip,
    Resize,
    Erase,
}

/// <summary>
/// 문서 배경 변환 교체(FR-EDIT-001~004). 모든 연산 종류를 한 명령으로 다룸.
/// 양 끝점이 수십 바이트짜리 전체 파이프라인이라 역산이 정확하고 기록 부담도 작음.
/// 적용·복원 때 대상 상태를 검증해 문서나 분기가 엇갈리면 조용히 망치지 않고 바로 실패.
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

    /// <summary>작성 UI 제스처 ID. 0이면 병합하지 않음.</summary>
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
