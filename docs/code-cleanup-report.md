# Desk Cards 코드 정리 보고서

작성일: 2026-09-28. 시작점: `0e037c9` (앱 0.3.0, master).

## 작업한 로컬 커밋

| 커밋 | 정리 내용 |
| --- | --- |
| `d5bd00a` | 미사용 using 5개와 호출되지 않는 `GroupManager.Groups` 속성 제거 |
| `d867898` | 도구 창·비활성 창·클릭 통과 스타일을 `Hwnd`에 통합하고 모니터 DPI 조회 중복 제거 |
| `b581800` | 설정의 `RowBg`/`RowBorder` 박스를 `RowBox`로, ease-out 생성·진행률 계산을 `Motion`으로 통합 |
| `65e4777` | `SettingsWindow`를 Cards, General, Bar, About, Controls partial 파일로 분리 |
| `e3ec041` | `EdgeBar.cs`의 `BarWindow`, `GaugeOverlay`와 `BarPreview.cs`의 `ZoneBand`를 별도 파일로 이동 |

각 커밋에 `Co-authored-by: Codex <noreply@openai.com>`을 붙였다. 이 보고서는 별도 문서 커밋으로 기록한다.
작업 중 다른 작업에서 추가한 `.dard` 문서 커밋(`b4792bc`, `821f6c6`, `7d2ac10`)은 수정하거나 재작성하지 않았다.

## 보존·검증

- 각 단계에서 `dotnet build src/DeskCards --nologo -warnaserror` 후 `dotnet run --project tests/DeskCards.RegressionTests`를 실행했다. 빌드 경고 0개, 오류 0개, 회귀 테스트 32개 모두 통과했다.
- 설치본은 실행하지 않았다. 회귀 테스트는 기존 테스트의 고유 임시 폴더를 사용했으며 실제 `%APPDATA%\DeskCards`, `%USERPROFILE%\DeskCards`의 설정·그룹 데이터는 변경하지 않았다.
- Roslyn으로 소스와 WPF 생성 코드의 참조를 확인했다. 정리 후 직접 작성한 앱 소스에 불필요한 using 진단(`CS8019`)이 남지 않았다. XAML 생성 파일은 편집하지 않았다.
- 파일 분리 직전·직후 설정 창의 61개 멤버와 이동한 카드 바 클래스들의 코드 토큰을 비교했다. 본문이 동일하고, 설정 창 필드 초기화식의 상대 순서도 유지됨을 확인했다.
- 시작점 대비 앱의 문자열 및 보간 문자열 텍스트 토큰 402종이 동일하다. 모든 XAML, `Config.cs`, 공유 코드, 프로젝트 파일과 버전은 변경하지 않았다.
- 설정 창 본체는 999줄에서 238줄로, `EdgeBar.cs`는 569줄에서 192줄로 줄었다. 클래스 이름과 네임스페이스는 유지했다.
- 창 스타일 비트, DPI 조회 위치·실패 시 대체 값, 설정 박스별 여백, 애니메이션 시작·끝 값·차수·시간을 유지했다. WPF 애니메이션과 직접 프레임을 그리는 애니메이션의 실행 방식도 그대로다.
- 태그·릴리스·푸시는 하지 않았다. 테스트와 분석을 위한 임시 도구는 저장소에 추가하지 않았다.

검증 범위는 기존 회귀 테스트와 소스 비교다. 실제 설치본, 실제 사용자 설정, 다중 모니터의 실행 중 DPI 전환, 실제 전역 키 입력을 이용한 수동 검증은 수행하지 않았다.

## 건너뛴 항목과 이유

- `KeyComboDialog`, `BarKey` enum, 이전 탐색기 컨텍스트 메뉴·다른 그룹으로 이동 메뉴 구현: 현재 시작점에서 이미 제거되어 있다. 정상적으로 사용 중인 파일 위치 열기, 파일 이동, 바탕화면 셸 연동은 유지했다.
- 추가 P/Invoke 삭제: 현재 선언에 호출이 있거나 Win32 구조체 연동에 필요하므로 제거하지 않았다.
- `WINDOWPOS`, `APPBARDATA`, `BITMAP`, `BITMAPINFOHEADER`의 직접 읽지 않는 필드: 네이티브 메모리 배치와 크기를 구성하므로 유지했다.
- `CardWindow.IsEditing`: 앱 내부 참조만 보면 미사용이지만 회귀 테스트가 사용하는 멤버다(`tests/DeskCards.RegressionTests/Program.cs:292`). 유지했다.
- 설정 직렬화 속성, 공개 이름의 일괄 변경: 설정 형식·외부 참조 보존을 위해 유지했다. 새 도우미는 기존 PascalCase 메서드·한국어 설명 주석 스타일을 따랐다.
- DPI 좌표 선택까지 일괄 통합: 모니터 중심, 작업 영역 모서리, 커서 위치는 호출 목적이 다르다. 실제 조회 부분만 통합했다.
- 애니메이션 시계·이벤트 수명 통합: `CompositionTarget.Rendering`과 WPF 애니메이션의 종료·취소 동작까지 합치면 타이밍에 영향을 줄 수 있어 수식과 생성 코드만 통합했다.
- `GroupManager`, `Updater`, 카드 창의 추가 분할: 이번에 확인한 자연스러운 페이지·클래스 경계 이외의 분할과 광범위한 이름 변경은 피했다.

## 발견했지만 수정하지 않은 기존 오류

### 카드 바를 꺼도 조합키가 등록됐다고 표시됨 — 임시 설정으로 재현

- 위치: `src/DeskCards/SettingsWindow.Bar.cs:157` (특히 159~161줄).
- 조건: `BarEnabled = false`, `BarKeys = Ctrl + D`, `EdgeBar.HotkeyRegistered = null`인 상태에서 카드 바의 조합키 페이지를 연다.
- 실제 표시: `Windows 단축키로 등록돼 있어요. 누르는 동안 다른 앱에는 전달되지 않고, 카드 바를 끄면 등록도 풀려요.`
- 원인: 등록 결과가 `false`인지만 검사하여 미등록 상태인 `null`도 성공 문구 분기로 들어간다. 앱에서 카드 바를 끄면 등록을 풀고 상태를 `null`로 만드는 코드는 `src/DeskCards/EdgeBar.cs:69`에 있다.
- 검증: 임시 경로를 지정한 Config/GroupManager로 설정 창을 생성하고, 조합키 페이지의 실제 TextBlock을 읽었다. 출력은 `BarEnabled=False; HotkeyRegistered=null`인데 등록 성공 문구가 존재했다. 창을 화면에 표시하거나 전역 단축키를 등록하지 않았고, 실제 설정 파일도 사용하지 않았다.
- 미수정 이유: 분기와 화면 안내를 바꿔야 하므로 이번의 동작·문구 보존 조건을 벗어난다.

### 보조키만 사용하는 Win/Alt 조합의 메뉴 억제 — 코드상 누락, 실제 키 입력 재현은 보류

- 위치: `src/DeskCards/EdgeBar.cs:71`, `src/DeskCards/EdgeBar.cs:106`, `src/DeskCards/EdgeBar.cs:144`.
- 일반 키가 없는 조합은 RegisterHotKey 등록을 건너뛴다. 메뉴 억제 호출은 WM_HOTKEY 처리에 있지만, 실제 카드 바를 여는 경로에는 없다.
- 따라서 Win/Alt만 포함한 보조키 조합으로 바를 열었을 때 키를 놓으면서 시작 메뉴·앱 메뉴가 뜨는지 추가 확인이 필요하다. 설정의 설명은 바를 열 때도 억제한다고 되어 있다(`src/DeskCards/SettingsWindow.Bar.cs:168`).
- 실제 데스크톱에 전역 키 입력을 보내지 않았으므로 관찰된 오작동으로 단정하지 않는다. 이번에는 입력 동작 변경 없이 보고만 한다.
