using EzyImageViewer.Core.Activation;

namespace EzyImageViewer.App;

/// <summary>
/// 활성화 라우터만 보관.
/// Program.Main의 최초 요청이 무거운 <see cref="AppServices"/> 초기화를 깨우지 않게 함.
/// 서비스 초기화는 작업자 스레드에서 XAML 런타임 준비와 겹쳐 실행.
/// </summary>
internal static class ActivationChannel
{
    internal static ActivationRouter Router { get; } = new();
}
