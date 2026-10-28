# DeskCards 카드 v2 개편 (GPT 작업 프롬프트)

저장소: `V:\desktop\deskfolders` (WPF .NET 10, 공개 repo hhsshoo12/desk-cards). 먼저 `docs/dard-design.html`(새 설계)을 끝까지 읽고, 이전 설계는 `docs/dard-design-v1.html`에 있다. 새 설계가 우선이다.

## 방향 요약

- 전제: **카드는 사용자가 직접 만들었거나 믿는 앱이다.** 권한, 승인, 검증, 샌드박스는 두지 않는다.
- 카드 = 폴더(`manifest.json`, `card.html`, 선택 `main.py`, 선택 `settings.html`). `.dard` zip 패키지, 해시, 파일 목록 제한은 없앤다.
- 카드는 앱 내부 저장소 `%LOCALAPPDATA%\DeskCards\cards\<id>\`로 **가져온다**(복사). 그룹 폴더의 `*.dard` 감시는 없앤다.
- 실행은 카드마다 루프백 IP(`127.x.y.z`, 카드 id 해시, 고정 포트)에서 `main.py`가 띄우는 로컬 웹서버를 WebView2가 여는 구조. `main.py`가 없으면 앱이 같은 주소에서 폴더를 정적으로 제공한다. desk API는 없다.
- 기존 .dard 사용자는 없으므로 **변환·하위 호환 코드를 만들지 않는다.**

## 지켜야 할 것

- 폴더 카드(그룹)와 카드 틀(DWM 둥근 창, 아크릴, Progman 층, 편집 모드, 스마트 가이드)은 건드리지 않는다.
- 앱 자동 업데이트 규약은 바꾸지 않는다: 태그 `app-vX.Y.Z`, 자산 `DeskCards-win-x64.zip`과 `.sha256`, zip 루트의 `DeskCards.exe`와 `version.txt`, 릴리스는 draft/prerelease 금지.
- WebView2 복구 정책(`BrowserRecoveryPolicy`, `RendererRecoveryPolicy`)과 "보이지 않으면 5초 뒤 일시정지" 모델은 유지한다.
- 한 단계가 끝날 때마다 빌드와 테스트가 통과해야 하고, 단계마다 따로 커밋한다.
- **`tests\DeskCards.Setup.Tests\bin\Release\net48\DeskCards-Setup.exe`는 절대 실행하지 않는다**(실제 설치창이 뜬다). 설치기 테스트는 같은 폴더의 `DeskCards.Setup.Tests.exe`를 직접 실행한다.
- 빌드 환경: `$env:DOTNET_ROOT="$env:USERPROFILE\scoop\apps\dotnet10-sdk\current"`, 설치기는 `-p:AutomaticallyUseReferenceAssemblyPackages=true`가 필요하다. 앱 테스트: `dotnet run --project tests\DeskCards.RegressionTests -c Release --no-build`(필터는 `DESKCARDS_TEST_FILTER`).

## 1단계: 걷어내기

설계 문서의 "없애는 것" 표를 코드에서 제거한다. 대상은 `src/DeskCards/`의 `Dard*.cs`를 중심으로 다음과 같다. 파일 이름은 현재 코드를 보고 확인한다.

- 권한 모델: manifest의 `permissions`, 권한 목록·등급, 설치 창(권한 승인)과 `GroupManager.Dards.cs`의 승인/해시 재확인(`Approval`, `NextDardPrompt`), `Config`의 `Dards` 승인 기록
- 보안 정책: `DardScriptPolicy`(CSP, 스크립트 해시), `DardProxy`(내부망 차단 프록시와 막다른 프록시), `PermissionRequested`/`NavigationStarting`/`WebResourceRequested` 차단, 주입한 `desk` 라이브러리와 모든 desk.* API(`DardRuntime`의 메시지 처리)
- 저장소 관리: `DardStorage*.cs`(위치 구분, 용량 한도, 옮기기, 복구, `desk-origins.json`, 불일치 경고)와 설정 화면의 위젯 카드 저장소 목록
- 패키지: `DardPackage`의 zip 파싱·크기 제한·파일 목록 검사, 출처 배지
- 제약: 비율 고정과 "페이지 화면 크기 고정(확대 비율)" 방식. 카드는 사용자가 자유롭게 크기를 바꾸고, 페이지는 실제 창 크기를 본다.
- 테스트: 위에 해당하는 테스트(`DardLeakTests`, `DardStorageTests`, `DardRecoveryTests`, 권한·해시 테스트 등)를 같이 지운다. 남기는 기능의 테스트는 지우지 않는다.

이 단계가 끝난 상태에서도 앱이 빌드·실행되고 폴더 카드는 정상이어야 한다. 카드(.dard) 기능은 일시적으로 없는 상태여도 된다.

## 2단계: 폴더 카드와 가져오기

- 새 `Card` 모델: `manifest.json` = `{ id, name, size:[w,h], background? }`. 읽기 실패와 id 형식 오류는 사용자에게 알린다.
- 가져오기 UI(그룹의 "카드 만들기"):
  1. 폴더 선택 → 복사
  2. 직접 선택 → 이름, HTML 파일, (선택) Python 파일, 처음 크기, 그룹을 입력받아 manifest를 만들어 복사
  3. zip/.dard 파일 → 풀어서 폴더로 만든 뒤에는 폴더만 사용
  가져오기 전에 "이 카드는 PC에 제한 없이 접근해요. 직접 만들었거나 믿는 카드만 쓰세요." 안내를 한 번 보여 준다.
- 카드 메뉴: 카드 설정(settings.html이 있을 때), 다시 가져오기, 폴더 열기(내부 폴더), 삭제(확인 후 내부 폴더도 삭제). 개발용 "연결 모드"(원본 폴더를 복사 없이 쓰고 파일이 바뀌면 새로 고침)도 만든다.
- 어느 그룹에 속하는지, 위치와 크기는 앱 설정(config)에 저장한다.
- 제거기의 "모든 설정과 설치한 카드 지우기"는 `LocalDataDir`(= `%LOCALAPPDATA%\DeskCards`)에 카드 폴더가 들어 있으니 그대로 지워지는지 확인하고, 그룹 루트의 최상위 `*.dard` 삭제 코드가 더 필요한지 판단해서 정리한다(`installer/DeskCards.Setup/SetupEngine.cs`, `docs/installer-spec.md` 갱신).

## 3단계: 실행 (로컬 서버)

- 카드마다 루프백 주소를 정한다: 카드 id의 해시로 `127.x.y.z`(`127.0.0.1` 제외)를 정하고, 다른 카드와 겹치면 다음 주소로 밀어서 config에 저장한다. 포트는 흔하지 않은 번호 하나로 고정하고, 바인딩에 실패하면 다른 포트로 다시 시도한다.
- `main.py`가 있으면 번들한 Python(embeddable)으로 실행하고 환경변수 `DESK_ADDR`, `DESK_PORT`를 넘긴다. 포트가 열릴 때까지 기다린 뒤 WebView를 `http://<DESK_ADDR>:<DESK_PORT>/`로 연다. 제한 시간 안에 안 열리면 카드 안에 오류 화면(마지막 stderr 일부)과 [다시 시작]을 보여 준다.
- `main.py`가 없으면 앱이 같은 주소에서 카드 폴더를 정적으로 제공한다(`HttpListener` 등 구현은 선택).
- 프로세스는 카드를 지우거나 앱이 종료될 때 자식 프로세스까지 정리한다(Job Object 사용 권장). 백엔드가 죽으면 오류 화면과 [다시 시작], 자동 재시작은 분당 3회까지.
- 생명주기: 카드가 보이지 않는 상태가 5초 지속되면 WebView `TrySuspendAsync`와 백엔드 프로세스 일시정지(`NtSuspendProcess`), 다시 보이면 재개. manifest의 `background: true`이면 일시정지하지 않는다.
- 토큰·호스트 검증은 두지 않는다(로컬 앱을 신뢰하는 전제).
- Python 번들 방식과 패키지 설치는 설계 문서 "남은 질문"에 열려 있으므로, 먼저 **개발용으로 PATH의 `python`을 쓰는 임시 경로**로 구현하고 번들 방식은 따로 정한다(결정이 필요하면 멈추고 알린다).

## 4단계: 디자인 키트와 문서

- 기존 테마 변수(`--desk-*`) 주입을 확장해 `desk.css`로 만든다: 간격·모서리·글꼴 토큰, 버튼·토글·슬라이더·목록 행·제목/본문 스타일(`.desk-*` 클래스), 다크·라이트 자동, 투명 배경. JS API 없이 CSS만 넣는다.
- `docs/card-guide.md`: AI에게 그대로 붙여 넣을 수 있는 한 장 지침(폴더 구조, manifest, `main.py` 서버 템플릿(`DESK_ADDR`/`DESK_PORT`), 디자인 키트 사용법).
- `examples/clock`을 새 구조로 바꾸고 `examples/pack.ps1`은 폴더를 zip으로 묶는 용도로 줄이거나 지운다.
- README의 카드 설명과 `docs/dard-design.html`의 남은 질문 상태를 갱신한다.

## 보고

각 단계가 끝날 때마다 무엇을 지웠고 무엇을 만들었는지, 테스트 결과(통과 수), 새로 생긴 결정 사항을 짧게 보고한다. 설계와 충돌하거나 결정이 필요한 부분은 임의로 정하지 말고 멈춰서 묻는다.
