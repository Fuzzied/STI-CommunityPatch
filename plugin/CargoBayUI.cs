// Space Travel Idle community mod - the controls and readouts for the three
// cargo lines. Compiled into CargoBay.dll.
//
// Three things live here:
//
//   1. two switches for the cargo doors, in the mod's own settings section:
//      whether to empty the hold at all, and whether "empty" means keeping
//      what the destination recommends you arrive with
//   2. both switches again on the travel tab, which is where Fuzzied asked for
//      them and where the decision is actually made
//   3. a line on the travel panel saying what the doors are about to do, so
//      the consequence is visible before the Departure button is pressed
//      rather than after
//   4. the quantum pocket's own block on the engine tab, under Speed Modules,
//      one row per resource with a fill bar, the figures, and a short note.
//      It lived on the travel panel until 20.09.2026 and Fuzzied moved it: the
//      engine tab is what is open during a flight, and a single total cannot
//      say what the trip is actually collecting. Bar colours are his:
//      grey at empty, green to half, yellow from half, red at full
//   5. the word "falling" on the game's own Speed row, and only when the
//      overflow has really cost something. Everything about that is measured
//      rather than predicted, which is the lesson of the 20.09.2026 test run
//
// The settings rows are the ones that are guaranteed to appear. It uses the clone
// path every other settings row in this mod uses (see CommunitySettings.cs),
// which has survived a game update already. The travel tab row is best effort:
// the travel panel is a prefab this mod has never touched, so if it is not the
// shape this expects, the row is dropped and the settings one still works.
// Both drive the same config entry, so whichever one the player uses, the
// other follows.
//
// Why the per-resource choice has no UI of its own: it reuses the lock slider
// that is already on every row of the storage panel. See the header of
// CargoDoors.cs for why that is a better control than seven tick boxes, and
// ResourceUnitTooltipPatch below for how the player is told.

using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using LargeNumbers;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

internal static class CargoBayUI
{
    internal const string SETTINGS_ROW = "CommunityCargoDoorsRow";
    internal const string SETTINGS_KEEP_ROW = "CommunityCargoKeepRecRow";
    private const string TRAVEL_ROW = "CommunityCargoDoorsTravelRow";
    private const string TRAVEL_KEEP_ROW = "CommunityCargoKeepRecTravelRow";

    // The only clonable toggle this mod has ever found in the game, stashed
    // when the settings panel loads so the travel tab can borrow it later.
    private static GameObject toggleTemplate;

    private static Toggle settingsToggle;
    private static Toggle travelToggle;
    private static Toggle settingsKeepToggle;
    private static Toggle travelKeepToggle;

    private static int travelAttempts;
    private static bool loggedTravelFailure;

    internal static void StashTemplate(GameObject template)
    {
        if (toggleTemplate == null)
        {
            toggleTemplate = template;
        }
    }

    /// <summary>
    /// Set the arm switch and bring every copy of it into line. Called from
    /// both toggles, so flipping one does not leave the other showing the old
    /// state.
    /// </summary>
    internal static void SetArmed(bool armed)
    {
        if (CargoBayPlugin.cfgDoorsArmed.Value != armed)
        {
            CargoBayPlugin.cfgDoorsArmed.Value = armed;
            CargoBayPlugin.Log.LogInfo("Cargo doors armed = " + armed);
        }
        Sync(settingsToggle, armed);
        Sync(travelToggle, armed);
    }

    /// <summary>
    /// The same for the second switch. Two switches rather than a three way
    /// choice because they answer different questions: the first is whether
    /// to empty the hold at all, the second is what counts as empty.
    /// </summary>
    internal static void SetKeepRecommended(bool keep)
    {
        if (CargoBayPlugin.cfgDoorsKeepRecommended.Value != keep)
        {
            CargoBayPlugin.cfgDoorsKeepRecommended.Value = keep;
            CargoBayPlugin.Log.LogInfo("Cargo doors keep what the destination "
                + "recommends = " + keep);
        }
        Sync(settingsKeepToggle, keep);
        Sync(travelKeepToggle, keep);
    }

    private static void Sync(Toggle toggle, bool armed)
    {
        if (toggle != null && toggle.isOn != armed)
        {
            // Without dropping the listener first this would call back into
            // SetArmed and round the loop again.
            Toggle.ToggleEvent listeners = toggle.onValueChanged;
            toggle.onValueChanged = new Toggle.ToggleEvent();
            toggle.isOn = armed;
            toggle.onValueChanged = listeners;
        }
    }

    private static void Label(GameObject row, string text)
    {
        LangText label = row.GetComponentInChildren<LangText>(true);
        if (label != null)
        {
            label.SetLocalisedText(text);
        }
    }

    private static void Tip(GameObject row, string text)
    {
        TooltipComposite tip = row.GetComponent<TooltipComposite>();
        if (tip == null)
        {
            tip = row.AddComponent<TooltipComposite>();
        }
        tip.defaultTooltip = text;
    }

    private const string DOORS_TIP =
        "With this on, leaving a star throws the hold overboard and keeps it "
        + "shut for the whole flight, so the air and biomass the space star "
        + "produces cannot trickle back in and slow you down. At level 1 of "
        + "the Cargo Bay Doors line everything goes. At level 2 each resource "
        + "keeps whatever its lock slider on the storage panel protects, so "
        + "you can throw the iron overboard and keep the biomass your space "
        + "research needs. Whatever is jettisoned is gone. The switch under "
        + "this one changes what counts as empty.";

    private const string KEEP_TIP =
        "Every planet's travel panel lists a recommended amount of certain "
        + "resources to arrive with. With this on, the doors keep exactly "
        + "that much of each one and throw out the rest, instead of emptying "
        + "the hold completely. What is kept is locked for the flight, so "
        + "research cannot spend it on the way. Biomass is never thrown out "
        + "while this is on, whatever the destination says, because that is "
        + "what your space research runs on; only the amount the destination "
        + "asks for is locked. Only the Moon, Venus and Mercury recommend "
        + "anything in the base game, so on a trip anywhere else this leaves "
        + "the doors doing what they already did.";

    // -----------------------------------------------------------------------
    // 1. the settings row
    // -----------------------------------------------------------------------

    internal static void BuildSettingsRow(SettingsManager owner, Toggle template)
    {
        settingsToggle = SettingsRow(owner, template, SETTINGS_ROW,
            "Jettison cargo on departure", DOORS_TIP,
            CargoBayPlugin.cfgDoorsArmed.Value, SetArmed);
        settingsKeepToggle = SettingsRow(owner, template, SETTINGS_KEEP_ROW,
            "Keep what the destination recommends", KEEP_TIP,
            CargoBayPlugin.cfgDoorsKeepRecommended.Value, SetKeepRecommended);

        CargoBayPlugin.Log.LogInfo("Settings: added the cargo doors rows (armed="
            + CargoBayPlugin.cfgDoorsArmed.Value + ", keepRecommended="
            + CargoBayPlugin.cfgDoorsKeepRecommended.Value + ")");
    }

    private static Toggle SettingsRow(SettingsManager owner, Toggle template,
        string name, string label, string tip, bool initial,
        UnityEngine.Events.UnityAction<bool> listener)
    {
        GameObject clone = UnityEngine.Object.Instantiate(
            template.gameObject, template.transform.parent);
        clone.name = name;

        if (!CommunitySettings.Adopt(owner, template, clone, CargoBayPlugin.Log))
        {
            clone.transform.SetSiblingIndex(
                template.transform.GetSiblingIndex() + 1);
        }

        Toggle toggle = clone.GetComponent<Toggle>();
        if (toggle == null)
        {
            UnityEngine.Object.Destroy(clone);
            return null;
        }
        // Listeners first, then state, then ours - Instantiate copies the
        // template's serialized callback and setting isOn with it still
        // attached would fire the game's own settings handler.
        toggle.onValueChanged = new Toggle.ToggleEvent();
        toggle.isOn = initial;
        toggle.onValueChanged.AddListener(listener);

        Label(clone, label);
        Tip(clone, tip);
        return toggle;
    }

    // -----------------------------------------------------------------------
    // 2. the same switch on the travel tab
    // -----------------------------------------------------------------------

    /// <summary>
    /// Best effort. Tried a handful of times and then left alone, because this
    /// is reached from a per-tick postfix and a panel that is never going to
    /// be the right shape should not be probed forever.
    /// </summary>
    internal static void TryBuildTravelRow(SidePanel panel)
    {
        if (travelToggle != null || travelAttempts > 30 || toggleTemplate == null)
        {
            return;
        }
        travelAttempts++;
        try
        {
            Transform block;
            Transform host = TravelHost(panel, out block);
            if (host == null)
            {
                return;
            }
            travelToggle = TravelRow(host, block, 0, TRAVEL_ROW,
                "Jettison on departure", DOORS_TIP,
                CargoBayPlugin.cfgDoorsArmed.Value, SetArmed);
            travelKeepToggle = TravelRow(host, block, 1, TRAVEL_KEEP_ROW,
                "Keep what the destination recommends", KEEP_TIP,
                CargoBayPlugin.cfgDoorsKeepRecommended.Value,
                SetKeepRecommended);

            CargoBayPlugin.Log.LogInfo("Travel tab: added the cargo doors "
                + "toggles under '" + host.name + "', after '"
                + ((block == null) ? "(nothing)" : block.name) + "', layout "
                + "group " + ((host.GetComponent<LayoutGroup>() == null)
                    ? "absent so placed by hand" : "present so it places it"));
        }
        catch (Exception e)
        {
            if (!loggedTravelFailure)
            {
                loggedTravelFailure = true;
                CargoBayPlugin.Log.LogWarning("Could not put the cargo doors "
                    + "toggle on the travel tab: " + e.Message
                    + ". The settings row still works.");
            }
            travelAttempts = 99;
        }
    }

    /// <summary>
    /// One travel tab row. `slot` is how many rows already sit between this
    /// one and the Set Off button, so the second one lands under the first
    /// rather than on top of it.
    /// </summary>
    private static Toggle TravelRow(Transform host, Transform block, int slot,
        string name, string label, string tip, bool initial,
        UnityEngine.Events.UnityAction<bool> listener)
    {
        Transform existing = host.Find(name);
        if (existing != null)
        {
            return existing.GetComponent<Toggle>();
        }

        GameObject clone = UnityEngine.Object.Instantiate(toggleTemplate, host);
        clone.name = name;

        Toggle toggle = clone.GetComponent<Toggle>();
        if (toggle == null)
        {
            UnityEngine.Object.Destroy(clone);
            return null;
        }
        toggle.onValueChanged = new Toggle.ToggleEvent();
        toggle.isOn = initial;
        toggle.onValueChanged.AddListener(listener);

        Label(clone, label);
        Tip(clone, tip);

        // Where it goes. A layout group owns anchoredPosition and overwrites
        // it every frame, so with one present the only thing to set is the
        // sibling order: directly after the block that holds the Set Off
        // button, and after any row already placed there. Without one, the
        // row is copied off that block's own rectangle and pushed down by a
        // block height per slot, so the rows stack under the button instead
        // of on top of it.
        //
        // 0.9.22 got this wrong. It parented the row to the button's own
        // container ('DepartureBtnContainer', which is the button and nothing
        // else) and then anchored it to the bottom centre of that container,
        // so the toggle landed inside the Set Off button.
        if (block != null)
        {
            clone.transform.SetSiblingIndex(block.GetSiblingIndex() + 1 + slot);
        }
        if (host.GetComponent<LayoutGroup>() == null)
        {
            RectTransform rt = clone.GetComponent<RectTransform>();
            RectTransform brt = (block == null) ? null : block as RectTransform;
            if (rt != null && brt != null)
            {
                rt.anchorMin = brt.anchorMin;
                rt.anchorMax = brt.anchorMax;
                rt.pivot = brt.pivot;
                rt.sizeDelta = new Vector2(brt.sizeDelta.x, rt.sizeDelta.y);
                float drop = (Mathf.Max(brt.rect.height, 24f) + 6f) * (slot + 1);
                rt.anchoredPosition =
                    new Vector2(brt.anchoredPosition.x,
                                brt.anchoredPosition.y - drop);
            }
            else if (rt != null)
            {
                // Nothing to measure against. Park it at the bottom of the
                // host rather than over anything.
                rt.anchorMin = new Vector2(0.5f, 0f);
                rt.anchorMax = new Vector2(0.5f, 0f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0f, -6f - 30f * slot);
            }
        }
        return toggle;
    }

    /// <summary>
    /// Where on the travel tab the row goes. Found without knowing any object
    /// names: SidePanel holds the travel button's label, the label knows its
    /// parents, so walk up to the Button and then keep walking until we reach
    /// something that arranges its children in a column.
    ///
    /// `block` comes back as the direct child of that column which contains
    /// the button, so the caller can sit the row right underneath it.
    ///
    /// The button's immediate parent is no good as a host. In this build it is
    /// 'DepartureBtnContainer', a wrapper around the button and nothing else,
    /// so anything put in it lands on top of the button. That is the 0.9.22
    /// bug. We climb until either a LayoutGroup turns up (that is the column,
    /// and it will place the row for us) or we run out of a sensible number of
    /// levels, and in that second case we still return a grandparent rather
    /// than the button's own wrapper.
    /// </summary>
    private static Transform TravelHost(SidePanel panel, out Transform block)
    {
        block = null;
        FieldInfo field = AccessTools.Field(typeof(SidePanel), "travelButtonLabel");
        if (field == null)
        {
            return null;
        }
        LangText label = field.GetValue(panel) as LangText;
        if (label == null)
        {
            return null;
        }
        Transform button = label.transform;
        while (button != null && button.GetComponent<Button>() == null)
        {
            button = button.parent;
        }
        if (button == null || button.parent == null
            || button.parent.parent == null)
        {
            return null;
        }

        // Climb for a column. Four levels is plenty for a side panel and keeps
        // this from ever reaching the Canvas.
        Transform child = button;
        Transform parent = button.parent;
        for (int i = 0; i < 4 && parent != null; i++)
        {
            if (parent.GetComponent<LayoutGroup>() != null)
            {
                block = child;
                return parent;
            }
            child = parent;
            parent = parent.parent;
        }

        // No column anywhere above it. Settle for the grandparent, which is at
        // least a container that holds more than the button.
        block = button.parent;
        return button.parent.parent;
    }

    // -----------------------------------------------------------------------
    // 3. the readout
    // -----------------------------------------------------------------------

    private static FieldInfo travelDescField;
    private static FieldInfo tmpTextField;
    private static bool readoutResolved;

    // What this last wrote onto the label. UpdateTravelLabels returns early
    // when nothing is selected and nothing is in flight, and a postfix runs
    // after that return, so without this the readout would be appended to its
    // own output once per FixedUpdate and grow forever.
    private static string lastWritten;

    private static readonly ElementType[] Kinds =
        (ElementType[])Enum.GetValues(typeof(ElementType));

    /// <summary>
    /// A line under the game's own travel description saying what the doors
    /// and the pocket are about to do. Written straight onto the TMP component
    /// rather than through LangText.SetText, which runs Regex.Unescape over
    /// whatever it is given and clears the label's localisation path.
    /// </summary>
    internal static void AppendReadout(SidePanel panel)
    {
        if (!readoutResolved)
        {
            readoutResolved = true;
            travelDescField = AccessTools.Field(typeof(SidePanel), "travelDescLabel");
            tmpTextField = AccessTools.Field(typeof(LangText), "tmpText");
        }
        if (travelDescField == null || tmpTextField == null)
        {
            return;
        }
        LangText label = travelDescField.GetValue(panel) as LangText;
        if (label == null)
        {
            return;
        }
        TMP_Text tmp = tmpTextField.GetValue(label) as TMP_Text;
        if (tmp == null)
        {
            return;
        }
        // Unchanged since the last append means the game took its early return
        // and the label is still holding our own output.
        if (tmp.text == lastWritten)
        {
            return;
        }

        string text = MarkFallingSpeed(tmp.text);
        string line = ReadoutText();
        if (line != null)
        {
            text = text + "\n" + line;
        }
        lastWritten = text;
        if (tmp.text != text)
        {
            tmp.text = text;
        }
    }

    /// <summary>
    /// One word on the end of the game's own Speed row, when the overflow is
    /// costing measurable speed.
    /// </summary>
    /// <remarks>
    /// The row is found by locating the speed figure the game formatted a
    /// moment ago in this same frame and running to the end of that line,
    /// rather than by knowing the wording of the localisation template or
    /// where the unit sits in it. If the figure is not in the text, nothing
    /// is marked and the block on the engine tab still carries the number.
    ///
    /// The threshold matters more than the wording. Below a tenth of a percent
    /// this stays quiet, because a hold already carrying 4.4e10 of iron cannot
    /// notice a few thousand units of air and a warning that is always on is
    /// a warning nobody reads.
    /// </remarks>
    private static string MarkFallingSpeed(string text)
    {
        try
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }
            // Mark it when there is no figure as well as when the figure is
            // big. No figure means the overflow outweighs the entire ship,
            // which is the one case where the speed most deserves marking,
            // and the old code stayed quiet through it because the failure
            // came back as the number zero.
            double cost;
            bool known = QuantumPocket.TrySpeedCostPercent(out cost);
            if (known && cost < 0.1)
            {
                return text;
            }
            Player player = Player.shared;
            if (player == null || player.spaceship == null
                || player.spaceship.engine == null)
            {
                return text;
            }
            ScientificNotation speed = player.spaceship.engine.estimatedSpeed;
            if (!(speed > ScientificNotation.zero))
            {
                return text;
            }
            string figure = speed.ToString();
            if (string.IsNullOrEmpty(figure))
            {
                return text;
            }
            int at = text.IndexOf(figure, StringComparison.Ordinal);
            if (at < 0)
            {
                return text;
            }
            int end = text.IndexOf('\n', at);
            if (end < 0)
            {
                end = text.Length;
            }
            // "falling" is a verb in the present continuous and it was being
            // driven by a running total, which is the same mistake as the
            // overflow line and the row note, one surface over. Fuzzied paused
            // Air on 21.09.2026, the other two went quiet on cue and this one
            // did not. Nothing was spilling, so nothing was being added to
            // the hold, so the weight was flat and the speed was not falling
            // at all. It was low, and staying low, which is worth saying and
            // is a different sentence. Hiding the mark instead would have
            // left a ship 14% slower than it should be with nothing on screen
            // to explain why.
            bool now = QuantumPocket.AnythingSpillingNow();
            string word = now
                ? " <color=#" + MARK_FALLING + ">falling</color>"
                : " <color=#" + MARK_SLOWED + ">slowed</color>";
            return text.Substring(0, end) + word + text.Substring(end);
        }
        catch (Exception)
        {
            return text;
        }
    }

    private static string ReadoutText()
    {
        Player player = Player.shared;
        if (player == null || player.location == null || player.storage == null)
        {
            return null;
        }
        bool travelling = player.location.isTravelling;
        string line = null;

        int doors = CargoDoors.Level();
        if (doors > 0 && CargoBayPlugin.cfgDoorsArmed.Value)
        {
            if (travelling)
            {
                line = "Cargo doors: sealed for this flight.";
            }
            else
            {
                ScientificNotation overboard = ScientificNotation.zero;
                ScientificNotation staying = ScientificNotation.zero;
                foreach (ElementType type in Kinds)
                {
                    ScientificNotation have = player.storage.GetAmount(type);
                    ScientificNotation keep = CargoDoors.KeepAmount(type);
                    ScientificNotation extra = have - keep;
                    if (extra > ScientificNotation.zero)
                    {
                        overboard += extra;
                        staying += keep;
                    }
                    else
                    {
                        staying += have;
                    }
                }
                // What the seal will keep, and where that was decided. This
                // is not decoration. On 21.09.2026 Fuzzied's biomass lock was
                // at 0, so a sealed flight correctly kept no biomass at all,
                // emptied his hold and starved his biomass plant. Every part
                // of that was working as designed and nothing on the screen
                // named the slider that designed it, so it read as the doors
                // eating his cargo. Twenty minutes to find, and the player
                // who hits it in their own save has nobody to ask.
                string staysBehind;
                if (staying > ScientificNotation.zero)
                {
                    staysBehind = " " + staying + " units stay aboard, which "
                        + "is what the lock sliders on the Storage panel are "
                        + "set to keep.";
                }
                else if (doors < 2)
                {
                    staysBehind = " Nothing stays aboard. Cargo Bay Doors "
                        + "level 2 is what lets you hold a share back.";
                }
                else
                {
                    staysBehind = " Nothing stays aboard, because every lock "
                        + "slider on the Storage panel is at 0%. Raise the "
                        + "lock on anything you want the seal to keep.";
                }
                // The weight, speed and time above are already the ones the
                // ship will fly with, because Engine.totalWeight is read
                // through the doors. Saying so is the difference between a
                // number that looks wrong and one that looks planned.
                string keeping = CargoDoors.KeepingRecommended()
                    ? " Keeping what the destination recommends." : "";
                line = (overboard > ScientificNotation.zero)
                    ? ("Cargo doors: armed, " + overboard
                        + " units go overboard on departure." + keeping
                        + staysBehind
                        + " The weight, speed and time above are for the "
                        + "lightened hold.")
                    : ("Cargo doors: armed, nothing to jettison." + keeping);
            }
        }

        // The pocket used to print a line here too. It moved to the engine tab
        // on 20.09.2026, under the Speed Modules block, because that is where
        // Fuzzied looks while a flight is running and the travel panel had the
        // number squeezed in under six other stats. See EngineReadout below.
        return line;
    }

    // -----------------------------------------------------------------------
    // 4. the pocket readout, on the engine tab under Speed Modules
    // -----------------------------------------------------------------------
    //
    // Fuzzied asked for two things at once here, and they are the same thing:
    // one line per resource rather than a single total, and the block moved
    // out of the travel panel into the empty space under the Speed Modules
    // list. A total of "6,72e5 units" does not say whether the trip is
    // collecting air, biomass or both, which is the only question worth
    // asking mid flight.
    //
    // It is parented to the engine tab's own content object, so it hides and
    // shows with that tab and needs no visibility logic of its own.

    private const string ENGINE_READOUT = "CommunityQuantumPocketReadout";

    private static TMP_Text pocketLabel;
    private static int engineAttempts;
    private static bool loggedEngineFailure;

    // air, water, soil, biomass, coal, silicon, iron, matching ElementType.
    private static readonly string[] KindKeys =
    {
        "air", "water", "soil", "biomass", "coal", "silicon", "iron"
    };

    private static string KindName(int index)
    {
        try
        {
            string name = Localisation.GetLocalisation(
                "panels.storage.resources." + KindKeys[index] + ".name");
            if (!string.IsNullOrEmpty(name) && name.IndexOf('{') < 0)
            {
                return name;
            }
        }
        catch (Exception)
        {
            // Falls through to the English name below.
        }
        string raw = KindKeys[index];
        return char.ToUpper(raw[0]) + raw.Substring(1);
    }

    internal static void UpdateEngineReadout()
    {
        if (pocketLabel == null)
        {
            TryBuildEngineReadout();
            if (pocketLabel == null)
            {
                return;
            }
        }
        string text = PocketBlock();
        if (pocketLabel.text != text)
        {
            pocketLabel.text = text;
        }
    }

    // Five columns, pinned. Fuzzied, on an earlier readout: "the part after xx
    // jumps around. Can the text after units be set static". A <pos> tag is an
    // absolute x on the line, so the name, the bar and the figures never move
    // as the numbers grow, and the only thing free to change width is the note
    // on the far right, which has nothing to its right to shove.
    //
    // The percent has no column. It rides along inside the figures as
    // "4,07e6 of 1,25e8 (3%)", which is variant D of the mockups Fuzzied picked
    // on 20.09.2026. It is the one placement that does move when the number of
    // digits changes, and he was told that and chose it anyway, because the
    // percent reads as part of the same sentence as the amount rather than as
    // a fourth thing on the line. The movement is small: both figures are
    // scientific notation, so only the exponent's digit count can change.
    // If he ever tires of it, pinning it back is one more <pos> constant.
    private const string COL_BAR = "<pos=17%>";
    private const string COL_AMOUNT = "<pos=31%>";
    private const string COL_NOTE = "<pos=52%>";

    // The bar is a <mark> highlight over a run of spaces, and getting here
    // took three goes, so the reasoning is worth writing down.
    //
    // A highlight is drawn at the height of the LINE, not the height of the
    // characters it covers, and the line's height comes from the tallest thing
    // on it. Every row here also carries normal sized text, so the highlight
    // is always full text height whatever is done to the spaces. That is why
    // the first build drew seven full red bars as one solid rectangle, and why
    // the second build, which shrank the spaces to 45%, kept its width but
    // came back exactly as tall. The spaces were never the thing being
    // measured.
    //
    // The third build gave up on highlights and used block characters, which
    // do scale. That worked, and then the font said no: the game's own atlas
    // has no U+2588, no U+25A0 and no U+25AC, so it fell back to a row of
    // equals signs. Real bars were not on offer that way.
    //
    // So: highlights, and the gap comes from the line instead. BAR_LINES
    // stretches the distance between baselines without touching the font's
    // ascender and descender, which is what the highlight is actually sized
    // from, so the bars stay the height they were and the rows move apart.
    private const string BAR_LINES = "<line-height=145%>";
    private const string BAR_CELL = "0.55em";

    // Long and thin reads as a bar. Short and thick reads as a block, which
    // is what Fuzzied got on 20.09.2026 and called plain. The highlight cannot
    // be made thinner, because its height is the height of the text beside
    // it, so the only lever left is length. Fifteen cells is about as far as
    // it can go before it reaches the figures in the next column, and it also
    // buys finer steps: each cell is under seven percent instead of ten.
    private const int BAR_CELLS = 15;

    // Each colour is three shades, darkest first. The filled part of the bar
    // is drawn in thirds so it lightens from left to right. It is a cheap
    // trick and it is the only depth available: a highlight is one flat
    // colour, and TextMeshPro will not round a corner or shade one vertically
    // however it is asked. Kept subtle on purpose. It should read as a bar
    // with a bit of life in it, not as three bars in a row.
    private const string BAR_TRACK = "2E2E36";
    private static readonly string[] BAR_GREEN =
        { "3E9A4E", "4CAF50", "62C267" };
    private static readonly string[] BAR_YELLOW =
        { "C79323", "DEA62E", "EDBB4F" };
    private static readonly string[] BAR_RED =
        { "C2403A", "D75248", "E4706A" };

    // The word on the game's own Speed row is one flat colour, so it needs a
    // plain string and not one of the arrays above. It had BAR_RED in it and
    // that still compiled after the colours became arrays, because C# will
    // happily concatenate an array by calling ToString on it, which is how
    // the Speed row came to say "<color=#System.String[]>falling" on Fuzzied's
    // screen on 20.09.2026. Anything that needs a single colour takes this.
    private const string MARK_FALLING = "D75248";

    // The same row when the loss has stopped growing. Calmer on purpose: the
    // ship is carrying a penalty rather than taking one, and a red word that
    // never goes away is a red word nobody reads.
    private const string MARK_SLOWED = "C8A046";

    /// <summary>
    /// One fill bar. Fuzzied set the thresholds on 20.09.2026: grey at empty,
    /// green to half, yellow from half, red at full. The colour says how full
    /// it is and nothing else, so the note beside it carries whether being
    /// full is a problem.
    /// </summary>
    /// <summary>
    /// The fill as a whole percent, clamped at both ends the same way the bar
    /// is. A row that reads 0% next to a bar with a cell lit, or 100% next to
    /// a bar with a gap in it, would just look like one of the two is broken.
    /// </summary>
    private static string Percent(double fraction)
    {
        return PercentWhole(fraction).ToString(CultureInfo.CurrentCulture)
            + "%";
    }

    private static int PercentWhole(double fraction)
    {
        if (fraction <= 0.0)
        {
            return 0;
        }
        int whole = (int)Math.Round(fraction * 100.0);
        if (whole < 1)
        {
            whole = 1;
        }
        if (whole > 100)
        {
            whole = 100;
        }
        if (fraction < 1.0 && whole == 100)
        {
            whole = 99;
        }
        return whole;
    }

    private static string Bar(double fraction)
    {
        // The colour comes from the same whole percent the row prints, not
        // from the raw fraction. Fuzzied's rule is green up to 50 and yellow
        // from there, and a resource sitting on exactly half came out yellow
        // on the first run with the percent hidden, because the division
        // landed a hair over 0,5. With the number on screen next to it, a row
        // reading 50% in yellow looks like a bug whatever the arithmetic says.
        int whole = PercentWhole(fraction);
        string[] shades;
        if (whole <= 50)
        {
            shades = BAR_GREEN;
        }
        else if (whole < 100)
        {
            shades = BAR_YELLOW;
        }
        else
        {
            shades = BAR_RED;
        }

        int on = (int)Math.Round(fraction * BAR_CELLS);
        if (on < 0)
        {
            on = 0;
        }
        if (on > BAR_CELLS)
        {
            on = BAR_CELLS;
        }
        // Rounding alone gets both ends wrong. A pocket with something in it
        // should never look empty, and one that is not full should never look
        // full, because the difference between those two is the whole point.
        if (fraction > 0.0 && on == 0)
        {
            on = 1;
        }
        if (fraction < 1.0 && on == BAR_CELLS)
        {
            on = BAR_CELLS - 1;
        }

        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append("<mspace=").Append(BAR_CELL).Append(">");

        // The lit part in three shades, dark to light, left to right. Split
        // so the lightest shade always gets the leftovers and a bar of one
        // or two cells still lands on a single shade rather than a stripe.
        int third = on / 3;
        int[] run = { third, third, on - third - third };
        for (int i = 0; i < run.Length; i++)
        {
            if (run[i] > 0)
            {
                sb.Append("<mark=#").Append(shades[i]).Append("FF>")
                  .Append(' ', run[i]).Append("</mark>");
            }
        }
        if (on < BAR_CELLS)
        {
            sb.Append("<mark=#").Append(BAR_TRACK).Append("FF>")
              .Append(' ', BAR_CELLS - on).Append("</mark>");
        }
        sb.Append("</mspace>");
        return sb.ToString();
    }

    /// <summary>How long until the ship arrives, in seconds, or zero.</summary>
    private static double FlightSecondsLeft()
    {
        try
        {
            Player player = Player.shared;
            if (player == null || player.location == null
                || player.spaceship == null || player.spaceship.engine == null)
            {
                return 0.0;
            }
            ScientificNotation speed = player.spaceship.engine.estimatedSpeed;
            if (!(speed > ScientificNotation.zero))
            {
                return 0.0;
            }
            return (player.location.remainingDistance / speed).Standard();
        }
        catch (Exception)
        {
            return 0.0;
        }
    }

    /// <summary>
    /// The short note on the right of a row. Full is the only state with two
    /// completely different meanings, so it always says which one it is: the
    /// overflow either goes into the hold and slows the ship, or the doors
    /// stop the collector and nothing is lost.
    /// </summary>
    private static string RowNote(int index, ScientificNotation cap,
        ScientificNotation amount)
    {
        // Full is whatever RoomFor calls full, rather than a second
        // comparison that agrees with it most of the time. They disagreed on
        // save B on 21.09.2026, over a gap of a millionth of a unit, and this
        // row went on quoting a fill rate at a pocket that was spilling.
        if (!(cap > ScientificNotation.zero)
            || !(QuantumPocket.RoomFor((ElementType)index)
                > ScientificNotation.zero))
        {
            if (CargoDoors.RefusesIncome((ElementType)index))
            {
                return "full, sealed";
            }
            // Full is not the same as spilling. Space pays air and biomass and
            // nothing else, so a full iron pocket with the doors wide open is
            // losing precisely nothing and must not claim otherwise. Only say
            // spilling once something really has gone past.
            // "spilling" is present tense, so it asks the present tense
            // question, the same one the overflow line below asks. A pocket
            // that filled, spilled for a while and then went quiet is full
            // and nothing more.
            return QuantumPocket.SpillingNow(index) ? "full, spilling" : "full";
        }
        ScientificNotation perSecond = QuantumPocket.RateAt(index);
        if (!(perSecond > ScientificNotation.zero))
        {
            return "";
        }
        double toFull = ((cap - amount) / perSecond).Standard();
        double left = FlightSecondsLeft();
        // Only worth saying when it happens before you land. Otherwise the
        // rate is the more useful number, because it says the collector runs.
        if (toFull > 0.0 && left > 0.0 && toFull < left)
        {
            string when = Pretty(toFull);
            if (when != null)
            {
                return "full in " + when;
            }
        }
        return "+" + perSecond + "/s";
    }

    /// <summary>
    /// A duration the way the rest of the game writes one.
    /// </summary>
    /// <remarks>
    /// ToDHMSFormat is the game's own formatter and it returns the unit names
    /// as unresolved localisation tags: "5{general.time.hour}40{general.time.
    /// minute}". Labels that go in through LangText.SetLocalisedText get those
    /// filled in on the way, but this block assigns .text directly, so nothing
    /// downstream resolves them. Fuzzied saw the result on 20.09.2026: the air
    /// row said "full in 5{general.time.hour}40{general.time.minute}21{general.
    /// time.second}", which is four times the width it should be and ran off
    /// the end of the panel. GetLocalisationFromLocPath is what LangText calls.
    /// OfflineFix.cs has the same helper for the same reason, and it is copied
    /// rather than shared because these are two separate assemblies.
    /// </remarks>
    private static string Pretty(double seconds)
    {
        try
        {
            if (seconds < 1.0)
            {
                return null;
            }
            string raw = seconds.ToDHMSFormat();
            // "-" is its answer for infinity, NaN and anything past
            // TimeSpan.MaxValue, none of which is worth printing.
            if (string.IsNullOrEmpty(raw) || raw == "-")
            {
                return null;
            }
            string text = Localisation.GetLocalisationFromLocPath(raw);
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The whole block, already broken into lines. Null is never returned:
    /// an empty string hides it without destroying anything.
    /// </summary>
    private static string PocketBlock()
    {
        Player player = Player.shared;
        if (player == null || player.location == null
            || QuantumPocket.Level() <= 0
            || !CargoBayPlugin.cfgQuantumPocket.Value)
        {
            return "";
        }

        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        if (player.location.isTravelling)
        {
            // The stretched line spacing is what keeps one row's bar
            // off the next one's. It applies to the whole block, so the
            // heading and the overflow line get the same air.
            sb.Append(BAR_LINES).Append("Quantum pocket, weighs nothing");
            ScientificNotation cap = QuantumPocket.Capacity();
            int shown = 0;
            for (int i = 0; i < KindKeys.Length; i++)
            {
                // A row per resource that is actually doing something. Out in
                // space that is air and biomass, and printing five more at
                // zero would bury the two that move.
                if (!QuantumPocket.Shows(i))
                {
                    continue;
                }
                ScientificNotation amount = QuantumPocket.HeldAt(i);
                double fraction = (cap > ScientificNotation.zero)
                    ? (amount / cap).Standard() : 0.0;

                sb.Append("\n").Append(KindName(i))
                  .Append(COL_BAR).Append(Bar(fraction))
                  .Append(COL_AMOUNT).Append(amount).Append(" of ").Append(cap)
                  .Append(" (").Append(Percent(fraction)).Append(")");
                string note = RowNote(i, cap, amount);
                if (note.Length > 0)
                {
                    sb.Append(COL_NOTE).Append(note);
                }
                shown++;
            }
            if (shown == 0)
            {
                sb.Append("\nNothing in it yet. It fills with what you earn "
                    + "out here.");
            }
            AppendSpillLine(sb);
            return sb.ToString();
        }

        // On the ground the pocket is empty by definition, because arrival
        // empties it. Saying nothing at all reads as the feature being broken,
        // which is what it looked like to Fuzzied on his first landing, so the
        // ground version reports what the trip delivered instead.
        if (QuantumPocket.lastLanded > ScientificNotation.zero)
        {
            sb.Append("Quantum pocket, delivered when you landed");
            for (int i = 0; i < KindKeys.Length; i++)
            {
                ScientificNotation amount = QuantumPocket.LandedAt(i);
                if (amount > ScientificNotation.zero)
                {
                    sb.Append("\n").Append(KindName(i))
                      .Append(COL_AMOUNT).Append(amount);
                }
            }
            return sb.ToString();
        }
        return "Quantum pocket: empty. It fills with what you earn once you "
            + "set off.";
    }

    /// <summary>
    /// What the overflow has actually cost, in percent, rather than a word
    /// that warns whether or not there is anything to warn about. On a hold
    /// full of iron this reads as too little to matter, which is the true
    /// answer and the one a red light would have got wrong.
    /// </summary>
    /// <summary>
    /// True while at least one thing that has already spilled is still free to
    /// land in the hold.
    /// </summary>
    /// <remarks>
    /// A sealed flight turns income away once the pocket is full and the hold
    /// is back at its lock level, and from that moment the overflow is history
    /// rather than a cost the ship is still paying. Asked per resource rather
    /// than once for the flight, because a hold can be sealed against biomass
    /// and wide open to air in the same second: the lock sliders are set one
    /// resource at a time and a lock of zero is a perfectly normal setting.
    /// </remarks>
    private static bool StillLandingInTheHold()
    {
        for (int i = 0; i < KindKeys.Length; i++)
        {
            ElementType type = (ElementType)i;
            // Two questions. Has this resource gone past the pocket in the
            // last few seconds, which is the sentence's own claim measured
            // directly rather than inferred from a total or from the pocket
            // being full. And will the hold take it, so that arming the
            // doors mid flight shuts the sentence up on the same tick
            // instead of three seconds later.
            if (QuantumPocket.SpillingNow(i)
                && !CargoDoors.HoldIsAtItsLock(type))
            {
                return true;
            }
        }
        return false;
    }

    private static void AppendSpillLine(System.Text.StringBuilder sb)
    {
        if (!QuantumPocket.HasSpilled())
        {
            return;
        }
        // HasSpilled is a running total for the flight and nothing clears it,
        // so on its own it answers "did anything ever spill" while the sentence
        // below asks "is anything spilling now". Those two came apart on save B
        // on 21.09.2026 and the result accused a working feature of leaking.
        // The travel panel said the doors were sealed, this line said overflow
        // was going into the hold and quoted 5,2% of speed, and the hold held
        // no biomass whatsoever. The 2,58e7 it was counting had landed during
        // the offline catch-up, while the pocket was still empty and the doors
        // had nothing yet to refuse, and the seal had thrown it overboard long
        // before anyone read the panel. RowNote asks the right question one
        // line above this one, so ask the same question here rather than a
        // second one that happens to agree most of the time.
        double cost;
        bool known = QuantumPocket.TrySpeedCostPercent(out cost);
        if (!StillLandingInTheHold())
        {
            // Nothing is going past the pocket this second, so the sentence
            // below would be a lie. The weight from earlier in the flight is
            // still aboard though, and the ship is still paying for it every
            // second until it lands. Returning outright here left the Speed
            // row marked with nothing anywhere on the tab to say why, and a
            // mark with no explanation is how a mark stops meaning anything.
            // So the same figure, in the tense that is true.
            if (!known)
            {
                sb.Append("\nOverflow from earlier in this flight is in "
                    + "the hold.");
            }
            else if (cost >= 0.05)
            {
                sb.Append("\nOverflow from earlier in this flight is in "
                      + "the hold. The ship is ")
                  .Append(cost.ToString("0.0", CultureInfo.CurrentCulture))
                  .Append("% slower than it would be if the pocket had "
                      + "caught it all.");
            }
            return;
        }
        sb.Append("\n");
        if (!known)
        {
            // No figure, so say the part that is true and stop.
            // Saying "too little to change the ship's speed" here
            // was the H4 bug: the only way to reach this branch is
            // an overflow that outweighs the whole ship.
            sb.Append("Overflow is going into the hold.");
        }
        else if (cost >= 0.05)
        {
            // Present tense on purpose. The first wording said "has cost you
            // X% so far", which reads as a total racked up over the flight,
            // and it is not: it is what the ship weighs now against what it
            // would weigh if the pocket had caught the lot. Fuzzied's own save
            // proved the difference. A 41 second offline catch-up dumped
            // 3,49e9 of biomass into his hold, and the offline fix takes
            // cruise speed before offline loot lands, so that weight cost him
            // nothing while he was away. It is costing him now, every second
            // of the rest of the trip, which is what this sentence should say.
            // "The ship", not "your ship". Fuzzied on 21.09.2026. The panel
            // talks about the ship in the third person everywhere else, so
            // addressing the reader here made this one line stick out.
            sb.Append("Overflow is going into the hold. The ship is ")
              .Append(cost.ToString("0.0", CultureInfo.CurrentCulture))
              .Append("% slower than it would be if the pocket had caught "
                  + "it all.");
        }
        else
        {
            sb.Append("Overflow is going into the hold, too little of it to "
                + "change the ship's speed.");
        }
    }

    /// <summary>
    /// Build the label once. Best effort and then left alone, the same rule
    /// the travel row follows, because this runs from a per tick postfix.
    /// </summary>
    private static void TryBuildEngineReadout()
    {
        if (engineAttempts > 60)
        {
            return;
        }
        engineAttempts++;
        try
        {
            SpaceshipPanel panel =
                UnityEngine.Object.FindObjectOfType<SpaceshipPanel>();
            if (panel == null)
            {
                return;
            }

            FieldInfo contentField =
                AccessTools.Field(typeof(SpaceshipPanel), "engineTabContent");
            FieldInfo menuField =
                AccessTools.Field(typeof(SpaceshipPanel), "speedModuleMenus");
            GameObject content = (contentField == null)
                ? null : contentField.GetValue(panel) as GameObject;
            Component menu = (menuField == null)
                ? null : menuField.GetValue(panel) as Component;
            if (content == null && menu == null)
            {
                return;
            }

            Transform host = (content != null)
                ? content.transform : menu.transform.parent;
            if (host == null)
            {
                return;
            }

            Transform existing = host.Find(ENGINE_READOUT);
            if (existing != null)
            {
                pocketLabel = existing.GetComponent<TMP_Text>();
                return;
            }

            // Borrow a font from a label the game already drew in this panel,
            // so the block matches the Speed Modules rows instead of falling
            // back to whatever TMP considers default.
            TMP_Text sample = (menu == null)
                ? host.GetComponentInChildren<TMP_Text>(true)
                : menu.GetComponentInChildren<TMP_Text>(true);

            GameObject go = new GameObject(ENGINE_READOUT);
            go.transform.SetParent(host, false);
            TextMeshProUGUI label = go.AddComponent<TextMeshProUGUI>();
            if (sample != null)
            {
                label.font = sample.font;
                label.fontSharedMaterial = sample.fontSharedMaterial;
                label.color = sample.color;
                label.fontSize = sample.fontSize;
            }
            label.enableWordWrapping = true;
            label.alignment = TextAlignmentOptions.BottomLeft;
            label.raycastTarget = false;

            // Bottom left of the engine tab, across most of its width, which
            // is the empty space Fuzzied circled. A layout group would fight
            // this, so it is only anchored by hand when there is none.
            RectTransform rt = go.GetComponent<RectTransform>();
            if (host.GetComponent<LayoutGroup>() == null)
            {
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(1f, 0f);
                rt.pivot = new Vector2(0f, 0f);
                rt.offsetMin = new Vector2(24f, 24f);
                // Tall enough for the worst case, which is a staged test save
                // with all seven resources in the pocket: a heading, seven
                // rows, the overflow line and the doors line. 176px fitted
                // that with nothing to spare and the text is bottom aligned,
                // so anything extra would have been clipped off the top, and
                // the stretched line spacing the bars need made every one of
                // those lines half again as tall. The box being bigger than
                // the text costs nothing, because the usual two line case
                // still sits on the bottom edge.
                rt.offsetMax = new Vector2(-24f, 400f);
            }
            else
            {
                go.transform.SetAsLastSibling();
            }

            pocketLabel = label;
            CargoBayPlugin.Log.LogInfo("Engine tab: put the quantum pocket "
                + "readout under '" + host.name + "', "
                + ((sample == null) ? "with no font to borrow"
                    : ("font borrowed from '" + sample.name + "'")) + ", "
                + ((host.GetComponent<LayoutGroup>() == null)
                    ? "anchored to the bottom by hand"
                    : "placed last by the layout group"));
        }
        catch (Exception e)
        {
            if (!loggedEngineFailure)
            {
                loggedEngineFailure = true;
                CargoBayPlugin.Log.LogWarning("Could not put the quantum "
                    + "pocket readout on the engine tab: " + e.Message
                    + ". Nothing else is affected.");
            }
            engineAttempts = 999;
        }
    }
}

// SettingsManager.LoadSystemSettings is where every settings row this mod adds
// gets built, and the only place the clonable toggle can be reached from.
[HarmonyPatch(typeof(SettingsManager), "LoadSystemSettings")]
public static class CargoSettingsRowPatch
{
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
            CargoBayUI.StashTemplate(template.gameObject);

            // LoadSystemSettings can run more than once.
            if (CommunitySettings.AlreadyAdded(template, CargoBayUI.SETTINGS_ROW))
            {
                return;
            }
            CargoBayUI.BuildSettingsRow(__instance, template);
        }
        catch (Exception e)
        {
            // Swallowed on purpose: a throw here would repeat on every settings
            // load, and a missing row costs the player a switch they can still
            // reach from the travel tab and the config file.
            CargoBayPlugin.Log.LogWarning("Could not add the cargo doors "
                + "settings row: " + e.Message);
        }
    }
}

// UpdateTravelLabels is called from SidePanel.FixedUpdate by way of
// UpdateTravel, so it is the natural place to keep the readout current and to
// notice that the travel tab now exists.
[HarmonyPatch(typeof(SidePanel), "UpdateTravelLabels")]
public static class SidePanelReadoutPatch
{
    private static bool loggedFailure;

    public static void Postfix(SidePanel __instance)
    {
        try
        {
            CargoBayUI.TryBuildTravelRow(__instance);
            CargoBayUI.AppendReadout(__instance);
            // The engine tab has no per tick hook of its own, and this one
            // runs from SidePanel.FixedUpdate whichever tab is open.
            CargoBayUI.UpdateEngineReadout();
        }
        catch (Exception e)
        {
            if (!loggedFailure)
            {
                loggedFailure = true;
                CargoBayPlugin.Log.LogWarning("The cargo readout on the travel "
                    + "panel failed: " + e.Message + ". Travel is unaffected.");
            }
        }
    }
}

// The lock slider is doing a second job now, so the tooltip that explains the
// sliders has to say so. It already lists the lock and discard values, which
// is exactly where a player looks when wondering what the handle does.
[HarmonyPatch(typeof(ResourceUnit), "GetDetailTooltip")]
public static class ResourceUnitTooltipPatch
{
    public static void Postfix(ResourceUnit __instance, ref string __result)
    {
        try
        {
            if (CargoDoors.Level() < 2)
            {
                return;
            }
            double locked = __instance.GetLockValue();
            __result = __result + "\nCargo doors: "
                + ((locked > 0.0)
                    ? ("keeps the locked " + Math.Round(locked * 100.0, 1)
                        + "% on departure")
                    : "all of this goes overboard on departure");
        }
        catch (Exception)
        {
            // a tooltip is not worth a throw on a hover
        }
    }
}
