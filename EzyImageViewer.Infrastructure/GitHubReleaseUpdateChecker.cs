using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EzyImageViewer.Infrastructure;

public enum UpdateCheckStatus
{
    Skipped,
    Current,
    UpdateAvailable,
    Unavailable,
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string CurrentVersion,
    string? LatestVersion = null,
    Uri? ReleasePage = null);

/// <summary>앱이 비교할 릴리스 버전. 세 자리 SemVer와 Windows식 네 자리 버전을 함께 받음.</summary>
public sealed partial class ReleaseVersion : IComparable<ReleaseVersion>
{
    private ReleaseVersion(
        int major,
        int minor,
        int patch,
        int revision,
        string[] prerelease,
        string display)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Revision = revision;
        Prerelease = prerelease;
        Display = display;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public int Revision { get; }
    public IReadOnlyList<string> Prerelease { get; }
    public string Display { get; }
    public bool IsPrerelease => Prerelease.Count > 0;

    public static bool TryParse(string? value, out ReleaseVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var candidate = value.Trim();
        if (candidate.StartsWith('v') || candidate.StartsWith('V'))
            candidate = candidate[1..];
        var buildSeparator = candidate.IndexOf('+');
        if (buildSeparator >= 0)
            candidate = candidate[..buildSeparator];

        var match = VersionPattern().Match(candidate);
        if (!match.Success
            || !TryNumber(match.Groups["major"].Value, out var major)
            || !TryNumber(match.Groups["minor"].Value, out var minor)
            || !TryNumber(match.Groups["patch"].Value, out var patch)
            || !TryNumber(match.Groups["revision"].Value, out var revision))
            return false;

        var prerelease = match.Groups["prerelease"].Success
            ? match.Groups["prerelease"].Value.Split('.')
            : [];
        if (prerelease.Any(identifier => identifier.Length == 0))
            return false;

        version = new ReleaseVersion(
            major, minor, patch, revision, prerelease, candidate);
        return true;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
            return 1;

        var numeric = Major.CompareTo(other.Major);
        if (numeric == 0) numeric = Minor.CompareTo(other.Minor);
        if (numeric == 0) numeric = Patch.CompareTo(other.Patch);
        if (numeric == 0) numeric = Revision.CompareTo(other.Revision);
        if (numeric != 0)
            return numeric;

        if (!IsPrerelease || !other.IsPrerelease)
            return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;

        var length = Math.Min(Prerelease.Count, other.Prerelease.Count);
        for (var index = 0; index < length; index++)
        {
            var comparison = CompareIdentifier(Prerelease[index], other.Prerelease[index]);
            if (comparison != 0)
                return comparison;
        }
        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    public override string ToString() => Display;

    private static bool TryNumber(string value, out int number)
    {
        if (value.Length == 0)
        {
            number = 0;
            return true;
        }
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = int.TryParse(
            left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
        var rightNumeric = int.TryParse(
            right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
        if (leftNumeric && rightNumeric)
            return leftNumber.CompareTo(rightNumber);
        if (leftNumeric != rightNumeric)
            return leftNumeric ? -1 : 1;
        return string.Compare(left, right, StringComparison.Ordinal);
    }

    [GeneratedRegex(
        @"^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:\.(?<revision>0|[1-9]\d*))?(?:-(?<prerelease>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}

public interface IUpdateCheckStateStore
{
    DateTimeOffset? ReadLastAttemptUtc();
    void WriteLastAttemptUtc(DateTimeOffset value);
}

/// <summary>마지막 조회 시각만 보관. 릴리스 정보나 사용자 데이터는 슬쩍 끼워 넣지 않음.</summary>
public sealed class UpdateCheckStateStore(IAppDataPaths paths) : IUpdateCheckStateStore
{
    public DateTimeOffset? ReadLastAttemptUtc()
    {
        try
        {
            if (!File.Exists(paths.UpdateCheckStateFile))
                return null;
            var text = File.ReadAllText(paths.UpdateCheckStateFile, Encoding.UTF8).Trim();
            return DateTimeOffset.TryParseExact(
                text,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var value)
                ? value
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return null;
        }
    }

    public void WriteLastAttemptUtc(DateTimeOffset value)
    {
        AtomicFileWriter.Write(
            paths.UpdateCheckStateFile,
            Encoding.UTF8.GetBytes(value.ToUniversalTime().ToString(
                "O", CultureInfo.InvariantCulture)),
            AtomicFileProtection.CurrentUserAndSystem);
    }
}

/// <summary>GitHub 공개 릴리스 목록을 읽어 새 버전만 판정. 다운로드와 설치는 취급 안 함.</summary>
public sealed class GitHubReleaseUpdateChecker
{
    public static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);
    private const int MaximumResponseBytes = 512 * 1024;

    private readonly HttpClient _httpClient;
    private readonly IUpdateCheckStateStore _stateStore;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GitHubReleaseUpdateChecker(
        HttpClient httpClient,
        IUpdateCheckStateStore stateStore,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<UpdateCheckResult> CheckAsync(
        string currentVersion,
        bool force,
        CancellationToken cancellationToken = default)
    {
        if (!ReleaseVersion.TryParse(currentVersion, out var current))
            return new UpdateCheckResult(UpdateCheckStatus.Unavailable, currentVersion);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (!force
                && _stateStore.ReadLastAttemptUtc() is { } previous
                && previous <= now
                && now - previous < AutomaticCheckInterval)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Skipped, currentVersion);
            }

            TryRecordAttempt(now);
            using var request = new HttpRequestMessage(
                HttpMethod.Get, ReleaseDistributionPolicy.ReleasesApi);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
                "application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd("ezyImageViewer-update-check/1.0");
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
                return new UpdateCheckResult(UpdateCheckStatus.Unavailable, currentVersion);

            var payload = await ReadBoundedAsync(
                response.Content, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return new UpdateCheckResult(UpdateCheckStatus.Unavailable, currentVersion);

            ReleaseVersion? latest = null;
            Uri? releasePage = null;
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object
                    || ReadBoolean(release, "draft")
                    || !TryReadRelease(release, out var candidate, out var candidatePage)
                    || candidate.CompareTo(latest) <= 0)
                    continue;
                latest = candidate;
                releasePage = candidatePage;
            }

            if (latest is null || releasePage is null)
                return new UpdateCheckResult(UpdateCheckStatus.Unavailable, currentVersion);
            return latest.CompareTo(current) > 0
                ? new UpdateCheckResult(
                    UpdateCheckStatus.UpdateAvailable,
                    currentVersion,
                    latest.Display,
                    releasePage)
                : new UpdateCheckResult(
                    UpdateCheckStatus.Current,
                    currentVersion,
                    latest.Display,
                    releasePage);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Unavailable, currentVersion);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
            or JsonException or InvalidOperationException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Unavailable, currentVersion);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void TryRecordAttempt(DateTimeOffset value)
    {
        try
        {
            _stateStore.WriteLastAttemptUtc(value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            // 캐시가 삐끗해도 조회까지 파업할 이유는 없음.
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > MaximumResponseBytes)
                throw new IOException("GitHub release response exceeded the size limit.");
            output.Write(buffer, 0, read);
        }
    }

    private static bool TryReadRelease(
        JsonElement release,
        out ReleaseVersion version,
        out Uri page)
    {
        version = null!;
        page = null!;
        if (!release.TryGetProperty("tag_name", out var tagProperty)
            || tagProperty.ValueKind != JsonValueKind.String
            || !ReleaseVersion.TryParse(tagProperty.GetString(), out var parsed)
            || parsed is null
            || !release.TryGetProperty("html_url", out var pageProperty)
            || pageProperty.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(pageProperty.GetString(), UriKind.Absolute, out var parsedPage)
            || !ReleaseDistributionPolicy.IsTrustedReleasePage(parsedPage))
            return false;

        version = parsed;
        page = parsedPage;
        return true;
    }

    private static bool ReadBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.True;
}
