# 바탕화면 카드 DWM 전환 보고서

- 기준: `49c0181`, Windows 11 빌드 26200, WPF / .NET 10.
- 실제 사용자 설정·그룹 폴더를 시험 대상으로 쓰지 않았다. 실행 중이던 저장소의 개발본은 `DeskCards.Quit` 이벤트로 정상 종료했다. 설치본은 실행하지 않았다.
- 버전·설정 형식·저장된 `CellSize`는 바꾸지 않았다. 저장소의 GitHub noreply 이메일을 유지하고 로컬 커밋만 했다.

## 구현

1. `DeskCard`의 layered 분기, `AllowsTransparency`, `Layered`, `Pad`, `Inset`, `TopPad`, WPF 강조 그림자를 제거했다. `WindowChrome`의 전체 유리 틀과 시스템 아크릴, 둥근 모서리를 모든 바탕화면 카드에 적용한다. `SetWindowCompositionAttribute` / ACCENT 경로는 추가하지 않았다.
2. `PopupBg`는 공통 창 루트에 한 번만 칠한다. `.dard`의 별도 배경은 제거했다. `WM_NCACTIVATE`는 기존처럼 `wParam=1`로 `DefWindowProc`에 전달한다.
3. 폴더 카드 판은 이름 줄까지 감싸는 안쪽 강조 테두리만 남겼다. 이름은 카드 안 아래쪽 28 DIP 행에서 테마 `Fg`를 쓰며 그림자가 없다. 아이콘 칸과 이름 행을 따로 배치한다. 카드 바의 판·그림자·판 아래 이름은 유지했다.
4. 폴더 기준 크기는 `(Cols × cell, Rows × cell + LabelH)`, 새 칸 크기 측정은 `w / 2`다. 기존 `CellSize=72`가 로드·저장 뒤에도 그대로인 것을 검사했다.
5. 폴더 카드는 `WS_EX_NOACTIVATE`를 유지한다. `.dard`만 `Activatable=true`다. Progman 소유자와 평소 `HWND_BOTTOM`, 편집 중 위로 올리는 흐름은 유지했다.
6. 모든 카드가 테마 알림에서 DWM 다크 모드·아크릴·테두리를 다시 적용한다. 닫힐 때 구독을 해제한다. 크기 조절 손잡이의 레이어드 창 전용 알파 1 배경은 `Transparent`로 바꿨다.
7. README와 `.dard` 설계의 창 틀·이름 줄·활성화 설명을 갱신했다.

## 수정한 버그

### 테마·선택 변경 때 드롭 대상 강조 소실

- 위치: `src/DeskCards/DeskCard.cs`, `UpdateBorder`와 호출부.
- 원인: 테마 알림과 편집·선택 변경이 매번 `UpdateBorder(false)`를 호출해서 드롭 강조를 지웠다. 폴더 카드도 테마 재적용을 받게 되면서 실제로 영향을 받는다.
- 재현: 드롭 강조를 켠 뒤 `Theme.Apply()`와 Dispatcher 처리를 거치면 안쪽 테두리가 2에서 0으로 바뀌었다. 추가한 회귀 검사에서 수정 전 실패를 확인했다.
- 수정: 드롭 상태를 별도 보관한다. 테마·편집·선택 변경은 상태를 보존하고 `DragLeave` / `Drop`만 명시적으로 해제한다. 수정 후 해당 검사와 전체 회귀 검사가 통과했다.

## 검증

- `dotnet build src/DeskCards/DeskCards.csproj --nologo -warnaserror`: 경고 0, 오류 0.
- `dotnet run --project tests/DeskCards.RegressionTests`: **44개 통과, Failures: 0**.
- 첫 구현 커밋과 버그 수정 커밋을 만들기 전에도 앱 빌드와 당시 전체 회귀 검사를 통과했다.
- 모든 설정·파일·WebView2 데이터는 테스트가 만든 `%TEMP%\DeskCards-regression-<GUID>` 안에서 사용했다.

추가 검사:

- 폴더·`.dard`의 실제 HWND 스타일: 레이어드 아님, 도구 창, 활성화 허용 여부, Progman 소유자.
- DWM 조회: 시스템 배경 3(아크릴), 둥근 모서리 설정 2, 현재 테마에 맞는 다크 모드. 일부러 다르게 설정한 DWM 값을 테마 알림이 복구하는지도 검사했다.
- 폴더 기준 크기, 창 내용 영역의 여백 없음, 아이콘·이름이 겹치지 않음, 이름 색·그림자 제거, 저장된 칸 크기 보존.
- 카드 바의 기존 배경·테두리·그림자·판 아래 이름 유지.
- 편집 선택·손잡이, 물리 픽셀 이동, 비율 확대, 편집 종료 후 안쪽 테두리와 topmost 해제.
- 드롭 강조의 테마·선택 변경 후 유지와 종료 후 해제.
- 실제 WebView2 문서를 불러온 뒤 편집 캡처 생성, 웹 화면 숨김, 편집 종료 후 웹 화면 복원 및 확대 비율 유지. `.dard`에서도 테마 재적용 확인.
- 기존 파일 이동·바로가기 생성, 화면 경계 크기 제한, 이름 변경, 설정·편집 막대·펼친 창 수명, `.dard` 설정·재로드·삭제와 업데이트 검사를 모두 통과했다.

### 캡처 확인과 검사 도구의 보완

`DESKCARDS_SNAPSHOT_DIR`를 지정하면 PerMonitorV2 테스트 프로세스에서 물리 픽셀 크기의 `PrintWindow(PW_RENDERFULLCONTENT=2)` 캡처를 만든다. 이번 결과는 `%TEMP%\DeskCards-dwm-migration-20260928`의 `dwm-folder.png`, `dwm-folder-selected.png`, `dwm-dard-selected.png`와 `regression.log`에 남겼다.

캡처를 직접 열어 폴더 안쪽 이름, 선택 테두리·손잡이, `.dard`의 이름 줄 없는 캡처 화면을 확인했다. 상태 변경 직후에는 이전 합성 프레임이 찍혀 200ms 처리 후 캡처하도록 보완했다. 이 캡처는 내용 배치 확인용이며, 바탕화면과 합성된 아크릴 흐림·DWM 외곽 그림자·클리핑을 전부 재현하지 않는다. 해당 효과의 실제 화면 모양을 완전히 검증했다고 보지는 않는다.

`DWMWA_BORDER_COLOR` 조회는 이 환경에서 `E_INVALIDARG`를 반환했다. [Microsoft 문서](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)도 이 속성을 설정용으로 설명한다. 따라서 네이티브 테두리 색 조회를 테스트 성공 조건으로 삼지 않고, 안쪽 테두리와 캡처를 검사한다.

WebView2 캡처 검사의 첫 시도는 콘솔 본문에서 편집을 호출해 WPF 동기화 컨텍스트가 없었고, 비동기 캡처 뒤 UI 접근 예외가 났다. 실제 메뉴와 같은 Dispatcher 컨텍스트에서 편집을 시작하도록 검사 코드를 고쳤다. 앱의 `DardView` 동작은 변경하지 않았다.

## 고치지 않은 항목과 한계

- 추가로 확정해 남겨 둔 앱 버그는 없다. 다만 `src/DeskCards/DardView.cs:403`의 캡처가 끝나야 `:417`에서 HWND 웹 화면을 숨기는 기존 흐름에는, 캡처 대기 중 웹 입력을 받을 가능성이 있다. 이번 검사에서는 입력 문제를 재현하지 않았다. 요청대로 편집 캡처 흐름을 유지했으며, 느린 캡처 상황에서 실제 입력을 보내는 별도 검증이 필요하다.
- Win+D, 탐색기 재시작, 다중 모니터 이동 중 DPI 변경, 실제 Windows 밝게/어둡게 전환은 직접 실행하지 않았다. Progman 소유자·DWM 속성·테마 알림 처리는 실제 창에서 검사했으나 이것만으로 모든 셸 동작을 검증한 것은 아니다.
- 실제 마우스 끌기·Alt 가이드 해제·호버 펼치기·우클릭 메뉴 입력은 새 자동 입력 시험으로 재현하지 않았다. 관련 핸들러·스마트 가이드·펼친 창 배치 코드는 그대로이고, 기존 파일 처리·창 수명 검사와 새 크기·편집 상태 검사를 통과했다.
- 설정 형식, 설치기, 카드 바 키보드 처리, `.dard` 권한 모델은 이번 변경에 포함하지 않았다.

## 커밋

- `572a575` — 바탕화면 카드를 DWM 아크릴 창으로 통일, 공통 창·크기·편집 회귀 검사 추가.
- `2b5b320` — 테마 전환과 선택 변경 때 카드 드롭 강조 유지.
- 이 보고서와 README·설계 문서, WebView2 편집 복원 검사는 후속 검증·문서 커밋에 함께 기록한다.

모든 작업 커밋에 `Co-authored-by: Codex <noreply@openai.com>`을 붙인다. push·태그·릴리스는 하지 않는다.
