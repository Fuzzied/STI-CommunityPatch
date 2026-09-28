// Space Travel Idle community mod - more deck / permanent-set loadouts
//
// Vanilla hardcodes 2 saved loadouts for the battle deck and 2 for the
// permanent card set, but the save file already stores room for 100 of
// each (CardBag allocates CardLoadout[100] and serializes it). This
// patch raises the visible count as you reach more planets:
//
//   loadouts = 2 + (planets reached beyond Venus), capped at 10
//   Earth/Moon/Venus: 2 (vanilla) ... Mercury: 3 ... Mars: 5 ...
//   Jupiter: 6 ... Saturn: 7 ... Uranus: 8 ... Neptune: 9 ... Pluto: 10
//
// The loadout dropdowns rebuild through CardPanel.ReloadLoadoutControls,
// which the game already calls on every unlock-requirement update
// (including reaching a new planet), so new slots appear on their own.
// Saves are untouched: slots beyond the visible count keep whatever they
// held, and a Big Bang (which resets farthestStarIdx) simply hides the
// extra slots again without erasing them.
//
// It also fixes a vanilla bug in the same area: a newly unlocked perm slot
// (or deck slot) could not actually be filled until the save was reloaded.
// CardBag.currentPermSet is a PermanentCardSet built with a FIXED cardCount,
// and InsertCard silently refuses once cards.Count reaches it. The live
// permanentCardSetCount grows the moment the research finishes, so the panel
// draws the new empty slot and CardLoadout.EquipCard accepts the card - but
// InsertCard drops it, so it never appears and its bonus never applies.
// ForceReloadCurrentPermSet (which rebuilds the set from the loadout) only
// ran on save load, which is why returning to the menu and loading fixed it.
// We rebuild whenever the captured count no longer matches the live one.
//
// v1.2.0. Fuzzied: "My Loadout list is missing or cannot be scrolled past the
// first 4". The list itself is complete: CardPanel.LoadDeckLoadouts builds
// "current deck" plus one line per loadout and hands the lot to
// SearchDropdown.SetOptionsAndKeepIndexID, and SearchDropdownModel caps
// nothing - with an empty search box it returns every option it was given. So
// the loss is in the popup, which is a stock TMP_Dropdown template authored
// when these lists only ever held three lines.
//
// Two things can cut a popup short and they look the same on screen. It can
// have nothing to scroll with, and it can be clipped by a mask on a panel
// above it: TMP_Dropdown parents the popup next to the dropdown itself and
// gives it its own sorting order, which fixes what draws on top but does
// nothing about clipping, so everything past the edge of the panel is simply
// not drawn - and the wheel over that region belongs to the panel's own
// scroll view rather than to the list.
//
// Nothing here is done on a hunch. Each correction is made only when the
// measurement asks for it: scrolling is switched on only where it is off, a
// popup is grown only when it has no scroll view at all, and it is lifted out
// to the root canvas only when a mask above it genuinely does not contain it.
// A popup that already scrolls inside its own panel is left exactly as the
// game authored it. Every action taken is written to the log with the numbers
// behind it.
//
// v1.3.0. Fuzzied: "Im still missing the Travel one I renamed at the end."
//
// Not a popup problem this time, and not a missing loadout either. His save
// holds permSetLoadoutNames[7] = "Travel" with five cards in
// permSetLoadouts[7], all of them owned at the exact levels the loadout asks
// for. The dropdown was simply not offering that slot: the log reads
//
//     Popup list on 'LoadOutDropdown': 7 line(s)
//
// and the panel builds one line for the live set plus one per loadout, so the
// count was 6 and slot 8 was past the end of it.
//
// That count is the rule at the top of this file, 2 + planets beyond Venus,
// and farthestStarIdx is reset to 0 by a Big Bang. The note above says so
// plainly and calls it harmless, because nothing is erased. It is not
// harmless. A loadout you built and named is a thing you own, and taking it
// out of the dropdown until you have flown back out to Jupiter is losing it
// for as long as that takes.
//
// So the planet rule now only ever ADDS slots. A slot that has cards saved in
// it, or that you have given a name of your own, is always offered whatever
// the rule says. Both tests are deliberately strict about what counts as
// used, because merely looking at a slot is not using one: previewing an
// empty slot makes the game write a CardLoadout with no cards into it
// (GetNthPermSetLoadoutCards) and fill in a default name like "Perm Set
// Preset 8" (LoadPermSetLoadouts), and neither of those should pin a slot
// open. Cards in it, or a name that is not the one the game wrote, do.
//
// v1.4.0. Fuzzied: "the loadout drop down ... closes every time a research or
// infra completes I think". He is right, and the chain is entirely vanilla:
//
//   Research.CheckLevelUp (Research.cs:259) / Infra level-up (Infra.cs:311)
//     -> UnlockManager.shared.needsUnlockReqUpdate = true
//   UnlockManager.Update, next frame
//     -> UnlockRequirementUpdate
//        -> MainPanel.shared.cardPanel.ReloadLoadoutControls()
//           -> LoadDeckLoadouts / LoadPermSetLoadouts
//              -> SearchDropdown.SetOptionsAndKeepIndexID(list)
//
// SetOptionsAndKeepIndexID looks safe when nothing changed, and half of it is:
// SearchDropdownModel.SetOptions returns early unless the option list really
// differs. But it then calls SetOptionValue(value) unconditionally, and that
// is three separate UpdateUI passes -
//
//   SetSelectedOption(index)  -> UpdateUI
//   SetSearchText("")         -> UpdateUI
//   OnValueChange()           -> SetIsEditing(false) -> UpdateUI
//
// - and UpdateUI opens with dropdown.Hide(), re-showing only while the model
// still says it is editing, which SetIsEditing(false) has just turned off. So
// the popup shuts. ReloadLoadoutControls then calls
// DisplayDeckLoadoutsNameSLControls(on: false) as well, which is why the
// rename box and the Save / Load buttons vanish on the same beat.
//
// On a busy planet research levels several times a second, so the list cannot
// stay open long enough to click a line in it.
//
// The fix is to do nothing when there is nothing to redo. What those controls
// draw is a function of exactly two things: how many loadout slots are offered
// and what they are called. Both are cheap to read, so they are read and
// compared against the last rebuild, and an identical answer skips the whole
// method. The first call always runs (no previous answer to match), and
// anything that genuinely moves a name or a count - reaching a planet, saving
// into a fresh slot, renaming one - differs immediately and rebuilds on the
// very next pass.
//
// Deliberately NOT fixed in SearchDropdown.UpdateUI, which is the other
// obvious place. That method is shared by every dropdown in the game,
// including the forge and alchemy pickers and the language selector, and
// teaching it to keep an expanded popup open would change all of them to fix
// one. This stays inside the panel that has the problem.
//
// v1.5.0: a loadout keeps its cards when you level them up.
//
// A loadout slot does not store "Storm Core". It stores "storm_core-0", an id
// and an exact level, and CardBag.VerifyCardLoadout (CardBag.cs:336) throws
// away any entry the player owns none of:
//
//   int battleCardCount = GetBattleCardCount(id, level);
//   int permCardCount   = GetPermCardCount(id, level);
//   if (battleCardCount != 0) { ... }
//   else if (permCardCount != 0) { ... }
//   // no branch keeps a pair that matches neither, so it is dropped
//
// Merging three level 0 copies into a level 1 is the ordinary way cards go up,
// and it leaves every saved loadout pointing at a level that no longer exists.
// The card vanishes out of the loadout with no message. It is worst on the
// cards you level most, which are the ones worth putting in a loadout.
//
// The Prefix rewrites a stale entry to the highest level of the same card the
// player actually owns, then lets the original method run and do its own
// counting. Highest rather than nearest: for a permanent card a higher level
// is a bigger bonus, and picking the strongest copy is what someone loading
// their travel set wants. An entry whose card is gone entirely, or disabled,
// is left alone and the original drops it as before.
//
// Permanent sets refuse two cards with the same id (CardLoadout.EquipCard,
// allowSameID: false), so a rewrite that would collide with an entry already
// in the list is skipped rather than made. Battle decks do allow duplicates,
// so there the rewrite goes ahead and the original caps the count at what is
// owned.
//
// v1.6.0. Fuzzied: "the last card slot is empty and cannot be filled."
//
// Two lists back the permanent set and nothing in Vanilla keeps them level:
// CardBag.currentPermSetLoadout.idLevelStrings is what the game equips from,
// CardBag.currentPermSet.cards is what the panel draws. CardPanel draws
// permanentCardSetCount slots and fills them from cards, while
// CardBag.AttemptSetToPermSet asks the loadout for permission:
//
//     currentPermSetLoadout.EquipCard(permanentCardSetCount, ...)
//         if (idLevelStrings.Count() >= sizeLimit) { return false; }
//
// His save reads 7 loadout entries, 6 cards, cardCount 7. So the seventh slot
// is drawn empty and every click on it is refused, because as far as EquipCard
// is concerned the list is already full. The extra entry is resource_gain_1-24,
// owned, not disabled, and not in the set, so it draws nothing and cannot be
// clicked off either. An invisible card holding a visible slot shut.
//
// The entry gets in because InsertCard is silent. It refuses once the set is
// at its captured cardCount, and the caller has already written the loadout by
// then. Any moment where the live count is behind (a load where the research
// bonuses are not on yet, a Big Bang, a slot that has just moved) leaves one
// behind, and it stays for good: the 1.4.0 refresh only rebuilds when
// cardCount and permanentCardSetCount disagree, and here they agree at 7.
//
// So the two lists are compared directly now. If they differ, the set is
// rebuilt from the loadout, which is enough on its own when the set simply has
// room it was not using. If it is still short after that, the leftover entries
// name cards the set genuinely cannot take, and they are dropped from the
// loadout so the slot they were holding opens up. Nothing you can see is
// touched: every entry the set did accept is kept, in its own order.
//
// CardDeck.InsertCard is the same code with a different name, and
// AttemptSetToDeck gates on currentDeckLoadout the same way, so the deck gets
// the same treatment. Not during a battle, where the hand is dealt out of the
// deck and the counts are meant to move.
//
// v1.7.0. Fuzzied: "When loading or Saving a loadout, have it snap back to
// Current Perm Set that is the topmost one. It makes not sense to have to do
// that manually each time as that is where players want to end up when they
// are done." And then: "Toggle in settings on both, and battle deck too."
//
// Entry 0 of either dropdown is the live thing you are wearing - "current
// deck", "current perm set" - and entries 1 and up are the saved loadouts.
// CardPanel keeps that as deckLoadOutIdx / permSetLoadOutIdx, where -1 means
// entry 0, so the panel draws what you have on rather than a preset.
//
// Press Load and the cards move across, but the dropdown stays parked on the
// preset, still drawing the preset. Press Save and the same thing happens the
// other way round. Either way the player is now looking at a copy rather than
// at their cards, and has to reach up and set the dropdown back by hand.
//
// So all four handlers get a postfix that puts the control back to entry 0.
// It has to drive the control, not just write permSetLoadOutIdx, or the
// dropdown says one thing and the panel draws another.
//
// SearchDropdown.SetOptionValue(int) cannot do it alone. It writes the model
// and then calls OnValueChange, which reads the TMP_Dropdown's own value - and
// that is still pointing at the preset, so it would put the selection straight
// back. The inner control is set first, without notifying, and SetOptionValue
// then agrees with it and fires CardPanel.OnPermSetLoadoutSelect(0) for us.
//
// v1.7.1. Fuzzied saved his live session into a save slot, loaded a save of
// his own, and came back to a card panel with an empty loadout list, the
// grey "you are previewing a preset" overlay stuck on, and not one card he
// could click. Pressing Load threw, and so did clicking out of the rename
// box:
//
//   IndexOutOfRangeException
//     at CardBag.LoadLoadoutToPermSet (System.Int32 idx) [0x00000]
//     at (wrapper dynamic-method) CardPanel.DMD<CardPanel::OnPermSetLoadoutLoad>
//
//   IndexOutOfRangeException
//     at (wrapper stelemref) System.Object.virt_stelemref_sealed_class
//     at CardBag.RenamePermSetLoadout (System.Int32 idx, System.String newName)
//     at CardPanel.OnPermSetLoadoutNameEdit (System.String newName)
//
// The index was -1, not some huge number: every loadout array in every save
// on disk is the full 100 entries. -1 is CardPanel's sentinel for "entry 0,
// the live set", which is why those two methods have no bounds check in
// front of them. Vanilla relies on the Save / Load / rename controls being
// hidden whenever the index is -1, and hiding them is something only
// ReloadLoadoutControls does.
//
// Which is where 1.4.0 comes in. That skip is keyed on a static signature of
// the slot count and the loadout names, and static means it outlives the
// panel. Exiting to the menu destroys the card panel and builds a new one
// whose dropdowns have no options at all and whose index is a fresh -1, and
// both of Fuzzied's saves were the same playthrough, so the names and the
// count matched to the byte. The first ReloadLoadoutControls on the new
// panel was skipped as redundant. It was not redundant. It is the only
// thing in the whole game that fills those dropdowns or clears that overlay
// (UnlockManager.cs:216 is its only caller), so the panel was left in the
// state a freshly built panel starts in and never leaves on its own.
//
// Two changes, because the two halves are separately wrong.
//
// The remembered answer now belongs to the panel and the bag it was taken
// from. A panel that has never been filled has to be filled whatever the
// names say, and a new bag can hold different names behind the same count.
//
// And the six handlers that take a loadout index now check it before
// handing it to an array. Hidden buttons are not a bounds check. The rename
// one is not even reachable by a deliberate click: TMP_InputField fires
// OnEndEdit from OnDeselect, so merely clicking somewhere else while the box
// has focus is enough. When a guard trips it also drops the remembered
// signature, so the next pass rebuilds for real and the panel unsticks
// itself instead of staying broken until a restart.
//
// v1.8.0. Fuzzied: "Loadouts are still loading specific levels, that can only
// be worse than what has been upgraded. They need to equip max cards, can
// have a setting that toggles this on or off."
//
// He is right and 1.5.0 only did half of it. That version rewrote an entry
// whose exact level you no longer own, which covers the card that VANISHES.
// It does nothing for the card that merely gets stale: own a storm_core-2 and
// a storm_core-5 and the loadout keeps putting on the 2, forever, because the
// 2 is still a level you own. Every merge widens that gap, and it widens worst
// on the cards you level most, which are the ones worth saving a loadout for.
//
// So the rewrite is no longer conditional on the saved level being gone. With
// the setting on, every entry is moved to the best copy you own.
//
// The thing that makes this more than a one line change is counting. Vanilla's
// VerifyCardLoadout groups by the whole "id-level" string and caps each group
// at GetBattleCardCount(id, level), which is what you own AT THAT EXACT LEVEL:
//
//   dictionary2[key] = Math.Min(dictionary[key], battleCardCount);
//
// A battle deck holds duplicates, so three entries of foo-0 is three cards. If
// all three were rewritten to foo-9 and you own one foo-9, that Math.Min caps
// the group at 1 and the other two are dropped with no message. "Equip the
// best" would then quietly cost you two cards, which is a worse bug than the
// one it set out to fix.
//
// So entries are decided one card id at a time, against a spendable budget per
// level. Three entries, one foo-9 and three foo-0 owned, becomes one foo-9 and
// two foo-0: the same three cards, as strong as they can be. An entry already
// sitting on a level you own spends that copy before anything is handed out,
// so nobody loses a card they were already wearing.
//
// A permanent set is the easy case and is treated as such. EquipCard refuses
// the same id twice (allowSameID: false), so only the first entry of an id is
// ever rewritten and the rest are leftovers the original drops, which is what
// 1.6.0 is about.
//
// The setting lives in PlayerPrefs next to the game's own and defaults to ON.
// A default of off is not neutral here: it is the behaviour that gets steadily
// worse the longer somebody plays, and they would never know to go looking for
// a switch.
//
// Compiled with the .NET Framework 4 csc against the game's own
// Assembly-CSharp.dll and the BepInEx 5 core DLLs (see build_plugin.ps1).

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[BepInPlugin("sti.community.loadoutplus", "STI Community Loadout Plus", "1.8.0")]
public class LoadoutPlusPlugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;

    private void Awake()
    {
        Log = Logger;
        Harmony harmony = new Harmony("sti.community.loadoutplus");
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        Logger.LogInfo("Loadout Plus active: up to 10 loadouts, live "
            + "slot-count refresh, popup lists that are not cut off, a "
            + "slot you have used is never hidden again, and the dropdown "
            + "stays open while research levels, a loadout keeps its cards "
            + "when you level them up, an equip slot that cannot be "
            + "filled is unstuck, the dropdown snaps back to what you "
            + "are wearing after a load or a save, and the loadout controls "
            + "always rebuild for a panel that has just been built");
    }

    public static int LoadoutCount()
    {
        Player player = Player.shared;
        if (player == null || player.travelProgress == null)
        {
            return 2;
        }
        int beyondVenus = Math.Max(0, player.travelProgress.farthestStarIdx - 2);
        int byTravel = Math.Min(10, 2 + beyondVenus);

        int used = UsedSlots.HighestUsed() + 1;
        if (used > byTravel)
        {
            return Math.Min(10, used);
        }
        return byTravel;
    }
}

// v1.3.0: a slot that has been used is never hidden again. See the header.
//
// The scan is two 100-entry walks over arrays that are already in memory, but
// the count getters are read on every panel redraw, so the answer is kept for
// a moment rather than recomputed per frame. It is deliberately not cached for
// the session: pressing Save fills a slot, and the new slot has to survive the
// next Big Bang without a reload.
internal static class UsedSlots
{
    private static readonly FieldInfo DeckArrayField =
        AccessTools.Field(typeof(CardBag), "deckLoadouts");
    private static readonly FieldInfo PermArrayField =
        AccessTools.Field(typeof(CardBag), "permSetLoadouts");

    private const float REFRESH_SECONDS = 0.5f;

    private static float nextScan;
    private static int cached = -1;
    private static bool loggedFailure;

    // The highest slot index that is genuinely in use, or -1 for none.
    internal static int HighestUsed()
    {
        if (Time.unscaledTime < nextScan && cached >= -1)
        {
            return cached;
        }
        nextScan = Time.unscaledTime + REFRESH_SECONDS;
        cached = Scan();
        return cached;
    }

    private static int Scan()
    {
        try
        {
            Player player = Player.shared;
            CardBag bag = (player == null) ? null : player.cardBag;
            if (bag == null)
            {
                return -1;
            }
            int best = -1;
            best = Math.Max(best, Highest(
                DeckArrayField.GetValue(bag) as CardLoadout[],
                bag.deckLoadoutNames, "panels.cards.preset.deck"));
            best = Math.Max(best, Highest(
                PermArrayField.GetValue(bag) as CardLoadout[],
                bag.permSetLoadoutNames, "panels.cards.preset.permSet"));
            return best;
        }
        catch (Exception e)
        {
            if (!loggedFailure)
            {
                loggedFailure = true;
                LoadoutPlusPlugin.Log.LogWarning("used-slot scan skipped: " + e);
            }
            return -1;
        }
    }

    private static int Highest(CardLoadout[] slots, string[] names, string locPath)
    {
        int count = 0;
        if (slots != null && slots.Length > count) { count = slots.Length; }
        if (names != null && names.Length > count) { count = names.Length; }
        if (count == 0)
        {
            return -1;
        }

        // The name the game writes into a slot it has merely shown you. Read
        // once per scan, because it is a dictionary lookup and a concatenation
        // per slot otherwise.
        string autoPrefix = null;
        try { autoPrefix = Localisation.GetLocalisation(locPath); }
        catch (Exception) { autoPrefix = null; }

        int best = -1;
        for (int i = 0; i < count; i++)
        {
            if (slots != null && i < slots.Length)
            {
                CardLoadout slot = slots[i];
                if (slot != null && slot.idLevelStrings != null
                    && slot.idLevelStrings.Count > 0)
                {
                    best = i;
                    continue;
                }
            }
            if (names != null && i < names.Length)
            {
                string name = names[i];
                if (!string.IsNullOrEmpty(name)
                    && (autoPrefix == null
                        || name != (autoPrefix + " " + (i + 1))))
                {
                    best = i;
                }
            }
        }
        return best;
    }
}

[HarmonyPatch(typeof(CardBag), "deckLoadoutCount", MethodType.Getter)]
public static class DeckLoadoutCountPatch
{
    public static void Postfix(ref int __result)
    {
        __result = LoadoutPlusPlugin.LoadoutCount();
    }
}

[HarmonyPatch(typeof(CardBag), "permSetLoadoutCount", MethodType.Getter)]
public static class PermSetLoadoutCountPatch
{
    public static void Postfix(ref int __result)
    {
        __result = LoadoutPlusPlugin.LoadoutCount();
    }
}

// UnlockManager.UnlockRequirementUpdate is the "something you unlocked just
// changed" pass; Research.cs flags it on every research level-up, which is
// when a slot count can move. Rebuilding only happens when the count actually
// changed, always from currentPermSetLoadout / currentDeckLoadout, so nothing
// equipped is lost. The deck is left alone during a battle.
//
// NOT hooked on CardBag.ForceReloadUI, which looks like the obvious place: it
// also runs early inside CardBag.OnDataLoad, BEFORE ReloadPassiveBonuses, and
// currentPassiveBonus is [JsonIgnore] - so it is still null there. Reading
// permanentCardSetCount at that moment goes Modifiers.extraEquipSlot ->
// Player.shared.cardBag.currentPassiveBonus.GetBonus and throws. That threw
// out of the postfix into UnlockManager.Update before it could clear
// needsUnlockReqUpdate, so the whole update retried and threw every single
// frame. Everything here is guarded and wrapped for that reason.
[HarmonyPatch(typeof(UnlockManager), "UnlockRequirementUpdate")]
public static class SlotCountRefreshPatch
{
    public static void Postfix()
    {
        try
        {
            RefreshStaleSlotCounts();
        }
        catch (Exception e)
        {
            // never let this break the caller's update loop
            UnityEngine.Debug.LogWarning("[LoadoutPlus] slot refresh skipped: " + e.Message);
        }
    }

    private static void RefreshStaleSlotCounts()
    {
        Player player = Player.shared;
        if (player == null || player.cardBag == null || TechnoManager.shared == null)
        {
            return;
        }

        CardBag bag = player.cardBag;
        if (bag.currentPassiveBonus == null)
        {
            return;  // bonuses not built yet - the counts cannot be read
        }

        PermanentCardSet permSet = bag.currentPermSet;
        if (permSet != null && permSet.cardCount != bag.permanentCardSetCount)
        {
            bag.ForceReloadCurrentPermSet();
        }
        ReconcilePermSet(bag);

        BattleManager battle = BattleManager.shared;
        if (battle != null && battle.inBattle)
        {
            return;
        }

        CardDeck deck = bag.currentDeck;
        if (deck != null && (deck.deckSize != bag.deckSize
                             || deck.handCardCount != 3 + Modifiers.extraHandSlot))
        {
            bag.ForceReloadCurrentDeck();
        }
        ReconcileDeck(bag);
    }

    private static readonly FieldInfo PermLoadoutField =
        AccessTools.Field(typeof(CardBag), "currentPermSetLoadout");

    private static readonly FieldInfo DeckLoadoutField =
        AccessTools.Field(typeof(CardBag), "currentDeckLoadout");

    // v1.6.0: an equipped-card list the panel cannot show. See the header.
    private static void ReconcilePermSet(CardBag bag)
    {
        CardLoadout loadout = PermLoadoutField.GetValue(bag) as CardLoadout;
        if (loadout == null || loadout.idLevelStrings == null
            || bag.currentPermSet == null || bag.currentPermSet.cards == null
            || loadout.idLevelStrings.Count == bag.currentPermSet.cards.Count)
        {
            return;
        }

        int entries = loadout.idLevelStrings.Count;
        bag.ForceReloadCurrentPermSet();
        List<string> held = PermSetIds(bag);
        if (held.Count != entries)
        {
            // Whatever the set would not take is what is holding the slot.
            Trim(loadout, held);
            bag.ForceReloadCurrentPermSet();
            held = PermSetIds(bag);
        }
        LoadoutPlusPlugin.Log.LogInfo("Loadout Plus: your equip slots had "
            + entries + " card(s) written down but only "
            + held.Count + " on screen, so a free slot would not take a card. "
            + "Rebuilt: " + held.Count + " equipped, "
            + loadout.idLevelStrings.Count + " written down");
        bag.ForceReloadUI();
    }

    // The deck is the same code under a different name. Skipped in battle,
    // where the hand is dealt out of the deck and the counts move on purpose.
    private static void ReconcileDeck(CardBag bag)
    {
        CardLoadout loadout = DeckLoadoutField.GetValue(bag) as CardLoadout;
        if (loadout == null || loadout.idLevelStrings == null
            || bag.currentDeck == null || bag.currentDeck.cards == null
            || loadout.idLevelStrings.Count == bag.currentDeck.cards.Count)
        {
            return;
        }

        int entries = loadout.idLevelStrings.Count;
        bag.ForceReloadCurrentDeck();
        List<string> held = DeckIds(bag);
        if (held.Count != entries)
        {
            Trim(loadout, held);
            bag.ForceReloadCurrentDeck();
            held = DeckIds(bag);
        }
        LoadoutPlusPlugin.Log.LogInfo("Loadout Plus: your deck had "
            + entries + " card(s) written down but only "
            + held.Count + " on screen, so a free slot would not take a card. "
            + "Rebuilt: " + held.Count + " in the deck, "
            + loadout.idLevelStrings.Count + " written down");
        bag.ForceReloadUI();
    }

    private static List<string> PermSetIds(CardBag bag)
    {
        List<string> ids = new List<string>();
        List<PermanentCard> cards = bag.currentPermSet.cards;
        for (int i = 0; i < cards.Count; i++)
        {
            if (cards[i] != null) { ids.Add(cards[i].idLevelString); }
        }
        return ids;
    }

    private static List<string> DeckIds(CardBag bag)
    {
        List<string> ids = new List<string>();
        List<BattleCard> cards = bag.currentDeck.cards;
        for (int i = 0; i < cards.Count; i++)
        {
            if (cards[i] != null) { ids.Add(cards[i].idLevelString); }
        }
        return ids;
    }

    // Keep every entry the set actually accepted, in the loadout's own order,
    // counting duplicates, and drop the rest. Written as a budget per id so a
    // deck holding two of the same card keeps both.
    private static void Trim(CardLoadout loadout, List<string> held)
    {
        Dictionary<string, int> budget = new Dictionary<string, int>();
        for (int i = 0; i < held.Count; i++)
        {
            int had;
            budget[held[i]] = budget.TryGetValue(held[i], out had) ? had + 1 : 1;
        }

        List<string> keep = new List<string>();
        for (int i = 0; i < loadout.idLevelStrings.Count; i++)
        {
            string entry = loadout.idLevelStrings[i];
            int left;
            if (budget.TryGetValue(entry, out left) && left > 0)
            {
                budget[entry] = left - 1;
                keep.Add(entry);
            }
            else
            {
                LoadoutPlusPlugin.Log.LogInfo("Loadout Plus: " + entry
                    + " was written down but the set would not take it, "
                    + "so it is no longer holding a slot shut");
            }
        }
        loadout.idLevelStrings = keep;
    }
}


// v1.4.0: rebuild the loadout controls only when they would come out
// different. See the header for the chain that closes the dropdown.
[HarmonyPatch(typeof(CardPanel), "ReloadLoadoutControls")]
public static class LoadoutControlsRebuildPatch
{
    // A byte that cannot occur in a loadout name the player typed, so two
    // different lists can never build the same signature.
    private const char SEP = '';

    // Enough lines to show the shape of this in a log without a long session
    // writing one every frame.
    private const int MAX_SAID = 8;
    private static int said;

    private static string lastSignature;
    private static int skippedSinceRebuild;

    // v1.7.1. The signature answers "would these controls come out
    // different", which is not the same question as "have these controls
    // ever been drawn", and the two come apart the moment the panel is
    // rebuilt. See the header for what that cost.
    private static CardPanel lastPanel;
    private static CardBag lastBag;

    // Make the next pass rebuild for certain. For when something else finds
    // the panel in a state only a real rebuild can clear.
    internal static void Invalidate()
    {
        lastSignature = null;
    }

    public static bool Prefix(CardPanel __instance)
    {
        Player owner = Player.shared;
        CardBag ownerBag = owner == null ? null : owner.cardBag;

        // ReferenceEquals rather than ==, because a destroyed CardPanel
        // compares equal to null under Unity's operator, and then every
        // later panel would look like the same one.
        if (!ReferenceEquals(__instance, lastPanel)
            || !ReferenceEquals(ownerBag, lastBag))
        {
            lastPanel = __instance;
            lastBag = ownerBag;
            lastSignature = null;
        }

        string now;
        try
        {
            now = Signature();
        }
        catch (Exception e)
        {
            // Cannot tell whether anything moved, so let the game do what it
            // has always done rather than risk a stale panel.
            lastSignature = null;
            LoadoutPlusPlugin.Log.LogWarning("Loadout controls rebuilt "
                + "unconditionally: " + e.Message);
            return true;
        }

        if (now == null)
        {
            // Too early to read the bag. Vanilla behaviour, and do not let
            // this pass be remembered as the answer.
            lastSignature = null;
            return true;
        }

        if (now == lastSignature)
        {
            skippedSinceRebuild++;
            if (skippedSinceRebuild == 1 && said < MAX_SAID)
            {
                said++;
                LoadoutPlusPlugin.Log.LogInfo("Loadout dropdown: leaving the "
                    + "list alone, slot count and names are unchanged");
            }
            return false;
        }

        if (skippedSinceRebuild > 0 && said < MAX_SAID)
        {
            said++;
            LoadoutPlusPlugin.Log.LogInfo("Loadout dropdown: rebuilding, the "
                + "list really changed (" + skippedSinceRebuild
                + " rebuild(s) skipped since the last one)");
        }
        lastSignature = now;
        skippedSinceRebuild = 0;
        return true;
    }

    // What the two dropdowns draw, and nothing else: how many slots are on
    // offer and what each one is called. Returns null when the bag cannot be
    // read yet, which the caller treats as "do not skip".
    private static string Signature()
    {
        Player player = Player.shared;
        if (player == null || player.cardBag == null)
        {
            return null;
        }

        CardBag bag = player.cardBag;
        string[] deckNames = bag.deckLoadoutNames;
        string[] permNames = bag.permSetLoadoutNames;
        if (deckNames == null || permNames == null)
        {
            return null;
        }

        StringBuilder sb = new StringBuilder(128);
        Append(sb, bag.deckLoadoutCount, deckNames);
        sb.Append(SEP);
        Append(sb, bag.permSetLoadoutCount, permNames);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, int count, string[] names)
    {
        sb.Append(count);
        int n = Math.Min(count, names.Length);
        for (int i = 0; i < n; i++)
        {
            sb.Append(SEP);
            sb.Append(names[i] ?? "");
        }
    }
}


// v1.5.0: keep a loadout's cards when their level changes. See the header.
// ===================================================================
// v1.8.0: a loadout equips the best copy you own. See the header.
// ===================================================================

internal static class LoadoutBestLevel
{
    // PlayerPrefs, the same store the game's own settings use, so the choice
    // survives a restart without the mod keeping a file of its own.
    public const string PREF_KEY = "communityLoadoutBestLevel";

    // On by default. A saved loadout names an exact level, and every merge
    // leaves it pointing further behind what you actually own, so the default
    // that does nothing is the one that quietly gets worse the longer you play.
    public static bool Enabled
    {
        get { return PlayerPrefs.GetInt(PREF_KEY, 1) == 1; }
        set { PlayerPrefs.SetInt(PREF_KEY, value ? 1 : 0); }
    }
}


[HarmonyPatch(typeof(CardBag), "VerifyCardLoadout")]
public static class LoadoutLevelFallbackPatch
{
    private const int MAX_SAID = 12;
    private static int said;

    public static void Prefix(CardBag __instance, CardLoadout loadout)
    {
        if (__instance == null || loadout == null)
        {
            return;
        }
        List<string> entries = loadout.idLevelStrings;
        if (entries == null || entries.Count == 0)
        {
            return;
        }

        // Group by card id before deciding anything. Vanilla's own
        // VerifyCardLoadout counts by the whole "id-level" string and caps each
        // group at what you own AT THAT EXACT LEVEL, so handing three deck
        // entries the single level 9 copy you own would leave two of them with
        // nothing and vanilla would drop both without a word. Deciding a whole
        // id in one go is the only way to move levels up without losing cards.
        Dictionary<string, List<int>> byId = new Dictionary<string, List<int>>();
        List<int> levels = new List<int>();

        for (int i = 0; i < entries.Count; i++)
        {
            string id = null;
            int level = 0;
            if (!string.IsNullOrEmpty(entries[i]))
            {
                try
                {
                    Utils.SplitIdLevel(entries[i], out id, out level);
                }
                catch (Exception)
                {
                    id = null;
                }
            }
            levels.Add(level);
            if (id == null)
            {
                continue;
            }
            List<int> slots;
            if (!byId.TryGetValue(id, out slots))
            {
                slots = new List<int>();
                byId[id] = slots;
            }
            slots.Add(i);
        }

        foreach (KeyValuePair<string, List<int>> pair in byId)
        {
            bool isPermanent;
            IdenticalCardSet set = Owner(__instance, pair.Key, out isPermanent);
            if (set == null || set.disabled || set.levelDict == null)
            {
                continue;
            }
            Rewrite(entries, levels, pair.Key, set, pair.Value, isPermanent);
        }
    }

    private static void Rewrite(List<string> entries, List<int> levels,
        string id, IdenticalCardSet set, List<int> slots, bool isPermanent)
    {
        // What you own of this card, best level first, with a spendable count
        // against each one.
        List<int> owned = new List<int>();
        Dictionary<int, int> left = new Dictionary<int, int>();
        foreach (KeyValuePair<int, CardSetLevel> pair in set.levelDict)
        {
            if (pair.Value != null && pair.Value.count > 0)
            {
                owned.Add(pair.Key);
                left[pair.Key] = pair.Value.count;
            }
        }
        if (owned.Count == 0)
        {
            return; // none at any level, so there is nothing to swap to
        }
        owned.Sort();
        owned.Reverse();

        bool best = LoadoutBestLevel.Enabled;

        // A permanent set refuses the same card twice (CardLoadout.EquipCard,
        // allowSameID: false), so only the first entry of an id can become
        // anything. Any others are leftovers and the original drops them.
        int take = isPermanent ? 1 : slots.Count;

        // Two passes on purpose. An entry that is already sitting on a level
        // you own spends that copy FIRST, so turning this setting off, or
        // leaving it off, cannot cost somebody a card they were already
        // wearing. Only what is left over gets handed out.
        List<int> wanting = new List<int>();
        for (int n = 0; n < take; n++)
        {
            int i = slots[n];
            int room;
            if (!best && left.TryGetValue(levels[i], out room) && room > 0)
            {
                left[levels[i]] = room - 1;
            }
            else
            {
                wanting.Add(i);
            }
        }

        foreach (int i in wanting)
        {
            int give = -1;
            for (int k = 0; k < owned.Count; k++)
            {
                if (left[owned[k]] > 0)
                {
                    give = owned[k];
                    break;
                }
            }
            if (give < 0)
            {
                break; // every copy is spoken for, the rest keep what they had
            }
            left[give] = left[give] - 1;
            int wasLevel = levels[i];
            if (give == wasLevel)
            {
                continue; // already the best available, nothing to write
            }

            string swapped = id + "-" + give;
            // Guarded even though grouping makes it nearly impossible: a
            // permanent loadout that arrived holding the same id twice could
            // still collide, and a duplicate in a permanent set is exactly the
            // invisible-card bug 1.6.0 exists to clear up.
            if (isPermanent && entries.Contains(swapped))
            {
                continue;
            }

            string was = entries[i];
            entries[i] = swapped;
            levels[i] = give;

            if (said < MAX_SAID)
            {
                said++;
                // Which of the two things happened is decided by the ORIGINAL
                // stock, not by what is left after the spending above. An
                // entry can be perfectly valid and still get moved up, and
                // reading that back as "a level you no longer own" would send
                // the next person hunting a bug that is not there.
                bool stale = Owned(set, wasLevel) == 0;
                LoadoutPlusPlugin.Log.LogInfo(stale
                    ? "Loadout: '" + was + "' is a level you no longer own, "
                        + "loading '" + swapped + "' instead"
                    : "Loadout: '" + was + "' is not the best you own, "
                        + "equipping '" + swapped + "' instead");
            }
        }
    }

    private static int Owned(IdenticalCardSet set, int level)
    {
        CardSetLevel node;
        if (set.levelDict.TryGetValue(level, out node) && node != null)
        {
            return node.count;
        }
        return 0;
    }

    private static IdenticalCardSet Owner(CardBag bag, string id,
        out bool isPermanent)
    {
        isPermanent = false;
        IdenticalCardSet set;
        // VerifyCardLoadout looks at the battle dictionary first, so match it
        if (bag.battleCardsDict != null
            && bag.battleCardsDict.TryGetValue(id, out set) && set != null)
        {
            return set;
        }
        if (bag.permanentCardsDict != null
            && bag.permanentCardsDict.TryGetValue(id, out set) && set != null)
        {
            isPermanent = true;
            return set;
        }
        return null;
    }
}


// v1.2.0: keep a long popup on screen. See the header.
[HarmonyPatch(typeof(TMP_Dropdown), "Show")]
public static class DropdownFitPatch
{
    private static readonly FieldInfo ListField =
        AccessTools.Field(typeof(TMP_Dropdown), "m_Dropdown");

    // Enough to identify the shape of the problem in a log without letting a
    // dropdown the player opens all day write a line every time.
    private const int MAX_SAID = 12;
    private static int said;

    private static void Postfix(TMP_Dropdown __instance)
    {
        try
        {
            Fit(__instance);
        }
        catch (Exception e)
        {
            LoadoutPlusPlugin.Log.LogWarning("Popup list left alone: "
                + e.Message);
        }
    }

    private static void Fit(TMP_Dropdown dd)
    {
        if (dd == null || ListField == null)
        {
            return;
        }
        GameObject list = ListField.GetValue(dd) as GameObject;
        if (list == null)
        {
            return;
        }
        RectTransform listRT = list.transform as RectTransform;
        if (listRT == null)
        {
            return;
        }

        ScrollRect scroll = list.GetComponentInChildren<ScrollRect>(true);
        RectTransform content = (scroll == null) ? null : scroll.content;
        RectTransform view = (scroll == null) ? null : scroll.viewport;
        int items = (content == null) ? 0 : content.childCount;
        float contentH = (content == null) ? 0f : content.rect.height;
        float listH = listRT.rect.height;

        string did = "";

        // A scroll view that is switched off is the whole fault on its own,
        // and switching it on costs nothing where it was already on.
        if (scroll != null && !scroll.vertical)
        {
            scroll.vertical = true;
            did += " turned vertical scrolling on;";
        }

        // No scroll view at all means the only way to show the rest of the
        // list is to be taller. Capped so a long list cannot cover the screen.
        if (scroll == null && contentH <= 0f && items > 0)
        {
            contentH = Height(listRT, items);
        }
        if (scroll == null && contentH > listH + 1f)
        {
            float cap = Cap(listRT);
            float want = Math.Min(contentH, cap);
            if (want > listH + 1f)
            {
                listRT.sizeDelta = new Vector2(listRT.sizeDelta.x,
                    listRT.sizeDelta.y + (want - listH));
                did += " grew it from " + Mathf.RoundToInt(listH) + " to "
                    + Mathf.RoundToInt(want) + " to fit "
                    + Mathf.RoundToInt(contentH) + ";";
                listH = listRT.rect.height;
            }
        }

        // And the mask. Measured against the popup's own rectangle, so a
        // popup that fits inside its panel is never moved.
        Component clip = Clipper(listRT);
        if (clip != null && !Contains(clip.transform as RectTransform, listRT))
        {
            Canvas root = RootOf(list);
            if (root != null && Lift(listRT, root))
            {
                did += " lifted it out of '" + clip.gameObject.name
                    + "', which was cutting it off;";
            }
        }

        if (did != "" || said < MAX_SAID)
        {
            said++;
            LoadoutPlusPlugin.Log.LogInfo("Popup list on '"
                + dd.gameObject.name + "': " + items + " line(s), list "
                + Mathf.RoundToInt(listH) + "px, content "
                + Mathf.RoundToInt(contentH) + "px, scroll view "
                + ((scroll == null) ? "missing" : "present")
                + ", viewport " + ((view == null) ? 0
                    : Mathf.RoundToInt(view.rect.height)) + "px."
                + ((did == "") ? " Left alone." : did));
        }
    }

    // Only used when there is no scroll view to read a content height from.
    private static float Height(RectTransform listRT, int items)
    {
        RectTransform first = (listRT.childCount > 0)
            ? listRT.GetChild(0) as RectTransform : null;
        float row = (first == null || first.rect.height < 1f)
            ? 20f : first.rect.height;
        return row * items;
    }

    private static float Cap(RectTransform listRT)
    {
        Canvas root = RootOf(listRT.gameObject);
        RectTransform canvasRT = (root == null)
            ? null : root.transform as RectTransform;
        float h = (canvasRT == null) ? Screen.height : canvasRT.rect.height;
        return h * 0.7f;
    }

    private static Canvas RootOf(GameObject go)
    {
        Canvas c = go.GetComponentInParent<Canvas>();
        return (c == null) ? null : c.rootCanvas;
    }

    // The nearest thing above the popup that clips its children.
    private static Component Clipper(RectTransform rt)
    {
        Transform t = rt.parent;
        while (t != null)
        {
            RectMask2D r = t.GetComponent<RectMask2D>();
            if (r != null && r.enabled)
            {
                return r;
            }
            Mask m = t.GetComponent<Mask>();
            if (m != null && m.enabled && m.graphic != null)
            {
                return m;
            }
            Canvas c = t.GetComponent<Canvas>();
            if (c != null && c.isRootCanvas)
            {
                return null;
            }
            t = t.parent;
        }
        return null;
    }

    // World corners rather than rects: the two can sit under different
    // parents with different scales.
    private static bool Contains(RectTransform outer, RectTransform inner)
    {
        if (outer == null)
        {
            return true;
        }
        Vector3[] o = new Vector3[4];
        Vector3[] i = new Vector3[4];
        outer.GetWorldCorners(o);
        inner.GetWorldCorners(i);
        const float slack = 1f;
        return i[0].x >= o[0].x - slack && i[0].y >= o[0].y - slack
            && i[2].x <= o[2].x + slack && i[2].y <= o[2].y + slack;
    }

    // Out to the root canvas, keeping the size and the place on screen the
    // game just chose for it. Anchors are collapsed to a point first, or a
    // template anchored to the edges of a small panel would stretch to the
    // width of the whole screen the moment its parent changed.
    private static bool Lift(RectTransform rt, Canvas root)
    {
        RectTransform canvasRT = root.transform as RectTransform;
        if (canvasRT == null || rt.parent == canvasRT)
        {
            return false;
        }
        Vector2 size = rt.rect.size;
        Vector3 where = rt.position;
        rt.SetParent(canvasRT, true);
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = size;
        rt.position = where;
        Clamp(rt, canvasRT);
        return true;
    }

    // And back inside the screen, since it may now be taller than it was.
    private static void Clamp(RectTransform rt, RectTransform canvasRT)
    {
        Vector3[] a = new Vector3[4];
        Vector3[] b = new Vector3[4];
        rt.GetWorldCorners(a);
        canvasRT.GetWorldCorners(b);
        float dy = 0f;
        if (a[0].y < b[0].y)
        {
            dy = b[0].y - a[0].y;
        }
        else if (a[1].y > b[1].y)
        {
            dy = b[1].y - a[1].y;
        }
        if (dy != 0f)
        {
            rt.position = new Vector3(rt.position.x, rt.position.y + dy,
                rt.position.z);
        }
    }
}


// ===================================================================
// v1.7.0: back to the live set after a Load or a Save. See the header.
// ===================================================================

internal static class LoadoutSnapBack
{
    // PlayerPrefs, the same store the game's own settings use, so the choice
    // survives a restart without the mod keeping a file of its own.
    public const string PREF_KEY = "communityLoadoutSnapBack";

    public static bool Enabled
    {
        get { return PlayerPrefs.GetInt(PREF_KEY, 1) == 1; }
        set { PlayerPrefs.SetInt(PREF_KEY, value ? 1 : 0); }
    }

    // The TMP_Dropdown inside the game's SearchDropdown wrapper. Private and
    // serialized, so reflection is the only way at it, and null here just
    // means falling back to moving the panel without the control.
    private static readonly FieldInfo InnerDropdownField =
        AccessTools.Field(typeof(SearchDropdown), "dropdown");

    private const int MAX_SAID = 6;
    private static int said;

    // "after" is the button that got us here, Load or Save. The two used to
    // log the identical sentence, which meant the log could prove the snap
    // back had happened and could not prove which button did it. Test plan
    // items B3 and B4 are exactly that pair, so one of them was unprovable
    // from the log and had to be asked for by hand. Naming the button costs
    // one word and makes both readable off disk.
    internal static void Snap(CardPanel panel, SearchDropdown dropdown,
        bool permSet, string after)
    {
        if (!Enabled || panel == null || dropdown == null)
        {
            return;
        }
        try
        {
            if (dropdown.value == 0)
            {
                return; // already showing the live set
            }

            TMP_Dropdown inner = InnerDropdownField == null
                ? null
                : InnerDropdownField.GetValue(dropdown) as TMP_Dropdown;

            if (inner != null)
            {
                // Order matters. OnValueChange reads the inner control, so it
                // has to agree with the model before SetOptionValue fires the
                // panel's own select handler.
                inner.Hide();
                inner.SetValueWithoutNotify(0);
                dropdown.SetOptionValue(0);
            }
            else
            {
                // No way to reach the control, so at least put the panel on
                // the live set. The dropdown will read wrong until the next
                // rebuild, which is still better than drawing a preset.
                if (permSet) { panel.OnPermSetLoadoutSelect(0); }
                else { panel.OnDeckLoadoutSelect(0); }
            }

            if (said < MAX_SAID)
            {
                said++;
                LoadoutPlusPlugin.Log.LogInfo("Loadout dropdown: after a "
                    + after + ", back to the "
                    + (permSet ? "current perm set" : "current deck"));
            }
        }
        catch (Exception e)
        {
            // A throw here would repeat on every press, so it is swallowed.
            // Worst case the player puts the dropdown back by hand, which is
            // exactly what they did before this existed.
            LoadoutPlusPlugin.Log.LogWarning("Could not snap the loadout "
                + "dropdown back: " + e.Message);
        }
    }
}

[HarmonyPatch(typeof(CardPanel), "OnPermSetLoadoutLoad")]
public static class PermSetLoadoutLoadSnapBackPatch
{
    private static void Postfix(CardPanel __instance,
        SearchDropdown ___permLoadoutDropdown)
    {
        LoadoutSnapBack.Snap(__instance, ___permLoadoutDropdown, true, "Load");
    }
}

[HarmonyPatch(typeof(CardPanel), "OnPermSetLoadoutSave")]
public static class PermSetLoadoutSaveSnapBackPatch
{
    private static void Postfix(CardPanel __instance,
        SearchDropdown ___permLoadoutDropdown)
    {
        LoadoutSnapBack.Snap(__instance, ___permLoadoutDropdown, true, "Save");
    }
}

[HarmonyPatch(typeof(CardPanel), "OnDeckLoadoutLoad")]
public static class DeckLoadoutLoadSnapBackPatch
{
    private static void Postfix(CardPanel __instance,
        SearchDropdown ___deckLoadoutDropdown)
    {
        LoadoutSnapBack.Snap(__instance, ___deckLoadoutDropdown, false, "Load");
    }
}

[HarmonyPatch(typeof(CardPanel), "OnDeckLoadoutSave")]
public static class DeckLoadoutSaveSnapBackPatch
{
    private static void Postfix(CardPanel __instance,
        SearchDropdown ___deckLoadoutDropdown)
    {
        LoadoutSnapBack.Snap(__instance, ___deckLoadoutDropdown, false, "Save");
    }
}


// ===================================================================
// v1.7.1: a loadout index of -1 means the live set, not slot minus one.
// See the header.
// ===================================================================

internal static class LoadoutIndexGuard
{
    private const int MAX_SAID = 6;
    private static int said;

    // True when the handler should be skipped. -1 is the panel's sentinel
    // for "showing what you are wearing", and there is no slot to save to,
    // load from or rename there, so doing nothing is the whole of the
    // correct behaviour.
    internal static bool OutOfRange(int idx, string what)
    {
        if (idx >= 0)
        {
            return false;
        }

        // Only a panel that never got its first rebuild can be here, so give
        // the next pass a reason to do it properly.
        LoadoutControlsRebuildPatch.Invalidate();

        if (said < MAX_SAID)
        {
            said++;
            LoadoutPlusPlugin.Log.LogWarning(what + " was pressed while the "
                + "panel was showing the live set (index " + idx + "), which "
                + "would have thrown. Ignored, and the loadout controls will "
                + "rebuild on the next pass.");
        }
        return true;
    }
}

[HarmonyPatch(typeof(CardPanel), "OnPermSetLoadoutLoad")]
public static class PermSetLoadoutLoadGuardPatch
{
    private static bool Prefix(int ___permSetLoadOutIdx)
    {
        return !LoadoutIndexGuard.OutOfRange(___permSetLoadOutIdx,
            "Load perm set loadout");
    }
}

[HarmonyPatch(typeof(CardPanel), "OnPermSetLoadoutSave")]
public static class PermSetLoadoutSaveGuardPatch
{
    private static bool Prefix(int ___permSetLoadOutIdx)
    {
        return !LoadoutIndexGuard.OutOfRange(___permSetLoadOutIdx,
            "Save perm set loadout");
    }
}

[HarmonyPatch(typeof(CardPanel), "OnPermSetLoadoutNameEdit")]
public static class PermSetLoadoutNameGuardPatch
{
    private static bool Prefix(int ___permSetLoadOutIdx)
    {
        return !LoadoutIndexGuard.OutOfRange(___permSetLoadOutIdx,
            "Rename perm set loadout");
    }
}

[HarmonyPatch(typeof(CardPanel), "OnDeckLoadoutLoad")]
public static class DeckLoadoutLoadGuardPatch
{
    private static bool Prefix(int ___deckLoadOutIdx)
    {
        return !LoadoutIndexGuard.OutOfRange(___deckLoadOutIdx,
            "Load deck loadout");
    }
}

[HarmonyPatch(typeof(CardPanel), "OnDeckLoadoutSave")]
public static class DeckLoadoutSaveGuardPatch
{
    private static bool Prefix(int ___deckLoadOutIdx)
    {
        return !LoadoutIndexGuard.OutOfRange(___deckLoadOutIdx,
            "Save deck loadout");
    }
}

[HarmonyPatch(typeof(CardPanel), "OnDeckLoadoutNameEdit")]
public static class DeckLoadoutNameGuardPatch
{
    private static bool Prefix(int ___deckLoadOutIdx)
    {
        return !LoadoutIndexGuard.OutOfRange(___deckLoadOutIdx,
            "Rename deck loadout");
    }
}

// The toggle row, cloned from the vanilla "hide card control UI" toggle and
// adopted into the mod's own settings section. Same pattern as DevConsoleOff,
// which has the long version of why it is done this way.
[HarmonyPatch(typeof(SettingsManager), "LoadSystemSettings")]
public static class AddLoadoutSnapBackTogglePatch
{
    private const string CLONE_NAME = "CommunityLoadoutSnapBackRow";

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
                    LoadoutPlusPlugin.Log))
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
            // Drop the listeners Instantiate copied BEFORE writing isOn, or
            // the template's own callback fires and changes a vanilla setting.
            toggle.onValueChanged = new Toggle.ToggleEvent();
            toggle.isOn = LoadoutSnapBack.Enabled;
            toggle.onValueChanged.AddListener(OnToggleChanged);

            LangText label = clone.GetComponentInChildren<LangText>(true);
            if (label != null)
            {
                label.SetLocalisedText("Loadout snaps back to your cards");
            }

            TooltipComposite tip = clone.GetComponent<TooltipComposite>();
            if (tip == null) { tip = clone.AddComponent<TooltipComposite>(); }
            tip.defaultTooltip = "After you load or save a loadout, the "
                + "dropdown goes back to the top entry, the deck or permanent "
                + "set you are actually wearing. Without this it stays on the "
                + "loadout you just used and keeps drawing that, so you have "
                + "to put it back by hand every time. Covers the battle deck "
                + "and the permanent set both.";

            LoadoutPlusPlugin.Log.LogInfo(
                "Settings: added 'Loadout snaps back to your cards' toggle (on="
                + toggle.isOn + ")");
        }
        catch (Exception e)
        {
            LoadoutPlusPlugin.Log.LogWarning(
                "Could not add the loadout snap back toggle: " + e.Message);
        }
    }

    private static void OnToggleChanged(bool isOn)
    {
        LoadoutSnapBack.Enabled = isOn;
        LoadoutPlusPlugin.Log.LogInfo("Loadout snap back set to " + isOn);
    }
}


// v1.8.0 toggle: equip the best copy you own. Same clone-and-adopt pattern
// as the snap back row above it, which carries the long explanation of why
// a row is cloned rather than built from scratch.
// adopted into the mod's own settings section. Same pattern as DevConsoleOff,
// which has the long version of why it is done this way.
[HarmonyPatch(typeof(SettingsManager), "LoadSystemSettings")]
public static class AddLoadoutBestLevelTogglePatch
{
    private const string CLONE_NAME = "CommunityLoadoutBestLevelRow";

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
                    LoadoutPlusPlugin.Log))
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
            // Drop the listeners Instantiate copied BEFORE writing isOn, or
            // the template's own callback fires and changes a vanilla setting.
            toggle.onValueChanged = new Toggle.ToggleEvent();
            toggle.isOn = LoadoutBestLevel.Enabled;
            toggle.onValueChanged.AddListener(OnToggleChanged);

            LangText label = clone.GetComponentInChildren<LangText>(true);
            if (label != null)
            {
                label.SetLocalisedText("Loadouts equip your best cards");
            }

            TooltipComposite tip = clone.GetComponent<TooltipComposite>();
            if (tip == null) { tip = clone.AddComponent<TooltipComposite>(); }
            tip.defaultTooltip = "A saved loadout remembers an exact card "
                + "level, so every time you merge a card the loadout keeps "
                + "putting on the weaker copy you saved instead of the one "
                + "you just built. With this on, loading a loadout equips "
                + "the best copy of each card you own. It never takes more "
                + "copies than you have, so a deck asking for three of a "
                + "card you own one strong one of gets that one plus your "
                + "next best two. Covers the battle deck and the permanent "
                + "set both.";

            LoadoutPlusPlugin.Log.LogInfo(
                "Settings: added 'Loadouts equip your best cards' toggle (on="
                + toggle.isOn + ")");
        }
        catch (Exception e)
        {
            LoadoutPlusPlugin.Log.LogWarning(
                "Could not add the best card level toggle: " + e.Message);
        }
    }

    private static void OnToggleChanged(bool isOn)
    {
        LoadoutBestLevel.Enabled = isOn;
        LoadoutPlusPlugin.Log.LogInfo("Loadout best card level set to " + isOn);
    }
}
