namespace EzyImageViewer.Core.Documents.Layers;

/// <summary>
/// 주석 개체의 순서 있는 묶음(UR-007).
/// 포토샵처럼 표시·잠금·그리기 순서가 그룹 전체에 적용. 다른 문서 값처럼 불변.
/// <see cref="Annotations"/>의 인덱스 0이 레이어에서 가장 뒤.
/// </summary>
public sealed record AnnotationLayer
{
    /// <summary>최초 레이어의 결정적 ID. 빈 문서와 v1 마이그레이션이 공유해 초기 상태와 픽스처를 재현.</summary>
    public static readonly Guid InitialLayerId = new("1b48d9e6-6f3b-4e7b-9c5a-000000000001");

    public required Guid Id { get; init; }

    /// <summary>빈 문자열은 이름 없음. UI가 현지화된 위치 이름으로 대신 표시.</summary>
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
