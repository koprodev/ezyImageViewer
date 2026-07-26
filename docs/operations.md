# ezy Image Viewer 운영 및 로컬 데이터 안내

이 문서는 현재 제품 구현을 기준으로 로컬 데이터, 구조화 로그, 비정상 종료 복구와 안전 모드의 동작을 설명한다. 내부 벤치마크·스모크 실행 경로는 개발 및 검증 전용이며 일반 사용자용 장애 조치 명령이 아니다. 이 문서에서는 해당 실행 인수를 안내하지 않는다.

## 1. 로컬 데이터 위치

일반 실행의 데이터 루트는 다음과 같다.

```text
%LOCALAPPDATA%\ezyImageViewer
```

PowerShell에서 실제 경로를 확인할 때는 다음 읽기 전용 명령을 사용할 수 있다.

```powershell
$dataRoot = Join-Path $env:LOCALAPPDATA 'ezyImageViewer'
Get-Item -LiteralPath $dataRoot -Force
```

데이터 루트가 아직 생성되지 않았거나 앱이 로컬 데이터를 기록하기 전에 종료되었다면 `Get-Item`이 경로 없음 오류를 표시할 수 있다. 내부 진단 실행은 일반 데이터와 섞이지 않도록 시스템 임시 폴더 아래의 격리된 데이터 루트를 사용할 수 있다.

| 경로 | 현재 저장 내용 | 개인정보·운영 특성 |
|---|---|---|
| `settings.json` | 테마, 창·도구 기본값, 캡처 단축키와 개인정보 관련 설정 | 이미지 원본은 없으며 최대 64 KiB이다. |
| `recent-files.json` | 최근에 연 파일의 전체 로컬 경로와 마지막 열기 UTC 시각 | 기본 최대 20개, 파일 최대 1 MiB이다. 원문 경로가 있으므로 외부 공유 전 확인해야 한다. 최근 파일 기록을 끄면 앱이 이 저장소를 비운다. |
| `logs\` | 한 줄에 JSON 객체 하나인 구조화 로컬 로그 | 원문 문서 경로와 예외 메시지는 저장하지 않는다. 상세 정책은 2절을 따른다. |
| `recovery\` | 수정 중인 창별 복구 체크포인트 | 원본 이미지 바이트와 주석을 포함한 프로젝트 payload가 들어갈 수 있다. 암호화 저장소가 아니다. |
| `recovery-quarantine\` | 무결성 또는 형식 검증에 실패해 격리된 복구 파일·충돌 표식 | 손상되었더라도 원본 내용이 남아 있을 수 있으므로 민감 데이터로 취급한다. |
| `crash-markers\` | 실행 세션 ID와 시작 UTC 시각 | 이미지·문서 경로·예외 메시지는 저장하지 않는다. |
| `startup-health.json` | 시작 실패 fingerprint, 연속 횟수, 마지막 실패 UTC 시각 | 최대 4 KiB이다. 예외 메시지·경로·문서 내용은 저장하지 않는다. 정상 시작이 확인되면 앱이 상태를 정리한다. |

일시적인 잠금 파일이나 원자적 쓰기용 보조 파일이 보일 수 있다. 앱이 실행 중일 때는 이 파일들을 수정하거나 데이터 루트 일부를 교체하지 않는다.

## 2. 구조화 로그와 개인정보

로그 파일 이름은 `ezy-yyyyMMdd-NNN.jsonl` 형식이다. 각 행에는 다음 중 해당하는 필드만 기록된다.

- UTC 시각, 로그 수준, 이벤트 이름
- 앱 버전과 운영체제 설명
- 오류 코드, 렌더러, 포맷, 경과 시간
- 문서 경로 식별자와 예외 타입 이름

문서 경로는 원문으로 기록하지 않는다. 프로세스마다 무작위로 만든 키와 HMAC-SHA-256을 사용하고 앞 12바이트만 `path-...` 식별자로 저장한다. 따라서 같은 프로세스 안에서는 같은 경로의 이벤트를 연관 지을 수 있지만, 앱을 다시 시작한 뒤에도 같은 식별자가 유지된다고 가정하면 안 된다.

예외는 타입 이름만 기록한다. 예외 메시지와 stack trace, 이미지 픽셀, 클립보드 내용은 구조화 로그에 저장하지 않는다. 다만 앱 버전, 운영체제, 포맷과 처리 시간도 진단 정보이므로 필요한 로그만 선별해 공유한다.

현재 제품 기본 로그 정책은 다음과 같다.

- 최소 수준: `Information`
- 파일당 최대 크기: 1 MiB
- 최대 파일 수: 5개
- 보존 기간: 7일
- 정리 시점: 새 로그를 성공적으로 쓴 뒤

보존 기간과 파일 수 조건을 함께 적용해 오래된 파일부터 정리한다. 파일이 다른 프로세스에 잠겨 있거나 접근 권한이 없으면 정리가 실패할 수 있으므로, 위 수치는 정상적인 파일 접근이 가능한 경우의 보존 목표다.

로그와 복구 파일을 서버로 자동 업로드하는 telemetry·원격 수집 파이프라인은 구현되어
있지 않다. 앱은 자동 업데이트 확인을 포함한 자체 네트워크 요청을 하지 않으며 설치와
업데이트는 Microsoft Store가 관리한다.

## 3. 복구, 격리와 안전 모드

### 3.1 복구 체크포인트

복구는 수정된 문서가 있는 창에만 동작한다. 편집이 바뀐 뒤 2초 동안 추가 변경이 없으면 최신 상태를 저장하며, 그 사이 더 최신 변경이 오면 앞선 예약은 대체된다. 현재 체크포인트 payload는 원본 소스와 주석을 포함하는 프로젝트 데이터이며, 창별 파일 하나를 원자적으로 갱신한다.

복구 파일의 현재 한도는 metadata 64 KiB, payload 512 MiB이다. 저장 형식의 SHA-256 값은 손상·변조 감지용 무결성 검사이지 암호화가 아니다.

비정상 종료 후 다음 일반 실행에서는 후보의 헤더만 먼저 읽어 시각과 크기를 표시한다. 사용자가 `모두 복구`를 선택한 후보만 전체 payload를 읽고 SHA-256을 검증한다. 대화상자의 선택은 다음과 같다.

- `모두 복구`: 유효한 후보를 창에 복원한다. 원래 후보는 복원이 성공한 뒤에만 정리한다.
- `모두 버리기`: 표시된 이전 세션 후보를 의도적으로 폐기한다.
- `나중에`: 후보를 그대로 두고 이번 실행에서는 처리하지 않는다.

문서가 더 이상 수정 상태가 아니거나 창을 정상적으로 닫으면 해당 창 체크포인트를 정리한다. 마지막 창의 정상 종료에서는 예약된 작업을 먼저 drain한 뒤 현재 세션의 복구 파일과 충돌 표식을 정리한다. 정상 종료 후에도 같은 세션의 파일이 남아 있다면 파일 잠금·권한 등으로 정리가 끝나지 않았을 가능성이 있으므로 먼저 백업하고 로그를 확인한다.

### 3.2 격리

형식, 파일 이름의 세션·창 ID, 크기 또는 SHA-256 검증에 실패한 항목은 `recovery-quarantine\`으로 이동하고 이름 끝에 `.corrupt`를 붙인다. 일시적으로 파일을 읽을 수 없거나 전체 후보를 분류하지 못한 경우에는 정상 후보라고 단정하지 않고 파괴적인 정리를 보류한다.

격리 저장소의 기본 보존 목표는 다음과 같다.

- 최대 30일
- 최대 20개
- 전체 최대 512 MiB
- 새 손상 항목을 격리한 뒤 정리

잠금이나 권한 문제로 정리가 실패하면 한도를 넘는 파일이 남을 수 있다. `.corrupt` 파일은 일반 사용자 복원 대상으로 다시 열지 않으며, 원본 내용이 있을 수 있으므로 진단이 끝날 때까지 보호된 백업에 보존한다.

### 3.3 안전 모드

동일한 시작 실패 fingerprint가 7일 안에 2회 연속 기록되면 다음 시작에서 안전 모드를 제안한다. fingerprint는 예외 타입과 메서드 식별 정보로 만든 SHA-256 값이며 예외 메시지나 원문 경로를 포함하지 않는다. 다른 fingerprint가 발생하면 연속 횟수는 다시 1부터 시작하고, 첫 화면 렌더링 또는 문서 열기 처리가 정상적으로 안정화되면 실패 상태를 정리한다.

사용자가 시작 대화상자에서 안전 모드를 선택하면 저장된 설정 자체를 바꾸지 않고 해당 실행에만 다음 제한을 적용한다.

- 클립보드 감시와 캡처 통합을 시작하지 않는다.
- 최근 파일 기록을 일시 중지한다.
- 하위 폴더 탐색을 사용하지 않는다.
- 복구 세션과 이전 후보 복원을 시작하지 않는다.

안전 모드에서 이전 복구 파일을 수동으로 수정하지 않는다. 앱이 안정적으로 열리는지 확인한 뒤 정상 종료하고, 다음 일반 실행에서 기본 복구 대화상자로 후보를 처리한다.

## 4. 장애별 읽기 전용 조사 순서

가능하면 앱의 모든 창을 정상적으로 닫고 다음 명령으로 프로세스가 남아 있지 않은지 확인한다. 출력이 없으면 실행 중인 해당 프로세스가 없다는 뜻이다.

```powershell
Get-Process -Name 'ezyImageViewer' -ErrorAction SilentlyContinue
```

그 다음 공통 현황을 수집한다.

```powershell
$dataRoot = Join-Path $env:LOCALAPPDATA 'ezyImageViewer'
Get-ChildItem -LiteralPath $dataRoot -Force |
    Sort-Object Name |
    Select-Object Name, Length, LastWriteTimeUtc, Attributes

$logRoot = Join-Path $dataRoot 'logs'
$latestLog = Get-ChildItem -LiteralPath $logRoot -Filter 'ezy-*.jsonl' -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

if ($latestLog) {
    Get-Content -LiteralPath $latestLog.FullName |
        ForEach-Object { $_ | ConvertFrom-Json } |
        Select-Object timestampUtc, level, eventName, errorCode, exceptionType
}
```

### 앱이 시작되지 않거나 안전 모드가 반복 제안될 때

1. `startup-health.json`의 fingerprint, 연속 횟수와 UTC 시각을 확인한다.
2. 최신 로그에서 `StartupFailureRecorded`, `SafeModeEnabled`, `AppStarted`, `AppStopped` 순서를 확인한다.
3. `crash-markers\`의 파일 시각과 로그의 마지막 정상 종료 시각을 비교한다.
4. 안전 모드는 시작 대화상자에서만 선택해 재현하고, 내부 진단 실행 인수를 장애 회피 수단으로 사용하지 않는다.

```powershell
$healthPath = Join-Path $dataRoot 'startup-health.json'
if (Test-Path -LiteralPath $healthPath) {
    Get-Content -LiteralPath $healthPath -Raw
}

Get-ChildItem -LiteralPath (Join-Path $dataRoot 'crash-markers') -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object Name, Length, LastWriteTimeUtc
```

### 복구 대화상자가 반복되거나 복원이 실패할 때

1. `recovery\`, `recovery-quarantine\`, `crash-markers\`의 파일 이름·크기·UTC 시각만 먼저 비교한다.
2. 로그에서 `RecoverySaved`, `RecoveryRestored`, `RecoveryOperationFailed`, `RecoveryCleanupFailed`를 시간순으로 확인한다.
3. `.recovery`와 `.corrupt`는 바이너리 프로젝트 내용이므로 텍스트 편집기에서 열거나 수정하지 않는다.
4. 앱이 후보를 완전히 분류하지 못한 경우 정리가 보류되는 것이 정상적인 보존 동작이다. 원본 폴더를 백업한 뒤 재현 시각과 로그를 함께 보존한다.

```powershell
'recovery', 'recovery-quarantine', 'crash-markers' | ForEach-Object {
    Get-ChildItem -LiteralPath (Join-Path $dataRoot $_) -File -ErrorAction SilentlyContinue
} | Sort-Object LastWriteTimeUtc -Descending |
    Select-Object DirectoryName, Name, Length, LastWriteTimeUtc
```

### 설정이나 최근 파일 목록이 예상과 다를 때

1. `settings.json`과 `recent-files.json`의 존재 여부, 크기와 수정 시각을 확인한다.
2. JSON을 로컬에서만 파싱해 schema version을 확인한다. 최근 파일 내용에는 전체 경로가 있으므로 원문을 지원 채널에 그대로 붙이지 않는다.
3. 최근 파일 기록을 껐거나 파일이 더 이상 존재하지 않으면 목록 파일이 없거나 항목이 줄어드는 것은 정상이다.
4. 손상되거나 지원하지 않는 설정은 개인정보 보호형 기본값으로 돌아갈 수 있으므로, 파일을 직접 고치기 전에 백업과 로그 시각을 확보한다.

```powershell
$settingsPath = Join-Path $dataRoot 'settings.json'
$recentPath = Join-Path $dataRoot 'recent-files.json'
Get-Item -LiteralPath $settingsPath, $recentPath -Force -ErrorAction SilentlyContinue |
    Select-Object Name, Length, LastWriteTimeUtc

if (Test-Path -LiteralPath $settingsPath) {
    Get-Content -LiteralPath $settingsPath -Raw |
        ConvertFrom-Json |
        Select-Object schemaVersion
}

if (Test-Path -LiteralPath $recentPath) {
    $recentState = Get-Content -LiteralPath $recentPath -Raw | ConvertFrom-Json
    [pscustomobject]@{
        SchemaVersion = $recentState.schemaVersion
        EntryCount = @($recentState.entries).Count
    }
}
```

### 로그가 없거나 갱신되지 않을 때

1. `logs\`의 존재 여부, 파일 크기와 마지막 수정 시각을 확인한다.
2. 데이터 루트와 로그 폴더의 ACL을 읽어 현재 계정에 접근 거부가 있는지 확인한다.
3. 파일당 1 MiB, 최대 5개, 7일 정책과 마지막 로그 쓰기 시각을 비교한다.
4. 구조화 로거는 I/O 또는 접근 권한 실패 시 앱 동작을 막지 않고 해당 쓰기를 포기하므로, 로그가 없다는 사실만으로 정상 동작을 단정하지 않는다.

```powershell
Get-ChildItem -LiteralPath $logRoot -Filter 'ezy-*.jsonl' -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object Name, Length, LastWriteTimeUtc

Get-Acl -LiteralPath $dataRoot
if (Test-Path -LiteralPath $logRoot) {
    Get-Acl -LiteralPath $logRoot
}
```

## 5. 백업과 복구 절차

### 5.1 정상 종료 후 백업

1. 앱의 모든 창을 UI로 정상 종료한다. 마지막 창이 닫힐 때 앱은 캡처를 중지하고, 복구와 최근 파일 작업 및 로그를 drain한다.
2. `Get-Process` 확인 결과가 없을 때까지 기다린다. 실행 중 복사하면 서로 다른 시점의 파일이 섞일 수 있다.
3. 파일 탐색기에서 `%LOCALAPPDATA%\ezyImageViewer` 폴더 전체를 날짜·시각이 포함된 별도 폴더로 복사한다. 원본 백업은 덮어쓰지 않는다.
4. 백업 위치는 사용자 계정만 접근할 수 있거나 암호화된 저장소를 사용한다. 특히 `recent-files.json`, `recovery\`, `recovery-quarantine\`은 민감 데이터로 취급한다.
5. 파일 수·크기·수정 시각을 원본과 백업에서 비교한다. 필요한 경우 다음 읽기 전용 명령으로 각 파일의 SHA-256을 기록한다.

```powershell
$dataRoot = Join-Path $env:LOCALAPPDATA 'ezyImageViewer'
Get-ChildItem -LiteralPath $dataRoot -File -Recurse |
    Sort-Object FullName |
    Get-FileHash -Algorithm SHA256 |
    Select-Object Path, Hash
```

### 5.2 설정 상태를 백업에서 되돌릴 때

1. 앱을 정상 종료하고 프로세스가 없음을 확인한다.
2. 현재 데이터 루트도 새 폴더에 한 번 더 복사해 되돌릴 수 있는 지점을 만든다.
3. 파일 탐색기로 신뢰할 수 있는 백업의 `settings.json`만 복사한다. 최근 파일 목록도 필요할 때만 `recent-files.json`을 함께 복사한다.
4. 설정 복원만 필요하다면 `recovery\`, `recovery-quarantine\`, `crash-markers\`, `startup-health.json`을 오래된 백업에서 함께 되돌리지 않는다. 과거 충돌 상태나 복구 대화상자를 다시 만들 수 있다.
5. 앱을 한 번 시작해 설정을 확인하고 정상 종료한다. 문제가 재현되면 복원 전 백업과 최신 로그를 유지한다.

### 5.3 비정상 종료 후 작업을 복구할 때

1. 앱이 닫힌 상태에서 데이터 루트 전체를 먼저 별도 폴더에 복사한다. 복구 시도 전에 원본 증거를 보존한다.
2. 앱을 일반 방식으로 한 번 시작한다. 안전 모드가 제안되면 먼저 안전 모드에서 기본 화면이 열리는지 확인하고 정상 종료한 뒤, 다음 일반 실행에서 복구 대화상자를 사용한다.
3. 복구 대화상자에서 후보 시각과 크기를 확인하고 `모두 복구` 또는 `나중에`를 선택한다. 보존이 필요하면 `모두 버리기`를 선택하지 않는다.
4. 복원된 문서를 즉시 새 `.ezyimg` 경로에 저장하고, 다시 열어 이미지와 주석을 확인한다. 복구 체크포인트를 장기 저장본으로 사용하지 않는다.
5. 검증을 마친 뒤 앱을 정상 종료한다. 정상 종료가 끝나면 현재 세션 체크포인트와 충돌 표식이 정리된다.
6. 복원이 실패하거나 항목이 격리되면 같은 파일을 편집하거나 반복 복사하지 않는다. 보존한 전체 백업, 재현 시각, 필요한 구조화 로그만 지원 담당자에게 전달한다.

앱은 이 자료를 자동 전송하지 않는다. 지원 공유 시 로그는 필요한 기간만 고르고, 전체 경로가 있는 최근 파일 목록과 원본 내용이 있는 복구 파일은 사용자가 필요성을 확인한 뒤 별도로 제공한다.
