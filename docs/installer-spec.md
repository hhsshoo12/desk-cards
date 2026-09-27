# Desk Cards 설치기 명세 (웹 설치기, .NET Framework 4.8)

이 문서는 Python 설치기(`installer/installer.py`)를 C# 웹 설치기로 바꾸는 작업의 요구사항이다.
"해야 한다"는 필수, "해도 된다"는 선택이다. 문서에 없는 동작은 추가하지 않는다. 판단이 필요한 부분이 생기면 구현하지 말고 질문으로 남긴다.

## 1. 목표

- 설치 파일 `DeskCards-Setup.exe`는 1MB 안팎의 작은 exe다. 앱 본체를 담지 않는다.
- 설치할 때 GitHub Releases의 **최신** 앱 zip을 내려받아 설치한다.
- 같은 exe가 설치 폴더에 `uninstall.exe`로 복사되어 제거기 역할도 한다.
- Windows가 권장하는 방식으로 등록한다(설치된 앱 목록, 시작 메뉴, 작업 관리자의 시작 앱).
- 관리자 권한 없이 현재 사용자 계정에만 설치한다.

## 2. 하지 않는 것

- 설치 위치 선택(고정 경로만 사용)
- 특정 버전 설치(항상 최신)
- 오프라인 설치(인터넷이 없으면 안내 후 종료)
- 코드 서명, 자동 업데이트, `.dard` 파일 연결
- 앱 본체(`src/DeskCards`)의 .NET 버전 변경. 앱은 계속 .NET 10 self-contained다.

## 3. 저장소 구성

| 경로 | 내용 |
|---|---|
| `installer/DeskCards.Setup/DeskCards.Setup.csproj` | 새 설치기. SDK 스타일, `net48`, WPF, `LangVersion` latest, NuGet 패키지 없음 |
| `tests/DeskCards.Setup.Tests/` | 설치기 테스트. `net48` 콘솔 앱(기존 `tests/DeskCards.RegressionTests`와 같은 방식) |
| `installer/build.ps1` | 새로 작성. 7장 참고 |

지울 것: `installer/installer.py`, `installer/make_icon.py`, `tests/test_installer.py`, 그리고 `.gitignore`의 Python 관련 항목(`installer/.venv/`, `installer/build/`, `installer/payload/`, `__pycache__/`).
`installer/dist/`는 계속 무시한다.

- 대상 프레임워크는 `net48`이다(모든 Windows 11에 기본 포함. 4.8.1은 22H2부터라 쓰지 않는다).
- 외부 dll 없이 exe 하나로 동작해야 한다. zip은 `System.IO.Compression`/`System.IO.Compression.FileSystem`, 다운로드는 `System.Net.Http.HttpClient`를 쓴다. JSON 파서가 필요 없도록 설계되어 있다.
- 앱 아이콘은 `src/DeskCards/app.ico`를 링크해서 쓴다.
- 매니페스트는 `asInvoker`, `PerMonitorV2`, Windows 10/11 `supportedOS`를 넣는다.

## 4. 상수

| 이름 | 값 |
|---|---|
| 앱 이름 | `Desk Cards` |
| 게시자 | `hhsshoo12` |
| 저장소 URL | `https://github.com/hhsshoo12/desk-cards` |
| 설치 폴더 | `%LOCALAPPDATA%\Programs\Desk Cards` (고정) |
| 앱 exe | `DeskCards.exe` |
| 제거기 | `uninstall.exe` (설치기와 같은 파일) |
| 릴리스 목록 API | `https://api.github.com/repos/hhsshoo12/desk-cards/releases?per_page=100` |
| 앱 릴리스 태그 | `app-v<주.부.수>` (예: `app-v0.2.0`) |
| 설치기 릴리스 태그 | `installer-v<주.부.수>` (예: `installer-v1.0.0`) |
| 앱 zip 파일 이름 | `DeskCards-win-x64.zip` |
| 해시 파일 이름 | `DeskCards-win-x64.zip.sha256` |
| 설치기 파일 이름 | `DeskCards-Setup.exe` |
| 설치기 다운로드 주소(README용) | `https://github.com/hhsshoo12/desk-cards/releases/latest/download/DeskCards-Setup.exe` |
| 제거 등록 키 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\DeskCards` |
| 자동 실행 키 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, 값 이름 `DeskCards` |
| 시작 앱 승인 키 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run`, 값 이름 `DeskCards` |
| AppUserModelID | `hhsshoo12.DeskCards` |
| 시작 메뉴 바로가기 | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Desk Cards.lnk` |
| 바탕화면 바로가기 | 사용자 바탕화면 폴더(`Environment.SpecialFolder.DesktopDirectory`)의 `Desk Cards.lnk` |
| 그룹 폴더 | `%USERPROFILE%\DeskCards` — **설치기는 절대 읽거나 지우지 않는다** |
| 앱 설정 폴더 | `%APPDATA%\DeskCards` |
| 예전 이름 | exe `DeskFolders.exe`, Run 값 `DeskFolders`, 설정 폴더 `%APPDATA%\DeskFolders` |
| 앱 종료 요청 이벤트 | `DeskCards.Quit` (이름 있는 `EventWaitHandle`, AutoReset) |
| 앱 단일 실행 Mutex | `DeskCards.SingleInstance` (이미 앱에 있음) |

## 5. 릴리스 구성

앱과 설치기는 **따로** 릴리스한다. 버전도 따로 올린다.

### 앱 릴리스 (태그 `app-vX.Y.Z`)

| 파일 | 내용 |
|---|---|
| `DeskCards-win-x64.zip` | 루트에 `DeskCards.exe`(현재 build.ps1과 같은 self-contained 단일 exe, 내부 압축 켬)와 `version.txt`(`X.Y.Z` 한 줄, BOM 있어도 읽혀야 함) |
| `DeskCards-win-x64.zip.sha256` | zip의 SHA-256 소문자 16진수 64자. 뒤에 공백과 파일 이름이 붙어 있어도 첫 토큰만 읽는다 |

- "Latest" 표시를 하지 않는다(`gh release create ... --latest=false`).

### 설치기 릴리스 (태그 `installer-vX.Y.Z`)

| 파일 | 내용 |
|---|---|
| `DeskCards-Setup.exe` | 설치기 |

- 설치기 릴리스에 **"Latest" 표시를 한다**. 그래서 README의 `releases/latest/download/DeskCards-Setup.exe` 주소가 항상 최신 설치기를 가리킨다.
- 설치기는 앱을 찾을 때 "Latest" 표시를 쓰지 않는다(아래 "최신 앱 찾기").

### 최신 앱 찾기

1. 릴리스 목록 API를 `GET`한다. 헤더: `User-Agent: DeskCards-Setup/<설치기 버전>`, `Accept: application/vnd.github+json`.
2. 응답(JSON 배열)에서 `draft`와 `prerelease`가 모두 `false`이고 `tag_name`이 정규식 `^app-v(\d+)\.(\d+)\.(\d+)$`(대소문자 무시)에 맞는 릴리스만 고른다.
3. 태그의 숫자 세 개를 `System.Version`으로 비교해 가장 큰 것을 고른다(릴리스 날짜나 목록 순서를 믿지 않는다).
4. 그 릴리스의 `assets`에서 이름이 `DeskCards-win-x64.zip`, `DeskCards-win-x64.zip.sha256`인 항목의 `browser_download_url`을 쓴다. 둘 중 하나라도 없으면 "받을 수 있는 앱 버전이 없어요"로 중단한다.
5. JSON 해석은 .NET Framework 기본 포함 라이브러리(`System.Runtime.Serialization.Json.DataContractJsonSerializer` 또는 `System.Web.Script.Serialization.JavaScriptSerializer`)로 한다. NuGet 패키지를 쓰지 않는다. 필요한 필드(`tag_name`, `draft`, `prerelease`, `assets[].name`, `assets[].browser_download_url`)만 읽는다.
6. API가 403/429(요청 한도)면 "잠시 후 다시 시도해 주세요"로, 네트워크 오류면 "인터넷에 연결한 뒤 다시 시도해 주세요"로 안내한다.

## 6. 실행 모드

| 조건 | 모드 |
|---|---|
| 인자에 `/uninstall` | 제거 |
| 그 밖 | 설치 또는 업데이트(제거 등록 키가 있고 `InstallLocation`의 `DeskCards.exe`가 있으면 업데이트) |

설치기는 한 번에 하나만 실행된다(`DeskCards.Setup.SingleInstance` Mutex). 이미 실행 중이면 기존 창을 앞으로 가져오고 끝낸다.

## 7. 빌드 (`installer/build.ps1`)

1. `src/DeskCards/DeskCards.csproj`의 `<Version>`을 읽는다.
2. 앱을 지금과 같은 옵션으로 게시한다: `-c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none`.
3. `installer/dist/app/`에 앱 릴리스 파일 두 개를 만든다. zip 안의 `version.txt`는 BOM 없는 UTF-8.
4. 설치기를 `-c Release`로 빌드해 `installer/dist/installer/DeskCards-Setup.exe`로 복사한다. 설치기 버전은 `installer/DeskCards.Setup/DeskCards.Setup.csproj`의 `<Version>`이다.
5. `-App`, `-Installer` 스위치로 둘 중 하나만 만들 수도 있게 한다(기본은 둘 다).
6. 업로드는 하지 않는다. 마지막에 올릴 파일과 예시 명령을 출력만 한다.
   - 앱: `gh release create app-vX.Y.Z installer/dist/app/* --title "Desk Cards X.Y.Z" --latest=false`
   - 설치기: `gh release create installer-vX.Y.Z installer/dist/installer/DeskCards-Setup.exe --title "Desk Cards 설치기 X.Y.Z" --latest`

## 8. 설치 / 업데이트 흐름

모든 단계는 `%TEMP%\DeskCards-Setup.log`에 기록한다(시각, 단계, 예외 전문).

1. **버전 확인**: 5장 "최신 앱 찾기"로 최신 앱 버전과 파일 주소를 얻는다. 실패하면 "인터넷에 연결한 뒤 다시 시도해 주세요" 화면을 보여 주고 [다시 시도]/[닫기]만 제공한다.
   업데이트 모드에서 설치된 버전(`DisplayVersion`)과 같으면 "이미 최신 버전이에요" 안내와 함께 [다시 설치]를 제공한다.
   버전 비교는 `System.Version`으로 한다. 어느 쪽이든 해석할 수 없으면 "다른 버전"으로 본다.
   4단계에서 zip 안의 `version.txt`가 태그의 버전과 다르면 설치를 거부한다.
2. **다운로드**: 1단계에서 얻은 주소로 zip과 `.sha256`을 `%TEMP%\DeskCards-Setup-<GUID>\`에 받는다.
   - `User-Agent: DeskCards-Setup/<설치기 버전>` 헤더를 넣는다. 리디렉션을 따른다.
   - TLS 프로토콜을 코드에 고정하지 않는다(OS 기본값 사용).
   - 진행률(받은 MB / 전체 MB, 속도)을 표시한다. [취소]하면 임시 폴더를 지우고 아무것도 바꾸지 않은 채 끝낸다.
   - 300MB를 넘으면 중단한다.
3. **검증**: SHA-256이 다르면 중단한다("내려받은 파일이 손상됐어요. 다시 시도해 주세요").
4. **압축 풀기**: 같은 임시 폴더의 `stage\`에 푼다.
   - 모든 항목의 최종 경로가 `stage\` 안이어야 한다. 절대 경로, `..`, 드라이브 문자가 있으면 전체를 거부한다.
   - 풀린 전체 크기가 1GB를 넘으면 거부한다.
   - `DeskCards.exe`와 `version.txt`가 없으면 거부한다.
5. **앱 종료**: 앱이 실행 중이면(`DeskCards.SingleInstance` Mutex 존재) `DeskCards.Quit` 이벤트를 신호한다.
   5초 안에 설치 폴더의 `DeskCards.exe` 프로세스가 끝나지 않으면 그 프로세스만 강제 종료하고 최대 5초 더 기다린다.
   그래도 남아 있으면 중단한다("Desk Cards를 끄지 못했어요").
   이 단계부터 [취소]는 비활성화한다.
6. **교체**:
   - 설치 폴더가 없으면 `stage\`의 내용을 설치 폴더로 옮긴다.
   - 있으면 기존 `DeskCards.exe`를 `DeskCards.exe.old`로 이름을 바꾸고 새 파일을 옮긴다. 이후 단계에서 실패하면 `.old`를 되돌린다. 성공하면 `.old`를 지운다.
   - 설치 폴더의 예전 이름 파일(`DeskFolders.exe`)을 지운다.
   - 설치 폴더의 다른 파일은 건드리지 않는다.
7. **제거기 복사**: 실행 중인 설치기 exe를 `uninstall.exe.new`로 복사한 뒤 `uninstall.exe`로 바꿔치기한다. 설치기가 이미 설치 폴더의 `uninstall.exe`에서 실행 중이면 건너뛴다.
8. **바로가기**: 추가 작업 선택에 따라 만들거나 지운다(9장).
9. **등록**: 10장의 제거 등록 키 값을 모두 쓴다.
10. **자동 실행**: 11장.
11. **정리**: 임시 폴더를 지운다. 예전 Run 값 `DeskFolders`를 지운다.
12. **완료**: [Desk Cards 실행] 체크(기본 켬)가 켜져 있으면 [마침] 때 앱을 실행한다.

실패하면 어느 단계에서 무엇이 실패했는지 한국어로 보여 주고, 로그 파일을 여는 링크를 둔다. 6~10단계 중 실패하면 6단계의 되돌리기를 수행한다. 1~4단계에서 실패하면 앱을 끄지 않고 아무것도 바꾸지 않는다.

### 업데이트할 때의 기본 선택

추가 작업 페이지의 체크 상태는 현재 상태에서 가져온다.

- 시작 메뉴 바로가기: 파일이 있으면 켬
- 바탕화면 바로가기: 파일이 있으면 켬
- Windows 시작 시 실행: 11장의 "켜져 있음" 판정

새 설치의 기본값은 시작 메뉴 켬, 바탕화면 끔, 시작 시 실행 켬이다.

## 9. 바로가기

- `IShellLinkW` + `IPersistFile`로 만든다. 대상은 설치 폴더의 `DeskCards.exe`, 작업 폴더는 설치 폴더, 아이콘은 exe의 0번.
- 시작 메뉴 바로가기에는 `IPropertyStore`로 `PKEY_AppUserModel_ID` = `hhsshoo12.DeskCards`를 넣는다(Windows 알림에 필요).
- 바탕화면 바로가기에도 같은 값을 넣어도 된다.

## 10. 설치된 앱 목록 등록

`HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\DeskCards`

| 값 | 형식 | 내용 |
|---|---|---|
| `DisplayName` | SZ | `Desk Cards` |
| `DisplayVersion` | SZ | zip의 `version.txt` |
| `Publisher` | SZ | `hhsshoo12` |
| `URLInfoAbout` | SZ | 저장소 URL |
| `InstallLocation` | SZ | 설치 폴더 |
| `DisplayIcon` | SZ | `"<설치 폴더>\DeskCards.exe",0` |
| `UninstallString` | SZ | `"<설치 폴더>\uninstall.exe" /uninstall` |
| `NoModify` | DWORD | 1 |
| `NoRepair` | DWORD | 1 |
| `EstimatedSize` | DWORD | 설치 폴더 전체 크기(KB) |
| `InstallDate` | SZ | `yyyyMMdd` |

`QuietUninstallString`은 쓰지 않고, 있으면 지운다(무인 제거를 지원하지 않음).

## 11. 시작 앱 (작업 관리자와 같은 상태)

Windows는 `Run` 값을 작업 관리자 "시작 앱"에 보여 주고, 사용자가 거기서 끄면 `StartupApproved\Run`의 같은 이름 값(REG_BINARY 12바이트)에 기록한다.
첫 바이트가 홀수(예: `03`)면 꺼짐, 짝수(예: `02`)면 켜짐이다. 값이 없으면 켜짐으로 본다.

- **켜짐 판정**: `Run\DeskCards`가 있고, `StartupApproved\Run\DeskCards`가 없거나 첫 바이트가 짝수.
- **켜기**: `Run\DeskCards` = `"<설치 폴더>\DeskCards.exe"`, `StartupApproved\Run\DeskCards` = `02 00 00 00 00 00 00 00 00 00 00 00`.
- **끄기**: `Run\DeskCards`와 `StartupApproved\Run\DeskCards`를 지운다.

### 앱 쪽 변경 (`src/DeskCards`)

설치기와 같은 규칙을 앱에도 적용해야 한다.

1. `AppPaths.cs`의 `AutoStart.Enabled` get/set을 위 판정·켜기·끄기 규칙으로 바꾼다. 설정 창 스위치가 작업 관리자와 같은 상태를 보여야 한다.
2. `App.xaml.cs`: `DeskCards.Quit` 이벤트를 기다리는 스레드를 추가한다. 신호를 받으면 UI 스레드에서 트레이 메뉴의 [종료]와 같은 경로로 정상 종료한다(설정 저장 포함). 기존 `DeskCards.ShowSettings` 처리와 같은 구조로 만든다.
3. 시작할 때 `SetCurrentProcessExplicitAppUserModelID("hhsshoo12.DeskCards")`를 호출한다.

## 12. 제거 흐름

1. 대상 폴더는 `/target` 인자, 없으면 제거 등록 키의 `InstallLocation`, 그것도 없으면 4장의 고정 설치 폴더다.
   설치 폴더의 `uninstall.exe`에서 실행되었으면, 자신을 `%TEMP%\DeskCards-uninstall-<PID>.exe`로 복사하고 그 사본을 `/uninstall /target "<설치 폴더>"`로 실행한 뒤 즉시 끝낸다.
2. 확인 페이지: [카드 위치·크기 설정도 지우기] 체크(기본 끔). 그룹 폴더(`%USERPROFILE%\DeskCards`)는 지우지 않는다는 안내를 보여 준다.
3. 8장 5단계와 같은 방식으로 앱을 끈다.
4. 시작 메뉴·바탕화면 바로가기를 지운다.
5. `Run`과 `StartupApproved\Run`의 `DeskCards`, `DeskFolders` 값을 지운다.
6. 설치 폴더의 `DeskCards.exe`, `DeskFolders.exe`, `uninstall.exe`를 지운다. **하나라도 지우지 못하면 제거 등록 키를 남기고** 오류를 보여 준다(설정 앱에서 다시 제거할 수 있도록).
7. 제거 등록 키를 지운다.
8. 시작 메뉴 흔적을 지운다(13장).
9. 설치 폴더가 비어 있으면 지운다. 다른 파일이 있으면 남긴다.
10. 체크했으면 `%APPDATA%\DeskCards`, `%APPDATA%\DeskFolders`를 지운다.
11. 완료 페이지. [마침]을 누르면 시작 메뉴 흔적을 한 번 더 지우고(13장), 12.1의 TEMP 사본을 예약 삭제한다.
    예약 삭제는 기존 Python의 `schedule_cleanup`과 같다: PowerShell `-EncodedCommand`로 2초 간격 최대 30번 `Remove-Item -LiteralPath`를 시도한다. 경로는 작은따옴표 리터럴로 넣고 `'`는 `''`로 바꾼다. `CREATE_NO_WINDOW`로 실행한다.

## 13. 시작 메뉴 흔적 정리

기존 `installer.py`의 `remove_start_traces`, `is_our_tile`, `only_our_tiles`를 그대로 옮긴다.

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Start\TileProperties`의 하위 키 중 이름이 `W~<설치 폴더>\DeskCards.exe` 또는 `W~<설치 폴더>\DeskFolders.exe`와 **대소문자 무시 완전 일치**하는 키만 지운다. 부분 문자열 비교는 금지.
- `HKCU\Software\Microsoft\Windows\CurrentVersion\AppListBackup`의 `ListOfEventDrivenBackedUpTiles*` 값은 UTF-16LE JSON(배열 또는 객체)이다. 모든 항목의 `tileId`가 위 규칙으로 우리 것일 때만 값을 지운다. 하나라도 다른 앱 것이면 남긴다. 해석할 수 없으면 남긴다.

## 14. 화면

WinUI 3(Windows 11 Fluent) 모양을 **WPF로** 구현한다. WinUI 3 프레임워크 자체는 .NET Framework에서 쓸 수 없으므로 사용하지 않는다.
시각 기준은 앱의 `src/DeskCards/SettingsWindow.xaml`(설정 창)이다. 색은 `src/DeskCards/Theme.cs`의 밝게/어둡게 값을 복사해서 쓴다.

- 창: 약 720×520 고정 크기, 크기 조절 불가, 최대화 불가. Windows 22621 이상이면 Mica(`DWMWA_SYSTEMBACKDROP_TYPE`=2), 다크 모드 속성, 둥근 모서리.
- 글꼴: `Segoe UI Variable Text, Segoe UI, Malgun Gothic`. 아이콘: `Segoe Fluent Icons, Segoe MDL2 Assets`.
- 배치: 왼쪽에 단계 목록(현재 단계 강조, 지난 단계는 체크), 오른쪽에 페이지 내용, 아래 막대에 [뒤로] [다음 또는 실행 버튼(파란색)] [취소].
- 체크 항목은 설정 창의 스위치나 Win11 체크박스 모양을 쓴다. 진행 막대는 Win11 모양(얇은 막대, 파란색)으로 만든다.
- 시스템 `MessageBox`를 쓰지 않는다. 확인이 필요하면 `src/DeskCards/DialogWindow.xaml`과 같은 모양의 대화 상자를 만든다.

### 설치 단계

| 단계 | 내용 |
|---|---|
| 시작 | 앱 소개 한두 줄, 설치 위치(고정, 표시만), 필요한 공간(약 80MB), "인터넷에서 최신 버전을 내려받아요" 안내 |
| 추가 작업 | 시작 메뉴 바로가기 / 바탕화면 바로가기 / Windows 시작 시 실행 |
| 설치 준비 | 선택 요약. 파란 버튼 글자는 [설치] |
| 설치 | 다운로드 진행률 → 단계별 상태 문장 |
| 완료 | [Desk Cards 실행] 체크, [마침] |

### 업데이트 단계

설치 단계와 같고, 시작 페이지에 "설치된 버전 X → 최신 버전 Y"를 보여 준다. 파란 버튼 글자는 [업데이트]다. 그룹과 카드 설정이 그대로 남는다는 안내를 넣는다.

### 제거 단계

| 단계 | 내용 |
|---|---|
| 시작 | 12장 2단계의 확인 내용. 파란 버튼 [제거] |
| 제거 | 단계별 상태 |
| 완료 | [마침] |

## 15. 테스트 (필수)

**테스트는 실제 레지스트리 값과 사용자 폴더를 절대 건드리지 않는다.** 예전에 테스트가 실제 `%APPDATA%\DeskCards`를 지운 사고가 있었다.

- 설치 로직은 화면과 분리된 클래스로 만들고, 모든 경로와 레지스트리 위치를 `SetupEnvironment` 같은 객체 하나로 주입받는다(설치 폴더, 시작 메뉴 폴더, 바탕화면 폴더, 설정 폴더, TEMP, 레지스트리 루트 키 경로).
- 테스트 환경은 폴더를 `%TEMP%\DeskCards-Setup-Tests-<GUID>\` 아래에, 레지스트리를 `HKCU\Software\DeskCards-Setup-Tests\<GUID>\` 아래에 둔다. 테스트가 끝나면 둘 다 지운다.
- 테스트용 환경 객체는 생성할 때 모든 경로가 위 두 위치 안에 있는지 검사하고, 아니면 예외를 던진다.
- 다운로드는 HTTP 호출을 추상화해서 로컬 파일로 대체한다. 테스트에서 실제 네트워크를 쓰지 않는다.

필수 테스트:

1. `tests/test_installer.py`의 10개를 옮긴다(타일 판정 5개, BOM 있는 버전 파일, 앱 종료 시간 초과 보고, 다운로드·검증 실패 시 앱을 끄지 않음(기존 "페이로드 없음" 테스트의 웹 설치기 판), 제거 실패 시 등록 유지, 예약 삭제 경로 리터럴 처리).
2. zip에 `..\` 경로가 있으면 거부하고 설치 폴더를 바꾸지 않는다.
3. SHA-256 불일치면 설치 폴더를 바꾸지 않는다.
4. 교체 후 단계에서 실패하면 기존 `DeskCards.exe`가 되돌려진다.
5. 업데이트할 때 바로가기·자동 실행의 현재 상태가 기본 선택으로 읽힌다.
6. `StartupApproved` 판정: 값 없음 = 켜짐, `02…` = 켜짐, `03…` = 꺼짐, `Run` 없음 = 꺼짐.
7. 제거 시 그룹 폴더와(체크하지 않았으면) 설정 폴더가 남는다.
8. 설치 폴더에 다른 파일이 있으면 제거 후에도 폴더와 그 파일이 남는다.
9. 최신 앱 찾기: 저장된 API 응답 JSON으로 검사한다. `installer-v…`, `v0.1.0`, draft, prerelease는 제외되고, `app-v0.10.0`이 `app-v0.9.0`보다 새 버전으로 골라지며(문자열 비교 금지), 필요한 파일이 없는 릴리스면 오류가 난다.
10. zip 안 `version.txt`가 태그 버전과 다르면 설치 폴더를 바꾸지 않는다.

실행 방법을 `README.md`의 "회귀 테스트" 절에 추가한다. `python -m unittest` 줄은 지운다.

## 16. 문서

- `README.md`의 "설치" 절: 다운로드 링크를 `https://github.com/hhsshoo12/desk-cards/releases/latest/download/DeskCards-Setup.exe`로, 설치 파일 이름을 `DeskCards-Setup.exe`로, "설치할 때 인터넷 연결이 필요합니다"를 추가, 설치 위치 선택 문구를 삭제한다.
- `README.md`의 "직접 빌드하기": Python 요구 사항을 지운다.

## 17. 기존 설치(0.1.0)에서 넘어오기

- 기존 릴리스 `v0.1.0`은 5장 형식이 아니다(태그가 `app-v`로 시작하지 않음). 새 설치기는 이를 무시한다. 첫 배포는 `app-vX.Y.Z`를 먼저 올리고, 그다음 `installer-vX.Y.Z`를 "Latest"로 올리는 순서다. 이 순서를 build.ps1 출력에 적는다.
- 0.1.0은 Python 설치기로 같은 폴더·같은 등록 키에 설치되어 있다. 새 설치기는 이를 업데이트 모드로 처리해야 한다.
  - 0.1.0 앱에는 `DeskCards.Quit` 처리가 없으므로 5단계의 강제 종료 경로를 탄다(정상).
  - 기존 81MB `uninstall.exe`(PyInstaller)는 7단계에서 새 제거기로 덮어쓴다.
  - `UninstallString`은 10장 값으로 덮어쓴다.

## 18. 완료 조건

- `dotnet build -warnaserror`가 앱, 설치기, 두 테스트 프로젝트에서 경고 없이 통과한다.
- 모든 테스트가 통과한다.
- `installer/build.ps1`이 5장의 앱 릴리스 파일 두 개와 설치기 파일을 만든다. `DeskCards-Setup.exe`는 1MB 이하이며 옆에 dll이 없어도 실행된다.
- 커밋은 기능 단위로 나눈다. 커밋 작성자 이메일은 저장소에 설정된 `269848033+hhsshoo12@users.noreply.github.com`을 그대로 쓴다(바꾸지 않는다).
- 실제 설치/제거는 수동으로 확인한다. 자동 테스트에서 실제 설치 경로에 설치하지 않는다.
