// Space Travel Idle community mod - make the Research / Infra / card filters
// survive a reload (vanilla bug)
//
// Reported by Fuzzied 2026-09-09: "Under Techno and Research/Infra the Completion
// filter resets every time you load into the game. For me I have them to Not
// Maxed as I want to see what remains and what to select."
//
// The game clearly MEANT to remember them. There is a whole FilterData object
// in the save, SaveLoadManager writes it on every save
// (TechnoPanel.ExportFilterData -> ResearchFilter/InfraFilter.ExportFilterData)
// and the filters read it back in their own Start():
//
//     LoadFilterData(SaveLoadManager.shared.currentSave.filterData);
//
// And the save really does contain the right value - Fuzzied's autosave holds
// researchCompletionFilterValue = "Not Max Level". So the write half works and
// the read half is what fails.
//
// The read half is fragile in two ways, and both are fixed the same way here:
//
//   1. SaveLoadManager.currentSave is only assigned on the LAST line of the
//      save-loading routine. ResearchFilter.Start() dereferences it directly,
//      with no null check, so if a filter's Start() happens to run before the
//      save finishes loading the whole method dies on a NullReferenceException
//      - after SetOptions() has already reset the dropdown to index 0 ("All"),
//      which is exactly the symptom.
//
//   2. SearchDropdown.SetOptionValue(string) sets the value and then calls
//      OnValueChange(), which throws that value away again and re-derives it
//      from the TMP_Dropdown widget's own index:
//
//          dropdownModel.SetSelectedOption(showingOptionIds[dropdown.value]);
//
//      That is fine after a real click, where the widget index IS the truth,
//      but during a restore the model is the truth and the widget is stale.
//      Any frame where TMP_Dropdown has not caught up yet - and TMP_Dropdown
//      builds its item list lazily, on first use - lands back on index 0.
//
// The fix does not try to guess which one bit on any given launch. It waits
// until the save and the Techno panel are both genuinely alive, then re-applies
// the saved values through the path that cannot self-clobber:
//
//     dd.SetOptionValue(wanted, false);     // shouldInvokeEvent: false
//     dd.onValueChanged.Invoke(dd.value);   // then notify, like a real click
//
// The second line is what makes the list actually re-filter rather than just
// making the dropdown read correctly.
//
// v1.5.0 - the write half is broken too, and that is the real root cause
//
// Fuzzied 2026-09-14: "Not Max Level is reset upon starting the client now too".
// His Player.log for that session has exactly two Filter memory lines, both
// from Awake. The restore never ran at all. Reading the save files off disk
// explains why, and it is worse than a read bug.
//
// A filter dropdown only gets its options in its own Start(), and the panel it
// lives on is an inactive GameObject until the player opens that tab. So on a
// session where you never open Techno, ResearchFilter.Start never runs,
// SearchDropdown.stringValue is
//
//     public string stringValue => dropdownModel.GetSelectedId();
//
// with no options behind it, and ExportFilterData cheerfully writes that null
// into the save:
//
//     filterData.researchCompletionFilterValue = completionFilterDropdown.stringValue;
//
// SaveLoadManager builds a brand new FilterData for every save, so there is no
// old value underneath to survive. One autosave written before you open the
// Techno tab erases the filter you set yesterday. Fuzzied's save folder is full
// of the evidence: 1_Save Slot, 0_Save Slot, departureAuto_Mars and
// departureAuto_Mercury have all eleven fields null, and another five have the
// card fields filled in and every Techno field null.
//
// And an all-null FilterData is indistinguishable from the empty one Unity
// default-constructs on the SaveLoadManager component, which is what IsBlank
// below is for. So the plugin correctly refused to latch onto it, went on
// waiting for a real one, and waited for the rest of the session in total
// silence. That is the missing log.
//
// It is self-perpetuating. A blank save produces a session with no restore,
// which produces another blank save.
//
// Two changes, and the first is the one that matters:
//
//   - A postfix on TechnoPanel.ExportFilterData, which is the last thing to
//     touch the FilterData on the way into the save, refills any field the
//     game left null or empty from the last value that was actually seen.
//     Seen means read out of the save on load, or written by a panel that was
//     genuinely open. A panel that never opened can no longer erase anything.
//
//   - Research and Infra get the same Start postfix the card filters got in
//     1.4.0. Polling for them was always wrong for the same reason: their
//     Start can be minutes away or never come, and five seconds of polling
//     cannot reach it.
//
// The wanted values are also captured in the LoadSave postfix now, straight
// off the Save that was passed in, rather than by watching SaveLoadManager
// .currentSave from Update and guessing when it has become real.
//
// A save that is already all-null has nothing to restore from and nothing can
// change that. Set the filter once and it sticks from then on.
//
// The card bags on the Deck and Permanent tabs (CardFilter) have the identical
// bug in the identical place, so they get the identical treatment - Effect and
// Rarity on both tabs.
//
// Their THIRD dropdown, Tier, is a different and slightly sillier bug: the save
// has had a pair of fields sitting there for it the whole time
//
//     FilterData.deckCardHiLevelFilterValue
//     FilterData.permCardHiLevelFilterValue
//
// and nothing in the entire game ever writes to them or reads from them - the
// Tier filter is simply not saved. So there is nothing here to restore until we
// save it ourselves, which is what the one Harmony patch at the bottom of this
// file does: SaveLoadManager builds a brand new FilterData on every save and
// hands it to CardPanel.ExportFilterData, so we fill in the two forgotten
// fields on the way past. Those fields being dead in vanilla is exactly what
// makes this safe - nothing can be confused by what we put there, and a save
// written with this mod still loads fine without it.
//
// While a filter is on, the card bag builds a second copy of everything the
// filter is hiding (vanilla bug, fixed at the bottom of this file)
//
// Fuzzied 2026-09-10: "Ive seen a couple of times in vanilla that cards gets
// visually duplicated - it just happened now when I filtered on energy and
// turned it off".
//
// CardPanel.DisplayBagCardSets is a diff, not a rebuild. It asks what is on
// screen now, pools whatever no longer belongs, and creates a row for whatever
// is missing. Both halves ask the same question:
//
//     IEnumerable<string> oldCardSetNames =
//         from set in parent.GetComponentsInChildren<CardItemSet>()
//         select ((Object)set).name;
//
// GetComponentsInChildren<T>() with no argument means includeInactive: false,
// and inactive is exactly how the filter hides a card - RunPermBagFilter ends
// on gameObject.SetActive(list2[i]).
//
// So with a filter on, every card the filter hid is invisible to that query.
// The diff concludes those cards are missing from the bag and builds a second
// copy of each. Clear the filter, RunPermBagFilter reactivates the lot - it
// DOES pass includeInactive: true - and both copies appear. That is the
// doubled bag.
//
// It repeats as often as the panel refreshes, which is CardPanel.FixedUpdate
// polling a dirty flag, so a filtered bag left open keeps growing. Fuzzied's
// Player.log has ObjectPool handing out 68 CardItemSet objects for a bag
// holding about half that many distinct cards.
//
// The same missing argument leaks. The stale half of the diff reads from the
// same query, so a card set that is both hidden and genuinely gone from the
// bag is never handed back to the pool - it stays parked under the bag as an
// inactive orphan for the rest of the session.
//
// And a third one sits behind those, because ObjectPool never touches
// activeSelf; GetObjectFromPool only reparents transform.GetChild(0). A card
// set hidden by the filter and then pooled carries activeSelf == false into
// the pool and hands it to whichever card claims it next, which then draws
// nothing at all.
//
// This is a base-game bug and Fuzzied has hit it without the mod. Worth saying
// plainly though: the restore above makes it easier to reach, because vanilla
// forgets your filter when you load and we deliberately do not. A bag that
// comes up already filtered is a bag already in the state that triggers it.
//
// Deliberately conservative:
//   - it only ever runs during the first few seconds after a save is loaded,
//     and stops the moment every dropdown reads what the save asked for, so it
//     can never fight the player;
//   - a value the dropdown does not recognise (a save written in a different
//     language, say) is left alone rather than forced;
//   - everything is wrapped so a change in a future game version turns this
//     into a no-op instead of a crash.
//
// v1.6.0 adds two things to the same panel.
//
// THE SLIDER AFTER A MERGE. Fuzzied: "When cards are merged, either manually per
// card or via the Merge all. Have the slider go all the way to the right so it
// shows the highest value on the card." Merge four level 4 copies into a level
// 5 while you still own other level 4s and the card carries on showing you
// level 4, because CardItemSet.UpdateSliderSettings only jumps to the top when
// the level you were looking at has disappeared completely:
//
//     int highestLevel = currentLevel;
//     edittingLevels = model.levelDict.Keys.ToList();
//     if (!edittingLevels.Contains(highestLevel))
//     {
//         highestLevel = model.GetHighestLevel();
//     }
//     levelSlider.value = edittingLevels.BinarySearch(highestLevel);
//
// The thing you just made is off to the right and you have to go and find it.
// SetModel already knows the answer, it just never runs after a merge:
//
//     UpdateSliderSettings();
//     levelSlider.value = levelSlider.maxValue;
//
// Every merge in the game goes through IdenticalCardSet, one of MergeOneOfLevel,
// MergeAllOfLevel or MergeAll, and the bag wide Merge All button is a loop over
// MergeAll, so three postfixes cover all four buttons. Toggle in settings,
// because it changes what you are looking at rather than fixing something
// broken.
//
// v1.6.1 - 1.6.0 did nothing at all on the common case, and the reason is in
// the line above that used to say "each of them has already called
// view.UpdateSliderSettings by the time we run". They have not. Nothing calls
// it after a merge; IdenticalCardSet calls only view.CheckShowingLevel.
//
// That assumption mattered because levelSlider.value is an INDEX into
// edittingLevels, not a card level, and UpdateSliderSettings is the only thing
// that rebuilds either the list or maxValue. Skip it and the slider is still
// describing the levels you had before the merge: the level you just made is
// not in the list, maxValue is the index of the old top, and the slider is
// already sitting on it. So the old code compared value against a stale max,
// found them equal, concluded it was already at the right end and redrew the
// level you were on.
//
// Fuzzied, 22.09.2026: "I upgraded The bomb to 4 but it didnt move and said 3
// still until I dragged it one level down, back to 3, from 3, to 4."
//
// So call UpdateSliderSettings ourselves first, then move to the top. And
// redraw by calling SliderValueChanged directly rather than relying on the
// write to fire the prefab's listener, because after a rebuild the new top
// level can land on the same index number the old one had. Same number, no
// event, different meaning.
//
// THE PANEL THAT REBUILT ITSELF ON EVERY LEVEL UP. Fuzzied, twice: "I am having
// problem with input lag again not being able to remove cards." It is not lag
// and it is not the mouse. Vanilla chain:
//
//     Research.LevelUp / Infra.LevelUp
//       -> UnlockManager.needsUnlockReqUpdate = true
//       -> UnlockManager.Update -> UnlockRequirementUpdate()
//       -> player.cardBag.ForceReloadUI()        // sets EVERY pending flag
//       -> CardPanel.FixedUpdate -> LoadPermSet() / LoadDeck()
//       -> DisplayEquipedCards()
//            cardItemInBagPool.SaveAllChildrenToPool<CardItemInBag>(parent);
//            ... then re-fetches every card item from the pool
//
// So every single level up tears the equipped card items down and builds them
// again. A click needs the press and the release to land on the same object,
// and if the object went back to the pool in between, Unity never fires
// OnPointerClick, so CardItemInBag.Clicked never runs and the card will not
// come out. Holding the mouse still does not help, because movement was never
// involved. His log measured the storm behind it: 2506 research and infra level
// ups after one arrival, 1152 of them inside a single minute.
//
// So LoadDeck and LoadPermSet now look before they build. If the panel already
// holds exactly the cards it is about to draw, the redraw is skipped and the
// pending flag is cleared as though it had happened.
//
// "Exactly" means the same card OBJECTS, not cards with the same id and level.
// CardBag.ForceReloadCurrentPermSet throws the set away and builds new
// instances from the loadout, and removal is by reference:
//
//     currentPermSet.RemoveCard(card) -> cards.Remove(card)
//
// A panel holding equal but different card objects would look right and refuse
// every click, which is the bug we are fixing rather than a second one to ship.
//
// Compiled with the .NET Framework 4 csc against the game's own
// Assembly-CSharp.dll and the BepInEx 5 core DLLs (see build_plugin.ps1).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

[BepInPlugin("sti.community.filtermemory", "STI Community Filter Memory", "1.6.1")]
public class FilterMemoryPlugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;
    internal static FilterMemoryPlugin shared;

    private static ConfigEntry<bool> cfgEnabled;
    internal static ConfigEntry<bool> cfgSaveCardTier;
    internal static ConfigEntry<bool> cfgFixDuplicates;
    internal static ConfigEntry<bool> cfgKeepUnopened;
    internal static ConfigEntry<bool> cfgSkipUnchangedEquipRedraw;

    // How long after the save loads we are willing to keep nudging, and how
    // often. Ten tries over five seconds is far more than the Techno panel
    // needs, and it is deliberately not enough for the card panel.
    //
    // v1.4.0. Fuzzied's log said 'Filter memory: gave up after 10 pass(es)' on a
    // run where Research and Infra had in fact been restored correctly. The
    // old code ANDed all eleven dropdowns into one 'settled' flag, so one
    // dropdown that was not ready made the whole pass look like a failure, and
    // the give-up line claimed nothing had worked when nine tenths of it had.
    //
    // The ones that were not ready are always the same three-plus-three: the
    // Deck and Permanent card filters. CardFilter builds its dropdown options
    // in Start, and the card panel's GameObject is inactive until the player
    // opens that tab, so Start may not run for minutes and need never run at
    // all. Polling for it is the wrong shape - five seconds is far too short
    // and forever is far too long.
    //
    // So each dropdown now carries its own done flag, the poll stops when the
    // Techno ones have landed, and the card ones are applied from a postfix on
    // CardFilter.Start, which fires at exactly the moment they become real and
    // immediately after the game's own LoadDeckFilterData / LoadPermFilterData
    // have run, so ours is the value that sticks.
    private const int MAX_ATTEMPTS = 10;
    private const float RETRY_SECONDS = 0.5f;

    private static readonly FieldInfo ResearchFilterField =
        typeof(TechnoPanel).GetField("researchFilter",
            BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo InfraFilterField =
        typeof(TechnoPanel).GetField("infraFilter",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo ResearchCompletionField =
        typeof(ResearchFilter).GetField("completionFilterDropdown",
            BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo ResearchBonusField =
        typeof(ResearchFilter).GetField("bonusFilterDropdown",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo InfraCompletionField =
        typeof(InfraFilter).GetField("completionFilterDropdown",
            BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo InfraCostField =
        typeof(InfraFilter).GetField("costFilterDropdown",
            BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo InfraBonusField =
        typeof(InfraFilter).GetField("bonusFilterDropdown",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo DeckFilterField =
        typeof(CardPanel).GetField("deckFilter",
            BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo PermFilterField =
        typeof(CardPanel).GetField("permFilter",
            BindingFlags.Instance | BindingFlags.NonPublic);

    internal static readonly FieldInfo CardEffectField =
        typeof(CardFilter).GetField("effectFilterDropdown",
            BindingFlags.Instance | BindingFlags.NonPublic);
    internal static readonly FieldInfo CardTierField =
        typeof(CardFilter).GetField("tierFilterDropdown",
            BindingFlags.Instance | BindingFlags.NonPublic);
    internal static readonly FieldInfo CardRarityField =
        typeof(CardFilter).GetField("rarityFilterDropdown",
            BindingFlags.Instance | BindingFlags.NonPublic);

    // What the save asked for, captured once so that an autosave written in
    // the meantime - which would record the WRONG, reset-to-All value - cannot
    // overwrite our intent before we have managed to apply it.
    private bool captured;
    private string wantResearchCompletion;
    private string wantResearchBonus;
    private string wantInfraCompletion;
    private string wantInfraCost;
    private string wantInfraBonus;
    private string wantDeckEffect;
    private string wantDeckRarity;
    private string wantDeckTier;
    private string wantPermEffect;
    private string wantPermRarity;
    private string wantPermTier;

    // One per dropdown, so a pass that fixes nine of eleven is recorded as
    // having fixed nine rather than as a failure, and the two that are left
    // are the only ones a later pass touches.
    private bool doneResearchCompletion;
    private bool doneResearchBonus;
    private bool doneInfraCompletion;
    private bool doneInfraCost;
    private bool doneInfraBonus;
    private bool doneDeckEffect;
    private bool doneDeckRarity;
    private bool doneDeckTier;
    private bool donePermEffect;
    private bool donePermRarity;
    private bool donePermTier;

    private int attempts;
    private float nextAttemptTime;
    private bool finished;

    // v1.5.0. The 2026-09-14 Player.log had nothing in it at all between Awake
    // and the end of the session, which told us the restore had not run but not
    // one thing about why. Every early return out of Step now says so once, so
    // a silent log can never happen again.
    private string waitingReason;

    private void Awake()
    {
        Log = Logger;
        shared = this;
        cfgEnabled = Config.Bind("Filter memory", "RestoreOnLoad", true,
            "Re-apply the Research / Infrastructure / card-bag filters saved in "
            + "your save file when the game starts. The base game saves most of "
            + "them but fails to read them back.");
        cfgSaveCardTier = Config.Bind("Filter memory", "SaveCardTierFilter", true,
            "Also remember the Tier filter on the Deck and Permanent card tabs. "
            + "The base game never saves that one at all, so this writes it into "
            + "two unused fields the save file has always carried. Turning this "
            + "off leaves the save byte-for-byte as vanilla would write it.");
        cfgFixDuplicates = Config.Bind("Filter memory", "FixDuplicatedCards",
            true,
            "Stop the card bag showing two of everything. With a filter on, "
            + "the base game cannot see the cards the filter is hiding and "
            + "builds a second copy of each one, which you find when you "
            + "clear the filter. This clears the copies out and lets the game "
            + "rebuild one of each.");
        cfgKeepUnopened = Config.Bind("Filter memory", "KeepFiltersYouCannotSee",
            true,
            "Stop a save wiping the filters on a panel you did not open. A "
            + "filter dropdown is empty until the first time you open its tab, "
            + "and the base game saves that emptiness over whatever you had "
            + "set, so one autosave can erase it. This writes your last known "
            + "setting back in.");
        cfgSkipUnchangedEquipRedraw = Config.Bind("Card panel",
            "SkipUnchangedEquipRedraw", true,
            "Stop the equipped cards being torn down and rebuilt on every "
            + "research and infrastructure level up. The base game rebuilds "
            + "them whether or not anything changed, and a card rebuilt "
            + "between your mouse going down and coming up cannot be "
            + "clicked, which is why cards sometimes refuse to come out of "
            + "the Perm Bag. The redraw is skipped only when the panel "
            + "already holds the exact same cards.");
        Harmony harmony = new Harmony("sti.community.filtermemory");
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        Logger.LogInfo("Filter memory active: Research/Infra/card filters "
            + "restored on load, the merge slider lands on the new top level, "
            + "and the equipped cards are not rebuilt when nothing changed");
    }

    private void Update()
    {
        if (finished || !cfgEnabled.Value)
        {
            return;
        }
        try
        {
            Step();
        }
        catch (Exception e)
        {
            Log.LogWarning("Filter memory gave up: " + e.Message);
            finished = true;
        }
    }

    private void Step()
    {
        SaveLoadManager saveLoad = SaveLoadManager.shared;
        if (saveLoad == null || saveLoad.currentSave == null
            || saveLoad.currentSave.filterData == null)
        {
            Waiting("the save has not finished loading");
            return;
        }
        MainPanel mainPanel = MainPanel.shared;
        if (mainPanel == null)
        {
            Waiting("the main panel does not exist yet");
            return;
        }
        TechnoPanel techno = mainPanel.technoPanel;
        if (techno == null)
        {
            Waiting("the Techno panel does not exist yet");
            return;
        }
        CardPanel cards = mainPanel.cardPanel;
        if (cards == null)
        {
            Waiting("the card panel does not exist yet");
            return;
        }
        object research = (ResearchFilterField == null)
            ? null : ResearchFilterField.GetValue(techno);
        object infra = (InfraFilterField == null)
            ? null : InfraFilterField.GetValue(techno);
        object deck = (DeckFilterField == null)
            ? null : DeckFilterField.GetValue(cards);
        object perm = (PermFilterField == null)
            ? null : PermFilterField.GetValue(cards);
        // Only the Techno panel's two have to exist to get started. The card
        // panel's two are allowed to be missing: Settle treats a null filter
        // as 'not ready' and the CardFilter.Start postfix below picks them up
        // whenever the player first opens that tab.
        if (research == null || infra == null)
        {
            Waiting("the Research and Infrastructure filters are not built yet");
            return;
        }

        if (!captured)
        {
            FilterData data = saveLoad.currentSave.filterData;

            // SaveLoadManager.currentSave is a public field on a MonoBehaviour
            // and Save is [Serializable], so Unity default-constructs an empty
            // one before anything is loaded, and LoadSave only assigns the real
            // save on its very last line - after OfflineManager.SkipTime, which
            // is spread over many frames. So for the whole of an offline catch-
            // up this reads an empty FilterData whose every field is null,
            // latches captured on it, finds nothing to restore and reports
            // success. That is exactly Fuzzied's "Not Max Level keeps getting
            // reset": the real save did hold it, we just never looked at it.
            //
            // v1.5.0: this is now only a fallback. The LoadSave postfix
            // captures off the Save it was handed, which is unambiguous. If
            // that patch ever stops binding, this still works for a save that
            // has something in it - and a save that has nothing in it is now
            // reported rather than waited on in silence, because a genuinely
            // all-null FilterData is a real and common state (see the header)
            // and waiting for it to change is waiting forever.
            if (IsBlank(data))
            {
                Waiting("the loaded save has no filters recorded in it");
                return;
            }

            Capture(data, "the save");
        }

        if (Time.unscaledTime < nextAttemptTime)
        {
            return;
        }
        nextAttemptTime = Time.unscaledTime + RETRY_SECONDS;
        attempts++;

        bool settled = true;
        settled &= Settle(ref doneResearchCompletion, research,
            ResearchCompletionField, wantResearchCompletion);
        settled &= Settle(ref doneResearchBonus, research,
            ResearchBonusField, wantResearchBonus);
        settled &= Settle(ref doneInfraCompletion, infra,
            InfraCompletionField, wantInfraCompletion);
        settled &= Settle(ref doneInfraCost, infra,
            InfraCostField, wantInfraCost);
        settled &= Settle(ref doneInfraBonus, infra,
            InfraBonusField, wantInfraBonus);
        settled &= Settle(ref doneDeckEffect, deck,
            CardEffectField, wantDeckEffect);
        settled &= Settle(ref doneDeckRarity, deck,
            CardRarityField, wantDeckRarity);
        settled &= Settle(ref doneDeckTier, deck,
            CardTierField, wantDeckTier);
        settled &= Settle(ref donePermEffect, perm,
            CardEffectField, wantPermEffect);
        settled &= Settle(ref donePermRarity, perm,
            CardRarityField, wantPermRarity);
        settled &= Settle(ref donePermTier, perm,
            CardTierField, wantPermTier);

        if (settled)
        {
            finished = true;
            Log.LogInfo("Filter memory: restored after " + attempts
                + " pass(es)");
            return;
        }
        if (attempts >= MAX_ATTEMPTS)
        {
            // Not a failure, and it no longer says so. Whatever is left is a
            // dropdown whose panel has never been opened, and CardFilter.Start
            // will hand it to us the moment it is.
            finished = true;
            Log.LogInfo("Filter memory: restored " + DoneCount()
                + " of 11 after " + attempts + " pass(es); the rest are "
                + "dropdowns the game has not built yet, and they are set "
                + "when their panel first opens");
        }
    }

    private int DoneCount()
    {
        int n = 0;
        if (doneResearchCompletion) { n++; }
        if (doneResearchBonus) { n++; }
        if (doneInfraCompletion) { n++; }
        if (doneInfraCost) { n++; }
        if (doneInfraBonus) { n++; }
        if (doneDeckEffect) { n++; }
        if (doneDeckRarity) { n++; }
        if (doneDeckTier) { n++; }
        if (donePermEffect) { n++; }
        if (donePermRarity) { n++; }
        if (donePermTier) { n++; }
        return n;
    }

    // One dropdown, once. A filter object that is not there yet is not a
    // failure, it is a 'come back later', which is the distinction the old
    // single settled flag could not make.
    private static bool Settle(ref bool done, object filter, FieldInfo field,
        string wanted)
    {
        if (done)
        {
            return true;
        }
        if (filter == null)
        {
            return false;
        }
        done = Apply(filter, field, wanted);
        return done;
    }

    // Called from the CardFilter.Start postfix at the bottom of this file,
    // which is the first instant at which these dropdowns have any options in
    // them. Runs whether or not the polling loop has finished, because the
    // whole point is that it usually has.
    internal void ApplyToCardFilter(CardFilter filter)
    {
        if (!captured || filter == null || cfgEnabled == null
            || !cfgEnabled.Value)
        {
            return;
        }
        MainPanel mainPanel = MainPanel.shared;
        if (mainPanel == null || mainPanel.cardPanel == null)
        {
            return;
        }
        CardPanel cards = mainPanel.cardPanel;
        object deck = (DeckFilterField == null)
            ? null : DeckFilterField.GetValue(cards);
        object perm = (PermFilterField == null)
            ? null : PermFilterField.GetValue(cards);

        // Identity rather than the private isBattleCard flag. CardFilter is a
        // general component and the game is free to put another one somewhere
        // we know nothing about; the only two we have remembered values for
        // are the two hanging off CardPanel.
        bool isDeck = ReferenceEquals(filter, deck);
        bool isPerm = ReferenceEquals(filter, perm);
        if (!isDeck && !isPerm)
        {
            return;
        }
        if (isDeck)
        {
            if (doneDeckEffect && doneDeckRarity && doneDeckTier)
            {
                return;
            }
            Settle(ref doneDeckEffect, filter, CardEffectField, wantDeckEffect);
            Settle(ref doneDeckRarity, filter, CardRarityField, wantDeckRarity);
            Settle(ref doneDeckTier, filter, CardTierField, wantDeckTier);
        }
        else
        {
            if (donePermEffect && donePermRarity && donePermTier)
            {
                return;
            }
            Settle(ref donePermEffect, filter, CardEffectField, wantPermEffect);
            Settle(ref donePermRarity, filter, CardRarityField, wantPermRarity);
            Settle(ref donePermTier, filter, CardTierField, wantPermTier);
        }
        Log.LogInfo("Filter memory: the " + (isDeck ? "deck" : "perm")
            + " card filters were set as that panel was built ("
            + DoneCount() + " of 11 restored)");
    }

    // Every field null or empty means nobody has written to this FilterData,
    // which for a save that has actually been loaded cannot happen: the game
    // writes "All"/"none" style defaults into it. So this is Unity's
    // placeholder and not worth latching onto.
    private static bool IsBlank(FilterData data)
    {
        return string.IsNullOrEmpty(data.researchCompletionFilterValue)
            && string.IsNullOrEmpty(data.researchBonusFilterValue)
            && string.IsNullOrEmpty(data.infraCompletionFilterValue)
            && string.IsNullOrEmpty(data.infraCostFilterValue)
            && string.IsNullOrEmpty(data.infraBonusFilterValue)
            && string.IsNullOrEmpty(data.deckCardEffectFilterValue)
            && string.IsNullOrEmpty(data.deckCardRarityFilterValue)
            && string.IsNullOrEmpty(data.deckCardHiLevelFilterValue)
            && string.IsNullOrEmpty(data.permCardEffectFilterValue)
            && string.IsNullOrEmpty(data.permCardRarityFilterValue)
            && string.IsNullOrEmpty(data.permCardHiLevelFilterValue);
    }

    // Called the moment the real save has been assigned, with the Save that
    // was actually loaded. Starts the whole restore over, so whatever was read
    // before that point is thrown away, and captures the wanted values
    // straight off the save rather than watching currentSave from Update and
    // guessing when it has stopped being Unity's placeholder.
    internal void Rearm(Save save)
    {
        captured = false;
        finished = false;
        waitingReason = null;
        attempts = 0;
        nextAttemptTime = 0f;
        doneResearchCompletion = false;
        doneResearchBonus = false;
        doneInfraCompletion = false;
        doneInfraCost = false;
        doneInfraBonus = false;
        doneDeckEffect = false;
        doneDeckRarity = false;
        doneDeckTier = false;
        donePermEffect = false;
        donePermRarity = false;
        donePermTier = false;

        if (save != null && save.filterData != null && !IsBlank(save.filterData))
        {
            Capture(save.filterData, "the save that just loaded");
        }
        else
        {
            Log.LogInfo("Filter memory: the save that just loaded has no "
                + "filters recorded in it. Nothing to put back this time; set "
                + "them once and they will stick from now on.");
        }
    }

    // One place where the eleven wanted values are read out of a FilterData,
    // so the LoadSave postfix and the Update fallback cannot disagree.
    private void Capture(FilterData data, string where)
    {
        wantResearchCompletion = data.researchCompletionFilterValue;
        wantResearchBonus = data.researchBonusFilterValue;
        wantInfraCompletion = data.infraCompletionFilterValue;
        wantInfraCost = data.infraCostFilterValue;
        wantInfraBonus = data.infraBonusFilterValue;
        wantDeckEffect = data.deckCardEffectFilterValue;
        wantDeckRarity = data.deckCardRarityFilterValue;
        wantDeckTier = data.deckCardHiLevelFilterValue;
        wantPermEffect = data.permCardEffectFilterValue;
        wantPermRarity = data.permCardRarityFilterValue;
        wantPermTier = data.permCardHiLevelFilterValue;
        captured = true;

        // Everything the save carried becomes the floor that a panel which
        // never opens can no longer write over. See FilterFieldMemory.
        FilterFieldMemory.Seed(data);

        Log.LogInfo("Filter memory: " + where + " asks for research='"
            + Show(wantResearchCompletion) + "' infra='"
            + Show(wantInfraCompletion) + "' deck='"
            + Show(wantDeckEffect) + "'/'" + Show(wantDeckTier) + "' perm='"
            + Show(wantPermEffect) + "'/'" + Show(wantPermTier) + "'");
    }

    // Applies the remembered Research or Infra values to a filter whose Start
    // has just run, which is the first instant its dropdowns have any options
    // in them. Same shape as ApplyToCardFilter above, and for the same reason:
    // the Techno panel's GameObject is inactive until the player opens that
    // tab, so polling for it in the first five seconds can never reach it.
    internal void ApplyToResearchFilter(ResearchFilter filter)
    {
        if (!captured || filter == null || cfgEnabled == null
            || !cfgEnabled.Value)
        {
            return;
        }
        if (doneResearchCompletion && doneResearchBonus)
        {
            return;
        }
        Settle(ref doneResearchCompletion, filter, ResearchCompletionField,
            wantResearchCompletion);
        Settle(ref doneResearchBonus, filter, ResearchBonusField,
            wantResearchBonus);
        Log.LogInfo("Filter memory: the Research filters were set as that "
            + "panel was built (" + DoneCount() + " of 11 restored)");
    }

    internal void ApplyToInfraFilter(InfraFilter filter)
    {
        if (!captured || filter == null || cfgEnabled == null
            || !cfgEnabled.Value)
        {
            return;
        }
        if (doneInfraCompletion && doneInfraCost && doneInfraBonus)
        {
            return;
        }
        Settle(ref doneInfraCompletion, filter, InfraCompletionField,
            wantInfraCompletion);
        Settle(ref doneInfraCost, filter, InfraCostField, wantInfraCost);
        Settle(ref doneInfraBonus, filter, InfraBonusField, wantInfraBonus);
        Log.LogInfo("Filter memory: the Infrastructure filters were set as "
            + "that panel was built (" + DoneCount() + " of 11 restored)");
    }

    // Returns true when this dropdown needs no further attention - either it
    // already reads what the save asked for, or there is nothing sensible to
    // do about it.
    private static bool Apply(object filter, FieldInfo field, string wanted)
    {
        if (field == null || string.IsNullOrEmpty(wanted))
        {
            return true;
        }
        SearchDropdown dropdown = field.GetValue(filter) as SearchDropdown;
        if (dropdown == null)
        {
            return true;
        }
        if (dropdown.stringValue == wanted)
        {
            return true; // the game got it right, or we already fixed it
        }
        if (dropdown.stringValue == null)
        {
            return false; // options not built yet - come back next pass
        }
        // Set the model without letting SetOptionValue's own OnValueChange()
        // re-derive the value from the stale widget index, then fire the event
        // ourselves so the Techno panel re-runs the filter.
        dropdown.SetOptionValue(wanted, false);
        if (dropdown.stringValue != wanted)
        {
            // The dropdown does not know this id at all. Most likely the save
            // was written in another language. Leave the player's UI alone.
            return true;
        }
        dropdown.onValueChanged.Invoke(dropdown.value);
        return true;
    }

    private static string Show(string value)
    {
        return (value == null) ? "(unset)" : value;
    }

    // Says why the restore is still sitting on its hands, once per distinct
    // reason. Once per reason rather than once overall, because the reasons
    // come in sequence as the game builds itself and the last one is the
    // interesting one.
    private void Waiting(string reason)
    {
        if (waitingReason == reason)
        {
            return;
        }
        waitingReason = reason;
        Log.LogInfo("Filter memory: waiting, " + reason);
    }
}


// The last value each filter field was actually seen holding.
//
// The base game builds a brand new FilterData for every save and asks each
// panel to fill it in. A panel whose GameObject has never been switched on has
// no options in its dropdowns, so it fills its fields in with null, and the
// save comes out with your filters erased. There is no older copy underneath
// to fall back on, which is why this has to keep one.
//
// Seeded from the save on load and topped up from every value a panel that IS
// open writes, so a field only ever gets refilled with something the player
// genuinely chose. A player who sets a filter back to All writes "All", which
// is a real value and is remembered as one, so nothing sticks that should not.
internal static class FilterFieldMemory
{
    private static readonly FieldInfo[] Fields =
        typeof(FilterData).GetFields(BindingFlags.Instance | BindingFlags.Public);
    private static readonly Dictionary<string, string> lastGood =
        new Dictionary<string, string>();

    internal static void Seed(FilterData data)
    {
        if (data == null)
        {
            return;
        }
        for (int i = 0; i < Fields.Length; i++)
        {
            if (Fields[i].FieldType != typeof(string))
            {
                continue;
            }
            string v = Fields[i].GetValue(data) as string;
            if (!string.IsNullOrEmpty(v))
            {
                lastGood[Fields[i].Name] = v;
            }
        }
    }

    // Remember what is there, put back what is not. Returns how many fields
    // had to be put back, purely so the log can say.
    internal static int Refill(FilterData data)
    {
        if (data == null)
        {
            return 0;
        }
        int repaired = 0;
        for (int i = 0; i < Fields.Length; i++)
        {
            if (Fields[i].FieldType != typeof(string))
            {
                continue;
            }
            string v = Fields[i].GetValue(data) as string;
            if (!string.IsNullOrEmpty(v))
            {
                lastGood[Fields[i].Name] = v;
                continue;
            }
            string kept;
            if (lastGood.TryGetValue(Fields[i].Name, out kept)
                && !string.IsNullOrEmpty(kept))
            {
                Fields[i].SetValue(data, kept);
                repaired++;
            }
        }
        return repaired;
    }
}


// The root-cause fix. TechnoPanel.ExportFilterData is the last thing to touch
// the FilterData on its way into the save:
//
//     FilterData filterData = new FilterData();
//     MainPanel.shared.cardPanel.ExportFilterData(filterData);
//     MainPanel.shared.technoPanel.ExportFilterData(filterData);
//     obj.filterData = filterData;
//
// so running after it is running after every panel has had its say. Any field
// still null or empty at that point belongs to a panel that was never opened
// this session, and the value it would write is not a choice the player made,
// it is an absence. Put back what the save came in with instead.
[HarmonyPatch(typeof(TechnoPanel), "ExportFilterData")]
public static class TechnoPanelExportFilterDataPatch
{
    private static bool saidSo;

    private static void Postfix(FilterData filterData)
    {
        if (filterData == null || FilterMemoryPlugin.cfgKeepUnopened == null
            || !FilterMemoryPlugin.cfgKeepUnopened.Value)
        {
            return;
        }
        try
        {
            int repaired = FilterFieldMemory.Refill(filterData);
            if (repaired > 0 && !saidSo)
            {
                saidSo = true;
                FilterMemoryPlugin.Log.LogInfo("Filter memory: put " + repaired
                    + " filter setting(s) back into the save. The base game "
                    + "writes a blank over any filter whose panel you have not "
                    + "opened this session, which is what kept erasing them.");
            }
        }
        catch (Exception e)
        {
            FilterMemoryPlugin.Log.LogWarning(
                "Filter memory could not keep the filters in the save: "
                + e.Message);
        }
    }
}


// The moment the Research or Infrastructure filter becomes real.
//
// Identical in kind to the CardFilter.Start patch at the bottom of this file,
// and needed for the identical reason: ResearchFilter.Start and
// InfraFilter.Start are where the dropdowns get their options and where the
// game calls LoadFilterData, and the Techno panel's GameObject is inactive
// until the player opens that tab. Polling in the first five seconds after a
// load cannot reach a Start that has not happened yet.
[HarmonyPatch(typeof(ResearchFilter), "Start")]
public static class ResearchFilterStartPatch
{
    private static void Postfix(ResearchFilter __instance)
    {
        if (FilterMemoryPlugin.shared == null)
        {
            return;
        }
        try
        {
            FilterMemoryPlugin.shared.ApplyToResearchFilter(__instance);
        }
        catch (Exception e)
        {
            FilterMemoryPlugin.Log.LogWarning(
                "Filter memory could not set the Research filters as the panel "
                + "was built: " + e.Message);
        }
    }
}

[HarmonyPatch(typeof(InfraFilter), "Start")]
public static class InfraFilterStartPatch
{
    private static void Postfix(InfraFilter __instance)
    {
        if (FilterMemoryPlugin.shared == null)
        {
            return;
        }
        try
        {
            FilterMemoryPlugin.shared.ApplyToInfraFilter(__instance);
        }
        catch (Exception e)
        {
            FilterMemoryPlugin.Log.LogWarning(
                "Filter memory could not set the Infrastructure filters as the "
                + "panel was built: " + e.Message);
        }
    }
}

// The Tier dropdown on the Deck and Permanent card tabs is the one filter the
// base game never saves. CardPanel.ExportFilterData fills in Effect and Rarity
// for both tabs and stops there, leaving FilterData's deckCardHiLevelFilterValue
// and permCardHiLevelFilterValue permanently null - fields that nothing else in
// the game touches. We run straight after it and fill them in, and the restore
// pass above reads them back on the next launch.
[HarmonyPatch(typeof(CardPanel), "ExportFilterData")]
public static class CardPanelExportFilterDataPatch
{
    private static readonly FieldInfo DeckFilterField =
        typeof(CardPanel).GetField("deckFilter",
            BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo PermFilterField =
        typeof(CardPanel).GetField("permFilter",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private static void Postfix(CardPanel __instance, FilterData filterData)
    {
        if (filterData == null || FilterMemoryPlugin.cfgSaveCardTier == null
            || !FilterMemoryPlugin.cfgSaveCardTier.Value)
        {
            return;
        }
        try
        {
            filterData.deckCardHiLevelFilterValue =
                TierOf(DeckFilterField, __instance);
            filterData.permCardHiLevelFilterValue =
                TierOf(PermFilterField, __instance);
        }
        catch (Exception e)
        {
            FilterMemoryPlugin.Log.LogWarning(
                "Filter memory could not save the card Tier filter: " + e.Message);
        }
    }

    private static string TierOf(FieldInfo filterField, CardPanel panel)
    {
        if (filterField == null || FilterMemoryPlugin.CardTierField == null)
        {
            return null;
        }
        object filter = filterField.GetValue(panel);
        if (filter == null)
        {
            return null;
        }
        SearchDropdown dropdown =
            FilterMemoryPlugin.CardTierField.GetValue(filter) as SearchDropdown;
        return (dropdown == null) ? null : dropdown.stringValue;
    }
}


// The repair for the doubled card bag described in the header. It runs before
// the game's own diff and then leaves the diff to do the actual work:
//
//   1. reactivate every card set under the bag, so the game's two queries see
//      the whole bag instead of the visible part of it. Both callers,
//      LoadDeckBag and LoadPermBag, re-run their filter on the very next line,
//      so nothing is on screen for even one frame that should not be - and
//      that same line is what un-hides a card set that came out of the pool
//      carrying activeSelf == false;
//
//   2. pool EVERY copy of any card id that appears more than once. Not all but
//      one - every one. A duplicate is not stale by the game's own test, since
//      its name still matches a card in the bag, so vanilla will never clear
//      it; and choosing a survivor ourselves would mean guessing which copy
//      IdenticalCardSet.view is pointing at. Removing the lot makes that id
//      missing, and the game's own add loop rebuilds exactly one, wired up by
//      the code whose job that is.
//
// Cheap when there is nothing wrong: one GetComponentsInChildren over children
// that already exist, and a dictionary count over them.
[HarmonyPatch(typeof(CardPanel), "DisplayBagCardSets")]
public static class CardPanelDisplayBagCardSetsPatch
{
    private static void Prefix(GameObject parent, ObjectPool ___cardItemSetPool)
    {
        if (parent == null || ___cardItemSetPool == null
            || FilterMemoryPlugin.cfgFixDuplicates == null
            || !FilterMemoryPlugin.cfgFixDuplicates.Value)
        {
            return;
        }
        try
        {
            CardItemSet[] sets =
                parent.GetComponentsInChildren<CardItemSet>(true);
            if (sets == null || sets.Length == 0)
            {
                return;
            }

            Dictionary<string, int> seen = new Dictionary<string, int>();
            for (int i = 0; i < sets.Length; i++)
            {
                if (sets[i] == null)
                {
                    continue;
                }
                // A card set the filter hid is invisible to the game's own
                // diff, and being invisible to the diff is precisely what
                // gets it built a second time.
                if (!sets[i].gameObject.activeSelf)
                {
                    sets[i].gameObject.SetActive(true);
                }
                string id = sets[i].name;
                if (id == null)
                {
                    continue;
                }
                int n;
                seen.TryGetValue(id, out n);
                seen[id] = n + 1;
            }

            int pooled = 0;
            for (int i = 0; i < sets.Length; i++)
            {
                if (sets[i] == null || sets[i].name == null)
                {
                    continue;
                }
                int n;
                if (!seen.TryGetValue(sets[i].name, out n) || n < 2)
                {
                    continue;
                }
                ___cardItemSetPool.SaveObjectToPool(sets[i]);
                pooled++;
            }
            if (pooled > 0)
            {
                FilterMemoryPlugin.Log.LogInfo("Filter memory: took "
                    + pooled + " duplicated card row(s) out of the bag - the "
                    + "game builds a second copy of every card a filter is "
                    + "hiding. It rebuilds one of each on this same pass.");
            }
        }
        catch (Exception e)
        {
            FilterMemoryPlugin.Log.LogWarning(
                "Filter memory could not tidy the card bag: " + e.Message);
        }
    }
}


// SaveLoadManager.LoadSave assigns currentSave on its LAST line, after
// OfflineManager.SkipTime has been kicked off - and SkipTime runs across many
// frames, so every plugin Update in between sees the placeholder described in
// Step. Re-arming here means the restore always reads the save the player
// actually loaded, whatever it read before.
[HarmonyPatch(typeof(SaveLoadManager), "LoadSave")]
public static class SaveLoadManagerLoadSavePatch
{
    private static void Postfix(Save save)
    {
        if (FilterMemoryPlugin.shared == null)
        {
            return;
        }
        try
        {
            FilterMemoryPlugin.shared.Rearm(save);
        }
        catch (Exception e)
        {
            FilterMemoryPlugin.Log.LogWarning(
                "Filter memory could not re-read the loaded save: " + e.Message);
        }
    }
}


// The moment the Deck or Permanent card filter becomes real.
//
// CardFilter.Start is where the game fills the three dropdowns with options
// and then calls LoadDeckFilterData or LoadPermFilterData to put its own two
// values back. Until it has run, the dropdowns have no options and
// SearchDropdown.stringValue is null, which is why the polling loop in this
// plugin could never finish for them: the card panel's GameObject is inactive
// until the player opens that tab, so Start can be minutes away or never come.
//
// A postfix is the right hook for three reasons. It fires exactly when there is
// something to write to, it fires after the game has written its own two so
// ours is the value that survives, and it costs nothing at all on the runs
// where the player never opens the card panel.
[HarmonyPatch(typeof(CardFilter), "Start")]
public static class CardFilterStartPatch
{
    private static void Postfix(CardFilter __instance)
    {
        if (FilterMemoryPlugin.shared == null)
        {
            return;
        }
        try
        {
            FilterMemoryPlugin.shared.ApplyToCardFilter(__instance);
        }
        catch (Exception e)
        {
            FilterMemoryPlugin.Log.LogWarning(
                "Filter memory could not set the card filters as the panel was "
                + "built: " + e.Message);
        }
    }
}


// ===================================================================
// v1.6.0: the level slider lands on the new top level after a merge.
// See the header.
// ===================================================================

internal static class MergeShowsTopLevel
{
    // PlayerPrefs, the same store the game's own settings use.
    public const string PREF_KEY = "communityMergeShowTopLevel";

    public static bool Enabled
    {
        get { return PlayerPrefs.GetInt(PREF_KEY, 1) == 1; }
        set { PlayerPrefs.SetInt(PREF_KEY, value ? 1 : 0); }
    }

    // The slider is a private serialized field on CardItemSet, so reflection
    // is the only way to it. Null means this game version moved it, and the
    // whole feature then quietly does nothing.
    private static readonly FieldInfo LevelSliderField =
        AccessTools.Field(typeof(CardItemSet), "levelSlider");

    internal static void Apply(CardItemSet view)
    {
        if (!Enabled || view == null || LevelSliderField == null)
        {
            return;
        }
        try
        {
            Slider slider = LevelSliderField.GetValue(view) as Slider;
            if (slider == null)
            {
                return;
            }

            // v1.6.1. The slider's value is an INDEX into CardItemSet's
            // edittingLevels list, not a card level, and that list plus
            // maxValue are only ever rebuilt by UpdateSliderSettings, which
            // vanilla calls from SetModel and nowhere else. A merge calls
            // only CheckShowingLevel. So at this point the slider still
            // describes the levels that existed BEFORE the merge: the new
            // top level is not in the list at all, and the slider is already
            // parked on the old max. The old code read that stale max, found
            // value == maxValue, decided it was already at the top and just
            // redrew the level you were on. Fuzzied merged The Bomb to 4 and
            // the card kept saying 3 until he dragged the slider by hand.
            // Rebuild first, then move.
            view.UpdateSliderSettings();

            if (slider.maxValue < 0f)
            {
                // No levels left at all, which happens if the merge consumed
                // the last copies. Nothing to show.
                return;
            }

            slider.value = slider.maxValue;

            // Setting the value only fires the prefab's listener when the
            // number actually changes, and after a rebuild the new top level
            // can land on the same index the old one had. The index means
            // something different now, so redraw unconditionally rather than
            // hope the event fired. Calling this twice is harmless: it reads
            // the slider and shows that level.
            view.SliderValueChanged();
        }
        catch (Exception e)
        {
            FilterMemoryPlugin.Log.LogWarning("Could not move the merge "
                + "slider to the top level: " + e.Message);
        }
    }
}

[HarmonyPatch(typeof(IdenticalCardSet), "MergeOneOfLevel")]
public static class MergeOneOfLevelTopPatch
{
    private static void Postfix(IdenticalCardSet __instance)
    {
        MergeShowsTopLevel.Apply(__instance.view);
    }
}

[HarmonyPatch(typeof(IdenticalCardSet), "MergeAllOfLevel")]
public static class MergeAllOfLevelTopPatch
{
    private static void Postfix(IdenticalCardSet __instance)
    {
        MergeShowsTopLevel.Apply(__instance.view);
    }
}

// Also the whole bag Merge All button, which is a loop over this.
[HarmonyPatch(typeof(IdenticalCardSet), "MergeAll")]
public static class MergeAllTopPatch
{
    private static void Postfix(IdenticalCardSet __instance)
    {
        MergeShowsTopLevel.Apply(__instance.view);
    }
}

// The toggle row. Same pattern as DevConsoleOff, which carries the long
// version of why a row is built by cloning a vanilla toggle.
[HarmonyPatch(typeof(SettingsManager), "LoadSystemSettings")]
public static class AddMergeTopLevelTogglePatch
{
    private const string CLONE_NAME = "CommunityMergeTopLevelRow";

    private static readonly FieldInfo HideCardUiToggleField =
        AccessTools.Field(typeof(SettingsManager), "hideCardControlUIToggle");

    private static void Postfix(SettingsManager __instance)
    {
        try
        {
            if (HideCardUiToggleField == null) { return; }
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

            GameObject clone = UnityEngine.Object.Instantiate(
                template.gameObject, parent);
            clone.name = CLONE_NAME;

            if (!CommunitySettings.Adopt(__instance, template, clone,
                    FilterMemoryPlugin.Log))
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
                        - new Vector2(0f, src.rect.height + 6f);
                }
            }

            Toggle toggle = clone.GetComponent<Toggle>();
            if (toggle == null)
            {
                UnityEngine.Object.Destroy(clone);
                return;
            }
            // Listeners off BEFORE isOn, or the template's own callback fires
            // and changes a vanilla setting behind the player's back.
            toggle.onValueChanged = new Toggle.ToggleEvent();
            toggle.isOn = MergeShowsTopLevel.Enabled;
            toggle.onValueChanged.AddListener(OnToggleChanged);

            LangText label = clone.GetComponentInChildren<LangText>(true);
            if (label != null)
            {
                label.SetLocalisedText("Merging shows the new top level");
            }

            TooltipComposite tip = clone.GetComponent<TooltipComposite>();
            if (tip == null) { tip = clone.AddComponent<TooltipComposite>(); }
            tip.defaultTooltip = "Merge four copies into one of the next "
                + "level and the card shows you what you just made. Without "
                + "this the slider stays on the level you were looking at "
                + "whenever you still own some of them, so the new card is "
                + "off to the right and you have to go and find it. Works "
                + "for the single merge buttons and for Merge All.";

            FilterMemoryPlugin.Log.LogInfo(
                "Settings: added 'Merging shows the new top level' toggle (on="
                + toggle.isOn + ")");
        }
        catch (Exception e)
        {
            FilterMemoryPlugin.Log.LogWarning(
                "Could not add the merge top level toggle: " + e.Message);
        }
    }

    private static void OnToggleChanged(bool isOn)
    {
        MergeShowsTopLevel.Enabled = isOn;
        FilterMemoryPlugin.Log.LogInfo("Merge shows top level set to " + isOn);
    }
}

// ===================================================================
// v1.6.0: do not rebuild the equipped cards when nothing changed.
// See the header.
// ===================================================================

internal static class EquipRedraw
{
    // Enough lines to show the shape of this in a log without a long session
    // writing one every frame.
    private const int MAX_SAID = 8;
    private static int said;
    private static int skipped;

    // Whether the panel already holds exactly the cards it is about to be
    // given. The comparison is by reference on purpose: see the header.
    internal static bool AlreadyDrawn(GameObject parent, IList cards, int total)
    {
        if (parent == null || cards == null)
        {
            return false;
        }

        // DisplayEquipedCards draws one item per card and then pads out to
        // the slot count with empty slots, so this is what the panel holds
        // when it is up to date.
        int expected = cards.Count > total ? cards.Count : total;
        if (parent.transform.childCount != expected)
        {
            return false;
        }

        CardItemInBag[] drawn =
            parent.GetComponentsInChildren<CardItemInBag>(true);
        if (drawn == null || drawn.Length != cards.Count)
        {
            return false;
        }

        for (int i = 0; i < cards.Count; i++)
        {
            object card = cards[i];
            if (card == null)
            {
                return false;
            }
            bool found = false;
            for (int j = 0; j < drawn.Length; j++)
            {
                if (drawn[j] != null
                    && ReferenceEquals(drawn[j].card, card))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                return false;
            }
        }
        return true;
    }

    internal static void Skipped()
    {
        skipped++;
        if (said < MAX_SAID && (skipped == 1 || skipped == 100
            || skipped == 1000))
        {
            said++;
            FilterMemoryPlugin.Log.LogInfo("Card panel: left the equipped "
                + "cards alone, nothing about them changed (" + skipped
                + " redraw(s) skipped so far)");
        }
    }
}

[HarmonyPatch(typeof(CardPanel), "LoadPermSet")]
public static class LoadPermSetSkipPatch
{
    private static bool Prefix(GameObject ___currentPermSetInBag)
    {
        try
        {
            if (FilterMemoryPlugin.cfgSkipUnchangedEquipRedraw == null
                || !FilterMemoryPlugin.cfgSkipUnchangedEquipRedraw.Value)
            {
                return true;
            }
            Player player = Player.shared;
            if (player == null || player.cardBag == null
                || player.cardBag.currentPermSet == null)
            {
                return true;
            }
            CardBag bag = player.cardBag;
            if (!EquipRedraw.AlreadyDrawn(___currentPermSetInBag,
                    bag.currentPermSet.cards, bag.permanentCardSetCount))
            {
                return true;
            }
            // The flag has to be cleared even though the redraw did not
            // happen, or FixedUpdate asks again on the very next frame and
            // this runs for ever.
            bag.FinishPermSetDisplayUpdate();
            EquipRedraw.Skipped();
            return false;
        }
        catch (Exception e)
        {
            FilterMemoryPlugin.Log.LogWarning("Card panel: redrawing the "
                + "permanent set because the check threw: " + e.Message);
            return true;
        }
    }
}

[HarmonyPatch(typeof(CardPanel), "LoadDeck")]
public static class LoadDeckSkipPatch
{
    private static bool Prefix(GameObject ___currentDeckInBag)
    {
        try
        {
            if (FilterMemoryPlugin.cfgSkipUnchangedEquipRedraw == null
                || !FilterMemoryPlugin.cfgSkipUnchangedEquipRedraw.Value)
            {
                return true;
            }
            Player player = Player.shared;
            if (player == null || player.cardBag == null
                || player.cardBag.currentDeck == null)
            {
                return true;
            }
            CardBag bag = player.cardBag;
            if (!EquipRedraw.AlreadyDrawn(___currentDeckInBag,
                    bag.currentDeck.cards, bag.deckSize))
            {
                return true;
            }
            bag.FinishDeckDisplayUpdate();
            EquipRedraw.Skipped();
            return false;
        }
        catch (Exception e)
        {
            FilterMemoryPlugin.Log.LogWarning("Card panel: redrawing the "
                + "deck because the check threw: " + e.Message);
            return true;
        }
    }
}
