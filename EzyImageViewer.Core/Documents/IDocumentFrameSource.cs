using EzyImageViewer.Core.Imaging;

namespace EzyImageViewer.Core.Documents;

public enum DocumentSequenceKind
{
    SingleFrame,
    Pages,
    Animation,
    ScalableVector,
}

public readonly record struct DocumentFrameInfo(TimeSpan Duration)
{
    public static DocumentFrameInfo Still { get; } = new(TimeSpan.Zero);
}

/// <summary>페이지·애니메이션 프레임·배율별 벡터 렌더를 늦게 공급하는 원본.</summary>
public interface IDocumentFrameSource : IDisposable
{
    int FrameCount { get; }
    DocumentSequenceKind Kind { get; }
    IReadOnlyList<DocumentFrameInfo> Frames { get; }
    bool IsScaleDependent => false;

    Task<DecodeResult> DecodeFrameAsync(
        int frameIndex,
        DecodeRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// 원본 파일이 같은 내용으로 다른 경로에 놓였을 때(이름 변경) 읽기 대상을 옮긴다.
    /// 길이·수정시각은 그대로라 기존 변조 검증은 계속 유효하다.
    /// 메모리 기반 원본처럼 경로가 없는 구현은 아무것도 하지 않는다.
    /// </summary>
    void RebindSourcePath(string path)
    {
    }
}
