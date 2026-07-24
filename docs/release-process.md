# 릴리스 절차

> 현재 계약: `v1.0.42-preview.1` / 앱 `1.0.38.0` / Portable `1.0.42-portable.1`
>
> 공개 저장소: `koprodev/ezyImageViewer`

공개 자산은 개인 평가·테스트용 unsigned preview다. production 서명본으로 표현하거나
신뢰된 설치 파일로 재배포하지 않는다.

## 1. 채널

### Installer + Portable preview

`packaging/preview-release.json`이 버전과 태그의 단일 기준이다.

공개 파일:

- `ezyImageViewerSetup-1.0.42-x64-dev-unsigned.exe`
- `ezyImageViewer.exe`
- `EzyRtfLargeTheme.xml`
- `LICENSE-MRL.txt`
- `preview-release-manifest.json`
- `SHA256SUMS.txt`

Setup은 per-user·per-machine MSI 중 하나를 고르는 WiX Burn 번들이다. 개발용 unsigned
빌드에서는 Windows package identity 등록을 끄며 앱 파일, App Paths, 바로가기, Open With
등록만 설치한다. Portable은 설치·레지스트리·바로가기를 만들지 않는다.

로컬에서 같은 묶음을 만들고 검증하려면 다음 명령을 쓴다.

```powershell
powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File packaging/build-preview-release.ps1 `
  -OutputDirectory packaging/out/preview-1.0.42

powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File packaging/verify-preview-release.ps1 `
  -OutputDirectory packaging/out/preview-1.0.42
```

### Basic Portable preview

초기 비교용 계약은 `packaging/portable-release.json`의 `0.1.0-portable.1`이다.
공개 파일은 ZIP, `SHA256SUMS.txt`, `portable-release-manifest.json`이다.

```powershell
powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File packaging/build-portable-release.ps1 `
  -Version 0.1.0-portable.1 -OutputDirectory packaging/out/portable

powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File packaging/verify-portable-release.ps1 `
  -Version 0.1.0-portable.1 -OutputDirectory packaging/out/portable
```

## 2. 공개 소스

공개 소스는 개발 저장소 전체가 아니라
`packaging/public-source-allowlist.txt`의 allowlist로 만든 검토용 snapshot이다.
`packaging/new-public-source-snapshot.ps1`이 `git archive` 결과에서 허용 경로만 복사하고
`PUBLIC-SOURCE-MANIFEST.json`에 원본 commit과 파일 hash를 기록한다.

내부 협업 문서, 로컬 설계 자료, Git 이력, 개인 설정, 인증 정보는 공개 snapshot에 넣지 않는다.
공개 작업 트리는 기본적으로 형제 폴더 `ezyImageViewer-public`을 사용한다.

게시 전 계약:

```powershell
powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File packaging/test-publication-readiness-contract.ps1

powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File packaging/test-public-source-snapshot-contract.ps1
```

## 3. 로컬 빌드

버전·commit·GitHub 상태를 바꾸지 않는 개발 진입점:

```powershell
tools\build-portable.cmd
tools\build-installer.cmd
```

- Portable: 현재 작업 트리를 단일 EXE로 만들고 실행 스모크까지 확인한다.
- Installer: 개발용 unsigned MSI 2종과 Setup을 만들고 번들 구조를 확인한다.
- 산출물: gitignore된 `packaging/out`·`installer/out` 아래에만 둔다.
- 설치: 스크립트가 자동으로 실행하지 않는다.

자세한 옵션은 [`tools/README.md`](../tools/README.md)를 따른다.

## 4. 게시 절차

릴리스 노트를 먼저 갱신한다.

- `docs/preview-release-notes.md`
- 기능·제약·파일 이름·unsigned 경고를 실제 결과와 맞춘다.
- 이전 버전에서 달라진 내용을 사용자 관점으로 적는다.

전체 게시 진입점:

```powershell
tools\release.cmd
```

실행 순서:

1. preview 버전 결정과 문서 토큰 갱신
2. locked restore, Release build, 테스트, 패키징 계약 실행
3. 개발 저장소 릴리스 commit 생성
4. 공개 snapshot 동기화·push
5. `.github/workflows/release-preview.yml` 실행
6. 게시 자산 다운로드
7. `SHA256SUMS.txt` 전건 대조와 Portable 실행 검증

주요 보조 명령:

```powershell
tools\release.cmd -DryRun
tools\release.cmd -Bump none
tools\release.cmd -Version 1.2.0
tools\release.cmd -EditNotes
tools\release.cmd -Watch
```

`-Watch`는 push 뒤 로컬 감시가 끊겼을 때 실행 중 workflow에 다시 붙는다. 게시한 태그는
덮어쓰지 않는다. 실패를 고친 뒤 preview 번호를 올려 새 태그로 게시한다.

## 5. 필수 검증

CI와 로컬 게시 게이트는 다음을 구분해 확인한다.

- solution restore/build/test
- public snapshot allowlist와 민감 파일 이름 차단
- Portable exact payload, 추출, 실행 스모크, `NotSigned`
- MSI database와 Burn payload, scope 선택, Open With 계약
- release manifest의 tag·version·source commit·파일 hash
- 공개 자산 이름과 개수
- `SHA256SUMS.txt` 정렬·누락·hash 일치
- WiX theme source와 MS-RL 원문 동봉

unsigned preview 성공은 production signing, SmartScreen 신뢰, package identity 등록, clean VM
install/repair/upgrade/rollback/remove 검증을 대신하지 않는다.

## 6. production 보류 조건

다음 항목은 아직 완료되지 않았다.

- Windows App SDK Engineering Preview 배포 조건 해소
- SignPath Foundation 승인과 실제 signing workflow
- production Publisher, signer, RFC 3161 timestamp
- clean VM의 CurrentUser·AllUsers lifecycle
- 실제 package identity에서 Snipping Tool callback
- 최종 라이선스·개인정보·SBOM 검토

자세한 판정은 [코드 서명 정책](code-signing-policy.md)과
[SignPath 준비 점검](signpath-readiness.md)을 따른다.
