"""Build the Mac release zip for the community mod.

Run it from anywhere; it works out its own paths:

    python "C:\\My Downloads\\Claude Projects\\SpaceTravelidle modding\\tools\\build_mac_package.py"

What it does that a right-click-and-zip in Explorer cannot:

- sets the executable bit on the .command and .sh files, so they run on the
  Mac instead of opening in TextEdit;
- forces LF line endings on them, because a shell script with CRLF fails on
  macOS with a confusing "bad interpreter" error;
- only includes the plugin DLLs, asset folders and data files that
  setup_mod.py actually asks for, read out of setup_mod.py itself so the
  list cannot drift.

The parts shared with the Windows packager live in package_common.py.
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
    "STI-CommunityPatch-%s-Mac.zip" % m.PUBLIC_VERSION)

# Files that must arrive on the Mac runnable rather than as text.
EXECUTABLE = (".command", ".sh")

# The installer files live in installer\ here but belong at the top of the
# zip, beside the payload folders, which is the layout the Mac scripts and
# setup_mod.py itself expect once it is unpacked.
TOP = [
    (os.path.join("installer", "setup_mod.py"), "setup_mod.py"),
    ("PATCH_NOTES.txt", "PATCH_NOTES.txt"),
    # GPL 3 with Fuzzied as the holder, and the licences of everything we
    # ship that other people made. Fuzzied, 28.09.2026: "GPL with Fuzzied".
    ("LICENCE.txt", "LICENCE.txt"),
    ("THIRD PARTY LICENCES.txt", "THIRD PARTY LICENCES.txt"),
    (os.path.join("installer", "READ ME FIRST (Mac).txt"),
     "READ ME FIRST (Mac).txt"),
    (os.path.join("installer", "Setup Space Travel Idle Mod.command"),
     "Setup Space Travel Idle Mod.command"),
    (os.path.join("installer", "Check My Mac Setup.command"),
     "Check My Mac Setup.command"),
    (os.path.join("tools", "mac_check.py"), "tools/mac_check.py"),
    # The priority list repair works on any platform now that it asks the
    # system where the saves are, and a Mac player hit by the old Auto Start
    # bug needs it exactly as much as a Windows one does.
    (os.path.join("tools", "repair_priority_order.py"),
     "tools/repair_priority_order.py"),
]


def wanted_files():
    """Every path that goes in the zip, as (path on disk, name in the zip)."""
    for disk, name in TOP:
        yield os.path.join(pc.ROOT, disk), name
    for entry in itertools.chain(pc.payload_files("payload-macos"),
                                 pc.plugin_and_asset_files(m),
                                 pc.data_files(m)):
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
