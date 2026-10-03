# DeskCards 코드 품질 및 아키텍처 개선 분석 보고서

본 문서는 결함 수정 및 회귀 테스트 보강 커밋(`073b271`) 이후의 전체 코드베이스를 대상으로, **런타임 안정성, 성능 병목, 경계 조건 예외 처리, 코드 정리(Cleanup) 및 기술 부채**를 엄격하게 전수 점검하여 작성된 2차 심층 리뷰 보고서입니다.  
이미 해결된 과거 결함은 모두 제외하고, **현재 시점에서 개선이 필요한 실질적인 과제들만을 선별**하여 기술하였습니다.

---

## 1. 개선 과제 요약 (Review Matrix)

| 번호 | 영역 | 항목 | 심각도 | 파일 위치 | 영향 요약 |
|:---:|:---:|---|:---:|---|---|
| **01** | **성능/안정성** | `GroupModel.Reload`의 UI 스레드 동기 파일 열거 및 중복 `Directory.Exists` | Medium | `src/DeskCards/GroupModel.cs:19, 109` | 파일 수 증가 및 네트워크/동기화 폴더 시 UI 프레임 드롭 |
| **02** | **아키텍처** | `ShellIcons.Async` 단일 STA 스레드로 인한 전역 헤드오브라인(HoL) 블로킹 | Medium | `src/DeskCards/ShellIcons.Async.cs:14, 32` | 단일 느린 I/O가 다른 모든 그룹의 정상 아이콘 로딩까지 지연 |
| **03** | **자원 관리** | 카드 바 비활성화(`BarEnabled == false`) 시에도 40ms 무한 타이머 동작 | Low | `src/DeskCards/EdgeBar.cs:42, 194` | 기능 OFF 상태에서도 상시 초당 25회 디스패처 기상 및 자원 낭비 |
| **04** | **데이터 무결성** | `FileOps.MoveContentsAndDelete` 드라이브 간 이동(Cross-volume) 시 폴더 이동 실패 | Medium | `src/DeskCards/FileOps.cs:150-160` | 바탕화면이 다른 드라이브/OneDrive일 때 서브폴더가 있으면 그룹 삭제 실패 |
| **05** | **안정성** | `Config.Save` 실패 시 오류 묵살 및 `.bak` 파일 락 충돌 시 무음 실패 | Low | `src/DeskCards/Config.cs:187-194` | 외부 동기화/백업 락 발생 시 설정 저장이 실패해도 사용자 인지 불가 |
| **06** | **보안/네트워크** | `DardProxy`의 비웹 포트(DB/RDP/캐시 등) CONNECT 터널링 허용 구조 | Medium | `src/DeskCards/DardProxy.cs:116` | Chromium 차단 외 비표준 포트(6379, 3389 등)로의 TCP 터널링 가능 |
| **07** | **웹 런타임** | `DardScriptPolicy` 인코딩 가정 및 `<template>` 태그 내 스크립트 해시 누락 | Low | `src/DeskCards/DardScriptPolicy.cs:19-21` | 웹 컴포넌트 템플릿 내 스크립트 작성 시 런타임 CSP 차단 발생 |
| **08** | **코드 정리** | 리팩토링 잔여 고아 요약 주석(Orphan Docstrings) 방치 | Trivial | `src/DeskCards/Config.cs:202`<br>`src/DeskCards/GroupManager.cs:33, 91` | 삭제·이동된 메서드의 주석이 엉뚱한 멤버에 붙어 IDE 툴팁 왜곡 |
| **09** | **UI 동시성** | `ExpandedWindow` 설정 버튼 연속 클릭 시 중복 창 생성 위험 | Low | `src/DeskCards/ExpandedWindow.xaml.cs:64` | 창 닫힘 애니메이션 중 빠른 더블클릭 시 다중 이벤트 핸들러 등록 |
| **10** | **예외 처리** | `DardPackage.Unzip`에서 `InvalidDataException` 외 스트림 예외 누락 | Low | `src/DeskCards/DardPackage.cs:243` | 비표준 압축 시 `NotSupportedException` 등이 원시 예외로 탈출 |

---

## 2. 세부 분석 및 개선 방안

### 01. [성능/안정성] `GroupModel.Reload`의 UI 스레드 동기 파일 열거 및 중복 I/O
- **코드 위치**: `src/DeskCards/GroupModel.cs:19-22, 109-114`
- **문제점 분석**:
  1. 아이콘 추출은 `ShellIcons.Async.cs`의 백그라운드 워커로 성공적으로 분리되었으나, **디렉터리 파일 열거 자체(`Directory.EnumerateFileSystemEntries`)는 여전히 UI 메인 스레드에서 동기적으로 실행**됩니다.
  2. 또한 `ShellEntry` 생성자 내부에서 파일명/확장자 분리를 위해 다음 코드를 매 항목마다 호출합니다:
     ```csharp
     Name = Directory.Exists(path) || ext.Length == 0
         ? System.IO.Path.GetFileName(path)
         : System.IO.Path.GetFileNameWithoutExtension(path);
     ```
     `GroupModel.cs:113`에서 이미 `var attr = File.GetAttributes(p)`를 조회했음에도 불구하고, 비트 연산(`(attr & FileAttributes.Directory) != 0`)을 사용하지 않고 **디스크 시스템 콜인 `Directory.Exists(path)`를 항목마다 중복해서 동기 호출**하고 있습니다.
- **실제 영향**:
  - 그룹 폴더 안에 수백 개의 파일이 있거나, 해당 폴더가 네트워크 드라이브(UNC), 클라우드 동기화(OneDrive, DropBox) 경로일 경우 `Reload()`가 트리거될 때마다 UI 스레드가 수십~수백 밀리초간 정지하여 버벅임(Micro-stutter)이 발생합니다.
- **개선 방안**:
  - `ShellEntry` 생성 시 이미 확인한 `attr` 또는 `isDir` 불리언 값을 인자로 넘겨 `Directory.Exists` 시스템 콜을 제거하고, 파일 수가 많은 폴더의 열거 작업도 백그라운드 태스크로 넘긴 뒤 UI 컬렉션을 교체하도록 개선합니다.

---

### 02. [아키텍처/병목] `ShellIcons.Async` 단일 STA 스레드로 인한 전역 헤드오브라인(HoL) 블로킹
- **코드 위치**: `src/DeskCards/ShellIcons.Async.cs:13-21, 32-42`
- **문제점 분석**:
  1. `ShellIcons`는 셸 확장의 동시성 문제를 방지하기 위해 단 하나의 백그라운드 STA 스레드(`Worker`)와 단일 큐(`BlockingCollection<Action>`)를 사용합니다.
  2. 모든 바탕화면 카드와 그룹 폴더가 이 단일 워커 스레드를 공유합니다.
  3. 만약 어떤 사용자가 A 그룹 폴더에 오프라인 상태인 네트워크 드라이브(NAS) 바로가기나 반응이 극히 느린 외장 디스크의 파일을 넣어둔 경우, 단일 STA 스레드가 해당 파일 아이콘을 추출하느라 Windows Shell API 타임아웃(최대 10~30초) 동안 블로킹됩니다.
- **실제 영향**:
  - UI 스레드는 멈추지 않지만, **B 그룹, C 그룹 등 로컬 초고속 SSD에 있는 다른 정상적인 폴더들의 아이콘 로딩까지 모조리 대기열 뒤에 갇혀(Head-of-Line Blocking)**, 30초 동안 모든 카드가 기본 플레이스홀더 아이콘으로 멈춰 있게 됩니다.
- **개선 방안**:
  - 네트워크/원격 경로(UNC, `\\`로 시작하거나 네트워크 드라이브 문자)는 로컬 파일 큐와 분리된 전용 저순위 워커에서 처리하거나, 개별 셸 호출에 짧은 타임아웃을 강제하여 로컬 큐가 막히지 않도록 채널을 격리해야 합니다.

---

### 03. [자원 관리] 카드 바 비활성화(`BarEnabled == false`) 시에도 40ms 무한 타이머 동작
- **코드 위치**: `src/DeskCards/EdgeBar.cs:42-44, 144-158, 194`
- **문제점 분석**:
  1. `EdgeBar`의 모니터링 타이머는 40ms 간격으로 상시 회전합니다:
     ```csharp
     // EdgeBar.cs:194
     if (!mgr.BarEnabled || _suspended || mgr.Editing || !KeyCombo.IsDown(mgr.BarKeys)) return false;
     ```
  2. 사용자가 카드 설정에서 "카드 바 사용"을 껐더라도(`mgr.BarEnabled == false`), 타이머는 멈추지 않고 1초에 25번씩 지속적으로 `Tick()`을 호출하여 `Native.GetCursorPos()`와 `AtEdge()`를 연산합니다.
- **실제 영향**:
  - 사용하지도 않는 기능 때문에 불필요한 디스패처 메시지가 지속 발생하고, 모바일 노트북 환경에서 미세한 CPU C-state 방해 요인이 됩니다.
- **개선 방안**:
  - `mgr.Changed` 이벤트에서 `mgr.BarEnabled`를 확인하여 `false`일 때는 `_timer.Stop()`, 다시 켜졌을 때만 `_timer.Start()`하도록 라이프사이클을 연동해야 합니다.

---

### 04. [데이터 무결성] `FileOps.MoveContentsAndDelete` 드라이브 간 이동(Cross-volume) 시 폴더 이동 실패
- **코드 위치**: `src/DeskCards/FileOps.cs:150-160`, `src/DeskCards/GroupManager.cs:670`
- **문제점 분석**:
  1. 그룹 삭제 시 내부 항목을 바탕화면(`FileOps.UserDesktop`)으로 이동하는 `MoveContentsAndDelete` 내부 로직:
     ```csharp
     static void Move(string from, string to)
     {
         if (Directory.Exists(from)) Directory.Move(from, to);
         else File.Move(from, to);
     }
     ```
  2. Windows 환경에서 사용자 바탕화면이 D 드라이브에 있거나, OneDrive 폴더 리디렉션(`C:\Users\...\OneDrive\Desktop`)을 사용하고 있을 때, 그룹 폴더(`%USERPROFILE%\DeskCards\그룹`) 내에 서브폴더가 들어있는 경우:
  3. `File.Move`는 드라이브 간 복사-삭제가 자동 지원되지만, **.NET의 `Directory.Move`는 드라이브 간(Cross-volume) 이동 시 `IOException: Source and destination path must have identical roots` 예외를 던지며 실패**합니다.
- **실제 영향**:
  - 그룹 안에 일반 파일만 있으면 정상 이동되지만, 서브폴더가 단 하나라도 들어 있으면 그룹 삭제가 실패하고 롤백이 발생하여 그룹을 삭제할 수 없게 됩니다.
- **개선 방안**:
  - `Path.GetPathRoot(from)`과 `Path.GetPathRoot(to)`가 다를 경우, 재귀 디렉터리 복사 후 원본 삭제를 수행하는 안전 폴백(Cross-volume directory move fallback)을 적용해야 합니다.

---

### 05. [안정성] `Config.Save` 실패 시 오류 묵살 및 `.bak` 파일 락 충돌 시 무음 실패
- **코드 위치**: `src/DeskCards/Config.cs:187-194`
- **문제점 분석**:
  1. `Config.Save()`는 원자적 대체를 위해 `File.Replace(temporary, _filePath, _filePath + ".bak")`를 사용합니다.
  2. 백신 실시간 감시, 클라우드 동기화(OneDrive), 인덱싱 서비스가 순간적으로 `config.json.bak`를 읽기 전용으로 열고 있으면 `File.Replace`는 `IOException`을 던집니다.
  3. `Config.Save()`는 모든 예외를 잡아서 단순히 `return false;`만 반환하고 임시 파일을 지웁니다.
  4. 더 심각한 점은, `GroupManager.cs`, `DeskCard.cs` 등 **앱 전체에서 `_cfg.Save()`를 호출하는 거의 모든 곳에서 반환값 `bool`을 전혀 검사하지 않고 버린다**는 것입니다(`_cfg.Save();`).
- **실제 영향**:
  - 사용자가 카드 위치를 옮기거나 크기를 조정했는데 외부 프로그램에 의해 저장이 실패해도 사용자나 관리자에게 아무런 피드백이 없어, 다음 실행 때 위치가 이전 상태로 되돌아가는 현상이 발생합니다.
- **개선 방안**:
  - `File.Replace` 실패 시 재시도 로직을 도입하거나 백업 파일 락 우회 전략을 마련하고, 저장 실패 시 최소한 로그 또는 UI 알림을 남기도록 보완해야 합니다.

---

### 06. [보안/네트워크] `DardProxy`의 비웹 포트(DB/RDP/캐시 등) CONNECT 터널링 허용 구조
- **코드 위치**: `src/DeskCards/DardProxy.cs:116-119`
- **문제점 분석**:
  1. 유휴 릴레이 누수가 수정되었고 SSH(22)·SMTP(25)는 Chromium 자체에서 차단되지만, Chromium의 차단 포트 목록에 없는 일반 서비스 포트들이 여전히 다수 존재합니다:
     - Redis: `6379`, MongoDB: `27017`, MySQL: `3306`, PostgreSQL: `5432`, RDP: `3389` 등
  2. `DardProxy.TrySplit`은 여전히 `1 <= port <= 65535`만 검사하므로, 공인 IP 상의 데이터베이스나 원격 제어 포트에 대한 CONNECT 요청을 그대로 중계합니다.
- **실제 영향**:
  - 카드가 웹 통신(HTTP/HTTPS)을 넘어 외부 데이터베이스나 인프라 관리 포트에 무차별 대입(Brute-force) 공격이나 비인가 프로토콜 패킷을 전송하는 프록시 터널로 악용될 여지가 남아 있습니다.
- **개선 방안**:
  - 웹 카드 플랫폼이라는 목적에 맞추어 `port is 80 or 443 or 8080 or 8443` 또는 허용 포트 화이트리스트 정책을 명시적으로 적용하는 심층 방어(Defense in Depth)가 바람직합니다.

---

### 07. [웹 런타임] `DardScriptPolicy` 인코딩 가정 및 `<template>` 태그 내 스크립트 해시 누락
- **코드 위치**: `src/DeskCards/DardScriptPolicy.cs:19-22`
- **문제점 분석**:
  1. `Encoding.UTF8.GetString(bytes)`로 디코딩을 고정하고 있으나, 카드가 UTF-16이나 BOM이 포함된 인코딩일 경우 해시 불일치가 발생할 수 있습니다.
  2. `doc.QuerySelectorAll("script")`는 W3C DOM 표준상 `<template>` 태그 내부의 DocumentFragment 안쪽에 정의된 `<script>` 요소를 탐색하지 않습니다.
- **실제 영향**:
  - 현대 웹 컴포넌트(Web Components)나 템플릿 기법을 사용하여 `<template><script>...</script></template>`를 작성하고 나중에 DOM에 붙이는 카드가 있다면, 해당 스크립트의 해시가 CSP 헤더에서 누락되어 런타임에 실행이 차단됩니다.
- **개선 방안**:
  - `template` 태그의 `content` 내부까지 재귀적으로 스크립트 태그를 수집하도록 보완합니다.

---

### 08. [코드 정리] 리팩토링 잔여 고아 요약 주석(Orphan Docstrings) 방치
- **코드 위치**:
  - `src/DeskCards/Config.cs:202-204`
  - `src/DeskCards/GroupManager.cs:33-35, 91-93`
- **문제점 분석**:
  - 과거 코드 수정 및 파일 분할 과정에서 메서드가 삭제되거나 이동하면서 주석만 엉뚱한 위치에 남겨진 곳들이 있습니다:
    1. `Config.cs:202`: `/// <summary>.dard 한 종류...의 승인.</summary>` 주석이 `DardApproval`이 아닌 `BarItem` 클래스 바로 위에 붙어 있음.
    2. `GroupManager.cs:33-34`: `AllCards` 프로퍼티 바로 위에 완전히 다른 내용의 summary 주석 2개가 겹쳐서 적혀 있음.
    3. `GroupManager.cs:91`: 삭제된 `RefreshPlacement` 관련 설명 주석이 엉뚱하게 `TipHidden(string id)` 메서드 위에 방치되어 있음.
- **개선 방안**:
  - 고아 주석들을 삭제하거나 올바른 대상 위치로 재배치하여 코드 가독성과 IDE IntelliSense 정확도를 확보합니다.

---

### 09. [UI 동시성] `ExpandedWindow` 설정 버튼 연속 클릭 시 중복 창 생성 위험
- **코드 위치**: `src/DeskCards/ExpandedWindow.xaml.cs:64-68`
- **문제점 분석**:
  ```csharp
  GearButton.Click += (_, _) =>
  {
      Closed += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => SettingsWindow.Open(_mgr, _card));
      SafeClose();
  };
  ```
  `SafeClose()`로 창이 닫히는 비동기 과정 동안 사용자가 설정(톱니바퀴) 버튼을 빠르게 여러 번 클릭하면, `Closed` 이벤트에 핸들러가 누적 등록되어 창이 완전히 닫힌 후 `SettingsWindow.Open`이 여러 번 호출됩니다.
- **개선 방안**:
  - 버튼 클릭 즉시 `GearButton.IsEnabled = false`로 비활성화하거나 플래그를 두어 1회만 트리거되도록 가드합니다.

---

### 10. [예외 처리] `DardPackage.Unzip`에서 `InvalidDataException` 외 스트림 예외 누락
- **코드 위치**: `src/DeskCards/DardPackage.cs:243-247`
- **문제점 분석**:
  - `Unzip()`은 `InvalidDataException`만 잡아서 `DardException("zip 파일이 아니거나 손상됐어요.")`로 변환합니다.
  - 지원되지 않는 Zip 압축 방식이나 손상된 스트림 읽기 시 발생하는 `NotSupportedException`, `IOException` 등은 잡히지 않고 외부로 원시 예외가 탈출합니다.
- **개선 방안**:
  - `catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)` 형태로 묶어 사용자 친화적인 `DardException`으로 일관되게 감싸주어야 합니다.

---

## 3. 권장 조치 로드맵 (Action Items)

1. **단기 안정화 (Quick Wins)**:
   - `GroupManager.cs` / `Config.cs`의 고아 주석 3곳 정리
   - `EdgeBar.cs`의 `BarEnabled == false` 시 타이머 정지 처리
   - `ExpandedWindow.xaml.cs` 설정 버튼 중복 클릭 방지
2. **중기 아키텍처 개선 (Core Improvements)**:
   - `GroupModel.cs`의 `Directory.Exists` 중복 호출 제거 및 파일 열거 백그라운드화
   - `FileOps.MoveContentsAndDelete`에 볼륨 간 디렉터리 이동 폴백 추가
   - `DardProxy.cs`에 웹 표준 포트 화이트리스트 필터링 추가
3. **장기 보완 (Defensive Hardening)**:
   - `ShellIcons.Async.cs`의 네트워크 경로 전용 격리 큐 도입 (Head-of-Line 방지)
   - `DardScriptPolicy.cs`의 `<template>` 태그 재귀 탐색 지원
