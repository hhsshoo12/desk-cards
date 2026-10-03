# DeskCards 수정 목록 (GPT 전달용)

Gemini 2차·3차 리뷰를 Claude가 코드와 대조해 검증한 결과입니다. 함수와 파일 크기 정리 항목을 함께 담았습니다.
**"고칠 것"만 수정하세요.** "고치지 말 것"은 검증에서 틀렸거나 설계상 의도된 것이라 남긴 기록입니다.

공통 조건:
- 수정마다 회귀 테스트를 추가하세요. 실기가 필요해서 자동화가 불가능하면 그 이유를 커밋 메시지에 적으세요.
- 동작 수정과 구조 정리(C 항목)는 커밋을 나누세요.
- 기존 테스트를 모두 통과해야 합니다.

---

## A. 고칠 것 — 결함

| # | 심각도 | 위치 | 내용 |
|:-:|:-:|---|---|
| A1 | Medium | `DardView.cs:140-143` | WebView2 브라우저 프로세스가 죽으면 복구하지 않음 (3차 "확인 필요" 1) |
| A2 | Medium | `GroupManager.cs:367` | 루트 감시기에 오류가 나도 다시 만들지 않음 (3차 02) |
| A3 | Low~Med | `GroupManager.cs:614-624` | 대소문자만 바꾸는 이름 변경이 두 번 실패하면 `.rename-…` 폴더가 카드로 뜸 (3차 05) |
| A4 | Low | `DardStorage.Keeper.cs:195-200` | 큰 항목을 복사하다 실패하면 임시 파일 스트림이 해제되지 않음 (3차 04, 축소) |
| A5 | Low | `App.xaml.cs:73`, `SettingsWindow.xaml.cs:122` | 종료 중에 설정 창이 열릴 수 있음 (3차 06) |
| A6 | Low | `EdgeBar.cs:42` | 카드 바를 꺼도 40ms 폴링 타이머가 계속 돎 (2차 03) |
| A7 | Low | `Config.cs:172` | `Save()`가 실패를 버림 (2차 05) |
| A8 | Low | `DardScriptPolicy.cs:20` | `<template>` 안의 `<script>`가 해시에서 빠짐 (2차 07) |
| A9 | Low | `DardPackage.cs:243` | `Unzip`이 `InvalidDataException`만 잡음 (2차 10) |
| A10 | Trivial | `GroupModel.cs:19` | `ShellEntry`가 `Directory.Exists`를 중복 호출 (2차 01, 축소) |
| A11 | Trivial | `Config.cs:202`, `GroupManager.cs:33, 85, 91` | 엉뚱한 곳에 붙은 주석과 들여쓰기 (2차 08) |

### A1. 브라우저 프로세스가 죽었을 때
- **현재 코드**: `ProcessFailed`에서 `RenderProcessExited`일 때만 `core.Reload()`를 부릅니다.
- **문제**: `.dard` 카드는 환경(online/offline)마다 브라우저 프로세스 하나를 같이 씁니다. 이 프로세스가 죽으면(`BrowserProcessExited`) 그 환경의 카드가 **전부** 빈 화면이 되고, `Reload()`로는 살아나지 않습니다.
- **재현**: 작업 관리자에서 `msedgewebview2.exe` 브라우저 프로세스를 끝냅니다. 카드가 앱을 다시 켤 때까지 빈 채로 남습니다.
- **수정**
  - `BrowserProcessExited`면 해당 환경의 카드 창들을 다시 만듭니다(`RecreateDardWindow` 경로).
  - 환경 캐시(`DardStorage.Environment`)와 keeper 뷰도 새로 만들어야 하는지 확인하세요.
  - 같은 환경의 카드가 동시에 이벤트를 받으므로, 다시 만드는 작업은 한 번만 하세요.
  - `RenderProcessUnresponsive` 등 다른 종류도 어떻게 처리할지 정하세요.

### A2. 루트 감시기 복구
- **바로잡기**: Gemini는 "버퍼가 넘치면 감시가 영구히 멈춘다"고 했지만 틀렸습니다. .NET `FileSystemWatcher`는 버퍼가 넘쳐도 계속 감시하고, 지금의 `Bump()`(전체 다시 맞추기)가 버퍼 넘침에 맞는 처리입니다.
- **실제 문제**: 버퍼 넘침이 아닌 오류(루트 폴더 핸들 무효화 등)가 나면 감시가 멈추는데, 지금은 다시 만들지 않습니다.
- **수정**
  - `GroupModel.cs:151-156`처럼 `Error`에서 감시기를 버리고 다시 만든 뒤 `Bump()`합니다.
  - 생성 코드를 `StartRootWatch()`로 빼서 다시 씁니다.
  - `_shuttingDown`이면 무시합니다.

### A3. 대소문자 이름 변경의 임시 폴더
- **재현**: `docs` → `Docs`로 바꿉니다. 첫 이동(`→ .rename-<guid>`)은 성공하고, 둘째 이동과 되돌리기가 모두 실패합니다(이름이 바뀐 직후 백신이나 인덱서가 폴더를 잡는 경우).
- **결과**: `.rename-<guid>`는 숨김 속성이 없어 `ListGroupFolders`를 통과합니다. 원래 카드는 사라지고 `.rename-…` 카드가 뜹니다. 데이터는 잃지 않습니다.
- **수정**
  - 임시 이름에 숨김 속성을 주거나, `ListGroupFolders`에서 `.rename-` 접두사를 빼세요.
  - 시작할 때 `.rename-*` 폴더가 남아 있으면 원래 이름을 알 수 없으므로 지우지 말고, 사용자에게 알리거나 "복구된 그룹" 같은 이름으로 되살리세요. 방식은 GPT가 판단하세요.

### A4. 임시 파일 스트림
- **바로잡기**: Gemini의 "강제 종료하면 임시 파일이 영구히 남고 65,535개 한도로 고갈된다"는 과장입니다. `DeleteOnClose`는 프로세스가 끝날 때도 OS가 지웁니다.
- **실제 문제**: `s.CopyTo(copy)`가 예외를 던지면 `copy`를 해제하지 않아, GC가 정리할 때까지 큰 임시 파일이 남습니다. 또 `GetTempFileName()`은 만든 뒤 `FileStream`이 실패하면 0바이트 파일을 남깁니다.
- **수정**: 실패하면 `copy.Dispose()`를 부르세요. 파일 이름은 `Path.GetTempFileName()` 대신 `Path.Combine(Path.GetTempPath(), Guid…)`에 `FileMode.CreateNew`로 만드세요.

### A5. 종료 중 설정 창
- **재현**: 종료가 시작된 직후 두 번째 인스턴스를 실행하거나 트레이를 더블클릭하면, 큐에 들어간 `SettingsWindow.Open`이 실행될 수 있습니다.
- **영향**: Gemini가 말한 "좀비 프로세스"는 과장입니다. 앱은 어차피 종료되고, 창이 잠깐 깜빡이는 정도입니다.
- **수정**: `SettingsWindow.Open`(그리고 `OpenWidgetCards` 같은 다른 진입점)에 `if (mgr.IsShuttingDown || Application.Current.Dispatcher.HasShutdownStarted) return;`을 넣으세요.

### A6~A11 (2차 리뷰에서 넘어온 작은 것들)
- **A6**: `BarEnabled`가 false면 타이머를 멈추고, 켜면 다시 시작합니다. 타이머가 게이지 등 다른 일도 맡고 있는지 먼저 확인하세요(`EdgeBar.cs:175, 182`).
- **A7**: `File.Replace`에서 `IOException`이 나면 짧게 몇 번(예: 50ms × 3) 다시 시도하고, 그래도 실패하면 로그를 남깁니다. UI 알림은 하지 않습니다.
- **A8**: `template.content`까지 재귀로 스크립트를 모읍니다. card.html을 내보낼 때 charset이 UTF-8로 고정인지도 확인하세요.
- **A9**: `NotSupportedException`과 `IOException`도 `DardException("zip 파일이 아니거나 손상됐어요.")`으로 감쌉니다.
- **A10**: `Reload`에서 이미 읽은 `attr`의 Directory 비트를 `ShellEntry`에 넘깁니다. 열거를 백그라운드로 옮기는 건 하지 마세요.
- **A11**
  - `Config.cs:202`: DardApproval 주석을 `DardApproval` 위로 옮깁니다.
  - `GroupManager.cs:33`: 겹친 summary 2개 중 맞는 것만 남깁니다.
  - `GroupManager.cs:91`: 엉뚱하게 붙은 RefreshPlacement 설명을 지웁니다.
  - `GroupManager.cs:85`: 들여쓰기를 고칩니다.

---

## B. 확인한 뒤 고칠 것

### B1. 배율이 다른 보조 모니터의 카드 위치 (3차 01)
- **판단**: 가능성이 높지만 실기 확인이 필요합니다. Gemini가 매긴 High는 다중 모니터 사용자에게만 해당합니다.
- **근거**
  - 저장은 "물리 픽셀 ÷ **그 모니터** 배율"입니다(`DeskCard.cs:672`, `GroupManager.cs:435`).
  - 복원은 HWND를 만들기 전에 `card.Left = pos[0]`을 대입합니다(`GroupManager.cs:506`).
  - PerMonitorV2 WPF는 HWND를 만들기 전의 Left/Top을 **주 모니터(시스템) DPI**로 물리 좌표로 바꿉니다. 그래서 150% 보조 모니터 X=2400px에 있던 카드는 1600 DIP로 저장되고, 1600px(주 모니터)에 복원될 수 있습니다.
- **확인 방법**: 100% 주 모니터와 150% 보조 모니터를 두고, 보조 모니터에 카드를 둔 채 앱을 다시 켭니다. 실기가 없으면 `Native.MonitorScaleOf`와 모니터 목록을 바꿔 끼울 수 있게 해서 단위 테스트로 계산만 검증하세요.
- **수정 방향**
  - 위치는 **물리 픽셀**(또는 모니터 + 상대 위치)로 저장합니다.
  - 복원은 `SourceInitialized` 뒤에 `SetWindowPos`로 합니다. `BarWindow`와 `CardLabel`이 이미 이 방식입니다.
  - `ExpandedWindow.PlaceNearCard`(`275-286`)와 `DardSettingsWindow.Place`(`130-136`)도 같은 방식으로 맞춥니다.
  - 기존 config의 DIP 값은 하위 호환 없이 무시해도 됩니다. 이 경우 위치가 한 번 초기화됩니다.

---

## C. 구조 정리 (동작 변경 없음)

**규칙**
- 줄 수 목표는 두지 않습니다. 책임 단위로 나눕니다.
- 파일을 나누는 커밋(옮기기만)과 함수를 쪼개는 커밋을 따로 만드세요.
- public이나 internal 시그니처는 바꾸지 않습니다.

### C1. 쪼갤 함수 (로직이라 효과가 큼)

| 줄 수 | 함수 | 쪼갤 방향 |
|:-:|---|---|
| 93 | `SetupEngine.InstallAsync` (`installer/…/SetupEngine.cs:22`) | 내려받기·검증 / 교체 / 등록 / 롤백 단계별로 |
| 89 | `GroupManager.ReconcileDards` (`GroupManager.Dards.cs:48`) | 승인 / 저장소 위치 확인·옮기기 / 중복 / 창 관리. 안의 61줄 람다(`:73`)도 메서드로 |
| 78 | `DardPackage.Parse` (`DardPackage.cs:127`) | 필드 그룹별 검사 함수(신원·카드·권한·저장소) |
| 72 | `DeskCard.WndProc` (`DeskCard.cs:465`) | 메시지별 처리 메서드로 |
| 63 | `Config.Load` (`Config.cs:108`) | 읽기 / 백업 복구 / 정리(normalize) |
| 62 | `DardStorage.MoveAsync` (`DardStorage.Transfer.cs:127`) | 내보내기 / 지우기 / 가져오기 / 마무리 단계별로. 재개(DardMoving) 흐름이 보이게 |

### C2. UI를 코드로 만드는 긴 함수 (여유 있을 때)
- `SettingsWindow.About.AppCard`(137줄)는 영역별로 나눕니다.
- 나머지는 순서대로 컨트롤을 만드는 코드라 낮은 우선순위입니다: `BalloonTip` 생성자(115), `EditBar` 생성자(88), `CardLabel.Bake`(82), `DardSettingsWindow` 생성자(78), `SettingsWindow.Controls.Row`(78), 설치기 `MainWindow.Render`(72), `BarWindow.Cards.CreateEntry`(70), `SettingsWindow.Cards.BuildWidget`(64), `CardView` 생성자(63).

### C3. 나눌 파일

| 줄 수 | 파일 | 방향 |
|:-:|---|---|
| 668 | `GroupManager.cs` | 기존 `.Dards`·`.DardIssues`처럼 partial로 나눕니다. 예: `.Settings`(설정 속성), `.Groups`(만들기·삭제·이름 바꾸기), `.Edit`(편집 모드), `.Placement`(위치) |
| 671 | `tests/DeskCards.RegressionTests/Program.cs` | 실행기만 남기고 테스트는 주제별 파일로 옮깁니다 |
| 612 | `DeskCard.cs` | WndProc·바탕화면 층 / 끌기·크기 조절 / 배치·DPI로 partial 분리 |

400~550줄인 `SettingsWindow.xaml.cs`, `BarWindow.Cards.cs`, `ExpandedWindow.xaml.cs`, `GroupManager.Dards.cs`, `Updater.cs`, `DardView.cs`는 지금 그대로 둡니다. C1을 하면 자연히 줄어듭니다.

---

## D. 고치지 말 것 (검증 결과)

| 출처 | 항목 | 이유 |
|---|---|---|
| 3차 03 | 제거기 뮤텍스 경합·8.3 경로 | **틀림**. 임시 복사본 경로는 부모가 `env.TempDir`(= `GetFullPath(GetTempPath())`)로 만들고, 자식도 같은 `GetTempPath()`로 비교합니다. 둘 다 짧은 이름을 풀지 않으므로 문자열이 같습니다. 조건이 맞으면 자식은 부모가 끝나길 기다린 뒤 뮤텍스를 잡습니다. 설치기는 net48이고 single-file이 아니라 `Assembly.Location`도 정상입니다. |
| 3차 확인 필요 3 | 시작 시각 Ticks 오차 | **틀림**. 두 프로세스가 같은 커널 값(GetProcessTimes)을 읽으므로 정확히 같습니다. |
| 3차 확인 필요 2 | 위젯이 많을 때 자원 | 추측. 브라우저 프로세스는 환경당 하나를 같이 씁니다. 실제 문제가 보이면 그때 측정합니다. |
| 3차 07 | 테스트 부재 | 별도 항목으로 두지 않습니다. A·B 항목마다 테스트를 추가하는 것으로 대신합니다. |
| 2차 02 | 아이콘 단일 STA 큐 | 그룹 폴더가 로컬이라 드뭅니다. 보류합니다. |
| 2차 04 | 다른 볼륨으로 폴더를 옮기면 실패 | 원본을 보존한 채 실패하는 의도된 설계입니다. 사용자 결정 대기 중입니다. |
| 2차 06 | 프록시 포트 화이트리스트 | 설계 결정("Chrome 수준")과 맞지 않습니다. 프록시는 CONNECT만 받으므로 TLS만 지나갑니다. |
| 2차 09 | 설정 버튼 연속 클릭 | 무해합니다. `SettingsWindow.Open`은 창을 하나만 씁니다. |
