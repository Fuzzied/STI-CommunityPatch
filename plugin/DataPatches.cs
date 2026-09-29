// Space Travel Idle community mod - the game data changes, made in memory
//
// 0.1 changed the game's data tables (star map, cards, Big Bang upgrades,
// battle zones, the language files) with a Python setup that rewrote
// resources.assets on disk, using UnityPy. Players had to install Python and
// UnityPy for that, and a lot of them did not have it (Fuzzied, 29.09.2026:
// "We gravely underestimated one thing, and that is that people have to
// install python..").
//
// 0.1.1 makes exactly the same changes here instead, each time the game
// starts, and writes nothing to the game's files. Every table the game reads
// goes through one function, Utils.ReadJsonResource(name), so one postfix on
// it hands the game the changed text. Player.log shows BepInEx finishes
// loading plugins before the game reads its first table; the log lines below
// ("Awake" first, "first table read" later) prove it on every launch.
//
// The changes are not typed in here. tools/export_patches.py exports them
// from setup_mod.PATCHES into community_patches.json, which is embedded in
// this DLL, and the operations below are a line for line port of
// setup_mod.apply_ops and find_entry. tools/check_data_patches.py proves the
// port: with DumpTables on, this plugin writes each table before and after,
// and the check runs the Python on the "before" and compares bytes.
//
// All or nothing, as in 0.1: if any change does not fit the game's tables
// (a different game version, or a resources.assets 0.1 already changed on
// disk), the game gets its own tables untouched and a box on screen says why.
// Half a set of changes is worse than none, because the tables refer to each
// other: a card whose name lives in a language file that was not changed.
//
// This plugin also writes BepInEx\config\STI Community Patch.cfg, the one
// file where a player turns any part of the patch off. See CommunityToggle.cs.
//
// Compiled with the .NET Framework 4 csc against the game's own
// Assembly-CSharp.dll and the BepInEx 5 core DLLs (see build_plugin.ps1).

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

[Serializable]
public class CommunityPatchFile
{
    public string version;
    public string build;
    public List<CommunityPatch> patches;
}

[Serializable]
public class CommunityPatch
{
    public string key;
    public string kind;
    public bool defaultOn;
    public string title;
    public string desc;
    public List<CommunityTable> tables;
}

[Serializable]
public class CommunityTable
{
    public string asset;
    public List<CommunityOp> ops;
    public List<CommunityVerify> verify;
}

[Serializable]
public class CommunityOp
{
    public string kind;
    public string a;
    public string b;
    public string c;
    public int n;
    public List<string> list;
}

[Serializable]
public class CommunityVerify
{
    public string anchor;
    public string marker;
}

public class CommunityPatchError : Exception
{
    public CommunityPatchError(string message) : base(message) { }
}

[BepInPlugin("sti.community.datapatches", "STI Community Data Patches", "1.0.0")]
public class DataPatchesPlugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;
    internal static CommunityPatchFile PatchList;
    internal static HashSet<string> Wanted = new HashSet<string>();
    internal static ConfigEntry<bool> cfgDump;

    // null until the first table read; then the changed text per table name,
    // empty if anything failed
    internal static Dictionary<string, string> Patched;
    internal static string Problem;
    private bool _dismissed;

    private void Awake()
    {
        Log = Logger;
        PatchList = LoadPatchFile();
        if (PatchList == null)
        {
            Problem = "The patch list inside DataPatches.dll could not be read. "
                + "Unzip the whole Community Patch again.";
            return;
        }

        // The one settings file. Written by BepInEx's ConfigFile so the
        // format, comments and all, is what BepInEx players already know.
        ConfigFile cfg = new ConfigFile(CommunityToggle.FilePath, true);
        foreach (CommunityPatch p in PatchList.patches)
        {
            if (!p.defaultOn)
            {
                // CommunityToggle reads a missing line as on; that is only
                // true while every default is on.
                Problem = "Patch '" + p.key + "' is off by default, which "
                    + "CommunityToggle.cs cannot handle. This is a bug in the build.";
                Log.LogError(Problem);
                return;
            }
            string section = p.kind == "data" ? "Game data" : "Mods";
            string desc = p.title + (string.IsNullOrEmpty(p.desc) ? "" : "\n" + p.desc)
                + "\nSet to false to turn it off. Restart the game after a change.";
            if (cfg.Bind(section, p.key, p.defaultOn, desc).Value && p.kind == "data")
            {
                Wanted.Add(p.key);
            }
        }
        cfgDump = cfg.Bind("Debug", "DumpTables", false,
            "Writes every changed table, before and after, to BepInEx\\community_dump. "
            + "Only for checking the patch; leave it off.");

        new Harmony("sti.community.datapatches").PatchAll(Assembly.GetExecutingAssembly());
        int data = 0;
        foreach (CommunityPatch p in PatchList.patches)
        {
            if (p.kind == "data")
            {
                data++;
            }
        }
        Log.LogInfo("Space Travel Idle Community Patch " + PatchList.version
            + " (build " + PatchList.build + ")");
        Log.LogInfo("Data patches: Awake, " + Wanted.Count + " of " + data
            + " switched on in " + CommunityToggle.FileName
            + ". Nothing is changed until the game reads its first table.");
    }

    private static CommunityPatchFile LoadPatchFile()
    {
        try
        {
            using (Stream s = Assembly.GetExecutingAssembly()
                       .GetManifestResourceStream("community_patches.json"))
            using (StreamReader r = new StreamReader(s, Encoding.UTF8))
            {
                return ParsePatchFile(r.ReadToEnd());
            }
        }
        catch (Exception e)
        {
            Log.LogError("Data patches: could not read the embedded patch list: " + e);
            return null;
        }
    }

    // ---- reading community_patches.json ----
    //
    // Not Unity's JsonUtility. The first real launch of 0.1.1 (29.09.2026)
    // got a CommunityPatchFile back from it with patches == null, and Awake
    // died on a NullReferenceException before its first log line. The
    // offline check had used .NET's own JSON reader and passed. So the file
    // is read here, by code tools/check_data_patches.py calls too: what the
    // check proves is what the game runs.

    internal static CommunityPatchFile ParsePatchFile(string json)
    {
        int i = 0;
        object tree = JsonValue(json, ref i);
        JsonSpace(json, ref i);
        if (i != json.Length)
        {
            throw new FormatException("text after the end of the patch list at char " + i);
        }
        CommunityPatchFile file = (CommunityPatchFile)JsonTo(typeof(CommunityPatchFile), tree);
        if (file.patches == null || file.patches.Count == 0)
        {
            throw new FormatException("the patch list has no patches");
        }
        return file;
    }

    // Fills public fields of T from a parsed object, List<T> from an array.
    private static object JsonTo(Type type, object value)
    {
        if (value == null)
        {
            return null;
        }
        if (type == typeof(string))
        {
            return (string)value;
        }
        if (type == typeof(bool))
        {
            return (bool)value;
        }
        if (type == typeof(int))
        {
            return (int)(long)value;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            Type item = type.GetGenericArguments()[0];
            System.Collections.IList list = (System.Collections.IList)Activator.CreateInstance(type);
            foreach (object v in (List<object>)value)
            {
                list.Add(JsonTo(item, v));
            }
            return list;
        }
        object o = Activator.CreateInstance(type);
        foreach (KeyValuePair<string, object> kv in (Dictionary<string, object>)value)
        {
            FieldInfo f = type.GetField(kv.Key);
            if (f == null)
            {
                throw new FormatException("unknown field '" + kv.Key + "' in " + type.Name);
            }
            f.SetValue(o, JsonTo(f.FieldType, kv.Value));
        }
        return o;
    }

    private static void JsonSpace(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r'
                                || s[i] == '﻿'))
        {
            i++;
        }
    }

    private static object JsonValue(string s, ref int i)
    {
        JsonSpace(s, ref i);
        if (i >= s.Length)
        {
            throw new FormatException("the patch list ends too early");
        }
        char c = s[i];
        if (c == '{')
        {
            Dictionary<string, object> obj = new Dictionary<string, object>();
            i++;
            JsonSpace(s, ref i);
            if (i < s.Length && s[i] == '}')
            {
                i++;
                return obj;
            }
            while (true)
            {
                JsonSpace(s, ref i);
                string key = JsonString(s, ref i);
                JsonSpace(s, ref i);
                JsonExpect(s, ref i, ':');
                obj[key] = JsonValue(s, ref i);
                JsonSpace(s, ref i);
                if (i < s.Length && s[i] == ',')
                {
                    i++;
                    continue;
                }
                JsonExpect(s, ref i, '}');
                return obj;
            }
        }
        if (c == '[')
        {
            List<object> arr = new List<object>();
            i++;
            JsonSpace(s, ref i);
            if (i < s.Length && s[i] == ']')
            {
                i++;
                return arr;
            }
            while (true)
            {
                arr.Add(JsonValue(s, ref i));
                JsonSpace(s, ref i);
                if (i < s.Length && s[i] == ',')
                {
                    i++;
                    continue;
                }
                JsonExpect(s, ref i, ']');
                return arr;
            }
        }
        if (c == '"')
        {
            return JsonString(s, ref i);
        }
        if (string.CompareOrdinal(s, i, "true", 0, 4) == 0)
        {
            i += 4;
            return true;
        }
        if (string.CompareOrdinal(s, i, "false", 0, 5) == 0)
        {
            i += 5;
            return false;
        }
        if (string.CompareOrdinal(s, i, "null", 0, 4) == 0)
        {
            i += 4;
            return null;
        }
        int start = i;
        if (s[i] == '-')
        {
            i++;
        }
        while (i < s.Length && s[i] >= '0' && s[i] <= '9')
        {
            i++;
        }
        if (i == start || (i < s.Length && (s[i] == '.' || s[i] == 'e' || s[i] == 'E')))
        {
            throw new FormatException("unexpected '" + c + "' at char " + start);
        }
        return long.Parse(s.Substring(start, i - start), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void JsonExpect(string s, ref int i, char c)
    {
        if (i >= s.Length || s[i] != c)
        {
            throw new FormatException("expected '" + c + "' at char " + i);
        }
        i++;
    }

    // \uXXXX goes in as the one UTF-16 unit it names, so a surrogate pair
    // written as two escapes comes out as the same two units Python wrote.
    private static string JsonString(string s, ref int i)
    {
        JsonExpect(s, ref i, '"');
        StringBuilder sb = new StringBuilder();
        while (true)
        {
            if (i >= s.Length)
            {
                throw new FormatException("a string in the patch list never ends");
            }
            char c = s[i++];
            if (c == '"')
            {
                return sb.ToString();
            }
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }
            if (i >= s.Length)
            {
                throw new FormatException("a string in the patch list never ends");
            }
            char e = s[i++];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (i + 4 > s.Length)
                    {
                        throw new FormatException("a short \\u escape at char " + i);
                    }
                    sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                    i += 4;
                    break;
                default:
                    throw new FormatException("unknown escape \\" + e + " at char " + (i - 1));
            }
        }
    }

    /// <summary>Build every changed table at once, all or nothing.</summary>
    internal static void BuildAll(string firstTable)
    {
        Patched = new Dictionary<string, string>();
        Log.LogInfo("Data patches: first table read is '" + firstTable + "', building the changed tables");
        if (PatchList == null || Wanted.Count == 0)
        {
            return;
        }

        // Table names in the order their first patch lists them, and for
        // each, its entries in patch order: the same order setup_mod.py
        // applied them in.
        List<string> order = new List<string>();
        Dictionary<string, List<CommunityTable>> byTable = new Dictionary<string, List<CommunityTable>>();
        foreach (CommunityPatch p in PatchList.patches)
        {
            if (!Wanted.Contains(p.key))
            {
                continue;
            }
            foreach (CommunityTable t in p.tables)
            {
                if (!byTable.ContainsKey(t.asset))
                {
                    byTable[t.asset] = new List<CommunityTable>();
                    order.Add(t.asset);
                }
                byTable[t.asset].Add(t);
            }
        }

        Dictionary<string, string> built = new Dictionary<string, string>();
        try
        {
            foreach (string name in order)
            {
                TextAsset asset = Resources.Load<TextAsset>(Path.Combine("JSONData", name));
                if (asset == null)
                {
                    throw new CommunityPatchError("the game has no data table called '" + name + "'");
                }
                string original = asset.text;
                // setup_mod.py normalised line endings before patching, so a
                // search text only ever has to be written one way.
                string text = original.Replace("\r\n", "\n");
                foreach (CommunityTable t in byTable[name])
                {
                    text = ApplyOps(text, t, name);
                }
                built[name] = text;
                if (cfgDump != null && cfgDump.Value)
                {
                    Dump(name, original, text);
                }
            }
        }
        catch (CommunityPatchError e)
        {
            string was01 = LooksPatchedBy01()
                ? " Your game data still has the 0.1 changes on disk. "
                  + (Application.platform == RuntimePlatform.OSXPlayer
                     ? "Close the game and run \"Install Community Patch.command\" again, it takes them out."
                     : "Close the game, double-click \"Undo 0.1 game data.bat\" in the game folder, "
                       + "then start the game again.")
                : " Your game files differ from the community version 0.35.43 this patch is made for.";
            Problem = "The game data changes were NOT applied, the game runs with its own data. "
                + e.Message + "." + was01;
            Log.LogError("Data patches: " + Problem);
            return;
        }

        Patched = built;
        int patches = 0;
        foreach (CommunityPatch p in PatchList.patches)
        {
            if (Wanted.Contains(p.key))
            {
                patches++;
            }
        }
        Log.LogInfo("Data patches: " + patches + " patch(es) applied to " + built.Count
            + " table(s): " + string.Join(", ", order.ToArray()));
    }

    private static bool LooksPatchedBy01()
    {
        // setup_mod.py kept the untouched file beside the one it changed.
        // Windows: SpaceTravelIdle_Data; Mac: the .app's Contents/Resources/Data.
        foreach (string dir in new[] { Application.dataPath,
                                       Path.Combine(Application.dataPath, Path.Combine("Resources", "Data")) })
        {
            if (System.IO.File.Exists(Path.Combine(dir, "resources.assets.backup-original")))
            {
                return true;
            }
        }
        return false;
    }

    private static void Dump(string name, string original, string patched)
    {
        try
        {
            string dir = Path.Combine(Paths.BepInExRootPath, "community_dump");
            Directory.CreateDirectory(dir);
            UTF8Encoding utf8 = new UTF8Encoding(false);
            System.IO.File.WriteAllText(Path.Combine(dir, name + ".original.txt"), original, utf8);
            System.IO.File.WriteAllText(Path.Combine(dir, name + ".patched.txt"), patched, utf8);
        }
        catch (Exception e)
        {
            Log.LogWarning("Data patches: could not dump '" + name + "': " + e.Message);
        }
    }

    // ---- a line for line port of setup_mod.apply_ops and find_entry ----

    private static int Count(string text, string needle)
    {
        // Python's str.count: non-overlapping, ordinal
        int n = 0;
        int i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            n++;
            i += needle.Length;
        }
        return n;
    }

    private static List<string> Split(string text, string sep)
    {
        List<string> parts = new List<string>();
        int i = 0;
        int at;
        while ((at = text.IndexOf(sep, i, StringComparison.Ordinal)) >= 0)
        {
            parts.Add(text.Substring(i, at - i));
            i = at + sep.Length;
        }
        parts.Add(text.Substring(i));
        return parts;
    }

    private static string Differs(string table)
    {
        return " in " + table;
    }

    private static void FindEntry(string text, string key, string table,
                                  out int start, out int end, out string indent)
    {
        string needle = "\"" + key + "\": {";
        int count = Count(text, needle);
        if (count != 1)
        {
            throw new CommunityPatchError("expected exactly one '" + key + "' entry but found "
                + count + Differs(table));
        }
        start = text.IndexOf(needle, StringComparison.Ordinal);
        int lineStart = text.LastIndexOf('\n', start) + 1;
        indent = text.Substring(lineStart, start - lineStart);
        int i = text.IndexOf('{', start);
        int depth = 0;
        while (true)
        {
            char ch = text[i];
            if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }
            }
            i++;
        }
        end = i + 1;
    }

    private static string ApplyOps(string text, CommunityTable t, string table)
    {
        foreach (CommunityOp op in t.ops)
        {
            if (op.kind == "replace")
            {
                int count = Count(text, op.a);
                if (count != op.n)
                {
                    throw new CommunityPatchError("expected " + op.n + " occurrence(s) of a search text but found "
                        + count + Differs(table));
                }
                text = text.Replace(op.a, op.b);
            }
            else if (op.kind == "replace_seq")
            {
                int count = Count(text, op.a);
                if (count != op.list.Count)
                {
                    throw new CommunityPatchError("expected " + op.list.Count + " occurrence(s) of a search text but found "
                        + count + Differs(table));
                }
                List<string> parts = Split(text, op.a);
                StringBuilder sb = new StringBuilder();
                for (int k = 0; k < op.list.Count; k++)
                {
                    sb.Append(parts[k]).Append(op.list[k]);
                }
                sb.Append(parts[parts.Count - 1]);
                text = sb.ToString();
            }
            else if (op.kind == "set_asset")
            {
                string got = Sha256(text);
                if (got != op.a)
                {
                    throw new CommunityPatchError("the game's own copy of this table is not the one this patch was built against (sha256 "
                        + got + ")" + Differs(table));
                }
                text = op.b;
            }
            else if (op.kind == "clone_entry")
            {
                if (text.IndexOf(op.b, StringComparison.Ordinal) >= 0)
                {
                    throw new CommunityPatchError("'" + op.b + "' already exists" + Differs(table));
                }
                int start, end;
                string indent;
                FindEntry(text, op.a, table, out start, out end, out indent);
                string block = text.Substring(start, end - start);
                int at = block.IndexOf(op.a, StringComparison.Ordinal);
                block = block.Substring(0, at) + op.b + block.Substring(at + op.a.Length);
                text = text.Substring(0, end) + ",\n" + indent + block + text.Substring(end);
            }
            else if (op.kind == "insert_entry")
            {
                string newKey = op.c.Split('"')[1];
                if (text.IndexOf("\"" + newKey + "\": {", StringComparison.Ordinal) >= 0)
                {
                    continue;  // this language already has it
                }
                int start, end;
                string indent;
                FindEntry(text, op.a, table, out start, out end, out indent);
                if (op.b == "after")
                {
                    text = text.Substring(0, end) + ",\n" + indent + op.c + text.Substring(end);
                }
                else
                {
                    text = text.Substring(0, start) + op.c + ",\n" + indent + text.Substring(start);
                }
            }
            else
            {
                throw new CommunityPatchError("unknown operation '" + op.kind + "'");
            }
        }
        // each verify marker must appear within 25k chars of its anchor id
        foreach (CommunityVerify v in t.verify)
        {
            int pos = text.IndexOf("\"id\": \"" + v.anchor + "\"", StringComparison.Ordinal);
            if (pos < 0)
            {
                throw new CommunityPatchError("verification failed near '" + v.anchor + "'" + Differs(table));
            }
            int from = Math.Max(0, pos - 25000);
            int to = Math.Min(text.Length, pos + 25000);
            if (text.Substring(from, to - from).IndexOf(v.marker, StringComparison.Ordinal) < 0)
            {
                throw new CommunityPatchError("verification failed near '" + v.anchor + "'" + Differs(table));
            }
        }
        return text;
    }

    private static string Sha256(string text)
    {
        using (SHA256 sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(new UTF8Encoding(false).GetBytes(text));
            StringBuilder sb = new StringBuilder();
            foreach (byte b in hash)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }
    }

    // ---- the box on screen when the changes could not be made ----

    private void OnGUI()
    {
        if (Problem == null || _dismissed)
        {
            return;
        }
        float w = Mathf.Min(900f, Screen.width - 40f);
        Rect box = new Rect((Screen.width - w) / 2f, 30f, w, 190f);
        GUIStyle style = new GUIStyle(GUI.skin.box);
        style.fontSize = 20;
        style.wordWrap = true;
        style.alignment = TextAnchor.UpperLeft;
        style.padding = new RectOffset(16, 16, 14, 14);
        GUI.Box(box, "Space Travel Idle Community Patch\n\n" + Problem, style);
        if (GUI.Button(new Rect(box.xMax - 110f, box.yMax - 46f, 94f, 32f), "OK"))
        {
            _dismissed = true;
        }
    }
}

[HarmonyPatch(typeof(Utils), "ReadJsonResource")]
public static class ReadJsonResourcePatch
{
    public static void Postfix(string filepath, ref string __result)
    {
        if (DataPatchesPlugin.Patched == null)
        {
            DataPatchesPlugin.BuildAll(filepath);
        }
        string name = Path.GetFileName(filepath);
        string text;
        if (DataPatchesPlugin.Patched.TryGetValue(name, out text))
        {
            __result = text;
            // once per table, so the log proves the game got the changed
            // text and not only that it was built
            if (Handed.Add(name))
            {
                DataPatchesPlugin.Log.LogInfo("Data patches: the game read the changed '" + name
                    + "' (" + Handed.Count + " of " + DataPatchesPlugin.Patched.Count + ")");
            }
        }
    }

    private static readonly HashSet<string> Handed = new HashSet<string>();
}
