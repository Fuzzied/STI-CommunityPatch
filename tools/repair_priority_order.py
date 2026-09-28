# -*- coding: utf-8 -*-
"""Put one star's priority order back from a save you know was good.

Written for the v1.12.0 regression: on loading, the game rebuilds a research
or infrastructure row at the BOTTOM of the priority list, and the auto start
memory recognised it by id and left it there. The arrangement that came out of
that was then written into the record, so the next launch had nothing good to
restore. v1.12.1 fixes the cause; this repairs a list that has already been
scrambled, without touching anything else in the save.

It copies one star's block out of a good ".sti.layers" record and writes it
into both the record and the priorityIdList inside the save itself. The save
is edited as text and re-encrypted, so every other byte in it is untouched -
nothing is re-serialised and nothing else can be lost.

THE GAME MUST BE CLOSED. It holds the save open and writes its own copy back
over yours on the way out.

Run it with no arguments to repair Mars in the current autosave from the
2_Save Slot backup, or pass --star / --good / --target to do another one.
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys

from Crypto.Cipher import DES
from Crypto.Util.Padding import pad, unpad

def find_save_dir(override=None):
    """Where this machine keeps the saves.

    This used to be one os.path.join with AppData/LocalLow in it, which is
    the Windows answer and only the Windows answer. The tool ships to Mac
    players too, and there it would have built a path under ~/AppData that
    cannot exist, then said "missing:" and stopped. Asking the system where
    it puts things beats writing down where it put them on one machine.

    Unity's persistentDataPath is what the game uses, so the candidates
    below are Unity's own layouts. The Mac one has two spellings because
    Unity changed it: the modern <company>/<product> and the older
    unity.<company>.<product> that some installs still carry.
    """
    if override:
        return os.path.abspath(os.path.expanduser(override))
    env = os.environ.get("STI_SAVE_DIR")
    if env:
        return os.path.abspath(os.path.expanduser(env))

    if sys.platform == "win32":
        home = os.environ.get("USERPROFILE") or os.path.expanduser("~")
        candidates = [os.path.join(home, "AppData", "LocalLow",
                                   "Ayatsuji", "SpaceTravelIdle")]
    elif sys.platform == "darwin":
        # HOME first, because expanduser on some machines prefers a Windows
        # style HOMEDRIVE/HOMEPATH pair and would hand back a path from the
        # wrong operating system entirely.
        home = os.environ.get("HOME") or os.path.expanduser("~")
        support = os.path.join(home, "Library", "Application Support")
        candidates = [os.path.join(support, "Ayatsuji", "SpaceTravelIdle"),
                      os.path.join(support,
                                   "unity.Ayatsuji.SpaceTravelIdle")]
    else:
        home = os.environ.get("HOME") or os.path.expanduser("~")
        candidates = [os.path.join(home, ".config", "unity3d",
                                   "Ayatsuji", "SpaceTravelIdle")]

    for path in candidates:
        if os.path.isdir(path):
            return path
    # Nothing found. Hand back the first candidate anyway so the error the
    # caller prints names a real path for this platform, which someone can
    # look at, rather than a path from someone else's operating system.
    return candidates[0]


def game_is_running():
    """True, False, or None when this machine could not be asked.

    The old version ran tasklist inside a try and set the answer to "" on
    any failure, so on a Mac, where tasklist does not exist, the guard came
    back clean every time. A check that cannot run must not report safe:
    the whole reason it is here is that the game writes its own copy of the
    save on the way out and would undo the repair. So the three states are
    kept apart, and the caller treats "could not ask" as a reason to stop
    rather than a reason to carry on.
    """
    try:
        if sys.platform == "win32":
            out = subprocess.check_output(
                ["tasklist", "/fi", "imagename eq SpaceTravelIdle.exe"],
                stderr=subprocess.STDOUT).decode("utf-8", "replace")
            return "SpaceTravelIdle.exe" in out
        out = subprocess.check_output(
            ["ps", "-A", "-o", "comm"],
            stderr=subprocess.STDOUT).decode("utf-8", "replace")
        # The process has no space in its name. A check for "Space Travel
        # Idle" would never match and would always say not running.
        return any("SpaceTravelIdle" in line for line in out.splitlines())
    except Exception:
        return None

KEY = "S@Y^A&JT".encode("utf-8")
IV = bytes([18, 52, 86, 120, 144, 171, 205, 239])

DEFAULT_GOOD = "2_Save Slot.sti"
DEFAULT_TARGET = "auto_AutoSave.sti"
DEFAULT_STAR = 5          # Mars


# --------------------------------------------------------------- the record

def read_ledger(path):
    """{star index: [entry, ...]} out of a .sti.layers file."""
    blocks = {}
    current = None
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            line = line.rstrip("\n")
            if not line or line[0] == "#":
                continue
            if line.startswith("star "):
                try:
                    current = int(line[5:].strip())
                except ValueError:
                    current = None
                    continue
                blocks[current] = []
            elif line.startswith("e ") and current is not None:
                blocks[current].append(line[2:])
    return blocks


def write_ledger(path, blocks, order):
    lines = ["# STI community auto start - remembered priority lists",
             "# One 'star' line per place, then its rows in order.",
             "v 1"]
    for idx in order:
        lines.append("star " + str(idx))
        for entry in blocks[idx]:
            lines.append("e " + entry)
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(lines) + "\n")


def ledger_order(path):
    """The star indices in the order the file lists them, so a rewrite keeps
    the file looking exactly like the one the mod would have written."""
    order = []
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            if line.startswith("star "):
                try:
                    order.append(int(line[5:].strip()))
                except ValueError:
                    pass
    return order


def entry_id(entry):
    """A layer carries its limits after a '|'; everything else is a bare id."""
    bar = entry.find("|")
    return entry if bar < 0 else entry[:bar]


def entry_order(good, target):
    """`target`'s rows, in `good`'s order.

    A layer is taken from `target` rather than `good`, so limits edited since
    the backup survive; a row `good` has never heard of keeps its place at the
    end of the list.
    """
    live = {}
    for entry in target:
        live.setdefault(entry_id(entry), entry)
    result = []
    used = set()
    for entry in good:
        key = entry_id(entry)
        if key in live and key not in used:
            result.append(live[key])
            used.add(key)
    for entry in target:
        key = entry_id(entry)
        if key not in used:
            result.append(entry)
            used.add(key)
    return result


# ----------------------------------------------------------------- the save

def decrypt(path):
    with open(path, "rb") as handle:
        raw = handle.read()
    return unpad(DES.new(KEY, DES.MODE_CBC, IV).decrypt(raw),
                 DES.block_size).decode("utf-8")


def encrypt(text):
    return DES.new(KEY, DES.MODE_CBC, IV).encrypt(
        pad(text.encode("utf-8"), DES.block_size))


def find_priority_array(text, marker):
    """The span of the "priorityIdList":[...] that contains `marker`.

    Found by scanning the raw text rather than by parsing, so the rest of the
    save comes back out byte for byte as it went in.
    """
    for match in re.finditer(r'"priorityIdList"\s*:\s*\[', text):
        start = match.end() - 1
        depth = 0
        i = start
        while i < len(text):
            ch = text[i]
            if ch == '"':                       # skip a string wholesale
                i += 1
                while i < len(text) and text[i] != '"':
                    i += 2 if text[i] == "\\" else 1
            elif ch == "[":
                depth += 1
            elif ch == "]":
                depth -= 1
                if depth == 0:
                    break
            i += 1
        end = i + 1
        if marker in text[start:end]:
            return start, end
    return None


# --------------------------------------------------------------------- main

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--star", type=int, default=DEFAULT_STAR,
                        help="star index to repair (Mars is 5)")
    parser.add_argument("--good", default=DEFAULT_GOOD,
                        help="save whose order is the one you want back")
    parser.add_argument("--target", default=DEFAULT_TARGET,
                        help="save to repair")
    parser.add_argument("--save-dir", default=None,
                        help="folder holding the saves, if it is not where "
                             "this machine normally puts them")
    parser.add_argument("--force", action="store_true",
                        help="repair even though the game looks like it is "
                             "running (do not)")
    args = parser.parse_args()

    save_dir = find_save_dir(args.save_dir)
    if not os.path.isdir(save_dir):
        print("no save folder at: " + save_dir)
        print("pass --save-dir with the folder that holds your .sti files.")
        return 1

    good_save = os.path.join(save_dir, args.good)
    target_save = os.path.join(save_dir, args.target)
    good_ledger = good_save + ".layers"
    target_ledger = target_save + ".layers"

    for path in (good_save, good_ledger, target_save, target_ledger):
        if not os.path.exists(path):
            print("missing: " + path)
            return 1

    running = game_is_running()
    if running and not args.force:
        print("Space Travel Idle is still running. Close the game first - it "
              "writes its own copy of the save on the way out and would put "
              "the scrambled list straight back.")
        return 1
    if running is None and not args.force:
        print("could not check whether the game is running on this machine.")
        print("close the game, then run this again with --force.")
        return 1

    good_blocks = read_ledger(good_ledger)
    if args.star not in good_blocks or not good_blocks[args.star]:
        print("the good record has nothing for star " + str(args.star))
        return 1

    target_blocks = read_ledger(target_ledger)
    order = ledger_order(target_ledger)
    if args.star not in target_blocks:
        print("the target record has nothing for star " + str(args.star))
        return 1

    # The good order decides the order; the target decides which rows exist.
    # Play carries on between the backup and the repair, so a research that
    # has finished since should not be resurrected and one that has started
    # since should not be thrown away - it keeps its own place at the end
    # until the mod learns a better one.
    wanted = entry_order(good_blocks[args.star], target_blocks[args.star])

    if target_blocks[args.star] == wanted:
        print("star " + str(args.star) + " already matches - nothing to do")
        return 0

    # A layer id is unique to this star, so it is what identifies the right
    # priorityIdList inside the save without having to trust index order.
    marker = None
    for entry in wanted:
        if entry.startswith("*acc_"):
            marker = entry.split("|")[0]
            break
    if marker is None:
        marker = wanted[0]

    text = decrypt(target_save)
    span = find_priority_array(text, marker)
    if span is None:
        print("could not find a priorityIdList containing " + marker
              + " in " + args.target)
        return 1
    start, end = span

    before = json.loads(text[start:end])
    replacement = json.dumps(wanted, separators=(",", ":"))
    patched = text[:start] + replacement + text[end:]

    shutil.copy2(target_save, target_save + ".before-repair")
    shutil.copy2(target_ledger, target_ledger + ".before-repair")

    with open(target_save, "wb") as handle:
        handle.write(encrypt(patched))
    target_blocks[args.star] = list(wanted)
    write_ledger(target_ledger, target_blocks, order)

    print("repaired star " + str(args.star) + " in " + args.target)
    print("   was " + str(len(before)) + " row(s), now "
          + str(len(wanted)) + " row(s)")
    print("   backups: " + os.path.basename(target_save) + ".before-repair"
          + " and " + os.path.basename(target_ledger) + ".before-repair")
    print("")
    print("the record it will load with - rows that have already finished are")
    print("kept as history and will not show up in the list itself:")
    for i, entry in enumerate(wanted):
        name = entry.split("|")[0]
        print("   %2d  %s" % (i + 1, name))
    return 0


if __name__ == "__main__":
    sys.exit(main())
