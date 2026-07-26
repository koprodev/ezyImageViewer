# Microsoft Store 릴리스 절차

> Store ID: `9P82BRPVKC5N`
>
> 현재 상태: Microsoft Store 인증 대기

애플리케이션 바이너리는 Microsoft Store만 배포한다. GitHub는 공개 소스·문서·이슈
운영에만 사용하며 Releases, Portable, MSI/Setup, App Installer 파일을 게시하지 않는다.

## 1. 제출 전 확인

- 작업 트리와 제출 버전을 확인한다.
- Store 버전은 네 부분 숫자이며 네 번째 값은 `0`이어야 한다.
- Partner Center 값은 아래 세 항목과 정확히 일치해야 한다.

| 항목 | 값 |
|---|---|
| Identity Name | `koProDev.ezyImageViewer` |
| Publisher | `CN=C90B63D5-FCFF-4640-91DB-5547A8D3ECDA` |
| Publisher Display Name | `koProDev` |

## 2. 복원·빌드·테스트

프로젝트 규칙에 따라 실행 중인 앱을 먼저 종료하고 로컬 .NET SDK 경로를 적용한다.

```powershell
Get-Process ezyImageViewer -ErrorAction SilentlyContinue | Stop-Process -Force
$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"
$env:PATH="$env:DOTNET_ROOT;$env:PATH"

dotnet restore EzyImageViewer.slnx --locked-mode
dotnet build EzyImageViewer.slnx -c Release --no-restore
dotnet test EzyImageViewer.Tests/EzyImageViewer.Tests.csproj `
  -c Release --no-build
```

## 3. Store MSIX 생성·검증

아래의 `1.0.0.0`은 실제 제출 버전으로 바꾼다. `pack-msix.ps1`은 Store 전용 출력에서
MSIX를 만들고 패키지 내용·매니페스트·빌드 provenance를 검증한다.

```powershell
powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File packaging/pack-msix.ps1 `
  -Version 1.0.0.0 `
  -ReleaseVersion 1.0.0 `
  -IdentityName "koProDev.ezyImageViewer" `
  -Publisher "CN=C90B63D5-FCFF-4640-91DB-5547A8D3ECDA" `
  -PublisherDisplayName "koProDev" `
  -SkipSign -StoreChannel

powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File packaging/verify-msix-release.ps1 `
  -MainPackage packaging/out/ezyImageViewer.msix `
  -Version 1.0.0.0 `
  -IdentityName "koProDev.ezyImageViewer" `
  -Publisher "CN=C90B63D5-FCFF-4640-91DB-5547A8D3ECDA" `
  -PublisherDisplayName "koProDev" `
  -RequireBuildOutputMatch
```

`-SkipSign` 산출물은 Store 제출 전 구조 검증용이다. Store 서명과 설치·업데이트 동작은
Partner Center 제출 및 인증 결과로 확인한다.

## 4. 공개 소스 동기화

공개 소스 snapshot은 `packaging/public-source-allowlist.txt`만 포함한다.

```powershell
powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File packaging/test-public-source-snapshot-contract.ps1

powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File packaging/sync-public-source.ps1
```

동기화는 로컬 공개 작업 트리만 갱신한다. 개발 저장소와 공개 저장소의 commit·push는
각각 명시적으로 승인받은 뒤 별도 수행한다.

## 5. 완료 판정

- solution restore/build/test 성공
- Store MSIX의 identity, 언어, 파일 연결, protocol, capability, license, payload 검증 성공
- Microsoft Store 인증 성공과 실제 Store 설치·기동·업데이트 경로 확인
- 공개 소스 snapshot 계약 성공

로컬 MSIX 생성 성공만으로 Store 배포 완료라고 판정하지 않는다.
