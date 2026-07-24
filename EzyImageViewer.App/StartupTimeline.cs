using System.Diagnostics;

namespace EzyImageViewer.App;

/// <summary>
/// 시작 성능 측정용 콜드 스타트 이정표.
/// 짧은 잠금에서 튜플 하나만 기록해 늘 켜 두고, 결과 출력은 벤치마크에서만 함.
/// </summary>
internal static class StartupTimeline
{
    // 일반 실행은 목록을 비우지 않으므로 상한으로 후속 창의 평생 적립을 방지.
    // 시작 과정은 이 절반도 쓰지 않음.
    private const int CapacityLimit = 64;
    private static readonly List<(string Name, long Timestamp)> Marks = new(16);

    internal static void Mark(string name)
    {
        lock (Marks)
        {
            if (Marks.Count < CapacityLimit)
                Marks.Add((name, Stopwatch.GetTimestamp()));
        }
    }

    /// <summary>주어진 프로세스 시작 시각부터 누적 밀리초로 이정표 반환.</summary>
    internal static IReadOnlyList<object> Snapshot(long origin)
    {
        lock (Marks)
        {
            return [.. Marks.Select(mark => new
            {
                name = mark.Name,
                ms = Math.Round(Stopwatch.GetElapsedTime(origin, mark.Timestamp).TotalMilliseconds, 1),
            })];
        }
    }
}
