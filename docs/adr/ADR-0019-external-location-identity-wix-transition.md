# ADR-0019: external-location identity와 WiX 설치 구조

- 상태: Accepted — 구현·read-only artifact 검증 완료, production/clean VM lifecycle 전
- 결정일: 2026-07-19
- 범위: M9-B Windows 설치·배포 전환

## 맥락

ezy Image Viewer는 일반 WinUI 3 실행 파일을 사용자가 선택한 폴더에 설치하면서 Snipping Tool
callback과 격리 CodecHost dependency에 필요한 package identity를 가져야 한다. Microsoft의
external-location package는 기존 설치 프로그램과 실행 파일 위치를 유지한 채 identity를 부여할
수 있지만, 설치 scope별 등록 권한과 제거 책임은 설치 프로그램이 직접 관리해야 한다.

여기서 **per-user**는 현재 Windows 계정만의 설치·등록, **per-machine**은 이 컴퓨터의 모든
사용자를 위한 상승된 설치·provisioning을 뜻한다. **Burn**은 두 고정 MSI 중 하나를 선택·실행하는
WiX bootstrapper이며 앱 MSI 자체를 dual-purpose로 만들지 않는다.

## 사용자 확정 결정

| 항목 | 결정 |
|---|---|
| 지원 OS | Windows 10 build 19041 이상 x64. 모든 호환 Windows 11 x64 포함 |
| 앱 MSI scope | 고정 per-user MSI와 고정 per-machine MSI 분리 |
| 일반 설치 UX | Burn에서 현재 사용자/모든 사용자 선택, 기본은 현재 사용자 |
| WiX | SDK·UI·Util·BootstrapperApplications 7.0.0, `AcceptEula=wix7` 승인 |
| 파일 연결 | 기본 등록, 옵션에서 해제 가능, 기존 기본 앱 비강제 |

WiX 승인은 개발·CI 도입에 대한 사용자 승인이다. OSMF의 조직 수익·후원·계약 조건과 제품
라이선스 법무 판정을 대신하지 않는다.

## 결정

### 1. identity와 payload 불변 조건

1. 주 앱 sparse identity는 `GRTech.ezyImageViewer`, application `App`, x64 실행 파일과 같은
   Publisher를 사용한다.
2. CodecHost는 `GRTech.ezyImageViewer.CodecHost` x64 framework MSIX이며 activation surface와
   capability가 없다.
3. 주 앱은 Host 이름·Publisher·최소 버전을 exact dependency로 선언한다.
4. sparse package는 `AllowExternalContent=true`, `Architecture=neutral`, Windows build 19041
   baseline을 유지한다.
5. 실행 파일 side-by-side manifest와 sparse package identity는 생성 뒤 `mt.exe` 재추출로 다시
   검증한다.
6. MSI payload는 self-contained external publish inventory와 exact 일치해야 하며 PDB, Host
   binary, PDFium, SDK tool, 미선언 stale 파일을 거부한다.
7. 설치 순서는 Host→main, 제거는 main→Host다. 설치 전 package는 소유하지 않으며 rollback·제거
   중 보존한다.

### 2. MSI와 Burn scope

- per-user layout MSI는 `Scope=perUserOrMachine`, 기본 `%LocalAppData%\Programs\ezy Image Viewer`,
  HKCU registry key path, impersonated deferred identity action을 사용한다.
- per-machine layout MSI는 `Scope=perMachineOrUser`, 기본 `%ProgramFiles%\ezy Image Viewer`,
  file key path, HKLM과 non-impersonated deferred identity action을 사용한다.
- MSI마다 별도 UpgradeCode와 component GUID 집합을 유지한다. `MajorUpgrade`로 낮은 버전을
  교체하고 downgrade를 차단한다. scope 간 자동 migration은 하지 않는다.
- 두 앱 MSI 자체가 dual-purpose이므로 Burn은 configurable-scope bundle로 판정한다.
  WixStdBA가 설정하는 `WixStdBAScope` 값에 따라 해당 layout MSI 하나만 계획된다.
- Burn은 scope별 설치 경로 변수를 따로 보존한다. 하나의 사용자 경로가 상승된 install에
  재사용되지 않는다.

### 3. 등록 backend와 transaction

- `identity-registration-backend.ps1`은 bounded ZIP/XML parsing으로 MSIX manifest를 읽고 예상
  identity, Publisher, version, architecture, framework/dependency 계약을 재검증한다.
- CurrentUser는 `Add-AppxPackage`로 Host를 등록한 뒤 main을 `ExternalLocation`과 등록한다.
- AllUsers는 Host와 main을 stage한 뒤 라이선스 파일이 없는 LOB package 계약에 맞춰 둘 다
  `SkipLicense`로 online provision한다. main의 external location은 Microsoft의 build 19041 호환
  절차대로 stage 단계에 전달하며, 최신 로컬 모듈에만 보이는 provisioning 인자를 요구하지 않는다.
- 상태 파일은 설치 폴더의 `InstallerResources` 아래에 원자적으로 기록하며 pre-existing 여부와
  이번 transaction 소유 단계를 구분한다.
- 실패 시 완료 단계만 역순 rollback한다. Host 제거 전 다른 dependent가 있으면 제거하지 않는다.
- 기존 full MSIX와 같은 main identity가 state 없이 존재하면 자동 migration·제거하지 않고 명확한
  ownership 오류로 중단한다. 정상 uninstall에서는 손상·불일치 state로 ownership을 확인할 수 없는
  경우 package를 보존한 채 MSI 파일 제거를 계속하지만 rollback과 실제 package 제거 실패는 엄격하다.
- `invoke-identity-registration.ps1`은 x64와 build 19041을 다시 검사하고 Register, Unregister,
  Rollback 진입점을 제공한다. MSI custom action Target 길이를 넘기지 않도록 나머지 resource
  path는 invoker 위치에서 안전하게 파생한다.

### 4. 사용자 옵션과 설치 후 실행

- 시작 메뉴 바로가기와 파일 연결은 기본 켜짐이다. 바탕 화면 바로가기와 파일 연결 feature는
  각각 level 2의 property 조건으로 제어하며 Burn checkbox의 기본값은 바탕 화면 `0`, 파일 연결 `1`이다.
- 파일 연결은 png, jpg, jpeg, bmp, gif, webp, tif, tiff의 ProgID, OpenWithProgids,
  Capabilities, RegisteredApplications만 쓴다. 확장자 기본값은 쓰지 않는다.
- 두 MSI는 같은 scope의 `App Paths\ezyImageViewer.exe`에 실제 실행 파일 경로를 등록한다.
  Burn WixStdBA의 단일 `LaunchTarget=ezyImageViewer.exe`는 Windows Shell이 이 값을 해석하므로
  선택된 scope의 물리 경로를 추측하지 않는다.
- standalone MSI는 `WixUnelevatedShellExec`으로 신규 설치 완료 시에만 일반 사용자 실행을
  제공한다.

### 5. 서명과 배포 고지

- 개발 gate는 `-DevelopmentUnsigned`를 명시하며 파일 이름과 metadata에 unsigned 상태를 남긴다.
  Windows가 unsigned MSIX identity 등록을 거부하므로 이 빌드는 `EZY_REGISTER_IDENTITY=0`으로
  identity custom action을 건너뛰고 일반 데스크톱 앱·App Paths·파일 연결만 설치한다.
  identity와 CodecHost 등록은 production 서명 빌드에서만 활성화한다.
- production parameter set은 승인된 current-user code-signing 인증서 thumbprint, HTTPS
  RFC 3161 timestamp URL, 고정 BuildTools 경로를 모두 요구한다. Subject=Publisher, private key,
  유효기간, code-signing EKU를 output 생성 전에 확인한다. 선택된 x64 SignTool 자체도 Windows trust
  `Valid`, quoted/escaped Subject 값과 구분한 exact `O=Microsoft Corporation` RDN, code-signing EKU를
  통과해야 한다. 산출물은 SignTool Windows
  policy뿐 아니라 선택 signer thumbprint exact와 RFC 3161 timestamper 존재를 후검증한다.
- Burn은 engine detach→engine 서명→reattach→전체 bundle 서명의 WiX 공식 2단계 절차를 사용한다.
- local CSP/KSP·hardware token 경로를 우선하며 PIN 상호작용은 rehearsal에서 확인한다. provider가
  확정되지 않은 cloud/HSM adapter는 선제 도입하지 않는다.
- 현재 `installer/assets/EULA.rtf`는 저장소 MIT License를 반영한다. 배포 참고 문서의 과거 독점
  EULA 제안은 승인된 원문과 법무 결정 전에는 적용하지 않는다.
- Burn theme는 WiX v7.0.0 `RtfLargeTheme.xml` 파생물이므로 원 저작권 고지를 유지하고 수정 XML
  source와 `LICENSE-MRL.txt`를 Setup과 함께 배포·checksum한다.

## 검증 전략과 허용한 경고 억제

`build-wix-installer.ps1`은 foundation stage/verify, 결정적 fragment 재생성 비교, locked restore,
MSI 2개·Burn build, Windows Installer COM database 검사, Burn extract와 payload
SHA-512 검사, 공개 artifact SHA-256 생성을 한 진입점으로 묶는다. verifier는 설치나 registry,
trust store, package registration을 변경하지 않는다.

다음 억제는 실제 package 내용과 독립 verifier를 함께 둔 좁은 예외다.

| 억제 | 범위·근거 |
|---|---|
| ICE03 | Microsoft Windows App SDK XAML MUI version resource의 LCID를 ICE03가 거부. custom action Target 길이와 MSI table 계약은 별도 verifier가 검사 |
| ICE60 | app-local Material Symbols font를 system FontsFolder에 등록하지 않는 의도적 private font |
| ICE91 | per-user layout MSI에만 적용. 번들이 선택하지 않는 per-machine transition을 전제한 경고 |
| WIX1140 | configurable bundle의 기본 registration scope만 보고 조건부 per-machine MSI를 경고. verifier가 두 dual-purpose package와 `WixStdBAScope` 조건을 검사 |

억제 목록을 확대할 때는 해당 행의 실제 원인, 제한된 scope, 대체 검증을 함께 추가해야 한다.

## 결과와 남은 gate

구현 결과는 Windows 10 build 19041+ x64, 분리 MSI, Burn scope 선택, 기본 등록 파일 연결이라는 사용자
결정을 소스와 read-only 산출물 계약에 반영한다. MRL source/license도 설치 artifact 집합에 포함한다.

다만 다음은 아직 입증하지 않았다.

- production Publisher·인증서 체인·timestamp와 실제 Authenticode 서명
- clean VM의 UAC, CurrentUser/AllUsers registration·provisioning, 설치/repair/upgrade/downgrade/
  rollback/remove와 다중 사용자 lifecycle
- 실제 설치 identity에서 Snipping Tool callback과 CodecHost framework resolution
- 더 높은 기존 Host의 protocol compatibility와 breaking-version identity 정책
- PDFium/Chromium upstream 고지, OSMF 조직 조건, 최종 제품 EULA 법무 검토

따라서 unsigned 개발 artifact를 `production`, `release complete`, `일반 사용자 배포 가능`으로
표시하지 않는다.

## 근거

- [Grant package identity by packaging with external location](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps)
- [Application Registration — App Paths](https://learn.microsoft.com/en-us/windows/win32/shell/app-registration)
- [Microsoft ICE105](https://learn.microsoft.com/en-us/windows/win32/msi/ice-105)
- [WiX Burn bundles](https://docs.firegiant.com/wix/tools/burn/)
- [WiX configurable-scope bundles](https://docs.firegiant.com/wix/whatsnew/configurable_scope_bundles/)
- [WiX package scope](https://docs.firegiant.com/wix/schema/wxs/packagescopetype/)
- [WiX Open Source Maintenance Fee](https://docs.firegiant.com/wix/osmf/)
- [WiX v7.0.0 RtfLarge theme source](https://github.com/wixtoolset/wix/blob/v7.0.0/src/ext/Bal/stdbas/Resources/RtfLargeTheme.xml)
