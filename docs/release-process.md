# ezy Image Viewer 릴리스 절차

상태: **unsigned Installer + Portable 개인 평가·테스트 프리릴리스 · production 서명 및 Store 심사 전**

이 문서는 현재 저장소에서 재현 가능한 후보 산출물 생성·검증 절차를 정의한다.
현재 공개 바이너리는 기존 Basic Portable ZIP과 사용자 승인된 unsigned Setup/단일 Portable
개인 평가·테스트 프리릴리스로 제한한다.
최종 production 배포는 승인된 배포 정책에 따라 WiX MSI와 Burn Setup을 사용한다.
아래 full MSIX/AppInstaller 절차는 package identity·CodecHost 격리 선행 검증으로 보존한다.
최종 채널은 고정 scope MSI 두 개와 scope 선택형 Burn Setup이다. WiX 소스·등록 backend와
read-only artifact verifier는 구현됐지만 production Publisher·서명과 clean VM의 실제
등록·설치 lifecycle을 통과하기 전에는 어떤 산출물도 정식 릴리스로 표시하지 않는다.

## 0. 확정 배포 방식

- 공개 채널은 `koprodev/ezyImageViewer`의 GitHub Releases 한 곳으로 고정한다.
  (2026-07-23 사용자 지시로 기존 `koprodev/ezy-image-viewer-releases`·private archive 저장소를
  삭제하고 이 단일 저장소로 통합. 기존 v0.1.0-portable.1~v1.0.12-preview.1 릴리스 자산과
  1.0.12 이하 배포 바이너리의 `업데이트 확인` 링크는 더 이상 유효하지 않다.)
- 기존 unsigned Basic Portable ZIP과 `v1.0.38-preview.1`의 unsigned Setup/단일 Portable을
  명시적 개인 평가·테스트 prerelease로 제공한다.
- 향후 production 일반 사용자의 기본 다운로드는 scope 선택형 Burn Setup이다. 고정 per-user/per-machine MSI는
  관리자·고급 사용자가 scope를 직접 고를 때 사용하는 보조 자산이다.
- 앱은 자동 버전 조회·다운로드·설치를 하지 않으며 사용자가 `업데이트 확인`을 누를 때만 최신
  Release 페이지를 브라우저로 연다.
- 개발 저장소에는 공개 원격을 연결하지 않는다. `packaging/public-source-allowlist.txt`에 명시된
  제품 소스·테스트·공개 문서만 별도 `ezyImageViewer-public` Git 작업트리에 동기화한 뒤
  `koprodev/ezyImageViewer`로 게시한다. 루트 개발 명세·UI 참고 이미지, AI 협업 지침·상태,
  내부 RTM·spike·review 문서는 공개 작업트리에 복사하지 않는다.
- 공개 저장소에는 기존 로컬 Git history가 아니라 검토된 allowlist 기반 clean source snapshot만
  게시하고, 공개 source manifest로 원본 commit·허용 경로·파일·SHA-256을 고정한다.
- source snapshot과 Basic Portable artifact는 public CI에서 같은 source commit을 기준으로 생성한다.
- 2026-07-21 사용자 승인은 검증된 unsigned Burn Setup 하나의 개인 평가·테스트 공개까지
  확장한다. SignPath 문의·서명 요청·production installer 공개 승인은 아니며 production 서명·
  라이선스·notice·clean VM gate 전에는 정식 산출물로 표시하지 않는다.

### 0.2 Installer + 단일 Portable 개인 프리릴리스

`packaging/preview-release.json`은 `v1.0.38-preview.1`의 앱·CodecHost·Portable 버전과
unsigned Publisher를 고정한다. 공개 주 실행 자산은 다음 둘이다.

- `ezyImageViewerSetup-1.0.38-x64-dev-unsigned.exe`: scope 선택형 Burn Setup. 지원 이미지
  형식은 Open With 후보로 기본 등록하며 기본 앱을 강제하지 않는다.
- `ezyImageViewer.exe`: 압축된 단일 파일 Portable. WinUI 내장 리소스 확인을 위해 파일명을 유지한다. 레지스트리와
  파일 연결을 등록하지 않고 실행 중 `%TEMP%` 계열에 런타임을 추출한다.

WiX 수정 theme의 reciprocal source 계약 때문에 `EzyRtfLargeTheme.xml`과
`LICENSE-MRL.txt`도 공개한다. `preview-release-manifest.json`과 `SHA256SUMS.txt`는 같은
source commit에 결박된 네 자산의 길이·SHA-256을 기록한다.

```powershell
powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `
  packaging\build-preview-release.ps1 -OutputDirectory packaging\out\preview-1.0.38
powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `
  packaging\verify-preview-release.ps1 -OutputDirectory packaging\out\preview-1.0.38
```

GitHub에서는 `.github/workflows/release-preview.yml`을 protected `main`에서 수동 실행한다.
기존 태그가 있으면 덮어쓰지 않고 실패하며, build와 verifier가 모두 성공한 정확한 자산만
prerelease로 게시한다. 게시 직후 실제 다운로드한 `ezyImageViewer.exe`도 대화형 Windows
환경에서 `verify-single-file-portable.ps1`로 다시 검증해야 릴리스를 완료한 것으로 본다.

### 0.1 Basic Portable 평가·테스트 프리릴리스

고정 계약은 `packaging/portable-release.json`의 `0.1.0-portable.1`과
`v0.1.0-portable.1`이다. 공개 자산은 ZIP, `SHA256SUMS.txt`,
`portable-release-manifest.json` 세 개뿐이다. ZIP에는 주 앱 self-contained x64 payload,
`PORTABLE-README.txt`, `THIRD-PARTY-NOTICES.md`, runtime dependency에서 수집한
`THIRD-PARTY-LICENSES`와 결정적 `PACKAGE-CONTENTS.sha256`을 포함한다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File packaging\build-portable-release.ps1 `
  -Version 0.1.0-portable.1 -OutputDirectory packaging\out\portable
powershell -NoProfile -ExecutionPolicy Bypass -File packaging\verify-portable-release.ps1 `
  -Version 0.1.0-portable.1 -OutputDirectory packaging\out\portable
```

Portable에는 package identity, installer, file association, CodecHost/PDFium/PSD, PDB를
넣지 않는다. main EXE가 unsigned인지, WinUI 2.2.1 Engineering Preview 원문이 실제
license inventory에 포함됐는지, ZIP 경로·hash·payload가 exact인지 verifier가 검사한다.
따라서 이 자산은 production 또는 live 운영용으로 표시하지 않는다. 다음 무료 배포 단계는
Microsoft Store full MSIX 심사를 별도 진행하는 것이다.

## 1. 릴리스 단위

기존 packaged 검증 후보는 다음 두 MSIX를 한 세트로 취급한다. AppInstaller는 이 쌍의
오프라인 identity·dependency 계약 회귀에만 사용하며 최종 GitHub Release 자산으로 확정하지 않았다.

- `ezyImageViewer.msix`: WinUI 주 앱과 공식 Snipping Tool callback identity
- `ezyImageViewer.CodecHost.msix`: PDF/PSD 네이티브 디코더를 격리하는 x64 framework package

주 앱 manifest는 CodecHost의 정확한 Publisher와 최소 버전을 참조한다. CodecHost wire protocol은
현재 exact-version 계약이므로, 새 Host와 이전 주 앱의 호환성을 확인하기 전에는 Host만 독립적으로
승격하지 않는다. Windows dependency 해석은 `MinVersion` 이상인 이미 설치된 더 새 framework를
선택할 수 있으므로 AppInstaller XML에 후보 Host 버전을 적는 것만으로 실행 Host 버전이 고정되지는
않는다. 깨끗한 VM의 이전/이후 Host 선설치 matrix를 통과시키고, breaking protocol에서는 Host
identity versioning 정책을 먼저 확정한다.

## 2. 사전 조건

- `global.json`의 .NET SDK와 `Directory.Packages.props`의 Windows SDK BuildTools가 locked
  restore되어 있어야 한다.
- 작업 트리의 사용자 소유 변경과 릴리스 변경 범위를 먼저 분리한다.
- MSIX 버전은 각각 선행 0이 없는 `0..65535` 범위의 정규 숫자 네 부분으로 명시한다. 설치 후보를 바꿀 때마다
  해당 package 버전을 증가시킨다.
- AppInstaller 2017/2 profile을 사용하는 후보는 주 앱과 Host 버전의 major가 0보다 커야 한다.
- 정식 후보에서는 `-NoBuild`를 사용하지 않는다. 이 옵션은 같은 cycle에서 이미 검증한 로컬
  packaged 출력 재사용 전용이다.
- BuildTools는 project assets의 `packageFolders`, `NUGET_PACKAGES`, 기본 NuGet
  cache 후보에서 고정 버전의 물리 디렉터리 하나만 선택한다. 사용자 profile 경로를 고정값으로
  가정하지 않는다.
- 제품은 업데이트 endpoint를 주입하거나 GitHub API를 호출하지 않는다. 사용자가 설정의
  `업데이트 확인`을 누른 경우에만 고정된 공개 `releases/latest` 페이지를 브라우저로 연다.

## 3. 소스·테스트 게이트

저장소 루트에서 다음 순서로 실행한다.

```powershell
dotnet restore EzyImageViewer.slnx --locked-mode `
  -p:NuGetAuditMode=all -p:WarningsAsErrors=NU1903%3BNU1904
dotnet build EzyImageViewer.slnx -c Release --no-restore
dotnet test EzyImageViewer.Tests\EzyImageViewer.Tests.csproj -c Release --no-build
```

경고나 오류, 실패 테스트가 하나라도 있으면 패키징을 시작하지 않는다. UI·접근성·실파일·실캡처
게이트는 자동 테스트 결과와 별도로 기록한다.

공개 저장소용 source는 임의 복사본이 아니라 commit에서 생성한 allowlist 기반 clean snapshot만
사용한다. 다음 계약은 필수 source, 비허용 개발 자료 부재, manifest의 exact 파일 수·SHA-256을
검증하며 외부 저장소 생성이나 업로드는 수행하지 않는다.

```powershell
powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `
  packaging\test-public-source-snapshot-contract.ps1

powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `
  packaging\new-public-source-snapshot.ps1 `
  -Revision HEAD `
  -OutputDirectory <새 출력 디렉터리>

powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `
  packaging\sync-public-source.ps1 `
  -Revision HEAD `
  -PublicDirectory <별도 public Git 작업트리>
```

동기화 대상은 별도 Git 저장소의 루트이며 미커밋 변경이 있으면 실패한다. 생성물의
`PUBLIC-SOURCE-MANIFEST.json`은 source commit, 허용 경로, payload 파일 수와 각 파일의 SHA-256을
기록한다. 공개 직전에는 이 자동 계약과 별도로 dedicated secret scanner와 staged tree 수동 검토를
통과해야 한다.

### 3.1 external-location/WiX 입력 staging

WiX package 도입 전에도 실제 MSI 입력이 될 unpackaged self-contained payload와 sparse identity의
정합을 다음 명령으로 검증할 수 있다. 아래 Publisher는 개발 검증 예시이며 production 값이 아니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  packaging\stage-msi-foundation.ps1 `
  -OutputDirectory <새 출력 디렉터리> `
  -Version 1.0.0.0 `
  -CodecHostVersion 1.0.0.0 `
  -Publisher "CN=ezyImageViewer Dev" `
  -MinVersion 10.0.19041.0

powershell -NoProfile -ExecutionPolicy Bypass -File `
  packaging\verify-msi-foundation.ps1 `
  -StagingDirectory <출력 디렉터리> `
  -Version 1.0.0.0 `
  -CodecHostVersion 1.0.0.0 `
  -Publisher "CN=ezyImageViewer Dev" `
  -MinVersion 10.0.19041.0
```

산출물을 보존할 필요가 없는 CI·로컬 전체 gate는 위 두 단계를 exact-owned 임시 경로에서
조합하고 정리하는 다음 진입점을 사용한다.

```powershell
powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `
  packaging\verify-external-location-build.ps1 `
  -Version 1.0.0.0 `
  -CodecHostVersion 1.0.0.0 `
  -Publisher "CN=ezyImageViewer Dev" `
  -MinVersion 10.0.19041.0
```

`19041`은 external-location API/manifest와 설치 프로그램이 함께 강제하는 제품 최소값이다.
사용자 결정에 따라 공식 지원 범위는 **Windows 10 build 19041 이상 x64**이며 모든 지원되는
Windows 11 x64도 이 조건에 포함된다.

staging은 다음을 fail-closed로 확인한다.

- MSBuild `PublishOutputs.*.txt`와 payload exact 일치, PDB·SDK tool·CodecHost/PDFium 혼입 금지
- `ezyImageViewer.pri`, Material Symbols와 고지 파일을 포함한 필수 payload allowlist
- 실행 파일에 sparse package와 같은 Publisher/package/application identity가 실제로 embed됐는지
  `mt.exe` 재추출 검증
- sparse manifest의 `AllowExternalContent`, neutral architecture, Host dependency,
  protocol/capability exact allowlist
- payload `PACKAGE-CONTENTS.sha256`, bounded staging metadata, unsigned sparse MSIX pack/unpack
- unpacked sparse package의 exact `AppxManifest.xml`+로고 3개 경로와 원본 SHA-256

이 단계 자체는 서명·인증서 생성/신뢰 등록·Appx registration·MSI 설치를 수행하지 않는다.
실제 등록은 `identity-registration-backend.ps1`과 `invoke-identity-registration.ps1`이 담당하며
Host→main 등록, main→Host 제거, pre-existing package 보존, 완료 단계 역순 rollback과 고유
exit code 계약을 구현한다. fake-adapter harness로 권한별 성공·복구·rollback 경로를 검증하되,
실제 Windows package 등록 증거는 clean VM gate에서 별도로 수집한다.

2026-07-19 실측 기준 C# external 계약 7/7, manifest 변이 31/31, MSI foundation 52/52,
실제 payload 545개·264,034,230 bytes, embedded identity와 unsigned `makeappx pack/unpack /nv`가
PASS했고 임시 staging 잔여는 0이다. 저장소의 ignored `bin/obj/external`은 MSBuild cache로 남는다.

`NuGetAuditMode=all`은 전이적 패키지까지 감사하며 NU1903(high)·NU1904(critical)를
restore 실패로 처리한다. 감사 피드에 연결되지 않은 실행을 취약점 0건으로 기록하지 않는다.

### 3.2 WiX v7 MSI/Burn 생성

최종 installer는 다음 세 실행 파일을 한 버전 세트로 만든다.

- 사용자 우선 dual-purpose MSI: `%LocalAppData%\Programs\ezy Image Viewer`, 일반 사용자 등록
- 컴퓨터 우선 dual-purpose MSI: `%ProgramFiles%\ezy Image Viewer`, 상승된 전체 사용자 등록
- Burn Setup: 기본은 현재 사용자이며 scope radio 선택에 따라 위 MSI 하나만 계획

두 MSI의 설치 내용과 등록 책임은 분리하되, WiX가 Burn 자체를 configurable-scope로 판정하도록
각 패키지 scope를 `perUserOrMachine`과 `perMachineOrUser`로 선언한다. WixStdBA의
`WixStdBAScope` 값에 따라 앱 MSI 하나만 설치된다. 시작 메뉴는 기본 켜짐이고 바탕 화면 바로가기와 png/jpg/jpeg/
bmp/gif/webp/tif/tiff의 Open With 등록은 기본 켜짐이다. 확장자 기본 handler 값은 쓰지 않는다.
`-DevelopmentUnsigned` 빌드는 Windows가 unsigned MSIX identity를 거부하므로 identity 등록 custom
action을 비활성화한다. 앱 파일·App Paths·바로가기·파일 연결은 그대로 설치한다. 승인된
서명으로 만드는 production 빌드만 identity와 CodecHost 등록을 수행한다.

서명하지 않은 개발용 정적 검증 후보는 다음처럼 명시적으로 만든다. 출력 디렉터리는 새 경로여야
하며 이 명령은 설치·package 등록·인증서 생성·신뢰 저장소 변경을 수행하지 않는다.

```powershell
powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `
  packaging\build-wix-installer.ps1 `
  -Version 1.0.9.0 `
  -CodecHostVersion 1.0.3.0 `
  -Publisher "CN=ezyImageViewer Dev" `
  -CodecHostPackage packaging\out\ezyImageViewer.CodecHost.msix `
  -EulaRtf installer\assets\EULA.rtf `
  -OutputDirectory installer\out\m9b-1.0.9-dev `
  -MinVersion 10.0.19041.0 `
  -DevelopmentUnsigned
```

진입점은 foundation stage/verify, manifest·package validation, 결정적 WiX fragment 2회 생성 비교,
세 WiX 프로젝트 locked restore, MSI·Burn build, MSI COM database verifier와 Burn extraction verifier,
SHA-256 생성을 한 번에 수행한다. 출력은 MSI 2개, Setup EXE, `installer-artifacts.json`,
`SHA256SUMS`, 수정된 Burn theme 원문 `EzyRtfLargeTheme.xml`, MS-RL 원문 `LICENSE-MRL.txt`다.
마지막 두 파일은 WiX 표준 theme 파생물의 reciprocal source 제공 계약이므로 공개 자산에서
제외하지 않는다.

production parameter set은 `-DevelopmentUnsigned`를 허용하지 않고 다음 값을 모두 요구한다.

```powershell
-CertificateThumbprint <승인된 current-user code-signing 인증서 thumbprint> `
-TimestampUrl <승인된 HTTPS RFC 3161 URL> `
-BuildToolsRoot <고정 Microsoft.Windows.SDK.BuildTools 물리 경로>
```

timestamp는 absolute HTTPS URL이어야 하며 자격 증명이나 fragment를 포함할 수 없다. BuildTools root는
`Directory.Packages.props`의 고정 버전과 이름이 일치하는 물리 디렉터리여야 하고 x64
`signtool.exe`가 정확히 하나여야 한다. 선택된 SignTool 자체도 Windows trust `Valid`, 인용·escape된
Subject 값과 구분한 exact `O=Microsoft Corporation` RDN, code-signing EKU를 통과해야 한다. 이
preflight와 인증서 선택은 output·working directory
생성과 payload staging보다 먼저 실행한다. 스크립트는 package Publisher와 인증서 Subject exact 일치,
private key, 유효기간, code-signing EKU를 확인하고 identity MSIX 두 개→앱 MSI 두 개를
SHA-256 Authenticode로 서명한다. 각 서명 뒤에는 SignTool Windows policy와
`Get-AuthenticodeSignature`의 `Status=Valid`, 선택 인증서 thumbprint exact, RFC 3161 timestamper 존재를
모두 검사한다. Burn은 WiX 7 CLI로 engine을 detach하고 engine을 서명·검증한 뒤 reattach한 전체
bundle을 다시 서명·검증한다. production 모드는 승인된 값으로 clean VM 검증 직전에만 실행한다.
hardware token/KSP 인증서는 PIN prompt가 생길 수 있으므로 무인 실행 가능성을 가정하지 않고 rehearsal에서
상호작용과 실패 복구를 확인한다. cloud/HSM signing은 provider가 확정된 뒤 별도 adapter로 설계한다.

비용 0원 공개 신뢰 경로는 SignPath Foundation을 우선 준비한다. public source remote는
`https://github.com/koprodev/ezyImageViewer`의 reviewed clean snapshot으로 고정한다.
아직 SignPath project·certificate·artifact configuration·provider adapter는 없다. PE/MSI/MSIX 형식 지원은
공식 reference에서 확인했지만 Burn의 2단계 remote signing 순서는 미확정이다. 또한 self-contained
  Windows App SDK의 Microsoft 전용 재배포 조건과 WinUI 2.2.1 `license.txt`의 Engineering Preview/live
  운영 제한이 production 신청·공개 전 차단 항목이다. PDFium/Chromium notice는 upstream
  `chromium/7690`에서 version-pinned로 확보해 CodecHost 계약에 hash-lock했다. Basic Portable은
  CodecHost를 포함하지 않으며 신청 자격을 주장하지 않는 testing prerelease다. 세부 판정은
`docs/signpath-readiness.md`와 `docs/code-signing-policy.md`를 따른다.

2026-07-19 사후 보완 뒤 최종 로컬 unsigned 1.0.9 개발 gate는 external payload 545개·264,034,230 bytes,
WiX 네 프로젝트 각각 경고 0·오류 0, MSI database 2,892 assertions, Burn extraction 50 assertions,
identity backend 21건을 통과했다. `installer/out/m9b-1.0.9-dev-final5`의 세 실행 artifact는 모두
`NotSigned`이며 metadata·`SHA256SUMS`·MRL source/license mirror가 일치하고 working staging 잔여는
0이다. 이 경로와 hash는 로컬 개발 증거이지 공개 릴리스나 설치 완료 증거가 아니다.

## 4. 후보 패키지 생성

주 패키징 스크립트는 두 MSIX를 고유 staging 폴더에서 만들고 실물 검증을 통과시킨 뒤,
배타적 writer lock 아래 기존 최종 쌍을 백업하고 순서대로 승격한다. 처리 가능한 오류가 나면
직전 쌍을 복구하고 staging을 정리한다. 이 절차는 동시 writer와 일반 예외를 방어하지만, 두 고정
파일 교체 사이의 전원 중단까지 reader-atomic하게 만드는 구조는 아니다. 공개 업로드는 스크립트
성공 후 verifier와 release manifest가 다시 확인한 한 쌍만 대상으로 한다.

패커는 각 build output을 해당 MSBuild `FileListAbsolute`와 대조해 누락·미선언·stale
파일을 거부하고, PDB/ref/NativeAotProbe를 복사하지 않는다. package 전용 로고도 승인된 세
파일만 명시적으로 복사한다. 최종 layout에는 나머지 모든 payload 경로와 SHA-256을 담은
`PACKAGE-CONTENTS.sha256`을 넣는다.

### 4.1 서명하지 않은 CI 후보

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File packaging\pack-msix.ps1 `
  -Version 1.0.9.0 `
  -CodecHostVersion 1.0.3.0 `
  -Publisher "CN=ezyImageViewer Dev" `
  -SkipSign
```

`-SkipSign` 후보는 패키지 구조·내용·SBOM 재현성 검사용이며 사용자 배포용이 아니다. 스크립트는
오래된 `ezyImageViewer-dev.cer`를 남기지 않아 unsigned 후보와 이전 인증서가 한 세트처럼 보이는
상태를 차단한다.

### 4.2 명시적으로 승인된 서명 후보

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File packaging\pack-msix.ps1 `
  -Version 1.0.9.0 `
  -CodecHostVersion 1.0.3.0 `
  -Publisher "<승인된 X.500 Publisher>" `
  -CertificateThumbprint "<승인된 인증서 thumbprint>"
```

- 스크립트는 유효기간, private key, code-signing EKU, Publisher 일치와 단일 인증서 선택을
  확인한다.
- `-CreateDevCertificate`는 개발 인증서를 새로 만드는 명시적 opt-in이며 production 절차가 아니다.
- production timestamp, 인증서 체인, Store identity 정책은 아직 미결정 출시 차단 항목이다.
- 인증서 설치·신뢰 저장소 변경은 별도 사용자 승인과 깨끗한 검증 환경에서만 수행한다.

## 5. 실제 MSIX 검증

다음 검증은 패키지를 설치하지 않고 BuildTools의 `makeappx unpack`으로 임시 폴더에만 푼다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File packaging\verify-msix-release.ps1 `
  -MainPackage packaging\out\ezyImageViewer.msix `
  -CodecHostPackage packaging\out\ezyImageViewer.CodecHost.msix `
  -Version 1.0.9.0 `
  -CodecHostVersion 1.0.3.0 `
  -Publisher "CN=ezyImageViewer Dev" `
  -RequireBuildOutputMatch
```

검증기는 다음을 fail-closed로 확인한다.

- 두 identity의 이름·Publisher·버전·x64 architecture
- 주 앱의 CodecHost dependency Publisher와 최소 버전
- Host가 activation/capability가 없는 framework package인지 여부
- 주 앱에 CodecHost/PDFtoImage/Magick/PDFium/PDB가 섞이지 않았는지 여부
- Host에 진단 processor/PDB/주 앱 실행 파일이 섞이지 않았는지 여부
- 내장 `PACKAGE-CONTENTS.sha256`과 실제 payload의 경로 집합·각 SHA-256 일치
- 주 앱 실행 파일/XBF/PRI/CodecProtocol과 Host 실행 파일·PDFium·Magick·Skia 네이티브
  경계 파일이 모두 있는지 여부
- Material Symbols TTF가 ADR-0013의 고정 SHA-256과 일치하고 Apache-2.0 원문이 동봉됐는지 여부
- 두 패키지에 `LICENSE.txt`와 `THIRD-PARTY-NOTICES.md`가 있는지 여부
- `-HashesFile` 지정 시 정렬·중복·경로·SHA-256을 포함한 전체 hash 계약

검증기는 trust anchor 없이 자체 선언 inventory만 확인하는 실행을 거부한다. unsigned CI·로컬
후보는 `-RequireBuildOutputMatch`로 MSBuild output과 고정 패키징 자산의 전체 경로·SHA-256을
대조한다. 정식 서명 정책 확정 뒤에는 `-RequireSignature`를 사용한다. 이 옵션은 Windows trust
policy로 두 서명을 검증하며 신뢰 저장소를 변경하지 않는다. 두 스위치를 함께 사용할 수도 있다.

## 5.1 AppInstaller 오프라인 생성·검증

현재 제품 최소 OS인 Windows 10 build 19041을 유지하기 위해 1803부터 지원되는 AppInstaller
`2017/2` namespace를 사용한다. 생성기는 identity 값을 중복 인수로 받지 않고 실제 두 MSIX의
`AppxManifest.xml`에서 Name·Publisher·Version·x64 architecture를 읽는다. Host가 framework인지,
주 앱 `PackageDependency`가 후보 Host를 정확히 가리키는지도 함께 검사한다.
스키마·수동 생성 계약은 Microsoft의 [App Installer schema version reference](https://learn.microsoft.com/en-us/uwp/schemas/appinstallerschema/app-installer-file)와
[manual creation guide](https://learn.microsoft.com/en-us/windows/msix/app-installer/how-to-create-appinstaller-file)를 기준으로 한다.

공개 endpoint가 아직 결정되지 않았으므로 다음 `example.invalid` 값은 오프라인 계약 검증
전용이며 게시하면 안 된다. 기본 생성에는 `UpdateSettings`가 없다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File packaging\generate-appinstaller.ps1 `
  -MainPackage packaging\out\ezyImageViewer.msix `
  -CodecHostPackage packaging\out\ezyImageViewer.CodecHost.msix `
  -OutputPath packaging\out\ezyImageViewer.appinstaller `
  -AppInstallerUri https://example.invalid/ezyimageviewer/ezyImageViewer.appinstaller `
  -MainPackageUri https://example.invalid/ezyimageviewer/ezyImageViewer.msix `
  -CodecHostPackageUri https://example.invalid/ezyimageviewer/ezyImageViewer.CodecHost.msix

powershell -NoProfile -ExecutionPolicy Bypass -File packaging\verify-appinstaller-release.ps1 `
  -AppInstallerFile packaging\out\ezyImageViewer.appinstaller `
  -MainPackage packaging\out\ezyImageViewer.msix `
  -CodecHostPackage packaging\out\ezyImageViewer.CodecHost.msix `
  -AppInstallerUri https://example.invalid/ezyimageviewer/ezyImageViewer.appinstaller `
  -MainPackageUri https://example.invalid/ezyimageviewer/ezyImageViewer.msix `
  -CodecHostPackageUri https://example.invalid/ezyimageviewer/ezyImageViewer.CodecHost.msix
```

OnLaunch profile은 기존 verifier의 음성·호환성 회귀 fixture로만 보존한다. 최종 배포 정책은
자동 버전 확인과 OS-level 자동 업데이트를 모두 제외하므로 정식 후보를 OnLaunch로 생성하거나
`.appinstaller`를 사용자 배포 채널로 게시하지 않는다.

검증기는 DTD/외부 entity, BOM·비ASCII, 2017/2 외 namespace, 알 수 없는/중복 요소·속성,
actual MSIX와 다른 identity, HTTP/상대 URI, userinfo/query/fragment, 잘못된 파일명을
fail-closed로 거부한다. 다음 harness는 정상 기본/OnLaunch와 대표 변이들을 실제로 실행한다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File packaging\test-appinstaller-contract.ps1 `
  -MainPackage packaging\out\ezyImageViewer.msix `
  -CodecHostPackage packaging\out\ezyImageViewer.CodecHost.msix
```

`ms-appinstaller:` protocol은 Microsoft 정책상 기본 비활성 상태이므로 배포 링크로 사용하지
않는다([App Installer overview](https://learn.microsoft.com/en-us/windows/msix/app-installer/app-installer-file-overview)). 공개 시에는 HTTPS의
`.appinstaller` 파일을 직접 내려받아 실행하는 경로를 사용하고, 서버가
`.appinstaller=application/appinstaller`, `.msix=application/msix`, 정확한 Content-Length를
GET/HEAD에 제공하는지 확인한다. immutable versioned MSIX 두 개를 먼저 업로드해 hash·MIME·길이를
검증한 뒤 canonical AppInstaller를 마지막에 교체한다.

## 6. 릴리스 메타데이터

최종 후보에 대해서만 `packaging/generate-release-metadata.ps1`을 실행한다. 생성되는 최소 산출물은
다음과 같다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File packaging\generate-release-metadata.ps1 `
  -Version 1.0.9.0 `
  -CodecHostVersion 1.0.3.0 `
  -OutputDirectory packaging\out `
  -MainMsix packaging\out\ezyImageViewer.msix `
  -CodecHostMsix packaging\out\ezyImageViewer.CodecHost.msix `
  -AppInstallerFile packaging\out\ezyImageViewer.appinstaller `
  -MainDepsJson EzyImageViewer.App\bin\packaged\x64\Release\net10.0-windows10.0.26100.0\win-x64\ezyImageViewer.deps.json `
  -CodecHostDepsJson EzyImageViewer.CodecHost\bin\Release\net10.0\win-x64\EzyImageViewer.CodecHost.deps.json `
  -MainProjectAssetsJson EzyImageViewer.App\obj\packaged\project.assets.json `
  -CodecHostProjectAssetsJson EzyImageViewer.CodecHost\obj\project.assets.json
```

- `SHA256SUMS.txt`: 두 MSIX와 AppInstaller의 basename ordinal 정렬, SHA-256 재검증 완료
- `release-manifest.json`: 세 artifact 이름·role·버전·hash, 각 런타임
  `.deps.json` 이름·SHA-256·target을 묶은 결정적 manifest
- CycloneDX 1.6 SBOM: 주 앱과 CodecHost의 실제 win-x64 `.deps.json` 런타임 종속성
  분리와 Material Symbols font의 고정 commit·SHA-256·Apache-2.0 provenance

SBOM의 NuGet SHA-512는 각 복원 폴더의 실제 `.nupkg`를 다시 해시하고 정확히 하나인
`.nupkg.sha512` sidecar와 일치할 때만 기록한다. `.deps.json`의 `sha512`는 NuGet의 정규화된
content hash이므로 raw `.nupkg` 파일 hash와 같은 값이라고 가정하지 않는다.

외부로 전달한 두 `.deps.json`은 각각 MSIX 루트의 동명 항목과 바이트 SHA-256이
정확히 일치해야 한다. 이름·바이트·`.NETCoreApp,Version=v10.0/win-x64` target 중 하나라도
다르면 metadata 생성을 중단하므로 다른 dependency graph를 SBOM으로 바꿔치기할 수 없다.

SBOM은 test/build-only 패키지와 다른 OS의 runtime asset을 제품 runtime으로 섞지 않는다.
라이선스 표현은 로컬 NuGet nuspec과 package 고지 파일에서 재현하되, 생성 결과를 법무 검토
완료로 간주하지 않는다.

## 7. 깨끗한 VM lifecycle 게이트

정식 배포 전에는 스냅샷으로 되돌릴 수 있는 깨끗한 Windows VM에서 다음을 수동·자동 혼합으로
확인한다. 기존 사용자 PC의 package를 검증 편의상 제거하지 않는다.

Windows 10 일반 채널 22H2 build 19045는 최종 Windows 10 release지만 일반 지원은
2025-10-14 종료됐다. 2026 이후 보안 상태는 ESU enrollment 또는 LTSC edition에 따라 달라지므로 VM
edition·build·patch·ESU 상태를 증거에 기록한다. full lifecycle은 19045 x64 대표 VM에서 수행하고,
최소 조건 19041 x64는 격리된 snapshot에서 install/launch smoke를 별도로 남긴다. 신형 Snipping Tool
callback은 현재 Windows 11 VM에서 분리 검증한다.

1. Burn 기본 per-user, Burn 선택 per-machine, standalone MSI 두 개를 각각 새 snapshot에서 설치한다.
2. per-machine 로그에서 Host와 main 모두 `Add-AppxProvisionedPackage -SkipLicense`가 성공하고,
   main은 먼저 설치 폴더를 external location으로 stage했는지 확인한다.
3. 설치 범위·경로·UAC, 설치 후 실행, 시작 메뉴와 opt-in 옵션, installed app 항목을 확인한다.
4. 설치된 sparse main/CodecHost package full name·Publisher·버전·architecture와 external location이
   실제 설치 폴더와 정확히 일치하는지 확인한다.
5. 앱 기동, 일반 이미지 열기, PDF/PSD 격리 smoke, 공식 캡처 callback을 실행한다.
6. 이전 승인 버전 A에서 후보 B로 같은 scope 업그레이드하고 설정·최근 파일·복구 marker 보존을 확인한다.
7. 같은 Name의 기존 full MSIX가 있는 snapshot에서는 신규 설치가 ownership 오류로 중단되는지,
   기존 앱을 Windows 설정에서 의도적으로 제거한 migration snapshot에서는 신규 설치가 성공하는지
   각각 확인한다. 기존 사용자 PC의 package를 테스트 편의상 제거하지 않는다.
8. 더 낮은 Host A와 더 높은 Host C가 각각 선설치된 상태에서 후보 B의 실제 선택 Host와 protocol
   호환성을 확인한다.
9. 네트워크 단절 상태에서도 앱 자체가 update 요청을 만들지 않는지 확인하고, 수동
   `업데이트 확인`은 OS 기본 브라우저 전달까지만 앱 책임으로 구분한다.
10. rollback 유도, repair, downgrade 차단, Windows 설정 제거를 실행해 identity·App Paths·바로가기·
   파일 연결 제거와 pre-existing Host 보존, 사용자 앱 데이터 유지 정책을 확인한다.
11. 손상 ownership state와 설치 EXE 누락을 각각 주입해 MSI 제거가 완료되고 경고가 남는지,
    실제 Appx 제거 실패는 오류로 유지되는지 확인한다.
12. per-machine은 둘 이상의 사용자로 등록/기동/제거 후 provisioning 잔존을 확인한다.
13. VM을 원래 스냅샷으로 되돌린다.

공식 callback 실측에는 신형 Microsoft Snipping Tool이 설치된 환경이 필요하다. 현재 개발 PC처럼
해당 앱이 없으면 clipboard 선착 경로만 검증되므로 callback PASS로 기록하지 않는다.

## 8. 공개 전 차단 항목

다음은 외부 환경·production 권한 또는 별도 법무 결정이 필요하다.

- production identity·Publisher·서명 인증서·timestamp 정책
- SignPath의 기존 release 증거, self-contained Microsoft redistributable 허용 범위, Burn 2단계
  signing 지원과 대한민국 개인 maintainer 자격의 명시적 확인
- Windows App SDK WinUI 2.2.1의 Engineering Preview/live 운영 제한 해소
- 기존 더 높은 CodecHost가 설치된 경우의 resolution 정책과 breaking protocol identity 정책
- 깨끗한 VM lifecycle, 접근성, 외부 포맷 corpus, 고정 기준 PC 성능 게이트
- 현재 저장소 MIT License를 대체하는 독점 EULA 채택 여부와 승인 원문. 개발용 `EULA.rtf`는
  현 MIT 원문을 그대로 반영하며 법무 결정 없이 독점 EULA로 바꾸지 않는다.
- PDFium/Chromium 제3자 고지의 최종 법무 검토, WiX OSMF의 조직 수익·후원 조건 및 Adobe/Photoshop 비제휴 표현의 최종 검토

이 항목이 남아 있는 후보에는 `release complete`, `production`, `공개 배포 완료` 표시를 사용하지
않는다.
