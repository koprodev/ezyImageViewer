using System.Globalization;
using EzyImageViewer.Infrastructure;

namespace EzyImageViewer.App;

/// <summary>
/// Resources.resw에서 UI 문자열 조회(NFR-I18N).
/// PRI를 못 읽는 실행도 멀쩡히 보이도록 en-US 대체 문자열을 코드에 들고 있다.
/// </summary>
public static class AppStrings
{
    private static readonly object LoaderSync = new();
    private static Microsoft.Windows.ApplicationModel.Resources.ResourceLoader? _loader;
    // 언어를 정하기 전에 로더가 태어나면 안 된다. 준비 플래그를 volatile로 걸어 순서를 보장.
    private static volatile bool _loaderReady;

    private static Microsoft.Windows.ApplicationModel.Resources.ResourceLoader? Loader
    {
        get
        {
            if (_loaderReady)
                return _loader;
            lock (LoaderSync)
            {
                if (!_loaderReady)
                {
                    _loader = TryCreateLoader();
                    _loaderReady = true;
                }
            }
            return _loader;
        }
    }

    /// <summary>
    /// 저장된 UI 언어를 적용한다. 창을 만들기 전에 한 번 부른다.
    /// 빈 태그는 Windows 표시 언어로 되돌리라는 MRT 규약값이다.
    /// </summary>
    public static void ApplyLanguage(string? tag)
    {
        var selected = LanguagePolicy.IsSelectable(tag)
            ? tag ?? LanguagePolicy.SystemDefault
            : LanguagePolicy.SystemDefault;
        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = selected;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            // 재정의를 못 걸면 시스템 언어로 그냥 간다. 여기서 죽을 이유는 없다.
        }

        LanguagePolicy.EffectiveUiLanguage = selected.Length != 0 ? selected : ResolveSystemLanguage();
        // 표시 언어만 옮긴다. 숫자·날짜 서식은 Windows 지역 설정 몫이라 CurrentCulture는 그대로 둔다.
        TrySetUiCulture(LanguagePolicy.EffectiveUiLanguage);

        lock (LoaderSync)
        {
            _loader = null;
            _loaderReady = false;
        }
    }

    private static string ResolveSystemLanguage()
    {
        try
        {
            var languages = Microsoft.Windows.Globalization.ApplicationLanguages.Languages;
            if (languages.Count > 0)
                return languages[0];
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            // 아래 컬처 기반 추정으로 떨어진다.
        }
        return CultureInfo.CurrentUICulture.Name;
    }

    private static void TrySetUiCulture(string tag)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(tag);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
            // 알 수 없는 태그면 스레드 기본값 유지.
        }
    }

    private static Microsoft.Windows.ApplicationModel.Resources.ResourceLoader? TryCreateLoader()
    {
        try
        {
            return new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader();
        }
        catch
        {
            return null;
        }
    }

    private static string Get(string key, string fallback)
    {
        try
        {
            var value = Loader?.GetString(key);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
        catch
        {
            return fallback;
        }
    }

    public static string ToolOpen => Get("ToolOpen", "Open (Ctrl+O)");
    public static string ToolRecent => Get("ToolRecent", "Recent files");
    public static string TipRecent => Get("TipRecent", "Shows files you recently opened successfully.");
    public static string RecentTitle => Get("RecentTitle", "Recent files");
    public static string RecentEmpty => Get("RecentEmpty", "There are no recent files, or history is turned off.");
    public static string RecentOpen => Get("RecentOpen", "Open");
    public static string RecentClear => Get("RecentClear", "Clear all");
    public static string RecentCleared => Get("RecentCleared", "Recent file history cleared");
    public static string RecentClearFailed => Get("RecentClearFailed", "Could not clear recent file history");
    public static string RecentEnableBlocked => Get("RecentEnableBlocked", "History was not turned on because the existing recent file history could not be cleared");
    public static string RecentDisableIncomplete => Get("RecentDisableIncomplete", "Recent file history is off, but the existing history could not be cleared. Deletion will be retried the next time the app starts");
    public static string RecoveryTitle => Get("RecoveryTitle", "There is work to recover");
    public static string RecoveryBody => Get("RecoveryBody", "The previous session did not close normally. You can recover unsaved work.");
    public static string RecoveryIncompleteWarning => Get("RecoveryIncompleteWarning", "Some recovery items could not be read. Only the items listed below are handled; the rest are preserved.");
    public static string RecoveryRestoreAll => Get("RecoveryRestoreAll", "Recover all");
    public static string RecoveryDiscardAll => Get("RecoveryDiscardAll", "Discard all");
    public static string RecoveryDiscardVisible => Get("RecoveryDiscardVisible", "Discard listed items");
    public static string RecoveryDiscarded => Get("RecoveryDiscarded", "Discarded the listed recovery items");
    public static string RecoveryDiscardDeferred => Get("RecoveryDiscardDeferred", "Discarded the listed recovery items. Items that could not be read will be checked again at the next start");
    public static string RecoveryLater => Get("RecoveryLater", "Later");
    public static string RecoveryFailed => Get("RecoveryFailed", "Some work could not be recovered");
    public static string RecoveryRestored => Get("RecoveryRestored", "Your previous work was recovered. Please save it somewhere safe");
    public static string RecoveryAvailabilityTitle => Get("RecoveryAvailabilityTitle", "Autorecovery status");
    public static string RecoveryUnavailablePersistent => Get("RecoveryUnavailablePersistent", "Autorecovery could not be started. Please save your work often during this session.");
    public static string RecoveryDegradedPersistent => Get("RecoveryDegradedPersistent", "Autorecovery saving is having trouble. Please save your work manually until this notice disappears.");
    public static string AppDataProtectionTitle => Get("AppDataProtectionTitle", "Local data protection failed");
    public static string AppDataProtectionPersistent => Get("AppDataProtectionPersistent", "Your private data folder could not be secured, so settings, recent files, and autorecovery are disabled for this session.");
    public static string SafeModeTitle => Get("SafeModeTitle", "Start in safe mode?");
    public static string SafeModeBody => Get("SafeModeBody", "The same startup error happened repeatedly. Safe mode turns off clipboard watching, global capture, recent files, subfolder browsing, and autorecovery prompts for this session.");
    public static string SafeModeStart => Get("SafeModeStart", "Start in safe mode");
    public static string SafeModeContinue => Get("SafeModeContinue", "Continue normally");
    public static string SafeModeLabel => Get("SafeModeLabel", "Safe mode");
    public static string ToolClipboard => Get("ToolClipboard", "Open document from clipboard");
    public static string ToolWhiteboard => Get("ToolWhiteboard", "Open whiteboard");
    public static string TipWhiteboard => Get("TipWhiteboard", "Opens a 4K whiteboard with a grid background as a new document.");
    public static string WhiteboardWhite => Get("WhiteboardWhite", "White whiteboard");
    public static string WhiteboardBlack => Get("WhiteboardBlack", "Black whiteboard");
    public static string ToolNewWindow => Get("ToolNewWindow", "Open new window");
    public static string ToolSettings => Get("ToolSettings", "Settings");
    public static string TipSettings => Get("TipSettings", "Changes app behavior and privacy settings.");
    public static string SettingsTitle => Get("SettingsTitle", "Settings");
    public static string SettingsSave => Get("SettingsSave", "Save");
    public static string SettingsSaved => Get("SettingsSaved", "Settings saved");
    public static string SettingsSaveFailed => Get("SettingsSaveFailed", "Could not save settings");
    public static string SettingsTheme => Get("SettingsTheme", "Theme");
    public static string SettingsThemeSystem => Get("SettingsThemeSystem", "Use system setting");
    public static string SettingsThemeLight => Get("SettingsThemeLight", "Light");
    public static string SettingsThemeDark => Get("SettingsThemeDark", "Dark");
    public static string SettingsFileActivation => Get("SettingsFileActivation", "When opening another file");
    public static string SettingsReuseWindow => Get("SettingsReuseWindow", "Open in the current window");
    public static string SettingsOpenNewWindow => Get("SettingsOpenNewWindow", "Open in a new window");
    public static string SettingsClipboardWatch => Get("SettingsClipboardWatch", "Watch clipboard for captures");
    public static string SettingsRecentFiles => Get("SettingsRecentFiles", "Recent file history");
    public static string SettingsPrivacySummary => Get("SettingsPrivacySummary", "Your files and image content are never sent anywhere. There is no telemetry or automatic network communication.");
    public static string SettingsIncludeSubfolders => Get("SettingsIncludeSubfolders", "Include subfolders when browsing");
    public static string SettingsCaptureHotkey => Get("SettingsCaptureHotkey", "Global capture shortcut");
    public static string SettingsHotkeyInvalid => Get("SettingsHotkeyInvalid", "Choose at least one modifier key and assign a key.");
    public static string SettingsApplicationInformation => Get("SettingsApplicationInformation", "App info");
    public static string SettingsCurrentVersion => Get("SettingsCurrentVersion", "Current version: {0}");
    public static string SettingsNavGeneral => Get("SettingsNavGeneral", "Language and other settings");
    public static string SettingsNavToolbarGroups => Get("SettingsNavToolbarGroups", "Toolbar groups");
    public static string SettingsNavFileAssoc => Get("SettingsNavFileAssoc", "File associations");
    public static string SettingsNavAbout => Get("SettingsNavAbout", "About");
    public static string SettingsNavUpdate => Get("SettingsNavUpdate", "Updates");
    public static string SettingsNavSupport => Get("SettingsNavSupport", "Support the developer");
    public static string SettingsLanguage => Get("SettingsLanguage", "Language");
    public static string ToggleOn => Get("ToggleOn", "On");
    public static string ToggleOff => Get("ToggleOff", "Off");
    public static string DeleteImageTitle => Get("DeleteImageTitle", "Delete this image?");
    public static string DeleteImageRecycleBody => Get("DeleteImageRecycleBody",
        "'{0}' will be moved to the Recycle Bin. You can restore it from Windows.");
    public static string DeleteImagePermanentBody => Get("DeleteImagePermanentBody",
        "This location has no Recycle Bin, so '{0}' will be deleted permanently. This cannot be undone.");
    public static string DeleteImageUnsavedNote => Get("DeleteImageUnsavedNote",
        "This document has unsaved changes. They are not saved anywhere by deleting the file.");
    public static string DeleteImageConfirm => Get("DeleteImageConfirm", "Delete");
    public static string DeleteImageDone => Get("DeleteImageDone", "Moved '{0}' to the Recycle Bin");
    public static string DeleteImageDonePermanent => Get("DeleteImageDonePermanent", "Deleted '{0}'");
    public static string DeleteImageFailed => Get("DeleteImageFailed", "Could not delete the file");
    public static string RenameFileLabel => Get("RenameFileLabel", "File name");
    public static string RenameFileTip => Get("RenameFileTip", "Click or press F2 to rename this file.");
    public static string RenameDone => Get("RenameDone", "Renamed to '{0}'");
    public static string RenameFailed => Get("RenameFailed", "Could not rename the file");
    public static string RenameEmpty => Get("RenameEmpty", "Enter a file name.");
    public static string RenameInvalidCharacters => Get("RenameInvalidCharacters",
        "A file name cannot contain \\ / : * ? \" < > | or end with a space or a period.");
    public static string RenameReservedName => Get("RenameReservedName",
        "That name is reserved by Windows. Choose another one.");
    public static string RenameTooLong => Get("RenameTooLong", "That file name is too long.");
    public static string RenameTargetExists => Get("RenameTargetExists",
        "A file with that name already exists in this folder.");
    public static string SettingsLanguageSystem => Get("SettingsLanguageSystem", "System default (Windows display language)");
    public static string SettingsLanguageRestartNote => Get("SettingsLanguageRestartNote", "Restart the app to apply the new language everywhere.");
    public static string FileAssocWindowsSettings => Get("FileAssocWindowsSettings", "Windows default apps settings");
    public static string FileAssocPackagedNote => Get("FileAssocPackagedNote", "In this build the installation package manages file associations. The formats below are already registered as open-with candidates, so there is nothing to apply here. To use the app for double-clicks, select ezy Image Viewer under 'Windows default apps settings'.");
    public static string FileAssocPackagedRegistered => Get("FileAssocPackagedRegistered", "Formats registered by the package");
    public static string FilmstripLabel => Get("FilmstripLabel", "Folder thumbnail list");
    public static string FilmstripCurrent => Get("FilmstripCurrent", "Current file");
    public static string FilmstripShow => Get("FilmstripShow", "Show thumbnail list");
    public static string FilmstripHide => Get("FilmstripHide", "Hide thumbnail list");
    public static string TipFilmstrip => Get("TipFilmstrip", "Leave the list open to pick several images one after another.");
    public static string AboutDescription => Get("AboutDescription", "A light, fast image viewer and editor for Windows");
    public static string AboutLicense => Get("AboutLicense", "MIT License · © 2026 koprodev");
    public static string UpdateStoreManagedNote => Get("UpdateStoreManagedNote", "Updates are handled automatically by the Microsoft Store. You can also check your Store library yourself.");
    public static string SupportNote => Get("SupportNote", "ezy Image Viewer is free. If it helped you, consider supporting its development.");
    public static string SupportAction => Get("SupportAction", "Support development ☕");
    public static string LinkOpenFailed => Get("LinkOpenFailed", "Could not open the page");
    public static string StatusPreview => Get("StatusPreview", "Preview");
    public static string ToolPrevious => Get("ToolPrevious", "Previous file (←)");
    public static string ToolNext => Get("ToolNext", "Next file (→)");
    public static string ToolPreviousPage => Get("ToolPreviousPage", "Previous page (Page Up)");
    public static string ToolNextPage => Get("ToolNextPage", "Next page (Page Down)");
    public static string ToolFit => Get("ToolFit", "Fit to window (Ctrl+0)");
    public static string ToolActualSize => Get("ToolActualSize", "Actual size (Ctrl+1)");
    public static string ToolRotate => Get("ToolRotate", "Rotate document clockwise");
    public static string ToolRotateCcw => Get("ToolRotateCcw", "Rotate document counterclockwise");
    public static string ToolOpenGroup => Get("ToolOpenGroup", "Open menu");
    public static string TipOpenGroup => Get("TipOpenGroup", "Groups file, recent, clipboard, capture, whiteboard, and new window into one menu.");
    public static string ToolTransformGroup => Get("ToolTransformGroup", "Rotate and flip menu");
    public static string TipTransformGroup => Get("TipTransformGroup", "Groups clockwise and counterclockwise rotation with horizontal and vertical flips.");
    public static string ToolCropGroup => Get("ToolCropGroup", "Crop and resize menu");
    public static string TipCropGroup => Get("TipCropGroup", "Groups crop, crop ratio, and resize into one menu.");
    public static string ToolZoomGroup => Get("ToolZoomGroup", "Zoom menu");
    public static string TipZoomGroup => Get("TipZoomGroup", "Groups fit to window and actual size into one menu.");
    public static string ToolProtectGroup => Get("ToolProtectGroup", "Redaction menu");
    public static string TipProtectGroup => Get("TipProtectGroup", "Groups pixelate, blur, and blackout into one menu.");
    public static string MenuCrop => Get("MenuCrop", "Crop");
    public static string MenuCropRatio => Get("MenuCropRatio", "Crop ratio");
    public static string MenuResize => Get("MenuResize", "Resize");
    public static string SettingsToolbarGroups => Get("SettingsToolbarGroups", "Choose which toolbar buttons are collapsed into dropdown groups.");
    public static string SettingsToolbarGroupOpen => Get("SettingsToolbarGroupOpen", "Open group (open, recent, clipboard, capture, whiteboard, new window)");
    public static string SettingsToolbarGroupSelect => Get("SettingsToolbarGroupSelect", "Select split button (object / box selection)");
    public static string SettingsToolbarGroupTransform => Get("SettingsToolbarGroupTransform", "Rotate and flip group");
    public static string SettingsToolbarGroupCrop => Get("SettingsToolbarGroupCrop", "Crop and resize group (crop, ratio, resize)");
    public static string SettingsToolbarGroupZoom => Get("SettingsToolbarGroupZoom", "Zoom group (fit to window, actual size)");
    public static string SettingsToolbarGroupProtect => Get("SettingsToolbarGroupProtect", "Redaction group (pixelate, blur, blackout)");
    public static string ToolFullScreen => Get("ToolFullScreen", "Full screen (F11)");
    public static string ToolDockToggle => Get("ToolDockToggle", "Switch tool rail orientation");
    public static string ToolRail => Get("ToolRail", "Toolbar");
    public static string ToolSendToBack => Get("ToolSendToBack", "Send to back (Ctrl+Shift+[)");
    public static string ToolSendBackward => Get("ToolSendBackward", "Send backward (Ctrl+[)");
    public static string ToolBringForward => Get("ToolBringForward", "Bring forward (Ctrl+])");
    public static string ToolBringToFront => Get("ToolBringToFront", "Bring to front (Ctrl+Shift+])");
    public static string ToolDuplicate => Get("ToolDuplicate", "Duplicate selection (Ctrl+D)");
    public static string ToolEditText => Get("ToolEditText", "Edit selected text");
    public static string LayerPanel => Get("LayerPanel", "Layers");
    public static string LayerVisible => Get("LayerVisible", "Visible");
    public static string LayerHidden => Get("LayerHidden", "Hidden");
    public static string LayerLocked => Get("LayerLocked", "Locked");
    public static string LayerUnlocked => Get("LayerUnlocked", "Unlocked");
    public static string LayerTypeInk => Get("LayerTypeInk", "Pen");
    public static string LayerTypeHighlighter => Get("LayerTypeHighlighter", "Highlighter");
    public static string LayerTypeLine => Get("LayerTypeLine", "Line");
    public static string LayerTypeArrow => Get("LayerTypeArrow", "Arrow");
    public static string LayerTypeRectangle => Get("LayerTypeRectangle", "Shape");
    public static string LayerTypeEllipse => Get("LayerTypeEllipse", "Ellipse");
    public static string LayerTypeText => Get("LayerTypeText", "Text");
    public static string LayerTypeNumber => Get("LayerTypeNumber", "Number");
    public static string LayerTypeSpeechBubble => Get("LayerTypeSpeechBubble", "Speech bubble");
    public static string LayerTypeImage => Get("LayerTypeImage", "Image");
    public static string LayerTypeMosaic => Get("LayerTypeMosaic", "Pixelate");
    public static string LayerTypeBlur => Get("LayerTypeBlur", "Blur");
    public static string LayerTypeMask => Get("LayerTypeMask", "Blackout");
    public static string TextEditTitle => Get("TextEditTitle", "Edit text");
    public static string ToolColor => Get("ToolColor", "Choose drawing color");
    public static string ToolEyedropper => Get("ToolEyedropper", "Pick a color from the image on screen");
    public static string ToolZoomOut => Get("ToolZoomOut", "Zoom out");
    public static string ToolZoomIn => Get("ToolZoomIn", "Zoom in");
    public static string ToolSelect => Get("ToolSelect", "Select and move");
    public static string ToolSelectMode => Get("ToolSelectMode", "Change selection mode");
    public static string TipSelectMode => Get("TipSelectMode", "Chooses whether the select tool picks objects or draws a box selection.");
    public static string SelectModeRegion => Get("SelectModeRegion", "Box selection");
    public static string TipRegionSelect => Get("TipRegionSelect", "Selects a rectangular area of the background. Drag inside to lift and move it, or press Ctrl+X to cut.");
    public static string RegionReviewHint => Get("RegionReviewHint", "Selected area: drag inside = lift and move · Ctrl+X = cut · Ctrl+C = copy · Esc = cancel");
    public static string RegionCutDone => Get("RegionCutDone", "Cut the selected area to the clipboard");
    public static string RegionNeedsFullRes => Get("RegionNeedsFullRes", "Area editing is not available on a preview-resolution document");
    public static string ToolPen => Get("ToolPen", "Pen");
    public static string ToolHighlighter => Get("ToolHighlighter", "Highlighter");
    public static string ToolLine => Get("ToolLine", "Line");
    public static string ToolArrow => Get("ToolArrow", "Arrow");
    public static string ToolRectangle => Get("ToolRectangle", "Rectangle (drag to draw · Delete to remove)");
    public static string ToolRoundedRectangle => Get("ToolRoundedRectangle", "Rounded rectangle");
    public static string ToolEllipse => Get("ToolEllipse", "Ellipse");
    public static string ToolText => Get("ToolText", "Text box");
    public static string ToolNumber => Get("ToolNumber", "Number marker");
    public static string ToolSpeechBubble => Get("ToolSpeechBubble", "Speech bubble");
    public static string ToolMosaic => Get("ToolMosaic", "Pixelate");
    public static string TipMosaic => Get("TipMosaic", "Hides the dragged area behind blocks");
    public static string ToolBlur => Get("ToolBlur", "Blur");
    public static string TipBlur => Get("TipBlur", "Blurs the dragged area");
    public static string ToolMask => Get("ToolMask", "Blackout");
    public static string TipMask => Get("TipMask", "Covers the dragged area completely with a solid color");
    public static string ToolUndo => Get("ToolUndo", "Undo (Ctrl+Z)");
    public static string ToolRedo => Get("ToolRedo", "Redo (Ctrl+Y)");
    public static string ToolCrop => Get("ToolCrop", "Crop (drag, then press Enter or double-click inside to apply · Esc to cancel)");
    public static string ToolCropRatio => Get("ToolCropRatio", "Switch crop ratio (free / 1:1 / 4:3 / 16:9)");
    public static string ToolFlipHorizontal => Get("ToolFlipHorizontal", "Flip horizontally");
    public static string ToolFlipVertical => Get("ToolFlipVertical", "Flip vertically");
    public static string ToolResize => Get("ToolResize", "Resize");
    public static string CropRatioFree => Get("CropRatioFree", "Free");
    public static string CropReviewHint => Get("CropReviewHint", "Crop review: press Enter or double-click inside to apply · Esc to cancel");
    public static string ColorBlack => Get("ColorBlack", "Black");
    public static string ColorGray => Get("ColorGray", "Gray");
    public static string ColorSilver => Get("ColorSilver", "Silver");
    public static string ColorWhite => Get("ColorWhite", "White");
    public static string ColorRed => Get("ColorRed", "Red");
    public static string ColorOrange => Get("ColorOrange", "Orange");
    public static string ColorYellow => Get("ColorYellow", "Yellow");
    public static string ColorLime => Get("ColorLime", "Lime");
    public static string ColorGreen => Get("ColorGreen", "Green");
    public static string ColorTeal => Get("ColorTeal", "Teal");
    public static string ColorSky => Get("ColorSky", "Sky");
    public static string ColorBlue => Get("ColorBlue", "Blue");
    public static string ColorNavy => Get("ColorNavy", "Navy");
    public static string ColorPurple => Get("ColorPurple", "Purple");
    public static string ColorMagenta => Get("ColorMagenta", "Magenta");
    public static string ColorBrown => Get("ColorBrown", "Brown");
    public static string ColorSelected => Get("ColorSelected", "Selected");
    public static string StateReady => Get("StateReady", "Ready");
    public static string StateLoading => Get("StateLoading", "Loading…");
    public static string StateFailed => Get("StateFailed", "Open failed");
    public static string StateModified => Get("StateModified", "Modified");
    public static string DiscardTitle => Get("DiscardTitle", "Unsaved changes");
    public static string DiscardBody => Get("DiscardBody", "This document has unsaved changes. If you don't save, they will be lost.");
    public static string DiscardConfirm => Get("DiscardConfirm", "Discard changes");
    public static string DiscardCancel => Get("DiscardCancel", "Cancel");
    public static string AnimationEditTitle => Get("AnimationEditTitle", "Edit animation frame");
    public static string AnimationEditBody => Get("AnimationEditBody", "Continuing will stop playback and flatten the current frame into a still image.");
    public static string AnimationEditConfirm => Get("AnimationEditConfirm", "Edit current frame");
    public static string EditFailed => Get("EditFailed", "Edit failed");
    public static string DialogApply => Get("DialogApply", "Apply");
    public static string DialogCancel => Get("DialogCancel", "Cancel");
    public static string ResizeTitle => Get("ResizeTitle", "Resize");
    public static string ResizeWidthLabel => Get("ResizeWidthLabel", "Width (px)");
    public static string ResizeHeightLabel => Get("ResizeHeightLabel", "Height (px)");
    public static string ResizePercentLabel => Get("ResizePercentLabel", "Scale (%)");
    public static string ResizeKeepAspect => Get("ResizeKeepAspect", "Keep aspect ratio");
    public static string TextTitle => Get("TextTitle", "Add text");
    public static string SpeechBubbleTitle => Get("SpeechBubbleTitle", "Speech bubble text");
    public static string TextContentLabel => Get("TextContentLabel", "Content");
    public static string MarkerLimitReached => Get("MarkerLimitReached", "The number marker limit has been reached.");
    public static string StyleFill => Get("StyleFill", "Fill");
    public static string StyleBackground => Get("StyleBackground", "Background");
    public static string StyleBlockSize => Get("StyleBlockSize", "Block size");
    public static string StyleBlurSigma => Get("StyleBlurSigma", "Strength");
    public static string LayerCollapse => Get("LayerCollapse", "Collapse layers");
    public static string LayerExpand => Get("LayerExpand", "Expand layers");
    public static string TipLayerCollapse => Get("TipLayerCollapse", "Collapses or expands the layer list.");
    public static string StyleStrokeWidth => Get("StyleStrokeWidth", "Stroke width (px)");
    public static string StyleOpacity => Get("StyleOpacity", "Opacity (%)");
    public static string StyleFontSize => Get("StyleFontSize", "Font size");
    public static string StyleRotation => Get("StyleRotation", "Rotation (°)");
    public static string ToolCapture => Get("ToolCapture", "Screen capture");
    public static string TipCapture => Get("TipCapture", "Opens the Windows snipping tool and imports the result automatically");
    public static string CaptureNoticeTitle => Get("CaptureNoticeTitle", "A new capture was detected on the clipboard");
    public static string CaptureNoticeOpen => Get("CaptureNoticeOpen", "Open");
    public static string CaptureLaunchFailed => Get("CaptureLaunchFailed", "Could not open the snipping tool — capture with Win+Shift+S and it will be imported automatically");
    public static string CaptureHotkeyUnavailable => Get("CaptureHotkeyUnavailable", "The global capture shortcut ({0}) is already in use by another app");
    public static string CaptureFailed => Get("CaptureFailed", "The capture was not completed");
    public static string ToolSave => Get("ToolSave", "Save (Ctrl+S)");
    public static string TipSave => Get("TipSave", "Quick save · Save as is Ctrl+Shift+S");
    public static string SaveDone => Get("SaveDone", "Saved");
    public static string SaveDoneNoMetadata => Get("SaveDoneNoMetadata", "Saved · metadata excluded");
    public static string SaveNoChanges => Get("SaveNoChanges", "No changes to save");
    public static string SaveFailed => Get("SaveFailed", "Save failed");
    public static string SaveInProgress => Get("SaveInProgress", "Saving…");
    public static string SaveFullResUnavailable => Get("SaveFullResUnavailable", "The original could not be reloaded at full resolution, so it cannot be exported");
    public static string SaveSourceChanged => Get("SaveSourceChanged", "The source file changed on disk, so it cannot be saved. Please reopen the document");
    public static string SaveDefaultName => Get("SaveDefaultName", "Image");
    public static string CopyDone => Get("CopyDone", "Copied to the clipboard");

    public static string CopyRegionDone => Get("CopyRegionDone", "Copied the selected area to the clipboard");

    public static string CopyRegionStale => Get("CopyRegionStale", "The crop review area is no longer valid, so nothing was copied");
    public static string ExportOptionsTitle => Get("ExportOptionsTitle", "Export options");
    public static string ExportQualityLabel => Get("ExportQualityLabel", "Quality");
    public static string ExportLosslessLabel => Get("ExportLosslessLabel", "Lossless");
    public static string ExportKeepMetadataLabel => Get("ExportKeepMetadataLabel", "Keep metadata (sensitive entries such as location are always removed)");
    public static string OverwriteTitle => Get("OverwriteTitle", "Overwrite the original");
    public static string OverwriteBody => Get("OverwriteBody", "The original file will be overwritten with your edits. This cannot be undone.");
    public static string OverwriteConfirm => Get("OverwriteConfirm", "Overwrite");
    public static string DialogSaveButton => Get("DialogSaveButton", "Save");
    public static string DialogDontSaveButton => Get("DialogDontSaveButton", "Don't save");
    public static string ProjectTypeName => Get("ProjectTypeName", "ezyImage project");
    public static string StyleCornerRadius => Get("StyleCornerRadius", "Corner radius");
    public static string StyleArrowhead => Get("StyleArrowhead", "Arrowhead");
    public static string StyleFontFamily => Get("StyleFontFamily", "Font");
    public static string StyleBold => Get("StyleBold", "Bold");
    public static string StyleItalic => Get("StyleItalic", "Italic");
    public static string StyleAlignment => Get("StyleAlignment", "Text alignment");
    public static string ArrowheadOpen => Get("ArrowheadOpen", "Open arrowhead");
    public static string ArrowheadTriangle => Get("ArrowheadTriangle", "Triangle arrowhead");
    public static string AlignmentLeft => Get("AlignmentLeft", "Left");
    public static string AlignmentCenter => Get("AlignmentCenter", "Center");
    public static string AlignmentRight => Get("AlignmentRight", "Right");
    public static string StatusFile => Get("StatusFile", "File");
    public static string StatusPage => Get("StatusPage", "Page");
    public static string ToolPlayAnimation => Get("ToolPlayAnimation", "Play animation");
    public static string ToolPauseAnimation => Get("ToolPauseAnimation", "Pause animation");
    public static string StatusColorMode => Get("StatusColorMode", "Color mode");
    public static string StatusProgress => Get("StatusProgress", "Task progress");
    public static string StatusZoom => Get("StatusZoom", "Zoom level");
    public static string StatusDetails => Get("StatusDetails", "Image details");
    public static string CanvasName => Get("CanvasName", "Image editing canvas");
    public static string ToolContext => Get("ToolContext", "Tool properties");
    public static string ColorModeRgb8 => Get("ColorModeRgb8", "RGB 8-bit");
    public static string ColorModeRgba8 => Get("ColorModeRgba8", "RGBA 8-bit");
    public static string GroupFile => Get("GroupFile", "File tools");
    public static string GroupHistory => Get("GroupHistory", "History tools");
    public static string GroupImage => Get("GroupImage", "Image tools");
    public static string GroupDrawing => Get("GroupDrawing", "Drawing tools");
    public static string GroupShapes => Get("GroupShapes", "Shape tools");
    public static string GroupText => Get("GroupText", "Text tools");
    public static string GroupProtection => Get("GroupProtection", "Redaction tools");
    public static string GroupView => Get("GroupView", "View tools");
    public static string LayerShow => Get("LayerShow", "Show");
    public static string LayerHide => Get("LayerHide", "Hide");
    public static string LayerLock => Get("LayerLock", "Lock");
    public static string LayerUnlock => Get("LayerUnlock", "Unlock");
    public static string LayerDefaultName => Get("LayerDefaultName", "Layer");
    public static string LayerActive => Get("LayerActive", "Active layer");
    public static string LayerSetActive => Get("LayerSetActive", "Set as active layer");
    public static string ToolLayerPanel => Get("ToolLayerPanel", "Layer panel");
    public static string TipLayerPanel => Get("TipLayerPanel", "Shows or hides the layer panel.");
    public static string LayerAdd => Get("LayerAdd", "New layer");
    public static string TipLayerAdd => Get("TipLayerAdd", "Adds a new layer above the active layer.");
    public static string LayerDelete => Get("LayerDelete", "Delete layer");
    public static string TipLayerDelete => Get("TipLayerDelete", "Deletes the active layer and everything on it.");
    public static string LayerMoveUp => Get("LayerMoveUp", "Move layer up");
    public static string TipLayerMoveUp => Get("TipLayerMoveUp", "Moves the active layer one step forward.");
    public static string LayerMoveDown => Get("LayerMoveDown", "Move layer down");
    public static string TipLayerMoveDown => Get("TipLayerMoveDown", "Moves the active layer one step backward.");
    public static string LayerRename => Get("LayerRename", "Rename layer");
    public static string TipLayerRename => Get("TipLayerRename", "Changes the name of the active layer.");
    public static string LayerMoveSelection => Get("LayerMoveSelection", "Move selection to active layer");
    public static string TipLayerMoveSelection => Get("TipLayerMoveSelection", "Moves the selected objects to the top of the active layer.");
    public static string LayerBlockedHidden => Get("LayerBlockedHidden", "A hidden layer cannot be edited");
    public static string LayerBlockedLocked => Get("LayerBlockedLocked", "A locked layer cannot be edited");
    public static string TipOpen => Get("TipOpen", "Choose a supported image file and open it in this window.");
    public static string TipClipboard => Get("TipClipboard", "Opens the clipboard image as a new document.");
    public static string TipNewWindow => Get("TipNewWindow", "Opens one more independent empty viewer window.");
    public static string TipPrevious => Get("TipPrevious", "Goes to the previous image in the current folder.");
    public static string TipNext => Get("TipNext", "Goes to the next image in the current folder.");
    public static string TipFit => Get("TipFit", "Fits the whole image inside the canvas.");
    public static string TipActualSize => Get("TipActualSize", "Shows one image pixel as one screen pixel.");
    public static string TipRotate => Get("TipRotate", "Rotates the image or the selected objects clockwise.");
    public static string TipColor => Get("TipColor", "Picks the color used for drawing and for selected objects.");
    public static string TipEyedropper => Get("TipEyedropper", "Picks the displayed color of a single pixel from the composed image.");
    public static string TipZoomOut => Get("TipZoomOut", "Lowers the canvas zoom by one step.");
    public static string TipZoomIn => Get("TipZoomIn", "Raises the canvas zoom by one step.");
    public static string TipZoomSlider => Get("TipZoomSlider", "Adjusts the zoom with the slider or the arrow keys.");
    public static string TipFullScreen => Get("TipFullScreen", "Switches between full screen and windowed view.");
    public static string TipDockHorizontal => Get("TipDockHorizontal", "Moves the tool rail to a horizontal bar at the top of the window.");
    public static string TipDockVertical => Get("TipDockVertical", "Moves the tool rail to a vertical bar on the left of the window.");
    public static string TipSendToBack => Get("TipSendToBack", "Moves the selected objects behind all others.");
    public static string TipSendBackward => Get("TipSendBackward", "Moves the selected objects back one step.");
    public static string TipBringForward => Get("TipBringForward", "Moves the selected objects forward one step.");
    public static string TipBringToFront => Get("TipBringToFront", "Moves the selected objects in front of all others.");
    public static string TipDuplicate => Get("TipDuplicate", "Duplicates the selected objects and selects the new copies.");
    public static string TipEditText => Get("TipEditText", "Edits the content of the selected text object.");
    public static string TipSelect => Get("TipSelect", "Select objects to move, resize, and rotate them.");
    public static string TipPen => Get("TipPen", "Drag the pointer to draw a freehand curve.");
    public static string TipHighlighter => Get("TipHighlighter", "Draws a translucent freehand curve.");
    public static string TipLine => Get("TipLine", "Draws a straight line from the start point to the end point.");
    public static string TipArrow => Get("TipArrow", "Draws an arrow from the start point to the end point.");
    public static string TipRectangle => Get("TipRectangle", "Drag the pointer to draw a rectangle.");
    public static string TipRoundedRectangle => Get("TipRoundedRectangle", "Drag the pointer to draw a rounded rectangle.");
    public static string TipEllipse => Get("TipEllipse", "Drag the pointer to draw an ellipse.");
    public static string TipText => Get("TipText", "Mark out an area, then type your text.");
    public static string TipNumber => Get("TipNumber", "Places number markers that count up in order.");
    public static string TipSpeechBubble => Get("TipSpeechBubble", "Draws a text speech bubble. Use the tail handle to adjust where the tail points.");
    public static string TipUndo => Get("TipUndo", "Reverts the most recent edit by one step.");
    public static string TipRedo => Get("TipRedo", "Reapplies one reverted edit.");
    public static string TipCrop => Get("TipCrop", "Drag an area, then press Enter to apply or Esc to cancel.");
    public static string TipCropRatio => Get("TipCropRatio", "Each click cycles through free, 1:1, 4:3, and 16:9.");
    public static string TipFlipHorizontal => Get("TipFlipHorizontal", "Flips the image left to right.");
    public static string TipFlipVertical => Get("TipFlipVertical", "Flips the image top to bottom.");
    public static string TipResize => Get("TipResize", "Sets the output size in pixels or as a percentage.");
    public static string TipColorSwatch => Get("TipColorSwatch", "Press Enter to choose and use the arrow keys to move through the palette.");
}
