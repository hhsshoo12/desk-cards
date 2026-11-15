# 웹 카드 만들기

웹 카드는 HTML 화면(WebView2)과 선택적인 exe로 이루어진 바탕화면 카드입니다. **보안 장치가 없습니다.** 가져온 카드의 exe는 사용자 권한으로 그대로 실행되니, 믿는 카드만 가져오세요.

## 파일 모양

`.dard`는 zip입니다(안에 폴더 하나로 감싸도 됩니다). 폴더 하나를 그대로 가져와도 됩니다.

```
manifest.json
clock.html        (선택) 카드 화면
helper.exe        (선택) 백그라운드에서 도는 프로그램
```

### manifest.json

```json
{ "id": "com.example.clock", "name": "시계", "size": [260, 140], "html": "clock.html", "exe": "helper.exe", "mode": "card" }
```

| 필드 | 설명 |
|---|---|
| `id` | 카드 고유 이름. 영어 소문자·숫자·`.`·`-`(예: `com.example.clock`). 같은 id를 가져오면 새 내용으로 바뀝니다. |
| `name` | 설정 목록에 보이는 이름 |
| `size` | `[가로, 세로]` 처음 크기(DIP). 기본 300×200 |
| `html` | 카드 화면 파일(폴더 안 상대 경로) |
| `exe` | 함께 돌릴 프로그램(폴더 안 상대 경로). `html`과 `exe` 중 하나는 있어야 합니다. |
| `mode` | `card`(기본): 카드가 보이는 동안만 exe가 돕니다. `background`: 화면을 숨겨도 exe가 계속 돕니다. |

경로가 폴더 밖으로 나가거나 파일이 없으면 가져올 때 거절합니다.

## 가져오고 놓기

설정 → 카드 → **웹 카드**에서 `.dard` 파일이나 폴더를 가져오고, 바탕화면에 놓고, 내리고, 지웁니다. 가져온 카드 데이터 폴더는 `%LOCALAPPDATA%\DeskCards\carddata\<id>`이고 exe에 환경 변수 `DESKCARD_DATA`(데이터 폴더)와 `DESKCARD_ID`로 넘어가고, exe의 작업 폴더는 카드 폴더입니다.

## 화면(HTML)에서 쓰는 `window.deskcard`

배경은 투명이고, 카드 창 뒤는 Windows가 그리는 미카입니다. 글자 색은 직접 정하세요(라이트·다크 둘 다 보이게).

```js
deskcard.send(obj)                 // exe로 보낸다(JSON 한 줄로 stdin에 간다)
deskcard.onmessage = d => {}       // exe가 stdout으로 낸 줄(JSON이면 파싱, 아니면 문자열)
deskcard.bounds                    // {x,y,w,h} CSS 픽셀
deskcard.onbounds = b => {}        // 위치·크기가 바뀔 때
deskcard.visible / onvisibility    // 보이는지
deskcard.window.resize(w, h)       // 크기(CSS px, 80×60 이상)
deskcard.window.move(x, y)         // 위치
deskcard.window.show() / hide()    // 카드 보이기·숨기기
```

카드는 자기 폴더 주소(`https://<id>.deskcard/…`) 안에서만 움직이고 외부 주소로 이동할 수 없습니다. 우클릭은 카드 메뉴로 넘어갑니다. 편집 모드에서는 페이지 전체가 끌기 영역이 됩니다.

## exe와 주고받기

- exe의 **stdout 한 줄 = 화면의 `onmessage` 한 번**, 화면의 `send` 한 번 = exe의 **stdin 한 줄**입니다(UTF-8, 줄 끝 `\n`).
- 앱이 끝나면 exe도 함께 끝납니다(작업 개체로 묶임). 카드를 내리면(`card` 모드) 먼저 정리됩니다.

예제는 `examples/clock`(HTML만)과 `examples/clock.dard`에 있습니다.