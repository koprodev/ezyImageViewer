namespace EzyImageViewer.Core.Documents.Layers;

/// <summary>
/// 문서의 비파괴 배경 편집을 담는 순서 있는 불변 연산 파이프라인.
/// 원본 픽셀은 건드리지 않고 합성·내보내기 때 계산된 행렬을 적용.
/// 값 동등성은 연산 순서 기준. 명령이 생성 당시 상태에 적용되는지 검증할 수 있음.
/// </summary>
public sealed record BackgroundTransform
{
    public static BackgroundTransform Identity { get; } = new();

    /// <summary>파이프라인 순서 = 사용자 편집 순서. k번째 연산은 앞선 연산의 출력 공간 기준.
    /// 동등성 캐시와 참조 키 소비자가 진짜 불변성을 기대하므로 외부 목록 주입은 금지.</summary>
    public IReadOnlyList<TransformOp> Ops { get; private init; } = [];

    public bool IsIdentity => Ops.Count == 0;

    public BackgroundTransform Append(TransformOp op)
    {
        ArgumentNullException.ThrowIfNull(op);
        return new BackgroundTransform { Ops = [.. Ops, op] };
    }

    /// <summary>기록 용량 계산(FR-HIST-002): 목록 오버헤드 + 연산별 고정 데이터.</summary>
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
