# DeskCards 3차 코드 품질 및 안정성 분석 보고서

본 문서는 결함 수정 및 회귀 테스트 보강 이후의 코드베이스(`master`)를 대상으로, **런타임 경계 조건, 멀티 모니터 및 DPI 처리, 저장소 이전 동시성/장애 복구, 수명주기 및 설치기/제거기 동작, 테스트 결손 경로**를 전수 점검하여 새로 작성한 3차 심층 코드 리뷰 보고서입니다.  
과거 2차 보고서의 항목(01~10) 및 기획상 의도된 설계 사항(DardProxy 포트 무제한, 로컬 전용 그룹 폴더, 볼륨 간 실패 의도 등)은 모두 배제하고, **구체적 재현 시나리오와 명확한 방어 부재가 입증된 결함만을 엄선**하여 수록하였습니다.

---

## 1. 개선 과제 요약 (Review Matrix)

| 번호 | 영역 | 항목 | 심각도 | 파일:줄 | 영향 요약 |
|:---:|:---:|---|:---:|---|---|
| **01** | **다중 모니터 · DPI** | 보조 모니터 이종 DPI 배율 간 창 좌표계(DIP) 계산 오류로 인한 카드·팝업 위치 왜곡 및 재시작 시 모니터 튐 | **High** | `DeskCard.cs:671-673`<br>`GroupManager.cs:434-436`<br>`ExpandedWindow.xaml.cs:275-286`<br>`DardSettingsWindow.cs:130-136` | 주 모니터와 배율이 다른 보조 모니터에 카드를 둘 때 재시작/배율 갱신 후 주 모니터로 강제 이동하며, 펼친 창·설정 창이 엉뚱한 모니터에 뜸 |
| **02** | **FileSystemWatcher** | `GroupManager._rootWatcher`의 `Error` 이벤트 수신 시 복구(재등록) 누락으로 인한 폴더/카드 감시 영구 마비 | **High** | `GroupManager.cs:367` | 대량 파일 조작 등으로 버퍼 넘침(`InternalBufferOverflowException`) 발생 시 감시기가 죽어 이후 새 폴더 생성, 삭제, .dard 추가가 감지되지 않음 |
| **03** | **설치기 / 제거기** | `uninstall.exe` 단일 인스턴스 뮤텍스 경합 및 임시 경로 비교 결함으로 인한 제거기 무응답 즉시 종료 | **High** | `installer/DeskCards.Setup/SetupApplication.cs:29-48` | 설치 폴더나 '프로그램 추가/제거'에서 제거기 실행 시 2차 임시 프로세스가 뮤텍스를 취득하지 못해 창을 띄우지 못하고 조기 종료됨 |
| **04** | **저장소 이전 (.dard)** | 64MB 초과 대용량 이전 스트림 처리 중 `Path.GetTempFileName()` 누수 및 65,535개 한도 예외 위험 | **Medium** | `DardStorage.Keeper.cs:195-200` | 대용량 IndexedDB/OPFS 데이터 이전 도중 실패나 중단 발생 시 임시 파일이 %TEMP%에 남아 Win32 임시 파일 고갈 오류 유발 가능 |
| **05** | **GroupManager 상태** | 대소문자 전용 그룹 이름 변경 실패 시 롤백 실패로 인한 유령 그룹(`.rename-...`) 카드 영구 생성 | **Medium** | `GroupManager.cs:615-624` | 그룹명 대소문자 변경 도중 파일 락 등으로 롤백마저 실패하면 숨김 속성 없는 임시 폴더가 남아 바탕화면에 알 수 없는 카드로 영구 노출됨 |
| **06** | **종료 및 수명주기** | 앱 종료(`IsShuttingDown`) 상태 중 디스패처 큐에 대기 중이던 설정 창(`SettingsWindow.Open`) 실행 및 잔존 | **Low** | `App.xaml.cs:73`<br>`SettingsWindow.xaml.cs:122-129` | 종료 도중 2번째 인스턴스 신호나 단축키가 겹칠 때 `IsShuttingDown` 상태임에도 불구하고 설정 창이 새로 생성되어 종료가 지연되거나 창이 남음 |
| **07** | **테스트 결손 경로** | 이종 DPI 다중 모니터 좌표 복원, FileSystemWatcher 오류 복구, 제거기 뮤텍스 인계 회귀 테스트 부재 | **Medium** | `tests/DeskCards.RegressionTests/` 전반 | 실제 멀티 디스플레이 환경 및 파일 시스템 오류 상황을 검증하는 테스트 코드가 전무하여 핵심 결함이 자동화 파이프라인에서 잡히지 않음 |

---

## 2. 세부 분석 및 개선 방안

### 01. [다중 모니터 · DPI] 보조 모니터 이종 DPI 배율 간 창 좌표계(DIP) 계산 오류
- **코드 위치**:
  - `src/DeskCards/DeskCard.cs:671-673` (`ActualPosition`)
  - `src/DeskCards/GroupManager.cs:434-436` (`RecreateCard`)
  - `src/DeskCards/ExpandedWindow.xaml.cs:275-286` (`PlaceNearCard`)
  - `src/DeskCards/DardSettingsWindow.cs:130-136` (`Place`)
- **재현 시나리오**:
  1. 주 모니터 1 (1920×1080, 배율 100%, DpiScale = 1.0, X: 0 ~ 1920)과 보조 모니터 2 (1920×1080, 배율 150%, DpiScale = 1.5, X: 1920 ~ 3840)가 연결된 환경을 구성합니다.
  2. 사용자가 카드를 보조 모니터 2의 물리 좌표 X = 2400에 배치합니다.
  3. 카드를 드래그하여 놓으면 `DeskCard.ActualPosition`이 호출됩니다:
     `r.Left / dpi.DpiScaleX` = 2400 / 1.5 = 1600 DIP가 계산되어 `_cfg.Positions[card.Key]`에 저장됩니다.
  4. 앱을 재시작하거나, 배율 재동기화 타이머가 동작하여 `GroupManager.PlaceAndShow` 또는 `RecreateCard`가 실행됩니다.
  5. `card.Left = pos[0]` (1600)이 실행됩니다.
  6. WPF의 최상위 창 좌표계(`Window.Left`, `Window.Top`)는 **주 모니터의 DPI(가상 화면 DIP)**를 기준으로 물리 좌표를 계산합니다 (`X = card.Left * 1.0 = 1600`).
  7. **결과**: 물리 좌표 1600은 주 모니터 1의 화면 안(0 ~ 1920)이므로, 보조 모니터에 있던 카드가 앱 재시작 즉시 주 모니터 1로 순간 이동합니다.
  8. 추가로, 보조 모니터에 카드가 정상 위치해 있더라도 해당 카드를 클릭해 `ExpandedWindow`를 열면 `PlaceNearCard`에서 `wa.Left / dpi.DpiScaleX` (1920 / 1.5 = 1280) 및 `Left = 1600`을 대입하여, **펼친 창이 보조 모니터가 아닌 주 모니터 1의 1600px 위치에 팝업**되는 치명적인 UX 왜곡이 발생합니다 (`DardSettingsWindow.Place`도 동일).
- **왜 지금 방어가 부족한지**:
  - `BarWindow.cs`나 `CardLabel.cs`에서는 Win32 `Native.SetWindowPos`를 물리 픽셀로 직접 호출하여 모니터 간 왜곡이 없으나, `DeskCard`, `ExpandedWindow`, `DardSettingsWindow`에서는 물리 좌표를 '해당 모니터의 로컬 배율'로 나눈 값을 WPF `Window.Left/Top`에 그대로 할당하고 있습니다.
  - WPF의 멀티 모니터 가상 좌표계 규칙(Window.Left/Top은 주 모니터 기준 가상 단위이거나 모니터 간 원점 오프셋이 비례 변환되지 않음)을 간과하여, 서로 다른 배율의 모니터 간 경계를 넘을 때 좌표 공간 왜곡이 발생합니다.
- **개선 방안**:
  - `ExpandedWindow` 및 `DardSettingsWindow`의 팝업 배치는 `BarWindow` 및 `CardLabel`처럼 Win32 `SetWindowPos(hwnd, ..., physicalX, physicalY, ...)`를 사용하여 물리 좌표계로 직접 이동시킵니다.
  - 카드의 저장 좌표(`Positions`)는 로컬 모니터 배율로 나눈 값이 아닌, 주 모니터 스케일 기준 가상 좌표 또는 원시 물리 좌표(모니터 식별자 + 모니터 내 상대 오프셋)로 저장하고 복원 시 `SetWindowPos`로 복원하도록 일원화해야 합니다.

---

### 02. [FileSystemWatcher] `GroupManager._rootWatcher`의 `Error` 이벤트 수신 시 복구 누락
- **코드 위치**: `src/DeskCards/GroupManager.cs:357-369`
- **재현 시나리오**:
  1. 그룹 루트 폴더(`%USERPROFILE%\DeskCards`)에 500개 이상의 파일/폴더를 일괄 압축 해제하거나 빠른 속도로 복사합니다.
  2. Windows 파일 시스템 알림 버퍼(기본 8KB)가 가득 차면서 `FileSystemWatcher` 내부에서 `InternalBufferOverflowException`이 발생하고 `_rootWatcher.Error` 이벤트가 트리거됩니다.
  3. `_rootWatcher.Error += (_, _) => Bump();`가 실행되어 1회 디바운스 동기화(`Reconcile()`)가 실행됩니다.
  4. .NET 런타임의 `FileSystemWatcher`는 내부 오류나 버퍼 오버플로 발생 시 내부적으로 감시 핸들을 닫거나 `EnableRaisingEvents`를 `false`로 전환합니다.
  5. **결과**: `_rootWatcher`를 재생성하거나 다시 `EnableRaisingEvents = true`로 활성화하지 않으므로, 이 시점 이후 사용자가 탐색기에서 폴더를 새로 만들거나 삭제하거나 `.dard` 위젯 파일을 추가해도 **앱을 완전히 재시작하기 전까지 아무런 변화도 감지하지 못하고 감시가 영구 마비**됩니다.
- **왜 지금 방어가 부족한지**:
  - `GroupModel.cs:151-156`에서는 아래와 같이 `_watcher.Error` 발생 시 감시기를 안전하게 닫고 새로 생성하는 방어가 잘 구현되어 있습니다:
    ```csharp
    _watcher.Error += (_, _) => _debounce.Dispatcher.BeginInvoke(() => {
        if (_disposed) return;
        Watch();
        Reload();
    });
    ```
  - 그러나 메인 루트를 감시하는 `GroupManager.cs:367`에서는 단순 `Bump()`만 1회 호출할 뿐, 오류가 난 감시기를 해제(`Dispose`)하고 새로 띄우는(`StartWatch()`) 복구 루틴이 누락되어 있습니다.
- **개선 방안**:
  - `GroupManager`에 루트 감시기를 초기화 및 재시작하는 메서드를 정의하고, `_rootWatcher.Error` 발생 시 기존 인스턴스를 해제 후 새 `FileSystemWatcher`를 생성하여 감시를 즉시 복구하도록 수정합니다.

---

### 03. [설치기 / 제거기] `uninstall.exe` 단일 인스턴스 뮤텍스 경합으로 인한 조기 종료
- **코드 위치**: `installer/DeskCards.Setup/SetupApplication.cs:29-48`
- **재현 시나리오**:
  1. 사용자가 Windows 제어판의 '프로그램 추가/제거' 또는 설치 경로(`%LOCALAPPDATA%\Programs\Desk Cards\uninstall.exe`)에서 제거를 실행합니다.
  2. 1차 프로세스는 `exe.Equals(env.Uninstaller)` 조건을 감지하고 임시 경로(`%TEMP%\DeskCards-uninstall-<PID>.exe`)로 자신을 복사한 뒤, `Process.Start`로 2차 프로세스를 띄우고 자신은 `return;`으로 종료 경로를 밟습니다.
  3. 2차 프로세스가 실행되어 `Main`에 진입합니다.
  4. 2차 프로세스의 29행 조건:
     `Path.GetDirectoryName(exe)!.Equals(Path.GetTempPath().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)`
     Windows 환경에서 8.3 짧은 경로명(예: `C:\Users\USER~1\AppData\Local\Temp` vs `C:\Users\Username\AppData\Local\Temp`) 또는 마지막 슬래시 유무 차이가 발생하면 이 `if` 조건이 `false`가 됩니다.
  5. 조건이 실패하면 2차 프로세스는 1차 프로세스의 종료를 대기(`parent.WaitForExit(10000)`)하지 않고 즉시 40행으로 직행합니다.
  6. 40행에서 `new Mutex(true, "DeskCards.Setup.SingleInstance", out bool created)`를 실행하지만, 아직 1차 프로세스가 완전히 언로드되지 않아 뮤텍스를 소유하고 있으므로 `created`는 `false`가 됩니다.
  7. **결과**: 42행 `if (!created) { show.Set(); return; }`에 걸려 2차 프로세스가 화면에 아무 창도 띄우지 못한 채 즉시 무응답 종료됩니다. 1차 프로세스도 이미 종료되었으므로 사용자 입장에서는 제거가 아무런 반응 없이 증발합니다.
- **왜 지금 방어가 부족한지**:
  - 임시 디렉터리 경로 비교 시 `Path.GetFullPath` 정규화가 누락되어 8.3 변형 경로에서 부모 프로세스 대기 로직이 건너뛰어질 수 있습니다.
  - 또한 부모가 자식을 `Process.Start`한 직후 아직 뮤텍스를 쥐고 있는 상태에서 자식이 동일한 명명된 뮤텍스(`DeskCards.Setup.SingleInstance`)를 `Mutex(true, ...)`로 획득하려 시도하는 근본적 경합 구조가 존재합니다.
- **개선 방안**:
  - 경로 비교 시 `string.Equals(Path.GetFullPath(Path.GetDirectoryName(exe)!), Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\'), ...)`를 적용하여 정규화합니다.
  - 또는 제거 모드로 임시 복사본을 실행할 때는 `/parentpid <PID>` 인자를 명시적으로 전달하고, 2차 프로세스 시작 시 부모 프로세스의 핸들을 열어 종료를 확실히 기다린 후 뮤텍스를 획득하도록 보강합니다.

---

### 04. [저장소 이전 (.dard)] 64MB 초과 대용량 스트림 처리 중 `Path.GetTempFileName()` 누수
- **코드 위치**: `src/DeskCards/DardStorage.Keeper.cs:195-201`
- **재현 시나리오**:
  1. 사용자가 64MB 이상의 로컬 데이터(대용량 Blob, 미디어 캐시, OPFS 파일 등)를 저장한 .dard 위젯 카드를 사용 중인 상태에서 권한 변경이나 저장소 위치 이전이 발생합니다.
  2. `Keeper.Job.Get`에서 64MB 초과 항목을 읽기 위해 다음 코드가 실행됩니다:
     ```csharp
     var copy = entry.Length > 64L * 1024 * 1024
         ? (Stream)new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose)
         : new MemoryStream();
     ```
  3. `Path.GetTempFileName()`은 디스크(`%TEMP%`)에 즉시 0바이트 파일을 생성합니다.
  4. 만약 디스크 용량 부족, 권한 오류, 프로세스 강제 종료, 혹은 `new FileStream(...)` 생성 직후 또는 `s.CopyTo(copy)` 중단으로 핸들이 정상적으로 닫히지 못하면 `DeleteOnClose`가 동작하지 않고 0바이트 또는 부분 임시 파일이 디스크에 영구 잔존합니다.
  5. Windows의 `GetTempFileName` API는 한 폴더당 최대 65,535개의 임시 파일(`tmpXXXX.tmp`)만 생성할 수 있으며, 이 한도에 도달하면 이후 모든 `Path.GetTempFileName()` 호출이 `IOException: The file exists`를 던집니다.
- **왜 지금 방어가 부족한지**:
  - 시스템 공용 `%TEMP%`에 무작위 접두사 없이 파일을 만들고 있으며, 이전 작업이 실패하거나 취소되었을 때 해당 임시 파일들을 청소하는 명시적 롤백/클린업 추적 루틴이 없습니다.
- **개선 방안**:
  - `Path.GetTempFileName()` 대신 앱 데이터 폴더 내의 전용 임시 디렉터리(`AppPaths.WebDataDir/temp`)에 GUID 기반 임시 파일을 생성하고, `Keeper.Job`의 `Dispose` 시 생성된 임시 파일 목록을 순회 삭제하도록 개선합니다.

---

### 05. [GroupManager 상태] 대소문자 전용 그룹 이름 변경 실패 시 롤백 폴더 유령 카드 생성
- **코드 위치**: `src/DeskCards/GroupManager.cs:615-624`, `460-462`
- **재현 시나리오**:
  1. 대소문자만 다른 이름으로 그룹 이름을 변경합니다 (예: `docs` -> `Docs`).
  2. `caseOnly` 분기로 진입하여 1단계로 임시 폴더(`FileOps.Unique(Root, ".rename-" + Guid.NewGuid().ToString("N"))`)로 이동합니다.
  3. 2단계로 대상 폴더(`dest`)로 이동(`Directory.Move(tmp, dest)`)을 시도합니다.
  4. 이때 백신 실시간 검사 또는 외부 탐색기 프로세스가 해당 폴더 내 파일을 물고 있어 예외가 발생하고 catch 블록으로 진입합니다.
  5. catch 블록에서 원래 이름으로 복구하기 위해 `Directory.Move(tmp, card.Group.Folder)`를 시도합니다.
  6. 만약 이 복구 이동마저 동일한 파일 락이나 권한 문제로 예외가 발생하면(throw), 작업이 중단되며 디렉터리 루트에 `.rename-<GUID>` 폴더가 그대로 남습니다.
  7. 이후 `Reconcile()`이 실행될 때 `ListGroupFolders()`는 다음과 같이 디렉터리를 수집합니다:
     ```csharp
     Directory.EnumerateDirectories(Root)
         .Where(d => (File.GetAttributes(d) & (FileAttributes.Hidden | FileAttributes.System)) == 0)
     ```
  8. **결과**: `FileOps.Unique`로 생성된 `.rename-...` 폴더는 숨김(`Hidden`) 속성이 없으므로 정상 그룹 폴더로 판정되어, 바탕화면에 `.rename-3a4f...`라는 알 수 없는 이름의 유령 카드 창이 새로 생성되어 나타납니다.
- **왜 지금 방어가 부족한지**:
  - 임시 폴더 생성 시 숨김 속성(`FileAttributes.Hidden`)을 부여하지 않아 탐색기 및 `ListGroupFolders`의 필터를 통과하게 됩니다.
  - 롤백 실패 시 잔류 임시 폴더를 추적하여 정리하는 안전장치가 없습니다.
- **개선 방안**:
  - 대소문자 변경용 임시 폴더 생성 직후 `File.SetAttributes(tmp, FileAttributes.Hidden)`을 부여하여 장애 시에도 일반 그룹 카드로 인식되지 않도록 차단합니다.
  - 앱 시작 시(`Start()`) 루트 폴더 내 남아 있는 `.rename-*` 패턴의 고아 디렉터리를 자동 감지하여 정리하는 루틴을 추가합니다.

---

### 06. [종료 및 수명주기] 앱 종료(`IsShuttingDown`) 상태 중 설정 창 비동기 생성 및 잔존
- **코드 위치**:
  - `src/DeskCards/App.xaml.cs:70-74`
  - `src/DeskCards/SettingsWindow.xaml.cs:122-129`
- **재현 시나리오**:
  1. 앱이 트레이 메뉴 [종료] 또는 시스템 종료에 의해 `App.Shutdown()`을 시작합니다.
  2. `GroupManager.Shutdown()`이 호출되어 `_shuttingDown = true`가 설정되고 모든 카드 창이 닫힙니다.
  3. 마침 바로 그 타이밍에 사용자가 시작 메뉴에서 DeskCards를 다시 누르거나 두 번째 인스턴스를 실행하여 `_showSettings` 이벤트가 시그널링됩니다.
  4. `App.xaml.cs`의 리스너 스레드가 `signal == 1`을 감지하고 `Dispatcher.BeginInvoke(() => { if (_mgr != null) SettingsWindow.Open(_mgr); });`를 큐에 넣습니다.
  5. UI 디스패처가 종료 이벤트(`OnExit`) 직전에 이 콜백을 실행합니다.
  6. `SettingsWindow.Open` 내부:
     `_win ??= new SettingsWindow(mgr);`
     `_win.Reveal(mgr);`
  7. **결과**: 앱의 메인 관리 객체(`_mgr`)는 이미 모든 카드를 파괴하고 셧다운 상태(`IsShuttingDown == true`)인데, 새로운 `SettingsWindow` 인스턴스가 생성되고 화면에 표시됩니다. 창이 닫히지 않아 프로세스 완전 종료가 지연되거나 좀비 상태가 발생합니다.
- **왜 지금 방어가 부족한지**:
  - `SettingsWindow.Open` 및 하위 열기 메서드들(`OpenWidget`, `OpenBar` 등)에 `if (mgr.IsShuttingDown) return;` 방어 조건이 없습니다.
- **개선 방안**:
  - `SettingsWindow.Open` 진입부에 `if (mgr.IsShuttingDown) return;` 가드를 추가하여 종료 단계의 불필요한 UI 생성을 원천 차단합니다.

---

### 07. [테스트 결손 경로] 주요 경계 조건에 대한 회귀 테스트 부재
- **코드 위치**: `tests/DeskCards.RegressionTests/`
- **분석 내용**:
  현재 81개의 회귀 테스트가 작성되어 있으나, 다음의 위험 경로들은 단위/통합 테스트가 전혀 존재하지 않습니다:
  1. **다중 모니터 이종 DPI 배율 간 카드 이동 및 위치 저장 검증**:
     - 주 모니터(100%)와 보조 모니터(150%) 가상 환경에서 `ActualPosition` -> `PlaceAndShow` 복원 시 좌표가 올바른 모니터로 유지되는지 검증하는 테스트가 없습니다.
  2. **`GroupManager._rootWatcher`의 `Error` 이벤트 발생 시 복구 루프**:
     - `GroupModel`의 watcher 복구는 일부 테스트되나, `GroupManager`의 루트 watcher가 오버플로를 겪었을 때 감시가 유지되는지에 대한 검증이 없습니다.
  3. **`SetupApplication`의 단일 인스턴스 뮤텍스 핸드오버**:
     - `/uninstall` 명령으로 임시 실행 파일을 생성하고 원본 프로세스가 종료되는 인계 시나리오에 대한 테스트가 누락되어 있습니다.
- **개선 방안**:
  - `DeskCards.RegressionTests`에 가상 다중 디스플레이 좌표 검증, FileSystemWatcher 에러 주입 테스트, 설치기 인계 대기 테스트를 추가하여 회귀를 방지해야 합니다.

---

## 3. 확인 필요 (추측)

아래 항목들은 정적 코드 분석상 잠재적 위험성이 의심되나, 구체적 재현 환경(OS 버전, WebView2 런타임 버전 등)에 따라 영향도가 달라질 수 있어 추가 검증이 필요한 사항입니다.

1. **WebView2 브라우저 메인 프로세스 종료 시 복구 한계**:
   - `DardView.cs:142`에서는 `e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited`일 때만 `core.Reload()`를 시도합니다.
   - 렌더러 프로세스가 아닌 브라우저 메인 프로세스가 충돌하거나 종료(`BrowserProcessExited`)되었을 때는 단순히 `Reload()`로 복구되지 않으며, WebView2 컨트롤러 전체를 재생성해야 정상 복구됩니다.
2. **다수의 .dard 위젯 동시 구동 시 가상 메모리 및 데스크톱 힙 부하**:
   - .dard 위젯마다 별도의 WebView2 컨트롤러와 투명 창(`WS_POPUP` HwndSource)이 할당됩니다.
   - 사용자가 20~30개 이상의 위젯을 동시에 띄울 경우 Chromium 브라우저 프로세스 수 및 GDI/HWND 데스크톱 힙 자원 소비가 급증할 수 있으므로, 대량 위젯 구동 시 자원 점유율 측정이 권장됩니다.
3. **`WaitForUpdatedFrom`의 시작 시간(Ticks) 미세 오차 가능성**:
   - `App.xaml.cs:131`: `old.StartTime.ToUniversalTime().Ticks != ticks`로 프로세스를 검증합니다.
   - 고해상도 타이머 오차나 가상화 환경(Hyper-V/WSL 등)에서 프로세스 재시작 시 Ticks 값이 미세하게 어긋날 가능성이 있으므로, 시간 비교에 허용 오차(예: ±100ms)를 두는 것이 안전할 수 있습니다.
