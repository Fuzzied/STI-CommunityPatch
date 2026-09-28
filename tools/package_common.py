# -*- coding: utf-8 -*-
"""Shared machinery for the Windows and Mac release packagers.

The two platforms differ in three things only: which BepInEx payload goes in,
which launcher and readme ship beside it, and which files have to arrive
executable. Everything else, the plugin DLLs, the asset folders and the data
files, is the same list on both, and it is read out of setup_mod.py rather
than typed here so it cannot drift away from what the installer asks for.

Nothing in here is run directly. See build_windows_package.py and
build_mac_package.py.
"""
import os
import sys
import zipfile

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(SCRIPT_DIR)

# The people whose work 0.1 stands on, by the name players know them by.
# Fuzzied, 28.09.2026: 0.1 ships "with their credited work". Izm_'s credit went
# into the Discord post only and missed every file, and the GitHub README was
# written from the files, so it missed it too. check_release_zip.py and
# build_github_repo.py refuse any player text that leaves one of these out.
CREDITS = {
    "Izm_": "shares the community version v0.35.43 the patch is made for",
    "Berserker": "made the Unlocker, which the setup installs as the Balancer",
}


def missing_credits(*texts):
    """The CREDITS names that appear in none of the given texts."""
    joined = "\n".join(texts)
    return [name for name in CREDITS if name not in joined]


def installer():
    """Import setup_mod.py from the project, wherever this script was run."""
    path = os.path.join(ROOT, "installer")
    if path not in sys.path:
        sys.path.insert(0, path)
    import setup_mod
    return setup_mod


def walk(disk_dir):
    """Every file under disk_dir, as (path on disk, name in the zip)."""
    for base, _dirs, files in os.walk(disk_dir):
        for name in sorted(files):
            full = os.path.join(base, name)
            yield full, os.path.relpath(full, ROOT).replace(os.sep, "/")


def payload_files(payload_name):
    """The BepInEx loader for one platform, whole."""
    return walk(os.path.join(ROOT, "bepinex", payload_name))


def plugin_and_asset_files(m):
    """Only the plugins the installer can offer, and the assets they read.

    A patcher ships in the same plugin/ folder as the plugins. Which BepInEx
    folder it lands in is the installer's business, at install time.
    """
    for patch in m.CODE_PATCHES:
        for dll, _folder in m.patch_dlls(patch):
            yield (os.path.join(ROOT, "plugin", dll), "plugin/" + dll)
        # A third party plugin ships with its licence, or we may not ship it.
        if patch.get("licence"):
            yield (os.path.join(ROOT, "plugin", patch["licence"]),
                   "plugin/" + patch["licence"])
        folder = patch.get("assets")
        if not folder:
            continue
        for entry in walk(os.path.join(ROOT, "assets", folder)):
            yield entry


def data_files(m):
    """Data files read at install time: the ones set_asset ops name, and the
    ones setup_mod.py reads by itself (EXTRA_DATA_FILES)."""
    for rel in m.EXTRA_DATA_FILES:
        yield (os.path.join(ROOT, rel), rel.replace(os.sep, "/"))
    for patch in m.PATCHES:
        for sub in [patch] + list(patch.get("also", [])):
            for op in sub.get("ops", []):
                if op[0] != "set_asset":
                    continue
                for rel in (op[1], op[2]):
                    yield (os.path.join(ROOT, rel),
                           rel.replace(os.sep, "/"))


def write_zip(out_path, entries, executable=(), crlf=()):
    """Write the zip, or report what is missing and write nothing.

    entries: an iterable of (path on disk, name in the zip). The first time a
    zip name is seen wins, so a caller can place a file at the top of the zip
    before the generic walkers reach it.
    executable: zip names ending in one of these get mode 0755 and LF line
    endings, because a shell script with CRLF fails on macOS with a
    confusing "bad interpreter" error.
    crlf: zip names ending in one of these get CRLF, so a .bat and a readme
    open properly in old Windows tools.

    Returns (exit code, list of (path on disk, name in the zip) written).
    """
    seen = set()
    missing = []
    chosen = []
    for full, name in entries:
        if name in seen:
            continue
        seen.add(name)
        if not os.path.isfile(full):
            missing.append(name)
            continue
        chosen.append((full, name))

    if missing:
        print("MISSING, nothing written:")
        for name in missing:
            print("  " + name)
        return 1, []

    chosen.sort(key=lambda e: e[1])
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    if os.path.exists(out_path):
        os.remove(out_path)
    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for full, name in chosen:
            data = open(full, "rb").read()
            is_exe = bool(executable) and name.endswith(tuple(executable))
            if is_exe:
                data = data.replace(b"\r\n", b"\n")
            elif crlf and name.endswith(tuple(crlf)):
                data = data.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
            info = zipfile.ZipInfo(name)
            info.compress_type = zipfile.ZIP_DEFLATED
            # The high 16 bits of external_attr are the unix mode. Without
            # this every file arrives 0644 and the launchers will not run.
            info.external_attr = (0o755 if is_exe else 0o644) << 16
            zf.writestr(info, data)

    print("wrote %s" % out_path)
    print("%d file(s), %.1f MB"
          % (len(chosen), os.path.getsize(out_path) / 1e6))
    return 0, chosen
