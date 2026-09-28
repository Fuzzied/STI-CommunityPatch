// Space Travel Idle community mod - how long tooltips stay on screen
//
// Reported by Fuzzied: the pop-up describing a Big Bang upgrade "disappears
// after a couple of sec or so", and he asked for a setting, capped at 30
// seconds so a stuck tooltip can never become permanent.
//
// Vanilla behaviour, from Tooltip.cs:
//
//     public void PushTextSource(TooltipComposite comp)
//     {
//         Enable();
//         tooltipCompStack.Push(comp);
//         if (tooltipCompStack.Peek().dismissible)
//             StartCoroutine(DismissAfter5Secs());
//     }
//
// - five seconds, hardcoded, and it runs whether or not the pointer is still
// on the thing you are reading. Long descriptions are simply not readable in
// that time.
//
// This replaces PushTextSource with the same four lines, except the wait is
// the player's choice. It also calls StopAllCoroutines first, which fixes a
// second annoyance: in vanilla, moving onto a new item inherits whatever was
// left of the previous item's five seconds, so the second tooltip can vanish
// almost immediately. Tooltip runs no other coroutines, so stopping them all
// is safe.
//
// v1.1.0 - the tooltip was eating the frame rate, and the frame rate was
// eating clicks. Fuzzied: "Cannot remove cards again on Mercury, UI is frozen
// in perm bags no pop-up when hovering over cards either". Player.log for
// that session is 85,760 lines, and 3,796 of them are this:
//
//     Unable to add the requested character to font asset [zh-CN]'s atlas
//     texture. Please make the texture [zh-CN Atlas] readable.
//     ...24 lines of stack trace...
//     Tooltip:SetText ()
//     TooltipComposite:AttemptShowTooltip ()
//     TooltipComposite:Update ()
//
// Read the game's own code and it is doing this on purpose:
//
//     private void Update() { AttemptShowTooltip(); }        // TooltipComposite
//     ...
//     public void SetText()                                  // Tooltip
//     {
//         content.SetLocalisedText(...);
//         LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
//     }
//
// So for every frame a tooltip is on screen the game re-reads the string out
// of the localisation JSON, runs Regex.Unescape over it, hands it to TMP and
// forces an immediate layout rebuild - which re-parses the text and looks up
// every character. One character in the frame the game puts round every
// dismissible tooltip is in none of the loaded atlases, so TMP walks the
// fallback list down to zh-CN and zh-CN_bold, cannot write to either texture,
// and reports it. Twice a frame. Unity builds a stack trace for each one and
// writes it to Player.log synchronously, on disk.
//
// None of that is the mod's doing - the same warning shows up under
// SearchDropdown in the same log - but this mod made it worse: the tooltip
// used to clear itself after 5 seconds and now holds for the player's choice,
// which defaults to 10. Twice as long on screen is twice the writing.
//
// Why it froze the perm bags rather than just feeling slow is already written
// up in PATCH_NOTES under the message log: the game cancels a click as a
// scroll if the pointer drifts ten pixels between press and release, and at a
// few frames a second a steady hand always drifts that far.
//
// The fix is that the rebuild is only needed when the words change. The text
// is remembered, and a SetText that would lay out the same string on the same
// source is skipped. Nothing else is touched: AttemptShowTooltip still calls
// SetPos every frame, so the tooltip still follows the mouse, and the first
// frame of every tooltip - and every frame where the text really did change,
// which is what live numbers in a tooltip need - rebuilds as before.
//
// The setting is a row in the game's own settings panel, cloned from the
// "hide card control UI" toggle, used as a click-to-cycle button: 5 -> 10 ->
// 15 -> 20 -> 30 -> 5 seconds. The tick box is hidden because there is
// nothing binary about it; the label carries the value.
//
// NOTE for anyone cloning a Toggle in this codebase: replace onValueChanged
// BEFORE writing isOn. Object.Instantiate copies the serialized listeners, so
// setting the state first fires the TEMPLATE's callback - that is exactly how
// v1.0.0 of DevConsoleOff silently switched the player's card control UIs to
// hover-only.
//
// Compiled with the .NET Framework 4 csc against the game's own
// Assembly-CSharp.dll and the BepInEx 5 core DLLs (see build_plugin.ps1).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

[BepInPlugin("sti.community.tooltiptime", "STI Community Tooltip Time", "1.2.0")]
public class TooltipTimePlugin : BaseUnityPlugin
{
    public const string PREF_KEY = "communityTooltipSeconds";

    // Capped at 30 on Fuzzied's request: long enough to read anything in the
    // game, short enough that a tooltip left behind by a bug still clears.
    internal static readonly int[] CHOICES = new int[] { 5, 10, 15, 20, 30 };
    private const int DEFAULT_SECONDS = 10;

    internal static ManualLogSource Log;

    public static int Seconds
    {
        get
        {
            int stored = PlayerPrefs.GetInt(PREF_KEY, DEFAULT_SECONDS);
            for (int i = 0; i < CHOICES.Length; i++)
            {
                if (CHOICES[i] == stored)
                {
                    return stored;
                }
            }
            return DEFAULT_SECONDS; // anything unexpected falls back
        }
        set { PlayerPrefs.SetInt(PREF_KEY, value); }
    }

    public static int NextSeconds()
    {
        int now = Seconds;
        for (int i = 0; i < CHOICES.Length; i++)
        {
            if (CHOICES[i] == now)
            {
                return CHOICES[(i + 1) % CHOICES.Length];
            }
        }
        return DEFAULT_SECONDS;
    }

    private void Awake()
    {
        Log = Logger;
        Harmony harmony = new Harmony("sti.community.tooltiptime");
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        Logger.LogInfo("Tooltip time active: " + Seconds
            + "s; the setting lives in the settings panel");
    }
}

[HarmonyPatch(typeof(Tooltip), "PushTextSource")]
public static class TooltipHoldPatch
{
    private static bool warned;

    // Replaces the vanilla body. Returning false skips the original, which is
    // what drops the hardcoded five-second coroutine.
    public static bool Prefix(Tooltip __instance, TooltipComposite comp)
    {
        __instance.Enable();
        // A tooltip that is going back up has to lay itself out again even if
        // it says exactly what it said last time, because a rebuild while the
        // object was switched off did nothing.
        TooltipRebuildPatch.Forget();
        __instance.tooltipCompStack.Push(comp);
        try
        {
            if (comp != null && comp.dismissible)
            {
                // Vanilla leaves the previous item's timer running; this is
                // why a second tooltip could vanish almost at once.
                __instance.StopAllCoroutines();
                __instance.StartCoroutine(
                    DismissAfter(__instance, TooltipTimePlugin.Seconds));
            }
        }
        catch (Exception e)
        {
            if (!warned)
            {
                warned = true;
                TooltipTimePlugin.Log.LogWarning(
                    "Tooltip timer failed, tooltips will not auto-hide: " + e);
            }
        }
        return false;
    }

    private static IEnumerator DismissAfter(Tooltip tip, float seconds)
    {
        yield return new WaitForSeconds(seconds);
        if (tip != null)
        {
            tip.Disable();
        }
    }
}

// The whole point of v1.1.0. See the header.
[HarmonyPatch(typeof(Tooltip), "SetText")]
public static class TooltipRebuildPatch
{
    private static TooltipComposite lastSource;
    private static string lastText;
    private static int skipped;
    private static bool reported;

    // Called wherever the tooltip stops being drawn, so the next one lays
    // itself out from scratch rather than trusting what we remember.
    internal static void Forget()
    {
        lastSource = null;
        lastText = null;
    }

    public static bool Prefix(Tooltip __instance)
    {
        try
        {
            Stack<TooltipComposite> stack = __instance.tooltipCompStack;
            if (stack == null || stack.Count == 0)
            {
                // Vanilla does nothing here either.
                return false;
            }
            TooltipComposite top = stack.Peek();
            if (top == null)
            {
                Forget();
                return true;
            }
            if (ReferenceEquals(top, lastSource) && top.showingTooltip == lastText)
            {
                skipped++;
                if (!reported && skipped >= 1000)
                {
                    reported = true;
                    TooltipTimePlugin.Log.LogInfo("Tooltip: skipped 1000 "
                        + "identical layout rebuilds - that is 1000 frames of "
                        + "the game re-reading and re-measuring words that had "
                        + "not changed");
                }
                return false;
            }
            lastSource = top;
            lastText = top.showingTooltip;
        }
        catch (Exception e)
        {
            // Never let this be the reason a tooltip does not appear.
            Forget();
            if (!reported)
            {
                reported = true;
                TooltipTimePlugin.Log.LogWarning(
                    "Tooltip rebuild check failed, falling back to the game's "
                    + "own behaviour: " + e.Message);
            }
        }
        return true;
    }
}

// Disable() clears the stack and switches the object off, so whatever we
// remember about the last layout stops being true.
[HarmonyPatch(typeof(Tooltip), "Disable")]
public static class TooltipForgetOnDisablePatch
{
    public static void Postfix()
    {
        TooltipRebuildPatch.Forget();
    }
}

// Same hook and the same template as the dev-console toggle: LoadSystemSettings
// early-returns unless the real settings rows are wired up.
[HarmonyPatch(typeof(SettingsManager), "LoadSystemSettings")]
public static class AddTooltipTimeRowPatch
{
    private const string CLONE_NAME = "CommunityTooltipTimeRow";

    private static readonly FieldInfo HideCardUiToggleField =
        AccessTools.Field(typeof(SettingsManager), "hideCardControlUIToggle");

    private static Toggle row;
    private static LangText rowLabel;

    public static void Postfix(SettingsManager __instance)
    {
        try
        {
            if (HideCardUiToggleField == null)
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
                return; // only ever one row
            }

            GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, parent);
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
                    TooltipTimePlugin.Log))
            {
                clone.transform.SetSiblingIndex(
                    template.transform.GetSiblingIndex() + 1);
                if (parent.GetComponent<LayoutGroup>() == null)
                {
                    RectTransform src = template.GetComponent<RectTransform>();
                    RectTransform rt = clone.GetComponent<RectTransform>();
                    rt.anchorMin = src.anchorMin;
                    rt.anchorMax = src.anchorMax;
                    rt.pivot = src.pivot;
                    rt.sizeDelta = src.sizeDelta;
                    rt.anchoredPosition = src.anchoredPosition
                        - new Vector2(0f, (src.rect.height + 6f) * 2f);
                }
            }

            Toggle toggle = clone.GetComponent<Toggle>();
            if (toggle == null)
            {
                UnityEngine.Object.Destroy(clone);
                return;
            }
            // Drop the copied listeners FIRST - see the note at the top.
            toggle.onValueChanged = new Toggle.ToggleEvent();
            toggle.isOn = false;
            if (toggle.graphic != null)
            {
                // there is nothing on/off about a duration, so lose the tick
                toggle.graphic.enabled = false;
            }
            toggle.onValueChanged.AddListener(OnClicked);

            row = toggle;
            rowLabel = clone.GetComponentInChildren<LangText>(true);
            Relabel();

            // Fuzzied: "is it possible to have a small pop-up text explaining
            // what each toggle does?" TooltipComposite reads defaultTooltip
            // whenever the object carries no ITooltipAvailable of its own.
            TooltipComposite tip = clone.GetComponent<TooltipComposite>();
            if (tip == null) { tip = clone.AddComponent<TooltipComposite>(); }
            tip.defaultTooltip = "How long a tooltip stays on screen once it "
                + "has appeared. This row is a button rather than a switch: "
                + "clicking it steps to the next duration and starts again "
                + "at the shortest. The base game hides a tooltip after a "
                + "couple of seconds, which is not long enough to read a card "
                + "with a lot on it.";

            TooltipTimePlugin.Log.LogInfo("Settings: added tooltip time row ("
                + TooltipTimePlugin.Seconds + "s)");
        }
        catch (Exception e)
        {
            TooltipTimePlugin.Log.LogWarning(
                "Could not add the tooltip time row: " + e.Message);
        }
    }

    private static void OnClicked(bool ignored)
    {
        try
        {
            TooltipTimePlugin.Seconds = TooltipTimePlugin.NextSeconds();
            Relabel();
            if (row != null)
            {
                // put the (hidden) tick back without firing this again
                row.SetIsOnWithoutNotify(false);
            }
            TooltipTimePlugin.Log.LogInfo("Tooltip time set to "
                + TooltipTimePlugin.Seconds + "s");
        }
        catch (Exception e)
        {
            TooltipTimePlugin.Log.LogWarning("Tooltip time click failed: " + e.Message);
        }
    }

    private static void Relabel()
    {
        if (rowLabel != null)
        {
            // SetText writes the string straight through, unlike
            // SetLocalisedText which treats it as a localisation path.
            rowLabel.SetText("Tooltip time: " + TooltipTimePlugin.Seconds
                + "s  (click to change)");
        }
    }
}
