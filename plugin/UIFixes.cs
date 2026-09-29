// Space Travel Idle community mod - UI fixes
//
// Big Bang upgrades tab: the dark-matter total label is drawn over the
// bottom-left corner of the upgrade grid, covering an upgrade item.
//
// v1.1.0 changes the strategy on Fuzzied's request. v1.0.x only ever RAISED
// the grid's bottom edge above the label, which dodges the overlap but
// throws away the strip of empty panel below the label. Now the label is
// first parked in the panel's own bottom-left corner, and then the grid's
// bottom edge is set (up OR down) to just above whatever is actually
// sitting in the bottom of the panel - so the cards get every row that
// fits. Parking is undone when you leave the upgrades tab, so the Big
// Bang tab still looks stock.
//
// v1.2.0 fixes two things Fuzzied reported against v1.1.0:
//   - the tab visibly JUMPED, because the patch waited five physics ticks
//     for Unity's layout to settle before moving anything, so the stock
//     layout drew first. It now moves on the first tick and re-measures
//     from the baseline for a short while afterwards instead.
//   - the total's home position is in the middle-left of the panel, where
//     on the BIG BANG tab its frame draws a line straight across the score
//     list. Parking now happens on both tabs; only the grid resize is
//     specific to the upgrades tab.
//
// v1.4.0 finally fixes the horizontal line Fuzzied has reported three times,
// now that the v1.3.0 dump identified it: a 1190x3 Image under a 'Splitline'
// object, sitting at y 703-707 and cutting straight through the Max Damage
// Score row (697-728) and the formula text (640-871). Nothing in the game's
// code ever touches it - a grep of the decompile finds 'splitline' only in
// GameUpdatesPanel, which instantiates its own prefab - so it is a pure
// scene object placed at a position that does not survive this panel's
// height. It has to be moved at runtime.
//
// It is NOT moved to a number I picked. The scene's own sibling order says
// where the line belongs: it is listed between the BigBangInfo block and the
// BigBangFormula block, so the patch measures those two neighbours and drops
// the line in the middle of the space between them. If the neighbours cannot
// be read, or the line still lands on text afterwards, it is hidden instead
// of left crossing a word.
//
// v1.6.0 fixes what Fuzzied reported after a Big Bang: on the Upgrades tab the
// grid stops well short of the bottom of the panel, leaving a black band about
// two card rows deep. The log says why - 'ResetButton' and 'ConfirmButton'
// measure at y 301-342 in a panel whose floor is y 27, so the grid dutifully
// stops above them and the 274 units underneath go to waste. Those two buttons
// only exist while you are spending dark matter, which is exactly the moment
// you have just finished a Big Bang, which is why it looks like the fix stopped
// working. They are now parked on the panel floor first, the same way the
// dark-matter total already was, and then the grid is measured against them.
//
// v1.7.0 fixes the one Fuzzied could not click his way out of: "I cannot remove
// cards from my perm deck as clicks dont registers, community have reported
// this as a problem in vanilla too when too much research speed and infra
// accumulates the game lags".
//
// It is the message log, and it is not a slow leak - it is a per tick storm.
// Research.CheckLevelUp and Infra.CheckLevelUp both call
// MessagesPanel.NewMessage on EVERY level, and NewMessage is expensive:
// Instantiate a prefab carrying a LangText, SetSiblingIndex(0) which reshuffles
// every sibling, ScrollToTop which dirties the layout, and CountMessages, which
// after a 0.1s debounce runs GetComponentsInChildren<MessageItem>() twice over
// the whole list with a LINQ pass on each. PriorityJobManager.FixedUpdate ticks
// at Utils.SEC_TICK_COUNT = 10 per second, so once research and infra are fast
// enough to level on most ticks the cost is 10 x (running jobs) instantiations
// a second, each one rebuilding the layout of a list thousands of items long.
// Fuzzied's save: 31,396 research levels and 28,597 infra levels this Big Bang.
//
// Two leaks feed it. The level up messages carry life 5 so they do expire, but
// they arrive far faster than they leave. The MAX level ones carry life 0,
// MessageItem.ScheduleDisappear is never called for them, and nothing prunes
// the list except OfflineManager.FinishSimulation - which is why restarting the
// game buys a few responsive minutes and nothing else does.
//
// WHY IT LANDS ON THE CLICKS SPECIFICALLY, rather than just feeling slow. The
// cards live in scrolling lists, so on press Unity resolves pointerDrag to the
// ScrollRect while pointerPress is the CardItem. PointerInputModule.ProcessDrag
// then reads: once the pointer has moved past EventSystem.pixelDragThreshold
// (10px by default) and pointerPress != pointerDrag, it fires pointerUp on the
// card, sets eligibleForClick = false and nulls pointerPress - so the release
// never reaches CardItem.OnPointerClick and CardItemInBag.Clicked never runs.
// That threshold is in pixels and takes no account of frame time: at 60fps
// there are 16ms between the press and release samples and a hand cannot move
// 10px, at 3fps there are 300+ and it always does. The click is not dropped,
// it is deliberately reinterpreted as a scroll. Fix the frame rate and the
// clicks come back on their own, which is why nothing here touches input.
//
// The fix is in two halves, both on NewMessage:
//
//   - A PREFIX rate limits the level up flood. Only messages with life > 0
//     from research or infra are throttled - that is exactly the spam, because
//     the completion notices carry life 0 - and only one per source per
//     THROTTLE_SECONDS gets through, carrying a "(+N)" tail for the ones it
//     stands in for. Below that rate, which is all of normal play, nothing
//     changes at all.
//
//   - A POSTFIX caps the list. Anything past MAX_ITEMS is destroyed oldest
//     first, which bounds the immortal completion notices as well and keeps
//     CountMessages' two sweeps cheap forever.
//
// Trimming counts DOWN from childCount rather than looping while childCount is
// over the cap: Object.Destroy is deferred to the end of the frame, so the
// count does not drop as we go and a while loop would empty the whole list.
//
// Nothing here is hardcoded to scene coordinates: the scene typetrees in
// this build are stripped, so geometry cannot be read statically and
// everything is measured from the live RectTransforms instead. The patch
// logs every rectangle it measures and every change it makes, so if the
// result looks wrong the BepInEx log says exactly why.
//
// v1.9.0. Fuzzied: "And again I cannot remove cards from Perm Bag", then
// "Actually only the first 3 cards cannot be removed". Three is every card he
// has equipped - permanentCardSetCount is 2 + Modifiers.extraEquipSlot - so
// the whole row was stuck and the rest of it is empty slots. That rules out
// the 1.8.0 drag threshold and the 1.7.0 message flood, both of which are
// indifferent to which card you aim at.
//
// The game guards the equip path against a saved loadout being previewed and
// does not guard the unequip path at all:
//
//     public void AttemptSetToPermSet(PermanentCard card, int setCount)
//     {
//         if (MainPanel.shared.cardPanel.displayingCurrentPermSet)
//     ...
//     public void PutBackToBag(PermanentCard card)
//     {
//         string idLevelString = card.idLevelString;
//         if (currentPermSetLoadout.RemoveCard(idLevelString))
//
// Pick a saved loadout and the row is rebuilt from GetNthPermSetLoadoutCards,
// which is a copy of the SAVED list, and the panel stops refreshing it -
// LoadPermSet only runs while permSetLoadOutIdx is -1. So a click there reads
// the card off the preview and takes it out of the LIVE set instead. Nothing
// moves on screen either way, and the set you were not looking at loses a
// card. Both halves read as "my clicks do nothing".
//
// Read-only is plainly what was intended: DisplayPermSetLoadoutsNameSLControls
// already switches on permBagDisableIm, the game's own grey sheet over the
// bag, the moment a loadout is selected. The equipped row was simply missed.
// Both PutBackToBag overloads now stop while a loadout is being previewed, and
// the same grey sheet is put over the equipped row so it reads as a preview
// rather than as a broken panel.
//
// v1.9.1. Fuzzied, on the build that shipped 1.9.0: "Loadouts. They all appear
// empty now."
//
// That grey sheet was the mistake. permBagDisableIm covers the BAG, a big
// scrolling field of cards where a heavy wash still reads as a field of cards.
// The equipped row is five or six icons on one line, and the same wash over
// that is just a dark bar. Worse, the sheet was built by copying the model's
// sprite, colour, type and material, so whatever the game authored for the bag
// was reproduced at full strength over the row, and the fallback for a missing
// model was 60% black, which is no better.
//
// The sheet is now a flat tint at an alpha this plugin chooses, 0.2 by
// default, with no sprite and no material copied from anything. It still eats
// the click, which is what makes the row read-only along with the PutBackToBag
// guard below, and it is now faint enough that the cards it marks are still
// cards. PreviewTint in the config sets the strength, and 0 turns the tint off
// while leaving the read-only behaviour alone.
//
// What the sheet was actually doing is written to the log once, with the
// colour it would have copied and the rectangle it covers, because "the panel
// looks empty" and "the panel is covered" are the same picture from the
// player's side and the numbers are the only way to tell them apart.
//
// v1.10.0. Fuzzied, on a Big Bang line he had taken all the way to the top:
// "Maxed big bang line, says on the description that the next level ->
// 'Bonus inactive'. This is a vanilla bug."
//
// He is right, and it is in BigBangUpgradeItem.UpdateUI. That method fetches
// panels.bigBang.tabs.upgrades.noBonus once, into a local called
// 'localisation', and then spends it twice for two different meanings:
//
//     if (currentUpgrade == null)   currentBonusLabel.SetText(localisation);
//     if (nextLevelUpgrade == null) nextLevelBonusLabel.SetText(localisation);
//
// On the CURRENT label that string is right. Level 0, no bonus, inactive. On
// the NEXT label it is wrong, because nextLevelUpgrade is null for exactly
// one reason: there is no next level. The card ends up arguing with itself,
// the button saying "Max Level" and the line beside it saying the bonus is
// inactive, on a line the player just finished paying for.
//
// The fix reuses the game's own maxLevel string, the one the button is
// already showing, so it follows whatever language the player is in and no
// new text has to be translated. It only fires when the set genuinely has no
// level above the current one AND the current level is above zero, so a set
// that is empty or failed to load still reads "Bonus Inactive" rather than
// claiming to be finished.
//
// v1.11.0. Fuzzied, the moment he landed at Uranus: "Remaining time does not
// vanish", then "The entire right side is stuck". The travel block still read
// Destination: Uranus, Distance: 4,47e10km, Remaining Time: 0d0h0m0s while he
// was standing on Uranus choosing where to go next.
//
// Vanilla, and two causes stacked on each other.
//
// First, targetTotalDistance is only ever recomputed by the selectedStarIdx
// setter. Nothing recomputes it when a flight ends, so the panel keeps working
// from the finished leg's total. That is where a distance of 4,47e10km to a
// place you are already standing on comes from.
//
// Second, SidePanel.Arrive does this and nothing else:
//
//     player.location.CollectArriveFlag();
//     player.spaceship.engine.OnArrival(lastTravelIsTP);
//     locationChangeEvent.Invoke();
//     UpdateControlUIs();
//     UpdateTravelButtonText();
//
// It never rebuilds the destination selector and never recomputes the
// distance. UpdateTravelLabels does run every tick from FixedUpdate, but its
// parked branch opens with "if (selectedStarIdx == -1) return;", so on a
// landing with nothing selected it writes nothing at all and the last in
// flight frame simply stays on the screen.
//
// That is also why it looked intermittent to him. He found it cleared after
// switching tabs and then said it did not work the first time, which fits: a
// tab switch does not recompute the distance either, so it only clears when
// something happens to move the selection.
//
// The fix deliberately does not invent a reset. BigBangClean is the game's own
// "put this panel back to a sane state" routine and its whole body is
// ReloadUI() followed by SetupDestSelector(reset: true). Running the same pair
// on arrival is the smallest change that makes the panel self consistent, and
// it is a path the game already exercises every time someone resets.
//
// One visible consequence, which is a fair trade rather than a hidden cost:
// reset:true points the selection at the star you are standing on, so the
// block reads Destination: <where you are> with a distance of 0 and a dash for
// the time, instead of a stale destination and a fake zero. It stops claiming
// something false and says nothing instead, until the player picks somewhere.
//
// v1.12.0 remembers where the game window was, see WindowSpot at the bottom.
//
// Compiled with the .NET Framework 4 csc against the game's own
// Assembly-CSharp.dll and the BepInEx 5 core DLLs (see build_plugin.ps1).

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

[BepInPlugin("sti.community.uifixes", "STI Community UI Fixes", "1.12.0")]
public class UIFixesPlugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;

    internal static ConfigEntry<bool> cfgTameMessages;
    internal static ConfigEntry<int> cfgMaxMessages;
    internal static ConfigEntry<bool> cfgScaleDragThreshold;
    internal static ConfigEntry<bool> cfgLogCardClicks;
    internal static ConfigEntry<float> cfgPreviewTint;

    // v1.8.0: the drag threshold. See the header for the mechanism - a
    // click on a card in a scrolling list is reinterpreted as a scroll
    // once the pointer has moved further than EventSystem
    // .pixelDragThreshold between the press and release samples. That
    // threshold is a flat 10 pixels and takes no account of frame time,
    // so at 60fps a hand cannot cross it in the 16ms between samples and
    // at 5fps it crosses it every single time. Same hand, same speed, and
    // the click silently becomes a scroll.
    //
    // 1.7.0 attacked this from the frame rate end, by damming the message
    // log. That worked and the log says so (600+ messages folded away in
    // Fuzzied's run), and the clicks still went missing, because the message
    // log is not the only thing that can cost frames. So this is the same
    // bug attacked from the other end: scale the threshold with the frame
    // time, and a click stays a click no matter how slow the game gets.
    //
    // The cost is that starting a deliberate scroll needs a longer drag
    // while the game is slow. That is the right way round: a list that
    // needs a firmer flick is a nuisance, a card that cannot be clicked
    // at all is a broken game. And it undoes itself the moment the frame
    // rate recovers, because the scale bottoms out at 1.
    private const float REFERENCE_FRAME = 1f / 60f;
    private const float MAX_SCALE = 20f;
    private const int THRESHOLD_CEILING = 200;
    private const float CHECK_SECONDS = 0.25f;

    private float nextThresholdCheck;
    private int baseThreshold = -1;
    private int lastApplied = -1;
    private float nextThresholdReport;

    // v1.8.3. pixelDragThreshold is one global number and Unity consults it
    // for two opposite questions: how far may a pointer wander before a click
    // becomes a scroll, and how far must it travel before a drag begins.
    // Scaling it up answers the first well and ruins the second.
    //
    // Fuzzied: "I cannot move the research and infra queues now on Venus". His
    // log says why in one line:
    //
    //   Drag threshold: 51px (was 10px) at 12fps
    //
    // The priority rows are ReorderableSibling, an IBeginDragHandler, so Unity
    // will not call OnBeginDrag until the pointer has travelled 51px from
    // where it went down. A row is roughly its own height, so moving a job up
    // one place never reaches that and the row does not pick up at all; moving
    // it three places does. The number is not too big, it is being asked the
    // wrong question.
    //
    // So ask what the press landed on. ExecuteEvents.GetEventHandler walks up
    // from the pressed object to the first thing that could begin a drag. If
    // that is a ScrollRect, the pointer is inside a scrolling list and the
    // press is the case this whole fix exists for, so it keeps the scaled
    // threshold. Anything else - a queue row, a battle card - is a specific
    // thing the player grabbed on purpose, and that gets the game's own 10px
    // however badly the game is running.
    private bool pressOnDraggable;
    private bool pressWatchFailed;

    private void Awake()
    {
        // 0.1.1: off if the player set it to false in STI Community Patch.cfg
        if (!CommunityToggle.On("ui-fixes", Logger))
        {
            enabled = false;
            return;
        }
        Log = Logger;

        cfgTameMessages = Config.Bind("Message log", "TameMessageLog", true,
            "Keeps the message log from strangling the game. Every research "
            + "and infrastructure level writes a message, the game ticks ten "
            + "times a second, and each message rebuilds the layout of the "
            + "whole list - so once your research and infra are fast the log "
            + "alone can drop the game to a few frames a second, which is what "
            + "makes clicks stop registering. Level up messages are limited to "
            + "one every couple of seconds per source and the rest are counted "
            + "into it. Completion notices are never throttled.");
        cfgScaleDragThreshold = Config.Bind("Input", "ScaleDragThreshold",
            true,
            "Keeps clicks on cards working when the game is running "
            + "slowly. Unity decides a press was a scroll rather than a "
            + "click once the pointer has moved 10 pixels between two "
            + "samples, and it measures that in pixels with no regard for "
            + "how long the frame took - so at a few frames a second every "
            + "click on a card in a scrolling list is thrown away as a "
            + "scroll and nothing happens. This scales the 10 pixels with "
            + "the frame time, so a slow game needs a longer drag to start "
            + "scrolling and clicks keep working. It does nothing at all "
            + "while the game is running at speed.");
        // v1.8.2: off by default. The drag threshold fix above landed in
        // 0.9.24 and Fuzzied has not lost a card click since, so the question
        // this was built to answer has been answered. A line per click is
        // cheap for one person hunting one bug and is pure noise in every
        // other player's log, for the rest of the game. It stays in, because
        // a probe that costs nothing while it is off is worth keeping for
        // the day the bug comes back.
        cfgLogCardClicks = Config.Bind("Input", "LogCardClicks", false,
            "Writes one line to the BepInEx log for every press and release "
            + "on a card, saying how far the pointer moved, what the "
            + "threshold was, what the frame rate was, and whether the "
            + "click actually reached the card. Turn this on only if clicks "
            + "on cards are going missing for you and someone has asked to "
            + "see the log. It does nothing at all while it is off.");
        cfgPreviewTint = Config.Bind("Card panel", "PreviewTint", 0.2f,
            "How strongly the equipped row is shaded while you are looking at "
            + "a SAVED loadout rather than your live set. The row is read only "
            + "at that point, and the shading is what says so. 0 turns the "
            + "shading off and leaves the row read only. 1 is solid.");

        cfgMaxMessages = Config.Bind("Message log", "MaxMessages", 60,
            "How many messages the log keeps. The game never prunes it except "
            + "after an offline catch up, and 'complete' notices are written "
            + "with no expiry at all, so it grows for as long as the game is "
            + "running. Oldest go first.");

        WindowSpot.Bind(Config);

        Harmony harmony = new Harmony("sti.community.uifixes");
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        // The version comes from the attribute rather than being typed in
        // again. It was typed in again until v1.11.0, and it had already gone
        // stale: the banner still said v1.10.0 after the version moved. A log
        // line that names the wrong version is worse than one that names
        // none, because it is the line a bug report is read from.
        Logger.LogInfo("UI fixes active: Big Bang tab layout, divider, "
            + "upgrade item text, the maxed-line label, the message log, the "
            + "frame-time drag threshold, the read-only loadout preview and "
            + "the travel panel refresh on arrival and the remembered window "
            + "position (v"
            + Info.Metadata.Version + ")");
    }

    private void OnApplicationQuit()
    {
        try
        {
            WindowSpot.Remember();
        }
        catch (Exception)
        {
            // Quitting anyway. The spot from two seconds ago is still saved.
        }
    }

    private void Update()
    {
        WindowSpot.Tick();
        ClickProbe.Tick();
        WatchPress();
        if (cfgScaleDragThreshold == null || !cfgScaleDragThreshold.Value
            || Time.unscaledTime < nextThresholdCheck)
        {
            return;
        }
        nextThresholdCheck = Time.unscaledTime + CHECK_SECONDS;
        try
        {
            EventSystem system = EventSystem.current;
            if (system == null)
            {
                return;
            }
            // Read the game's own value once, before we have ever written
            // to it, so a later read can never pick up our own number and
            // ratchet it upwards.
            if (baseThreshold < 0)
            {
                baseThreshold = system.pixelDragThreshold;
                if (baseThreshold < 1)
                {
                    baseThreshold = 10;
                }
            }

            // smoothDeltaTime rather than deltaTime: one stutter should not
            // swing the threshold, and one good frame in a bad second
            // should not drop it back.
            float frame = Time.smoothDeltaTime;
            if (frame <= 0f)
            {
                frame = Time.unscaledDeltaTime;
            }
            float scale = Mathf.Clamp(frame / REFERENCE_FRAME, 1f, MAX_SCALE);
            if (pressOnDraggable)
            {
                scale = 1f;
            }
            int want = Mathf.Clamp(Mathf.RoundToInt(baseThreshold * scale),
                baseThreshold, THRESHOLD_CEILING);
            if (want == lastApplied && system.pixelDragThreshold == want)
            {
                return;
            }
            system.pixelDragThreshold = want;

            // Quiet while it is doing nothing, and at most once a minute
            // while it is, because a bad frame rate means this is being
            // reconsidered four times a second.
            bool worthSaying = (want != baseThreshold)
                || (lastApplied > baseThreshold);
            lastApplied = want;
            if (worthSaying && Time.unscaledTime >= nextThresholdReport)
            {
                nextThresholdReport = Time.unscaledTime + 60f;
                Log.LogInfo("Drag threshold: " + want + "px (the game's own "
                    + "is " + baseThreshold + "px) at "
                    + Mathf.RoundToInt(1f / Mathf.Max(frame, 0.0001f))
                    + "fps, " + (pressOnDraggable
                        ? "left alone because this press grabbed something that "
                            + "drags on purpose"
                        : "so a click on a card is not read as a scroll"));
            }
        }
        catch (Exception e)
        {
            cfgScaleDragThreshold.Value = false;
            Log.LogWarning("Could not scale the drag threshold, leaving it "
                + "alone: " + e.Message);
        }
    }

    // Decided once per press rather than every frame, because it costs a UI
    // raycast and because the answer cannot change while the button is held.
    private void WatchPress()
    {
        if (pressWatchFailed || cfgScaleDragThreshold == null
            || !cfgScaleDragThreshold.Value)
        {
            return;
        }
        try
        {
            if (Input.GetMouseButtonDown(0))
            {
                bool now = PressedSomethingDraggable();
                if (now != pressOnDraggable)
                {
                    pressOnDraggable = now;
                    // Apply it on this frame instead of waiting out the rest
                    // of the quarter second. The pointer has not moved yet, so
                    // the number is in place before Unity first asks.
                    nextThresholdCheck = 0f;
                }
            }
            else if (Input.GetMouseButtonUp(0) && pressOnDraggable)
            {
                pressOnDraggable = false;
                nextThresholdCheck = 0f;
            }
        }
        catch (Exception e)
        {
            // Losing this only means the threshold scales for everything
            // again, which is where 1.8.2 was. Not worth a second warning.
            pressWatchFailed = true;
            pressOnDraggable = false;
            Log.LogWarning("Could not tell what the press landed on, the drag "
                + "threshold will be scaled for every press: " + e.Message);
        }
    }

    private bool PressedSomethingDraggable()
    {
        EventSystem system = EventSystem.current;
        if (system == null)
        {
            return false;
        }
        PointerEventData probe = new PointerEventData(system);
        probe.position = Input.mousePosition;
        List<RaycastResult> hits = new List<RaycastResult>();
        system.RaycastAll(probe, hits);
        if (hits.Count == 0)
        {
            return false;
        }
        GameObject handler =
            ExecuteEvents.GetEventHandler<IBeginDragHandler>(hits[0].gameObject);
        if (handler == null)
        {
            return false;
        }
        // A ScrollRect is itself an IBeginDragHandler, and it is the one thing
        // this fix is meant to out-argue. Everything else that answers here is
        // something the player took hold of.
        return handler.GetComponent<ScrollRect>() == null;
    }
}

[HarmonyPatch(typeof(BigBangPanel), "FixedUpdate")]
public static class BigBangLayoutPatch
{
    // local (canvas) units
    private const float EDGE_MARGIN = 12f;  // label to panel corner
    private const float GRID_GAP = 10f;     // label top to grid bottom
    private const float MAX_RAISE = 0.33f;  // never eat more than a third of the grid

    // What counts as a section divider rather than a picture: an Image that
    // is a hairline (thin, in local units) and runs most of the way across
    // the panel. The aspect test is there so that a thin element on a very
    // large canvas scale cannot be confused with a line of text.
    private const float LINE_MAX_THICKNESS = 10f;
    private const float LINE_MIN_WIDTH_FRAC = 0.4f;
    private const float LINE_MIN_RATIO = 20f;

    private static readonly FieldInfo ScrollContentField =
        AccessTools.Field(typeof(BigBangPanel), "upgradeItemScrollContent");
    private static readonly FieldInfo DarkMatterLabelField =
        AccessTools.Field(typeof(BigBangPanel), "darkMatterLabel");
    private static readonly FieldInfo ResetButtonField =
        AccessTools.Field(typeof(BigBangPanel), "upgradeResetButton");
    private static readonly FieldInfo ConfirmButtonField =
        AccessTools.Field(typeof(BigBangPanel), "upgradeConfirmButton");

    // Unity's overloaded == makes a destroyed object compare as null, so a
    // scene reload after a Big Bang re-arms everything below by itself.
    private static BigBangPanel trackedPanel;
    private static string appliedSignature;
    private static int recheckTicks;
    private static bool loggedAlive;
    private static bool loggedFailure;

    // How many further physics ticks to re-measure after a change. v1.1.0
    // waited this out BEFORE touching anything, which is exactly what made
    // the tab visibly jump: the stock layout drew first. Now the move
    // happens on the very first tick and is simply redone from the baseline
    // while Unity finishes its own layout passes.
    private const int SETTLE_TICKS = 10;

    // baseline, so every re-apply starts from the untouched scene layout.
    // More than one thing gets moved now (the dark-matter total and, on the
    // upgrades tab, the two mode buttons), so this is a list.
    private class SavedMove
    {
        public RectTransform rt;
        public Vector2 pos;
    }
    private static readonly List<SavedMove> savedMoves = new List<SavedMove>();
    private static RectTransform savedScroll;
    private static Vector2 savedScrollSize;
    private static Vector2 savedScrollPos;

    private class SavedLine
    {
        public RectTransform rt;
        public Vector2 pos;
        public bool shown;
    }
    private static readonly List<SavedLine> savedLines = new List<SavedLine>();

    public static void Postfix(BigBangPanel __instance)
    {
        if (!loggedAlive)
        {
            loggedAlive = true;
            UIFixesPlugin.Log.LogInfo("BigBang layout: FixedUpdate postfix alive");
        }
        try
        {
            Tick(__instance);
        }
        catch (Exception e)
        {
            if (!loggedFailure)
            {
                loggedFailure = true;
                UIFixesPlugin.Log.LogWarning("BigBang layout: skipped - " + e);
            }
        }
    }

    private static void Tick(BigBangPanel panel)
    {
        GameObject content = ScrollContentField.GetValue(panel) as GameObject;
        if (content == null)
        {
            return;
        }
        if (trackedPanel != panel)
        {
            // fresh panel instance - the old objects are gone, drop the
            // baseline rather than trying to restore onto destroyed rects
            trackedPanel = panel;
            appliedSignature = null;
            savedMoves.Clear();
            savedScroll = null;
            savedLines.Clear();
            recheckTicks = 0;
        }

        GameObject resetBtn = ResetButtonField.GetValue(panel) as GameObject;
        GameObject confirmBtn = ConfirmButtonField.GetValue(panel) as GameObject;
        bool open = content.activeInHierarchy;
        // the two upgrade-mode buttons come and go, and they sit in the
        // bottom of the panel, so they change how low the grid may reach
        string signature = (open ? "1" : "0")
            + (IsActive(resetBtn) ? "1" : "0")
            + (IsActive(confirmBtn) ? "1" : "0");
        if (signature != appliedSignature)
        {
            appliedSignature = signature;
            recheckTicks = SETTLE_TICKS;
            RestoreBaseline();
            Apply(panel, content, resetBtn, confirmBtn, open, false);
            return;
        }
        if (recheckTicks > 0)
        {
            recheckTicks--;
            RestoreBaseline();
            Apply(panel, content, resetBtn, confirmBtn, open, recheckTicks == 0);
            if (recheckTicks == 0)
            {
                UIFixesPlugin.Log.LogInfo("BigBang layout: state " + signature
                    + " (open/reset/confirm) settled");
                if (!open)
                {
                    DumpPanelGraphics(panel, content);
                }
            }
        }
    }

    private static bool IsActive(GameObject go)
    {
        return go != null && go.activeInHierarchy;
    }

    // The RectTransform of an object that is actually on screen, or null.
    private static RectTransform GetRect(GameObject go)
    {
        if (!IsActive(go))
        {
            return null;
        }
        return go.GetComponent<RectTransform>();
    }

    private static void RestoreBaseline()
    {
        for (int i = 0; i < savedMoves.Count; i++)
        {
            SavedMove m = savedMoves[i];
            if (m.rt != null)
            {
                m.rt.anchoredPosition = m.pos;
            }
        }
        if (savedScroll != null)
        {
            // offsetMin/offsetMax are derived from these two, so restoring
            // both puts every edge back exactly where the scene had it
            savedScroll.sizeDelta = savedScrollSize;
            savedScroll.anchoredPosition = savedScrollPos;
        }
        for (int i = 0; i < savedLines.Count; i++)
        {
            SavedLine line = savedLines[i];
            if (line.rt == null)
            {
                continue;
            }
            line.rt.anchoredPosition = line.pos;
            Image img = line.rt.GetComponent<Image>();
            if (img != null)
            {
                img.enabled = line.shown;
            }
        }
    }

    // open = the upgrade grid is showing. The dark-matter total is parked in
    // the corner on BOTH tabs: its home position sits in the middle-left of
    // the panel, where on the Big Bang tab its frame cuts straight across the
    // score list. Only the grid resize is specific to the upgrades tab.
    private static void Apply(BigBangPanel panel, GameObject content,
                              GameObject resetBtn, GameObject confirmBtn,
                              bool open, bool verbose)
    {
        // find the scroll view walking up manually (GetComponentInParent
        // can skip inactive objects)
        ScrollRect scroll = null;
        Transform t = content.transform;
        while (t != null && scroll == null)
        {
            scroll = t.GetComponent<ScrollRect>();
            t = t.parent;
        }
        if (scroll == null)
        {
            UIFixesPlugin.Log.LogWarning("BigBang layout: no ScrollRect above the upgrade grid");
            return;
        }
        RectTransform scrollRt = scroll.GetComponent<RectTransform>();
        RectTransform panelRt = panel.GetComponent<RectTransform>();
        if (scrollRt == null || panelRt == null)
        {
            UIFixesPlugin.Log.LogWarning("BigBang layout: panel or scroll has no RectTransform");
            return;
        }
        float scale = scrollRt.lossyScale.y;
        if (scale <= 0f)
        {
            UIFixesPlugin.Log.LogWarning("BigBang layout: zero scale, skipping");
            return;
        }

        // corners: 0 bottom-left, 1 top-left, 2 top-right, 3 bottom-right
        Vector3[] pc = new Vector3[4];
        panelRt.GetWorldCorners(pc);
        Vector3[] sc = new Vector3[4];
        scrollRt.GetWorldCorners(sc);
        if (verbose)
        {
            UIFixesPlugin.Log.LogInfo(string.Format(
                "BigBang layout: panel ({0:F0},{1:F0})-({2:F0},{3:F0}) grid ({4:F0},{5:F0})-({6:F0},{7:F0})",
                pc[0].x, pc[0].y, pc[2].x, pc[2].y, sc[0].x, sc[0].y, sc[2].x, sc[2].y));
        }

        // --- 0. put any section divider that has drifted onto the text back
        // in the gap the scene's sibling order says it belongs in
        FixDividers(panel, panelRt, content != null ? content.transform : null, scale, verbose);

        // --- 1. park the dark-matter total in the panel's bottom-left corner
        TextMeshProUGUI label = DarkMatterLabelField.GetValue(panel) as TextMeshProUGUI;
        if (label != null && label.gameObject.activeInHierarchy)
        {
            RectTransform move = PickMoveTarget(label.rectTransform, panelRt, scrollRt);
            RememberMove(move);
            Vector3[] mc = new Vector3[4];
            move.GetWorldCorners(mc);
            Vector3 shift = new Vector3(
                pc[0].x + EDGE_MARGIN * move.lossyScale.x - mc[0].x,
                pc[0].y + EDGE_MARGIN * move.lossyScale.y - mc[0].y,
                0f);
            move.position = move.position + shift;
            if (verbose)
            {
                UIFixesPlugin.Log.LogInfo(string.Format(
                    "BigBang layout: parked '{0}' (moving '{1}') by ({2:F0},{3:F0})",
                    label.gameObject.name, move.gameObject.name, shift.x, shift.y));
            }
        }
        else if (verbose)
        {
            UIFixesPlugin.Log.LogInfo("BigBang layout: dark-matter label not active, not parking");
        }

        if (!open)
        {
            return; // Big Bang tab: park the total, leave the grid alone
        }

        // --- 1b. drop the two upgrade-mode buttons onto the panel floor.
        //
        // The scene puts them roughly a third of the way up the panel, which
        // made sense for whatever height it was laid out at and does not here.
        // Step 2 below will not let the grid cover them, so wherever they sit
        // is where the grid has to stop - and every pixel below them is dead
        // space. They are the only things down there apart from the total,
        // which is parked in the bottom-LEFT corner while these two are
        // centred, so nothing collides.
        ParkOnFloor(GetRect(resetBtn), pc, verbose);
        ParkOnFloor(GetRect(confirmBtn), pc, verbose);

        // --- 2. lower/raise the grid's bottom edge to just clear the bottom
        float floorTop = pc[0].y; // nothing in the way: the panel's own floor
        List<RectTransform> blockers = CollectBlockers(panel, scrollRt, resetBtn, confirmBtn);
        Vector3[] c = new Vector3[4];
        for (int i = 0; i < blockers.Count; i++)
        {
            RectTransform rt = blockers[i];
            rt.GetWorldCorners(c);
            bool horizontal = c[3].x > sc[0].x && c[0].x < sc[3].x;
            bool belowTop = c[1].y < sc[1].y; // headers above the grid don't count
            if (!horizontal || !belowTop)
            {
                continue;
            }
            if (verbose)
            {
                UIFixesPlugin.Log.LogInfo(string.Format(
                    "BigBang layout: '{0}' occupies ({1:F0},{2:F0})-({3:F0},{4:F0})",
                    rt.gameObject.name, c[0].x, c[0].y, c[2].x, c[2].y));
            }
            if (c[1].y > floorTop)
            {
                floorTop = c[1].y;
            }
        }

        float target = floorTop + GRID_GAP * scale;
        float ceiling = sc[0].y + (sc[1].y - sc[0].y) * MAX_RAISE;
        if (target > ceiling)
        {
            if (verbose)
            {
                UIFixesPlugin.Log.LogWarning(string.Format(
                    "BigBang layout: bottom {0:F0} capped at {1:F0} (a third of the grid)",
                    target, ceiling));
            }
            target = ceiling;
        }
        if (target < pc[0].y)
        {
            target = pc[0].y;
        }

        float delta = (target - sc[0].y) / scale;
        if (Mathf.Abs(delta) < 1f)
        {
            if (verbose)
            {
                UIFixesPlugin.Log.LogInfo("BigBang layout: grid bottom already correct");
            }
            return;
        }
        RememberScroll(scrollRt);
        SetBottomEdge(scrollRt, delta);
        if (verbose)
        {
            UIFixesPlugin.Log.LogInfo(string.Format(
                "BigBang layout: grid bottom moved {0} by {1:F0} local units",
                delta > 0f ? "up" : "down", Mathf.Abs(delta)));
        }
    }

    // v1.4.0. Find every full-width hairline in the panel and, if one is
    // lying on top of text, move it to where the scene says it belongs.
    private static void FixDividers(BigBangPanel panel, RectTransform panelRt,
                                    Transform contentT, float scale, bool verbose)
    {
        Vector3[] pc = new Vector3[4];
        panelRt.GetWorldCorners(pc);
        float panelWidth = pc[2].x - pc[0].x;

        List<RectTransform> lines = new List<RectTransform>();
        Image[] images = panel.GetComponentsInChildren<Image>(false);
        Vector3[] c = new Vector3[4];
        for (int i = 0; i < images.Length; i++)
        {
            RectTransform rt = images[i].rectTransform;
            if (contentT != null && rt.IsChildOf(contentT))
            {
                continue; // grid items, not panel furniture
            }
            rt.GetWorldCorners(c);
            float w = c[2].x - c[0].x;
            float h = c[2].y - c[0].y;
            if (h <= 0f || h > LINE_MAX_THICKNESS * scale)
            {
                continue;
            }
            if (w < panelWidth * LINE_MIN_WIDTH_FRAC || w < h * LINE_MIN_RATIO)
            {
                continue;
            }
            lines.Add(rt);
        }
        if (lines.Count == 0)
        {
            return;
        }

        List<RectTransform> text = new List<RectTransform>();
        TextMeshProUGUI[] labels = panel.GetComponentsInChildren<TextMeshProUGUI>(false);
        for (int i = 0; i < labels.Length; i++)
        {
            RectTransform rt = labels[i].rectTransform;
            if (contentT == null || !rt.IsChildOf(contentT))
            {
                text.Add(rt);
            }
        }

        for (int i = 0; i < lines.Count; i++)
        {
            FixOneDivider(lines[i], text, verbose);
        }
    }

    private static void FixOneDivider(RectTransform line, List<RectTransform> text, bool verbose)
    {
        Vector3[] lc = new Vector3[4];
        line.GetWorldCorners(lc);
        if (!CrossesText(lc, text))
        {
            return; // wherever it is, it is not in anyone's way
        }

        // Climb to the level where this line is a block of its own, so that
        // its siblings are the panel's other sections rather than the other
        // bits of its own wrapper.
        RectTransform block = line;
        while (true)
        {
            RectTransform up = block.parent as RectTransform;
            if (up == null || CountTextUnder(up, text) > 0)
            {
                break;
            }
            block = up;
        }
        Transform parent = block.parent;

        float prevLow = 0f, prevHigh = 0f, nextLow = 0f, nextHigh = 0f;
        bool hasPrev = parent != null
            && NeighbourBounds(parent, block.GetSiblingIndex(), -1, out prevLow, out prevHigh);
        bool hasNext = parent != null
            && NeighbourBounds(parent, block.GetSiblingIndex(), 1, out nextLow, out nextHigh);

        float targetY = 0f;
        bool placed = false;
        if (hasPrev && hasNext)
        {
            // Sibling order is a list, not a direction, so work out which of
            // the two neighbours is actually the higher one on screen.
            if (prevLow >= nextHigh)
            {
                targetY = (prevLow + nextHigh) * 0.5f;
                placed = true;
            }
            else if (nextLow >= prevHigh)
            {
                targetY = (nextLow + prevHigh) * 0.5f;
                placed = true;
            }
        }

        RememberDivider(line);
        if (placed)
        {
            float centre = (lc[0].y + lc[2].y) * 0.5f;
            line.position = line.position + new Vector3(0f, targetY - centre, 0f);
            line.GetWorldCorners(lc);
            if (!CrossesText(lc, text))
            {
                if (verbose)
                {
                    UIFixesPlugin.Log.LogInfo(string.Format(
                        "BigBang layout: divider '{0}' moved from y {1:F0} to y {2:F0}"
                        + " (between {3:F0} and {4:F0})",
                        block.gameObject.name, centre, targetY,
                        Mathf.Min(prevLow, nextLow), Mathf.Max(prevHigh, nextHigh)));
                }
                return;
            }
            placed = false;
        }

        // Nowhere safe to put it. A hidden divider costs nothing; one drawn
        // through a number makes the panel look broken.
        Image img = line.GetComponent<Image>();
        if (img != null)
        {
            img.enabled = false;
        }
        UIFixesPlugin.Log.LogWarning("BigBang layout: divider '" + block.gameObject.name
            + "' had no clear gap to move to, hidden instead");
    }

    private static bool CrossesText(Vector3[] lc, List<RectTransform> text)
    {
        Vector3[] c = new Vector3[4];
        for (int i = 0; i < text.Count; i++)
        {
            text[i].GetWorldCorners(c);
            if (lc[2].x <= c[0].x || lc[0].x >= c[2].x)
            {
                continue;
            }
            if (lc[2].y <= c[0].y || lc[0].y >= c[2].y)
            {
                continue;
            }
            return true;
        }
        return false;
    }

    private static int CountTextUnder(RectTransform ancestor, List<RectTransform> text)
    {
        int n = 0;
        for (int i = 0; i < text.Count; i++)
        {
            if (text[i].IsChildOf(ancestor))
            {
                n++;
            }
        }
        return n;
    }

    // The vertical extent of the nearest sibling in the given direction that
    // actually draws something.
    private static bool NeighbourBounds(Transform parent, int from, int step,
                                        out float low, out float high)
    {
        low = 0f;
        high = 0f;
        Vector3[] c = new Vector3[4];
        for (int i = from + step; i >= 0 && i < parent.childCount; i += step)
        {
            Transform sib = parent.GetChild(i);
            if (!sib.gameObject.activeInHierarchy)
            {
                continue;
            }
            Graphic[] gs = sib.GetComponentsInChildren<Graphic>(false);
            if (gs.Length == 0)
            {
                continue;
            }
            low = float.MaxValue;
            high = float.MinValue;
            for (int k = 0; k < gs.Length; k++)
            {
                gs[k].rectTransform.GetWorldCorners(c);
                if (c[0].y < low)
                {
                    low = c[0].y;
                }
                if (c[2].y > high)
                {
                    high = c[2].y;
                }
            }
            return true;
        }
        return false;
    }

    private static void RememberDivider(RectTransform rt)
    {
        for (int i = 0; i < savedLines.Count; i++)
        {
            if (savedLines[i].rt == rt)
            {
                return; // keep the original baseline
            }
        }
        SavedLine line = new SavedLine();
        line.rt = rt;
        line.pos = rt.anchoredPosition;
        Image img = rt.GetComponent<Image>();
        line.shown = img == null || img.enabled;
        savedLines.Add(line);
    }

    // v1.3.0 diagnostic. Fuzzied reports a horizontal line cutting straight
    // through the score list on the BIG BANG tab, and the log already proves
    // it is not the dark-matter total (that object is inactive on this tab,
    // so parking it cannot help). Rather than guess a third time, dump every
    // active graphic in the panel with its measured rectangle - the culprit
    // will be the wide, thin one sitting on top of the text. Once per run.
    private static bool dumpedBigBangTab;

    private static void DumpPanelGraphics(BigBangPanel panel, GameObject content)
    {
        if (dumpedBigBangTab)
        {
            return;
        }
        dumpedBigBangTab = true;
        RectTransform panelRt = panel.GetComponent<RectTransform>();
        if (panelRt == null)
        {
            return;
        }
        Vector3[] pc = new Vector3[4];
        panelRt.GetWorldCorners(pc);
        UIFixesPlugin.Log.LogInfo(string.Format(
            "BigBang tab dump: panel '{0}' ({1:F0},{2:F0})-({3:F0},{4:F0})",
            panelRt.gameObject.name, pc[0].x, pc[0].y, pc[2].x, pc[2].y));

        Transform contentT = content != null ? content.transform : null;
        // false = active graphics only, which is what is actually drawn
        Graphic[] graphics = panel.GetComponentsInChildren<Graphic>(false);
        Vector3[] c = new Vector3[4];
        for (int i = 0; i < graphics.Length; i++)
        {
            Graphic g = graphics[i];
            RectTransform rt = g.rectTransform;
            if (contentT != null && rt.IsChildOf(contentT))
            {
                continue; // grid items, not interesting here
            }
            rt.GetWorldCorners(c);
            UIFixesPlugin.Log.LogInfo(string.Format(
                "BigBang tab dump: '{0}' [{1}] ({2:F0},{3:F0})-({4:F0},{5:F0}) {6:F0}x{7:F0} under '{8}'",
                rt.gameObject.name, g.GetType().Name,
                c[0].x, c[0].y, c[2].x, c[2].y,
                c[2].x - c[0].x, c[2].y - c[0].y,
                rt.parent != null ? rt.parent.gameObject.name : "-"));
        }
        UIFixesPlugin.Log.LogInfo("BigBang tab dump: end");
    }

    // Everything active in the panel that could sit in the bottom strip and
    // must not be covered by the grid.
    private static List<RectTransform> CollectBlockers(BigBangPanel panel, RectTransform scrollRt,
                                                       GameObject resetBtn, GameObject confirmBtn)
    {
        List<RectTransform> list = new List<RectTransform>();
        TextMeshProUGUI[] labels = panel.GetComponentsInChildren<TextMeshProUGUI>(false);
        for (int i = 0; i < labels.Length; i++)
        {
            RectTransform rt = labels[i].rectTransform;
            if (!rt.IsChildOf(scrollRt))
            {
                list.Add(rt);
            }
        }
        AddButton(list, resetBtn, scrollRt);
        AddButton(list, confirmBtn, scrollRt);
        return list;
    }

    private static void AddButton(List<RectTransform> list, GameObject go, RectTransform scrollRt)
    {
        if (!IsActive(go))
        {
            return;
        }
        RectTransform rt = go.GetComponent<RectTransform>();
        if (rt != null && !rt.IsChildOf(scrollRt) && !list.Contains(rt))
        {
            list.Add(rt);
        }
    }

    // A total like this is usually a bit of text inside a small framed
    // badge; moving the text alone would leave the frame behind. If the
    // label's parent is a snug wrapper, move the wrapper instead.
    private static RectTransform PickMoveTarget(RectTransform labelRt, RectTransform panelRt,
                                                RectTransform scrollRt)
    {
        RectTransform parent = labelRt.parent as RectTransform;
        if (parent == null || parent == panelRt)
        {
            return labelRt;
        }
        if (scrollRt.IsChildOf(parent))
        {
            return labelRt; // that parent holds the grid too - moving it moves everything
        }
        float parentArea = Mathf.Abs(parent.rect.width * parent.rect.height);
        float labelArea = Mathf.Abs(labelRt.rect.width * labelRt.rect.height);
        if (labelArea <= 0f || parentArea > labelArea * 4f)
        {
            return labelRt; // a big container, not a badge
        }
        return parent;
    }

    private static void RememberMove(RectTransform rt)
    {
        for (int i = 0; i < savedMoves.Count; i++)
        {
            if (savedMoves[i].rt == rt)
            {
                return; // keep the original baseline, not the parked position
            }
        }
        SavedMove m = new SavedMove();
        m.rt = rt;
        m.pos = rt.anchoredPosition;
        savedMoves.Add(m);
    }

    // Drop a rect straight down until its bottom edge sits EDGE_MARGIN above
    // the panel floor, leaving its x alone. Only ever downwards: pulling
    // something UP here would push it into the grid, which is the opposite of
    // the point.
    private static void ParkOnFloor(RectTransform rt, Vector3[] panelCorners,
                                    bool verbose)
    {
        if (rt == null)
        {
            return;
        }
        Vector3[] c = new Vector3[4];
        rt.GetWorldCorners(c);
        float wanted = panelCorners[0].y + EDGE_MARGIN * rt.lossyScale.y;
        float shift = wanted - c[0].y;
        if (shift > -1f)
        {
            return; // already on the floor, or below where we would put it
        }
        RememberMove(rt);
        rt.position = rt.position + new Vector3(0f, shift, 0f);
        if (verbose)
        {
            UIFixesPlugin.Log.LogInfo(string.Format(
                "BigBang layout: dropped '{0}' by {1:F0} onto the panel floor",
                rt.gameObject.name, -shift));
        }
    }

    private static void RememberScroll(RectTransform rt)
    {
        if (savedScroll == rt)
        {
            return;
        }
        savedScroll = rt;
        savedScrollSize = rt.sizeDelta;
        savedScrollPos = rt.anchoredPosition;
    }

    // Moves a rect's bottom edge by delta (positive = up) keeping its top
    // edge where it is, for both stretched and fixed-height anchoring.
    private static void SetBottomEdge(RectTransform rt, float delta)
    {
        if (Mathf.Abs(rt.anchorMax.y - rt.anchorMin.y) > 0.0001f)
        {
            rt.offsetMin = rt.offsetMin + new Vector2(0f, delta);
            return;
        }
        rt.sizeDelta = rt.sizeDelta - new Vector2(0f, delta);
        rt.anchoredPosition = rt.anchoredPosition + new Vector2(0f, delta * (1f - rt.pivot.y));
    }
}

// --------------------------------------------------------------------------
// v1.3.0: the Big Bang upgrade cards themselves.
//
// Fuzzied on the five upgrades the bigbang-plus data patch adds: "the text is
// almost impossible to read". Two separate causes:
//
//  1. No artwork. Utils.LoadSprite("BigBang", <id>) looks the icon up by the
//     upgrade's id, and the community build obviously ships no sprite for an
//     id the mod invented, so the card draws the "?" placeholder. Each new
//     line is mapped onto the vanilla icon closest to what it does.
//
//  2. The bonus line is a TextMeshPro label with auto-sizing, so it shrinks
//     until the text fits. Two of the bonus-type names are ~38 characters
//     ("Production Efficiency Min Value Improve"), which shrinks the label
//     into the single digits. The names themselves are shortened by the
//     bigbang-plus data patch; this puts a floor under the font size so no
//     bonus line can shrink to unreadable again.
//
// The measured font size of the first card is logged once, so the floor can
// be tuned against real numbers instead of guessed at.
// --------------------------------------------------------------------------
[HarmonyPatch(typeof(BigBangUpgradeItem), "UpdateUI")]
public static class BigBangItemTextPatch
{
    private const float MIN_FONT = 14f;

    private static readonly FieldInfo ImageField =
        AccessTools.Field(typeof(BigBangUpgradeItem), "image");
    private static readonly FieldInfo UpgradeSetField =
        AccessTools.Field(typeof(BigBangUpgradeItem), "upgradeSet");
    private static readonly FieldInfo CurrentLabelField =
        AccessTools.Field(typeof(BigBangUpgradeItem), "currentBonusLabel");
    private static readonly FieldInfo NextLabelField =
        AccessTools.Field(typeof(BigBangUpgradeItem), "nextLevelBonusLabel");
    private static readonly FieldInfo CurrentLevelField =
        AccessTools.Field(typeof(BigBangUpgradeItem), "currentLevel");

    // new id -> vanilla id whose icon stands in for it
    private static readonly Dictionary<string, string> IconFallback =
        new Dictionary<string, string>
        {
            { "deterioration_floor_up", "resource_gain_up" },
            { "deterioration_slowdown", "energy_gain_up" },
            { "path_knowledge_up", "reduce_travel_time" },
            { "spaceship_dieting", "increase_engine_cap" },
            { "star_essence_gain_up", "stardust_gain_up" }
        };

    private static bool loggedMetrics;
    private static bool loggedFailure;
    private static bool loggedMaxed;

    public static void Postfix(BigBangUpgradeItem __instance)
    {
        try
        {
            FixIcon(__instance);
            FixMaxedLine(__instance);
            FloorFont(CurrentLabelField.GetValue(__instance) as LangText, false);
            FloorFont(NextLabelField.GetValue(__instance) as LangText, true);
        }
        catch (Exception e)
        {
            // UpdateUI runs for every card on every click; one throw here
            // would become a wall of log spam.
            if (!loggedFailure)
            {
                loggedFailure = true;
                UIFixesPlugin.Log.LogWarning("BigBang item: skipped - " + e);
            }
        }
    }

    private static void FixIcon(BigBangUpgradeItem item)
    {
        Image image = ImageField.GetValue(item) as Image;
        if (image == null || image.sprite != null)
        {
            return; // vanilla line, artwork already found
        }
        BigBangUpgradeSet set = UpgradeSetField.GetValue(item) as BigBangUpgradeSet;
        if (set == null || set.id == null)
        {
            return;
        }
        string standIn;
        if (!IconFallback.TryGetValue(set.id, out standIn))
        {
            return;
        }
        Sprite sprite = Utils.LoadSprite("BigBang", standIn);
        if (sprite != null)
        {
            image.sprite = sprite;
        }
    }

    // A line at its top level has no next level, and vanilla fills the next
    // level's label with the same "Bonus Inactive" it uses for level zero.
    // Say what the button beside it already says instead.
    private static void FixMaxedLine(BigBangUpgradeItem item)
    {
        BigBangUpgradeSet set = UpgradeSetField.GetValue(item) as BigBangUpgradeSet;
        LangText label = NextLabelField.GetValue(item) as LangText;
        if (set == null || label == null)
        {
            return;
        }
        int level = (int)CurrentLevelField.GetValue(item);
        if (level <= 0 || set.GetUpgrade(level + 1) != null)
        {
            return; // not maxed, or a set with nothing in it at all
        }
        string maxed = Localisation.GetLocalisation(
            "panels.bigBang.tabs.upgrades.upgradeButton.maxLevel");
        if (string.IsNullOrEmpty(maxed))
        {
            return; // no string to put there, leave vanilla alone
        }
        label.SetText(maxed);
        if (!loggedMaxed)
        {
            loggedMaxed = true;
            UIFixesPlugin.Log.LogInfo(
                "BigBang item: '" + set.id + "' is at level " + level +
                " with no level above it, next-level label set to '" +
                maxed.Replace("\n", " ") + "' instead of the no-bonus string");
        }
    }

    private static void FloorFont(LangText label, bool measure)
    {
        if (label == null)
        {
            return;
        }
        TextMeshProUGUI tmp = label.GetComponentInChildren<TextMeshProUGUI>(true);
        if (tmp == null)
        {
            return;
        }
        if (measure && !loggedMetrics)
        {
            loggedMetrics = true;
            UIFixesPlugin.Log.LogInfo(string.Format(
                "BigBang item: bonus label auto={0} size={1:F1} min={2:F1} max={3:F1} box {4:F0}x{5:F0} text '{6}'",
                tmp.enableAutoSizing, tmp.fontSize, tmp.fontSizeMin, tmp.fontSizeMax,
                tmp.rectTransform.rect.width, tmp.rectTransform.rect.height,
                tmp.text.Replace("\n", " / ")));
        }
        if (tmp.enableAutoSizing && tmp.fontSizeMin < MIN_FONT)
        {
            tmp.fontSizeMin = MIN_FONT;
        }
    }
}

// v1.5.0: artwork for cards this mod adds.
//
// UIColorManager.GetCardIm is just Utils.LoadSprite("Card/Image", id), which
// returns null for an id that has no artwork in the game's bundles - and a
// card with a null sprite draws as an empty frame. Patching the lookup itself
// rather than one panel means every place a card is drawn (deck, bag, alchemy
// preview, battle hand, tooltips) gets the stand-in.
[HarmonyPatch(typeof(UIColorManager), "GetCardIm")]
public static class CardIconFallbackPatch
{
    // new id -> vanilla id whose artwork stands in for it
    private static readonly Dictionary<string, string> IconFallback =
        new Dictionary<string, string>
        {
            { "lightning_mastery", "lightning_action" }
        };

    public static void Postfix(string id, ref Sprite __result)
    {
        if (__result != null || id == null)
        {
            return; // the game found its own artwork
        }
        string standIn;
        if (!IconFallback.TryGetValue(id, out standIn))
        {
            return;
        }
        __result = Utils.LoadSprite("Card/Image", standIn);
    }
}

// v1.7.0: the message log. See the header for why this is what makes clicks
// stop registering rather than merely making the game feel slow.
[HarmonyPatch(typeof(MessagesPanel), "NewMessage")]
public static class MessageFloodPatch
{
    // One per source per this long. Ten ticks a second is the rate we are
    // damming; anything a person could read is far below it.
    private const float THROTTLE_SECONDS = 2f;

    private static readonly FieldInfo MessageContentField =
        AccessTools.Field(typeof(MessagesPanel), "messageContent");

    // Keyed on MessageSource. Only research and infra are ever throttled, but
    // a dictionary keeps it honest if the game grows another noisy source.
    private static readonly Dictionary<int, float> lastShown =
        new Dictionary<int, float>();
    private static readonly Dictionary<int, int> suppressed =
        new Dictionary<int, int>();

    private static long totalSuppressed;
    private static float nextReport;

    // Only the level up flood. A completion notice carries life 0 and is the
    // thing the player actually wants to see, so it always goes through.
    private static bool IsFlood(Message message)
    {
        if (message == null || message.life <= 0)
        {
            return false;
        }
        return message.source == MessageSource.research
            || message.source == MessageSource.infra;
    }

    private static bool Prefix(ref Message message)
    {
        try
        {
            if (UIFixesPlugin.cfgTameMessages == null
                || !UIFixesPlugin.cfgTameMessages.Value
                || !IsFlood(message))
            {
                return true;
            }

            int key = (int)message.source;
            float now = Time.unscaledTime;
            float last;
            if (lastShown.TryGetValue(key, out last)
                && now - last < THROTTLE_SECONDS)
            {
                int held;
                suppressed.TryGetValue(key, out held);
                suppressed[key] = held + 1;
                totalSuppressed++;
                Report(now);
                return false; // swallowed; it is counted into the next one
            }

            lastShown[key] = now;

            int standingFor;
            suppressed.TryGetValue(key, out standingFor);
            if (standingFor > 0)
            {
                suppressed[key] = 0;
                // The content is already a mix of {tokens} and plain text, so
                // a plain tail survives Localisation untouched.
                message = new Message(message.messageLevel,
                    message.content + " (+" + standingFor + ")",
                    message.life, message.source);
            }
            Report(now);
            return true;
        }
        catch (Exception e)
        {
            // Never let a logging fix be the thing that breaks the game.
            UIFixesPlugin.Log.LogWarning(
                "Message log throttle failed, letting it through: " + e.Message);
            return true;
        }
    }

    // Quiet unless it is actually doing something, and at most once a minute -
    // this runs in the middle of the storm it is damming.
    private static void Report(float now)
    {
        if (totalSuppressed == 0 || now < nextReport)
        {
            return;
        }
        nextReport = now + 60f;
        UIFixesPlugin.Log.LogInfo("Message log: folded away "
            + totalSuppressed + " level up message(s) so far");
    }

    private static void Postfix(MessagesPanel __instance)
    {
        try
        {
            if (UIFixesPlugin.cfgMaxMessages == null || MessageContentField == null)
            {
                return;
            }
            int cap = UIFixesPlugin.cfgMaxMessages.Value;
            if (cap < 1)
            {
                return;
            }
            GameObject content =
                (GameObject)MessageContentField.GetValue(__instance);
            if (content == null)
            {
                return;
            }
            Transform parent = content.transform;
            int count = parent.childCount;
            if (count <= cap)
            {
                return;
            }
            // Downwards, not a while loop: Destroy is deferred to the end of
            // the frame, so childCount does not fall as we go. See the header.
            for (int i = count - 1; i >= cap; i--)
            {
                Transform child = parent.GetChild(i);
                if (child != null)
                {
                    UnityEngine.Object.Destroy(child.gameObject);
                }
            }
        }
        catch (Exception e)
        {
            UIFixesPlugin.Log.LogWarning(
                "Could not trim the message log: " + e.Message);
        }
    }
}


// ---------------------------------------------------------------------------
// v1.8.1 diagnostic: why a click on a card does nothing.
//
// Two guesses have now been spent on this. 1.7.0 dammed the message log to buy
// back frames, which worked and is still working (the log says it folded away
// 631 messages in the run that produced the complaint). 1.8.0 scaled the drag
// threshold with frame time, which also worked and reported "15px (was 10px)
// at 41fps" - and 41fps is a perfectly healthy frame rate, so whatever is
// eating the clicks is not the frame rate and never was.
//
// So stop guessing and measure. CardItem implements IPointerClickHandler and
// nothing else, so it never hears the press; if the click is cancelled, the
// card simply never finds out. Watching the raw mouse buttons from Update
// gives us the half the card cannot see, and a postfix on OnPointerClick gives
// us the other half. One line on release says which of the three cases it was:
//
//   reached the card          -> input is fine, the bug is in what runs next
//   not reached, travel large -> still the drag threshold, raise it further
//   not reached, travel small -> something else is swallowing it entirely
//
// Everything here is read-only.
internal static class ClickProbe
{
    private static bool down;
    private static Vector3 downAt;
    private static float downTime;
    private static float travel;

    internal static bool reachedCard;
    internal static string cardSeen;

    internal static void Tick()
    {
        try
        {
            if (UIFixesPlugin.cfgLogCardClicks == null
                || !UIFixesPlugin.cfgLogCardClicks.Value)
            {
                return;
            }
            if (Input.GetMouseButtonDown(0))
            {
                down = true;
                downAt = Input.mousePosition;
                downTime = Time.unscaledTime;
                travel = 0f;
                reachedCard = false;
                cardSeen = null;
                return;
            }
            if (!down)
            {
                return;
            }
            // Peak distance from the press point, not the distance at release:
            // the threshold is tripped by the furthest sample, and a hand that
            // wanders out and comes back would otherwise read as no movement.
            float d = Vector3.Distance(Input.mousePosition, downAt);
            if (d > travel)
            {
                travel = d;
            }
            if (!Input.GetMouseButtonUp(0))
            {
                return;
            }
            down = false;

            // Nothing to say about clicks that were never aimed at a card,
            // which is most of them.
            if (!reachedCard && cardSeen == null && travel < 1f)
            {
                return;
            }
            int threshold = (EventSystem.current == null)
                ? -1 : EventSystem.current.pixelDragThreshold;
            float frame = Time.smoothDeltaTime;
            UIFixesPlugin.Log.LogInfo("Card click: "
                + (reachedCard
                    ? ("reached '" + cardSeen + "'")
                    : "did NOT reach a card")
                + ", pointer moved " + travel.ToString("0.0") + "px"
                + ", threshold " + threshold + "px"
                + ", held " + (Time.unscaledTime - downTime).ToString("0.00") + "s"
                + ", " + Mathf.RoundToInt(1f / Mathf.Max(frame, 0.0001f)) + "fps");
        }
        catch (Exception e)
        {
            UIFixesPlugin.cfgLogCardClicks.Value = false;
            UIFixesPlugin.Log.LogWarning("Card click probe off: " + e.Message);
        }
    }
}

// The card end of the probe. Runs before CardItem.Clicked does its work, so a
// card that reports "reached" and still does nothing puts the blame squarely
// on what happens next rather than on the input.
[HarmonyPatch(typeof(CardItem), "OnPointerClick")]
public static class CardClickProbePatch
{
    private static void Prefix(CardItem __instance)
    {
        try
        {
            if (UIFixesPlugin.cfgLogCardClicks == null
                || !UIFixesPlugin.cfgLogCardClicks.Value || __instance == null)
            {
                return;
            }
            ClickProbe.reachedCard = true;
            ClickProbe.cardSeen = __instance.location.ToString()
                + ":" + ((__instance.card == null)
                    ? "(no card)" : __instance.card.idLevelString);
        }
        catch (Exception)
        {
            // A probe must never be the reason a click fails.
        }
    }
}

// And what happens next, for the perm set specifically. PutBackToBag does
// nothing at all when CardLoadout.RemoveCard returns false, which happens when
// the exact id-and-level string the card is carrying is not in the loadout.
// That is a silent no-op and looks exactly like a dropped click.
[HarmonyPatch(typeof(CardBag), "PutBackToBag", new Type[] { typeof(PermanentCard) })]
public static class PutBackToBagProbePatch
{
    private static readonly FieldInfo LoadoutField =
        AccessTools.Field(typeof(CardBag), "currentPermSetLoadout");

    private static void Prefix(CardBag __instance, PermanentCard card)
    {
        try
        {
            if (UIFixesPlugin.cfgLogCardClicks == null
                || !UIFixesPlugin.cfgLogCardClicks.Value
                || card == null || LoadoutField == null)
            {
                return;
            }
            CardLoadout loadout = LoadoutField.GetValue(__instance) as CardLoadout;
            string want = card.idLevelString;
            bool present = loadout != null && loadout.idLevelStrings != null
                && loadout.idLevelStrings.Contains(want);
            UIFixesPlugin.Log.LogInfo("Perm set remove: asked for '" + want
                + "', loadout "
                + ((loadout == null || loadout.idLevelStrings == null)
                    ? "is missing"
                    : ("holds [" + string.Join(", ",
                        loadout.idLevelStrings.ToArray()) + "]"))
                + ", so this will "
                + (present ? "work" : "do NOTHING"));
        }
        catch (Exception e)
        {
            UIFixesPlugin.Log.LogWarning("Perm set probe: " + e.Message);
        }
    }
}


// ---------------------------------------------------------------------------
// v1.9.0: a previewed loadout is read only. See the header.

internal static class LoadoutPreview
{
    private static readonly FieldInfo PermRowField =
        AccessTools.Field(typeof(CardPanel), "currentPermSetInBag");
    private static readonly FieldInfo DeckRowField =
        AccessTools.Field(typeof(CardPanel), "currentDeckInBag");
    private static readonly FieldInfo PermSheetField =
        AccessTools.Field(typeof(CardPanel), "permBagDisableIm");
    private static readonly FieldInfo DeckSheetField =
        AccessTools.Field(typeof(CardPanel), "deckBagDisableIm");

    private static GameObject permSheet;
    private static GameObject deckSheet;

    // One line per stuck click is enough to identify this in a log, and a
    // player who ignores it should not pay for it with a growing file.
    private const int MAX_SAID = 8;
    private static int said;

    internal static CardPanel Panel()
    {
        MainPanel main = MainPanel.shared;
        return (main == null) ? null : main.cardPanel;
    }

    // True while the row on screen is a copy of a SAVED loadout rather than
    // the live set, which is exactly when a click must not touch anything.
    internal static bool PreviewingPermSet()
    {
        CardPanel panel = Panel();
        return panel != null && !panel.displayingCurrentPermSet;
    }

    internal static bool PreviewingDeck()
    {
        CardPanel panel = Panel();
        return panel != null && !panel.displayingCurrentDeck;
    }

    internal static void Say(string which)
    {
        if (said >= MAX_SAID)
        {
            return;
        }
        said++;
        UIFixesPlugin.Log.LogInfo("Card panel: a saved " + which + " loadout "
            + "is selected, so that row is a preview of the saved list and "
            + "the game would have taken the card out of your live set "
            + "instead. Left alone. Press Load to make the loadout live "
            + "first, or pick the current one from the dropdown.");
    }

    // The grey sheet the bag already gets, over the equipped row as well.
    // It is a sibling of the row rather than a child: the row is emptied into
    // an object pool by SaveAllChildrenToPool<MonoBehaviour>, which would
    // swallow anything of ours parked inside it.
    internal static void ShowSheet(CardPanel panel, bool on, bool perm)
    {
        try
        {
            GameObject row = (perm ? PermRowField : DeckRowField)
                .GetValue(panel) as GameObject;
            if (row == null)
            {
                return;
            }
            GameObject sheet = perm ? permSheet : deckSheet;
            if (sheet == null)
            {
                GameObject model = (perm ? PermSheetField : DeckSheetField)
                    .GetValue(panel) as GameObject;
                sheet = Build(row, model);
                if (sheet == null)
                {
                    return;
                }
                if (perm) { permSheet = sheet; } else { deckSheet = sheet; }
            }
            if (on)
            {
                // Re-measured every time: the row moves when the slot count
                // changes, and Loadout Plus can change that at any moment.
                Cover(sheet, row);
            }
            sheet.SetActive(on);
        }
        catch (Exception e)
        {
            UIFixesPlugin.Log.LogWarning("Loadout preview sheet: " + e.Message);
        }
    }

    private static GameObject Build(GameObject row, GameObject model)
    {
        if (row.transform.parent == null)
        {
            return null;
        }
        GameObject sheet = new GameObject("CommunityLoadoutPreviewSheet",
            new Type[] { typeof(RectTransform), typeof(Image),
                typeof(LayoutElement) });
        sheet.transform.SetParent(row.transform.parent, false);

        // The row's parent may be running a layout group, and an extra child
        // would otherwise be dealt a cell of its own and shove the row along.
        sheet.GetComponent<LayoutElement>().ignoreLayout = true;

        // v1.9.1: a flat tint of our own, never the model's sprite, colour or
        // material. The model is the sheet the game lays over the BAG, which
        // is a large field of cards and survives being washed over; the row is
        // one line of icons and does not. See the header.
        Image im = sheet.GetComponent<Image>();
        im.sprite = null;
        im.material = null;
        im.color = new Color(0f, 0f, 0f, TintAlpha());

        // Eats the click as well as marking the row, so this still holds if
        // some other path reaches the card without going through PutBackToBag.
        // An Image with no sprite still takes a raycast at any alpha, so this
        // is true even with the tint turned all the way down.
        im.raycastTarget = true;

        NoteOnce(model, im.color);
        return sheet;
    }

    private static float TintAlpha()
    {
        if (UIFixesPlugin.cfgPreviewTint == null)
        {
            return 0.2f;
        }
        float a = UIFixesPlugin.cfgPreviewTint.Value;
        if (a < 0f) { return 0f; }
        if (a > 1f) { return 1f; }
        return a;
    }

    private static bool noted;

    // "The row looks empty" and "the row is covered" are the same picture from
    // the player's side. This is the only way to tell them apart afterwards.
    private static void NoteOnce(GameObject model, Color used)
    {
        if (noted)
        {
            return;
        }
        noted = true;
        Image from = (model == null) ? null : model.GetComponent<Image>();
        string had = (from == null)
            ? "no Image on the game's bag sheet"
            : ("the game's bag sheet is rgba " + from.color.r.ToString("0.00")
               + "," + from.color.g.ToString("0.00") + ","
               + from.color.b.ToString("0.00") + "," + from.color.a.ToString("0.00")
               + (from.sprite == null ? ", no sprite" : (", sprite " + from.sprite.name)));
        UIFixesPlugin.Log.LogInfo("Loadout preview sheet: " + had
            + "; using a flat tint at alpha " + used.a.ToString("0.00")
            + " instead, so the cards under it stay readable");
    }

    // Same parent, so copying the row's own rectangle lands exactly on it.
    private static void Cover(GameObject sheet, GameObject row)
    {
        RectTransform a = sheet.GetComponent<RectTransform>();
        RectTransform b = row.GetComponent<RectTransform>();
        if (a == null || b == null)
        {
            return;
        }
        a.anchorMin = b.anchorMin;
        a.anchorMax = b.anchorMax;
        a.pivot = b.pivot;
        a.anchoredPosition = b.anchoredPosition;
        a.sizeDelta = b.sizeDelta;
        a.localScale = b.localScale;
        a.SetAsLastSibling();
        a.GetComponent<Image>().color =
            new Color(0f, 0f, 0f, TintAlpha()); // config can change mid-run
    }
}

// The two halves of the fix. The guard is on CardBag rather than on the click,
// because PutBackToBag is what actually edits the live list and other callers
// would want the same answer.
[HarmonyPatch(typeof(CardBag), "PutBackToBag", new Type[] { typeof(PermanentCard) })]
public static class PermSetPreviewGuardPatch
{
    private static bool Prefix()
    {
        if (!LoadoutPreview.PreviewingPermSet())
        {
            return true;
        }
        LoadoutPreview.Say("perm set");
        return false;
    }
}

[HarmonyPatch(typeof(CardBag), "PutBackToBag", new Type[] { typeof(BattleCard) })]
public static class DeckPreviewGuardPatch
{
    private static bool Prefix()
    {
        if (!LoadoutPreview.PreviewingDeck())
        {
            return true;
        }
        LoadoutPreview.Say("deck");
        return false;
    }
}

// And the visible half, hung on the game's own "a loadout is selected" switch
// so the row greys out and clears at exactly the same moments the bag does.
[HarmonyPatch(typeof(CardPanel), "DisplayPermSetLoadoutsNameSLControls")]
public static class PermSetPreviewSheetPatch
{
    private static void Postfix(CardPanel __instance, bool on)
    {
        LoadoutPreview.ShowSheet(__instance, on, true);
    }
}

[HarmonyPatch(typeof(CardPanel), "DisplayDeckLoadoutsNameSLControls")]
public static class DeckPreviewSheetPatch
{
    private static void Postfix(CardPanel __instance, bool on)
    {
        LoadoutPreview.ShowSheet(__instance, on, false);
    }
}

// v1.11.0: the travel panel is stuck on the flight you just finished. See the
// header for why nothing on the arrival path clears it.
[HarmonyPatch(typeof(SidePanel), "Arrive")]
public static class ArrivalPanelRefreshPatch
{
    private static bool loggedFailure;

    private static void Postfix(SidePanel __instance)
    {
        try
        {
            // Exactly what BigBangClean does, in the same order. Using the
            // game's own reset rather than poking the labels means anything
            // else that panel learns to show in a later version gets reset
            // too, without this patch having to hear about it.
            __instance.ReloadUI();
            __instance.SetupDestSelector(true);
        }
        catch (Exception e)
        {
            // A landing must not fail because a label would not redraw. The
            // worst case without this is the stale panel that was there
            // before the fix, which is survivable; a throw inside Arrive
            // would not be, because Auto Start's own arrival work is hung
            // off the same method.
            if (!loggedFailure)
            {
                loggedFailure = true;
                UIFixesPlugin.Log.LogWarning("Could not refresh the travel "
                    + "panel on arrival: " + e.Message + ". The panel may "
                    + "still show the finished flight until you pick a "
                    + "destination. Nothing else is affected.");
            }
        }
    }
}

// v1.12.0. Fuzzied: "that it remembers it position it was closed from, so it
// stays in the place i drag it to from time to time".
//
// Unity 2020.3 remembers the window's size and mode in the registry
// (Screenmanager Resolution Width, Fullscreen mode) and which monitor it was
// on (UnitySelectMonitor), but not where on that monitor it was. Every launch
// puts it back in Unity's default spot.
//
// So this keeps the spot itself. Every couple of seconds it reads the
// window's outer rectangle and, when it has moved, writes the top left corner
// to the config. On the next launch it moves the window back there, once,
// without resizing it, without changing what is in front and without taking
// focus. Size stays Unity's business, because Unity already remembers it and
// fighting it over the same number would be two owners for one value.
//
// Guards, because a window put where nobody can see it is worse than one in
// the wrong place:
//   - Windows only. Mac players get the same dll and nothing here runs there.
//   - Windowed only. Full screen and a maximized or minimized window are
//     neither saved nor moved.
//   - The saved spot is only used if the middle of the title bar would land
//     on a monitor that exists right now, so unplugging a screen cannot strand
//     the game off the edge of the desktop.
//   - Nothing is saved until the restore has been tried, so the default spot
//     Unity opens in cannot overwrite the one the player chose.
internal static class WindowSpot
{
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint MONITOR_DEFAULTTONULL = 0;
    private const float CHECK_SECONDS = 2f;
    // Unity can still be settling its own window in the first frames, so the
    // move waits this long after the plugin wakes and is tried once.
    private const float RESTORE_AFTER = 1f;

    internal static ConfigEntry<bool> cfgRemember;
    internal static ConfigEntry<string> cfgSpot;

    private static bool off;
    private static bool restored;
    private static float wokeAt;
    private static float nextCheck;
    private static IntPtr window = IntPtr.Zero;
    private static uint myPid;
    // Kept in a field so the delegate cannot be collected while Windows is
    // still calling it back.
    private static readonly EnumProc matcher = Match;

    internal static void Bind(ConfigFile config)
    {
        cfgRemember = config.Bind("Window", "RememberPosition", true,
            "Puts the game window back where it was when you closed it. The "
            + "game remembers the window's size but not where it was, so every "
            + "launch it opens in the same default spot. Windowed mode only, "
            + "and a spot on a screen that is no longer connected is ignored.");
        cfgSpot = config.Bind("Window", "LastPosition", "",
            "Where the top left corner of the window was last seen, in screen "
            + "pixels, as x,y. Written by the game while it runs. Empty means "
            + "not known yet. Clear it to let the game use its default spot.");
        wokeAt = Time.unscaledTime;
        off = Application.platform != RuntimePlatform.WindowsPlayer
            || !cfgRemember.Value;
    }

    internal static void Tick()
    {
        if (off || Time.unscaledTime < nextCheck)
        {
            return;
        }
        try
        {
            if (!restored)
            {
                if (Time.unscaledTime - wokeAt < RESTORE_AFTER)
                {
                    return;
                }
                restored = true;
                Restore();
            }
            nextCheck = Time.unscaledTime + CHECK_SECONDS;
            Remember();
        }
        catch (Exception e)
        {
            off = true;
            UIFixesPlugin.Log.LogWarning("Could not keep track of the window "
                + "position, leaving it where Unity puts it: " + e.Message);
        }
    }

    // Called on quit as well, so a drag in the last two seconds still counts.
    internal static void Remember()
    {
        if (off || !restored)
        {
            return;
        }
        IntPtr hWnd = Window();
        RECT r;
        if (!Usable(hWnd) || !GetWindowRect(hWnd, out r))
        {
            return;
        }
        string spot = r.Left + "," + r.Top;
        if (spot != cfgSpot.Value)
        {
            cfgSpot.Value = spot;
        }
    }

    private static void Restore()
    {
        int x, y;
        if (!Parse(cfgSpot.Value, out x, out y))
        {
            return;
        }
        IntPtr hWnd = Window();
        RECT r;
        if (!Usable(hWnd) || !GetWindowRect(hWnd, out r))
        {
            UIFixesPlugin.Log.LogInfo("Window position: not restored, the "
                + "game is not in a normal window right now.");
            return;
        }
        if (r.Left == x && r.Top == y)
        {
            return;
        }
        int width = r.Right - r.Left;
        POINT grip = new POINT { X = x + width / 2, Y = y + 10 };
        if (MonitorFromPoint(grip, MONITOR_DEFAULTTONULL) == IntPtr.Zero)
        {
            UIFixesPlugin.Log.LogInfo("Window position: " + x + "," + y
                + " is not on any connected screen, left at "
                + r.Left + "," + r.Top + ".");
            return;
        }
        SetWindowPos(hWnd, IntPtr.Zero, x, y, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        UIFixesPlugin.Log.LogInfo("Window position: moved back to " + x + ","
            + y + " (Unity opened it at " + r.Left + "," + r.Top + ").");
    }

    private static bool Usable(IntPtr hWnd)
    {
        return hWnd != IntPtr.Zero
            && Screen.fullScreenMode == FullScreenMode.Windowed
            && IsWindowVisible(hWnd) && !IsIconic(hWnd) && !IsZoomed(hWnd);
    }

    private static bool Parse(string text, out int x, out int y)
    {
        x = y = 0;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        string[] parts = text.Split(',');
        return parts.Length == 2
            && int.TryParse(parts[0].Trim(), out x)
            && int.TryParse(parts[1].Trim(), out y);
    }

    // The game's own top level window: this process, Unity's window class.
    // GetActiveWindow would be simpler and is wrong exactly when it matters,
    // because a game started behind other windows is not the active one.
    private static IntPtr Window()
    {
        if (window != IntPtr.Zero)
        {
            return window;
        }
        myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        EnumWindows(matcher, IntPtr.Zero);
        return window;
    }

    private static bool Match(IntPtr hWnd, IntPtr lParam)
    {
        uint pid;
        GetWindowThreadProcessId(hWnd, out pid);
        if (pid != myPid)
        {
            return true;
        }
        StringBuilder name = new StringBuilder(64);
        GetClassName(hWnd, name, name.Capacity);
        if (name.ToString() == "UnityWndClass")
        {
            window = hWnd;
            return false;
        }
        return true;
    }
}
