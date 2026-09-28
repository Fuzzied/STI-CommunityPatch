// Space Travel Idle community mod - stop the development console popping up
//
// The beta and beta-danger Steam branches, and every Mac build, are Unity
// DEVELOPMENT builds. Their SpaceTravelIdle_Data\boot.config carries the
// player-connection block that only development builds get (it even ships
// the developer's LAN IP), which means Unity's "Development Console"
// overlay is compiled in. Any engine error at all, most commonly FMOD
// failing to follow a change of audio device, throws a black console box
// across the middle of the screen with red stack text on it. It cannot be
// dismissed permanently and it comes straight back on the next error.
//
// Corrected 28.09.2026: this used to say the community build is the
// development build. The community files are the public build's files
// with a few pieces changed, and Windows public is a release build, where
// the console never shows and this plugin has nothing to hide. See
// docs\game-versions.md.
//
// None of that is useful to a player, so this adds a toggle to the game's
// own settings panel ("Hide error console", on by default) and, while it
// is on, keeps Debug.developerConsoleVisible pinned to false.
//
// What this CANNOT do: the development-build flag itself is baked into
// UnityPlayer.dll at build time, so the small "Development Build" corner
// watermark is drawn by the engine and there is no public API to remove
// it. Only the console overlay is suppressible at runtime.
//
// The toggle is made by cloning the vanilla "hide card control UI" toggle,
// the same approach AlchemyQoL uses for its extra forge buttons: clone the
// GameObject, replace the whole onValueChanged event (Instantiate copies
// the serialized listeners too, so the copy would otherwise still call
// SettingsManager.SetCardHideControlUIs), then re-label it.
//
// Compiled with the .NET Framework 4 csc against the game's own
// Assembly-CSharp.dll and the BepInEx 5 core DLLs (see build_plugin.ps1).

using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

[BepInPlugin("sti.community.devconsole", "STI Community Dev Console", "1.1.1")]
public class DevConsoleOffPlugin : BaseUnityPlugin
{
    // PlayerPrefs is what the game's own settings use, so the choice
    // survives a restart the same way fullscreen and card-UI hiding do.
    public const string PREF_KEY = "communityHideDevConsole";

    internal static ManualLogSource Log;

    public static bool HideConsole
    {
        get { return PlayerPrefs.GetInt(PREF_KEY, 1) == 1; }
        set
        {
            PlayerPrefs.SetInt(PREF_KEY, value ? 1 : 0);
            if (!value)
            {
                return;
            }
            Debug.developerConsoleVisible = false;
        }
    }

    private void Awake()
    {
        Log = Logger;
        Harmony harmony = new Harmony("sti.community.devconsole");
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        Logger.LogInfo("Dev console suppression active (hide="
            + HideConsole + "); toggle lives in the settings panel");
    }

    private void Update()
    {
        // Unity re-shows the console on every new error, so this has to be
        // held down rather than set once. Reading the property first keeps
        // it to a comparison on the overwhelming majority of frames.
        if (!HideConsole)
        {
            return;
        }
        if (Debug.developerConsoleVisible)
        {
            Debug.developerConsoleVisible = false;
        }
    }
}

// SettingsManager.Awake calls LoadSystemSettings, which early-returns when
// the toggles are not wired up, so hooking the loader means we only ever
// build the row when there is a real row to clone.
[HarmonyPatch(typeof(SettingsManager), "LoadSystemSettings")]
public static class AddDevConsoleTogglePatch
{
    private const string CLONE_NAME = "CommunityHideDevConsoleToggle";

    private static readonly FieldInfo HideCardUiToggleField =
        AccessTools.Field(typeof(SettingsManager), "hideCardControlUIToggle");

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
                return; // LoadSystemSettings can run again; only clone once
            }

            GameObject clone = Object.Instantiate(template.gameObject, parent);
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
                    DevConsoleOffPlugin.Log))
            {
                clone.transform.SetSiblingIndex(
                    template.transform.GetSiblingIndex() + 1);

                // A settings list is usually laid out by a LayoutGroup, which
                // overrides anchoredPosition every frame; only position the
                // clone by hand when nothing else is going to.
                if (parent.GetComponent<LayoutGroup>() == null)
                {
                    RectTransform src = template.GetComponent<RectTransform>();
                    RectTransform rt = clone.GetComponent<RectTransform>();
                    rt.anchorMin = src.anchorMin;
                    rt.anchorMax = src.anchorMax;
                    rt.pivot = src.pivot;
                    rt.sizeDelta = src.sizeDelta;
                    rt.anchoredPosition = src.anchoredPosition
                        - new Vector2(0f, src.rect.height + 6f);
                }
            }

            Toggle toggle = clone.GetComponent<Toggle>();
            if (toggle == null)
            {
                Object.Destroy(clone);
                return;
            }
            // ORDER MATTERS, and v1.0.0 got it wrong. Instantiate copies the
            // serialized listener, so writing isOn first fired the TEMPLATE's
            // callback - SettingsManager.SetCardHideControlUIs - and silently
            // switched the player's card control UIs to hover-only. Drop the
            // copied listeners first, then set the state while nothing is
            // wired, then add ours.
            toggle.onValueChanged = new Toggle.ToggleEvent();
            toggle.isOn = DevConsoleOffPlugin.HideConsole;
            toggle.onValueChanged.AddListener(OnToggleChanged);

            LangText label = clone.GetComponentInChildren<LangText>(true);
            if (label != null)
            {
                label.SetLocalisedText("Hide error console");
            }

            // Fuzzied: "is it possible to have a small pop-up text explaining
            // what each toggle does?" TooltipComposite reads defaultTooltip
            // whenever the object carries no ITooltipAvailable of its own.
            TooltipComposite tip = clone.GetComponent<TooltipComposite>();
            if (tip == null) { tip = clone.AddComponent<TooltipComposite>(); }
            tip.defaultTooltip = "On the beta branches and on Mac the game "
                + "is a Unity development build, so any engine error at all "
                + "throws a black console box with red text across the "
                + "middle of the screen. Most often it happens when your "
                + "audio device changes. It cannot be dismissed for good and "
                + "comes straight back on the next one. This keeps it "
                + "hidden. On the normal Windows branch the box never "
                + "appears, so there this does nothing. It has nothing to do "
                + "with the small \"Development Build\" watermark in the "
                + "corner, which is baked into the engine and cannot be "
                + "removed.";

            DevConsoleOffPlugin.Log.LogInfo(
                "Settings: added 'Hide error console' toggle (on="
                + toggle.isOn + ")");
        }
        catch (System.Exception e)
        {
            // A throw here would repeat on every settings load, so it is
            // swallowed deliberately - worst case the toggle is missing and
            // the default (suppressed) still applies.
            DevConsoleOffPlugin.Log.LogWarning(
                "Could not add the dev console toggle: " + e.Message);
        }
    }

    private static void OnToggleChanged(bool isOn)
    {
        DevConsoleOffPlugin.HideConsole = isOn;
        DevConsoleOffPlugin.Log.LogInfo("Hide error console set to " + isOn);
    }
}
