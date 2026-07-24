using EzyImageViewer.Core.Documents.Layers;

namespace EzyImageViewer.Core.Commands;

/// <summary>
/// 실행 취소 가능한 문서 편집 하나. 적용·복원은 입력 상태의 순수 함수이며 I/O·실문서 참조 금지.
/// 실패한 명령은 상태·기록을 건드리지 않고 보유 바이트가 기록 예산 기준.
/// </summary>
public interface IEditCommand
{
    /// <summary>진단·기록 검사용 비지역화 식별자. 병합 식별자는 아님.</summary>
    string Name { get; }

    /// <summary>명령이 실행 취소·다시 실행 스택에서 보유하는 바이트.</summary>
    long EstimatedRetainedBytes { get; }

    /// <summary>구조화 병합 키. 같은 종류·대상·제스처의 null 아닌 키끼리만 병합.</summary>
    object? MergeKey { get; }

    DocumentState Apply(DocumentState state);

    DocumentState Revert(DocumentState state);
}
