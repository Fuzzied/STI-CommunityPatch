"""Build the Mac release zip for the community patch.

Run it from anywhere; it works out its own paths:

    python "C:\\My Downloads\\Claude Projects\\SpaceTravelidle modding\\tools\\build_mac_package.py"

Since 0.1.1 there is no Python (Fuzzied, 29.09.2026). The files that go into
the game folder sit under files/, the same list the Windows zip unzips
straight into the game (package_common.game_folder_files). "Install
Community Patch.command" copies them in and does the four things a Mac
needs on top: find the game, name the .app in run_bepinex.sh, clear the
quarantine, and hand over the Launch Options line.

The .command and .sh files get the executable bit and LF line endings,
because a shell script with CRLF fails on macOS with a confusing "bad
interpreter" error.
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
    # Short, with the platform in plain sight (28.09.2026, "rename them to STI-").
    "STI-CommunityPatch-%s-Mac.zip" % m.PUBLIC_VERSION)

EXECUTABLE = (".command", ".sh")

TOP = [
    (os.path.join("installer", "READ ME FIRST (Mac).txt"), "READ ME FIRST (Mac).txt"),
    (os.path.join("installer", "Install Community Patch.command"), "Install Community Patch.command"),
]


def wanted_files():
    for disk, name in TOP:
        yield os.path.join(pc.ROOT, disk), name
    for entry in itertools.chain(pc.docs_files(),
                                 pc.game_folder_files(m, "payload-macos", prefix="files/")):
        yield entry


def main():
    code, entries = pc.write_zip(OUT, wanted_files(), executable=EXECUTABLE)
    if code:
        return code
    print()
    print("marked executable:")
    for _full, name in entries:
        if name.endswith(EXECUTABLE):
            print("  " + name)
    return 0


if __name__ == "__main__":
    sys.exit(main())
