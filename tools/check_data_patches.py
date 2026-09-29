# -*- coding: utf-8 -*-
r"""Prove the plugin makes the same game data as the 0.1 setup did.

0.1.1 moves the game data changes from setup_mod.py (Python, run once, writes
resources.assets) into DataPatches.dll (C#, runs in memory at every start).
The C# is a port. This check runs both on the same untouched tables and
compares the results byte for byte, so the port cannot drift quietly.

Two ways to feed it the C# side:

  offline (default): builds a small test program that loads DataPatches.dll
  and calls its own reader and patch code on the tables. No game launch needed.

  --dump FOLDER: compares against BepInEx\community_dump, which the plugin
  writes in the real game when DumpTables = true. That also covers what the
  offline run cannot: Unity's own text loading and its JSON reader.

Run it in PowerShell:

    python "C:\My Downloads\Claude Projects\SpaceTravelidle modding\tools\check_data_patches.py"

It needs the untouched community version data. By default it reads
resources.assets.backup-original in the Steam game folder (0.1 made that
copy); pass --assets to point at another untouched resources.assets.
"""
from __future__ import print_function

import argparse
import io
import json
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
PLUGIN = os.path.join(ROOT, "plugin")
GAME = r"C:\Games\Steam\steamapps\common\Space Travel Idle"
CSC = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

sys.path.insert(0, os.path.join(ROOT, "installer"))
import setup_mod as m  # noqa: E402


def read_tables(assets, names):
    import UnityPy
    env = UnityPy.load(assets)
    out = {}
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        data = obj.read()
        if data.m_Name in names:
            text = data.m_Script
            if isinstance(text, bytes):
                text = text.decode("utf-8", "surrogateescape")
            out[data.m_Name] = text
    return out


def python_side(originals):
    """setup_mod.patch_game's loop, minus the writing."""
    wanted = {}
    for p in m.PATCHES:
        wanted.setdefault(p["asset"], []).append(p)
        for sub in p.get("also", []):
            wanted.setdefault(sub["asset"], []).append(dict(sub, key=p["key"], title=None))
    out = {}
    for name, patches in wanted.items():
        text = originals[name].replace("\r\n", "\n")
        for p in patches:
            text = m.apply_ops(text, p)
        out[name] = text
    return out


HARNESS = r'''
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

static class Harness
{
    static int Main(string[] args)
    {
        string dll = args[0], managed = args[1], bepcore = args[2], json = args[3], dir = args[4];
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string n = new AssemblyName(e.Name).Name + ".dll";
            foreach (string d in new[] { managed, bepcore })
            {
                string p = Path.Combine(d, n);
                if (File.Exists(p)) return Assembly.LoadFrom(p);
            }
            return null;
        };
        Assembly asm = Assembly.LoadFrom(dll);
        Type plugin = asm.GetType("DataPatchesPlugin");
        Type fileType = asm.GetType("CommunityPatchFile");
        MethodInfo apply = plugin.GetMethod("ApplyOps", BindingFlags.NonPublic | BindingFlags.Static);
        // the plugin's own reader, the one the game runs (JsonUtility failed
        // in the game while .NET's reader passed here, 29.09.2026)
        MethodInfo parse = plugin.GetMethod("ParsePatchFile", BindingFlags.NonPublic | BindingFlags.Static);
        object file;
        try { file = parse.Invoke(null, new object[] { File.ReadAllText(json, Encoding.UTF8) }); }
        catch (TargetInvocationException e) { Console.WriteLine("FAILED reading the patch list: " + e.InnerException.Message); return 1; }
        var patches = (System.Collections.IList)fileType.GetField("patches").GetValue(file);
        var order = new List<string>();
        var byTable = new Dictionary<string, List<object>>();
        foreach (object p in patches)
        {
            if ((string)p.GetType().GetField("kind").GetValue(p) != "data") continue;
            foreach (object t in (System.Collections.IList)p.GetType().GetField("tables").GetValue(p))
            {
                string asset = (string)t.GetType().GetField("asset").GetValue(t);
                if (!byTable.ContainsKey(asset)) { byTable[asset] = new List<object>(); order.Add(asset); }
                byTable[asset].Add(t);
            }
        }
        var utf8 = new UTF8Encoding(false);
        foreach (string name in order)
        {
            string text = File.ReadAllText(Path.Combine(dir, name + ".original.txt"), utf8).Replace("\r\n", "\n");
            foreach (object t in byTable[name])
            {
                try { text = (string)apply.Invoke(null, new object[] { text, t, name }); }
                catch (TargetInvocationException e) { Console.WriteLine("FAILED " + name + ": " + e.InnerException.Message); return 1; }
            }
            File.WriteAllText(Path.Combine(dir, name + ".patched.txt"), text, utf8);
            Console.WriteLine("c# patched " + name);
        }
        return 0;
    }
}
'''


def csharp_side(originals, work, game):
    for name, text in originals.items():
        with io.open(os.path.join(work, name + ".original.txt"), "w", encoding="utf-8",
                     errors="surrogateescape", newline="") as fh:
            fh.write(text)
    src = os.path.join(work, "Harness.cs")
    exe = os.path.join(work, "Harness.exe")
    with io.open(src, "w", encoding="utf-8") as fh:
        fh.write(HARNESS)
    subprocess.check_call([CSC, "/nologo", "/out:" + exe, src])
    managed = os.path.join(game, "SpaceTravelIdle_Data", "Managed")
    bepcore = os.path.join(ROOT, "bepinex", "payload", "BepInEx", "core")
    if not os.path.isdir(bepcore):
        bepcore = os.path.join(game, "BepInEx", "core")
    rc = subprocess.call([exe, os.path.join(PLUGIN, "DataPatches.dll"), managed, bepcore,
                          os.path.join(PLUGIN, "community_patches.json"), work])
    if rc != 0:
        raise SystemExit("FAIL: the C# side refused, see above")
    return read_folder(work, originals)


def read_folder(folder, names):
    out = {}
    for name in names:
        path = os.path.join(folder, name + ".patched.txt")
        if os.path.isfile(path):
            with io.open(path, encoding="utf-8", errors="surrogateescape", newline="") as fh:
                out[name] = fh.read()
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=GAME, help="the folder holding SpaceTravelIdle.exe")
    ap.add_argument("--assets", help="an untouched resources.assets (default: the game's resources.assets.backup-original)")
    ap.add_argument("--dump", help="BepInEx\\community_dump from a real game run")
    args = ap.parse_args()
    if not args.assets:
        args.assets = os.path.join(args.game, "SpaceTravelIdle_Data", "resources.assets.backup-original")

    names = set()
    for p in m.PATCHES:
        names.add(p["asset"])
        for sub in p.get("also", []):
            names.add(sub["asset"])
    if not os.path.isfile(args.assets):
        raise SystemExit("NOT FOUND: " + args.assets)
    originals = read_tables(args.assets, names)
    missing = sorted(names - set(originals))
    if missing:
        raise SystemExit("FAIL: tables not in %s: %s" % (args.assets, ", ".join(missing)))

    py = python_side(originals)
    if args.dump:
        cs = read_folder(args.dump, names)
        # the game's own untouched text must match what UnityPy read too
        game_orig = {}
        for name in names:
            path = os.path.join(args.dump, name + ".original.txt")
            if os.path.isfile(path):
                with io.open(path, encoding="utf-8", errors="surrogateescape", newline="") as fh:
                    game_orig[name] = fh.read()
        for name in sorted(names):
            same = game_orig.get(name) == originals[name]
            print("original %-28s %s" % (name, "same" if same else "DIFFERENT"))
    else:
        cs = csharp_side(originals, tempfile.mkdtemp(prefix="sti_check_"), args.game)

    bad = 0
    for name in sorted(names):
        a, b = py[name], cs.get(name)
        if b is None:
            print("patched  %-28s MISSING from the C# side" % name); bad += 1
        elif a == b:
            print("patched  %-28s same, %d chars, %d changed vs original"
                  % (name, len(a), 0 if a == originals[name] else 1))
        else:
            i = next((k for k in range(min(len(a), len(b))) if a[k] != b[k]), min(len(a), len(b)))
            print("patched  %-28s DIFFERENT at char %d\n    python: %r\n    c#:     %r"
                  % (name, i, a[max(0, i - 60):i + 60], b[max(0, i - 60):i + 60]))
            bad += 1
    if bad:
        raise SystemExit("FAIL: %d table(s) differ" % bad)
    print("PASS: all %d tables byte for byte the same" % len(names))


if __name__ == "__main__":
    main()
