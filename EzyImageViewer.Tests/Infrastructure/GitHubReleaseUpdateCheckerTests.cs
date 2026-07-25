using System.Net;
using System.Text;
using EzyImageViewer.Infrastructure;
using Xunit;

namespace EzyImageViewer.Tests.Infrastructure;

public sealed class GitHubReleaseUpdateCheckerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 2, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("1.0.43-preview.2", "1.0.43-preview.1")]
    [InlineData("1.0.43", "1.0.43-preview.99")]
    [InlineData("1.0.43-preview.10", "1.0.43-preview.2")]
    [InlineData("1.0.43.1", "1.0.43.0")]
    public void ReleaseVersion_OrdersPreviewStableAndWindowsVersions(
        string newer,
        string older)
    {
        Assert.True(ReleaseVersion.TryParse(newer, out var left));
        Assert.True(ReleaseVersion.TryParse(older, out var right));

        Assert.True(left!.CompareTo(right) > 0);
        Assert.True(right!.CompareTo(left) < 0);
    }

    [Fact]
    public async Task CheckAsync_IncludesPreviewAndChoosesTheHighestPublishedRelease()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            """
            [
              {
                "tag_name": "v9.0.0",
                "html_url": "https://github.com/koprodev/ezyImageViewer/releases/tag/v9.0.0",
                "draft": true,
                "prerelease": false
              },
              {
                "tag_name": "v1.0.43-preview.2",
                "html_url": "https://github.com/koprodev/ezyImageViewer/releases/tag/v1.0.43-preview.2",
                "draft": false,
                "prerelease": true
              },
              {
                "tag_name": "v1.0.42",
                "html_url": "https://github.com/koprodev/ezyImageViewer/releases/tag/v1.0.42",
                "draft": false,
                "prerelease": false
              }
            ]
            """));
        using var client = new HttpClient(handler);
        var store = new MemoryStateStore();
        var checker = CreateChecker(client, store);

        var result = await checker.CheckAsync("1.0.42.0", force: false);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("1.0.43-preview.2", result.LatestVersion);
        Assert.Equal(
            "https://github.com/koprodev/ezyImageViewer/releases/tag/v1.0.43-preview.2",
            result.ReleasePage!.OriginalString);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains("application/vnd.github+json", handler.Accept);
        Assert.Contains("ezyImageViewer-update-check/1.0", handler.UserAgent);
        Assert.Equal("2026-03-10", handler.ApiVersion);
        Assert.Equal(Now, store.LastAttemptUtc);
    }

    [Fact]
    public async Task CheckAsync_RejectsReleaseLinksOutsideTheFixedRepository()
    {
        using var client = new HttpClient(new RecordingHandler(_ => JsonResponse(
            """
            [
              {
                "tag_name": "v2.0.0",
                "html_url": "https://example.com/koprodev/ezyImageViewer/releases/tag/v2.0.0",
                "draft": false,
                "prerelease": false
              }
            ]
            """)));
        var checker = CreateChecker(client, new MemoryStateStore());

        var result = await checker.CheckAsync("1.0.42.0", force: true);

        Assert.Equal(UpdateCheckStatus.Unavailable, result.Status);
        Assert.Null(result.ReleasePage);
    }

    [Fact]
    public async Task CheckAsync_SkipsFreshAutomaticCheckButForceBypassesTheCache()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            """
            [
              {
                "tag_name": "v1.0.42",
                "html_url": "https://github.com/koprodev/ezyImageViewer/releases/tag/v1.0.42",
                "draft": false,
                "prerelease": false
              }
            ]
            """));
        using var client = new HttpClient(handler);
        var store = new MemoryStateStore { LastAttemptUtc = Now.AddHours(-1) };
        var checker = CreateChecker(client, store);

        var automatic = await checker.CheckAsync("1.0.42.0", force: false);
        var manual = await checker.CheckAsync("1.0.42.0", force: true);

        Assert.Equal(UpdateCheckStatus.Skipped, automatic.Status);
        Assert.Equal(UpdateCheckStatus.Current, manual.Status);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task CheckAsync_RecordsFailedAttemptsWithoutThrowing()
    {
        using var client = new HttpClient(new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        var store = new MemoryStateStore();
        var checker = CreateChecker(client, store);

        var result = await checker.CheckAsync("1.0.42.0", force: false);

        Assert.Equal(UpdateCheckStatus.Unavailable, result.Status);
        Assert.Equal(Now, store.LastAttemptUtc);
    }

    [Fact]
    public void StateStore_RoundTripsOnlyTheLastAttemptTimestamp()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "ezy-update-check-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppDataPaths(directory);
            var store = new UpdateCheckStateStore(paths);

            store.WriteLastAttemptUtc(Now);

            Assert.Equal(Now, store.ReadLastAttemptUtc());
            Assert.Equal(
                Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                File.ReadAllText(paths.UpdateCheckStateFile, Encoding.UTF8));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static GitHubReleaseUpdateChecker CreateChecker(
        HttpClient client,
        IUpdateCheckStateStore store) =>
        new(client, store, new StubTimeProvider(Now));

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class MemoryStateStore : IUpdateCheckStateStore
    {
        public DateTimeOffset? LastAttemptUtc { get; set; }
        public DateTimeOffset? ReadLastAttemptUtc() => LastAttemptUtc;
        public void WriteLastAttemptUtc(DateTimeOffset value) => LastAttemptUtc = value;
    }

    private sealed class StubTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string Accept { get; private set; } = string.Empty;
        public string UserAgent { get; private set; } = string.Empty;
        public string ApiVersion { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Accept = string.Join(",", request.Headers.Accept);
            UserAgent = request.Headers.UserAgent.ToString();
            ApiVersion = request.Headers.GetValues("X-GitHub-Api-Version").Single();
            Assert.Equal(ReleaseDistributionPolicy.ReleasesApi, request.RequestUri);
            return Task.FromResult(responseFactory(request));
        }
    }
}
