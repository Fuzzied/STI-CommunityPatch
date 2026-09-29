// Space Travel Idle community mod - the on/off switch every plugin obeys
//
// 0.1 had a Python setup with a menu, and a patch you turned off was simply
// not copied into the game. 0.1.1 has no setup on Windows: the whole patch is
// unzipped into the game folder, every DLL is always there, and each one asks
// this file whether it is wanted. Fuzzied picked that on 29.09.2026: everything
// on out of the box, one settings file to turn things off, and the same
// switches move into the game's Settings panel in 0.2.
//
// The file is BepInEx\config\STI Community Patch.cfg. DataPatches.cs writes
// it, with a line and a description for every patch. This class only reads
// it, by hand rather than through a BepInEx ConfigFile, because two
// ConfigFile objects on one path would each save their own copy over the
// other's.
//
// A missing file or a missing line means on. That is right only because
// every patch is on by default; community_patches.json says so per patch and
// DataPatches.cs refuses to start if one ever is not, so this cannot go
// quietly wrong.
//
// Compiled into every plugin, the same way CommunitySettings.cs is: BepInEx
// gives each plugin its own assembly, so each gets its own copy. Nothing here
// keeps state another copy needs.

using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;

internal static class CommunityToggle
{
    internal const string FileName = "STI Community Patch.cfg";

    private static string[] _lines;

    internal static string FilePath
    {
        get { return Path.Combine(Paths.ConfigPath, FileName); }
    }

    /// <summary>
    /// True unless the settings file sets this key to false, and says so in
    /// the log when it is off. A plugin that gets false sets enabled = false
    /// before it returns, so its Update and OnGUI never run on state its
    /// Awake never built. That line lives at the call site because the
    /// early window patcher compiles this file too, without Unity.
    /// </summary>
    internal static bool On(string key, ManualLogSource log)
    {
        if (IsOn(key))
        {
            return true;
        }
        if (log != null)
        {
            log.LogInfo("Off in " + FileName + " (" + key + " = false), nothing patched");
        }
        return false;
    }

    internal static bool IsOn(string key)
    {
        if (_lines == null)
        {
            try
            {
                _lines = File.Exists(FilePath) ? File.ReadAllLines(FilePath) : new string[0];
            }
            catch (Exception)
            {
                _lines = new string[0];
            }
        }
        foreach (string raw in _lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == '[')
            {
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0 || line.Substring(0, eq).Trim() != key)
            {
                continue;
            }
            return !string.Equals(line.Substring(eq + 1).Trim(), "false",
                                  StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }
}
