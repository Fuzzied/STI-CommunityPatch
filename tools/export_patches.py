# -*- coding: utf-8 -*-
r"""Write the patch list the Data Patches plugin carries, from setup_mod.py.

Why this exists. 0.1.1 drops Python for players (Fuzzied, 29.09.2026: "That
that as a version 0.1.1"). The game data changes move into a plugin that
makes them in memory each time the game starts. The changes themselves stay
defined in exactly one place, setup_mod.PATCHES, and this script exports
them as JSON for the plugin, so the C# side never has a hand typed copy that
could drift.

Output: plugin\community_patches.json. build_plugin.ps1 embeds it in
DataPatches.dll. The JSON is committed too, so building the plugins needs no
Python; only changing a patch does.

Run it in PowerShell after changing any patch in setup_mod.py:

    python "C:\My Downloads\Claude Projects\SpaceTravelidle modding\tools\export_patches.py"
"""
from __future__ import print_function

import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(ROOT, "plugin", "community_patches.json")

# Lines the settings file cannot switch, because they are not ours to switch:
# the Unlocker is Berserker's plugin and never reads our file. The readme
# says how to go without it.
NOT_SWITCHABLE = {"balancer", "balancer-settings"}


def op_to_json(op):
    kind = op[0]
    if kind == "replace":
        return {"kind": kind, "a": op[1], "b": op[2], "n": op[3]}
    if kind == "replace_seq":
        return {"kind": kind, "a": op[1], "list": list(op[2])}
    if kind == "set_asset":
        with open(os.path.join(ROOT, op[1]), encoding="utf-8") as fh:
            want = fh.read().strip()
        with open(os.path.join(ROOT, op[2]), encoding="utf-8") as fh:
            content = fh.read()
        return {"kind": kind, "a": want, "b": content}
    if kind == "clone_entry":
        return {"kind": kind, "a": op[1], "b": op[2]}
    if kind == "insert_entry":
        return {"kind": kind, "a": op[1], "b": op[2], "c": op[3]}
    raise SystemExit("REFUSED: unknown op %r, teach export_patches.py about it" % kind)


def table(entry):
    return {"asset": entry["asset"],
            "ops": [op_to_json(op) for op in entry["ops"]],
            "verify": [{"anchor": a, "marker": m} for a, m in entry.get("verify", [])]}


def main():
    sys.path.insert(0, os.path.join(ROOT, "installer"))
    import setup_mod as m

    out = []
    for p in m.PATCHES:
        spoiler = p["key"] in m.SAFE_TITLES
        out.append({
            "key": p["key"], "kind": "data", "defaultOn": bool(p["default"]),
            "title": m.SAFE_TITLES.get(p["key"], p["title"]),
            "desc": "" if spoiler else p["desc"],
            "tables": [table(p)] + [table(s) for s in p.get("also", [])],
        })
    for p in m.CODE_PATCHES:
        if p["key"] in NOT_SWITCHABLE:
            continue
        spoiler = p["key"] in m.SAFE_TITLES
        out.append({
            "key": p["key"], "kind": "code", "defaultOn": bool(p["default"]),
            "title": m.SAFE_TITLES.get(p["key"], p["title"]),
            "desc": "" if spoiler else p["desc"],
            "tables": [],
        })

    data = {"version": m.PUBLIC_VERSION, "build": m.BUILD_VERSION, "patches": out}
    text = json.dumps(data, ensure_ascii=True, indent=1, sort_keys=True)
    with open(OUT, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text + "\n")
    n_ops = sum(len(t["ops"]) for p in out for t in p["tables"])
    print("wrote %s: %d data patches (%d ops), %d code switches"
          % (OUT, sum(p["kind"] == "data" for p in out), n_ops,
             sum(p["kind"] == "code" for p in out)))


if __name__ == "__main__":
    main()
