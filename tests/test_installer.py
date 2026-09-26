import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch, MagicMock
import base64

spec = importlib.util.spec_from_file_location("deskcards_installer", Path(__file__).parents[1] / "installer" / "installer.py")
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)


class InstallerTests(unittest.TestCase):
    target = r"c:\programs\desk cards"

    def tiles(self, *ids):
        return json.dumps([{"tileId": value} for value in ids])

    def test_own_tile(self):
        self.assertTrue(installer.only_our_tiles(self.tiles(r"W~C:\Programs\Desk Cards\DeskCards.exe"), self.target))

    def test_similarly_named_directory_is_preserved(self):
        self.assertFalse(installer.only_our_tiles(self.tiles(r"W~C:\Programs\Desk Cards Other\Other.exe"), self.target))

    def test_other_application_in_install_folder_is_preserved(self):
        self.assertFalse(installer.only_our_tiles(self.tiles(r"W~C:\Programs\Desk Cards\Other.exe"), self.target))

    def test_mixed_backup_is_preserved(self):
        self.assertFalse(installer.only_our_tiles(self.tiles(r"W~C:\Programs\Desk Cards\DeskCards.exe", r"W~C:\Windows\notepad.exe"), self.target))

    def test_empty_target_is_never_matched(self):
        self.assertFalse(installer.only_our_tiles(self.tiles("something"), ""))

    def test_version_file_with_powershell_bom(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "version.txt"
            path.write_text("0.1.0", encoding="utf-8-sig")
            with patch.object(installer, "resource", return_value=path):
                self.assertEqual(installer.read_version(), "0.1.0")

    def test_stop_timeout_is_reported(self):
        with patch.object(installer, "app_running", return_value=True), \
             patch.object(installer.subprocess, "run"), patch.object(installer.time, "sleep"):
            with self.assertRaises(RuntimeError):
                installer.stop_app()

    def test_missing_payload_does_not_stop_running_app(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch.object(installer, "PAYLOAD_EXE", Path(directory) / "missing.exe"), \
                 patch.object(installer, "stop_app") as stop:
                with self.assertRaises(FileNotFoundError):
                    installer.do_install(installer.Job(), Path(directory), False, False, False)
                stop.assert_not_called()

    def test_uninstall_failure_keeps_registration_for_retry(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory)
            (target / installer.EXE_NAME).write_bytes(b"locked application")
            with patch.object(installer, "stop_app"), \
                 patch.object(installer, "start_menu_link", return_value=target / "absent-menu.lnk"), \
                 patch.object(installer, "desktop_link", return_value=target / "absent-desktop.lnk"), \
                 patch.object(installer.winreg, "OpenKey", return_value=MagicMock()), \
                 patch.object(installer.winreg, "DeleteValue"), \
                 patch.object(installer.winreg, "DeleteKey") as delete_key, \
                 patch.object(Path, "unlink", side_effect=PermissionError("locked")):
                with self.assertRaises(PermissionError):
                    installer.do_uninstall(installer.Job(), target, False)
                delete_key.assert_not_called()

    def test_cleanup_treats_special_characters_as_a_literal_path(self):
        path = Path(r"C:\Temp\percent% & quote'\DeskCards-uninstall-123.exe")
        with patch.object(installer, "_moved_uninstaller", path), \
             patch.object(installer.subprocess, "Popen") as popen:
            installer.schedule_cleanup()
            argv = popen.call_args.args[0]
            self.assertIsInstance(argv, list)
            self.assertEqual(argv[0], "powershell")
            script = base64.b64decode(argv[-1]).decode("utf-16-le")
            self.assertIn("-LiteralPath $cleanupFile", script)
            self.assertIn(str(path).replace("'", "''"), script)


if __name__ == "__main__":
    unittest.main()
