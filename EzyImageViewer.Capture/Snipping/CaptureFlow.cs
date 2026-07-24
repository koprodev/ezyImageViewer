namespace EzyImageViewer.Capture.Snipping;

public enum CaptureDecision
{
    /// <summary>내부 재게시 또는 감시 꺼짐: 아무 일 없음.</summary>
    Ignore,
    /// <summary>사용자가 버튼·단축키로 요청: 결과 즉시 열기.</summary>
    AutoOpen,
    /// <summary>감시 중 요청 없는 캡처: 열기 제안.</summary>
    Notify,
}

/// <summary>캡처 수신 순수 정책. 요청 뒤 일정 시간은 다음 외부 이미지를 자동 열고 그 밖엔 알림만.</summary>
public sealed class CaptureFlow
{
    /// <summary>사용자가 영역을 그릴 시간을 넉넉히 주고 버린 오버레이는 만료.</summary>
    public static readonly TimeSpan ArmWindow = TimeSpan.FromSeconds(60);

    private DateTimeOffset _armedUntil = DateTimeOffset.MinValue;

    /// <summary>클립보드 감시 토글. 기본 켜짐이며 알림은 비침습.</summary>
    public bool WatchEnabled { get; set; } = true;

    public bool IsArmed(DateTimeOffset now) => now <= _armedUntil;

    public void Arm(DateTimeOffset now) => _armedUntil = now + ArmWindow;

    public void Disarm() => _armedUntil = DateTimeOffset.MinValue;

    /// <summary>소비 없이 만료된 대기만 true. 이미 소비한 창을 감시기가 다시 깨우지 못하게 함.</summary>
    public bool ArmExpiredUnconsumed(DateTimeOffset now) =>
        _armedUntil != DateTimeOffset.MinValue && now > _armedUntil;

    public CaptureDecision OnClipboardImage(bool isInternalEcho, DateTimeOffset now)
    {
        if (isInternalEcho)
            return CaptureDecision.Ignore; // 우리 복사는 캡처 아님.
        if (IsArmed(now))
        {
            Disarm(); // 요청당 캡처 하나. 다음 이미지는 다시 요청 없음.
            return CaptureDecision.AutoOpen;
        }
        return WatchEnabled ? CaptureDecision.Notify : CaptureDecision.Ignore;
    }
}
