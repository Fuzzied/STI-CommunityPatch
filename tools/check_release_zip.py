# -*- coding: utf-8 -*-
"""Unpack a release zip somewhere clean and check it could actually install.

Run it from anywhere; it works out its own paths. With no arguments it checks
both zips in build\\:

    python "C:\\My Downloads\\Claude Projects\\SpaceTravelidle modding\\tools\\check_release_zip.py"

It does NOT run the installer and it never touches the game. It unpacks each
zip to a scratch folder, imports setup_mod.py out of the unpacked copy, and
asks it where it thinks everything is.

That is the check worth having, because setup_mod.py works out its own root by
looking for a bepinex folder beside itself. A zip that nests everything one
level deeper, or that forgets a plugin the menu offers, looks perfectly fine
in a file listing and fails the moment a player double clicks it.

One wrinkle: setup_mod.py picks its payload folder from the machine it is
running on, so a Mac zip checked on Windows would look as though its loader
were missing. This checks the payload the zip actually carries and says which
platform it is for.
"""
import hashlib
import os
import shutil
import sys
import tempfile
import warnings
import zipfile

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(SCRIPT_DIR)
BUILD = os.path.join(ROOT, "build")

PAYLOADS = {"payload": "Windows", "payload-macos": "Mac"}


def check(zip_path):
    """Returns the number of problems found. Prints as it goes."""
    print("=" * 70)
    print(os.path.basename(zip_path))
    print("=" * 70)

    work = tempfile.mkdtemp(prefix="stizip-")
    try:
        with zipfile.ZipFile(zip_path) as zf:
            names = zf.namelist()
            zf.extractall(work)

        # Import setup_mod.py as it sits in the unpacked zip, not the project
        # copy, so the layout being checked is the one a player gets.
        sys.modules.pop("setup_mod", None)
        sys.path.insert(0, work)
        try:
            # Any warning Python prints while reading the installer lands at
            # the top of the player's setup window. On 26.09.2026 a backslash
            # in a comment did exactly that, and every check here still
            # passed, so warnings count as problems now.
            with warnings.catch_warnings(record=True) as heard:
                warnings.simplefilter("always")
                import setup_mod as m
        finally:
            sys.path.remove(work)

        print("unpacked         %d file(s)" % len(names))
        print("version          Community Patch %s, build %s"
              % (m.PUBLIC_VERSION, m.BUILD_VERSION))

        bad = 0

        heard = [w for w in heard if not issubclass(w.category, ResourceWarning)]
        for w in heard:
            print("warning          %s line %s: %s"
                  % (os.path.basename(w.filename), w.lineno, w.message))
        bad += len(heard)

        # The loader. Which platform's payload is in here is the zip's
        # business, not this machine's.
        present = [name for name in PAYLOADS
                   if os.path.isdir(os.path.join(work, "bepinex", name))]
        if len(present) == 1:
            print("loader           %s (bepinex/%s)"
                  % (PAYLOADS[present[0]], present[0]))
        elif not present:
            print("loader           MISSING, no bepinex payload at all")
            bad += 1
        else:
            print("loader           BOTH payloads present, which is wrong for "
                  "a release zip")
            bad += 1

        for label, path in (("plugin folder", m.PLUGIN_DIR),
                            ("assets folder", m.ASSETS_DIR)):
            if os.path.isdir(path):
                continue
            print("%-16s MISSING at %s" % (label, path))
            bad += 1

        if m.ROOT_DIR != work:
            print("root             WRONG, setup_mod.py resolved %s"
                  % m.ROOT_DIR)
            bad += 1

        # Every plugin the menu can offer has to be in the zip, and every
        # patcher that comes with one.
        dlls = [dll for p in m.CODE_PATCHES for dll, _ in m.patch_dlls(p)]
        # A menu line can be a setting inside another patch's plugin, with no
        # DLL of its own, so plugins are counted, not menu lines.
        plugins = [p for p in m.CODE_PATCHES if p.get("plugin")]
        missing = [dll for dll in dlls
                   if not os.path.isfile(os.path.join(m.PLUGIN_DIR, dll))]
        print("code patches     %d offered, %d DLL(s) missing%s"
              % (len(m.CODE_PATCHES), len(missing),
                 ("  " + ", ".join(missing)) if missing else ""))
        bad += len(missing)

        # And it has to be the DLL that was actually built, not one from an
        # earlier round. On 21.09.2026 the build0.9.28 zips passed every check
        # above while carrying Cargo Bay 1.3.0, because the version had been
        # bumped in setup_mod.py and the zips were never repacked. Everything
        # the checker asked was true: the file was present, named right, and
        # offered by the menu. It was simply the wrong build, and nothing here
        # was asking that question.
        #
        # Read it the right way round. This does not say "the zip is stale",
        # it says the zip did not come from the DLLs sitting in plugin\ right
        # now. C# output is not byte reproducible, so rebuilding after packing
        # trips this too, with identical source. That is still worth stopping
        # for: if the two differ you cannot say which one ships.
        differing = []
        for name in dlls:
            here = os.path.join(m.PLUGIN_DIR, name)
            built = os.path.join(ROOT, "plugin", name)
            if not os.path.isfile(here) or not os.path.isfile(built):
                continue
            with open(here, "rb") as fh:
                a = hashlib.md5(fh.read()).hexdigest()
            with open(built, "rb") as fh:
                b = hashlib.md5(fh.read()).hexdigest()
            if a != b:
                differing.append(name)
        if differing:
            print("plugin bytes     %d DLL(s) differ from plugin\\, repack "
                  "before shipping  %s"
                  % (len(differing), ", ".join(sorted(differing))))
            bad += len(differing)
        else:
            print("plugin bytes     %d DLL(s) match the built ones in "
                  "plugin\\ (%d plugins, %d patcher)"
                  % (len(dlls), len(plugins), len(dlls) - len(plugins)))

        missing = sorted({p["assets"] for p in m.CODE_PATCHES
                          if p.get("assets")
                          and not os.path.isdir(
                              os.path.join(m.ASSETS_DIR, p["assets"]))})
        if missing:
            print("assets           %d folder(s) missing  %s"
                  % (len(missing), ", ".join(missing)))
            bad += len(missing)

        # Every data file a set_asset op names has to be in the zip too.
        wanted = set()
        for patch in m.PATCHES:
            for sub in [patch] + list(patch.get("also", [])):
                for op in sub.get("ops", []):
                    if op[0] == "set_asset":
                        wanted.add(op[1])
                        wanted.add(op[2])
        # And the ones setup_mod.py reads by itself. Those fail quietly by
        # design, so the menu still opens, which is exactly why every zip up
        # to 26.09.2026 shipped without the new Big Bang card names and this
        # check passed it. So these are compared byte for byte with the
        # project copy, like the DLLs.
        # A zip packed before 26.09.2026 has no such list, but its
        # installer read the same file.
        extra = getattr(m, "EXTRA_DATA_FILES",
                        [os.path.join("data", "bigBangUpgrades_NEWLINES.json")])
        for rel in extra:
            here = os.path.join(m.ROOT_DIR, rel)
            built = os.path.join(ROOT, rel)
            if not os.path.isfile(here):
                wanted.add(rel)
                continue
            with open(here, "rb") as fh:
                a = fh.read()
            with open(built, "rb") as fh:
                b = fh.read()
            if a != b:
                print("data bytes       %s differs from the project copy, "
                      "repack before shipping" % rel.replace(os.sep, "/"))
                bad += 1
        print("new Big Bang     %d card name(s) the installer can read"
              % len(m.NEW_BIGBANG_LINES))
        if not m.NEW_BIGBANG_LINES:
            bad += 1
        missing = sorted(r for r in wanted
                         if not os.path.isfile(os.path.join(m.ROOT_DIR, r)))
        print("data patches     %d offered, %d data file(s) missing%s"
              % (len(m.PATCHES), len(missing),
                 ("  " + ", ".join(missing)) if missing else ""))
        bad += len(missing)

        # The readme and the notes are the two things a player opens first.
        # The two licence files must travel with every zip: BepInEx, HarmonyX,
        # MonoMod, Mono.Cecil and Doorstop ship inside it, and their licences
        # ask for their text to come along (28.09.2026).
        for name in ("PATCH_NOTES.txt", "LICENCE.txt",
                     "THIRD PARTY LICENCES.txt"):
            if not os.path.isfile(os.path.join(work, name)):
                print("%-16s MISSING from the top of the zip" % name)
                bad += 1
        # Fuzzied, 27.09.2026: "Anything to the community has to be in a .txt
        # format and not .md". A player opens these in Notepad or TextEdit,
        # where markdown shows as stars and hashes.
        md = sorted(os.path.relpath(os.path.join(d, f), work).replace(os.sep, "/")
                    for d, _, files in os.walk(work)
                    for f in files if f.lower().endswith(".md"))
        print("markdown files   %d%s" % (len(md), ("  " + ", ".join(md) +
              "  (community text must be .txt)") if md else ""))
        bad += len(md)
        readmes = [n for n in os.listdir(work) if n.startswith("READ ME")]
        if readmes:
            print("readme           %s" % ", ".join(sorted(readmes)))
        else:
            print("readme           MISSING from the top of the zip")
            bad += 1

        # The credits must be in the text a player opens first, the readme
        # or the patch notes. See package_common.CREDITS for why.
        sys.path.insert(0, SCRIPT_DIR)
        import package_common
        first = []
        for name in readmes + ["PATCH_NOTES.txt"]:
            p = os.path.join(work, name)
            if os.path.isfile(p):
                with open(p, encoding="utf-8", errors="replace") as fh:
                    first.append(fh.read())
        gone = package_common.missing_credits(*first)
        print("credits          %d of %d named%s"
              % (len(package_common.CREDITS) - len(gone), len(package_common.CREDITS),
                 ("  MISSING: " + ", ".join(gone)) if gone else ""))
        bad += len(gone)

        print()
        print("PASSED" if bad == 0 else "%d PROBLEM(S)" % bad)
        print()
        return bad
    finally:
        shutil.rmtree(work, ignore_errors=True)


def main():
    zips = sys.argv[1:]
    if not zips:
        zips = sorted(os.path.join(BUILD, n) for n in os.listdir(BUILD)
                      if n.endswith(".zip"))
    if not zips:
        print("no zips in %s. Build one first." % BUILD)
        return 1

    total = sum(check(z) for z in zips)
    print("%d zip(s) checked, %d problem(s) in all"
          % (len(zips), total))
    return 1 if total else 0


if __name__ == "__main__":
    sys.exit(main())
