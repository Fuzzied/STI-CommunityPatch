# -*- coding: utf-8 -*-
r"""Install a release zip into a fake game folder, the way a player would,
and check what landed.

Run it in PowerShell. With no arguments it checks both zips in build\:

    python "C:\My Downloads\Claude Projects\SpaceTravelidle modding\tools\check_release_zip.py"

It never touches the real game. Since 0.1.1 (no Python, Fuzzied 29.09.2026)
the install is the zip itself, so the check does exactly what a player does:

  Windows: unzip into a fake game folder that still holds 0.1's changed data
  file and its backup, then run "Undo 0.1 game data.bat" from there, twice.

  Mac: unzip, then run "Install Community Patch.command" with Git Bash
  against a fake Mac Steam library in a fake home folder, with 0.1's backup
  in the fake .app. No Mac tools there (defaults, xattr, codesign, pbcopy),
  so those steps fall through the way they would on a Mac missing them.

Then, on the folder that came out: every file the patch should put in the
game folder is there and is the build in plugin\ right now, the patch list
inside DataPatches.dll is the current one, the Balancer settings carry every
value, the 0.1 data came out, and the readme, notes, licences and credits
travel with it.

A lesson from 0.1 kept here: every check passed on zips that had lost the
Big Bang card names, because nothing ran the zip's own installer. This runs
it.
"""
import hashlib
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import zipfile

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(SCRIPT_DIR)
BUILD = os.path.join(ROOT, "build")
sys.path.insert(0, SCRIPT_DIR)

import package_common as pc  # noqa: E402

m = pc.installer()

# Stand-ins for resources.assets, so the undo can be told apart from a copy
# that did nothing.
COMMUNITY = b"community version data, untouched"
PATCHED_BY_01 = b"community version data, changed by 0.1"

GIT_BASH = r"C:\Program Files\Git\bin\bash.exe"


def md5(path):
    with open(path, "rb") as fh:
        return hashlib.md5(fh.read()).hexdigest()


def read(path):
    with io.open(path, encoding="utf-8", errors="replace") as fh:
        return fh.read()


class Report(object):
    def __init__(self):
        self.bad = 0

    def line(self, label, ok, text):
        print("%-16s %s%s" % (label, "" if ok else "PROBLEM: ", text))
        if not ok:
            self.bad += 1


def install_windows(zip_path, work, r):
    game = os.path.join(work, "Space Travel Idle")
    data = os.path.join(game, "SpaceTravelIdle_Data")
    os.makedirs(data)
    open(os.path.join(game, "SpaceTravelIdle.exe"), "wb").close()
    with open(os.path.join(data, "resources.assets"), "wb") as fh:
        fh.write(PATCHED_BY_01)
    with open(os.path.join(data, "resources.assets.backup-original"), "wb") as fh:
        fh.write(COMMUNITY)
    with zipfile.ZipFile(zip_path) as zf:
        zf.extractall(game)

    bat = os.path.join(game, "Undo 0.1 game data.bat")

    def run(path):
        with open(os.devnull, "rb") as nul:
            p = subprocess.run(["cmd", "/c", path], stdin=nul, capture_output=True)
        return p.stdout.decode("cp850", "replace")

    # The .bat refuses while any SpaceTravelIdle.exe runs, which on this
    # machine is often. Then that refusal is checked for real, and the undo
    # itself runs from a copy that looks for a process that never exists.
    running = b"SpaceTravelIdle.exe" in subprocess.run(
        ["tasklist", "/fi", "imagename eq SpaceTravelIdle.exe"], capture_output=True).stdout
    if running:
        said = run(bat)
        r.line("game running", "The game is running" in said,
               "the game is open on this machine and the .bat refused, as it should"
               if "The game is running" in said else "the .bat did not refuse: " + said.strip()[:200])
        with open(bat, "rb") as fh:
            text = fh.read()
        bat = os.path.join(game, "undo-test-copy.bat")
        with open(bat, "wb") as fh:
            fh.write(text.replace(b"SpaceTravelIdle.exe", b"NoSuchProcessForTheCheck.exe"))
    outs = [run(bat), run(bat)]
    if running:
        os.remove(bat)
    with open(os.path.join(data, "resources.assets"), "rb") as fh:
        now = fh.read()
    backup_gone = not os.path.exists(os.path.join(data, "resources.assets.backup-original"))
    r.line("undo 0.1", now == COMMUNITY and backup_gone and "Done." in outs[0],
           "first run put the community data back and removed the backup"
           if now == COMMUNITY and backup_gone else "first run said: " + outs[0].strip()[:200])
    r.line("undo 0.1 again", "Nothing to undo" in outs[1],
           "second run found nothing to undo" if "Nothing to undo" in outs[1]
           else "second run said: " + outs[1].strip()[:200])
    return game


def install_mac(zip_path, work, r):
    home = os.path.join(work, "home")
    game = os.path.join(home, "Library", "Application Support", "Steam", "steamapps",
                        "common", "Space Travel Idle")
    data = os.path.join(game, "Space Travel Idle.app", "Contents", "Resources", "Data")
    os.makedirs(data)
    os.makedirs(os.path.join(home, "Desktop"))
    with open(os.path.join(data, "resources.assets"), "wb") as fh:
        fh.write(PATCHED_BY_01)
    with open(os.path.join(data, "resources.assets.backup-original"), "wb") as fh:
        fh.write(COMMUNITY)
    unz = os.path.join(work, "unzipped")
    with zipfile.ZipFile(zip_path) as zf:
        zf.extractall(unz)
        modes = {i.filename: (i.external_attr >> 16) & 0o777 for i in zf.infolist()}
    for name in ("Install Community Patch.command", "files/run_bepinex.sh"):
        r.line("executable bit", modes.get(name) == 0o755,
               "%s is %s" % (name, oct(modes.get(name) or 0)))

    if not os.path.isfile(GIT_BASH):
        r.line("install script", False, "no Git Bash at " + GIT_BASH + ", could not run it")
        return game
    env = dict(os.environ, HOME=home)
    with open(os.devnull, "rb") as nul:
        p = subprocess.run([GIT_BASH, os.path.join(unz, "Install Community Patch.command")],
                           stdin=nul, capture_output=True, env=env)
    out = p.stdout.decode("utf-8", "replace")
    report = os.path.join(home, "Desktop", "space-travel-idle-install-report.txt")
    r.line("install script", p.returncode == 0 and "ONE STEP LEFT" in out,
           "ran to the end, found the game by itself" if p.returncode == 0
           else "exit %d: %s" % (p.returncode, (out + p.stderr.decode("utf-8", "replace"))[-400:]))
    r.line("report", os.path.isfile(report), "written to the Desktop" if os.path.isfile(report)
           else "not on the Desktop")
    with open(os.path.join(data, "resources.assets"), "rb") as fh:
        now = fh.read()
    backup_gone = not os.path.exists(os.path.join(data, "resources.assets.backup-original"))
    r.line("undo 0.1", now == COMMUNITY and backup_gone,
           "put the community data back and removed the backup" if now == COMMUNITY and backup_gone
           else "0.1 data still in place")
    script = os.path.join(game, "run_bepinex.sh")
    if os.path.isfile(script):
        text = read(script)
        r.line("launcher", 'executable_name="Space Travel Idle.app"' in text and "\r" not in text,
               "run_bepinex.sh starts Space Travel Idle.app, LF endings")
    else:
        r.line("launcher", False, "run_bepinex.sh missing")
    r.line("launch option", '/run_bepinex.sh" %command%' in out,
           "printed" if '/run_bepinex.sh" %command%' in out else "not printed")
    return game


def check_game_folder(game, platform, r):
    loader = ["winhttp.dll", "doorstop_config.ini"] if platform == "Windows" \
        else ["run_bepinex.sh", "libdoorstop.dylib"]
    missing = [n for n in loader + ["BepInEx/core/BepInEx.dll"]
               if not os.path.isfile(os.path.join(game, n))]
    r.line("loader", not missing, ", ".join(loader) + " and BepInEx core" if not missing
           else "missing " + ", ".join(missing))

    # Everything game_folder_files promises, as the packer saw it.
    payload = "payload" if platform == "Windows" else "payload-macos"
    expected = list(pc.game_folder_files(m, payload))
    gone, stale = [], []
    for disk, name in expected:
        landed = os.path.join(game, name)
        if not os.path.isfile(landed):
            gone.append(name)
        elif name.endswith(".dll") and md5(landed) != md5(disk):
            stale.append(name)
    r.line("files", not gone, "%d of %d in place" % (len(expected) - len(gone), len(expected))
           + ("" if not gone else ", missing " + ", ".join(gone[:8])))
    dlls = [n for _d, n in expected if n.endswith(".dll") and "/plugins/" in n or "/patchers/" in n]
    r.line("plugin bytes", not stale, "%d plugin and patcher DLLs match plugin\\ now" % len(dlls)
           if not stale else "differ from plugin\\, repack: " + ", ".join(stale))

    # The patch list inside DataPatches.dll is embedded as it is, so the
    # current JSON's bytes must be in the DLL.
    dp = os.path.join(game, "BepInEx", "plugins", pc.DATA_PLUGIN)
    js = os.path.join(ROOT, "plugin", "community_patches.json")
    if os.path.isfile(dp):
        with open(dp, "rb") as fh:
            blob = fh.read()
        with open(js, "rb") as fh:
            want = fh.read()
        doc = json.loads(want.decode("utf-8"))
        r.line("patch list", want in blob,
               "DataPatches.dll carries the current list, Community Patch %s build %s, %d patches"
               % (doc["version"], doc["build"], len(doc["patches"]))
               if want in blob else "DataPatches.dll carries an older list, run export_patches.py and rebuild")
        r.line("version", doc["version"] == m.PUBLIC_VERSION,
               "%s, the same as setup_mod.PUBLIC_VERSION" % doc["version"])

    cfg = os.path.join(game, "BepInEx", "config", "STIU.cfg")
    spec = next(p["cfg_values"] for p in m.CODE_PATCHES if p.get("cfg_values"))
    if os.path.isfile(cfg):
        text = read(cfg)
        lost = [n for _s, n, v in spec["values"] if "%s = %s" % (n, v) not in text]
        r.line("Balancer cfg", not lost, "%d of %d values" % (len(spec["values"]) - len(lost),
                                                              len(spec["values"])))
    else:
        r.line("Balancer cfg", False, "STIU.cfg missing")

    # Windows unzips the docs folder into the game folder too; a Mac keeps it
    # in the unzipped download and copies only files/.
    py = sorted(os.path.relpath(os.path.join(d, f), game).replace(os.sep, "/")
                for d, _, files in os.walk(game) for f in files if f.endswith(".py"))
    want = [pc.DOCS_FOLDER + "/tools/repair_priority_order.py"] if platform == "Windows" else []
    r.line("python files", py == want, ("only the repair tool" if want else "none")
           if py == want else ", ".join(py))


def check_text(zip_path, r):
    with zipfile.ZipFile(zip_path) as zf:
        names = zf.namelist()
        tops = sorted({n.split("/")[0] for n in names})
        readmes = [n for n in tops if n.startswith("READ ME")]
        r.line("readme", len(readmes) == 1, ", ".join(readmes) or "MISSING from the top of the zip")
        docs = [pc.DOCS_FOLDER + "/" + n for n in ("PATCH_NOTES.txt", "LICENCE.txt",
                                                  "THIRD PARTY LICENCES.txt")]
        gone = [d for d in docs if d not in names]
        r.line("notes, licences", not gone, "in " + pc.DOCS_FOLDER if not gone
               else "missing " + ", ".join(gone))
        # A player opens these in Notepad or TextEdit (27.09.2026: text for
        # the community is .txt, never .md).
        md = [n for n in names if n.lower().endswith(".md")]
        r.line("markdown files", not md, "none" if not md else ", ".join(md))
        texts = [zf.read(n).decode("utf-8", "replace") for n in readmes + docs[:1] if n in names]
    gone = pc.missing_credits(*texts)
    r.line("credits", not gone, "%d of %d named" % (len(pc.CREDITS) - len(gone), len(pc.CREDITS))
           + ("" if not gone else ", missing " + ", ".join(gone)))
    dashes = sum(t.count(u"\u2014") + t.count(u"\u2013") for t in texts)
    r.line("dashes", dashes == 0, "no em or en dashes in the player text" if not dashes
           else "%d em or en dash(es) in the player text" % dashes)


def check(zip_path):
    print("=" * 70)
    print(os.path.basename(zip_path))
    print("=" * 70)
    r = Report()
    with zipfile.ZipFile(zip_path) as zf:
        names = zf.namelist()
    platform = "Mac" if "Install Community Patch.command" in names else "Windows"
    print("%-16s %s, %d file(s)" % ("platform", platform, len(names)))
    work = tempfile.mkdtemp(prefix="stizip-")
    try:
        if platform == "Windows":
            game = install_windows(zip_path, work, r)
        else:
            game = install_mac(zip_path, work, r)
        check_game_folder(game, platform, r)
        check_text(zip_path, r)
    finally:
        shutil.rmtree(work, ignore_errors=True)
    print()
    print("PASSED" if r.bad == 0 else "%d PROBLEM(S)" % r.bad)
    print()
    return r.bad


def main():
    zips = sys.argv[1:]
    if not zips:
        zips = sorted(os.path.join(BUILD, n) for n in os.listdir(BUILD)
                      if n.endswith(".zip") and ("-%s-" % m.PUBLIC_VERSION) in n)
    if not zips:
        print("no %s zips in %s. Build them first." % (m.PUBLIC_VERSION, BUILD))
        return 1
    total = sum(check(z) for z in zips)
    print("%d zip(s) checked, %d problem(s) in all" % (len(zips), total))
    return 1 if total else 0


if __name__ == "__main__":
    sys.exit(main())
