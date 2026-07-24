using EzyImageViewer.Core.Activation;

namespace EzyImageViewer.Infrastructure;

/// <summary>저장된 단일 인스턴스 설정을 웜 파일 활성화에 적용.
/// 두 번째 프로세스는 만들지 않으며 최초·명시 대상 활성화는 요청한 창 유지.</summary>
public static class ActivationRoutingPolicy
{
    public static ActivationRequest Apply(
        ActivationRequest request,
        AppSettings settings,
        bool safeMode = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);
        if (safeMode && request is FileActivation { IsInitial: true }
            or ProtocolActivation { IsInitial: true })
        {
            return new LaunchActivation
            {
                Timestamp = request.Timestamp,
                CorrelationId = request.CorrelationId,
            };
        }
        return request is FileActivation
            {
                IsInitial: false,
                Target: OpenTarget.ExistingWindow,
            } file
            && settings.SingleInstanceBehavior == SingleInstanceBehavior.OpenNewWindow
                ? file with { Target = OpenTarget.NewWindow }
                : request;
    }
}
