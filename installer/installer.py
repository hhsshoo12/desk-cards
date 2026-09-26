"""Desk Cards 설치 프로그램.

옛날 설치 마법사처럼 왼쪽에 배너(설치 단계 목록 포함), 오른쪽에 각 단계 내용을 보여 준다.
관리자 권한 없이 사용자 계정에만 설치한다(%LOCALAPPDATA%\\Programs\\Desk Cards).

    python installer.py              설치
    python installer.py /uninstall   제거 (설치 폴더의 uninstall.exe가 이렇게 실행된다)

build.ps1이 앱을 단일 exe로 게시해 payload/에 넣고, PyInstaller로 이 스크립트와 함께 묶는다.
"""
from __future__ import annotations

import base64
import ctypes
import json
import os
import queue
import shutil
import subprocess
import sys
import threading
import time
import tkinter as tk
import winreg
from ctypes import wintypes
from pathlib import Path
from tkinter import filedialog, font as tkfont, messagebox, ttk

APP_NAME = "Desk Cards"
APP_ID = "DeskCards"  # 제거 목록 레지스트리 키 이름
EXE_NAME = "DeskCards.exe"
OLD_EXE_NAMES = ("DeskFolders.exe",)  # 예전 이름. 업데이트·제거할 때 같이 정리한다.
UNINSTALLER_NAME = "uninstall.exe"
RUN_VALUE = "DeskCards"  # 앱의 'Windows 시작 시 실행'과 같은 이름
OLD_RUN_VALUES = ("DeskFolders",)
PUBLISHER = "hhsshoo12"
URL = "https://github.com/hhsshoo12/desk-cards"

UNINSTALL_KEY = rf"Software\Microsoft\Windows\CurrentVersion\Uninstall\{APP_ID}"
RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"

CREATE_NO_WINDOW = 0x08000000
DETACHED_PROCESS = 0x00000008
CREATE_BREAKAWAY_FROM_JOB = 0x01000000


# ----- 경로·환경 -----

def resource(rel: str) -> Path:
    base = Path(getattr(sys, "_MEIPASS", Path(__file__).resolve().parent))
    return base / rel


def read_version() -> str:
    try:
        return resource("payload/version.txt").read_text(encoding="utf-8").strip()
    except OSError:
        return "0.0.0"


VERSION = read_version()
PAYLOAD_EXE = resource(f"payload/{EXE_NAME}")


def shell_folder(csidl: int) -> Path:
    buf = ctypes.create_unicode_buffer(wintypes.MAX_PATH)
    ctypes.windll.shell32.SHGetFolderPathW(None, csidl, None, 0, buf)
    return Path(buf.value)


def start_menu_link() -> Path:
    return shell_folder(0x02) / f"{APP_NAME}.lnk"  # CSIDL_PROGRAMS


def desktop_link() -> Path:
    return shell_folder(0x10) / f"{APP_NAME}.lnk"  # CSIDL_DESKTOPDIRECTORY


def default_install_dir() -> Path:
    return Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData" / "Local")) / "Programs" / APP_NAME


def _new_or_old(base: Path) -> Path:
    """앱은 처음 실행될 때 예전 이름(DeskFolders) 폴더를 DeskCards로 옮긴다. 아직 안 옮겨졌으면 예전 폴더."""
    new, old = base / "DeskCards", base / "DeskFolders"
    return old if old.exists() and not new.exists() else new


def groups_dir() -> Path:
    return _new_or_old(Path.home())


def config_dirs() -> list[Path]:
    base = Path(os.environ.get("APPDATA", Path.home() / "AppData" / "Roaming"))
    return [base / "DeskCards", base / "DeskFolders"]


def installed_info() -> tuple[Path | None, str | None]:
    """이미 설치돼 있으면 (설치 폴더, 버전)."""
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, UNINSTALL_KEY) as k:
            loc = winreg.QueryValueEx(k, "InstallLocation")[0]
            ver = winreg.QueryValueEx(k, "DisplayVersion")[0]
            return Path(loc), ver
    except OSError:
        return None, None


def human_size(n: float) -> str:
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if n < 1024 or unit == "TB":
            return f"{n:.0f} {unit}" if unit in ("B", "KB") else f"{n:.1f} {unit}"
        n /= 1024
    return f"{n:.1f} TB"


def free_space(path: Path) -> int | None:
    p = path
    while not p.exists() and p.parent != p:
        p = p.parent
    try:
        return shutil.disk_usage(p).free
    except OSError:
        return None


def app_running() -> bool:
    try:
        out = subprocess.run(["tasklist", "/NH", "/FO", "CSV"],
                             capture_output=True, text=True, creationflags=CREATE_NO_WINDOW).stdout.lower()
        return any(f'"{n.lower()}"' in out for n in (EXE_NAME, *OLD_EXE_NAMES))
    except OSError:
        return False


def stop_app() -> None:
    if not app_running():
        return
    for name in (EXE_NAME, *OLD_EXE_NAMES):
        subprocess.run(["taskkill", "/IM", name, "/F"], capture_output=True, creationflags=CREATE_NO_WINDOW)
    for _ in range(50):
        if not app_running():
            return
        time.sleep(0.1)


def make_shortcut(link: Path, target: Path) -> None:
    def q(s: object) -> str:
        return "'" + str(s).replace("'", "''") + "'"
    script = (
        f"$s = (New-Object -ComObject WScript.Shell).CreateShortcut({q(link)});"
        f"$s.TargetPath = {q(target)};"
        f"$s.WorkingDirectory = {q(target.parent)};"
        f"$s.IconLocation = {q(str(target) + ',0')};"
        f"$s.Description = {q('바탕화면 폴더 카드')};"
        "$s.Save()"
    )
    link.parent.mkdir(parents=True, exist_ok=True)
    encoded = base64.b64encode(script.encode("utf-16-le")).decode("ascii")
    r = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
                       capture_output=True, text=True, creationflags=CREATE_NO_WINDOW)
    if r.returncode != 0 or not link.exists():
        raise RuntimeError(f"바로가기를 만들지 못했어요: {link}\n{r.stderr.strip()}")


def set_dpi_awareness() -> None:
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)  # 모니터별 DPI
    except Exception:
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:
            pass


# ----- 설치 / 제거 작업 (작업 스레드에서 실행) -----

class Job:
    """작업 스레드 → 화면으로 진행 상황을 보낸다."""

    def __init__(self) -> None:
        self.q: queue.Queue[tuple[str, object]] = queue.Queue()

    def status(self, text: str) -> None:
        self.q.put(("status", text))
        self.q.put(("log", text))

    def log(self, text: str) -> None:
        self.q.put(("log", text))

    def progress(self, pct: float) -> None:
        self.q.put(("progress", pct))


def do_install(job: Job, target: Path, start_menu: bool, desktop: bool, autorun: bool) -> None:
    exe = target / EXE_NAME

    job.status(f"실행 중인 {APP_NAME} 종료")
    stop_app()
    job.progress(5)

    job.status(f"폴더 만들기: {target}")
    target.mkdir(parents=True, exist_ok=True)

    job.status(f"파일 복사: {EXE_NAME}")
    total = PAYLOAD_EXE.stat().st_size
    tmp = exe.with_suffix(".exe.new")
    done = 0
    with open(PAYLOAD_EXE, "rb") as src, open(tmp, "wb") as dst:
        while chunk := src.read(1024 * 1024):
            dst.write(chunk)
            done += len(chunk)
            job.progress(5 + 75 * done / total)
    os.replace(tmp, exe)
    job.log(f"  {human_size(total)} 복사함")
    for old in OLD_EXE_NAMES:
        if (target / old).exists():
            job.status(f"예전 이름의 파일 지우기: {old}")
            (target / old).unlink()

    if getattr(sys, "frozen", False):
        job.status(f"제거 프로그램 복사: {UNINSTALLER_NAME}")
        uninstaller = target / UNINSTALLER_NAME
        if Path(sys.executable).resolve() != uninstaller.resolve():
            shutil.copy2(sys.executable, uninstaller)
        uninstall_cmd = f'"{uninstaller}" /uninstall'
    else:
        uninstall_cmd = f'"{sys.executable}" "{Path(__file__).resolve()}" /uninstall'
        job.log("  (개발 모드: 제거 프로그램 대신 이 스크립트를 등록)")
    job.progress(85)

    for want, link in ((start_menu, start_menu_link()), (desktop, desktop_link())):
        if want:
            job.status(f"바로가기 만들기: {link}")
            make_shortcut(link, exe)
        elif link.exists():
            job.status(f"바로가기 지우기: {link}")
            link.unlink()
    job.progress(92)

    job.status("Windows 앱 목록에 등록")
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, UNINSTALL_KEY) as k:
        winreg.SetValueEx(k, "DisplayName", 0, winreg.REG_SZ, APP_NAME)
        winreg.SetValueEx(k, "DisplayVersion", 0, winreg.REG_SZ, VERSION)
        winreg.SetValueEx(k, "Publisher", 0, winreg.REG_SZ, PUBLISHER)
        winreg.SetValueEx(k, "URLInfoAbout", 0, winreg.REG_SZ, URL)
        winreg.SetValueEx(k, "InstallLocation", 0, winreg.REG_SZ, str(target))
        winreg.SetValueEx(k, "DisplayIcon", 0, winreg.REG_SZ, f"{exe},0")
        winreg.SetValueEx(k, "UninstallString", 0, winreg.REG_SZ, uninstall_cmd)
        winreg.SetValueEx(k, "QuietUninstallString", 0, winreg.REG_SZ, uninstall_cmd)
        winreg.SetValueEx(k, "NoModify", 0, winreg.REG_DWORD, 1)
        winreg.SetValueEx(k, "NoRepair", 0, winreg.REG_DWORD, 1)
        winreg.SetValueEx(k, "EstimatedSize", 0, winreg.REG_DWORD, total // 1024)
        winreg.SetValueEx(k, "InstallDate", 0, winreg.REG_SZ, time.strftime("%Y%m%d"))

    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as k:
        for old in OLD_RUN_VALUES:
            try:
                winreg.DeleteValue(k, old)
            except FileNotFoundError:
                pass
        if autorun:
            job.status("Windows 시작 시 자동 실행 켜기")
            winreg.SetValueEx(k, RUN_VALUE, 0, winreg.REG_SZ, f'"{exe}"')
        else:
            try:
                winreg.DeleteValue(k, RUN_VALUE)
                job.status("Windows 시작 시 자동 실행 끄기")
            except FileNotFoundError:
                pass
    job.progress(100)
    job.status("설치를 마쳤어요.")


def do_uninstall(job: Job, target: Path, remove_config: bool) -> None:
    job.status(f"실행 중인 {APP_NAME} 종료")
    stop_app()
    job.progress(15)

    for link in (start_menu_link(), desktop_link()):
        if link.exists():
            job.status(f"바로가기 지우기: {link}")
            link.unlink()
    job.progress(35)

    job.status("Windows 시작 시 자동 실행 끄기")
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_SET_VALUE) as k:
            for name in (RUN_VALUE, *OLD_RUN_VALUES):
                try:
                    winreg.DeleteValue(k, name)
                except FileNotFoundError:
                    pass
    except FileNotFoundError:
        pass

    job.status("Windows 앱 목록에서 지우기")
    try:
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, UNINSTALL_KEY)
    except FileNotFoundError:
        pass
    job.progress(55)

    for name in (EXE_NAME, *OLD_EXE_NAMES):
        exe = target / name
        if exe.exists():
            job.status(f"파일 지우기: {exe}")
            exe.unlink()
    remove_start_traces(job, target)
    job.progress(70)

    # 지금 실행 중인 uninstall.exe는 지울 수 없지만 옮길 수는 있다. TEMP로 빼낸 뒤 설치 폴더를 바로 지운다.
    global _moved_uninstaller
    me = Path(sys.executable).resolve()
    if getattr(sys, "frozen", False) and me.parent == target.resolve():
        dest = Path(os.environ.get("TEMP", str(Path.home()))) / f"{APP_ID}-uninstall-{os.getpid()}.exe"
        try:
            os.replace(me, dest)
            _moved_uninstaller = dest
        except OSError as e:
            job.log(f"  제거 프로그램을 옮기지 못했어요: {e}")
    for f in (target / UNINSTALLER_NAME,):
        try:
            f.unlink()
        except OSError:
            pass
    try:
        target.rmdir()  # 빈 폴더만 지운다. 사용자가 넣어 둔 다른 파일이 있으면 폴더는 남긴다.
        job.status(f"설치 폴더 지우기: {target}")
    except FileNotFoundError:
        pass
    except OSError:
        job.log(f"  설치 폴더에 다른 파일이 있어 폴더는 남겨 둡니다: {target}")
    job.progress(80)

    if remove_config:
        for d in config_dirs():
            if d.exists():
                job.status(f"카드 설정 지우기: {d}")
                shutil.rmtree(d, ignore_errors=True)
    job.progress(90)

    job.progress(100)
    job.status("제거를 마쳤어요.")


_moved_uninstaller: Path | None = None


def remove_start_traces(job: Job | None, target: Path) -> None:
    r"""
    시작 메뉴가 이 앱에 대해 남기는 기록을 지운다.
    - Start\TileProperties\W~<설치 경로>...: 바로가기마다 만드는 타일 기록
    - AppListBackup\ListOfEventDrivenBackedUpTiles_*: 앱이 사라질 때 핀 복원용으로 남기는 백업.
      다른 앱 기록이 섞여 있을 수 있으므로, 목록의 타일이 전부 이 앱 것일 때만 지운다.
    Windows가 바로가기 삭제를 조금 늦게 알아채고 다시 쓰기도 해서, 제거 중과 [마침] 때 두 번 부른다.
    """
    def say(text: str) -> None:
        if job:
            job.status(text)

    needle = str(target).lower().rstrip("\\")

    base = r"Software\Microsoft\Windows\CurrentVersion\Start\TileProperties"
    for name in enum_keys(base):
        if needle in name.lower():
            say(f"시작 메뉴 타일 기록 지우기: {name}")
            delete_key_tree(base + "\\" + name)

    backup = r"Software\Microsoft\Windows\CurrentVersion\AppListBackup"
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, backup, 0, winreg.KEY_READ | winreg.KEY_SET_VALUE) as k:
            doomed = []
            i = 0
            while True:
                try:
                    name, value, _kind = winreg.EnumValue(k, i)
                except OSError:
                    break
                i += 1
                if name.startswith("ListOfEventDrivenBackedUpTiles") and only_our_tiles(value, needle):
                    doomed.append(name)
            for name in doomed:
                say(f"시작 메뉴 타일 백업 지우기: {name}")
                try:
                    winreg.DeleteValue(k, name)
                except OSError:
                    pass
    except OSError:
        pass


def only_our_tiles(value: object, needle: str) -> bool:
    if isinstance(value, bytes):
        value = value.decode("utf-16-le", errors="ignore").rstrip("\x00")
    if not isinstance(value, str):
        return False
    try:
        tiles = json.loads(value)
    except ValueError:
        return False
    if isinstance(tiles, dict):
        tiles = [tiles]
    if not isinstance(tiles, list) or not tiles:
        return False
    ids = [t.get("tileId", "") if isinstance(t, dict) else "" for t in tiles]
    return all(needle in str(i).lower() for i in ids)


def enum_keys(path: str) -> list[str]:
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path) as k:
            names = []
            i = 0
            while True:
                try:
                    names.append(winreg.EnumKey(k, i))
                except OSError:
                    return names
                i += 1
    except OSError:
        return []


def delete_key_tree(path: str) -> None:
    for sub in enum_keys(path):
        delete_key_tree(path + "\\" + sub)
    try:
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, path)
    except OSError:
        pass


def schedule_cleanup() -> None:
    """TEMP로 옮겨 둔 제거 프로그램을 창이 닫힌 뒤 지운다(실행 중에는 지울 수 없어서)."""
    if _moved_uninstaller is None:
        return
    f = _moved_uninstaller
    # timeout은 콘솔 없는 프로세스에서 바로 끝나 버리므로 ping으로 기다린다. 몇 번 다시 시도한다.
    cmd = " & ".join([f'ping -n 3 127.0.0.1 >nul & del /f /q "{f}" >nul 2>&1'] * 5)
    # DETACHED_PROCESS로 띄우면 cmd에 콘솔이 없어 그 안의 ping이 새 콘솔 창을 연다.
    # 숨긴 콘솔(CREATE_NO_WINDOW)을 주면 ping도 그 콘솔을 물려받아 아무 창도 뜨지 않는다.
    # 제거 프로그램을 부른 쪽(작업 개체)이 끝날 때 같이 죽지 않도록 가능하면 빠져나온다.
    for flags in (CREATE_NO_WINDOW | CREATE_BREAKAWAY_FROM_JOB, CREATE_NO_WINDOW):
        try:
            # 목록으로 넘기면 안쪽 따옴표가 \"로 바뀌어 cmd가 못 알아듣는다. 명령줄을 통째로 넘긴다.
            subprocess.Popen(f'cmd /s /c "{cmd}"', creationflags=flags, close_fds=True, cwd=str(f.parent),
                             stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            return
        except OSError:
            continue


# ----- 화면 -----

BANNER_TOP = "#0A2F66"
BANNER_BOTTOM = "#0067C0"
BANNER_TEXT = "#FFFFFF"
BANNER_DIM = "#9FC2EA"
TILE_COLORS = ["#4CC2FF", "#FFB900", "#6CCB5F", "#FF6F61"]
PAGE_BG = "#FFFFFF"
BAR_BG = "#F0F0F0"


class Wizard(tk.Tk):
    def __init__(self, uninstall: bool) -> None:
        super().__init__()
        self.uninstall = uninstall
        self.s = self.winfo_fpixels("1i") / 96  # 화면 배율(125% = 1.25)
        self.old_dir, self.old_version = installed_info()

        if uninstall:
            self.steps = ["시작", "제거", "완료"]
            self.pages = [self.page_uninstall_welcome, self.page_progress, self.page_finish]
            here = Path(sys.executable).resolve().parent if getattr(sys, "frozen", False) else None
            self.target = self.old_dir or here or default_install_dir()
        else:
            self.steps = ["시작", "설치 위치", "추가 작업", "설치 준비", "설치", "완료"]
            self.pages = [self.page_welcome, self.page_location, self.page_options,
                          self.page_ready, self.page_progress, self.page_finish]
            self.target = self.old_dir or default_install_dir()

        self.dir_var = tk.StringVar(value=str(self.target))
        self.start_menu_var = tk.BooleanVar(value=True)
        self.desktop_var = tk.BooleanVar(value=desktop_link().exists())
        self.autorun_var = tk.BooleanVar(value=True)
        self.launch_var = tk.BooleanVar(value=True)
        self.remove_config_var = tk.BooleanVar(value=False)
        self.index = 0
        self.busy = False
        self.failed: str | None = None

        self.title(f"{APP_NAME} {'제거' if uninstall else '설치'}")
        self.resizable(False, False)
        try:
            self.iconbitmap(default=str(resource("app.ico")))
        except tk.TclError:
            pass
        self.configure(bg=BAR_BG)
        self.setup_fonts_and_styles()
        self.build_frame()
        self.protocol("WM_DELETE_WINDOW", self.on_cancel)
        self.bind("<Return>", lambda _e: self.on_next() if str(self.next_btn["state"]) != "disabled" else None)
        self.bind("<Escape>", lambda _e: self.on_cancel())
        self.show(0)
        self.center()

    def px(self, v: float) -> int:
        return int(round(v * self.s))

    # --- 뼈대 ---

    def setup_fonts_and_styles(self) -> None:
        family = "Malgun Gothic"
        self.f_body = tkfont.Font(family=family, size=9)
        self.f_title = tkfont.Font(family=family, size=13, weight="bold")
        self.f_head = tkfont.Font(family=family, size=10, weight="bold")
        self.f_banner = tkfont.Font(family="Segoe UI Semibold", size=16)
        self.f_step = tkfont.Font(family=family, size=9)
        self.f_step_on = tkfont.Font(family=family, size=9, weight="bold")
        self.f_small = tkfont.Font(family=family, size=8)

        st = ttk.Style(self)
        try:
            st.theme_use("vista")
        except tk.TclError:
            pass
        st.configure(".", font=self.f_body)
        st.configure("Page.TFrame", background=PAGE_BG)
        st.configure("Page.TLabel", background=PAGE_BG)
        st.configure("Page.TCheckbutton", background=PAGE_BG)
        st.configure("Bar.TFrame", background=BAR_BG)
        st.configure("Bar.TLabel", background=BAR_BG, foreground="#8A8A8A", font=self.f_small)
        st.configure("TButton", padding=(self.px(10), self.px(2)))

    def build_frame(self) -> None:
        w, h = self.px(660), self.px(440)
        bar_h = self.px(58)
        self.geometry(f"{w}x{h}")

        top = tk.Frame(self, bg=PAGE_BG)
        top.place(x=0, y=0, width=w, height=h - bar_h)

        self.banner_w = self.px(190)
        self.banner = tk.Canvas(top, width=self.banner_w, height=h - bar_h, highlightthickness=0, bd=0)
        self.banner.pack(side="left", fill="y")

        self.body = ttk.Frame(top, style="Page.TFrame", padding=(self.px(26), self.px(22), self.px(24), self.px(12)))
        self.body.pack(side="left", fill="both", expand=True)

        # 아래 막대: 옛날 설치 프로그램처럼 이름이 새겨진 구분선 + 버튼들
        bar = ttk.Frame(self, style="Bar.TFrame")
        bar.place(x=0, y=h - bar_h, width=w, height=bar_h)
        brand = f"{APP_NAME} {VERSION}"
        line_y = self.px(9)
        line_x = self.px(10) + self.f_small.measure(brand) + self.px(6)
        tk.Frame(bar, bg="#C8C8C8", height=1).place(x=line_x, y=line_y, width=w - line_x - self.px(10))
        tk.Frame(bar, bg="#FFFFFF", height=1).place(x=line_x, y=line_y + 1, width=w - line_x - self.px(10))
        ttk.Label(bar, text=brand, style="Bar.TLabel").place(x=self.px(8), y=line_y, anchor="w")

        btns = ttk.Frame(bar, style="Bar.TFrame")
        btns.place(relx=1, rely=0.58, anchor="e", x=-self.px(12))
        self.back_btn = ttk.Button(btns, text="< 뒤로", command=self.on_back)
        self.next_btn = ttk.Button(btns, text="다음 >", command=self.on_next)
        self.cancel_btn = ttk.Button(btns, text="취소", command=self.on_cancel)
        self.back_btn.pack(side="left")
        self.next_btn.pack(side="left", padx=(self.px(2), self.px(10)))
        self.cancel_btn.pack(side="left")

    def center(self) -> None:
        self.update_idletasks()
        w, h = self.winfo_width(), self.winfo_height()
        x = (self.winfo_screenwidth() - w) // 2
        y = (self.winfo_screenheight() - h) // 3
        self.geometry(f"+{x}+{y}")

    # --- 왼쪽 배너 ---

    def draw_banner(self) -> None:
        c = self.banner
        c.delete("all")
        w, h = self.banner_w, int(c["height"])

        # 세로 그러데이션
        top = tuple(int(BANNER_TOP[i:i + 2], 16) for i in (1, 3, 5))
        bot = tuple(int(BANNER_BOTTOM[i:i + 2], 16) for i in (1, 3, 5))
        bands = 90
        for i in range(bands):
            t = i / (bands - 1)
            col = "#%02x%02x%02x" % tuple(int(a + (b - a) * t) for a, b in zip(top, bot))
            y0, y1 = h * i / bands, h * (i + 1) / bands + 1
            c.create_rectangle(0, y0, w, y1, fill=col, outline="")

        # 아래쪽 장식: 반쯤 잘린 큰 타일들
        big = self.px(62)
        gap = self.px(10)
        for i, col in enumerate(TILE_COLORS):
            x = w - self.px(40) + (i % 2) * (big + gap) - big
            y = h - self.px(40) + (i // 2) * (big + gap) - big
            self.round_rect(c, x, y, x + big, y + big, self.px(14), fill=self.mix(col, BANNER_BOTTOM, 0.72))

        # 앱 아이콘 + 이름
        pad = self.px(22)
        cell, g = self.px(15), self.px(3)
        for i, col in enumerate(TILE_COLORS):
            x = pad + (i % 2) * (cell + g)
            y = pad + (i // 2) * (cell + g)
            self.round_rect(c, x, y, x + cell, y + cell, self.px(4), fill=col)
        c.create_text(pad + 2 * cell + g + self.px(10), pad + cell + g / 2, text=APP_NAME, anchor="w",
                      fill=BANNER_TEXT, font=self.f_banner)
        c.create_text(pad, pad + 2 * cell + g + self.px(12), anchor="nw", fill=BANNER_DIM, font=self.f_small,
                      text=f"버전 {VERSION}" + ("  ·  제거" if self.uninstall else ""))

        # 설치 단계 목록
        y = pad + self.px(88)
        c.create_line(pad, y - self.px(14), w - pad, y - self.px(14), fill=self.mix("#FFFFFF", BANNER_TOP, 0.8))
        for i, name in enumerate(self.steps):
            done, now = i < self.index, i == self.index
            dot_col = BANNER_TEXT if (done or now) else BANNER_DIM
            r = self.px(4)
            cx, cy = pad + r, y + self.px(9)
            if done:
                c.create_text(cx, cy, text="✓", fill=BANNER_TEXT, font=self.f_step_on)
            elif now:
                c.create_oval(cx - r, cy - r, cx + r, cy + r, fill=BANNER_TEXT, outline="")
            else:
                c.create_oval(cx - r, cy - r, cx + r, cy + r, outline=dot_col, width=max(1, self.px(1)))
            if now:
                c.create_rectangle(pad - self.px(10), y - self.px(3), pad - self.px(7), y + self.px(21),
                                   fill=TILE_COLORS[0], outline="")
            c.create_text(pad + self.px(18), cy, text=name, anchor="w",
                          fill=BANNER_TEXT if (done or now) else BANNER_DIM,
                          font=self.f_step_on if now else self.f_step)
            y += self.px(30)

    @staticmethod
    def mix(a: str, b: str, t: float) -> str:
        ca = [int(a[i:i + 2], 16) for i in (1, 3, 5)]
        cb = [int(b[i:i + 2], 16) for i in (1, 3, 5)]
        return "#%02x%02x%02x" % tuple(int(x + (y - x) * t) for x, y in zip(ca, cb))

    @staticmethod
    def round_rect(c: tk.Canvas, x0: float, y0: float, x1: float, y1: float, r: float, **kw) -> None:
        pts = [x0 + r, y0, x1 - r, y0, x1, y0, x1, y0 + r, x1, y1 - r, x1, y1,
               x1 - r, y1, x0 + r, y1, x0, y1, x0, y1 - r, x0, y0 + r, x0, y0]
        c.create_polygon(pts, smooth=True, outline="", **kw)

    # --- 페이지 전환 ---

    def show(self, index: int) -> None:
        self.index = index
        for child in self.body.winfo_children():
            child.destroy()
        self.back_btn.configure(state="normal" if 0 < index else "disabled", text="< 뒤로")
        self.next_btn.configure(state="normal", text="다음 >")
        self.cancel_btn.configure(state="normal")
        self.pages[index]()
        self.draw_banner()
        self.next_btn.focus_set()

    def on_back(self) -> None:
        if self.index > 0 and not self.busy:
            self.show(self.index - 1)

    def on_next(self) -> None:
        if self.busy:
            return
        page = self.pages[self.index]
        if page == self.page_location and not self.validate_location():
            return
        if page == self.page_finish:
            self.finish()
            return
        self.show(self.index + 1)

    def on_cancel(self) -> None:
        if self.busy:
            return
        if self.pages[self.index] == self.page_finish:
            self.finish()
            return
        what = "제거" if self.uninstall else "설치"
        if messagebox.askyesno(self.title(), f"{what}를 취소할까요?", parent=self):
            self.destroy()

    # --- 페이지 공통 ---

    def heading(self, title: str, sub: str | None = None) -> None:
        ttk.Label(self.body, text=title, style="Page.TLabel", font=self.f_title).pack(anchor="w")
        if sub:
            ttk.Label(self.body, text=sub, style="Page.TLabel", foreground="#555555").pack(anchor="w", pady=(self.px(4), 0))
        ttk.Frame(self.body, style="Page.TFrame", height=self.px(16)).pack()

    def para(self, text: str, **kw) -> ttk.Label:
        lbl = ttk.Label(self.body, text=text, style="Page.TLabel", wraplength=self.px(400), justify="left", **kw)
        lbl.pack(anchor="w", pady=(0, self.px(10)))
        return lbl

    # --- 설치 페이지들 ---

    def page_welcome(self) -> None:
        self.heading(f"{APP_NAME} 설치를 시작합니다")
        self.para("Windows 11 시작 메뉴의 '범주' 폴더처럼, 바탕화면에 바로가기를 폴더 카드로 묶어 두는 앱입니다.")
        self.para("카드를 누르면 시작 메뉴처럼 펼쳐지고, 앱을 카드 위로 끌어다 놓으면 그 카드에 들어갑니다.")
        if self.old_version:
            self.para(f"이미 설치된 버전({self.old_version})이 있어요. 새 버전({VERSION})으로 업데이트합니다.\n"
                      "만들어 둔 그룹과 카드 위치·크기는 그대로 남아요.", foreground="#0A5DB8")
        if app_running():
            self.para(f"지금 실행 중인 {APP_NAME}는 설치하는 동안 잠시 종료됩니다.", foreground="#8A5A00")
        self.para("계속하려면 [다음]을 누르세요.")

    def page_location(self) -> None:
        self.heading("설치 위치 선택", f"{APP_NAME}를 설치할 폴더를 고르세요.")
        self.para("아래 폴더에 설치합니다. 다른 폴더에 설치하려면 [찾아보기]를 누르세요.")
        row = ttk.Frame(self.body, style="Page.TFrame")
        row.pack(fill="x", pady=(0, self.px(14)))
        entry = ttk.Entry(row, textvariable=self.dir_var)
        entry.pack(side="left", fill="x", expand=True, ipady=self.px(2))
        ttk.Button(row, text="찾아보기...", command=self.browse).pack(side="left", padx=(self.px(8), 0))

        need = PAYLOAD_EXE.stat().st_size if PAYLOAD_EXE.exists() else 0
        self.space_lbl = ttk.Label(self.body, style="Page.TLabel", foreground="#555555")
        self.space_lbl.pack(anchor="w")
        def refresh(*_a: object) -> None:
            free = free_space(Path(self.dir_var.get() or "C:\\"))
            text = f"필요한 공간: {human_size(need)}"
            if free is not None:
                text += f"\n사용 가능한 공간: {human_size(free)}"
            self.space_lbl.configure(text=text)
        refresh()
        trace = self.dir_var.trace_add("write", refresh)
        self.space_lbl.bind("<Destroy>", lambda _e: self.dir_var.trace_remove("write", trace))

        ttk.Frame(self.body, style="Page.TFrame", height=self.px(14)).pack()
        self.para("관리자 권한 없이 지금 사용자 계정에만 설치됩니다.", foreground="#777777")

    def browse(self) -> None:
        start = Path(self.dir_var.get())
        while not start.exists() and start.parent != start:
            start = start.parent
        chosen = filedialog.askdirectory(parent=self, initialdir=str(start), title="설치할 폴더 선택")
        if chosen:
            p = Path(chosen)
            # 옛날 설치 프로그램처럼, 고른 폴더 아래에 앱 이름 폴더를 만든다.
            if p.name.lower() != APP_NAME.lower():
                p = p / APP_NAME
            self.dir_var.set(str(p))

    def validate_location(self) -> bool:
        raw = self.dir_var.get().strip().strip('"')
        p = Path(raw)
        if not raw or not p.is_absolute() or not p.drive:
            messagebox.showwarning(self.title(), "설치할 폴더를 전체 경로로 적어 주세요.\n예: C:\\Programs\\Desk Cards", parent=self)
            return False
        if p.exists() and not p.is_dir():
            messagebox.showwarning(self.title(), "같은 이름의 파일이 있어요. 다른 폴더를 골라 주세요.", parent=self)
            return False
        if p.is_dir():
            others = [c.name for c in p.iterdir() if c.name.lower() not in {n.lower() for n in (EXE_NAME, UNINSTALLER_NAME, *OLD_EXE_NAMES)}]
            if others and not messagebox.askyesno(
                    self.title(), f"이 폴더에 이미 다른 파일이 있어요.\n{p}\n\n그래도 여기에 설치할까요?", parent=self):
                return False
        need = PAYLOAD_EXE.stat().st_size if PAYLOAD_EXE.exists() else 0
        free = free_space(p)
        if free is not None and free < need * 1.1:
            messagebox.showwarning(self.title(), "디스크 공간이 부족해요.", parent=self)
            return False
        self.target = p
        self.dir_var.set(str(p))
        return True

    def page_options(self) -> None:
        self.heading("추가 작업 선택", "설치하면서 함께 할 일을 고르세요.")
        for text, var in (("시작 메뉴에 바로가기 만들기", self.start_menu_var),
                          ("바탕화면에 바로가기 만들기", self.desktop_var),
                          ("Windows를 시작할 때 자동으로 실행", self.autorun_var)):
            ttk.Checkbutton(self.body, text=text, variable=var, style="Page.TCheckbutton").pack(anchor="w", pady=self.px(3))
        ttk.Frame(self.body, style="Page.TFrame", height=self.px(14)).pack()
        self.para("카드는 바탕화면에 바로 떠 있으므로 바탕화면 바로가기는 없어도 괜찮아요.\n"
                  "자동 실행은 나중에 트레이 아이콘 메뉴에서도 켜고 끌 수 있어요.", foreground="#777777")

    def page_ready(self) -> None:
        self.heading("설치 준비 완료", "아래 내용으로 설치합니다.")
        tasks = [t for t, v in (("시작 메뉴 바로가기", self.start_menu_var), ("바탕화면 바로가기", self.desktop_var),
                                ("Windows 시작 시 자동 실행", self.autorun_var)) if v.get()]
        lines = [
            "설치 위치:",
            f"    {self.target}",
            "",
            "추가 작업:",
            *(f"    {t}" for t in (tasks or ["없음"])),
            "",
            "그룹 폴더 (카드 내용이 실제로 저장되는 곳):",
            f"    {groups_dir()}",
        ]
        if self.old_version:
            lines += ["", f"업데이트: {self.old_version} → {VERSION}"]
        box = tk.Text(self.body, height=11, wrap="none", font=self.f_body, relief="solid", bd=1,
                      highlightthickness=0, bg="#FBFBFB", padx=self.px(8), pady=self.px(6))
        box.insert("1.0", "\n".join(lines))
        box.configure(state="disabled")
        box.pack(fill="both", expand=True)
        ttk.Frame(self.body, style="Page.TFrame", height=self.px(8)).pack()
        self.para("[설치]를 누르면 설치를 시작합니다.")
        self.next_btn.configure(text="설치")

    # --- 제거 페이지 ---

    def page_uninstall_welcome(self) -> None:
        self.heading(f"{APP_NAME} 제거", "컴퓨터에서 앱을 지웁니다.")
        self.para(f"다음 위치에 설치된 {APP_NAME}를 제거합니다.\n    {self.target}")
        self.para(f"그룹 폴더({groups_dir()})와 그 안의 바로가기는 지우지 않아요.\n"
                  "필요 없으면 제거한 뒤 직접 지우세요.", foreground="#0A5DB8")
        ttk.Checkbutton(self.body, text="카드 위치·크기 설정도 지우기", variable=self.remove_config_var,
                        style="Page.TCheckbutton").pack(anchor="w", pady=(self.px(4), self.px(10)))
        self.para("계속하려면 [제거]를 누르세요.")
        self.next_btn.configure(text="제거")

    # --- 진행 / 완료 ---

    def page_progress(self) -> None:
        what = "제거" if self.uninstall else "설치"
        self.heading(f"{what} 중", f"{APP_NAME}를 {what}하는 동안 잠시 기다려 주세요.")
        self.status_lbl = ttk.Label(self.body, text="준비 중...", style="Page.TLabel")
        self.status_lbl.pack(anchor="w", pady=(0, self.px(6)))
        self.bar = ttk.Progressbar(self.body, mode="determinate", maximum=100)
        self.bar.pack(fill="x", ipady=self.px(1))
        ttk.Frame(self.body, style="Page.TFrame", height=self.px(12)).pack()

        self.log_box = tk.Text(self.body, height=9, wrap="none", font=self.f_small, relief="solid", bd=1,
                               highlightthickness=0, bg="#FBFBFB", padx=self.px(6), pady=self.px(4))
        self.details_btn = ttk.Button(self.body, text="세부 정보 보기", command=self.toggle_details)
        self.details_btn.pack(anchor="w")

        self.back_btn.configure(state="disabled")
        self.next_btn.configure(state="disabled")
        self.cancel_btn.configure(state="disabled")
        self.busy = True
        self.job = Job()
        if self.uninstall:
            args = (self.job, self.target, self.remove_config_var.get())
            fn = do_uninstall
        else:
            args = (self.job, self.target, self.start_menu_var.get(), self.desktop_var.get(), self.autorun_var.get())
            fn = do_install

        def run() -> None:
            try:
                fn(*args)
                self.job.q.put(("done", None))
            except Exception as e:  # noqa: BLE001 — 화면에 보여 주고 멈춘다
                self.job.q.put(("error", str(e)))

        threading.Thread(target=run, daemon=True).start()
        self.after(50, self.pump)

    def toggle_details(self) -> None:
        if self.log_box.winfo_ismapped():
            self.log_box.pack_forget()
            self.details_btn.configure(text="세부 정보 보기")
        else:
            self.log_box.pack(fill="both", expand=True, before=self.details_btn, pady=(0, self.px(8)))
            self.details_btn.configure(text="세부 정보 숨기기")

    def pump(self) -> None:
        try:
            while True:
                kind, val = self.job.q.get_nowait()
                if kind == "status":
                    self.status_lbl.configure(text=str(val))
                elif kind == "log":
                    self.log_box.insert("end", f"{val}\n")
                    self.log_box.see("end")
                elif kind == "progress":
                    self.bar["value"] = float(val)  # type: ignore[arg-type]
                elif kind == "done":
                    self.busy = False
                    self.after(400, lambda: self.show(self.index + 1))
                    return
                elif kind == "error":
                    self.busy = False
                    self.failed = str(val)
                    self.log_box.insert("end", f"오류: {val}\n")
                    if not self.log_box.winfo_ismapped():
                        self.toggle_details()
                    what = "제거" if self.uninstall else "설치"
                    self.status_lbl.configure(text=f"{what}하지 못했어요.", foreground="#C42B1C")
                    messagebox.showerror(self.title(), f"{what} 중에 문제가 생겼어요.\n\n{val}", parent=self)
                    self.cancel_btn.configure(state="normal", text="닫기")
                    self.cancel_btn.configure(command=self.destroy)
                    return
        except queue.Empty:
            pass
        self.after(50, self.pump)

    def page_finish(self) -> None:
        if self.uninstall:
            self.heading(f"{APP_NAME} 제거 완료")
            self.para(f"{APP_NAME}를 컴퓨터에서 제거했어요.")
            self.para(f"그룹 폴더는 남아 있어요:\n    {groups_dir()}", foreground="#555555")
        else:
            self.heading(f"{APP_NAME} 설치 완료")
            self.para(f"{APP_NAME}를 설치했어요.")
            self.para("바탕화면 오른쪽 위에 카드가 나타나요. 앱을 카드 위로 끌어다 놓아 보세요.\n"
                      "카드를 누르고 오른쪽 위 ⚙를 누르면 설정이 열려요.")
            ttk.Checkbutton(self.body, text=f"{APP_NAME} 실행하기", variable=self.launch_var,
                            style="Page.TCheckbutton").pack(anchor="w", pady=(self.px(6), 0))
        self.back_btn.configure(state="disabled")
        self.cancel_btn.configure(state="disabled")
        self.next_btn.configure(text="마침")

    def finish(self) -> None:
        if self.uninstall:
            # 제거하는 사이 시작 메뉴가 다시 써 둔 기록이 있으면 한 번 더 지운다.
            remove_start_traces(None, self.target)
            schedule_cleanup()
        elif self.launch_var.get():
            exe = self.target / EXE_NAME
            subprocess.Popen([str(exe)], cwd=str(self.target), creationflags=DETACHED_PROCESS, close_fds=True)
        self.destroy()


def main() -> None:
    set_dpi_awareness()
    uninstall = any(a.lower() in ("/uninstall", "--uninstall", "-uninstall") for a in sys.argv[1:])
    if not uninstall and not PAYLOAD_EXE.exists():
        root = tk.Tk()
        root.withdraw()
        messagebox.showerror(f"{APP_NAME} 설치", f"설치할 파일이 없어요:\n{PAYLOAD_EXE}\n\nbuild.ps1로 먼저 빌드하세요.")
        return
    Wizard(uninstall).mainloop()


if __name__ == "__main__":
    main()
