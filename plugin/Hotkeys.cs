// Space Travel Idle community mod - keyboard shortcuts
//
// Fuzzied asked for: change the active loadout, toggle auto-levelling, next and
// previous combat zone, direct jumps to zones 1-10 and 11-20, quick save and
// quick load - and then "what else?", which turned into menu navigation on the
// arrow keys, travel controls, merge-all and the battle controls.
//
// Everything here drives the game's own public methods, the same ones its
// buttons are wired to in the inspector. Nothing reimplements game logic:
//
//     menu           MenuBtn.OpenPanel()          (each button knows its panel)
//     tabs           UITab.toggle.isOn = true     (what a click on a tab does)
//     zone jump      BattlePanel.OnZoneIdSelect() (after moving its dropdown)
//     zone level     BattlePanel.LevelUp/Down()
//     auto           BattlePanel.SwitchAutoLeveling/SwitchAutoFight()
//     battle         BattlePanel.SkipTurn/AbortBattle()
//     loadouts       CardBag.LoadLoadoutToDeck/LoadLoadoutToPermSet()
//     merge          CardPanel.MergeAllBattleCards/MergeAllPermCards()
//     travel         SidePanel.DestGoUp/DestGoDown/TravelButtonClicked()
//     save           SaveLoadManager.SaveFile/LoadFile()
//
// Three things this has to get right, and they are the reason the file is as
// long as it is:
//
// 1. TYPING. Loadout names, save names, the bag filter and the zone dropdown's
// own search box are all input fields. Without a guard, typing "Boss deck 5"
// into a loadout name would jump you to combat zone 5 mid-word. Nothing is
// handled while an input field is selected.
//
// 2. AN EMPTY LOADOUT IS NOT A LOADOUT. CardBag.LoadLoadoutToDeck does
// deckLoadouts[idx].Copy() with no null check, so a never-saved slot throws;
// and a saved-but-empty slot would silently strip your deck. Both are refused
// with a message instead.
//
// 3. THE ZONE DROPDOWN CAN BE EMPTY. If the battle panel has never been
// opened, its dropdown has no options, SearchDropdown.stringValue returns null,
// and OnZoneIdSelect would set currentZoneId = null. So the selection is always
// verified before OnZoneIdSelect is called, with ReloadAvailableZones as a
// retry and BattleManager.ChangeZoneId as the fallback.
//
// Bindings live in BepInEx\config\sti.community.hotkeys.cfg, which BepInEx
// writes out on the first run with every action listed. The format is plain
// text: "F5", "Ctrl+M", "Ctrl+Shift+UpArrow", or empty to unbind. Key names are
// Unity KeyCode names (A, F1, UpArrow, Space, Backspace, Alpha1, Keypad5...).
//
// v1.1.0 - the zone keys wait for the Combat panel. Fuzzied: "Something suddenly
// changed my combat zone to 2 - pray to rngjesus boss fight, so im effectively
// not getting biomass". The zone bank has no modifier, by design and by the
// printed layout, so a bare 2 pressed anywhere in the game moved him into a
// boss zone and quietly stopped his biomass income. Nothing was broken: the
// keys did exactly what they say. The problem is that they did it while he was
// looking at another panel entirely, where a digit means nothing and a
// keystroke can be a slip.
//
// So the five keys that CHANGE WHICH ZONE YOU ARE IN - the two number banks,
// previous, next and safe - now do nothing unless the Combat panel is the one
// on screen. MenuPanel.isOn is public and BattlePanel extends MenuPanel, so
// the test is one field read. Everything else in the Combat group is left
// alone: skip turn, abandon, auto fight, auto levelling and the level up/down
// keys are all things you might reasonably fire off while watching another
// screen, and none of them costs you a zone.
//
// It is a toggle in the game's own settings, on Fuzzied's request, because
// somebody who uses the digits as a fast zone switcher from anywhere should
// not have to find a config file to get that back.
//
// Quick load and depart both need the key pressed TWICE, and the second press
// only counts between 0.3 and 2 seconds after the first. Fuzzied asked for the
// lower bound: two presses in the same twitch of a finger, or a key that
// repeats because it stuck, are as much of an accident as one press was. Quick
// load in particular runs the same LoadFile path that ate half an hour of his
// progress in 0.9.3, and a stray keypress must not be able to throw away the
// session.
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
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// A parsed binding: one key plus the modifiers that must be held, and no
// others. Exact matching is what keeps 1, Shift+1, Ctrl+1 and Ctrl+Shift+1
// four separate shortcuts.
internal class Binding
{
    public KeyCode key;
    public bool ctrl;
    public bool shift;
    public bool alt;
    public bool bound;
    public string label = "unbound";
}

// What a press of a two-press shortcut turned out to be.
internal enum ConfirmState
{
    Armed,   // first press: warn and wait
    TooSoon, // inside the dead time: a fumble, ignore it
    Go       // a real second press
}

// The modifier half on its own, used by the four digit banks so the config
// needs four lines instead of forty.
internal class Mods
{
    public bool ctrl;
    public bool shift;
    public bool alt;
    public bool bound;
    public string label = "unbound";
}

[BepInPlugin("sti.community.hotkeys", "STI Community Hotkeys", "1.2.0")]
public class HotkeysPlugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;

    // The window for the second press of the two shortcuts that can lose
    // progress. Below the minimum it is a fumble, above the maximum the first
    // press has been forgotten.
    private const float CONFIRM_MIN_SECONDS = 0.3f;
    private const float CONFIRM_MAX_SECONDS = 2f;

    // Quick save and quick load share the last manual slot, the twelfth one in
    // the system panel. It has to be a slot the game already knows about:
    // SaveLoadManager.SaveIdToIdx only understands "0".."11", "auto", "auto2"
    // and "departureAuto", and anything else indexes an array with -1.
    private const string QUICK_SLOT = "11";
    private const string QUICK_NAME = "QuickSave";

    private ConfigEntry<bool> cfgEnabled;

    private ConfigEntry<string> cfgHelp;
    private ConfigEntry<string> cfgMenuPrev;
    private ConfigEntry<string> cfgMenuNext;
    private ConfigEntry<string> cfgTabPrev;
    private ConfigEntry<string> cfgTabNext;

    private ConfigEntry<string> cfgZonePrev;
    private ConfigEntry<string> cfgZoneNext;
    private ConfigEntry<string> cfgZoneSafe;
    private ConfigEntry<string> cfgLevelUp;
    private ConfigEntry<string> cfgLevelDown;
    private ConfigEntry<string> cfgAutoLevel;
    private ConfigEntry<string> cfgAutoFight;
    private ConfigEntry<string> cfgSkipTurn;
    private ConfigEntry<string> cfgAbort;
    private ConfigEntry<string> cfgZoneBank1;
    private ConfigEntry<string> cfgZoneBank2;

    // Static because the settings row that flips it is a static Harmony patch.
    internal static ConfigEntry<bool> cfgZonesNeedPanel;

    private ConfigEntry<string> cfgDeckLoadouts;
    private ConfigEntry<string> cfgPermLoadouts;
    private ConfigEntry<string> cfgMergeBattle;
    private ConfigEntry<string> cfgMergePerm;

    private ConfigEntry<string> cfgDestUp;
    private ConfigEntry<string> cfgDestDown;
    private ConfigEntry<string> cfgDepart;

    private ConfigEntry<string> cfgQuickSave;
    private ConfigEntry<string> cfgQuickLoad;

    private Binding help, menuPrev, menuNext, tabPrev, tabNext;
    private Binding zonePrev, zoneNext, zoneSafe, levelUp, levelDown;
    private Binding autoLevel, autoFight, skipTurn, abort;
    private Binding mergeBattle, mergePerm;
    private Binding destUp, destDown, depart;
    private Binding quickSave, quickLoad;
    private Mods zoneBank1, zoneBank2, deckLoadouts, permLoadouts;

    private float departArmedAt;
    private float quickLoadArmedAt;

    private bool showHelp;
    private string helpText;

    // The overlay caches its text; the zone key row changes a line in it.
    private static HotkeysPlugin instance;

    internal static void RebuildHelp()
    {
        if (instance != null) { instance.helpText = null; }
    }
    private GUIStyle helpStyle;

    // One bad frame in Update is a bug; several in a row would spam the log
    // every frame forever, so the whole thing steps aside instead.
    private int failures;
    private bool disabled;

    private void Awake()
    {
        // 0.1.1: off if the player set it to false in STI Community Patch.cfg
        if (!CommunityToggle.On("hotkeys", Logger))
        {
            enabled = false;
            return;
        }
        Log = Logger;
        instance = this;

        // The zone key settings row is a Harmony patch, and until v1.2.0
        // nothing here ever applied one: the patch class was written, the row
        // never appeared, and there was nothing in the log to say so because
        // the Postfix was simply never called. Every other plugin that hooks
        // the game does this in its Awake; this one was the only pure Update
        // plugin, and grew a patch without growing the line that runs it.
        new Harmony("sti.community.hotkeys").PatchAll(
            Assembly.GetExecutingAssembly());

        cfgEnabled = Config.Bind("1 General", "Enabled", true,
            "Master switch. Set to false to turn every shortcut off.");

        cfgHelp = Config.Bind("1 General", "ShowHotkeyList", "F1",
            "Shows or hides the on-screen list of shortcuts.");

        // Up and down walk the left-hand menu, left and right walk the tabs
        // inside whichever panel that lands on - so the arrows match the way
        // the screen is actually laid out: the menu is a vertical column, the
        // tabs are a horizontal row.
        cfgMenuPrev = Config.Bind("2 Menu", "PreviousPanel", "UpArrow",
            "Previous menu panel - one step UP the left-hand menu.");
        cfgMenuNext = Config.Bind("2 Menu", "NextPanel", "DownArrow",
            "Next menu panel - one step DOWN the left-hand menu.");
        cfgTabPrev = Config.Bind("2 Menu", "PreviousTab", "LeftArrow",
            "Previous tab inside the panel that is open.");
        cfgTabNext = Config.Bind("2 Menu", "NextTab", "RightArrow",
            "Next tab inside the panel that is open.");

        cfgZonePrev = Config.Bind("3 Combat", "PreviousZone", "A",
            "Previous unlocked combat zone.");
        cfgZoneNext = Config.Bind("3 Combat", "NextZone", "D",
            "Next unlocked combat zone.");
        cfgZoneSafe = Config.Bind("3 Combat", "SafeZone", "Q",
            "Back to the safe zone.");
        cfgLevelUp = Config.Bind("3 Combat", "LevelUp", "W",
            "Combat zone level up.");
        cfgLevelDown = Config.Bind("3 Combat", "LevelDown", "S",
            "Combat zone level down.");
        cfgAutoLevel = Config.Bind("3 Combat", "ToggleAutoLevelling", "Z",
            "Turns automatic advancement to the next level on or off.");
        cfgAutoFight = Config.Bind("3 Combat", "ToggleAutoFight", "X",
            "Turns auto fight on or off.");
        cfgSkipTurn = Config.Bind("3 Combat", "SkipTurn", "F",
            "Skips your turn in the current battle.");
        cfgAbort = Config.Bind("3 Combat", "AbortBattle", "G",
            "Abandons the current battle.");
        cfgZoneBank1 = Config.Bind("3 Combat", "ZoneBank1Modifier", "",
            "Held with 1-9 and 0 to jump to combat zones 1-10. Empty means the "
            + "number keys on their own. Use None to switch this bank off.");
        cfgZoneBank2 = Config.Bind("3 Combat", "ZoneBank2Modifier", "Shift",
            "Held with 1-9 and 0 to jump to combat zones 11-20.");
        cfgZonesNeedPanel = Config.Bind("3 Combat", "ZoneKeysNeedCombatPanel",
            true,
            "The keys that change which combat zone you are in - the number "
            + "banks, previous, next and safe - only work while the Combat "
            + "panel is open. On by default: the number bank has no modifier, "
            + "so a stray digit anywhere else in the game would otherwise move "
            + "you, possibly into a boss zone. There is a toggle for this in "
            + "the game's settings panel.");

        cfgDeckLoadouts = Config.Bind("4 Loadouts", "DeckLoadoutModifier", "Ctrl",
            "Held with 1-9 and 0 to load battle deck loadouts 1-10.");
        cfgPermLoadouts = Config.Bind("4 Loadouts", "PermLoadoutModifier", "Ctrl+Shift",
            "Held with 1-9 and 0 to load permanent set loadouts 1-10.");

        cfgMergeBattle = Config.Bind("5 Cards", "MergeAllBattleCards", "M",
            "Merges every battle card that can be merged.");
        cfgMergePerm = Config.Bind("5 Cards", "MergeAllPermanentCards", "Shift+M",
            "Merges every permanent card that can be merged.");

        cfgDestUp = Config.Bind("6 Travel", "DestinationUp", "Ctrl+UpArrow",
            "Moves the travel destination one star outwards.");
        cfgDestDown = Config.Bind("6 Travel", "DestinationDown", "Ctrl+DownArrow",
            "Moves the travel destination one star inwards.");
        cfgDepart = Config.Bind("6 Travel", "Depart", "Ctrl+Return",
            "Departs, or reverses course while travelling. Press twice, with a "
            + "short pause between - departing is not something to do by "
            + "accident, and neither is a fumbled double-tap.");

        cfgQuickSave = Config.Bind("7 Save", "QuickSave", "F5",
            "Saves to slot 12 of the system panel, named QuickSave.");
        cfgQuickLoad = Config.Bind("7 Save", "QuickLoad", "F9",
            "Loads slot 12. Press twice, with a short pause between: this "
            + "throws away everything since the last quick save and cannot be "
            + "undone. A rapid double-tap is treated as one press.");

        ReadBindings();

        Logger.LogInfo("Hotkeys active; bindings are in "
            + "BepInEx\\config\\sti.community.hotkeys.cfg");
    }

    private void ReadBindings()
    {
        help = Parse(cfgHelp.Value);
        menuPrev = Parse(cfgMenuPrev.Value);
        menuNext = Parse(cfgMenuNext.Value);
        tabPrev = Parse(cfgTabPrev.Value);
        tabNext = Parse(cfgTabNext.Value);

        zonePrev = Parse(cfgZonePrev.Value);
        zoneNext = Parse(cfgZoneNext.Value);
        zoneSafe = Parse(cfgZoneSafe.Value);
        levelUp = Parse(cfgLevelUp.Value);
        levelDown = Parse(cfgLevelDown.Value);
        autoLevel = Parse(cfgAutoLevel.Value);
        autoFight = Parse(cfgAutoFight.Value);
        skipTurn = Parse(cfgSkipTurn.Value);
        abort = Parse(cfgAbort.Value);

        mergeBattle = Parse(cfgMergeBattle.Value);
        mergePerm = Parse(cfgMergePerm.Value);

        destUp = Parse(cfgDestUp.Value);
        destDown = Parse(cfgDestDown.Value);
        depart = Parse(cfgDepart.Value);

        quickSave = Parse(cfgQuickSave.Value);
        quickLoad = Parse(cfgQuickLoad.Value);

        zoneBank1 = ParseMods(cfgZoneBank1.Value);
        zoneBank2 = ParseMods(cfgZoneBank2.Value);
        deckLoadouts = ParseMods(cfgDeckLoadouts.Value);
        permLoadouts = ParseMods(cfgPermLoadouts.Value);

        helpText = null; // rebuilt from the new bindings on next display
    }

    // ---------------------------------------------------------------- parsing

    private static Binding Parse(string text)
    {
        Binding b = new Binding();
        if (text == null)
        {
            return b;
        }
        string trimmed = text.Trim();
        if (trimmed.Length == 0 || string.Equals(trimmed, "None",
            StringComparison.OrdinalIgnoreCase))
        {
            return b;
        }

        string[] parts = trimmed.Split('+');
        string keyName = null;
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i].Trim();
            if (p.Length == 0)
            {
                continue;
            }
            if (IsCtrl(p)) { b.ctrl = true; }
            else if (IsShift(p)) { b.shift = true; }
            else if (IsAlt(p)) { b.alt = true; }
            else { keyName = p; }
        }
        if (keyName == null)
        {
            Warn("no key in shortcut '" + trimmed + "', ignoring it");
            return b;
        }
        try
        {
            b.key = (KeyCode)Enum.Parse(typeof(KeyCode), keyName, true);
        }
        catch (Exception)
        {
            Warn("'" + keyName + "' is not a key name, ignoring '" + trimmed + "'");
            return b;
        }
        b.bound = true;
        b.label = Describe(b.ctrl, b.shift, b.alt) + keyName;
        return b;
    }

    private static Mods ParseMods(string text)
    {
        Mods m = new Mods();
        if (text == null)
        {
            return m;
        }
        string trimmed = text.Trim();
        if (string.Equals(trimmed, "None", StringComparison.OrdinalIgnoreCase))
        {
            return m; // deliberately switched off
        }
        // An empty string is a real answer here: it means the bare number keys.
        string[] parts = trimmed.Split('+');
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i].Trim();
            if (p.Length == 0) { continue; }
            if (IsCtrl(p)) { m.ctrl = true; }
            else if (IsShift(p)) { m.shift = true; }
            else if (IsAlt(p)) { m.alt = true; }
            else
            {
                Warn("'" + p + "' is not Ctrl, Shift or Alt, ignoring '"
                    + trimmed + "'");
                return m;
            }
        }
        m.bound = true;
        m.label = Describe(m.ctrl, m.shift, m.alt) + "1-9 and 0";
        return m;
    }

    private static bool IsCtrl(string s)
    {
        return string.Equals(s, "Ctrl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "Control", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsShift(string s)
    {
        return string.Equals(s, "Shift", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAlt(string s)
    {
        return string.Equals(s, "Alt", StringComparison.OrdinalIgnoreCase);
    }

    private static string Describe(bool ctrl, bool shift, bool alt)
    {
        string s = "";
        if (ctrl) { s += "Ctrl+"; }
        if (shift) { s += "Shift+"; }
        if (alt) { s += "Alt+"; }
        return s;
    }

    // ------------------------------------------------------------ key testing

    private static bool CtrlHeld()
    {
        return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
    }

    private static bool ShiftHeld()
    {
        return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
    }

    private static bool AltHeld()
    {
        return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
    }

    private static bool Down(Binding b)
    {
        if (b == null || !b.bound)
        {
            return false;
        }
        if (!Input.GetKeyDown(b.key))
        {
            return false;
        }
        return CtrlHeld() == b.ctrl && ShiftHeld() == b.shift && AltHeld() == b.alt;
    }

    private static bool ModsHeld(Mods m)
    {
        if (m == null || !m.bound)
        {
            return false;
        }
        return CtrlHeld() == m.ctrl && ShiftHeld() == m.shift && AltHeld() == m.alt;
    }

    // slot 0 is the "1" key, slot 9 is the "0" key - the order they sit on the
    // keyboard, which is the order Fuzzied asked for.
    private static bool DigitDown(int slot)
    {
        KeyCode alpha = (slot == 9)
            ? KeyCode.Alpha0 : (KeyCode)((int)KeyCode.Alpha1 + slot);
        KeyCode pad = (slot == 9)
            ? KeyCode.Keypad0 : (KeyCode)((int)KeyCode.Keypad1 + slot);
        return Input.GetKeyDown(alpha) || Input.GetKeyDown(pad);
    }

    // ------------------------------------------------------------------ input

    private void Update()
    {
        if (disabled || !cfgEnabled.Value)
        {
            return;
        }
        try
        {
            // The help list works everywhere, including the title screen.
            if (Down(help))
            {
                showHelp = !showHelp;
                return;
            }
            if (TypingInTextField())
            {
                return;
            }
            if (MainPanel.shared == null)
            {
                return; // not in the game scene yet
            }
            HandleKeys();
        }
        catch (Exception e)
        {
            failures++;
            Log.LogWarning("Hotkey handling failed (" + failures + "): " + e);
            if (failures >= 5)
            {
                disabled = true;
                Log.LogWarning("Too many failures; hotkeys have switched "
                    + "themselves off for this session.");
            }
        }
    }

    private void HandleKeys()
    {
        if (Down(menuPrev)) { CycleMenu(-1); return; }
        if (Down(menuNext)) { CycleMenu(1); return; }
        if (Down(tabPrev)) { CycleTab(-1); return; }
        if (Down(tabNext)) { CycleTab(1); return; }

        if (Down(zonePrev)) { if (ZoneKeysAllowed()) { StepZone(-1); } return; }
        if (Down(zoneNext)) { if (ZoneKeysAllowed()) { StepZone(1); } return; }
        if (Down(zoneSafe))
        {
            if (ZoneKeysAllowed())
            {
                SelectZone(BattleZoneLibrary.safeZoneId);
            }
            return;
        }
        if (Down(levelUp)) { ChangeLevel(1); return; }
        if (Down(levelDown)) { ChangeLevel(-1); return; }
        if (Down(autoLevel)) { ToggleAutoLevelling(); return; }
        if (Down(autoFight)) { ToggleAutoFight(); return; }
        if (Down(skipTurn)) { SkipTurn(); return; }
        if (Down(abort)) { AbortBattle(); return; }

        if (Down(mergeBattle)) { MergeBattleCards(); return; }
        if (Down(mergePerm)) { MergePermCards(); return; }

        if (Down(destUp)) { MoveDestination(1); return; }
        if (Down(destDown)) { MoveDestination(-1); return; }
        if (Down(depart)) { Depart(); return; }

        if (Down(quickSave)) { QuickSave(); return; }
        if (Down(quickLoad)) { QuickLoad(); return; }

        HandleDigits();
    }

    private void HandleDigits()
    {
        for (int slot = 0; slot < 10; slot++)
        {
            if (!DigitDown(slot))
            {
                continue;
            }
            int number = slot + 1; // 1..10
            // Most specific modifier combination first.
            if (ModsHeld(permLoadouts)) { LoadPermLoadout(number); }
            else if (ModsHeld(deckLoadouts)) { LoadDeckLoadout(number); }
            else if (ModsHeld(zoneBank2))
            {
                if (ZoneKeysAllowed()) { JumpToZone(number + 10); }
            }
            else if (ModsHeld(zoneBank1))
            {
                if (ZoneKeysAllowed()) { JumpToZone(number); }
            }
            return;
        }
    }

    // An input field being selected is enough to keep out - it does not have to
    // have the caret blinking in it. The zone dropdown's search box counts too.
    private static bool TypingInTextField()
    {
        EventSystem es = EventSystem.current;
        if (es == null)
        {
            return false;
        }
        GameObject go = es.currentSelectedGameObject;
        if (go == null)
        {
            return false;
        }
        if (go.GetComponent<TMP_InputField>() != null)
        {
            return true;
        }
        if (go.GetComponent<InputField>() != null)
        {
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------- menu

    private static readonly FieldInfo MenuBtnPanelField =
        AccessTools.Field(typeof(MenuBtn), "panel");
    private static readonly FieldInfo MainPanelPanelsField =
        AccessTools.Field(typeof(MainPanel), "panels");
    private static readonly FieldInfo TabPanelTabsField =
        AccessTools.Field(typeof(UITabPanel), "tabs");
    private static readonly FieldInfo ZoneDropdownField =
        AccessTools.Field(typeof(BattlePanel), "zoneDropdown");

    // The visible menu buttons, in the order they are drawn. Reading them from
    // the scene rather than hardcoding a list means locked panels are skipped
    // for free, and so is anything the game adds later.
    private static List<MenuBtn> MenuButtons()
    {
        List<MenuBtn> list = new List<MenuBtn>();
        MenuBtn[] found = UnityEngine.Object.FindObjectsOfType<MenuBtn>();
        for (int i = 0; i < found.Length; i++)
        {
            MenuBtn b = found[i];
            if (b != null && b.gameObject.activeInHierarchy)
            {
                list.Add(b);
            }
        }
        list.Sort(delegate(MenuBtn x, MenuBtn y)
        {
            return string.CompareOrdinal(OrderKey(x.transform), OrderKey(y.transform));
        });
        return list;
    }

    // FindObjectsOfType hands things back in no particular order, so sort on a
    // key built from the sibling index at every level of the hierarchy.
    private static string OrderKey(Transform t)
    {
        string key = "";
        while (t != null)
        {
            key = t.GetSiblingIndex().ToString("D4") + "/" + key;
            t = t.parent;
        }
        return key;
    }

    private static MenuPanel PanelOf(MenuBtn btn)
    {
        if (MenuBtnPanelField == null || btn == null)
        {
            return null;
        }
        return (MenuPanel)MenuBtnPanelField.GetValue(btn);
    }

    private static void CycleMenu(int step)
    {
        List<MenuBtn> btns = MenuButtons();
        if (btns.Count == 0)
        {
            return;
        }
        int current = -1;
        for (int i = 0; i < btns.Count; i++)
        {
            MenuPanel p = PanelOf(btns[i]);
            if (p != null && p.isOn)
            {
                current = i;
                break;
            }
        }
        int next;
        if (current < 0)
        {
            next = (step > 0) ? 0 : btns.Count - 1;
        }
        else
        {
            next = ((current + step) % btns.Count + btns.Count) % btns.Count;
        }
        btns[next].OpenPanel();
    }

    private static MenuPanel OpenPanel()
    {
        if (MainPanelPanelsField == null || MainPanel.shared == null)
        {
            return null;
        }
        List<MenuPanel> panels =
            (List<MenuPanel>)MainPanelPanelsField.GetValue(MainPanel.shared);
        if (panels == null)
        {
            return null;
        }
        for (int i = 0; i < panels.Count; i++)
        {
            if (panels[i] != null && panels[i].isOn)
            {
                return panels[i];
            }
        }
        return null;
    }

    private static void CycleTab(int step)
    {
        MenuPanel open = OpenPanel();
        if (open == null)
        {
            return;
        }
        List<Toggle> tabs = TabToggles(open);
        if (tabs == null || tabs.Count < 2)
        {
            return; // this panel has nothing to cycle
        }
        int current = 0;
        for (int i = 0; i < tabs.Count; i++)
        {
            if (tabs[i].isOn)
            {
                current = i;
                break;
            }
        }
        for (int hop = 1; hop <= tabs.Count; hop++)
        {
            int idx = ((current + step * hop) % tabs.Count + tabs.Count) % tabs.Count;
            Toggle tab = tabs[idx];
            if (tab == null || !tab.gameObject.activeInHierarchy || !tab.IsInteractable())
            {
                continue; // locked tab
            }
            // Setting isOn is what a click does: the toggle group turns the
            // others off and the panel swaps the content.
            tab.isOn = true;
            return;
        }
    }

    // Finding a panel's tab row is harder than it looks, and getting it wrong
    // is what made SPACESHIP ignore the arrow keys (Fuzzied, 2026-09-09).
    //
    // v1.0.2 only ever asked for a UITabPanel. But UITabPanel is used by
    // exactly two screens in the whole game - GameUpdatesPanel and HelpBook -
    // and none of the menu panels reference it. The menu panels hold their tabs
    // as their own private Toggle fields instead (StoragePanel.overviewTab /
    // summaryTab, TechnoPanel.productionTab / researchTab / infraTab /
    // priorityTab, SpaceshipPanel.engineTab, BigBangPanel.bigBangTab /
    // upgradesTab, StatsPanel.statsTab / modifiersTab, and six on CardPanel).
    //
    // So we look for the panel's own Toggle fields, then take the row they sit
    // in: the tabs of a panel are siblings under one container, in the order
    // they are drawn. That also picks up tabs the panel class does not name -
    // SpaceshipPanel declares only engineTab, yet the screen has two tabs, and
    // the second one is what Fuzzied could not reach.
    private static readonly Dictionary<Type, Toggle[]> tabCache =
        new Dictionary<Type, Toggle[]>();

    private static List<Toggle> TabToggles(MenuPanel open)
    {
        List<Toggle> list = new List<Toggle>();

        // 1. A real UITabPanel, if this screen happens to use one.
        if (TabPanelTabsField != null)
        {
            UITabPanel tabPanel = open.GetComponentInChildren<UITabPanel>(true);
            if (tabPanel != null)
            {
                UITab[] tabs = (UITab[])TabPanelTabsField.GetValue(tabPanel);
                if (tabs != null)
                {
                    for (int i = 0; i < tabs.Length; i++)
                    {
                        if (tabs[i] != null && tabs[i].toggle != null)
                        {
                            list.Add(tabs[i].toggle);
                        }
                    }
                }
                if (list.Count >= 2)
                {
                    return list;
                }
                list.Clear();
            }
        }

        // 2. The panel's own tab row.
        Transform row = TabRow(open);
        if (row == null)
        {
            return null;
        }
        Toggle[] found = row.GetComponentsInChildren<Toggle>(true);
        for (int i = 0; i < found.Length; i++)
        {
            if (found[i] != null)
            {
                list.Add(found[i]);
            }
        }
        return list;
    }

    // The container the panel's declared tab toggles live under. Cached per
    // panel type - the fields never move, and this runs on every arrow press.
    private static Transform TabRow(MenuPanel open)
    {
        Type type = open.GetType();
        Toggle[] anchors;
        if (!tabCache.TryGetValue(type, out anchors))
        {
            List<Toggle> declared = new List<Toggle>();
            FieldInfo[] fields = type.GetFields(BindingFlags.Instance
                | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].FieldType != typeof(Toggle))
                {
                    continue;
                }
                if (fields[i].Name.IndexOf("Tab", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue; // a toggle, but not a tab (filters, switches)
                }
                Toggle t = fields[i].GetValue(open) as Toggle;
                if (t != null)
                {
                    declared.Add(t);
                }
            }
            anchors = declared.ToArray();
            tabCache[type] = anchors;
            HotkeysPlugin.Log.LogInfo("tabs: " + type.Name + " declares "
                + anchors.Length + " tab toggle(s)");
        }
        for (int i = 0; i < anchors.Length; i++)
        {
            if (anchors[i] != null && anchors[i].transform.parent != null)
            {
                return anchors[i].transform.parent;
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ zones

    // Index 0 is the safe zone, matching the list BattlePanel builds for its
    // own dropdown, so "combat zone 3" is simply index 3.
    private static List<string> ZoneIds()
    {
        List<string> ids = BattleZoneLibrary.GetAvailableZoneIds();
        if (ids == null)
        {
            return null;
        }
        ids.Insert(0, BattleZoneLibrary.safeZoneId);
        return ids;
    }

    // Only the keys that change which zone you are IN ask this. See the
    // header. BattlePanel extends MenuPanel, and MenuPanel.TurnOff sets isOn
    // false and deactivates the object, so isOn is exactly "on screen".
    private static bool ZoneKeysAllowed()
    {
        try
        {
            if (HotkeysPlugin.cfgZonesNeedPanel == null
                || !HotkeysPlugin.cfgZonesNeedPanel.Value)
            {
                return true;
            }
            if (MainPanel.shared == null) { return false; }
            BattlePanel panel = MainPanel.shared.battlePanel;
            return panel != null && panel.isOn;
        }
        catch (Exception e)
        {
            // A hotkey that cannot decide should still work.
            HotkeysPlugin.Log.LogWarning(
                "Could not tell whether the Combat panel is open: " + e.Message);
            return true;
        }
    }

    private static void JumpToZone(int number)
    {
        List<string> ids = ZoneIds();
        if (ids == null)
        {
            return;
        }
        if (number < 1 || number >= ids.Count)
        {
            Say("Combat zone " + number + " is not unlocked yet",
                MessageLevel.yellow_warning);
            return;
        }
        SelectZone(ids[number]);
    }

    private static void StepZone(int step)
    {
        List<string> ids = ZoneIds();
        if (ids == null || BattleManager.shared == null
            || BattleManager.shared.battleProgress == null)
        {
            return;
        }
        int current = ids.IndexOf(BattleManager.shared.battleProgress.currentZoneId);
        if (current < 0)
        {
            current = 0;
        }
        int next = current + step;
        if (next < 0 || next >= ids.Count)
        {
            Say((step > 0) ? "No further combat zone unlocked"
                           : "Already at the safe zone",
                MessageLevel.yellow_warning);
            return;
        }
        SelectZone(ids[next]);
    }

    // The dropdown is moved first and the result checked, because
    // OnZoneIdSelect reads the dropdown: if its options were never loaded,
    // stringValue is null and the game would end up with a null zone id.
    private static void SelectZone(string id)
    {
        bool done = false;
        BattlePanel panel = MainPanel.shared.battlePanel;
        if (panel != null && ZoneDropdownField != null)
        {
            SearchDropdown dd = (SearchDropdown)ZoneDropdownField.GetValue(panel);
            if (dd != null)
            {
                dd.SetOptionValue(id, false);
                if (dd.stringValue != id)
                {
                    panel.ReloadAvailableZones();
                    dd.SetOptionValue(id, false);
                }
                if (dd.stringValue == id)
                {
                    panel.OnZoneIdSelect();
                    done = true;
                }
            }
        }
        if (!done)
        {
            if (BattleManager.shared == null
                || BattleManager.shared.battleProgress == null)
            {
                return;
            }
            BattleManager.shared.ChangeZoneId(id);
        }
        Say(BattleZoneLibrary.GetBattleZoneName(id));
    }

    private static void ChangeLevel(int step)
    {
        BattlePanel panel = MainPanel.shared.battlePanel;
        if (panel == null)
        {
            return;
        }
        if (BattleManager.shared != null && BattleManager.shared.inSafeZone)
        {
            Say("Pick a combat zone first", MessageLevel.yellow_warning);
            return;
        }
        if (step > 0)
        {
            panel.LevelUp();
        }
        else
        {
            panel.LevelDown();
        }
    }

    private static void ToggleAutoLevelling()
    {
        BattlePanel panel = MainPanel.shared.battlePanel;
        if (panel == null || BattleManager.shared == null)
        {
            return;
        }
        panel.SwitchAutoLeveling();
        Say(BattleManager.shared.autoLevelingOn
            ? "Auto levelling on" : "Auto levelling off");
    }

    private static void ToggleAutoFight()
    {
        BattlePanel panel = MainPanel.shared.battlePanel;
        if (panel == null || BattleManager.shared == null)
        {
            return;
        }
        panel.SwitchAutoFight();
        Say(BattleManager.shared.autoFightOn ? "Auto fight on" : "Auto fight off");
    }

    private static void SkipTurn()
    {
        if (BattleManager.shared == null || !BattleManager.shared.inBattle)
        {
            return;
        }
        MainPanel.shared.battlePanel.SkipTurn();
    }

    private static void AbortBattle()
    {
        if (BattleManager.shared == null || !BattleManager.shared.inBattle)
        {
            return;
        }
        MainPanel.shared.battlePanel.AbortBattle();
        Say("Battle abandoned");
    }

    // --------------------------------------------------------------- loadouts

    private static void LoadDeckLoadout(int number)
    {
        Player player = Player.shared;
        if (player == null || player.cardBag == null)
        {
            return;
        }
        CardBag bag = player.cardBag;
        int idx = number - 1;
        if (idx < 0 || idx >= bag.deckLoadoutCount)
        {
            Say("Deck loadout " + number + " is not unlocked yet",
                MessageLevel.yellow_warning);
            return;
        }
        // GetNthDeckLoadoutCards fills in a missing slot with an empty loadout,
        // which is also what stops LoadLoadoutToDeck throwing on a null one.
        List<Card> cards = bag.GetNthDeckLoadoutCards(idx);
        if (cards == null || cards.Count == 0)
        {
            Say("Deck loadout " + number + " is empty - nothing loaded",
                MessageLevel.yellow_warning);
            return;
        }
        bag.LoadLoadoutToDeck(idx);
        Say("Deck: " + LoadoutName(bag.deckLoadoutNames, idx, "Deck " + number));
    }

    private static void LoadPermLoadout(int number)
    {
        Player player = Player.shared;
        if (player == null || player.cardBag == null)
        {
            return;
        }
        CardBag bag = player.cardBag;
        int idx = number - 1;
        if (idx < 0 || idx >= bag.permSetLoadoutCount)
        {
            Say("Permanent loadout " + number + " is not unlocked yet",
                MessageLevel.yellow_warning);
            return;
        }
        List<Card> cards = bag.GetNthPermSetLoadoutCards(idx);
        if (cards == null || cards.Count == 0)
        {
            Say("Permanent loadout " + number + " is empty - nothing loaded",
                MessageLevel.yellow_warning);
            return;
        }
        bag.LoadLoadoutToPermSet(idx);
        Say("Permanent set: "
            + LoadoutName(bag.permSetLoadoutNames, idx, "Set " + number));
    }

    private static string LoadoutName(string[] names, int idx, string fallback)
    {
        if (names == null || idx < 0 || idx >= names.Length)
        {
            return fallback;
        }
        string name = names[idx];
        if (name == null || name.Length == 0)
        {
            return fallback;
        }
        return name;
    }

    private static void MergeBattleCards()
    {
        CardPanel panel = MainPanel.shared.cardPanel;
        if (panel == null)
        {
            return;
        }
        panel.MergeAllBattleCards();
        Say("Merged all battle cards");
    }

    private static void MergePermCards()
    {
        CardPanel panel = MainPanel.shared.cardPanel;
        if (panel == null)
        {
            return;
        }
        panel.MergeAllPermCards();
        Say("Merged all permanent cards");
    }

    // ----------------------------------------------------------------- travel

    private static void MoveDestination(int step)
    {
        SidePanel side = SidePanel.shared;
        if (side == null)
        {
            return;
        }
        side.TurnOnTravelTab();
        if (step > 0)
        {
            side.DestGoUp();
        }
        else
        {
            side.DestGoDown();
        }
    }

    // The two-press check both shortcuts share. A press inside the dead time
    // leaves the arming running, so fumbling the first double-tap does not cost
    // you the confirmation - press again, at a human speed, and it goes.
    private ConfirmState Confirm(ref float armedAt)
    {
        float now = Time.unscaledTime;
        if (armedAt <= 0f || now > armedAt + CONFIRM_MAX_SECONDS)
        {
            armedAt = now;
            return ConfirmState.Armed;
        }
        if (now < armedAt + CONFIRM_MIN_SECONDS)
        {
            return ConfirmState.TooSoon;
        }
        armedAt = 0f;
        return ConfirmState.Go;
    }

    private void Depart()
    {
        SidePanel side = SidePanel.shared;
        if (side == null)
        {
            return;
        }
        ConfirmState state = Confirm(ref departArmedAt);
        if (state == ConfirmState.TooSoon)
        {
            return;
        }
        if (state == ConfirmState.Armed)
        {
            Say("Press " + depart.label + " again to depart",
                MessageLevel.yellow_warning);
            return;
        }
        side.TurnOnTravelTab();
        side.TravelButtonClicked();
    }

    // ------------------------------------------------------------------- save

    private static void QuickSave()
    {
        SaveLoadManager slm = SaveLoadManager.shared;
        if (slm == null)
        {
            return;
        }
        if (slm.SaveFile(QUICK_SLOT, QUICK_NAME))
        {
            Say("Quick saved to slot 12");
        }
        else
        {
            Say("Could not quick save here", MessageLevel.yellow_warning);
        }
    }

    private void QuickLoad()
    {
        SaveLoadManager slm = SaveLoadManager.shared;
        if (slm == null)
        {
            return;
        }
        if (slm.SaveFileIsEmpty(QUICK_SLOT))
        {
            Say("Slot 12 is empty - nothing to quick load",
                MessageLevel.yellow_warning);
            return;
        }
        // Deliberately two presses, deliberately not two fast ones. This
        // discards everything since the quick save and there is no way back.
        ConfirmState state = Confirm(ref quickLoadArmedAt);
        if (state == ConfirmState.TooSoon)
        {
            return;
        }
        if (state == ConfirmState.Armed)
        {
            Say("Press " + quickLoad.label + " again to load slot 12 and lose "
                + "progress since the quick save", MessageLevel.red_warning);
            return;
        }
        if (!slm.LoadFile(QUICK_SLOT))
        {
            Say("Could not load slot 12", MessageLevel.yellow_warning);
        }
    }

    // --------------------------------------------------------------- messages

    private static void Say(string text)
    {
        Say(text, MessageLevel.info);
    }

    // The game's own message strip. LangText.SetText runs the string through
    // Regex.Unescape, which throws on a stray backslash, and loadout names come
    // from the player - so backslashes go before the text does.
    private static void Say(string text, MessageLevel level)
    {
        try
        {
            if (MessagesPanel.shared == null || text == null)
            {
                return;
            }
            MessagesPanel.shared.NewMessage(
                new Message(level, text.Replace("\\", ""), 4));
        }
        catch (Exception e)
        {
            Log.LogWarning("Could not show a hotkey message: " + e.Message);
        }
    }

    private static void Warn(string message)
    {
        if (Log != null)
        {
            Log.LogWarning("Hotkey config: " + message);
        }
    }

    // ------------------------------------------------------------------- help

    private void OnGUI()
    {
        if (!showHelp)
        {
            return;
        }
        if (helpText == null)
        {
            helpText = BuildHelpText();
        }
        if (helpStyle == null)
        {
            helpStyle = new GUIStyle(GUI.skin.label);
            helpStyle.fontSize = Mathf.Max(12, Mathf.RoundToInt(Screen.height / 62f));
            helpStyle.wordWrap = false;
            helpStyle.normal.textColor = Color.white;
            helpStyle.padding = new RectOffset(14, 14, 12, 12);
        }
        GUIContent content = new GUIContent(helpText);
        Vector2 size = helpStyle.CalcSize(content);
        Rect box = new Rect(20f, 20f, size.x + 28f, size.y + 24f);
        Color old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.85f);
        GUI.DrawTexture(box, Texture2D.whiteTexture);
        GUI.color = old;
        GUI.Label(box, content, helpStyle);
    }

    private string BuildHelpText()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine("Space Travel Idle community mod - hotkeys");
        sb.AppendLine("Edit them in BepInEx\\config\\sti.community.hotkeys.cfg");
        sb.AppendLine();
        Row(sb, help, "Show or hide this list");
        sb.AppendLine();
        sb.AppendLine("Menu");
        Row(sb, menuPrev, "Previous panel");
        Row(sb, menuNext, "Next panel");
        Row(sb, tabPrev, "Previous tab");
        Row(sb, tabNext, "Next tab");
        sb.AppendLine();
        sb.AppendLine("Combat");
        ModRow(sb, zoneBank1, "Combat zones 1-10");
        ModRow(sb, zoneBank2, "Combat zones 11-20");
        Row(sb, zonePrev, "Previous zone");
        Row(sb, zoneNext, "Next zone");
        Row(sb, zoneSafe, "Safe zone");
        if (HotkeysPlugin.cfgZonesNeedPanel != null
            && HotkeysPlugin.cfgZonesNeedPanel.Value)
        {
            sb.AppendLine("      (the five above only while the Combat panel "
                + "is open - Settings)");
        }
        Row(sb, levelUp, "Level up");
        Row(sb, levelDown, "Level down");
        Row(sb, autoLevel, "Auto levelling on/off");
        Row(sb, autoFight, "Auto fight on/off");
        Row(sb, skipTurn, "Skip turn");
        Row(sb, abort, "Abandon battle");
        sb.AppendLine();
        sb.AppendLine("Cards");
        ModRow(sb, deckLoadouts, "Load battle deck loadout 1-10");
        ModRow(sb, permLoadouts, "Load permanent set loadout 1-10");
        Row(sb, mergeBattle, "Merge all battle cards");
        Row(sb, mergePerm, "Merge all permanent cards");
        sb.AppendLine();
        sb.AppendLine("Travel");
        Row(sb, destUp, "Destination outwards");
        Row(sb, destDown, "Destination inwards");
        Row(sb, depart, "Depart (twice, with a pause)");
        sb.AppendLine();
        sb.AppendLine("Save");
        Row(sb, quickSave, "Quick save to slot 12");
        Row(sb, quickLoad, "Quick load slot 12 (twice, with a pause)");
        return sb.ToString();
    }

    private static void Row(System.Text.StringBuilder sb, Binding b, string what)
    {
        sb.AppendLine("   " + (b.label + "                    ").Substring(0, 20)
            + what);
    }

    private static void ModRow(System.Text.StringBuilder sb, Mods m, string what)
    {
        sb.AppendLine("   " + (m.label + "                    ").Substring(0, 20)
            + what);
    }
}

// Fuzzied asked for this one in the settings rather than the config file. Same
// hook and the same template as the dev console and tooltip time rows:
// LoadSystemSettings early-returns unless the real settings rows are wired up.
//
// NOTE, and it has bitten this codebase twice: replace onValueChanged BEFORE
// writing isOn. Object.Instantiate copies the serialized listeners, so setting
// the state first fires the TEMPLATE's callback.
[HarmonyPatch(typeof(SettingsManager), "LoadSystemSettings")]
public static class AddZoneGateRowPatch
{
    private const string CLONE_NAME = "CommunityZoneKeyGateRow";

    private static readonly FieldInfo HideCardUiToggleField =
        AccessTools.Field(typeof(SettingsManager), "hideCardControlUIToggle");

    public static void Postfix(SettingsManager __instance)
    {
        try
        {
            if (HideCardUiToggleField == null
                || HotkeysPlugin.cfgZonesNeedPanel == null)
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
                    HotkeysPlugin.Log))
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
                        - new Vector2(0f, (src.rect.height + 6f) * 3f);
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
            toggle.isOn = HotkeysPlugin.cfgZonesNeedPanel.Value;
            toggle.onValueChanged.AddListener(OnClicked);

            LangText label = clone.GetComponentInChildren<LangText>(true);
            if (label != null)
            {
                // SetText writes the string straight through, unlike
                // SetLocalisedText which treats it as a localisation path.
                label.SetText("Zone keys need the Combat panel");
            }

            TooltipComposite tip = clone.GetComponent<TooltipComposite>();
            if (tip == null) { tip = clone.AddComponent<TooltipComposite>(); }
            tip.defaultTooltip = "The keys that change which combat zone you "
                + "are in - the number keys, and previous, next and safe zone "
                + "- only work while the Combat panel is open. The number keys "
                + "have no modifier, so with this off a stray digit anywhere "
                + "in the game moves you, sometimes into a boss zone. Skip "
                + "turn, abandon, auto fight and the level keys are not "
                + "affected either way.";

            HotkeysPlugin.Log.LogInfo("Settings: added the zone key row (on="
                + HotkeysPlugin.cfgZonesNeedPanel.Value + ")");
        }
        catch (Exception e)
        {
            HotkeysPlugin.Log.LogWarning(
                "Could not add the zone key row: " + e.Message);
        }
    }

    private static void OnClicked(bool isOn)
    {
        try
        {
            HotkeysPlugin.cfgZonesNeedPanel.Value = isOn;
            HotkeysPlugin.RebuildHelp();
            HotkeysPlugin.Log.LogInfo("Zone keys need the Combat panel: "
                + isOn);
        }
        catch (Exception e)
        {
            HotkeysPlugin.Log.LogWarning("Zone key row click failed: "
                + e.Message);
        }
    }
}
