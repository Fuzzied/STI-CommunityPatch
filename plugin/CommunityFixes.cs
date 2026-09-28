// Space Travel Idle community mod - battle fixes
//
// Three small battle fixes:
//
// 1. BattleUnit.SetHpModify: vanilla raises max HP when an in-hand HP
//    buff (Taichi Master, The Bomb) applies, but never raises current
//    HP with it - the buff only added empty health bar. Current HP now
//    scales proportionally with max HP in both directions, so your HP
//    percentage is preserved when the modifier changes.
//
// 2. BattlePanel.PlayerAreaInsertCards: the battle hand area was laid
//    out for 5 cards; with the modded 6th hand slot the last card
//    clipped into the Skip/Surrender buttons. When more than 5 cards
//    are shown the hand area is scaled down so they all fit (5 cards
//    or fewer render exactly as vanilla).
//
// 3. BattleManager's four cached log lines (v1.1.0). Fuzzied: "the output from
//    biomass or stardust bugged somewhere along the way", with a screenshot of
//    the battle log reading "6538,18 panels.storage.resources.biomass.name".
//
//    BattleManager keeps four log strings in static readonly fields:
//
//        BIOMASS_DROP_LOG    = GetLocalisation("...biomass.name")
//        STARDUST_DROP_LOG   = GetLocalisation("...stardust.name")
//        DECK_EMPTY_LOG      = "..." + GetLocalisation("messages.deckBag...")
//        TOO_MANY_ROUNDS_LOG = GetLocalisation("panels.battle.too_many_rounds")
//
//    Static field initialisers run once, the first time anything touches the
//    class, and whatever they produce is what you get for the rest of the
//    session. Localisation.GetLocalisation opens with
//
//        if (!isReady) { return path; }
//
//    and isReady is only set in StartManager.Awake, on the title screen. So if
//    anything reaches BattleManager before that line runs, all four fields are
//    frozen holding their own lookup keys, and every biomass and stardust drop
//    for the rest of the session prints the key instead of the word. Nothing
//    else breaks, because everything else in the battle log is looked up when
//    it is drawn rather than cached at class load.
//
//    Which plugin or which scene reaches BattleManager that early is not
//    something this fix needs to know, and I could not pin it down: the repair
//    is the same either way. Once Localisation.isReady is true - the first
//    frame that it is - each of the four fields is compared against what its
//    key resolves to now, and any field still holding the raw key is written
//    back through reflection. A field that is already correct is left alone, so
//    on a session where this never went wrong the fix does nothing at all.
//
//    Touching the fields at that moment also runs the class initialiser if it
//    has not run yet, which loads them correctly in the first place.
//
// Compiled with the .NET Framework 4 csc against the game's own
// Assembly-CSharp.dll and the BepInEx 5 core DLLs (see build_plugin.ps1).

using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using LargeNumbers;
using UnityEngine;

[BepInPlugin("sti.community.battlefixes", "STI Community Battle Fixes", "1.1.0")]
public class CommunityFixesPlugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;

    private bool logLinesChecked;

    private void Awake()
    {
        Log = Logger;
        Harmony harmony = new Harmony("sti.community.battlefixes");
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        Logger.LogInfo("Battle fixes active: HP-buff current HP scaling, "
            + "6-card hand layout, battle log lines");
    }

    // One shot, on the first frame the game says its language file is ready.
    // A per-frame bool is cheaper than any hook precise enough to be worth it,
    // and this way no scene or load path can slip past. See fix 3 in the
    // header.
    private void Update()
    {
        if (logLinesChecked)
        {
            return;
        }
        try
        {
            if (!Localisation.isReady)
            {
                return;
            }
        }
        catch (Exception)
        {
            logLinesChecked = true;
            return;
        }
        logLinesChecked = true;
        BattleLogLineRepair.Run();
    }
}

// See fix 3 in the header. Four fields, each with the key it was built from
// and whatever the game wraps around it.
public static class BattleLogLineRepair
{
    private static readonly string[] Fields =
    {
        "BIOMASS_DROP_LOG",
        "STARDUST_DROP_LOG",
        "DECK_EMPTY_LOG",
        "TOO_MANY_ROUNDS_LOG"
    };

    private static readonly string[] Paths =
    {
        "panels.storage.resources.biomass.name",
        "panels.storage.resources.stardust.name",
        "messages.deckBag.deckEmpty",
        "panels.battle.too_many_rounds"
    };

    private static readonly string[] Before =
    {
        "", "", "\n<b><color=red>", ""
    };

    private static readonly string[] After =
    {
        "", "", "</color></b>", ""
    };

    public static void Run()
    {
        int repaired = 0;
        for (int i = 0; i < Fields.Length; i++)
        {
            try
            {
                FieldInfo field = AccessTools.Field(typeof(BattleManager),
                    Fields[i]);
                if (field == null)
                {
                    continue; // the game renamed it; nothing to repair
                }
                string word = Localisation.GetLocalisation(Paths[i]);
                if (word == null || word == Paths[i])
                {
                    // The key itself is missing from the language file, so
                    // there is no better text to put there than what is
                    // already there.
                    continue;
                }
                string good = Before[i] + word + After[i];
                string current = field.GetValue(null) as string;
                if (current == good)
                {
                    continue; // loaded correctly - the normal case
                }
                field.SetValue(null, good);
                repaired++;
                CommunityFixesPlugin.Log.LogInfo("Battle log line '"
                    + Fields[i] + "' was cached before the language file was "
                    + "ready and has been repaired");
            }
            catch (Exception e)
            {
                CommunityFixesPlugin.Log.LogWarning("Could not check the '"
                    + Fields[i] + "' battle log line: " + e.Message);
            }
        }
        if (repaired == 0)
        {
            CommunityFixesPlugin.Log.LogInfo("Battle log lines all loaded "
                + "correctly; nothing to repair");
        }
    }
}

[HarmonyPatch(typeof(BattleUnit), "SetHpModify")]
public static class SetHpModifyPatch
{
    private static readonly MethodInfo RemainingHPSetter =
        AccessTools.PropertySetter(typeof(BattleUnit), "remainingHP");

    public static void Prefix(BattleUnit __instance, out ScientificNotation[] __state)
    {
        // remember max HP and current HP from before the modifier changes
        __state = new ScientificNotation[] { __instance.maxHp, __instance.remainingHP };
    }

    public static void Postfix(BattleUnit __instance, ScientificNotation[] __state)
    {
        ScientificNotation oldMax = __state[0];
        ScientificNotation oldRemaining = __state[1];
        ScientificNotation newMax = __instance.maxHp;
        if (oldMax <= ScientificNotation.zero || oldMax == newMax)
        {
            return; // no-op call (same modifier reapplied each round)
        }
        ScientificNotation newRemaining = oldRemaining * newMax / oldMax;
        if (newRemaining > newMax)
        {
            newRemaining = newMax;
        }
        RemainingHPSetter.Invoke(__instance, new object[] { newRemaining });
    }
}

[HarmonyPatch(typeof(BattlePanel), "PlayerAreaInsertCards")]
public static class HandLayoutPatch
{
    private static readonly FieldInfo HandCardAreaField =
        AccessTools.Field(typeof(BattlePanel), "handCardArea");

    public static void Postfix(BattlePanel __instance)
    {
        GameObject area = (GameObject)HandCardAreaField.GetValue(__instance);
        if (area == null)
        {
            return;
        }
        int slots = area.transform.childCount; // one wrapper per hand card / empty slot
        float scale = 1f;
        if (slots > 5)
        {
            scale = 5f / (float)slots;
        }
        area.transform.localScale = new Vector3(scale, scale, 1f);
    }
}
