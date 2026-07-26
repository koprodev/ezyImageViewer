using System.Text.RegularExpressions;
using System.Xml.Linq;
using EzyImageViewer.Infrastructure;
using Xunit;

namespace EzyImageViewer.Tests.Contracts;

public sealed class LocalizationContractTests
{
    /// <summary>지원 언어마다 resw 폴더가 하나씩 있어야 한다. 목록에만 올리고 파일을 빼면
    /// 그 언어는 조용히 en-US로 떨어져 "지원한다"는 말이 거짓이 된다.</summary>
    [Fact]
    public void EverySupportedLanguage_HasAResourceFolderAndNoStrays()
    {
        var stringsRoot = RepoFile("EzyImageViewer.App", "Strings");
        var folders = Directory
            .GetDirectories(stringsRoot)
            .Select(Path.GetFileName)
            .Select(name => name!)
            .Order()
            .ToArray();

        Assert.Equal(LanguagePolicy.SupportedTags.Order().ToArray(), folders);
        Assert.All(folders, folder => Assert.True(
            File.Exists(Path.Combine(stringsRoot, folder, "Resources.resw")),
            $"Strings\\{folder}\\Resources.resw is missing."));
    }

    /// <summary>AppStrings의 대체 문자열은 PRI를 못 읽을 때의 안전망이지 번역 원본이 아니다.
    /// resw에 빠진 키는 화면에서 멀쩡히 보여 눈에 안 띄고, 언어를 늘릴 때 통째로 누락된다.</summary>
    [Fact]
    public void EveryLocale_DeclaresExactlyTheKeysAppStringsAsksFor()
    {
        var source = File.ReadAllText(RepoFile("EzyImageViewer.App", "AppStrings.cs"));
        var codeKeys = Regex
            .Matches(source, @"Get\(""(?<key>[A-Za-z0-9_]+)""")
            .Select(match => match.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(codeKeys);

        foreach (var tag in LanguagePolicy.SupportedTags)
        {
            var resourceKeys = LoadValues(tag).Keys.ToHashSet(StringComparer.Ordinal);

            var missing = codeKeys.Except(resourceKeys).Order().ToArray();
            Assert.True(
                missing.Length == 0,
                $"{tag} is missing: {string.Join(", ", missing)}");
            var orphaned = resourceKeys.Except(codeKeys).Order().ToArray();
            Assert.True(
                orphaned.Length == 0,
                $"{tag} declares unused keys: {string.Join(", ", orphaned)}");
        }
    }

    /// <summary>서식 자리표시자를 번역하다 빠뜨리면 그 언어에서만 버전 번호가 사라진다.
    /// 예외도 안 나서 그 언어로 실제로 보기 전까지 아무도 모른다.</summary>
    [Fact]
    public void TranslatedStrings_KeepTheSameFormatPlaceholdersAsEnglish()
    {
        var english = LoadValues(LanguagePolicy.FallbackTag);

        foreach (var tag in LanguagePolicy.SupportedTags)
        {
            if (string.Equals(tag, LanguagePolicy.FallbackTag, StringComparison.Ordinal))
                continue;
            var translated = LoadValues(tag);
            foreach (var (key, value) in english)
            {
                var expected = Placeholders(value);
                var actual = Placeholders(translated[key]);
                Assert.True(
                    expected.SetEquals(actual),
                    $"{tag}/{key} placeholders differ: expected [{string.Join(",", expected.Order())}], "
                    + $"actual [{string.Join(",", actual.Order())}]");
            }
        }
    }

    /// <summary>번역이 비어 있으면 그 자리가 화면에서 통째로 사라진다. 빈 값은 누락과 같다.</summary>
    [Fact]
    public void TranslatedStrings_AreNeverBlank()
    {
        foreach (var tag in LanguagePolicy.SupportedTags)
        {
            var blank = LoadValues(tag)
                .Where(pair => string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => pair.Key)
                .Order()
                .ToArray();
            Assert.True(blank.Length == 0, $"{tag} has blank values: {string.Join(", ", blank)}");
        }
    }

    /// <summary>중립 리소스 언어와 코드의 최종 폴백이 어긋나면 폴백이 두 갈래로 갈린다.</summary>
    [Fact]
    public void FallbackLanguage_MatchesTheProjectDefaultLanguage()
    {
        var project = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "EzyImageViewer.App.csproj"));

        Assert.Contains(
            $"<DefaultLanguage>{LanguagePolicy.FallbackTag}</DefaultLanguage>",
            project,
            StringComparison.Ordinal);
        Assert.Contains(LanguagePolicy.FallbackTag, LanguagePolicy.SupportedTags);
    }

    private static Dictionary<string, string> LoadValues(string tag) => XDocument
        .Load(RepoFile("EzyImageViewer.App", "Strings", tag, "Resources.resw"))
        .Root!
        .Elements("data")
        .Where(element => element.Attribute("name") is not null)
        .ToDictionary(
            element => (string)element.Attribute("name")!,
            element => element.Element("value")?.Value ?? string.Empty,
            StringComparer.Ordinal);

    private static HashSet<string> Placeholders(string value) => Regex
        .Matches(value, @"\{\d+\}")
        .Select(match => match.Value)
        .ToHashSet(StringComparer.Ordinal);

    private static string RepoFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "EzyImageViewer.slnx")))
                continue;
            return Path.Combine([directory.FullName, .. segments]);
        }
        throw new DirectoryNotFoundException(
            "Repository root was not found from the test output directory.");
    }
}
