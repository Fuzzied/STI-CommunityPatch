// Space Travel Idle community mod - resource collectors top the tank off
//
// Fuzzied: "If you have a resource collection at 100% but dont have capacity for
// one whole tick, it doesn't fire at all."
//
// ElementSource.Tick opens with
//
//     if (outputPercentage == 0.0 || Player.shared.storage.IsResourceFullWithOutput(
//             baseElementOutputPerFill.type,
//             baseElementOutputPerFill.amount * offlineSimulationSpeed))
//     {
//         inputAmount = ScientificNotation.zero;
//         outputAmount = ScientificNotation.zero;
//         filledPercentage = 0.0;
//         return 0;
//     }
//
// and IsResourceFullWithOutput is
//
//     elementDict[type].amount + GetElementGainMultiplier(type) * output
//         >= cap * Math.Max(unit.GetDiscardValue(), unit.GetLockValue())
//
// Two separate faults in there:
//
//   (a) It asks whether a WHOLE BAR's output would overflow, not whether there
//       is any room. baseElementOutputPerFill is the batch a completed bar
//       delivers, and a bar can be many seconds of energy, so a collector goes
//       idle up to a full batch short of the ceiling. Worth being precise about
//       what the ceiling is: not the tank size, but cap * max(discard, lock),
//       i.e. wherever the player put the sliders.
//
//   (b) When it refuses it sets filledPercentage = 0.0. Any progress already
//       bought with energy is thrown away, silently, every tick it waits.
//
// Note that outputPercentage cannot be used to shrink the problem: AddProgress
// does filledPercentage += progress * outputPercentage and then returns the
// whole baseElementOutputPerFill once the bar crosses 1.0. Output % is fill
// speed and energy cost per tick, not batch size. Turning a collector down
// only postpones the same refusal.
//
// So this plugin does three things, and they only work as a set:
//
//   1. IsResourceFullWithOutput becomes "is the tank AT the ceiling" rather
//      than "would a whole batch overflow", so a collector runs until the tank
//      is genuinely full.
//
//   2. When a delivery does not fit, the batch is scaled down to exactly the
//      headroom before it is added, and the tank is then snapped to the
//      ceiling. Scaling before the add rather than letting NormalizeResource
//      clip afterwards keeps the storage summary honest - nothing is booked as
//      discarded, because nothing is being thrown away. The snap matters for a
//      duller reason: without it, rounding can leave the tank a hair under the
//      ceiling, the guard in step 1 says "not full", and the collector burns a
//      tick of energy for a nanoscopic gain on every tick, for ever.
//
//   3. The part of the bar that did not get delivered stays on the bar, and a
//      refused tick keeps its bar instead of wiping it. This is what stops the
//      energy drain. Deliver 30% of a batch and 70% of the bar's work is still
//      banked, so the energy a collector spends stays proportional to the
//      resource it actually produces. Nothing is wasted and nothing is free.
//
// Together: the tank fills to the line you set, sitting at a full tank costs
// nothing, and the moment you spend something the collector picks up from
// exactly where it paused.
//
// On cost, since this runs ten times a second per collector. The steady state
// that matters is a full tank, and there the guard in step 1 refuses and Tick
// takes the same early exit it always did - so the common case stays the
// game's own cheap path, which is more than can be said for simply letting
// collectors run and discard. Steps 2 and 3 do a handful of comparisons and
// one division on the ticks where a delivery is clipped. No allocation, no
// loops, no lookups by string, nothing touching Unity objects.
//
// Offline is left completely alone (see TopOffState.online). The offline
// catch-up runs with offlineSimulationSpeed > 1 and deliberately does not
// normalise until the end, so the arithmetic above does not apply to it and
// there is nothing here worth risking the offline earnings calculation for.
//
// v1.2.0 does the same for energy. EnergySource.Tick has the identical flaw,
// with free sources already exempted:
//
//     if (outputPercentage == 0.0
//         || (Player.shared.IsEnergyFullWithOutput(
//                 baseEnergyOutputPerFill * offlineSimulationSpeed)
//             && baseBarFillRequiredResource.amount > ScientificNotation.zero))
//
// and IsEnergyFullWithOutput is
//
//     energy.amount + CalculateEnergy(output) >= Energy.cap
//
// which is worse here than it is for resources, because energy bars are big.
// Fuzzied, at Saturn: "Two of my Energy producing are idle", then "It was the
// energy storage cap going over one tick". His save says so exactly. The two
// that stopped are biomass_power_space (500 energy a bar) and gas_power_plant_2
// (1000 energy a bar), both of which cost a resource; the one that kept running
// is solar_energy_space, which has no requirement and so is exempted by the
// second half of that test. Multiply a 1000 base by his eProdSpeed and energyMul
// and one bar delivers more than the whole tank holds - at which point
// IsEnergyFullWithOutput is true at every energy level there is, including zero,
// and the plant can never run again. His energy in that save reads zero.
//
// So the same three steps, with two differences worth naming. The ceiling is a
// plain Energy.cap rather than cap * max(discard, lock), because NormalizeEnergy
// clips at the cap and there is no per-resource slider in the way. And the clip
// happens in Player.AddEnergy rather than in the storage, because that is where
// an energy delivery lands.
//
// The off switch lives both in the config file and, because Fuzzied asked for it
// there, as a row in the game's own Settings panel - see the bottom of this
// file.
//
// Compiled with the .NET Framework 4 csc against the game's own
// Assembly-CSharp.dll and the BepInEx 5 core DLLs (see build_plugin.ps1).

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LargeNumbers;
using UnityEngine;
using UnityEngine.UI;

[BepInPlugin("sti.community.topoff", "STI Community Top Off", "1.2.6")]
public class TopOffPlugin : BaseUnityPlugin
{
    internal static ConfigEntry<bool> cfgEnabled;

    internal static ConfigEntry<bool> cfgEnergy;

    internal static ConfigEntry<bool> cfgExplain;

    internal static ManualLogSource Log;

    private void Awake()
    {
        Log = Logger;
        cfgEnabled = Config.Bind("1 General", "TopOffCollectors", true,
            "Resource collectors run until the tank actually reaches your discard/lock "
            + "line instead of stopping a whole batch short; a collector with no room "
            + "keeps the progress it has already paid for instead of throwing it away; "
            + "and the part of a delivery that does not fit stays on the bar rather "
            + "than being discarded. Set to false for the base game's behaviour.");
        cfgEnergy = Config.Bind("1 General", "TopOffEnergySources", true,
            "The same treatment for energy plants, which have the identical "
            + "fault: a plant stops when one more bar would overflow the energy "
            + "tank rather than when the tank is actually full, and a plant whose "
            + "bar is worth more than the whole tank therefore never runs at all, "
            + "even on empty. Free plants like solar were already exempt from "
            + "that test and are unaffected either way. This follows the "
            + "collector setting above; set it to false to leave energy on the "
            + "base game's behaviour while keeping the collector fix.");
        cfgExplain = Config.Bind("2 Diagnostics", "ExplainIdleEnergyPlants", true,
            "Write a line to the log when an energy plant that is switched on produces nothing, saying which of the two reasons it was: the energy tank was already full, or there was no input resource to buy progress with. At most one line per plant per minute. This exists because an idle plant looks identical in the game either way, and the two have completely different fixes.");
        Harmony harmony = new Harmony("sti.community.topoff");
        harmony.PatchAll(Assembly.GetExecutingAssembly());

        // This line used to say "Top-off active" no matter what the two
        // settings were, which made it a lie in the one situation where the
        // log matters. It runs after both Config.Bind calls, so it already
        // knows the answer. It is also the first line anyone reads in a bug
        // report, and a first line that lies costs more than it saves.
        // The two are reported separately because energy needs both keys:
        // Enabled() ands them together, so collectors on with energy off is a
        // real state a player can be in.
        Logger.LogInfo("Top-off: collectors "
            + (cfgEnabled.Value ? "on" : "OFF")
            + ", energy plants "
            + ((cfgEnabled.Value && cfgEnergy.Value) ? "on" : "OFF")
            + (cfgEnabled.Value
                ? ""
                : " (the collector setting is off, which turns both off)"));
    }
}

// Shared between the three patches below. All of it is written and read on
// Unity's main thread inside one PriorityJobManager tick, so plain statics are
// the right tool.
public static class TopOffState
{
    // True only for the duration of one online ElementSource.Tick. Both of the
    // other patched methods are reached from there and are gated on it, which
    // is what keeps this plugin out of the offline simulation and out of every
    // other caller of AddElementBatch (research payouts, battle drops, and so
    // on).
    //
    // If Tick ever threw, the postfix that lowers this would be skipped and the
    // flag would stay up until the next tick raised it again. Deliberately not
    // guarded with a Harmony finalizer, because the worst a leak can do is trim
    // some other deposit to the discard line and snap the tank there - which is
    // exactly what NormalizeResource does to that deposit anyway. Not worth
    // wrapping every collector tick in an exception handler for.
    internal static bool online;

    // What fraction of the last delivery fit. 1.0 means "all of it", which is
    // the normal case and the value the Tick prefix resets to.
    internal static double clipFraction;

    internal static bool Enabled()
    {
        return TopOffPlugin.cfgEnabled != null && TopOffPlugin.cfgEnabled.Value;
    }

    // cap * max(discard, lock) - the same expression ResourceStorage uses in
    // both NormalizeResource and IsResourceFullWithOutput. Returns false while
    // the storage panel is still being built, which is the cue to leave the
    // game's own code to it.
    internal static bool TryGetCeiling(ElementType type, out ScientificNotation ceiling)
    {
        ceiling = ScientificNotation.zero;
        if (MainPanel.shared == null)
        {
            return false;
        }
        StoragePanel panel = MainPanel.shared.storagePanel;
        if (panel == null || panel.resourceUnitDict == null)
        {
            return false;
        }
        ResourceUnit unit;
        if (!panel.resourceUnitDict.TryGetValue(type, out unit) || unit == null)
        {
            return false;
        }
        ceiling = ResourceStorage.cap
            * Math.Max(unit.GetDiscardValue(), unit.GetLockValue());
        return true;
    }
}

// Step 1. "Is there room for a whole batch" becomes "is the tank at the line".
// ElementSource.Tick is the only caller in the game, so setting __result here
// is as surgical as a transpiler would be and a great deal easier to read.
[HarmonyPatch(typeof(ResourceStorage), "IsResourceFullWithOutput")]
public static class ResourceFullGuardPatch
{
    public static bool Prefix(ResourceStorage __instance, ElementType type,
        ref bool __result)
    {
        if (!TopOffState.online || !TopOffState.Enabled())
        {
            return true;
        }
        try
        {
            ScientificNotation ceiling;
            if (!TopOffState.TryGetCeiling(type, out ceiling))
            {
                return true;
            }
            ElementBatch held;
            if (__instance.elementDict == null
                || !__instance.elementDict.TryGetValue(type, out held)
                || held == null)
            {
                return true;
            }
            __result = held.amount >= ceiling;
            return false;
        }
        catch (Exception e)
        {
            TopOffPlugin.Log.LogWarning(
                "Top-off could not measure the headroom, using the game's own "
                + "check for this tick: " + e.Message);
            return true;
        }
    }
}

// Step 2. Scale a delivery that does not fit down to the headroom, then snap
// the tank to the ceiling.
//
// The batch object being mutated here is the one ElementSource.Tick builds
// fresh on the line above the call (new ElementBatch(type, amount * deter)),
// so it belongs to that call and nothing else reads it afterwards.
public struct TopOffAddState
{
    public bool clipped;
    public ElementType type;
    public ScientificNotation ceiling;
}

[HarmonyPatch(typeof(ResourceStorage), "AddElementBatch")]
public static class AddElementBatchClipPatch
{
    public static void Prefix(ResourceStorage __instance, ElementBatch batch,
        int offlineSimulationSpeed, out TopOffAddState __state)
    {
        __state = default(TopOffAddState);
        if (!TopOffState.online || !TopOffState.Enabled()
            || batch == null || offlineSimulationSpeed != 1)
        {
            return;
        }
        try
        {
            ScientificNotation ceiling;
            if (!TopOffState.TryGetCeiling(batch.type, out ceiling))
            {
                return;
            }
            ElementBatch held;
            if (__instance.elementDict == null
                || !__instance.elementDict.TryGetValue(batch.type, out held)
                || held == null)
            {
                return;
            }
            // What the tank is about to gain, after the resource multipliers -
            // the same figure AddElementBatch itself computes and returns.
            ScientificNotation intended =
                ResourceStorage.GetElementGainMultiplier(batch.type) * batch.amount;
            if (intended <= ScientificNotation.zero)
            {
                return;
            }
            ScientificNotation headroom = ceiling - held.amount;
            if (headroom >= intended)
            {
                return; // it all fits; this is the ordinary case
            }
            double fraction = 0.0;
            if (headroom > ScientificNotation.zero)
            {
                fraction = (headroom / intended).Standard();
                if (fraction < 0.0)
                {
                    fraction = 0.0;
                }
                else if (fraction > 1.0)
                {
                    fraction = 1.0;
                }
            }
            batch.amount = batch.amount * fraction;
            TopOffState.clipFraction = fraction;
            __state.clipped = true;
            __state.type = batch.type;
            __state.ceiling = ceiling;
        }
        catch (Exception e)
        {
            TopOffPlugin.Log.LogWarning(
                "Top-off could not fit the delivery to the headroom, letting the "
                + "game handle this one: " + e.Message);
        }
    }

    public static void Postfix(ResourceStorage __instance, TopOffAddState __state)
    {
        if (!__state.clipped)
        {
            return;
        }
        try
        {
            // Land exactly on the ceiling. The scaled batch above gets there to
            // within floating-point rounding, and "within rounding" is not good
            // enough: a tank a hair below the line reads as not full, and the
            // collector would spend a tick of energy chasing that hair on every
            // tick from then on.
            ElementBatch held;
            if (__instance.elementDict != null
                && __instance.elementDict.TryGetValue(__state.type, out held)
                && held != null)
            {
                held.amount = __state.ceiling;
            }
        }
        catch (Exception e)
        {
            TopOffPlugin.Log.LogWarning(
                "Top-off could not settle the tank on the line: " + e.Message);
        }
    }
}

// Step 3. Keep the bar. Also the gate that steps 1 and 2 hang off.
public struct TopOffTickState
{
    public bool active;
    public double bar;
}

[HarmonyPatch(typeof(ElementSource), "Tick")]
public static class ElementSourceKeepBarPatch
{
    // __0 is offlineSimulationSpeed. Taken by position rather than by name
    // because the parameter name is metadata the game is free to change.
    public static void Prefix(ElementSource __instance, int __0,
        out TopOffTickState __state)
    {
        __state = default(TopOffTickState);
        TopOffState.online = false;
        TopOffState.clipFraction = 1.0;
        if (__0 != 1 || !TopOffState.Enabled())
        {
            return; // offline runs on the game's own rules, untouched
        }
        TopOffState.online = true;
        if (__instance.outputPercentage == 0.0)
        {
            return; // switched off; Tick's own first test handles it
        }
        __state.active = true;
        __state.bar = __instance.filledPercentage;
    }

    public static void Postfix(ElementSource __instance, TopOffTickState __state)
    {
        TopOffState.online = false;
        if (!__state.active)
        {
            return;
        }
        try
        {
            // filledPercentage == 0.0 on the way out identifies the refusal
            // branch exactly. The only other early return leaves the bar alone,
            // and every path that reaches AddProgress leaves it above zero
            // (progress and outputPercentage are both non-zero by then).
            if (__instance.filledPercentage == 0.0)
            {
                if (__state.bar > 0.0 && __state.bar < 1.0)
                {
                    // No room. Wait, rather than burn what was already paid
                    // for. A bar at or past 1.0 has already been cashed in, so
                    // that one is left at zero.
                    __instance.filledPercentage = __state.bar;
                }
                return;
            }
            double fraction = TopOffState.clipFraction;
            if (fraction >= 0.999)
            {
                return; // the whole batch fit, or near enough that this is rounding
            }
            // Only part of the batch was delivered, so only that part of the
            // bar has been spent. The rest stays banked and the next delivery
            // is that much closer.
            double keep = 1.0 - fraction;
            if (keep > 0.999)
            {
                keep = 0.999; // never a whole bar, or AddProgress would reset it
            }
            __instance.filledPercentage = keep;
        }
        catch (Exception e)
        {
            TopOffPlugin.Log.LogWarning(
                "Top-off could not carry the bar over: " + e.Message);
        }
    }
}

// Fuzzied: "I want it in the Setting as a toggle there too". Same hook and the
// same template as the dev console, tooltip duration and zone key rows:
// SettingsManager.hideCardControlUIToggle is a plain labelled Toggle sitting in
// the settings list, so cloning it inherits the game's own styling for free.
// LoadSystemSettings rather than Awake, because that method early-returns
// unless the real rows are already wired up.
//
// NOTE, and it has bitten this codebase twice: replace onValueChanged BEFORE
// writing isOn. Object.Instantiate copies the serialized listeners, so setting
// the state first fires the TEMPLATE's callback.
[HarmonyPatch(typeof(SettingsManager), "LoadSystemSettings")]
public static class AddTopOffRowPatch
{
    private const string CLONE_NAME = "CommunityTopOffRow";

    private static readonly FieldInfo HideCardUiToggleField =
        AccessTools.Field(typeof(SettingsManager), "hideCardControlUIToggle");

    public static void Postfix(SettingsManager __instance)
    {
        try
        {
            if (HideCardUiToggleField == null || TopOffPlugin.cfgEnabled == null)
            {
                return;
            }
            Toggle template = (Toggle)HideCardUiToggleField.GetValue(__instance);
            if (template == null || template.transform.parent == null)
            {
                return;
            }
            Transform parent = template.transform.parent;
            if (CommunitySettings.AlreadyAdded(template, CLONE_NAME))
            {
                return; // LoadSystemSettings can run again; only ever one row
            }

            GameObject clone = UnityEngine.Object.Instantiate(
                template.gameObject, parent);
            clone.name = CLONE_NAME;

            // Into the mod's own section at the foot of the column, under
            // Language. Fuzzied asked for every setting the mod adds to sit
            // together where it can be told apart from the game's own, and a
            // vanilla sized row is too small to read on a wide monitor
            // anyway. False means the panel is not the shape this was
            // written against: the row then stays beside the one it was
            // cloned from, which is where it used to live. A row in the
            // wrong group still works; no row at all does not.
            if (!CommunitySettings.Adopt(__instance, template, clone,
                    TopOffPlugin.Log))
            {
                clone.transform.SetSiblingIndex(
                    template.transform.GetSiblingIndex() + 1);
                if (parent.GetComponent<LayoutGroup>() == null)
                {
                    // No layout group to place it, so it has to be positioned
                    // by hand. The multiplier is this row's place in the queue
                    // of rows the mod adds: dev console 1, tooltip duration 2,
                    // zone keys 3.
                    RectTransform src = template.GetComponent<RectTransform>();
                    RectTransform rt = clone.GetComponent<RectTransform>();
                    rt.anchorMin = src.anchorMin;
                    rt.anchorMax = src.anchorMax;
                    rt.pivot = src.pivot;
                    rt.sizeDelta = src.sizeDelta;
                    rt.anchoredPosition = src.anchoredPosition
                        - new Vector2(0f, (src.rect.height + 6f) * 4f);
                }
            }

            Toggle toggle = clone.GetComponent<Toggle>();
            if (toggle == null)
            {
                UnityEngine.Object.Destroy(clone);
                return;
            }
            // Listeners first, state second - see the note above.
            toggle.onValueChanged = new Toggle.ToggleEvent();
            toggle.isOn = TopOffPlugin.cfgEnabled.Value;
            toggle.onValueChanged.AddListener(OnClicked);

            LangText label = clone.GetComponentInChildren<LangText>(true);
            if (label != null)
            {
                // SetText writes the string straight through, unlike
                // SetLocalisedText which treats it as a localisation path.
                label.SetText("Collectors and plants fill the tank right up");
            }

            TooltipComposite tip = clone.GetComponent<TooltipComposite>();
            if (tip == null) { tip = clone.AddComponent<TooltipComposite>(); }
            tip.defaultTooltip = "Resource collectors and energy plants run "
                + "until the tank actually fills. The base game stops them a "
                + "whole bar's worth short of it and wipes the progress they "
                + "had already paid for, so they park below full and quietly "
                + "waste what they spent waiting. Energy plants have it worse: "
                + "a plant whose bar is worth more than your whole energy tank "
                + "never runs at all, not even on an empty tank. With this on, "
                + "anything with nowhere to put its output simply pauses and "
                + "keeps its bar, and a delivery that does not fit is trimmed "
                + "to the room available rather than thrown away. Sitting on "
                + "a full tank costs nothing either way.";

            TopOffPlugin.Log.LogInfo("Settings: added the top-off row (on="
                + TopOffPlugin.cfgEnabled.Value + ")");
        }
        catch (Exception e)
        {
            TopOffPlugin.Log.LogWarning(
                "Could not add the top-off row: " + e.Message);
        }
    }

    private static void OnClicked(bool isOn)
    {
        try
        {
            // Every patch reads cfgEnabled on the tick, so this takes effect
            // immediately. Switching it off mid-pause leaves a collector
            // holding a part-filled bar, which the game then wipes the next
            // time it refuses one - the base game's behaviour, which is what
            // switching it off asks for.
            TopOffPlugin.cfgEnabled.Value = isOn;
            TopOffPlugin.Log.LogInfo("Collectors fill the tank right up: " + isOn);
        }
        catch (Exception e)
        {
            TopOffPlugin.Log.LogWarning("Top-off row click failed: " + e.Message);
        }
    }
}


// ===========================================================================
// v1.2.0: the same three steps for energy plants
// ===========================================================================
//
// Kept apart from TopOffState rather than folded into it. ElementSource.Tick
// and EnergySource.Tick are separate walks of the priority list and neither is
// nested inside the other, but sharing one flag would mean a leaked flag from
// one kind of source silently changing the arithmetic for the other. Two
// flags, two owners, no interaction.
public static class TopOffEnergyState
{
    // True only for the duration of one online EnergySource.Tick. Both patched
    // methods below are reached from there and are gated on it, which keeps
    // this out of the offline simulation and out of every other caller of
    // AddEnergy - battle rewards, research payouts, the Big Bang, all of which
    // are perfectly entitled to overflow and discard.
    internal static bool online;

    // What fraction of the last delivery fit. 1.0 is the ordinary case and the
    // value the Tick prefix resets to.
    internal static double clipFraction;

    // Whether the full-tank guard was consulted during this Tick, and what it
    // answered. Tick has two ways to produce nothing and they look the same
    // from outside, so this is what tells them apart.
    internal static bool guardAsked;

    internal static bool guardFull;

    // What EnergySource.ExchangeResourceForEnergySourceProgress was asked
    // for and what it gave back. That method is where the second kind of
    // idle is decided and every number in it is private, so it is read off
    // a postfix rather than guessed at from outside.
    internal static bool exchangeSeen;

    internal static double exchangeResult;

    internal static double exchangeAccumReq;

    internal static ElementBatch exchangeWanted;

    // When each plant last had its silence explained, so the log gets one
    // line a minute per plant instead of one every tick.
    private static readonly Dictionary<string, float> lastSaid =
        new Dictionary<string, float>();

    internal static bool MaySay(string key)
    {
        if (TopOffPlugin.cfgExplain == null || !TopOffPlugin.cfgExplain.Value)
        {
            return false;
        }
        float now = Time.realtimeSinceStartup;
        float last;
        if (lastSaid.TryGetValue(key, out last) && now - last < 60f)
        {
            return false;
        }
        lastSaid[key] = now;
        return true;
    }

    internal static bool Enabled()
    {
        return TopOffPlugin.cfgEnabled != null && TopOffPlugin.cfgEnabled.Value
            && TopOffPlugin.cfgEnergy != null && TopOffPlugin.cfgEnergy.Value;
    }
}

// Step 1. "Would one more bar overflow" becomes "is the tank full".
//
// EnergySource.Tick is the only caller of this in the game, so writing
// __result is as surgical as a transpiler and much easier to read. The free
// plants never reach this test at all - Tick's own second condition exempts
// them - so nothing here changes for solar.
[HarmonyPatch(typeof(Player), "IsEnergyFullWithOutput")]
public static class EnergyFullGuardPatch
{
    public static bool Prefix(Player __instance, ref bool __result)
    {
        if (!TopOffEnergyState.online || !TopOffEnergyState.Enabled())
        {
            return true;
        }
        try
        {
            if (__instance == null || __instance.energy == null)
            {
                return true;
            }
            __result = __instance.energy.amount >= Energy.cap;
            TopOffEnergyState.guardAsked = true;
            TopOffEnergyState.guardFull = __result;
            return false;
        }
        catch (Exception e)
        {
            TopOffPlugin.Log.LogWarning(
                "Top-off could not measure the energy headroom, using the "
                + "game's own check for this tick: " + e.Message);
            return true;
        }
    }
}

public struct TopOffEnergyAddState
{
    public bool clipped;
}

// Step 2. Trim a delivery that does not fit down to the headroom, then settle
// the tank exactly on the cap.
//
// The alternative was to let NormalizeEnergy clip it afterwards, which is what
// the game does now. That books the difference as ERIOSource.discard and tells
// the player they are throwing energy away, when the whole point of step 3 is
// that they are not: the rest is still on the bar.
[HarmonyPatch(typeof(Player), "AddEnergy")]
public static class AddEnergyClipPatch
{
    // __0 is amount, __1 is offlineSimulationSpeed. Taken by position because
    // parameter names are metadata the game is free to change.
    public static void Prefix(Player __instance, ref ScientificNotation __0,
        int __1, out TopOffEnergyAddState __state)
    {
        __state = default(TopOffEnergyAddState);
        if (!TopOffEnergyState.online || !TopOffEnergyState.Enabled()
            || __1 != 1 || __instance == null || __instance.energy == null)
        {
            return;
        }
        try
        {
            // What the tank is about to gain, which is what AddEnergy computes
            // through CalculateEnergy and then returns as outputAmount.
            ScientificNotation intended = __0 * Modifiers.energyMul;
            if (intended <= ScientificNotation.zero)
            {
                return;
            }
            ScientificNotation headroom = Energy.cap - __instance.energy.amount;
            if (headroom >= intended)
            {
                return; // it all fits; the ordinary case
            }
            double fraction = 0.0;
            if (headroom > ScientificNotation.zero)
            {
                fraction = (headroom / intended).Standard();
                if (fraction < 0.0)
                {
                    fraction = 0.0;
                }
                else if (fraction > 1.0)
                {
                    fraction = 1.0;
                }
            }
            __0 = __0 * fraction;
            TopOffEnergyState.clipFraction = fraction;
            __state.clipped = true;
        }
        catch (Exception e)
        {
            TopOffPlugin.Log.LogWarning(
                "Top-off could not fit the energy to the headroom, letting the "
                + "game handle this one: " + e.Message);
        }
    }

    public static void Postfix(Player __instance, TopOffEnergyAddState __state)
    {
        if (!__state.clipped)
        {
            return;
        }
        try
        {
            // Land exactly on the cap. The scaled amount above gets there to
            // within floating-point rounding, and a tank a hair under the cap
            // reads as not full, so the plant would burn a tick of biomass or
            // gas chasing that hair on every tick from then on.
            if (__instance != null && __instance.energy != null)
            {
                __instance.energy.amount = Energy.cap;
            }
        }
        catch (Exception e)
        {
            TopOffPlugin.Log.LogWarning(
                "Top-off could not settle the energy tank on the cap: "
                + e.Message);
        }
    }
}

// Step 3. Keep the bar. Also the gate that steps 1 and 2 hang off.
// Read-only. The one place the second kind of idle is decided, and all of
// its inputs are private to it, so they are copied out here rather than
// reconstructed from outside where they could quietly disagree.
[HarmonyPatch(typeof(EnergySource), "ExchangeResourceForEnergySourceProgress")]
public static class EnergyExchangeProbePatch
{
    // __0 requirement, __1 resourceAccumulationReq, __2 offlineSimulationSpeed.
    public static void Postfix(ElementBatch __0, double __1, double __result)
    {
        if (!TopOffEnergyState.online)
        {
            return;
        }
        TopOffEnergyState.exchangeSeen = true;
        TopOffEnergyState.exchangeResult = __result;
        TopOffEnergyState.exchangeAccumReq = __1;
        TopOffEnergyState.exchangeWanted = __0;
    }
}

// The fourth kind of idle, and the only one that leaves no trace.
//
// PriorityJobManager.RunJobs counts every production job under the
// accumulation layer above it, and past the parallel task count it calls
// SetIOOff and skips Tick with a continue. Free plants never count towards
// that budget, which is why solar can keep running while a paid plant two
// rows down goes quiet. No patch on Tick can see this, because Tick is
// never called.
[HarmonyPatch(typeof(EnergySource), "SetIOOff")]
public static class EnergySourceIOOffPatch
{
    public static void Postfix(EnergySource __instance)
    {
        EnergySourceKeepBarPatch.Announce(__instance,
            "the priority list switched it off before it ran, so it is "
            + "past the parallel task count on the accumulation layer "
            + "above it. Move it higher in the list or raise that count");
    }
}

// An energy plant that is switched on but produces nothing looks exactly the
// same in the game whichever of the two reasons it is, and the two have
// nothing to do with each other: one is the tank, one is the supply chain.
// Saying which one it is in the log turns a guess into an answer.
[HarmonyPatch(typeof(EnergySource), "Tick")]
public static class EnergySourceKeepBarPatch
{
    // __0 is offlineSimulationSpeed.
    public static void Prefix(EnergySource __instance, int __0,
        out TopOffTickState __state)
    {
        __state = default(TopOffTickState);
        TopOffEnergyState.online = false;
        TopOffEnergyState.clipFraction = 1.0;
        TopOffEnergyState.guardAsked = false;
        TopOffEnergyState.guardFull = false;
        TopOffEnergyState.exchangeSeen = false;
        TopOffEnergyState.exchangeResult = 0.0;
        TopOffEnergyState.exchangeAccumReq = 0.0;
        TopOffEnergyState.exchangeWanted = null;
        if (__0 != 1 || !TopOffEnergyState.Enabled())
        {
            return; // offline runs on the game's own rules, untouched
        }
        TopOffEnergyState.online = true;
        if (__instance.outputPercentage == 0.0)
        {
            // Switched off. Tick's own first test handles the behaviour, but
            // this is the third way a plant can show no output, no input and
            // an empty bar, and it is indistinguishable from the other two
            // without being told.
            Announce(__instance, "its output slider is at 0, so it is "
                + "switched off rather than blocked by anything");
            return;
        }
        __state.active = true;
        __state.bar = __instance.filledPercentage;
    }

    // Everything ExchangeResourceForEnergySourceProgress weighs up, in the
    // order it weighs it: what the plant wants for one tick, what is in the
    // tank, and the floor under that tank made of the lock slider and the
    // accumulation layer, whichever is higher. available = amount - cap *
    // floor, and the plant gets nothing the moment that goes non-positive.
    private static string DescribeResource(EnergySource __instance)
    {
        try
        {
            ElementBatch need = __instance.baseBarFillRequiredResource;
            if (need == null)
            {
                return "free plant, it needs no resource at all";
            }
            string text = "needs " + need.amount + " of " + need.type
                + " per bar (after prodResourceCostReduction "
                + Modifiers.prodResourceCostReduction + ")";
            text += ", storage holds "
                + Player.shared.storage.GetAmount(need.type)
                + " of a " + ResourceStorage.cap + " cap";
            try
            {
                double locked = MainPanel.shared.storagePanel
                    .resourceUnitDict[need.type].GetLockValue();
                text += ", lock slider " + locked.ToString("0.####");
            }
            catch (Exception)
            {
                text += ", lock slider unreadable";
            }
            if (!TopOffEnergyState.exchangeSeen)
            {
                return text + ". The exchange was never reached, so the "
                    + "full-tank guard at the top of Tick is what stopped it.";
            }
            ScientificNotation wanted = (TopOffEnergyState.exchangeWanted == null)
                ? ScientificNotation.zero
                : TopOffEnergyState.exchangeWanted.amount;
            return text + ". The exchange asked for " + wanted
                + " this tick against an accumulation floor of "
                + TopOffEnergyState.exchangeAccumReq.ToString("0.#####")
                + " and got back "
                + TopOffEnergyState.exchangeResult.ToString("0.#####") + ".";
        }
        catch (Exception e)
        {
            return "could not be read: " + e.Message;
        }
    }

    private static void Explain(EnergySource __instance, double bar, string why)
    {
        try
        {
            string key = __instance.starId + "/" + __instance.id;
            if (!TopOffEnergyState.MaySay(key))
            {
                return;
            }
            ScientificNotation perBar = __instance.baseEnergyOutputPerFill
                * Modifiers.energyMul;
            TopOffPlugin.Log.LogInfo(
                "Energy plant " + key + " is on but produced nothing: " + why
                + ". Tank " + Player.shared.energy.amount + " of " + Energy.cap
                + ", one full bar of this plant would add " + perBar
                + ", its own bar is at " + bar.ToString("0.###")
                + ", output set to " + __instance.outputPercentage.ToString("0.###")
                + ", energyMul " + Modifiers.energyMul
                + ", eProdSpeed " + Modifiers.eProdSpeed);
            TopOffPlugin.Log.LogInfo("Energy plant " + key + " resource side: "
                + DescribeResource(__instance));
        }
        catch (Exception e)
        {
            TopOffPlugin.Log.LogWarning(
                "Top-off could not explain an idle energy plant: " + e.Message);
        }
    }

    internal static void Announce(EnergySource __instance, string why)
    {
        try
        {
            string key = __instance.starId + "/" + __instance.id;
            if (!TopOffEnergyState.MaySay(key))
            {
                return;
            }
            TopOffPlugin.Log.LogInfo("Energy plant " + key
                + " is producing nothing: " + why + ". Its bar is at "
                + __instance.filledPercentage.ToString("0.###") + ".");
        }
        catch (Exception e)
        {
            TopOffPlugin.Log.LogWarning(
                "Top-off could not explain an idle energy plant: " + e.Message);
        }
    }

    public static void Postfix(EnergySource __instance, TopOffTickState __state)
    {
        TopOffEnergyState.online = false;
        if (!__state.active)
        {
            return;
        }
        try
        {
            // filledPercentage == 0.0 on the way out identifies the refusal
            // branch exactly. The other early return, where the plant could not
            // buy any progress with its resource, leaves the bar alone, and
            // every path that reaches AddProgress leaves it above zero.
            if (__instance.filledPercentage == 0.0)
            {
                Explain(__instance, __state.bar,
                    (TopOffEnergyState.guardAsked && TopOffEnergyState.guardFull)
                        ? "the energy tank is already full"
                        : "it could not buy any progress, so its input resource "
                          + "is empty or held back by a lock or an accumulation line");
                if (__state.bar > 0.0 && __state.bar < 1.0)
                {
                    // Tank full. Wait, rather than burn what was already paid
                    // for. A bar at or past 1.0 has already been cashed in.
                    __instance.filledPercentage = __state.bar;
                }
                return;
            }
            if (TopOffEnergyState.exchangeSeen
                && TopOffEnergyState.exchangeResult == 0.0)
            {
                // The exchange bought nothing, so Tick returned before it
                // reached AddProgress. Comparing the bar against its entry
                // value looked equivalent and is not: AddProgress resets a
                // bar at or past 1.0 to zero and then adds to it, so a plant
                // that completes a full bar every tick comes out on the same
                // value it went in on while producing perfectly well. Solar
                // does exactly that, and the log called it idle.
                Explain(__instance, __state.bar,
                    "it could not buy any progress, so its input resource is "
                    + "empty or held back by a lock or an accumulation line");
                return;
            }
            double fraction = TopOffEnergyState.clipFraction;
            if (fraction >= 0.999)
            {
                return; // it all fit, or near enough that this is rounding
            }
            // Only part of the bar was delivered, so only that part has been
            // spent. This is what makes a plant whose bar is worth more than
            // the whole tank useful instead of dead: it tops the tank up, banks
            // the rest, and is ready to top it up again the moment you spend
            // something.
            double keep = 1.0 - fraction;
            if (keep > 0.999)
            {
                keep = 0.999; // never a whole bar, or AddProgress would reset it
            }
            __instance.filledPercentage = keep;
        }
        catch (Exception e)
        {
            TopOffPlugin.Log.LogWarning(
                "Top-off could not carry the energy bar over: " + e.Message);
        }
    }
}
