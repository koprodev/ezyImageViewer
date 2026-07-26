# ADR-0018: MSIX 전환·공식 캡처 프로토콜 (Q7=b)

- 상태: 채택 (2026-07-18, [25차] 최종 검수 보완 반영 — packaged 실동작 E2E는 설치 신뢰 후 수동 게이트)
- 선행: ADR-0017(M7 캡처 연동), Q7 사용자 결정 = b(packaged 재허용, 감사 §23.5)

## 배경

Q7 확정으로 FR-CAP-001의 최종 경로는 공식 Snipping Tool 프로토콜(MSIX identity + redirect-uri)이다. 개발 환경은 dotnet CLI 단독(VS·msbuild 부재 — ADR-0001)이라 VS 전용 Appx 패키징 target을 쓸 수 없고, 공식 문서(Launch Snipping Tool, 2026-04 개정)는 응답 수신에 packaged 앱과 커스텀 프로토콜 등록을 요구한다.

## 결정

1. **패키징 = 수동 레이아웃 + makeappx** (dotnet CLI 단독 실증). `packaging/pack-msix.ps1`(실행: `powershell -NoProfile -ExecutionPolicy Bypass -File`): packaged 전용 출력(`bin/packaged`·`obj/packaged` — [25차] 보완 4: `-NoBuild`가 dev flavor 산출물을 오포장하지 못함) 빌드 → 레이아웃 복사 → `AppxManifest.template.xml` 전개(버전·publisher) → `makeappx pack` → `signtool sign` → **공개키 `.cer` 상시 export**(깨끗한 PC의 신뢰 절차 재현). makeappx/signtool은 `Directory.Packages.props`에 고정된 `Microsoft.Windows.SDK.BuildTools` 버전 경로에서 가져온다(사전식 최신 폴더 선택 금지). 산출물·인증서는 `packaging/out`(gitignore).
2. **개발 루프는 unpackaged 유지.** csproj `WindowsPackageType=None`은 `-p:Packaged=true`가 아닐 때만 적용 — 테스트·smoke·일반 개발 실행은 기존 그대로, self-contained WASDK는 양쪽 공통. 배포 identity(`GRTech.ezyImageViewer`)·서명은 dev 자체 서명(`CN=ezyImageViewer Dev`, CurrentUser\My 자동 생성, Subject=Publisher 일치 검증) — **배포용 identity·인증서·채널 구성은 M9-B**.
3. **manifest 계약.** full-trust 데스크톱 앱(`EntryPoint=Windows.FullTrustApplication`+`runFullTrust`), 커스텀 프로토콜 `ezyimageviewer` 등록(`windows.protocol`) — `SnipProtocol.Scheme`과 일치가 계약이다. 파일 연결 등 나머지 manifest 확장은 M9-B.
4. **공식 요청(`SnipProtocol`, 순수).** `ms-screenclip://capture/image?rectangle&enabledModes=SnippingAllModes&user-agent=ezyImageViewer&api-version=1.2&x-request-correlation-id={guid}&redirect-uri=ezyimageviewer://capture-response`. api-version 1.2 고정(암묵 기본값 변동 회피, 문서 권고), mode 파라미터는 값 없음(규격), redirect-uri는 자체 query 없음 — 콜백 query 전체가 응답 파라미터다. 호출은 `Launcher.LaunchUriAsync` 필수(그 외 방법은 identity 미전달 → 응답 미배달, 규격).
5. **응답 수신 경로.** 콜백(`code`/`reason`/`x-request-correlation-id`/`file-access-token`)은 프로토콜 활성화로 도착 → 단일 인스턴스 redirect를 지나 활성화 라우터 → `WindowManager.Route`가 `SnipProtocol.IsResponse` 분기에서 **창 생성 후** `CaptureCoordinator.OnProtocolResponse(uri, coldStart)`로 전달한다. **cold start는 추론이 아니라 명시 신호다**([25차] 보완 2): `ProtocolActivation.IsInitial` — 프로세스를 기동시킨 최초 활성화만 true(Program.Main), warm redirect는 false. 토큰은 `SharedStorageAccessManager` 1회 상환(OS 계약 — 실패 시 재시도 불가, 상태 노출) + 캡처 읽기 예산 64MiB(경계 게이트는 WinRT와 분리해 단위 검증).
6. **조정자 정책(공식 경로, [25차] 보완 1·2·3 재설계).** 공식 요청은 클립보드 arm을 쓰지 않는다 — 요청마다 **불변 컨텍스트(correlation·deadline 60초·origin 창)** 를 만들고 콜백이 이를 **원자적으로 점유**한다: 점유 조건 = correlation 일치 **그리고** 마감 내. warm 콜백은 불일치·만료·선점됨(중복 재전달·위조 포함)이면 토큰 상환 없이 거부되고, cold start(명시 신호)만 컨텍스트 없는 200을 수용한다. 완료는 **자신의 컨텍스트 origin**에만 열고 전역 armed 상태를 건드리지 않는다 — 느린 상환 중 새 요청이 와도 대상이 뒤섞이지 않는다. passive 이미지는 in-flight 중 **폐기가 아니라 최신 1건 보류**: 완료 시 공식 결과와 byte 비교해 echo만 버리고 정당한 이미지는 알림으로 전달하며(실패·만료 시에도 전달), settle 5초 억제도 **byte-동일 echo에만** 적용된다. 시계는 주입 가능(`Clock`)해 60초/5초 경계가 테스트된다. 공식 launch 실패는 기존 armed-clipboard 계약으로 강등(Win+Shift+S 안내 — fallback은 경로)하고, 그 뒤 도착한 구 요청 콜백은 fallback을 교란하지 못한다. unpackaged(identity 부재)는 기존 legacy best-effort가 그대로 동작한다(`PackageIdentity` 런타임 실측 분기).
6a. **legacy 호스트 대응(수동 게이트 §24.6·§24.7).** 신형 Snipping Tool이 없는 기기(실측: ScreenSketch 미설치 — 콜백이 영영 오지 않고 결과는 클립보드뿐)를 위해 공식 요청은 **선착 채널 즉시 열기**다: 콜백과 클립보드 중 먼저 도착한 쪽이 요청 컨텍스트를 소진하고 즉시 열며, 늦은 쪽은 무컨텍스트로 자동 거부된다(이중 열기 불가 — 대기 지연 0, 사용자 피드백으로 초기 grace 2초 설계를 대체). 캡처 요청 시 origin 창은 **최소화**(`ICaptureTarget.PrepareForCapture`)되고 모든 완료 경로(성공 열기·취소·오류 포함)의 Activate가 복원한다(최소화 해제 포함). 아무 결과도 없는 방치(legacy Esc)는 **워치독(요청 61초)** 이 요청을 끝내고 창을 복원하며, legacy arm은 미소진 만료에만 복원한다(소진 후 포커스 탈취 금지). 워치독은 `Delay` 주입 seam으로 테스트된다.
7. **FR-CAP-003~005(감지·핫키·중복)은 변경 없음** — 클립보드 감지는 Win+Shift+S·passive 경로 수신으로 유지된다. FR-CAP-006 트레이는 2026-07-24 후속 결정으로 제거했다.

## 검증 계약

- 단위: `SnipProtocol`(요청 URI 규격·응답 파서 200/499/오류/대소문자/이물 스킴 거부 + manifest 스킴 계약 7건), `CaptureCoordinator` 공식 경로 14건(성공 상환·origin 열기 / stale correlation 무시+신규 완료 / 499 침묵+passive 복원 / 오류 상태 노출 / launch 실패 clipboard 강등 / cold-start 수용 / warm 무컨텍스트 거부 / 만료 콜백 거부(clock 주입) / 중복 전달 1회 상환 / 느린 상환·신규 요청 대상 격리(TCS) / fallback arm 교란 없음 / 토큰 누락·상환 예외 상태 노출 / held passive echo 판별·실패 시 전달), `CaptureTokenReader` 예산 경계 3건(정확 경계·+1·빈 파일).
- smoke(`--smoke-capture`): 공식 경로 E2E(주입 launcher가 correlation 포착 → 콜백 URI → 주입 redeem → 11×6 문서 실열림 = `captureOfficialOpened`) + `packageIdentity` 실측 필드 — 실제 Snipping Tool·클립보드 미접촉.
- 패키징: `pack-msix.ps1` 실행 = Packaged 빌드 경고 0·오류 0 → makeappx pack 성공(363파일) → 서명 성공 실측.
- 수동 게이트(이월): **설치 신뢰 1회(관리자)** — `certutil -addstore TrustedPeople packaging\out\ezyImageViewer-dev.cer` 후 `Add-AppxPackage packaging\out\ezyImageViewer.msix`(비관리자 시도 = 0x800B0109 실측) → packaged 실행에서 캡처 버튼 → 실캡처 → redirect 자동 열기, 취소(Esc) 침묵, Win+Shift+S 이중 제안 없음 확인.

## 결과와 트레이드오프

- dev 인증서·identity는 로컬 개발 전용이다 — 배포 서명 방식(구매 인증서/스토어), 채널 구성(MSIX↔MSI·포터블), 파일 연결 manifest 전환은 M9-B 재계획에서 확정한다.
- legacy `ms-screenclip:` 경로는 unpackaged 실행의 interim으로 남는다(개발 루프·비설치 실행) — 배포가 MSIX로 확정되면 사용자 배포본은 항상 공식 경로다.
- 프로토콜 응답이 오지 않는 비정상 종료(오버레이 강제 종료 등)는 워치독(61초)이 요청을 끝내고 창을 복원하며 passive 감시가 되살아난다.
- 전역 단축키 기본값은 **Ctrl+Shift+E**다(Q8 확정 — 초기값 Ctrl+Shift+X가 현 기기에서 타 앱 전역 선점 1409 실측, E는 FREE 실측). 선점 시 상태바 알림, 사용자 재정의는 M9-A. 신형 Snipping Tool 설치 시 공식 콜백 경로가 그대로 활성화된다(코드 분기 불요 — grace 설계가 양쪽을 흡수).
