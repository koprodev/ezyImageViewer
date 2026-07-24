using System.Text.RegularExpressions;
using System.Xml.Linq;
using SkiaSharp;
using Xunit;

namespace EzyImageViewer.Tests.Contracts;

public sealed class IconSystemContractTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void StaticIconReferences_AreUniqueAndResolvable()
    {
        var icons = XDocument.Load(RepoFile("EzyImageViewer.App", "Resources", "Icons.xaml"));
        var viewer = File.ReadAllText(RepoFile("EzyImageViewer.App", "Views", "ViewerWindow.xaml"));
        var keys = icons.Root!.Elements()
            .Select(element => (string?)element.Attribute(Xaml + "Key"))
            .Where(key => key is not null)
            .Cast<string>()
            .ToArray();

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.True(keys.Length >= 40, $"Expected the complete icon set, found {keys.Length}.");

        var rawReferences = Regex.Matches(viewer, @"StaticResource\s+(Icon\.[^}\s]+)")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        var references = rawReferences.Distinct(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(references);
        Assert.Equal(rawReferences.Length, references.Length);
        Assert.All(references, reference => Assert.Contains(reference, keys));
    }

    [Fact]
    public void ButtonIcons_DoNotUseInlineGlyphsOrLegacyCharacters()
    {
        var viewer = File.ReadAllText(RepoFile("EzyImageViewer.App", "Views", "ViewerWindow.xaml"));

        Assert.DoesNotContain("<FontIcon", viewer, StringComparison.Ordinal);
        Assert.DoesNotContain("Glyph=", viewer, StringComparison.Ordinal);
        foreach (var legacy in new[] { "✂", "✎", "HL", "╱", "→", "□", "▢", "○", "⇤", "⇥", "⧉", "T…" })
            Assert.DoesNotContain($"Text=\"{legacy}\"", viewer, StringComparison.Ordinal);
    }

    [Fact]
    public void RailSeparators_AndThirtySixPixelTargets_AreFixedContracts()
    {
        var viewer = XDocument.Load(RepoFile("EzyImageViewer.App", "Views", "ViewerWindow.xaml"));
        var app = File.ReadAllText(RepoFile("EzyImageViewer.App", "App.xaml"));
        var code = File.ReadAllText(RepoFile("EzyImageViewer.App", "Views", "ViewerWindow.xaml.cs"));
        var separators = viewer.Descendants()
            .Where(element => element.Name.LocalName == "Border")
            .Select(element => (string?)element.Attribute(Xaml + "Name"))
            .Where(name => name?.EndsWith("Separator", StringComparison.Ordinal) == true)
            .ToArray();

        // 맞춤·1:1 줌 그룹 이동 뒤 도구 그룹 수는 9.
        Assert.Equal(9, separators.Length);
        Assert.Contains("separator.Width = horizontal ? 1 : 28", code, StringComparison.Ordinal);
        Assert.Contains("separator.Height = horizontal ? 28 : 1", code, StringComparison.Ordinal);
        Assert.True(Regex.Matches(app, "<Setter Property=\"Width\" Value=\"36\" />").Count >= 2);
        Assert.True(Regex.Matches(app, "<Setter Property=\"Height\" Value=\"36\" />").Count >= 2);
    }

    [Fact]
    public void ProtectionUi_KeepsMaskColorIndependent_AndOffersNoRotateAffordance()
    {
        var code = File.ReadAllText(RepoFile("EzyImageViewer.App", "Views", "ViewerWindow.xaml.cs"));
        var rendering = File.ReadAllText(RepoFile("EzyImageViewer.Rendering", "AnnotationRendering.cs"));

        // 마스크 색은 독립 상태. 팔레트는 문맥에 따라 마스크·선 중 하나만 편집·반영.
        Assert.Contains("private uint _maskColor = 0xFF00_0000;", code, StringComparison.Ordinal);
        Assert.Contains("private bool IsMaskColorContext()", code, StringComparison.Ordinal);
        Assert.Contains(
            "var effective = IsMaskColorContext() ? _maskColor : _strokeColor;",
            code, StringComparison.Ordinal);
        // 보호 효과는 회전하지 않아 누름 처리와 선택 오버레이 모두 거절.
        Assert.Contains(
            "handle == SelectionHandle.Rotate && selected is ProtectionAnnotation",
            code, StringComparison.Ordinal);
        Assert.Contains(
            "var canRotate = annotation is not ProtectionAnnotation;",
            rendering, StringComparison.Ordinal);
    }

    [Fact]
    public void SavePipeline_OwnsItsInputs_AndBindsProjectMetadataToTheDocument()
    {
        var window = File.ReadAllText(RepoFile("EzyImageViewer.App", "Views", "ViewerWindow.xaml.cs"));
        var viewModel = File.ReadAllText(RepoFile("EzyImageViewer.App", "ViewModels", "ViewerViewModel.cs"));
        var clipboard = File.ReadAllText(RepoFile("EzyImageViewer.Capture", "Clipboard", "WinRtClipboardBackend.cs"));

        // 저장 완료는 확보 상태 토큰으로만 표시. 작업자 평면화는 자체 프레임·캐시 소유.
        Assert.Contains("_viewModel.Editor.MarkSaved(stateId);", window, StringComparison.Ordinal);
        Assert.Contains("private SKImage CopySnapshot()", window, StringComparison.Ordinal);
        Assert.Contains("WarmExportAssetsAsync(state, token)", window, StringComparison.Ordinal);
        Assert.DoesNotContain("state, _assetCache);", window, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(
            window,
            @"document\.WasAnimationFlattened\s*&&\s*document\.IsReducedPreview").Count);

        // 프로젝트 메타데이터는 로더의 정확한 문서 인스턴스에 결박. 복원 레이어로 패널 재그리기.
        Assert.Contains("ConditionalWeakTable<ImageDocument, ProjectOpenData>",
            viewModel, StringComparison.Ordinal);
        var consume = window.IndexOf("_activeLayerId = projectLayer;", StringComparison.Ordinal);
        Assert.True(consume >= 0, "project active-layer consume anchor missing");
        var repaint = window.IndexOf("UpdateLayerPanel();", consume, StringComparison.Ordinal);
        Assert.InRange(repaint - consume, 1, 200);
        Assert.Contains("Editor.Changed += EnforcePageHistoryBudget;", viewModel, StringComparison.Ordinal);
        Assert.Contains("public bool IsMutationBlocked", viewModel, StringComparison.Ordinal);
        Assert.Contains(
            "catch (ObjectDisposedException) when (!ReferenceEquals(document, Session.Current))",
            viewModel,
            StringComparison.Ordinal);

        // 복사는 앱 종료 뒤에도 남고 Ctrl+C는 포커스 있는 텍스트 입력에 양보.
        Assert.Contains("Clipboard.Flush()", clipboard, StringComparison.Ordinal);
        Assert.Contains("IsTextInputFocused()", window, StringComparison.Ordinal);
    }

    [Fact]
    public void DockToggle_IsPinnedBeforeTheScrollableRail()
    {
        var viewer = XDocument.Load(RepoFile("EzyImageViewer.App", "Views", "ViewerWindow.xaml"));
        var icons = XDocument.Load(RepoFile("EzyImageViewer.App", "Resources", "Icons.xaml"));
        var xaml = File.ReadAllText(RepoFile("EzyImageViewer.App", "Views", "ViewerWindow.xaml"));
        var code = File.ReadAllText(RepoFile("EzyImageViewer.App", "Views", "ViewerWindow.xaml.cs"));
        XElement Named(string name) => Assert.Single(viewer.Descendants(), element =>
            (string?)element.Attribute(Xaml + "Name") == name);

        var viewport = Named("ToolRailViewport");
        var dock = Named("DockToggleButton");
        var separator = Named("DockMenuSeparator");
        var scrollViewport = Named("ToolRailScrollableViewport");
        var scroll = Named("ToolRailScroll");
        Assert.Same(viewport, dock.Parent);
        Assert.Same(viewport, separator.Parent);
        Assert.Same(viewport, scrollViewport.Parent);
        Assert.Same(scrollViewport, scroll.Parent);
        Assert.DoesNotContain(dock, scroll.Descendants());
        foreach (var name in new[] { "ToolRailStartOverflowHint", "ToolRailEndOverflowHint" })
            Assert.Same(scrollViewport, Named(name).Parent);

        var directNames = viewport.Elements()
            .Select(element => (string?)element.Attribute(Xaml + "Name"))
            .Where(name => name is not null)
            .ToArray();
        Assert.Equal(new[] { "DockToggleButton", "DockMenuSeparator", "ToolRailScrollableViewport" }, directNames);
        Assert.Equal("0", (string?)dock.Attribute("Grid.Row"));
        Assert.Equal("0", (string?)dock.Attribute("Grid.Column"));
        Assert.Contains("Grid.SetRow(DockMenuSeparator, horizontal ? 0 : 1)", code, StringComparison.Ordinal);
        Assert.Contains("Grid.SetColumn(DockMenuSeparator, horizontal ? 1 : 0)", code, StringComparison.Ordinal);
        Assert.Contains("Grid.SetRow(ToolRailScrollableViewport, horizontal ? 0 : 2)", code, StringComparison.Ordinal);
        Assert.Contains("Grid.SetColumn(ToolRailScrollableViewport, horizontal ? 2 : 0)", code, StringComparison.Ordinal);

        var switchIcon = Assert.Single(icons.Root!.Elements(), element =>
            (string?)element.Attribute(Xaml + "Key") == "Icon.Layout.SwitchOrientation");
        Assert.Equal("FontIconSource", switchIcon.Name.LocalName);
        Assert.Contains("IconSource=\"{StaticResource Icon.Layout.SwitchOrientation}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Icon.View.DockHorizontal", xaml + code + icons, StringComparison.Ordinal);
        Assert.DoesNotContain("Icon.View.DockVertical", xaml + code + icons, StringComparison.Ordinal);
        Assert.DoesNotContain("DockToggleIcon", xaml + code, StringComparison.Ordinal);
    }

    [Fact]
    public void RailOverflow_UsesDirectionalHintsWithoutVisibleScrollbars()
    {
        var viewer = XDocument.Load(RepoFile("EzyImageViewer.App", "Views", "ViewerWindow.xaml"));
        var code = File.ReadAllText(RepoFile("EzyImageViewer.App", "Views", "ViewerWindow.xaml.cs"));
        var scroll = Assert.Single(viewer.Descendants(), element =>
            (string?)element.Attribute(Xaml + "Name") == "ToolRailScroll");

        Assert.Equal("Hidden", (string?)scroll.Attribute("VerticalScrollBarVisibility"));
        Assert.Equal("Disabled", (string?)scroll.Attribute("HorizontalScrollBarVisibility"));
        foreach (var name in new[] { "ToolRailStartOverflowHint", "ToolRailEndOverflowHint" })
        {
            var hint = Assert.Single(viewer.Descendants(), element =>
                (string?)element.Attribute(Xaml + "Name") == name);
            Assert.Equal("False", (string?)hint.Attribute("IsHitTestVisible"));
            Assert.Equal(name, (string?)hint.Attribute("AutomationProperties.AutomationId"));
            Assert.Equal("Raw", (string?)hint.Attribute("AutomationProperties.AccessibilityView"));
        }

        Assert.Contains("ToolRailScroll.ViewChanged += OnToolRailViewChanged", code, StringComparison.Ordinal);
        Assert.Contains("ToolRailScroll.LayoutUpdated += OnToolRailLayoutUpdated", code, StringComparison.Ordinal);
        Assert.Contains("ToolRailScroll.ScrollableWidth", code, StringComparison.Ordinal);
        Assert.Contains("ToolRailScroll.ScrollableHeight", code, StringComparison.Ordinal);
        Assert.Contains("_animationsEnabled = _uiSettings.AnimationsEnabled", code, StringComparison.Ordinal);
        Assert.Contains("_uiSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged", code, StringComparison.Ordinal);
        Assert.Contains("_uiSettings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged", code, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(900)", code, StringComparison.Ordinal);
        Assert.Contains("ScrollBarVisibility.Hidden", code, StringComparison.Ordinal);
        Assert.Contains("ToolRailViewport.Width = horizontal ? double.NaN : 36", code, StringComparison.Ordinal);
        Assert.Contains("ToolRailViewport.Height = horizontal ? 36 : double.NaN", code, StringComparison.Ordinal);
        Assert.Contains("QueueToolRailOverflowUpdate(resetToStart: true)", code, StringComparison.Ordinal);
        Assert.Contains("OnToolRailPointerWheelChanged", code, StringComparison.Ordinal);
        Assert.Contains("delta * 0.6", code, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true", code, StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicIcons_ResolveMaterialGlyphStringsPerWindow()
    {
        var icons = XDocument.Load(RepoFile("EzyImageViewer.App", "Resources", "Icons.xaml"));
        var app = File.ReadAllText(RepoFile("EzyImageViewer.App", "App.xaml"));
        var viewer = File.ReadAllText(RepoFile("EzyImageViewer.App", "Views", "ViewerWindow.xaml"));
        var code = File.ReadAllText(RepoFile("EzyImageViewer.App", "Views", "ViewerWindow.xaml.cs"));
        var dynamicGlyphs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Icon.Layer.Visible"] = "",
            ["Icon.Layer.Hidden"] = "",
            ["Icon.Layer.Locked"] = "",
            ["Icon.Layer.Unlocked"] = "",
            ["Icon.Common.Check.Light"] = "",
            ["Icon.Common.Check.Dark"] = "",
        };

        foreach (var (key, glyph) in dynamicGlyphs)
        {
            var resource = Assert.Single(icons.Root!.Elements(),
                element => (string?)element.Attribute(Xaml + "Key") == key);
            Assert.Equal("String", resource.Name.LocalName);
            Assert.Equal(glyph, resource.Value);
        }
        Assert.DoesNotContain("XamlBindingHelper", code, StringComparison.Ordinal);
        Assert.DoesNotContain("PathIconSource", code, StringComparison.Ordinal);
        Assert.Contains("return new FontIconSource", code, StringComparison.Ordinal);
        Assert.Contains("(FontFamily)Root.Resources[\"Icon.FontFamily\"]", code, StringComparison.Ordinal);
        Assert.Contains("ms-appx:///Resources/Icons.xaml", viewer, StringComparison.Ordinal);
        Assert.DoesNotContain("Resources/Icons.xaml", app, StringComparison.Ordinal);
        Assert.Contains("Root.Resources[key]", code, StringComparison.Ordinal);
    }

    [Fact]
    public void MaterialSymbolsMapping_MatchesTheApprovedManifest()
    {
        var icons = XDocument.Load(RepoFile("EzyImageViewer.App", "Resources", "Icons.xaml"));
        var viewer = File.ReadAllText(RepoFile("EzyImageViewer.App", "Views", "ViewerWindow.xaml"));
        var code = File.ReadAllText(RepoFile("EzyImageViewer.App", "Views", "ViewerWindow.xaml.cs"));
        var csproj = File.ReadAllText(RepoFile("EzyImageViewer.App", "EzyImageViewer.App.csproj"));
        // 승인된 매핑과 공식 MaterialSymbolsOutlined codepoints 파일로 추가 글리프 검증.
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Icon.File.Open"] = "E2C7",
            ["Icon.Group.Open"] = "E2C7",
            ["Icon.Group.Rotate"] = "E41A",
            ["Icon.Group.Crop"] = "E3BE",
            ["Icon.Group.Zoom"] = "EA10",
            ["Icon.Group.Protect"] = "E3EC",
            ["Icon.File.Save"] = "E161",
            ["Icon.File.Clipboard"] = "E14F",
            ["Icon.File.Recent"] = "E889",
            ["Icon.File.NewWindow"] = "E03C",
            ["Icon.File.Capture"] = "E412",
            ["Icon.History.Undo"] = "E166",
            ["Icon.History.Redo"] = "E15A",
            ["Icon.Image.Select"] = "F82F",
            ["Icon.Image.Crop"] = "E3BE",
            ["Icon.Image.CropRatio"] = "E85B",
            ["Icon.Image.Resize"] = "F707",
            ["Icon.Image.Rotate"] = "E41A",
            ["Icon.Image.FlipHorizontal"] = "E3E8",
            ["Icon.Image.FlipVertical"] = "E3E8",
            ["Icon.Draw.Pen"] = "F097",
            ["Icon.Draw.Highlighter"] = "E6D1",
            ["Icon.Draw.Line"] = "F41E",
            ["Icon.Draw.Arrow"] = "E941",
            ["Icon.Draw.ColorPalette"] = "E40A",
            ["Icon.Draw.Eyedropper"] = "E3B8",
            ["Icon.Shape.Rectangle"] = "EB54",
            ["Icon.Shape.Ellipse"] = "EF4A",
            ["Icon.Shape.Number"] = "E9EF",
            ["Icon.Text.Text"] = "E262",
            ["Icon.Protect.Mosaic"] = "E3EC",
            ["Icon.Protect.Blur"] = "E3A5",
            ["Icon.Protect.Mask"] = "F022",
            ["Icon.View.Fit"] = "EA10",
            ["Icon.View.LayerPanel"] = "E53B",
            ["Icon.Layout.SwitchOrientation"] = "E627",
            ["Icon.Layer.Add"] = "E145",
            ["Icon.Layer.Delete"] = "E92E",
            ["Icon.Layer.Up"] = "E5D8",
            ["Icon.Layer.Down"] = "E5DB",
            ["Icon.Layer.Rename"] = "E9A2",
            ["Icon.Layer.MoveSelection"] = "E9A1",
            ["Icon.Object.SendToBack"] = "E882",
            ["Icon.Object.SendBackward"] = "EB61",
            ["Icon.Object.BringForward"] = "EB64",
            ["Icon.Object.BringToFront"] = "E883",
            ["Icon.Object.Duplicate"] = "E14D",
            ["Icon.Object.EditText"] = "E745",
            ["Icon.View.Previous"] = "E5CB",
            ["Icon.View.Next"] = "E5CC",
            ["Icon.View.Play"] = "E037",
            ["Icon.View.Pause"] = "E034",
            ["Icon.View.ZoomOut"] = "E900",
            ["Icon.View.ZoomIn"] = "E8FF",
            ["Icon.View.FullScreen"] = "E5D0",
            ["Icon.App.Settings"] = "E8B8",
        };

        var fontIcons = icons.Root!.Elements()
            .Where(element => element.Name.LocalName == "FontIconSource")
            .ToArray();
        Assert.Equal(expected.Count, fontIcons.Length);
        foreach (var element in fontIcons)
        {
            var key = (string)element.Attribute(Xaml + "Key")!;
            var glyph = (string?)element.Attribute("Glyph");
            Assert.Equal("{StaticResource Icon.FontFamily}", (string?)element.Attribute("FontFamily"));
            Assert.Equal("20", (string?)element.Attribute("FontSize"));
            Assert.NotNull(glyph);
            var single = Assert.Single(glyph!);
            Assert.Equal(expected[key], ((int)single).ToString("X4"));
        }

        var family = Assert.Single(icons.Root!.Elements(),
            element => element.Name.LocalName == "FontFamily");
        Assert.Equal("Icon.FontFamily", (string?)family.Attribute(Xaml + "Key"));
        Assert.Equal(
            "ms-appx:///Assets/Fonts/MaterialSymbolsOutlined.ttf#Material Symbols Outlined",
            family.Value);

        // Material Symbols에 없는 두 글리프·말풍선·화이트보드는 사용자 벡터 유지.
        // 갈매기표는 보기의 아이콘 참조 고유성 계약 때문에 용도별 키로 같은 모양 반복.
        var customVectors = icons.Root!.Elements()
            .Where(element => element.Name.LocalName == "PathIconSource")
            .Select(element => (string?)element.Attribute(Xaml + "Key"))
            .ToArray();
        Assert.Equal(
            new[]
            {
                "Icon.Shape.SpeechBubble", "Icon.Shape.RoundedRectangle", "Icon.View.ActualSize",
                "Icon.File.Whiteboard", "Icon.Image.RegionSelect", "Icon.Common.ChevronDown",
                "Icon.Chevron.Small.Open", "Icon.Chevron.Small.Transform", "Icon.Chevron.Small.Crop",
                "Icon.Chevron.Small.Zoom", "Icon.Chevron.Small.Protect",
                "Icon.Chevron.Small.Filmstrip", "Icon.Image.RegionSelectFlat",
            },
            customVectors);

        // 실행 중 만든 페이지·재생 컨트롤은 정적 아이콘 원본 복제. 문자열 집합 취급 시 창 생성 실패.
        Assert.Contains("FontIconSource source => source.Glyph", code, StringComparison.Ordinal);

        // 뒤집기 글리프는 세로 변형이 없어 사용처에서 공유 글리프를 90도 회전.
        Assert.Contains(
            "IconSource=\"{StaticResource Icon.Image.FlipVertical}\" Style=\"{StaticResource ToolIconStyle}\" RenderTransformOrigin=\"0.5,0.5\"",
            viewer, StringComparison.Ordinal);
        Assert.Contains("<RotateTransform Angle=\"90\" />", viewer, StringComparison.Ordinal);

        // 출처는 google/material-design-icons@abd7f5c0. 자산이 바뀌면 여기서 실패.
        var ttf = RepoFile("EzyImageViewer.App", "Assets", "Fonts", "MaterialSymbolsOutlined.ttf");
        var license = RepoFile("EzyImageViewer.App", "Assets", "Fonts", "LICENSE-MaterialSymbols.txt");
        Assert.Equal(
            "6EB4B0BA0D788B9CFB4F22D68A768276142CBC3698177AC2803A0F1F1EB3207F",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ttf))));
        Assert.Equal(
            "58D1E17FFE5109A7AE296CAAFCADFDBE6A7D176F0BC4AB01E12A689B0499D8BD",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(license))));
        Assert.Contains(@"Assets\Fonts\MaterialSymbolsOutlined.ttf", csproj, StringComparison.Ordinal);
        Assert.Contains(@"Assets\Fonts\LICENSE-MaterialSymbols.txt", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void MaterialSymbolsSubset_ContainsEveryReferencedGlyph()
    {
        var icons = XDocument.Load(RepoFile("EzyImageViewer.App", "Resources", "Icons.xaml"));
        var glyphs = icons.Root!.Elements()
            .Where(element => element.Name.LocalName is "FontIconSource" or "String")
            .Select(element => element.Name.LocalName == "FontIconSource"
                ? (string?)element.Attribute("Glyph")
                : element.Value)
            .Where(glyph => !string.IsNullOrEmpty(glyph))
            .Select(glyph => Assert.Single(glyph!))
            .Distinct()
            .ToArray();

        using var typeface = SKTypeface.FromFile(
            RepoFile("EzyImageViewer.App", "Assets", "Fonts", "MaterialSymbolsOutlined.ttf"));
        Assert.NotNull(typeface);
        Assert.Equal("Material Symbols Outlined", typeface.FamilyName);

        using var font = new SKFont(typeface, 20);
        foreach (var glyph in glyphs)
        {
            var glyphId = font.GetGlyph(glyph);
            Assert.NotEqual(0, glyphId);
            using var path = font.GetGlyphPath(glyphId);
            Assert.NotNull(path);
            Assert.False(path.IsEmpty);
        }
    }

    [Fact]
    public void TooltipDescriptions_AreLocalizedAndExposeHelpText()
    {
        var strings = File.ReadAllText(RepoFile("EzyImageViewer.App", "AppStrings.cs"));
        var resw = XDocument.Load(RepoFile("EzyImageViewer.App", "Strings", "ko-KR", "Resources.resw"));
        var code = File.ReadAllText(RepoFile("EzyImageViewer.App", "Views", "ViewerWindow.xaml.cs"));
        var resourceNames = resw.Root!.Elements("data")
            .Select(element => (string?)element.Attribute("name"))
            .Where(name => name is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        var tips = Regex.Matches(strings, @"public static string (Tip\w+) =>")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.NotEmpty(tips);
        Assert.All(tips, tip => Assert.Contains(tip, resourceNames));
        Assert.Contains("$\"{title}\\n{description}\"", code, StringComparison.Ordinal);
        Assert.Contains("automationName ?? title", code, StringComparison.Ordinal);
        Assert.Contains("AppStrings.TipZoomSlider,\n            AppStrings.StatusZoom", code, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetHelpText(element, description)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void PaletteLayerAndResizeContracts_ArePresent()
    {
        var code = File.ReadAllText(RepoFile("EzyImageViewer.App", "Views", "ViewerWindow.xaml.cs"));
        var xaml = File.ReadAllText(RepoFile("EzyImageViewer.App", "Views", "ViewerWindow.xaml"));

        Assert.Contains("button.KeyDown += OnColorSwatchKeyDown", code, StringComparison.Ordinal);
        Assert.Contains("#{color.Argb & 0x00FF_FFFF:X6}", code, StringComparison.Ordinal);
        Assert.Contains("totalAnnotations > 0 || layers.Count > 1", code, StringComparison.Ordinal);
        Assert.Contains("ApplyLayerPanelCollapse();", code, StringComparison.Ordinal);
        Assert.Contains("Opacity = layer.IsVisible ? 1d : 0.6d", code, StringComparison.Ordinal);
        Assert.Contains("layerTransitionsExercised", code, StringComparison.Ordinal);
        Assert.Contains("AppWindow.Changed += OnAppWindowChanged", code, StringComparison.Ordinal);
        Assert.Contains("_observedXamlRoot.Changed += OnXamlRootChanged", code, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueuePriority.Low", code, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(300)", code, StringComparison.Ordinal);
        Assert.Contains("Canvas.EnableRenderLoop = true", code, StringComparison.Ordinal);
        Assert.Contains("Canvas.EnableRenderLoop = false", code, StringComparison.Ordinal);
        Assert.DoesNotContain("EnableRenderLoop=\"True\"", xaml, StringComparison.Ordinal);
    }

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
        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }
}
