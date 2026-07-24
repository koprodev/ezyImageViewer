using EzyImageViewer.Infrastructure;
using Xunit;

namespace EzyImageViewer.Tests.Infrastructure;

public sealed class UserChoiceHashTests
{
    // 마지막 두 벡터는 2026-07-23 DanysysTeam/PS-SFTA Get-Hash(MIT)로 생성.
    // UserChoiceHash의 Mozilla 방식과 독립 구현이라 둘의 일치로 비공개 알고리즘을 고정.
    // 앞의 둘은 이 구현의 회귀 잠금. Windows가 실제 UserChoice 키 5개에 쓴 해시를 바이트 단위로 재현.
    // 여기의 SID는 모두 합성값.
    [Theory]
    [InlineData(".png", "s-1-5-21-2088888888-3155555555-4011111111-1001",
        "ezyImageViewer.Image", "2026-07-23T06:00:00Z", "a6PQEyAtUs8=")]
    [InlineData(".jpg", "s-1-5-21-2088888888-3155555555-4011111111-1001",
        "ezyImageViewer.Image", "2026-07-23T14:59:00Z", "8LTooILLZLw=")]
    [InlineData(".webp", "s-1-5-21-1234567890-1234567890-1234567890-1001",
        "ezyImageViewer.Image", "2025-12-31T23:59:00Z", "uTCFH4ZB4T8=")]
    [InlineData(".tiff", "s-1-5-21-1234567890-1234567890-1234567890-500",
        "AppX43hnxtbyyps62jhe9sqpdzxn1790zetc", "2026-01-01T00:00:00Z", "1yx3oTVty5k=")]
    public void ComputeHash_MatchesTheIndependentSftaOracle(
        string extension, string sid, string progId, string timestamp, string expected)
    {
        var utc = DateTime.Parse(
            timestamp,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal);

        Assert.Equal(expected, UserChoiceHash.ComputeHash(
            extension, sid, progId, DateTime.SpecifyKind(utc, DateTimeKind.Utc)));
    }

    [Fact]
    public void ComputeHash_IgnoresSecondsAndMillisecondsWithinTheSameMinute()
    {
        var baseline = UserChoiceHash.ComputeHash(
            ".png", "s-1-5-21-1-2-3-1001", "ezyImageViewer.Image",
            new DateTime(2026, 7, 23, 6, 30, 0, DateTimeKind.Utc));

        Assert.Equal(baseline, UserChoiceHash.ComputeHash(
            ".png", "s-1-5-21-1-2-3-1001", "ezyImageViewer.Image",
            new DateTime(2026, 7, 23, 6, 30, 59, 999, DateTimeKind.Utc)));
    }

    [Fact]
    public void ComputeHash_ChangesAcrossAdjacentMinutes()
    {
        var first = UserChoiceHash.ComputeHash(
            ".png", "s-1-5-21-1-2-3-1001", "ezyImageViewer.Image",
            new DateTime(2026, 7, 23, 6, 30, 0, DateTimeKind.Utc));
        var second = UserChoiceHash.ComputeHash(
            ".png", "s-1-5-21-1-2-3-1001", "ezyImageViewer.Image",
            new DateTime(2026, 7, 23, 6, 31, 0, DateTimeKind.Utc));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ComputeHash_TreatsLocalAndUtcRepresentationsOfTheSameInstantEqually()
    {
        var utc = new DateTime(2026, 7, 23, 6, 30, 15, DateTimeKind.Utc);
        var local = utc.ToLocalTime();

        Assert.Equal(
            UserChoiceHash.ComputeHash(".png", "s-1-5-21-1-2-3-1001", "p.Image", utc),
            UserChoiceHash.ComputeHash(".png", "s-1-5-21-1-2-3-1001", "p.Image", local));
    }

    [Fact]
    public void BuildInput_IsLowercaseAndMinuteTruncatedFiletimeHex()
    {
        var input = UserChoiceHash.BuildInput(
            ".PNG", "S-1-5-21-1-2-3-1001", "ezyImageViewer.Image",
            new DateTime(2026, 7, 23, 6, 0, 0, DateTimeKind.Utc));

        Assert.Equal(input.ToLowerInvariant(), input);
        Assert.StartsWith(".pngs-1-5-21-1-2-3-1001ezyimageviewer.image", input, StringComparison.Ordinal);
        Assert.EndsWith(
            UserChoiceHash.UserExperience.ToLowerInvariant(), input, StringComparison.Ordinal);
        var fileTime = UserChoiceHash.ToMinuteFileTimeUtc(
            new DateTime(2026, 7, 23, 6, 0, 30, DateTimeKind.Utc));
        Assert.Contains(
            ((uint)(fileTime >> 32)).ToString("x8") + ((uint)fileTime).ToString("x8"),
            input, StringComparison.Ordinal);
    }
}
