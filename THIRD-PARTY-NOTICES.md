# ezyImageViewer 제3자 소프트웨어 고지

최종 로컬 확인일: 2026-07-26

이 문서는 Microsoft Store MSIX에 포함되는 제3자 구성 요소의 공학적 인벤토리입니다.
각 구성 요소의 저작권과 라이선스 조건은 해당 권리자에게 있으며, 이 문서는 개별
라이선스 전문을 대체하거나 법무 검토가 끝났음을 의미하지 않습니다.

## ezyImageViewer 자체 라이선스

ezyImageViewer 자체 코드는 저장소 루트의 `LICENSE`에 있는 MIT License를 따릅니다.
배포 패키지에는 같은 원문을 `LICENSE.txt`로 포함합니다.

> Copyright (c) 2026 koprodev

## 인벤토리 범위와 재현 방법

버전은 `Directory.Packages.props`, 각 프로젝트의 `packages.lock.json`, Store Release
빌드의 `ezyImageViewer.deps.json`, 실제 MSIX 레이아웃을 함께 확인합니다.

1. `dotnet restore EzyImageViewer.slnx --locked-mode`로 잠금 파일과 일치하게 복원합니다.
2. Store Release 빌드와 MSIX를 생성합니다.
3. `packaging/verify-msix-release.ps1`로 패키지 payload와 빌드 출력을 대조합니다.
4. 각 NuGet 패키지의 `.nuspec`, 라이선스 파일, 제3자 고지 파일을 확인합니다.

`file:` 표기는 NuGet 메타데이터가 패키지 내부 파일을 라이선스로 지정했다는 뜻이며
SPDX 식별자로 재해석하지 않습니다.

## 주 앱 런타임

| 구성 요소 | 확인 버전 | 패키지 라이선스 기록·출처 |
|---|---:|---|
| Material Symbols Outlined font subset | commit `abd7f5c0e179c83f068c770650bd14ebac5d5a09` | Apache-2.0. 동봉 [원문](EzyImageViewer.App/Assets/Fonts/LICENSE-MaterialSymbols.txt), [provenance](docs/adr/ADR-0013-material-symbols-font-icons.md), TTF SHA-256 `6EB4B0BA0D788B9CFB4F22D68A768276142CBC3698177AC2803A0F1F1EB3207F` |
| ExCSS | 4.3.1 | MIT, <https://github.com/TylerBrinks/ExCSS> |
| HarfBuzzSharp, HarfBuzzSharp.NativeAssets.Win32 | 14.2.1.1 | MIT. 각 패키지의 `LICENSE.txt`; native package의 `THIRD-PARTY-NOTICES.txt` |
| SkiaSharp, SkiaSharp.HarfBuzz, SkiaSharp NativeAssets/WinUI | 4.150.1 | MIT. 각 패키지의 `LICENSE.txt`와 native `THIRD-PARTY-NOTICES.txt` |
| Svg.Skia, Svg.Animation, Svg.Model, Svg.SceneGraph, ShimSkiaSharp | 5.1.1 | MIT, <https://github.com/wieslawsoltes/Svg.Skia> |
| Svg.Custom | 5.1.1 | Microsoft Public License(MS-PL), 해당 package `.nuspec` |
| Microsoft.WindowsAppSDK 및 Runtime | 1.8.260710003 | package `license.txt`, `NOTICE.txt`; <https://github.com/microsoft/windowsappsdk> |
| Microsoft.WindowsAppSDK.WinUI | 1.8.260709004 | package `license.txt`, `NOTICE.txt` |
| Microsoft.Web.WebView2 | 1.0.3179.45 | `file: LICENSE.txt`, `NOTICE.txt`; <https://aka.ms/webview> |
| System.Numerics.Tensors | 9.0.0 | MIT, package `LICENSE.TXT`와 `THIRD-PARTY-NOTICES.TXT`; <https://github.com/dotnet/runtime> |
| runtimepack.Microsoft.Windows.SDK.NET.Ref | 10.0.26100.57 | package `.nuspec`의 `licenseUrl`; <https://aka.ms/WinSDKLicenseURL> |

Windows App SDK 1.8 계열은 정식 배포 라이선스가 확인된 버전으로 고정합니다.
`Directory.Packages.props`의 주석과 실제 package `license.txt`를 버전 변경 전에 다시
확인해야 합니다.

## 제품 런타임에서 제외한 항목

- 테스트 전용: Microsoft.NET.Test.Sdk, Microsoft.CodeCoverage,
  Microsoft.TestPlatform 계열, xUnit 계열, Magick.NET.
- 빌드 전용: Microsoft.Windows.SDK.BuildTools,
  Microsoft.Windows.SDK.BuildTools.MSIX.
- `Microsoft.WindowsAppSDK.ML`, ONNX Runtime, DirectML payload:
  제품이 관련 WinRT API를 사용하지 않아 앱 프로젝트의 검증된 MSIX 빌드에서 제외합니다.
- 다른 RID용 네이티브 패키지: 잠금 그래프에만 존재하는 Linux·macOS 자산.

`packaging/verify-msix-release.ps1`은 테스트 전용 Magick/PDF 구성 요소, PDB, 예상하지
않은 payload가 Store MSIX에 들어오면 실패합니다.

## 출시 전 확인 사항

- MIT 원문과 이 제3자 고지를 Store MSIX에 포함합니다.
- 실제 Release `.deps.json`, 잠금 파일, MSIX 레이아웃을 이 목록과 대조합니다.
- 각 native package의 전체 제3자 고지와 Microsoft 고유 라이선스 원문을 확인합니다.
- Microsoft Store 인증 결과와 실제 설치 패키지의 라이선스 표시 경로를 확인합니다.
