using EzyImageViewer.Infrastructure;
using Xunit;

namespace EzyImageViewer.Tests.Infrastructure;

/// <summary>
/// 다국어 지원 이전에 저장된 설정 파일에는 language 키가 없다. 소스 생성 역직렬화는
/// JSON에 키가 없으면 프로퍼티 초기값을 적용하지 않고 null을 남기는데, 그대로 두면
/// 첫 사용처에서 앱이 통째로 죽는다. 기존 사용자 전원이 업그레이드하자마자 겪는 경로다.
/// </summary>
public sealed class AppSettingsLanguageUpgradeTests : IDisposable
{
    private const string SettingsWithoutLanguage = """
        {
          "schemaVersion": 5,
          "toolRailDock": 0,
          "clipboardWatchEnabled": true,
          "recentFilesEnabled": true,
          "includeSubfoldersInNavigation": false,
          "singleInstanceBehavior": 0,
          "theme": 0,
          "captureHotkey": { "modifiers": 6, "virtualKey": 69 },
          "toolDefaults": {
            "styles": {},
            "strokeArgb": 4293409582,
            "maskArgb": 4278190080,
            "fillEnabled": false,
            "mosaicBlockSize": 12,
            "blurSigma": 8,
            "cornerRadius": 8,
            "arrowhead": 2,
            "fontFamily": "Malgun Gothic",
            "fontBold": false,
            "fontItalic": false,
            "textAlignment": 0,
            "textBackgroundEnabled": false
          },
          "captureAutoSaveEnabled": false,
          "toolbarOpenGroupEnabled": true,
          "toolbarSelectGroupEnabled": true,
          "toolbarTransformGroupEnabled": true,
          "toolbarCropGroupEnabled": true,
          "toolbarZoomGroupEnabled": true,
          "toolbarProtectGroupEnabled": true
        }
        """;

    private readonly string _folder = Directory.CreateTempSubdirectory("ezy-lang-upgrade").FullName;

    [Fact]
    public void LoadingSettingsSavedBeforeMultiLanguage_LeavesLanguageUsableNotNull()
    {
        File.WriteAllText(Path.Combine(_folder, "settings.json"), SettingsWithoutLanguage);
        var store = new AppSettingsStore(new AppDataPaths(_folder));

        var settings = store.Load();

        // null이면 여기서가 아니라 앱 시작 경로에서 죽는다. 모델이 null을 들고 나가지 못하게 막는다.
        Assert.NotNull(settings.Language);
        Assert.Equal(LanguagePolicy.SystemDefault, settings.Language);
        // 나머지 설정은 그대로 살아 있어야 한다. 통째로 기본값으로 떨어지면 사용자 설정이 날아간다.
        Assert.Equal("Malgun Gothic", settings.ToolDefaults.FontFamily);
        Assert.True(settings.ClipboardWatchEnabled);
    }

    [Fact]
    public void ExplicitNullLanguage_IsNormalizedToSystemDefault()
    {
        var settings = new AppSettings { Language = null! };

        Assert.NotNull(settings.Language);
        Assert.Equal(LanguagePolicy.SystemDefault, settings.Language);
    }

    [Fact]
    public void RoundTrippedSettings_KeepTheChosenLanguage()
    {
        var store = new AppSettingsStore(new AppDataPaths(_folder));
        store.Save(new AppSettings { Language = "ja-JP" });

        Assert.Equal("ja-JP", store.Load().Language);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 테스트 결과와 무관.
        }
    }
}
