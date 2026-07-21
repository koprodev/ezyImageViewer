using EzyImageViewer.Core.Activation;

namespace EzyImageViewer.Infrastructure;

/// <summary>Maps persisted single-instance behavior onto warm file activations. It never creates
/// a second process; initial and explicitly targeted activations retain their requested window.</summary>
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
