"""The launcher's "Stay on <version>" in the game's own update check (host/hos_update.c, Windows).

A game folder whose user\\hookshot_update_policy.txt names the version installed is not asked about an update when the
game is started without its launcher. One whose file names another version (the folder was built again since), no
version or another mode, or that has no such file, is asked as before. The builder writes the file
(builder/HookshotBuilder/VersionPolicy.cs: tests/test_launcher_versions.py has its side).

The rule, pin_holds, is compiled here from its own text and run on every OS. Its reader (pinned_here with read_value:
wide paths, Windows' own) is compiled and run on Windows, on game folders written the way the builder writes them. The
place of the check in hos_update_check is read: after the folder's version, before GitHub is asked or anything shown.

python -I -B tests/test_version_pin.py
python -I -B tests/test_version_pin.py --cc x86_64-w64-mingw32-gcc --run wine     (the reader too, from Linux)
"""
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

RUN_ALL_ARGS = ["--cc", "gcc"]

ROOT = Path(__file__).resolve().parents[1]
SOURCE = (ROOT / "host/hos_update.c").read_text(encoding="utf-8")
POLICY = (ROOT / "builder/HookshotBuilder/VersionPolicy.cs").read_text(encoding="utf-8")
CC = "gcc"
WINDOWS = os.name == "nt"
RUN = []   # (what starts a compiled fixture: nothing on its own OS)

RULE = SOURCE[SOURCE.index("static int pin_holds("):SOURCE.index("static int pinned_here(")]
READER = SOURCE[SOURCE.index("static int read_value(const wchar_t"):SOURCE.index("static char *get_body")]
PINNED = SOURCE[SOURCE.index("static int pin_holds("):SOURCE.index("/* Portable metadata permits one constant launcher")]

# mode, the file's version, the version installed: does the pin hold
RULES = [
    ("pinned", "v2.2.0", "v2.2.0", 1),
    ("pinned", "v2.2.0-rc.1", "v2.2.0-rc.1", 1),
    ("pinned", "v2.1.0-rc.1-3-g1a2b3c4", "v2.1.0-rc.1-3-g1a2b3c4", 1),
    ("pinned", "v2.2.0", "v2.3.0", 0),          # (updated since: the pin was for the version before)
    ("pinned", "v2.3.0", "v2.2.0", 0),          # (a switch that never finished would have left this)
    ("pinned", "v2.2.0", "v2.2.0-rc.1", 0),
    ("pinned", "v2.2", "v2.2.0", 0),            # (not a beginning of it)
    ("pinned", "v2.2.0", "v2.2", 0),
    ("pinned", "", "v2.2.0", 0),                # (no version line)
    ("pinned", "", "", 0),                      # (nor two empty ones for a match)
    ("", "v2.2.0", "v2.2.0", 0),                # (no mode line)
    ("latest", "v2.2.0", "v2.2.0", 0),          # (another mode: not this rule's)
    ("Pinned", "v2.2.0", "v2.2.0", 0),
]

# the file's bytes, the version installed: does the folder stay (the reader's side, on Windows)
WRITTEN = "mode=pinned\nversion=%s\n"   # (VersionPolicy.SetPinnedTag's own line: test_the_builder_writes_these_two_lines)
FILES = [
    ((WRITTEN % "v2.2.0").encode(), "v2.2.0", 1),
    ((WRITTEN % "v2.2.0").encode(), "v2.3.0", 0),
    ((WRITTEN % "v2.2.0").encode(), "v2.2.0-rc.1", 0),
    (b"\xef\xbb\xbfmode=pinned\r\nversion=v2.2.0\r\n", "v2.2.0", 1),   # (saved again by Notepad)
    (b"version=v2.2.0\nmode=pinned\n", "v2.2.0", 1),
    (b"mode=pinned \nversion=v2.2.0\t \n", "v2.2.0", 1),
    (b"mode=pinned\nversion=v2.2.0\nversion=v2.3.0\n", "v2.3.0", 0),   # (the first line of a name is the one read)
    (b"mode=pinned\nversion=\nversion=v2.2.0\n", "v2.2.0", 1),       # (a line with no value is passed over)
    (b"mode=latest\nversion=v2.2.0\n", "v2.2.0", 0),
    (b"version=v2.2.0\n", "v2.2.0", 0),
    (b"mode=pinned\n", "v2.2.0", 0),
    (b"", "v2.2.0", 0),
]


def compiled(folder, name, code, *flags):
    source = Path(folder) / (name + ".c")
    source.write_text(code, encoding="utf-8")
    exe = Path(folder) / (name + (".exe" if WINDOWS or RUN else ""))
    subprocess.run([CC, "-std=c11", "-Wall", "-Wextra", "-Werror", *flags, str(source), "-o", str(exe)], check=True)
    return str(exe)


def answer(exe, *args):
    return subprocess.run([*RUN, exe, *args], capture_output=True, text=True, check=True).stdout.split()


class Rule(unittest.TestCase):
    def test_the_pin_holds_only_for_the_version_installed(self):
        with tempfile.TemporaryDirectory(prefix="hookshot-pin-") as folder:
            exe = compiled(folder, "rule", "#include <stdio.h>\n#include <string.h>\n" + RULE + r"""
int main(int argc, char **argv) {
    if (argc != 4) return 2;
    return printf("%d\n", pin_holds(argv[1], argv[2], argv[3])), 0;
}
""")
            for mode, pinned, have, holds in RULES:
                self.assertEqual(answer(exe, mode, pinned, have), [str(holds)], (mode, pinned, have))

    def test_the_check_comes_before_github_is_asked(self):
        start = SOURCE.index("void hos_update_check(void) {", SOURCE.index("#ifdef _WIN32"))
        check = SOURCE[start:SOURCE.index("#elif defined(__linux__)")]
        order = ['read_value(file, "version", have, sizeof have)', "if (pinned_here(dir, have)) {",
                 "latest_tag(have, tag, sizeof tag)", "MessageBoxW("]
        at = [check.index(part) for part in order]
        self.assertEqual(at, sorted(at))
        # (a folder that stays leaves there, and says so in the log, where "why was I not asked" is looked up)
        stays = check[check.index("if (pinned_here(dir, have)) {"):check.index("latest_tag(have, tag, sizeof tag)")]
        self.assertIn('fprintf(stderr, "update: this folder stays on %s', stays)
        self.assertLess(stays.index("fprintf("), stays.index("return;"))
        self.assertEqual(SOURCE.count("pinned_here(dir, have)"), 1)   # (Windows only: the Linux builder has no version list)

    def test_the_builder_writes_these_two_lines(self):
        self.assertIn('File.WriteAllText(policy, $"mode=pinned\\nversion={tag}\\n");', POLICY)
        self.assertIn('Path.Combine(folder, "user", "hookshot_update_policy.txt")', POLICY)
        self.assertIn('L"%ls\\\\user\\\\hookshot_update_policy.txt"', PINNED)
        self.assertIn('read_value(file, "mode", mode, sizeof mode);', PINNED)
        self.assertIn('read_value(file, "version", pinned, sizeof pinned);', PINNED)


class Reader(unittest.TestCase):
    def test_a_game_folder_stays_when_its_file_names_its_version(self):
        if not (WINDOWS or RUN):
            self.skipTest("the reader is Windows' own (wide paths): run there, or with --cc <a mingw gcc> --run wine")
        with tempfile.TemporaryDirectory(prefix="hookshot-pin-") as folder:
            exe = compiled(folder, "reader", "#include <windows.h>\n#include <stdio.h>\n#include <string.h>\n#include <wchar.h>\n" +
                           READER + PINNED + r"""
int wmain(int argc, wchar_t **argv) {
    if (argc != 3) return 2;
    char have[128];
    if (!WideCharToMultiByte(CP_UTF8, 0, argv[2], -1, have, sizeof have, NULL, NULL)) return 3;
    return printf("%d\n", pinned_here(argv[1], have)), 0;
}
""", "-municode")
            game = Path(folder) / "Game ü, stays"   # (a folder name outside ASCII: the paths are wide)
            game.mkdir()
            self.assertEqual(answer(exe, str(game), "v2.2.0"), ["0"], "no user folder")
            (game / "user").mkdir()
            self.assertEqual(answer(exe, str(game), "v2.2.0"), ["0"], "no file")
            for text, have, stays in FILES:
                (game / "user" / "hookshot_update_policy.txt").write_bytes(text)
                self.assertEqual(answer(exe, str(game), have), [str(stays)], (text, have))


if __name__ == "__main__":
    for option in ("--cc", "--run"):   # (--run: what starts a Windows fixture built elsewhere, to try the reader there)
        if option in sys.argv:
            i = sys.argv.index(option)
            if option == "--cc":
                CC = sys.argv[i + 1]
            else:
                RUN = [sys.argv[i + 1]]
            del sys.argv[i:i + 2]
    os.environ.setdefault("PYTHONDONTWRITEBYTECODE", "1")
    unittest.main()
