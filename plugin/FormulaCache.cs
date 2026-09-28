// Space Travel Idle community mod - Formula Cache
//
// Fuzzied, 26.09.2026, on the lag with many jobs running: "yes to both, new
// plugin". Measured cause, see docs/todo.md "Lag, third to fifth measurement"
// and docs/perf-baseline.md:
//
//   Every ordinary enemy kill rolls its drops in
//   BattleZoneLibrary.CalcCardDrops, which asks BattleZone.GetDropPool for the
//   drop chances, which reads each card's drop formula from text and hands it
//   to ParsableEquation.EvaluateExpr, which runs the MultiParse library over
//   it from scratch. For every piece of the formula MultiParse tries each of
//   its known functions and operators with Regex.Match on a pattern string it
//   builds on the spot. .NET keeps only the last 15 patterns it compiled
//   (Regex.CacheSize), MultiParse uses more than 40, so every one is compiled
//   again every time. On the Pluto save that was about 10 MB of garbage per
//   kill, about 80 % of all the game's garbage, and the collections it forces
//   are the stutter.
//
// Two fixes, each with its own switch:
//
//   KeepAnswers: remember each formula's answer. The game only ever evaluates
//   plain arithmetic on numbers it has already put into the text (the level,
//   the zone's constants), and MultiParse has no random or clock functions,
//   so the same text always gives the same number. The first time a formula
//   is seen it is parsed as before and its answer kept; every later time the
//   kept answer is handed back and the parse is skipped. The hook sits on
//   MultiParse's Expression.Compile, which every formula in the game passes
//   through, and only on the one Expression the game itself owns.
//   Not kept, and so always parsed: text with an assignment or a variable in
//   it (MultiParse supports both, the game uses neither), and any answer that
//   is not a plain number or true/false.
//
//   KeepMorePatterns: raise Regex.CacheSize from 15 to 256, so the parses
//   that still happen (each formula once, and anything KeepAnswers leaves
//   alone) reuse their patterns instead of compiling them again. The answers
//   are the same either way; this only changes how often a pattern is built.
//
// Proof it changes nothing but speed: tools/perf_baseline.py runs the game
// with the PerfProbe formula check, which records every formula text and its
// exact answer, once without this plugin and once with it, and fails on any
// formula whose answer differs.
//
// Compiled with the .NET Framework 4 csc against the game's own
// Assembly-CSharp.dll and the BepInEx 5 core DLLs (see build_plugin.ps1).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

[BepInPlugin("sti.community.formulacache", "STI Community Formula Cache", "1.0.0")]
public class FormulaCachePlugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;
    internal static object gameExpression;
    internal static FieldInfo isCompiled;

    // Every battle zone at every level is 22 579 different formulas (the
    // perf probe's full sweep, 26.09.2026), about 4 MB kept; normal play only
    // meets the zones and levels it is in. The limit only guards against a
    // formula that embeds an ever-changing number, which would otherwise grow
    // the table forever.
    internal const int MaxKept = 100000;
    internal static readonly Dictionary<string, object> kept =
        new Dictionary<string, object>(StringComparer.Ordinal);
    internal static readonly object gate = new object();
    internal static long reused;
    internal static long parsed;
    internal static long notKept;

    private ConfigEntry<bool> cfgAnswers;
    private ConfigEntry<bool> cfgPatterns;
    private float nextReport = 60f;

    private void Awake()
    {
        Log = Logger;
        cfgAnswers = Config.Bind("General", "KeepAnswers", true,
            "Remember each formula's answer, so the game parses a drop, enemy or "
            + "bonus formula once instead of on every enemy kill. Same answers, "
            + "far less garbage, far fewer stutters.");
        cfgPatterns = Config.Bind("General", "KeepMorePatterns", true,
            "Let .NET keep 256 compiled text patterns instead of 15, so the "
            + "parses that still happen reuse them.");

        if (cfgPatterns.Value)
        {
            int before = Regex.CacheSize;
            if (before < 256)
            {
                Regex.CacheSize = 256;
            }
            Logger.LogInfo("Formula Cache: pattern cache " + before + " -> " + Regex.CacheSize);
        }

        if (!cfgAnswers.Value)
        {
            Logger.LogInfo("Formula Cache: KeepAnswers is off, formulas are parsed every time");
            return;
        }
        try
        {
            FieldInfo exprField = AccessTools.Field(typeof(ParsableEquation), "expr");
            MethodInfo compile = AccessTools.DeclaredMethod(typeof(MultiParse.Expression),
                "Compile", new Type[0]);
            isCompiled = AccessTools.Field(typeof(MultiParse.Expression), "isCompiled");
            object expr = exprField == null ? null : exprField.GetValue(null);
            if (expr == null || compile == null || isCompiled == null)
            {
                Logger.LogWarning("Formula Cache: the game's formula engine was not where "
                    + "expected (ParsableEquation.expr, Expression.Compile, isCompiled). "
                    + "Answers are not kept; the game runs as without this plugin.");
                return;
            }
            gameExpression = expr;
            new Harmony("sti.community.formulacache").Patch(compile,
                new HarmonyMethod(typeof(FormulaCacheHooks), "Prefix"),
                new HarmonyMethod(typeof(FormulaCacheHooks), "Postfix"));
            Logger.LogInfo("Formula Cache: keeping formula answers");
        }
        catch (Exception e)
        {
            gameExpression = null;
            Logger.LogWarning("Formula Cache: could not hook the formula engine, answers are "
                + "not kept: " + e.Message);
        }
    }

    // A short line a minute in, then every 30 minutes, so the log shows it is
    // working without filling up.
    private void Update()
    {
        if (gameExpression == null || Time.unscaledTime < nextReport)
        {
            return;
        }
        nextReport = Time.unscaledTime + 1800f;
        int count;
        lock (gate)
        {
            count = kept.Count;
        }
        Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
            "Formula Cache: {0} formulas kept, {1} answers reused, {2} parsed, {3} not keepable",
            count, reused, parsed, notKept));
    }

    // Plain arithmetic only: no assignment, and every name must be a function
    // call, "pow(" or "min(". A name standing alone would be a variable, whose
    // value can change between calls. A letter straight after a digit is an
    // exponent, as in 1e5 or 2.5E+3, and is part of the number.
    internal static bool Keepable(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('=') >= 0)
        {
            return false;
        }
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (!char.IsLetter(c) && c != '_')
            {
                i++;
                continue;
            }
            bool exponent = i > 0 && (c == 'e' || c == 'E') && char.IsDigit(text[i - 1]);
            int start = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
            {
                i++;
            }
            if (exponent && IsDigits(text, start + 1, i))
            {
                continue;
            }
            int j = i;
            while (j < text.Length && char.IsWhiteSpace(text[j]))
            {
                j++;
            }
            if (j >= text.Length || text[j] != '(')
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsDigits(string s, int from, int to)
    {
        for (int k = from; k < to; k++)
        {
            if (!char.IsDigit(s[k]))
            {
                return false;
            }
        }
        return true;
    }

    // Boxed numbers and bools cannot change, so handing the same box back is
    // the same as handing back a fresh one.
    internal static bool KeepableAnswer(object answer)
    {
        return answer is double || answer is float || answer is int || answer is long
            || answer is decimal || answer is bool || answer is short || answer is byte
            || answer is uint || answer is ulong || answer is ushort || answer is sbyte;
    }
}

public static class FormulaCacheHooks
{
    public static bool Prefix(MultiParse.Expression __instance, ref object __result,
        out bool __state)
    {
        __state = false;
        if (!ReferenceEquals(__instance, FormulaCachePlugin.gameExpression))
        {
            return true;
        }
        string text = __instance.ParseExpression;
        if (!FormulaCachePlugin.Keepable(text))
        {
            FormulaCachePlugin.notKept++;
            return true;
        }
        object answer;
        lock (FormulaCachePlugin.gate)
        {
            if (!FormulaCachePlugin.kept.TryGetValue(text, out answer))
            {
                __state = true;
                return true;
            }
        }
        // The original leaves isCompiled true and its compiled steps behind for
        // Evaluate() without text. Those steps belong to an older formula now,
        // so mark it uncompiled: a later Evaluate() then parses the current
        // text instead of replaying the wrong one. The game never calls it.
        FormulaCachePlugin.isCompiled.SetValue(__instance, false);
        FormulaCachePlugin.reused++;
        __result = answer;
        return false;
    }

    public static void Postfix(MultiParse.Expression __instance, object __result, bool __state)
    {
        if (!__state)
        {
            return;
        }
        FormulaCachePlugin.parsed++;
        if (!FormulaCachePlugin.KeepableAnswer(__result))
        {
            FormulaCachePlugin.notKept++;
            return;
        }
        lock (FormulaCachePlugin.gate)
        {
            if (FormulaCachePlugin.kept.Count >= FormulaCachePlugin.MaxKept)
            {
                FormulaCachePlugin.kept.Clear();
            }
            FormulaCachePlugin.kept[__instance.ParseExpression] = __result;
        }
    }
}
