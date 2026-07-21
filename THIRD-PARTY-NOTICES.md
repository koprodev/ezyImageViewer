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

## 인벤토리 범위와 재현 방법

이 목록은 단순히 모든 lock 파일 항목을 합친 것이 아니라 다음 두 실제 Windows x64
제품 산출물의 `.deps.json`과 파일 레이아웃을 기준으로 합니다.

- 주 앱: `packaging/out/layout/ezyImageViewer.deps.json`
- 격리 코덱 호스트: `packaging/out/codec-host-layout/EzyImageViewer.CodecHost.deps.json`

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
| Material Symbols Outlined font | commit `abd7f5c0e179c83f068c770650bd14ebac5d5a09` | Apache-2.0 | 동봉된 [Apache 2.0 원문](EzyImageViewer.App/Assets/Fonts/LICENSE-MaterialSymbols.txt). TTF SHA-256: `0A186BE334A516CF80A4287073B788FEEF8F0FC2C633C74F4FF7828530F35293` ([provenance](docs/adr/ADR-0013-material-symbols-font-icons.md)) |
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

## 격리 CodecHost 런타임

CodecHost는 주 앱과 별도 MSIX로 설치되는 self-contained x64 제품 구성 요소입니다.
PDF와 PSD가 정상 사용자 UI에서 아직 비활성이어도 아래 바이너리는 CodecHost 배포물에
포함되므로 제품 런타임 인벤토리에서 제외하지 않습니다.

| 구성 요소 | 확인 버전 | 패키지 라이선스 기록 | 원문·출처 |
|---|---:|---|---|
| PDFtoImage | 5.2.1 | MIT | `pdftoimage/5.2.1/*.nuspec`; <https://github.com/sungaila/PDFtoImage> |
| bblanchon.PDFium.Win32 | 147.0.7690 | nuspec `Apache-2.0`; upstream 배포 `LICENSE`는 MIT(Benoît Blanchon) | `bblanchon.pdfium.win32/147.0.7690/*.nuspec`; 실제 PDFium·Chromium 고지 집합은 아래 §PDFium/Chromium 고지 집합 참조. nuspec 표현과 배포 `LICENSE` 원문이 다르므로 둘 다 기록한다. |
| Magick.NET.Core, Magick.NET-Q8-AnyCPU | 14.15.0 | Apache-2.0 | `magick.net-q8-anycpu/14.15.0/Notice.txt`; <https://github.com/dlemstra/Magick.NET>. 이 고지에는 동봉 ImageMagick 7.1.2-27의 라이선스와 귀속 정보가 포함됩니다. |
| SkiaSharp, SkiaSharp.NativeAssets.Win32 | 3.119.2 | MIT | 각 패키지의 `LICENSE.txt`; native 패키지의 `THIRD-PARTY-NOTICES.txt` |
| Microsoft.NETCore.App Runtime win-x64 | 10.0.10 | MIT | `microsoft.netcore.app.runtime.win-x64/10.0.10/LICENSE.TXT`, `THIRD-PARTY-NOTICES.TXT`; <https://github.com/dotnet/runtime> |

### PDFium/Chromium 고지 집합

복원된 `bblanchon.PDFium.Win32` NuGet 패키지에는 PDFium·Chromium 제3자 고지 원문이 없어,
재배포 바이너리에 대응하는 upstream 고지 집합을 버전 고정으로 확보해 CodecHost 배포물에
포함했습니다. 출처와 무결성은 다음과 같이 고정합니다.

| 항목 | 값 |
|---|---|
| upstream 릴리스 | bblanchon/pdfium-binaries `chromium/7690` ("PDFium 147.0.7690.0", published 2026-02-16) |
| 아카이브 | `pdfium-win-x64.tgz` SHA-256 `06EF95AC4F9B8897731224639DDF0F185693CB48BC9EE650F1E92F71E0D2A94E` |
| 바이너리 대응 증명 | 아카이브 `bin/pdfium.dll` SHA-256 `15DF9DDDD81EDDC5A177946AA5E34CDA821EBC46A51440ECB607F91E99644895`이 복원 NuGet `runtimes/win-x64/native/pdfium.dll`과 정확히 일치 |
| 포함 위치 | `EzyImageViewer.CodecHost/Notices/PDFium/` → CodecHost MSIX에 `Notices/PDFium/` 로 배포 |
| 무결성 계약 | `packaging/verify-msix-release.ps1`이 16개 파일 각각의 SHA-256을 fail-closed로 검증 |

포함 파일은 배포 `LICENSE.txt`(MIT, Benoît Blanchon)와 `licenses/`의 14개 upstream 제3자 고지이며,
그중 `licenses/pdfium.txt`가 PDFium Authors(Google)의 BSD-3-Clause 원문입니다. 나머지는
abseil, agg23, fast_float, freetype, icu, lcms, libjpeg-turbo(ijg·md), libopenjpeg, libpng,
libtiff, llvm-libc, simdutf, zlib 고지입니다. 파일은 upstream 바이트를 변경 없이 재현하며
`.gitattributes`의 `-text` 규칙으로 clone·CI 체크아웃에서도 고정 해시를 유지합니다. 이
포함은 공학적 확보·무결성 고정이며 별도 법무 검토를 대신하지 않습니다.

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
테스트 프로젝트가 PDFtoImage, Magick.NET, SkiaSharp를 직접 참조하더라도 같은 패키지가
CodecHost 또는 주 앱 레이아웃에 실재하면 위 제품 런타임 범위가 우선합니다.

## 출시 전 확인 사항

- 이 문서가 가리키는 앱 MIT 원문과 모든 제3자 라이선스·고지 원문을 해당 MSIX에
  포함하고 설치 후 접근 경로를 확인합니다.
- PDFium·Chromium의 버전 고정 제3자 고지 집합은 확보·포함했다(위 §PDFium/Chromium 고지 집합). 남은 것은 법무 검토뿐이다.
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
