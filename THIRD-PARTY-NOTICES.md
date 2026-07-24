# ezyImageViewer 제3자 소프트웨어 고지

최종 로컬 확인일: 2026-07-19

이 문서는 ezyImageViewer 제품 산출물에서 확인된 제3자 구성 요소의 공학적
인벤토리입니다. 각 구성 요소의 저작권과 라이선스 조건은 해당 권리자에게 있으며,
이 문서는 개별 라이선스 전문을 대체하거나 법무 검토가 끝났음을 의미하지 않습니다.

## ezyImageViewer 자체 라이선스

ezyImageViewer 자체 코드는 저장소 루트의 `LICENSE`에 있는 MIT License를 따릅니다.
배포 패키지에서는 같은 원문을 `LICENSE.txt`로 제공합니다.

> Copyright (c) 2026 koprodev

배포 패키지는 위 `LICENSE` 원문을 포함해야 합니다.

## 알고리즘 출처 고지 (UserChoice 해시)

`EzyImageViewer.Infrastructure/UserChoiceHash.cs`는 Mozilla Firefox
`browser/components/shell/WindowsUserChoice.cpp`(MPL-2.0)의 UserChoice 해시 구조를 C#으로
옮긴 **MPL-2.0 파생 저작물**입니다. 따라서 이 파일 하나는 루트 MIT가 아니라 MPL-2.0을 따르며,
파일 상단에 `SPDX-License-Identifier: MPL-2.0`과 원 출처를 표기했습니다. 배포물에는 MPL-2.0
전문과 이 파일의 소스 가용성 안내를 포함하고, 나머지 ezyImageViewer 코드가 MIT임을 함께
명시합니다. MPL-2.0 전문: <https://www.mozilla.org/en-US/MPL/2.0/>.

정확성 교차검증에는 DanysysTeam/PS-SFTA `SFTA.ps1`(MIT, Copyright 2022 Danysys)의 출력
벡터만 오프라인 오라클로 사용했으며, 그 소스 코드는 제품에 포함하지 않습니다.

이 기능(레지스트리 writer와 UI 버튼)은 installer(MSI) 빌드에서 사용자가 명시적으로 요청할
때만 동작하며 Store/packaged·registry-free Portable 산출물에는 컴파일되지 않습니다. 순수 해시
core(`UserChoiceHash`)는 Infrastructure 어셈블리에 존재하지만 writer/UI 없이는 호출되지
않습니다.

## 인벤토리 범위와 재현 방법

이 목록은 단순히 모든 lock 파일 항목을 합친 것이 아니라 실제 Windows x64 제품 산출물
`packaging/out/layout/ezyImageViewer.deps.json`의 파일 레이아웃을 기준으로 합니다.

목록을 재현하려면 다음 순서로 확인합니다.

1. `dotnet restore EzyImageViewer.slnx --locked-mode`로 lock 파일과 일치하는 패키지를
   복원합니다.
2. `dotnet nuget locals global-packages --list`로 전역 NuGet 패키지 폴더를 확인합니다.
3. 아래의 `{package}/{version}` 폴더에서 `.nuspec`의 `license`, `licenseUrl`,
   `projectUrl`, `repository`와 표에 적힌 라이선스·고지 파일을 읽습니다.
4. Release 패키지를 다시 만든 뒤 두 `.deps.json`의 `libraries`와 실제 패키지 파일을
   이 문서 및 별도 SBOM과 대조합니다.

표의 `file:` 표기는 NuGet 메타데이터가 패키지 내부 파일을 라이선스로 지정했다는
뜻이며 SPDX 식별자로 재해석하지 않습니다. 최종 배포에서는 표에 적힌 원문 파일을
변경 없이 수집해 각 MSIX에서 접근할 수 있게 해야 합니다.

## 주 앱 런타임

| 구성 요소 | 확인 버전 | 패키지 라이선스 기록 | 원문·출처 |
|---|---:|---|---|
| Material Symbols Outlined font subset | commit `abd7f5c0e179c83f068c770650bd14ebac5d5a09` | Apache-2.0 | FontTools 4.63.0으로 기본 인스턴스 55 glyph를 추출했다. 동봉된 [Apache 2.0 원문](EzyImageViewer.App/Assets/Fonts/LICENSE-MaterialSymbols.txt). 배포 TTF SHA-256: `6EB4B0BA0D788B9CFB4F22D68A768276142CBC3698177AC2803A0F1F1EB3207F` ([provenance](docs/adr/ADR-0013-material-symbols-font-icons.md)) |
| ExCSS | 4.3.1 | MIT | `excss/4.3.1/*.nuspec`; <https://github.com/TylerBrinks/ExCSS> |
| HarfBuzzSharp, HarfBuzzSharp.NativeAssets.Win32 | 14.2.1.1 | MIT | 각 패키지의 `LICENSE.txt`; native 패키지의 `THIRD-PARTY-NOTICES.txt` |
| SkiaSharp, SkiaSharp.HarfBuzz, SkiaSharp.NativeAssets.Win32, SkiaSharp.NativeAssets.WinUI, SkiaSharp.Views.WinUI | 4.150.1 | MIT | 각 패키지의 `LICENSE.txt`; native 패키지의 `THIRD-PARTY-NOTICES.txt`. 이 고지에는 libwebp 등 네이티브 전이 구성 요소가 포함되므로 파일 전체를 수집해야 합니다. |
| Svg.Skia, Svg.Animation, Svg.Model, Svg.SceneGraph, ShimSkiaSharp | 5.1.1 | MIT | 각 `{package}/5.1.1/*.nuspec`; <https://github.com/wieslawsoltes/Svg.Skia> |
| Svg.Custom | 5.1.1 | MS-PL | `svg.custom/5.1.1/*.nuspec`; <https://github.com/wieslawsoltes/Svg.Skia>. 같은 Svg.Skia 계열의 MIT 구성 요소와 구분해야 합니다. |
| System.Numerics.Tensors | 9.0.0 | MIT | `LICENSE.TXT`, `THIRD-PARTY-NOTICES.TXT`; <https://github.com/dotnet/runtime> |
| Microsoft.Web.WebView2, Microsoft.Web.WebView2.Core.Projection | 1.0.3719.77 | `file: LICENSE.txt` | `microsoft.web.webview2/1.0.3719.77/LICENSE.txt`, `NOTICE.txt`; <https://aka.ms/webview> |
| Microsoft.WindowsAppSDK | 2.2.0 | `file: license.txt` | 패키지의 `license.txt`, `NOTICE.txt`; <https://github.com/microsoft/windowsappsdk> |
| Microsoft.WindowsAppSDK.AI | 2.2.3 | `file: license.txt` | 패키지의 `license.txt`; <https://github.com/microsoft/windowsappsdk> |
| Microsoft.WindowsAppSDK.Foundation | 2.1.0 | `file: license.txt` | 패키지의 `license.txt`; <https://aka.ms/windowsappsdk> |
| Microsoft.WindowsAppSDK.InteractiveExperiences | 2.0.15 | `file: license.txt` | 패키지의 `license.txt`; <https://aka.ms/windowsappsdk> |
| Microsoft.WindowsAppSDK.ML | 2.1.70 | `file: license.txt` | 패키지의 `license.txt`, `ThirdPartyNotices.txt`; <https://github.com/microsoft/windowsappsdk> |
| Microsoft.WindowsAppSDK.Widgets | 2.0.5 | `file: license.txt` | 패키지의 `license.txt`; <https://github.com/microsoft/windowsappsdk> |
| Microsoft.WindowsAppSDK.WinUI | 2.2.1 | `file: license.txt` | 패키지의 `license.txt`, 루트·`tools/NOTICE.txt`; <https://github.com/microsoft/windowsappsdk> |
| Microsoft.Windows.AI.MachineLearning | 2.1.70 | `file: license.txt` | 패키지의 `license.txt`, `ThirdPartyNotices.txt`; <https://github.com/microsoft/windowsappsdk> |
| runtimepack.Microsoft.Windows.SDK.NET.Ref | 10.0.26100.57 | `.nuspec`의 `licenseUrl` | `microsoft.windows.sdk.net.ref/10.0.26100.57/*.nuspec`; <https://aka.ms/WinSDKLicenseURL> |

`Microsoft.WindowsAppSDK` 2.2.0의 실제 nuspec과 앱 lock graph는
`Microsoft.WindowsAppSDK.WinUI` 2.2.1을 끌어오고 self-contained publish layout에는 해당
WinUI DLL이 포함됩니다. 그런데 복원된 2.2.1 `license.txt`는 제품을 **Microsoft Windows App
SDK Engineering Preview**로 부르며 다른 계약이 허용하지 않는 한 live operating environment
사용을 금지합니다. NuGet 버전에 prerelease suffix가 없다는 사실만으로 이 원문 제한을 무시하지
않습니다. Microsoft의 명시적 정정·허용 근거 또는 release-safe dependency 전환과 전체 재검증 전에는
정식 배포를 차단합니다.

Windows App SDK upstream source repository의 MIT 표기와 실제 재배포 NuGet의 Microsoft Software
License Terms는 동일한 계약이 아닙니다. SignPath Foundation의 OSS 조건에서 이 self-contained
runtime이 System Library 예외에 해당하는지도 현재 미확정이므로 신청 전에 확인합니다.

## 설치 빌드 도구와 배포되는 파생 파일

WiX Toolset은 제품 런타임 라이브러리가 아니라 MSI와 Burn Setup을 만드는 빌드 도구입니다.
현재 모든 WiX SDK·확장은 정확히 `7.0.0`으로 잠겨 있고 프로젝트의
`AcceptEula=wix7`은 사용자가 2026-07-19 승인한 개발·CI 계약입니다. 이 승인은 조직 수익,
후원 또는 FireGiant 계약 필요성에 대한 법무·회계 판정을 대신하지 않습니다.

| 구성 요소 | 확인 버전·파일 | 라이선스·배포 조치 | 원문·출처 |
|---|---|---|---|
| WiX Toolset SDK, UI, Util, BootstrapperApplications | 7.0.0 | Microsoft Reciprocal License(MS-RL), OSMF EULA 적용 빌드 도구 | 각 NuGet package의 `.nuspec`, `OSMFEULA.txt`; [WiX v7.0.0 LICENSE.TXT](https://github.com/wixtoolset/wix/blob/v7.0.0/LICENSE.TXT) |
| WiX RtfLarge theme 파생물 | `installer/bundle/EzyRtfLargeTheme.xml` | MS-RL. 원 저작권 고지를 유지하고, Setup과 함께 수정된 XML 원문 및 `LICENSE-MRL.txt`를 제공하며 둘 다 `SHA256SUMS`에 포함 | [WiX v7.0.0 RtfLargeTheme.xml](https://github.com/wixtoolset/wix/blob/v7.0.0/src/ext/Bal/stdbas/Resources/RtfLargeTheme.xml), `installer/bundle/LICENSE-MRL.txt` |

`build-wix-installer.ps1`은 배포 폴더에 위 수정 XML과 MS-RL 원문을 복사합니다. 따라서 공개
릴리스에서 두 파일을 Setup과 분리하거나 checksum 목록에서 누락하면 안 됩니다.

## 제품 런타임에서 제외한 항목

다음 패키지는 현재 제품 `.deps.json`과 배포 레이아웃에 포함되지 않으므로 위 제품
런타임 고지에서 분리합니다.

- 테스트 전용: Microsoft.NET.Test.Sdk, Microsoft.CodeCoverage,
  Microsoft.TestPlatform 계열, xUnit 계열.
- 빌드 전용: Microsoft.Windows.SDK.BuildTools,
  Microsoft.Windows.SDK.BuildTools.MSIX.
- 다른 RID용 네이티브 패키지: lock 그래프에만 존재하는 Linux·macOS 자산.

이 항목들은 제품 런타임 SBOM이 아니라 개발·빌드 도구 SBOM에서 추적할 수 있습니다.
테스트 프로젝트는 픽스처 이미지를 만들기 위해 Magick.NET을 직접 참조하지만, 이 패키지는
어떤 제품 레이아웃에도 들어가지 않습니다. `packaging/verify-msix-release.ps1`과
`verify-portable-release.ps1`이 배포물 유입을 fail-closed로 차단합니다.

## 출시 전 확인 사항

- 이 문서가 가리키는 앱 MIT 원문과 모든 제3자 라이선스·고지 원문을 해당 MSIX에
  포함하고 설치 후 접근 경로를 확인합니다.
- Windows App SDK WinUI 2.2.1의 Engineering Preview/live 운영 제한을 명시적 근거 또는
  release-safe dependency 전환으로 해소합니다.
- SignPath Foundation에 self-contained Windows App SDK redistributable의 System Library 예외
  적용 여부를 확인합니다.
- 실제 Release `.deps.json`, 레이아웃, package hash를 기준으로 이 목록과 SBOM을
  다시 생성하고 차이를 0건으로 만듭니다.
- Microsoft 고유 `license.txt`, WebView2, Windows SDK 배포 조건과 전체 상용 배포
  조건은 별도 법무 검토를 거칩니다.
- Burn Setup과 함께 `EzyRtfLargeTheme.xml`, `LICENSE-MRL.txt`가 배포되고 두 파일의
  SHA-256이 공개 checksum에 포함됐는지 확인합니다.
