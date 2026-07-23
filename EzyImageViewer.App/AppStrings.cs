namespace EzyImageViewer.App;

/// <summary>
/// UI strings resolved from Resources.resw (NFR-I18N), with hardcoded ko-KR fallbacks so
/// unpackaged runs without a PRI still show correct text.
/// </summary>
public static class AppStrings
{
    private static readonly Microsoft.Windows.ApplicationModel.Resources.ResourceLoader? Loader = TryCreateLoader();

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

    public static string ToolOpen => Get("ToolOpen", "열기 (Ctrl+O)");
    public static string ToolRecent => Get("ToolRecent", "최근 파일");
    public static string TipRecent => Get("TipRecent", "최근에 정상적으로 연 파일을 표시합니다.");
    public static string RecentTitle => Get("RecentTitle", "최근 파일");
    public static string RecentEmpty => Get("RecentEmpty", "최근 파일이 없거나 기록이 꺼져 있습니다.");
    public static string RecentOpen => Get("RecentOpen", "열기");
    public static string RecentClear => Get("RecentClear", "모두 지우기");
    public static string RecentCleared => Get("RecentCleared", "최근 파일 기록을 지웠습니다");
    public static string RecentClearFailed => Get("RecentClearFailed", "최근 파일 기록을 지우지 못했습니다");
    public static string RecentEnableBlocked => Get("RecentEnableBlocked",
        "기존 최근 파일 기록을 지우지 못해 기록을 켜지 않았습니다");
    public static string RecentDisableIncomplete => Get("RecentDisableIncomplete",
        "최근 파일 기록은 껐지만 기존 기록을 지우지 못했습니다. 앱을 다시 시작하면 삭제를 다시 시도합니다");
    public static string RecoveryTitle => Get("RecoveryTitle", "복구할 작업이 있습니다");
    public static string RecoveryBody => Get("RecoveryBody", "이전 실행이 정상 종료되지 않았습니다. 저장되지 않은 작업을 복구할 수 있습니다.");
    public static string RecoveryIncompleteWarning => Get("RecoveryIncompleteWarning",
        "일부 복구 항목을 읽지 못했습니다. 아래에 표시된 항목만 처리하며 보이지 않는 항목은 보존합니다.");
    public static string RecoveryRestoreAll => Get("RecoveryRestoreAll", "모두 복구");
    public static string RecoveryDiscardAll => Get("RecoveryDiscardAll", "모두 버리기");
    public static string RecoveryDiscardVisible => Get("RecoveryDiscardVisible", "표시된 항목 버리기");
    public static string RecoveryDiscarded => Get("RecoveryDiscarded", "표시된 복구 작업을 버렸습니다");
    public static string RecoveryDiscardDeferred => Get("RecoveryDiscardDeferred",
        "표시된 복구 작업을 버렸습니다. 읽지 못한 항목은 다음 시작 때 다시 확인합니다");
    public static string RecoveryLater => Get("RecoveryLater", "나중에");
    public static string RecoveryFailed => Get("RecoveryFailed", "일부 작업을 복구하지 못했습니다");
    public static string RecoveryRestored => Get("RecoveryRestored", "이전 작업을 복구했습니다. 안전한 위치에 저장해 주세요");
    public static string RecoveryAvailabilityTitle => Get("RecoveryAvailabilityTitle", "자동 복구 상태");
    public static string RecoveryUnavailablePersistent => Get("RecoveryUnavailablePersistent",
        "자동 복구를 시작하지 못했습니다. 이번 실행에서는 작업을 자주 직접 저장해 주세요.");
    public static string RecoveryDegradedPersistent => Get("RecoveryDegradedPersistent",
        "자동 복구 저장에 문제가 있습니다. 이 안내가 사라질 때까지 작업을 직접 저장해 주세요.");
    public static string AppDataProtectionTitle => Get("AppDataProtectionTitle", "로컬 데이터 보호 실패");
    public static string AppDataProtectionPersistent => Get("AppDataProtectionPersistent",
        "개인 데이터 폴더를 안전하게 보호하지 못해 설정, 최근 파일, 자동 복구를 이번 실행에서 비활성화했습니다.");
    public static string SafeModeTitle => Get("SafeModeTitle", "안전 모드로 시작할까요?");
    public static string SafeModeBody => Get("SafeModeBody",
        "같은 시작 오류가 반복되었습니다. 안전 모드는 클립보드 감시, 전역 캡처, 최근 파일, 하위 폴더 탐색과 자동 복구 열기를 이번 실행에서 끕니다.");
    public static string SafeModeStart => Get("SafeModeStart", "안전 모드로 시작");
    public static string SafeModeContinue => Get("SafeModeContinue", "일반 모드 계속");
    public static string SafeModeLabel => Get("SafeModeLabel", "안전 모드");
    public static string ToolClipboard => Get("ToolClipboard", "클립보드에서 문서 열기");
    public static string ToolWhiteboard => Get("ToolWhiteboard", "화이트보드 열기");
    public static string TipWhiteboard => Get("TipWhiteboard", "격자 배경의 4K 화이트보드를 새 문서로 엽니다.");
    public static string WhiteboardWhite => Get("WhiteboardWhite", "흰색 화이트보드");
    public static string WhiteboardBlack => Get("WhiteboardBlack", "검은색 화이트보드");
    public static string ToolNewWindow => Get("ToolNewWindow", "새 창 열기");
    public static string ToolSettings => Get("ToolSettings", "설정");
    public static string TipSettings => Get("TipSettings", "앱 동작과 개인정보 설정을 변경합니다.");
    public static string SettingsTitle => Get("SettingsTitle", "설정");
    public static string SettingsSave => Get("SettingsSave", "저장");
    public static string SettingsSaved => Get("SettingsSaved", "설정을 저장했습니다");
    public static string SettingsSaveFailed => Get("SettingsSaveFailed", "설정을 저장하지 못했습니다");
    public static string SettingsTheme => Get("SettingsTheme", "테마");
    public static string SettingsThemeSystem => Get("SettingsThemeSystem", "시스템 설정 사용");
    public static string SettingsThemeLight => Get("SettingsThemeLight", "밝게");
    public static string SettingsThemeDark => Get("SettingsThemeDark", "어둡게");
    public static string SettingsFileActivation => Get("SettingsFileActivation", "다른 파일을 열 때");
    public static string SettingsReuseWindow => Get("SettingsReuseWindow", "현재 창에서 열기");
    public static string SettingsOpenNewWindow => Get("SettingsOpenNewWindow", "새 창에서 열기");
    public static string SettingsClipboardWatch => Get("SettingsClipboardWatch", "클립보드 캡처 감시");
    public static string SettingsRecentFiles => Get("SettingsRecentFiles", "최근 파일 기록");
    public static string SettingsPrivacySummary => Get("SettingsPrivacySummary",
        "파일과 이미지 내용은 외부로 전송하지 않습니다. 원격 분석과 자동 업데이트 확인은 사용하지 않습니다.");
    public static string SettingsIncludeSubfolders => Get("SettingsIncludeSubfolders", "폴더 탐색에 하위 폴더 포함");
    public static string SettingsCaptureHotkey => Get("SettingsCaptureHotkey", "전역 캡처 단축키");
    public static string SettingsHotkeyInvalid => Get("SettingsHotkeyInvalid", "보조 키를 하나 이상 선택하고 키를 지정하세요.");
    public static string SettingsApplicationInformation => Get("SettingsApplicationInformation", "앱 정보");
    public static string SettingsCurrentVersion => Get("SettingsCurrentVersion", "현재 버전: {0}");
    public static string SettingsCheckForUpdates => Get("SettingsCheckForUpdates", "업데이트 확인");
    public static string SettingsNavGeneral => Get("SettingsNavGeneral", "언어 및 기타 설정");
    public static string SettingsNavToolbarGroups => Get("SettingsNavToolbarGroups", "툴바 그룹 설정");
    public static string SettingsNavFileAssoc => Get("SettingsNavFileAssoc", "파일 연결");
    public static string SettingsNavAbout => Get("SettingsNavAbout", "프로그램 소개");
    public static string SettingsNavUpdate => Get("SettingsNavUpdate", "프로그램 업데이트");
    public static string SettingsNavSupport => Get("SettingsNavSupport", "개발자 지원");
    public static string SettingsLanguage => Get("SettingsLanguage", "언어");
    public static string SettingsLanguageKorean => Get("SettingsLanguageKorean", "한국어");
    public static string SettingsLanguageNote => Get("SettingsLanguageNote",
        "현재 버전은 한국어를 제공합니다. 영어는 이후 버전에서 지원할 예정입니다.");
    public static string FileAssocDescription => Get("FileAssocDescription",
        "선택한 확장자를 이 앱의 '연결 프로그램' 후보로 등록합니다. 기존 기본 앱은 변경하지 않습니다.");
    public static string FileAssocWindowsSettings => Get("FileAssocWindowsSettings", "Windows 기본 프로그램 설정");
    public static string FileAssocSelectEssential => Get("FileAssocSelectEssential", "필수 파일 선택");
    public static string FileAssocSelectAll => Get("FileAssocSelectAll", "전부 선택");
    public static string FileAssocSelectNone => Get("FileAssocSelectNone", "선택 안 함");
    public static string FileAssocApply => Get("FileAssocApply", "지금 적용");
    public static string FileAssocApplied => Get("FileAssocApplied", "파일 연결을 적용했습니다");
    public static string FileAssocApplyFailed => Get("FileAssocApplyFailed", "파일 연결을 적용하지 못했습니다");
    public static string FileAssocUnavailable => Get("FileAssocUnavailable",
        "파일 연결 정보를 읽지 못해 이 페이지를 사용할 수 없습니다");
    public static string FileAssocGroupRaster => Get("FileAssocGroupRaster", "래스터 이미지");
    public static string FileAssocGroupCodec => Get("FileAssocGroupCodec", "코덱 확장 형식 (AVIF·HEIC)");
    public static string FileAssocGroupVector => Get("FileAssocGroupVector", "벡터 이미지");
    public static string AboutDescription => Get("AboutDescription", "가볍고 빠른 Windows 이미지 뷰어·편집기");
    public static string AboutLicense => Get("AboutLicense", "MIT 라이선스 · © 2026 koprodev");
    public static string AboutProjectPage => Get("AboutProjectPage", "GitHub에서 릴리스 보기");
    public static string UpdatePolicyNote => Get("UpdatePolicyNote",
        "자동 업데이트 확인은 수행하지 않습니다. 아래 버튼은 최신 릴리스 페이지를 기본 브라우저로 엽니다.");
    public static string SupportNote => Get("SupportNote",
        "ezy Image Viewer는 무료로 제공됩니다. 도움이 되었다면 개발을 응원해 주세요.");
    public static string SupportAction => Get("SupportAction", "개발 응원하기 ☕");
    public static string LinkOpenFailed => Get("LinkOpenFailed", "페이지를 열 수 없습니다");
    public static string StatusPreview => Get("StatusPreview", "미리보기");
    public static string UpdateOpenFailed => Get("UpdateOpenFailed", "릴리스 페이지를 열 수 없습니다");
    public static string ToolPrevious => Get("ToolPrevious", "이전 파일 (←)");
    public static string ToolNext => Get("ToolNext", "다음 파일 (→)");
    public static string ToolPreviousPage => Get("ToolPreviousPage", "이전 페이지 (Page Up)");
    public static string ToolNextPage => Get("ToolNextPage", "다음 페이지 (Page Down)");
    public static string ToolFit => Get("ToolFit", "화면 맞춤 (Ctrl+0)");
    public static string ToolActualSize => Get("ToolActualSize", "실제 크기 (Ctrl+1)");
    public static string ToolRotate => Get("ToolRotate", "시계 방향으로 문서 회전");
    public static string ToolRotateCcw => Get("ToolRotateCcw", "반시계 방향으로 문서 회전");
    public static string ToolOpenGroup => Get("ToolOpenGroup", "열기 메뉴");
    public static string TipOpenGroup => Get("TipOpenGroup",
        "파일·최근·클립보드·캡처·화이트보드·새 창 열기를 한 메뉴로 엽니다.");
    public static string ToolTransformGroup => Get("ToolTransformGroup", "회전·반전 메뉴");
    public static string TipTransformGroup => Get("TipTransformGroup",
        "시계·반시계 회전과 좌우·상하 반전을 한 메뉴로 엽니다.");
    public static string ToolCropGroup => Get("ToolCropGroup", "자르기·크기 메뉴");
    public static string TipCropGroup => Get("TipCropGroup",
        "자르기, 자르기 비율, 크기 조절을 한 메뉴로 엽니다.");
    public static string ToolZoomGroup => Get("ToolZoomGroup", "배율 메뉴");
    public static string TipZoomGroup => Get("TipZoomGroup",
        "화면 맞춤과 실제 크기를 한 메뉴로 엽니다.");
    public static string ToolProtectGroup => Get("ToolProtectGroup", "정보 보호 메뉴");
    public static string TipProtectGroup => Get("TipProtectGroup",
        "모자이크, 블러, 가림막을 한 메뉴로 엽니다.");
    public static string MenuCrop => Get("MenuCrop", "자르기");
    public static string MenuCropRatio => Get("MenuCropRatio", "자르기 비율");
    public static string MenuResize => Get("MenuResize", "크기 조절");
    public static string SettingsToolbarGroups => Get("SettingsToolbarGroups",
        "그룹별로 툴바 묶음 버튼(드롭다운) 사용 여부를 선택합니다.");
    public static string SettingsToolbarGroupOpen => Get("SettingsToolbarGroupOpen",
        "열기 묶음 (열기·최근·클립보드·캡처·화이트보드·새 창)");
    public static string SettingsToolbarGroupSelect => Get("SettingsToolbarGroupSelect",
        "선택 분할 버튼 (객체/박스형 선택)");
    public static string SettingsToolbarGroupTransform => Get("SettingsToolbarGroupTransform",
        "회전·반전 묶음");
    public static string SettingsToolbarGroupCrop => Get("SettingsToolbarGroupCrop",
        "자르기·크기 묶음 (자르기·비율·크기 조절)");
    public static string SettingsToolbarGroupZoom => Get("SettingsToolbarGroupZoom",
        "배율 묶음 (화면 맞춤·실제 크기)");
    public static string SettingsToolbarGroupProtect => Get("SettingsToolbarGroupProtect",
        "정보 보호 묶음 (모자이크·블러·가림막)");
    public static string ToolFullScreen => Get("ToolFullScreen", "전체 화면 (F11)");
    public static string ToolDockToggle => Get("ToolDockToggle", "도구 레일 방향 전환");
    public static string ToolRail => Get("ToolRail", "도구 모음");
    public static string ToolSendToBack => Get("ToolSendToBack", "맨 뒤로 보내기 (Ctrl+Shift+[)");
    public static string ToolSendBackward => Get("ToolSendBackward", "한 단계 뒤로 (Ctrl+[)");
    public static string ToolBringForward => Get("ToolBringForward", "한 단계 앞으로 (Ctrl+])");
    public static string ToolBringToFront => Get("ToolBringToFront", "맨 앞으로 가져오기 (Ctrl+Shift+])");
    public static string ToolDuplicate => Get("ToolDuplicate", "선택 객체 복제 (Ctrl+D)");
    public static string ToolEditText => Get("ToolEditText", "선택 텍스트 편집");
    public static string LayerPanel => Get("LayerPanel", "레이어");
    public static string LayerVisible => Get("LayerVisible", "표시");
    public static string LayerHidden => Get("LayerHidden", "숨김");
    public static string LayerLocked => Get("LayerLocked", "잠김");
    public static string LayerUnlocked => Get("LayerUnlocked", "잠금 해제");
    public static string LayerTypeInk => Get("LayerTypeInk", "펜");
    public static string LayerTypeHighlighter => Get("LayerTypeHighlighter", "형광펜");
    public static string LayerTypeLine => Get("LayerTypeLine", "직선");
    public static string LayerTypeArrow => Get("LayerTypeArrow", "화살표");
    public static string LayerTypeRectangle => Get("LayerTypeRectangle", "도형");
    public static string LayerTypeEllipse => Get("LayerTypeEllipse", "타원");
    public static string LayerTypeText => Get("LayerTypeText", "텍스트");
    public static string LayerTypeNumber => Get("LayerTypeNumber", "번호");
    public static string LayerTypeSpeechBubble => Get("LayerTypeSpeechBubble", "말풍선");
    public static string LayerTypeImage => Get("LayerTypeImage", "이미지");
    public static string LayerTypeMosaic => Get("LayerTypeMosaic", "모자이크");
    public static string LayerTypeBlur => Get("LayerTypeBlur", "블러");
    public static string LayerTypeMask => Get("LayerTypeMask", "가림막");
    public static string TextEditTitle => Get("TextEditTitle", "텍스트 편집");
    public static string ToolColor => Get("ToolColor", "그리기 색 선택");
    public static string ToolEyedropper => Get("ToolEyedropper", "화면 이미지에서 색 추출");
    public static string ToolZoomOut => Get("ToolZoomOut", "축소");
    public static string ToolZoomIn => Get("ToolZoomIn", "확대");
    public static string ToolSelect => Get("ToolSelect", "선택 및 이동");
    public static string ToolSelectMode => Get("ToolSelectMode", "선택 모드 변경");
    public static string TipSelectMode => Get("TipSelectMode", "객체 선택과 박스형 선택 중 선택 도구의 동작을 고릅니다.");
    public static string SelectModeRegion => Get("SelectModeRegion", "박스형 선택");
    public static string TipRegionSelect => Get("TipRegionSelect",
        "배경에서 사각 영역을 선택합니다. 안쪽을 드래그하면 들어올려 이동하고, Ctrl+X로 잘라냅니다.");
    public static string RegionReviewHint => Get("RegionReviewHint",
        "선택 영역: 안쪽 드래그 = 들어올려 이동 · Ctrl+X = 잘라내기 · Ctrl+C = 복사 · Esc = 취소");
    public static string RegionCutDone => Get("RegionCutDone", "선택 영역을 클립보드로 잘라냈습니다");
    public static string RegionNeedsFullRes => Get("RegionNeedsFullRes",
        "미리보기 해상도 문서에서는 영역 편집을 사용할 수 없습니다");
    public static string ToolPen => Get("ToolPen", "펜");
    public static string ToolHighlighter => Get("ToolHighlighter", "형광펜");
    public static string ToolLine => Get("ToolLine", "직선");
    public static string ToolArrow => Get("ToolArrow", "화살표");
    public static string ToolRectangle => Get("ToolRectangle", "사각형 (드래그로 그리기 · Delete 로 삭제)");
    public static string ToolRoundedRectangle => Get("ToolRoundedRectangle", "둥근 사각형");
    public static string ToolEllipse => Get("ToolEllipse", "타원");
    public static string ToolText => Get("ToolText", "텍스트 상자");
    public static string ToolNumber => Get("ToolNumber", "번호 마커");
    public static string ToolSpeechBubble => Get("ToolSpeechBubble", "말풍선");
    public static string ToolMosaic => Get("ToolMosaic", "모자이크");
    public static string TipMosaic => Get("TipMosaic", "드래그한 영역을 블록으로 가립니다");
    public static string ToolBlur => Get("ToolBlur", "블러");
    public static string TipBlur => Get("TipBlur", "드래그한 영역을 흐리게 가립니다");
    public static string ToolMask => Get("ToolMask", "가림막");
    public static string TipMask => Get("TipMask", "드래그한 영역을 단색으로 완전히 가립니다");
    public static string ToolUndo => Get("ToolUndo", "실행 취소 (Ctrl+Z)");
    public static string ToolRedo => Get("ToolRedo", "다시 실행 (Ctrl+Y)");
    public static string ToolCrop => Get("ToolCrop", "자르기 (드래그 후 Enter 또는 영역 안 더블클릭으로 적용 · Esc 취소)");
    public static string ToolCropRatio => Get("ToolCropRatio", "자르기 비율 전환 (자유/1:1/4:3/16:9)");
    public static string ToolFlipHorizontal => Get("ToolFlipHorizontal", "좌우 반전");
    public static string ToolFlipVertical => Get("ToolFlipVertical", "상하 반전");
    public static string ToolResize => Get("ToolResize", "크기 조절");
    public static string CropRatioFree => Get("CropRatioFree", "자유");
    public static string CropReviewHint => Get("CropReviewHint", "자르기 검토: Enter 또는 영역 안 더블클릭으로 적용 · Esc로 취소");
    public static string ColorBlack => Get("ColorBlack", "검정");
    public static string ColorGray => Get("ColorGray", "회색");
    public static string ColorSilver => Get("ColorSilver", "은색");
    public static string ColorWhite => Get("ColorWhite", "흰색");
    public static string ColorRed => Get("ColorRed", "빨강");
    public static string ColorOrange => Get("ColorOrange", "주황");
    public static string ColorYellow => Get("ColorYellow", "노랑");
    public static string ColorLime => Get("ColorLime", "연두");
    public static string ColorGreen => Get("ColorGreen", "초록");
    public static string ColorTeal => Get("ColorTeal", "청록");
    public static string ColorSky => Get("ColorSky", "하늘");
    public static string ColorBlue => Get("ColorBlue", "파랑");
    public static string ColorNavy => Get("ColorNavy", "남색");
    public static string ColorPurple => Get("ColorPurple", "보라");
    public static string ColorMagenta => Get("ColorMagenta", "자홍");
    public static string ColorBrown => Get("ColorBrown", "갈색");
    public static string ColorSelected => Get("ColorSelected", "선택됨");
    public static string StateReady => Get("StateReady", "준비됨");
    public static string StateLoading => Get("StateLoading", "불러오는 중…");
    public static string StateFailed => Get("StateFailed", "열기 실패");
    public static string StateModified => Get("StateModified", "수정됨");
    public static string DiscardTitle => Get("DiscardTitle", "저장되지 않은 변경 사항");
    public static string DiscardBody => Get("DiscardBody",
        "이 문서에 저장되지 않은 변경 사항이 있습니다. 저장하지 않으면 변경 사항이 사라집니다.");
    public static string DiscardConfirm => Get("DiscardConfirm", "변경 사항 버리기");
    public static string DiscardCancel => Get("DiscardCancel", "취소");
    public static string AnimationEditTitle => Get("AnimationEditTitle", "애니메이션 프레임 편집");
    public static string AnimationEditBody => Get("AnimationEditBody",
        "편집을 계속하면 애니메이션 재생을 중지하고 현재 프레임을 정적 이미지로 평면화합니다.");
    public static string AnimationEditConfirm => Get("AnimationEditConfirm", "현재 프레임 편집");
    public static string EditFailed => Get("EditFailed", "편집 실패");
    public static string DialogApply => Get("DialogApply", "적용");
    public static string DialogCancel => Get("DialogCancel", "취소");
    public static string ResizeTitle => Get("ResizeTitle", "크기 조절");
    public static string ResizeWidthLabel => Get("ResizeWidthLabel", "너비 (px)");
    public static string ResizeHeightLabel => Get("ResizeHeightLabel", "높이 (px)");
    public static string ResizePercentLabel => Get("ResizePercentLabel", "배율 (%)");
    public static string ResizeKeepAspect => Get("ResizeKeepAspect", "종횡비 유지");
    public static string TextTitle => Get("TextTitle", "텍스트 추가");
    public static string SpeechBubbleTitle => Get("SpeechBubbleTitle", "말풍선 텍스트");
    public static string TextContentLabel => Get("TextContentLabel", "내용");
    public static string MarkerLimitReached => Get("MarkerLimitReached", "번호 마커 한도에 도달했습니다.");
    public static string StyleFill => Get("StyleFill", "채우기");
    public static string StyleBackground => Get("StyleBackground", "배경");
    public static string StyleBlockSize => Get("StyleBlockSize", "블록 크기");
    public static string StyleBlurSigma => Get("StyleBlurSigma", "강도");
    public static string LayerCollapse => Get("LayerCollapse", "레이어 접기");
    public static string LayerExpand => Get("LayerExpand", "레이어 펼치기");
    public static string TipLayerCollapse => Get("TipLayerCollapse", "레이어 목록을 접거나 펼칩니다.");
    public static string StyleStrokeWidth => Get("StyleStrokeWidth", "선 굵기(px)");
    public static string StyleOpacity => Get("StyleOpacity", "불투명도(%)");
    public static string StyleFontSize => Get("StyleFontSize", "글자 크기");
    public static string StyleRotation => Get("StyleRotation", "회전(°)");
    public static string ToolCapture => Get("ToolCapture", "화면 캡처");
    public static string TipCapture => Get("TipCapture", "Windows 캡처 도구를 열고 결과를 자동으로 가져옵니다");
    public static string CaptureNoticeTitle => Get("CaptureNoticeTitle", "클립보드에서 새 캡처를 감지했습니다");
    public static string CaptureNoticeOpen => Get("CaptureNoticeOpen", "열기");
    public static string CaptureLaunchFailed => Get("CaptureLaunchFailed",
        "캡처 도구를 열 수 없습니다 — Win+Shift+S로 캡처하면 자동으로 가져옵니다");
    public static string CaptureHotkeyUnavailable => Get("CaptureHotkeyUnavailable",
        "전역 캡처 단축키({0})를 다른 앱이 사용 중입니다");
    public static string CaptureFailed => Get("CaptureFailed", "캡처가 완료되지 않았습니다");
    public static string TrayWatchToggle => Get("TrayWatchToggle", "클립보드 캡처 감시");
    public static string TrayCapture => Get("TrayCapture", "화면 캡처");
    public static string TrayOpenWindow => Get("TrayOpenWindow", "창 열기");
    public static string ToolSave => Get("ToolSave", "저장 (Ctrl+S)");
    public static string TipSave => Get("TipSave", "빠른 저장 · 다른 이름으로 저장은 Ctrl+Shift+S");
    public static string SaveDone => Get("SaveDone", "저장했습니다");
    public static string SaveDoneNoMetadata => Get("SaveDoneNoMetadata", "저장했습니다 · 메타데이터는 제외됨");
    public static string SaveNoChanges => Get("SaveNoChanges", "변경 내용이 없습니다");
    public static string SaveFailed => Get("SaveFailed", "저장 실패");
    public static string SaveInProgress => Get("SaveInProgress", "저장하는 중…");
    public static string SaveFullResUnavailable => Get("SaveFullResUnavailable",
        "원본을 전체 해상도로 다시 읽을 수 없어 내보낼 수 없습니다");
    public static string SaveSourceChanged => Get("SaveSourceChanged",
        "원본 파일이 디스크에서 바뀌어 저장할 수 없습니다. 문서를 다시 열어 주세요");
    public static string SaveDefaultName => Get("SaveDefaultName", "이미지");
    public static string CopyDone => Get("CopyDone", "클립보드에 복사했습니다");

    public static string CopyRegionDone => Get("CopyRegionDone", "선택 영역을 클립보드에 복사했습니다");

    public static string CopyRegionStale => Get("CopyRegionStale", "자르기 검토 영역이 더 이상 유효하지 않아 복사하지 않았습니다");
    public static string ExportOptionsTitle => Get("ExportOptionsTitle", "내보내기 설정");
    public static string ExportQualityLabel => Get("ExportQualityLabel", "품질");
    public static string ExportLosslessLabel => Get("ExportLosslessLabel", "무손실");
    public static string ExportKeepMetadataLabel => Get("ExportKeepMetadataLabel",
        "메타데이터 유지 (위치 정보 등 민감 항목은 항상 제거)");
    public static string OverwriteTitle => Get("OverwriteTitle", "원본 덮어쓰기");
    public static string OverwriteBody => Get("OverwriteBody", "원본 파일을 편집 결과로 덮어씁니다. 되돌릴 수 없습니다.");
    public static string OverwriteConfirm => Get("OverwriteConfirm", "덮어쓰기");
    public static string DialogSaveButton => Get("DialogSaveButton", "저장");
    public static string DialogDontSaveButton => Get("DialogDontSaveButton", "저장 안 함");
    public static string ProjectTypeName => Get("ProjectTypeName", "ezyImage 프로젝트");
    public static string StyleCornerRadius => Get("StyleCornerRadius", "모서리 반경");
    public static string StyleArrowhead => Get("StyleArrowhead", "화살촉");
    public static string StyleFontFamily => Get("StyleFontFamily", "글꼴");
    public static string StyleBold => Get("StyleBold", "굵게");
    public static string StyleItalic => Get("StyleItalic", "기울임");
    public static string StyleAlignment => Get("StyleAlignment", "텍스트 정렬");
    public static string ArrowheadOpen => Get("ArrowheadOpen", "열린 화살촉");
    public static string ArrowheadTriangle => Get("ArrowheadTriangle", "삼각 화살촉");
    public static string AlignmentLeft => Get("AlignmentLeft", "왼쪽");
    public static string AlignmentCenter => Get("AlignmentCenter", "가운데");
    public static string AlignmentRight => Get("AlignmentRight", "오른쪽");
    public static string StatusFile => Get("StatusFile", "파일");
    public static string StatusPage => Get("StatusPage", "페이지");
    public static string ToolPlayAnimation => Get("ToolPlayAnimation", "애니메이션 재생");
    public static string ToolPauseAnimation => Get("ToolPauseAnimation", "애니메이션 일시정지");
    public static string StatusColorMode => Get("StatusColorMode", "색상 모드");
    public static string StatusProgress => Get("StatusProgress", "작업 진행 상태");
    public static string StatusZoom => Get("StatusZoom", "확대 배율");
    public static string StatusDetails => Get("StatusDetails", "이미지 정보");
    public static string CanvasName => Get("CanvasName", "이미지 편집 캔버스");
    public static string ToolContext => Get("ToolContext", "도구 속성");
    public static string ColorModeRgb8 => Get("ColorModeRgb8", "RGB 8비트");
    public static string ColorModeRgba8 => Get("ColorModeRgba8", "RGBA 8비트");
    public static string GroupFile => Get("GroupFile", "파일 도구");
    public static string GroupHistory => Get("GroupHistory", "기록 도구");
    public static string GroupImage => Get("GroupImage", "이미지 도구");
    public static string GroupDrawing => Get("GroupDrawing", "그리기 도구");
    public static string GroupShapes => Get("GroupShapes", "도형 도구");
    public static string GroupText => Get("GroupText", "텍스트 도구");
    public static string GroupProtection => Get("GroupProtection", "정보 보호 도구");
    public static string GroupView => Get("GroupView", "보기 도구");
    public static string LayerShow => Get("LayerShow", "표시하기");
    public static string LayerHide => Get("LayerHide", "숨기기");
    public static string LayerLock => Get("LayerLock", "잠그기");
    public static string LayerUnlock => Get("LayerUnlock", "잠금 해제하기");
    public static string LayerDefaultName => Get("LayerDefaultName", "레이어");
    public static string LayerActive => Get("LayerActive", "활성 레이어");
    public static string LayerSetActive => Get("LayerSetActive", "활성 레이어로 선택");
    public static string ToolLayerPanel => Get("ToolLayerPanel", "레이어 패널");
    public static string TipLayerPanel => Get("TipLayerPanel", "레이어 패널을 표시하거나 숨깁니다.");
    public static string LayerAdd => Get("LayerAdd", "새 레이어");
    public static string TipLayerAdd => Get("TipLayerAdd", "활성 레이어 위에 새 레이어를 추가합니다.");
    public static string LayerDelete => Get("LayerDelete", "레이어 삭제");
    public static string TipLayerDelete => Get("TipLayerDelete", "활성 레이어와 포함된 객체를 삭제합니다.");
    public static string LayerMoveUp => Get("LayerMoveUp", "레이어 위로");
    public static string TipLayerMoveUp => Get("TipLayerMoveUp", "활성 레이어를 한 단계 앞으로 이동합니다.");
    public static string LayerMoveDown => Get("LayerMoveDown", "레이어 아래로");
    public static string TipLayerMoveDown => Get("TipLayerMoveDown", "활성 레이어를 한 단계 뒤로 이동합니다.");
    public static string LayerRename => Get("LayerRename", "레이어 이름 바꾸기");
    public static string TipLayerRename => Get("TipLayerRename", "활성 레이어의 이름을 변경합니다.");
    public static string LayerMoveSelection => Get("LayerMoveSelection", "선택 객체를 활성 레이어로 이동");
    public static string TipLayerMoveSelection => Get("TipLayerMoveSelection", "선택한 객체를 활성 레이어의 맨 위로 이동합니다.");
    public static string LayerBlockedHidden => Get("LayerBlockedHidden", "숨긴 레이어에는 편집할 수 없습니다");
    public static string LayerBlockedLocked => Get("LayerBlockedLocked", "잠긴 레이어에는 편집할 수 없습니다");
    public static string TipOpen => Get("TipOpen", "지원 이미지 파일을 선택해 현재 창에서 엽니다.");
    public static string TipClipboard => Get("TipClipboard", "클립보드 이미지를 새 문서로 엽니다.");
    public static string TipNewWindow => Get("TipNewWindow", "독립된 빈 보기 창을 하나 더 엽니다.");
    public static string TipPrevious => Get("TipPrevious", "현재 폴더의 이전 이미지로 이동합니다.");
    public static string TipNext => Get("TipNext", "현재 폴더의 다음 이미지로 이동합니다.");
    public static string TipFit => Get("TipFit", "이미지 전체가 캔버스 안에 보이도록 맞춥니다.");
    public static string TipActualSize => Get("TipActualSize", "이미지 1픽셀을 화면 1픽셀로 표시합니다.");
    public static string TipRotate => Get("TipRotate", "이미지 또는 선택 객체를 시계 방향으로 회전합니다.");
    public static string TipColor => Get("TipColor", "그리기와 선택 객체에 적용할 색을 고릅니다.");
    public static string TipEyedropper => Get("TipEyedropper", "합성된 이미지에서 한 픽셀의 표시 색을 선택합니다.");
    public static string TipZoomOut => Get("TipZoomOut", "캔버스 배율을 한 단계 낮춥니다.");
    public static string TipZoomIn => Get("TipZoomIn", "캔버스 배율을 한 단계 높입니다.");
    public static string TipZoomSlider => Get("TipZoomSlider", "슬라이더 또는 화살표 키로 배율을 조절합니다.");
    public static string TipFullScreen => Get("TipFullScreen", "전체 화면 보기와 창 보기를 전환합니다.");
    public static string TipDockHorizontal => Get("TipDockHorizontal", "도구 레일을 창 위쪽 가로 방향으로 전환합니다.");
    public static string TipDockVertical => Get("TipDockVertical", "도구 레일을 창 왼쪽 세로 방향으로 전환합니다.");
    public static string TipSendToBack => Get("TipSendToBack", "선택 객체를 모든 객체 뒤로 이동합니다.");
    public static string TipSendBackward => Get("TipSendBackward", "선택 객체를 한 단계 뒤로 이동합니다.");
    public static string TipBringForward => Get("TipBringForward", "선택 객체를 한 단계 앞으로 이동합니다.");
    public static string TipBringToFront => Get("TipBringToFront", "선택 객체를 모든 객체 앞으로 이동합니다.");
    public static string TipDuplicate => Get("TipDuplicate", "선택 객체를 복제하고 새 복제본을 선택합니다.");
    public static string TipEditText => Get("TipEditText", "선택한 텍스트 객체의 내용을 편집합니다.");
    public static string TipSelect => Get("TipSelect", "객체를 선택해 이동·크기 조절·회전합니다.");
    public static string TipPen => Get("TipPen", "포인터를 드래그해 자유곡선을 그립니다.");
    public static string TipHighlighter => Get("TipHighlighter", "반투명 자유곡선을 그립니다.");
    public static string TipLine => Get("TipLine", "시작점에서 끝점까지 직선을 그립니다.");
    public static string TipArrow => Get("TipArrow", "시작점에서 끝점까지 화살표를 그립니다.");
    public static string TipRectangle => Get("TipRectangle", "포인터를 드래그해 사각형을 그립니다.");
    public static string TipRoundedRectangle => Get("TipRoundedRectangle", "포인터를 드래그해 둥근 사각형을 그립니다.");
    public static string TipEllipse => Get("TipEllipse", "포인터를 드래그해 타원을 그립니다.");
    public static string TipText => Get("TipText", "영역을 지정한 뒤 텍스트를 입력합니다.");
    public static string TipNumber => Get("TipNumber", "순서가 증가하는 번호 마커를 배치합니다.");
    public static string TipSpeechBubble => Get("TipSpeechBubble", "텍스트 말풍선을 그립니다. 꼬리 핸들로 말꼬리 위치를 조정합니다.");
    public static string TipUndo => Get("TipUndo", "가장 최근 편집 한 단계를 되돌립니다.");
    public static string TipRedo => Get("TipRedo", "되돌린 편집 한 단계를 다시 적용합니다.");
    public static string TipCrop => Get("TipCrop", "영역을 드래그하고 Enter로 적용하거나 Esc로 취소합니다.");
    public static string TipCropRatio => Get("TipCropRatio", "클릭할 때마다 자유, 1:1, 4:3, 16:9 순서로 전환합니다.");
    public static string TipFlipHorizontal => Get("TipFlipHorizontal", "이미지를 좌우 방향으로 뒤집습니다.");
    public static string TipFlipVertical => Get("TipFlipVertical", "이미지를 상하 방향으로 뒤집습니다.");
    public static string TipResize => Get("TipResize", "픽셀 또는 백분율로 출력 크기를 지정합니다.");
    public static string TipColorSwatch => Get("TipColorSwatch", "Enter로 선택하고 화살표 키로 색상표를 이동합니다.");
}
