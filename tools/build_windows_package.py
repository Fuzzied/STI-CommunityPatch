"""Build the Windows release zip for the community mod.

Run it from anywhere; it works out its own paths:

    python "C:\\My Downloads\\Claude Projects\\SpaceTravelidle modding\\tools\\build_windows_package.py"

What it does that a right-click-and-zip in Explorer cannot:

- ships only the plugin DLLs, asset folders and data files that setup_mod.py
  actually asks for, read out of setup_mod.py itself so the list cannot
  drift away from the installer menu;
- leaves out the whole working project: decomp, saves, docs, the other 50
  tools, the Mac payload, and every .cs source file beside the DLLs;
- forces CRLF on the .bat and the readme, so they open properly everywhere;
- puts setup_mod.py at the top of the zip, which is the layout setup_mod.py
  itself detects and expects once a player has unpacked it.

The parts shared with the Mac packager live in package_common.py.
"""
import itertools
import os
import sys

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, SCRIPT_DIR)

import package_common as pc  # noqa: E402

m = pc.installer()

OUT = os.path.join(
    pc.ROOT, "build",
    # Short, with the platform in plain sight: Discord cut the old 58 letter
    # names off before "windows" or "mac" showed. Fuzzied, 28.09.2026: "rename
    # them to STI-". The build number is inside the zip (setup_mod.py).
    "STI-CommunityPatch-%s-Windows.zip" % m.PUBLIC_VERSION)

# Text files a player may open in Notepad or run as a script.
CRLF = (".bat", ".txt")

TOP = [
    (os.path.join("installer", "setup_mod.py"), "setup_mod.py"),
    ("PATCH_NOTES.txt", "PATCH_NOTES.txt"),
    # GPL 3 with Fuzzied as the holder, and the licences of everything we
    # ship that other people made. Fuzzied, 28.09.2026: "GPL with Fuzzied".
    ("LICENCE.txt", "LICENCE.txt"),
    ("THIRD PARTY LICENCES.txt", "THIRD PARTY LICENCES.txt"),
    ("Setup Space Travel Idle Mod.bat", "Setup Space Travel Idle Mod.bat"),
    (os.path.join("installer", "READ ME FIRST (Windows).txt"),
     "READ ME FIRST (Windows).txt"),
    # The one repair job the installer cannot do, because it is the player's
    # save that needs fixing. The Windows readme explains when to reach for
    # it. It is the only tool that ships.
    (os.path.join("tools", "repair_priority_order.py"),
     "tools/repair_priority_order.py"),
]


def wanted_files():
    """Every path that goes in the zip, as (path on disk, name in the zip)."""
    for disk, name in TOP:
        yield os.path.join(pc.ROOT, disk), name
    for entry in itertools.chain(pc.payload_files("payload"),
                                 pc.plugin_and_asset_files(m),
                                 pc.data_files(m)):
        yield entry


def main():
    code, entries = pc.write_zip(OUT, wanted_files(), crlf=CRLF)
    if code:
        return code
    print()
    print("at the top of the zip:")
    for _full, name in entries:
        if "/" not in name:
            print("  " + name)
    return 0


if __name__ == "__main__":
    sys.exit(main())
