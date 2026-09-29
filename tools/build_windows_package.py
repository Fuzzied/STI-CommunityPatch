"""Build the Windows release zip for the community patch.

Run it from anywhere; it works out its own paths:

    python "C:\\My Downloads\\Claude Projects\\SpaceTravelidle modding\\tools\\build_windows_package.py"

Since 0.1.1 there is no setup (Fuzzied, 29.09.2026: "That that as a version
0.1.1", and "Unzip into game"). The zip's layout is the game folder's: the
player unzips it into the folder that holds SpaceTravelIdle.exe and starts
the game. So the top of the zip is winhttp.dll and the BepInEx folder, with
the readme beside them and the rest of the text in a folder of its own.

The file list is package_common.game_folder_files, shared with the Mac zip.
The .bat and the .txt files get CRLF so they open properly everywhere.
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
    # names off before "windows" or "mac" showed (28.09.2026, "rename them
    # to STI-").
    "STI-CommunityPatch-%s-Windows.zip" % m.PUBLIC_VERSION)

CRLF = (".bat", ".txt")

TOP = [
    (os.path.join("installer", "READ ME FIRST (Windows).txt"), "READ ME FIRST (Windows).txt"),
    # Only for someone coming from 0.1, which wrote its changes into the
    # game's data file. After the unzip it sits in the game folder and finds
    # the data by its own location.
    (os.path.join("installer", "Undo 0.1 game data.bat"), "Undo 0.1 game data.bat"),
]


def wanted_files():
    for disk, name in TOP:
        yield os.path.join(pc.ROOT, disk), name
    for entry in itertools.chain(pc.docs_files(), pc.game_folder_files(m, "payload")):
        yield entry


def main():
    code, entries = pc.write_zip(OUT, wanted_files(), crlf=CRLF)
    if code:
        return code
    print()
    print("at the top of the zip:")
    for top in sorted({name.split("/")[0] for _full, name in entries}):
        print("  " + top)
    return 0


if __name__ == "__main__":
    sys.exit(main())
