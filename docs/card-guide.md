# 카드 만들기 — AI에게 전달할 지침

DeskCards 카드는 신뢰하는 로컬 앱을 담는 폴더입니다. `desk.*` JavaScript API, 권한 선언, 해시 승인, 샌드박스는 없습니다.

## 파일

- `manifest.json`: 아래 형식. `id`는 영문 소문자·숫자·점·하이픈으로 만든 고유 이름입니다.
- `card.html`: 화면. `main.py`가 있으면 서버에서 직접 화면을 만들어도 됩니다.
- `main.py`: 선택. 로컬 HTTP 서버와 필요한 시스템 기능을 구현합니다.
- `settings.html`: 선택. 카드 메뉴에서 같은 서버의 이 경로를 엽니다.
- 이미지·글꼴·JS·`data/` 등은 필요한 만큼 추가합니다.

```json
{"id":"com.example.clock","name":"시계","size":[240,160],"background":false}
```

`size`는 최초 DIP 크기이며 이후 사용자가 자유롭게 조절합니다. 페이지는 실제 창 크기에 반응하도록 만드세요. 반응형 CSS를 사용하고 고정 확대 배율에 의존하지 마세요.

## Python 서버 기본형

현재 개발 경로는 PATH의 `python`입니다. Python 번들과 requirements.txt 자동 설치는 아직 도입하지 않았으므로 필요한 패키지는 사용자가 해당 Python 환경에 설치합니다.

```python
import os
from http.server import ThreadingHTTPServer, SimpleHTTPRequestHandler

class Handler(SimpleHTTPRequestHandler):
    def do_GET(self):
        if self.path == '/':
            self.path = '/card.html'
        # 필요한 /api/... 경로는 여기서 처리합니다.
        super().do_GET()

ThreadingHTTPServer(
    (os.environ['DESK_ADDR'], int(os.environ['DESK_PORT'])), Handler
).serve_forever()
```

반드시 전달된 주소와 포트로 바인딩하세요. HTML은 `fetch('/api/...')`로 자신의 서버에 접근합니다. 저장은 `data/` 같은 실제 파일에 직접 합니다. Python이 없으면 앱의 정적 서버가 `/`에서 `card.html`을 제공합니다.

## 디자인 키트

앱은 모든 카드 페이지에 `desk.css`와 테마 색을 주입합니다. 별도의 JS 라이브러리를 불러오지 마세요.

- 색: `--desk-fg`, `--desk-subfg`, `--desk-accent`, `--desk-card-bg`, `--desk-border`
- 간격: `--desk-space-1/2/3/4/6` (4/8/12/16/24px)
- 모서리: `--desk-radius`, `--desk-radius-lg`
- 글꼴: `--desk-font`, `--desk-text-small/body/title`
- 클래스: `desk-body`, `desk-title`, `desk-text`, `desk-caption`, `desk-button`, `desk-button-primary`, `desk-input`, `desk-select`, `desk-toggle`, `desk-slider`, `desk-list`, `desk-row`, `desk-divider`

```html
<body class="desk-body">
  <h1 class="desk-title">내 카드</h1>
  <label class="desk-row">옵션 <input type="checkbox" class="desk-toggle"></label>
  <button class="desk-button desk-button-primary">실행</button>
</body>
```

투명 배경을 유지하면 창의 아크릴이 보입니다. 다크·라이트는 CSS 변수와 `prefers-color-scheme`을 사용하세요. 입력에 label을 붙이고 키보드 초점을 보존하세요.

## 실행과 가져오기

그룹 메뉴의 **카드 만들기**에서 폴더, zip/.dard 또는 HTML/Python 파일을 선택합니다. 복사본은 `%LOCALAPPDATA%\DeskCards\cards\<id>\`에 저장됩니다. 연결 모드는 원본을 사용하고 변경 시 새로 고칩니다. 원본 코드를 다시 반영하려면 **다시 가져오기**를 사용하세요. 이 작업은 내부 데이터도 원본으로 교체하므로 확인 창을 읽으세요.

보이지 않은 상태가 5초 지속되면 화면과 Python을 일시정지합니다. `background: true`는 가시성에 따른 중지를 제외하며, Windows 절전 정책은 예외로 두지 않습니다. 서버가 종료되면 오류와 다시 시작 버튼이 표시되고 자동 재시작은 분당 최대 3회입니다.
