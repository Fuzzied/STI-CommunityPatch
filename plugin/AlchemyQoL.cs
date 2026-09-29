// Space Travel Idle community mod - alchemy forge QoL
//
// 1) "MAX" button under the forge's Synthesize button: repeats the
//    synthesis until you run out of biomass, stardust or ingredient
//    cards (hard cap 2000 per click), then reports how many cards were
//    made and the best level rolled. Each iteration is exactly the
//    vanilla CardPanel.ConfirmCardAlchemy logic - same costs, same
//    ingredient consumption, same level roll, same unlock handling.
//
// 2) "DE-LEVEL" toggle button under MAX (v1.1.0): when ON, synthesis
//    that is short of ingredient cards automatically splits your
//    higher-level copies of that card back down. Merging is 4 cards of
//    level N -> 1 card of level N+1, so splitting 1 card of level N
//    into 4 cards of level N-1 loses nothing - the total merged value
//    is preserved exactly. Splits always take the LOWEST level above
//    the needed one first (least disruption), never touch cards that
//    are protected or equipped in a loadout, and always keep at least
//    one copy at the set's highest level so your best card survives.
//    Each split costs biomass: 10% of the split card's sell value.
//
// 3) Missing-ingredient detail (v1.3.0): the base game answers a
//    synthesis it cannot afford with "not enough cards" and nothing else,
//    which leaves the player to work out which of four ingredients was
//    short and by how much. The recipe is right there on screen, so this
//    lists each one with what you have against what it wants, and marks
//    the ones you are short of. Two things the plain count would hide get
//    said outright: copies that are protected or sitting in a loadout are
//    not spendable, and where DE-LEVEL is off but you own higher-level
//    copies that could be split down, that is worth knowing before you go
//    hunting for more.
//
// 4) Perfect guard (v1.2.0): an alchemy card's level is a 0-99 roll and
//    99 is both its max level and the "Perfect" quality grade. Once you
//    own a Perfect copy of a recipe's output there is no better roll
//    left, so both Synthesize and MAX refuse to run that recipe instead
//    of spending more biomass, stardust and ingredient cards on it.
//
// Compiled with the .NET Framework 4 csc against the game's own
// Assembly-CSharp.dll and the BepInEx 5 core DLLs (see build_plugin.ps1).

using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using LargeNumbers;
using UnityEngine;
using UnityEngine.UI;

[BepInPlugin("sti.community.alchemyqol", "STI Community Alchemy QoL", "1.3.0")]
public class AlchemyQoLPlugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;

    private void Awake()
    {
        // 0.1.1: off if the player set it to false in STI Community Patch.cfg
        if (!CommunityToggle.On("alchemy-qol", Logger))
        {
            enabled = false;
            return;
        }
        Log = Logger;
        Harmony harmony = new Harmony("sti.community.alchemyqol");
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        Logger.LogInfo("Alchemy QoL active: MAX synthesize + DE-LEVEL toggle + Perfect guard");
    }
}

[HarmonyPatch(typeof(CardPanel), "ReloadAlchemyControls")]
public static class MaxSynthesizeButtonPatch
{
    private static readonly FieldInfo AlchemyButtonField =
        AccessTools.Field(typeof(CardPanel), "alchemyButton");

    private static readonly FieldInfo CardDropdownField =
        AccessTools.Field(typeof(CardPanel), "alchemyCardDropdown");

    private static readonly FieldInfo RecipeDropdownField =
        AccessTools.Field(typeof(CardPanel), "alchemyRecipeDropdown");

    private static readonly FieldInfo ResultLabelField =
        AccessTools.Field(typeof(CardPanel), "alchemyResultLabel");

    private static readonly MethodInfo ReloadDropdownsMethod =
        AccessTools.Method(typeof(CardPanel), "ReloadAlchemyDropdowns");

    internal static bool deLevelEnabled;

    // The game's own hover tooltip. TooltipComposite falls back to its
    // public defaultTooltip whenever the object carries nothing implementing
    // ITooltipAvailable, and a cloned Synthesize button carries nothing, so
    // one component holding one string is the whole job.
    //
    // Fuzzied: "'De-Level' Should have a tool tip, same with 'MAX'". Both are
    // buttons this mod invented, so nothing in the game explains them.
    internal const string MAX_TIP =
        "Runs the same synthesis over and over until something runs out - "
        + "biomass, stardust, or the ingredient cards - and stops at 2000 in "
        + "one click. Every roll is the game's own, so this only saves you "
        + "the clicking. It also stops the moment the card comes out Perfect, "
        + "because no better roll exists after that.";

    internal const string DELEVEL_TIP =
        "When you are short of an ingredient, this splits higher-level copies "
        + "of that same card back down to make up the difference. Four cards "
        + "merge into one of the next level up, so splitting one back into "
        + "four gives up nothing you had. It takes the lowest level above the "
        + "one you need first, never touches a card that is protected or in a "
        + "loadout, and always leaves one copy at your highest level. Each "
        + "split costs biomass worth a tenth of the split card's sell value.";

    private static Button maxButton;
    private static Button deLevelButton;
    private static LangText deLevelLabel;

    public static void Postfix(CardPanel __instance, bool isOn)
    {
        if (!isOn)
        {
            return;
        }
        Button orig = (Button)AlchemyButtonField.GetValue(__instance);
        if (orig == null)
        {
            return;
        }
        CardPanel panel = __instance;
        if (maxButton == null)
        {
            maxButton = CloneButton(orig, "AlchemyMaxButton", 1, "MAX",
                MAX_TIP);
            maxButton.onClick.AddListener(delegate { SynthesizeMax(panel); });
        }
        if (deLevelButton == null)
        {
            deLevelButton = CloneButton(orig, "AlchemyDeLevelButton", 2,
                deLevelEnabled ? "DE-LEVEL: ON" : "DE-LEVEL: OFF",
                DELEVEL_TIP);
            deLevelLabel = deLevelButton.GetComponentInChildren<LangText>(true);
            deLevelButton.onClick.AddListener(delegate { ToggleDeLevel(); });
        }
    }

    // clones the vanilla Synthesize button and stacks the copy
    // slotsBelow button-heights underneath it
    private static Button CloneButton(Button orig, string name, int slotsBelow,
        string label, string tooltip)
    {
        GameObject clone = Object.Instantiate(orig.gameObject, orig.transform.parent);
        clone.name = name;
        RectTransform origRt = orig.GetComponent<RectTransform>();
        RectTransform rt = clone.GetComponent<RectTransform>();
        rt.anchorMin = origRt.anchorMin;
        rt.anchorMax = origRt.anchorMax;
        rt.pivot = origRt.pivot;
        rt.sizeDelta = origRt.sizeDelta;
        rt.anchoredPosition = origRt.anchoredPosition
            - new Vector2(0f, (origRt.rect.height + 6f) * slotsBelow);

        LangText lang = clone.GetComponentInChildren<LangText>(true);
        if (lang != null)
        {
            lang.SetLocalisedText(label);
        }

        TooltipComposite tip = clone.GetComponent<TooltipComposite>();
        if (tip == null)
        {
            tip = clone.AddComponent<TooltipComposite>();
        }
        tip.defaultTooltip = tooltip;

        Button button = clone.GetComponent<Button>();
        button.onClick = new Button.ButtonClickedEvent();
        return button;
    }

    private static void ToggleDeLevel()
    {
        deLevelEnabled = !deLevelEnabled;
        if (deLevelLabel != null)
        {
            deLevelLabel.SetLocalisedText(deLevelEnabled ? "DE-LEVEL: ON" : "DE-LEVEL: OFF");
        }
    }

    internal static CardAlchemyRecipe ResolveSelectedRecipe(CardPanel panel)
    {
        SearchDropdown cardDd = (SearchDropdown)CardDropdownField.GetValue(panel);
        SearchDropdown recipeDd = (SearchDropdown)RecipeDropdownField.GetValue(panel);
        if (cardDd == null || recipeDd == null)
        {
            return null;
        }
        Dictionary<string, CardAlchemyRecipe> byId;
        if (!CardLibrary.alchemyRecipeDict.TryGetValue(cardDd.stringValue, out byId))
        {
            return null;
        }
        CardAlchemyRecipe recipe;
        if (!byId.TryGetValue(recipeDd.stringValue, out recipe))
        {
            return null;
        }
        return recipe;
    }

    internal static void SetResult(CardPanel panel, string text)
    {
        LangText label = (LangText)ResultLabelField.GetValue(panel);
        if (label != null)
        {
            label.SetLocalisedText(text);
        }
    }

    // An alchemy roll tops out at the card's max level, which is also
    // its "Perfect" quality grade. Owning one means no better roll is
    // reachable, so there is nothing left to spend resources on.
    internal static bool AlreadyPerfect(CardAlchemyRecipe recipe)
    {
        Player player = Player.shared;
        if (recipe == null || player == null)
        {
            return false;
        }
        IdenticalCardSet set = DeLevelHelper.FindSet(player.cardBag, recipe.cardId);
        if (set == null)
        {
            return false;
        }
        Card sample = CardLibrary.GetCardWithLevel(recipe.cardId, 0);
        return sample != null && set.GetHighestLevel() >= sample.maxLevel;
    }

    // The recipe, with what you actually hold against what it asks for.
    //
    // "Hold" means what the game would let this recipe spend, which is not
    // the number on the card in your bag: HasAlchemyRecipeCards counts
    // through GetUnprotectedCount(CountInLoadout(...)), so a protected copy
    // or one sitting in a loadout reads as zero. Somebody looking at four
    // cards being told they have none deserves the reason, so where the two
    // numbers disagree the gap is named.
    internal static string MissingCardsMessage(CardAlchemyRecipe recipe)
    {
        Player player = Player.shared;
        if (recipe == null || player == null
            || recipe.fromCardIdLevelCountStrings == null)
        {
            return "{panels.cards.labels.alchemyNotEnoughCards}";
        }
        try
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("<b>{panels.cards.labels.alchemyNotEnoughCards}</b>\n\n");
            bool couldSplit = false;
            for (int i = 0; i < recipe.fromCardIdLevelCountStrings.Count; i++)
            {
                Card card;
                int need;
                CardAlchemyRecipe.ProcessCardIdLevelCountStr(
                    recipe.fromCardIdLevelCountStrings[i], out card, out need);
                if (card == null) { continue; }
                int have = DeLevelHelper.UsableCount(player, card.id,
                    card.level);
                int owned = DeLevelHelper.OwnedCount(player, card.id,
                    card.level);
                bool short_ = have < need;
                if (short_ && !deLevelEnabled
                    && DeLevelHelper.CouldSplitDownTo(player, card.id,
                        card.level))
                {
                    couldSplit = true;
                }

                sb.Append("<color=#");
                sb.Append(ColorUtility.ToHtmlStringRGB(
                    UIColorManager.GetCardRarityColor(card.rarity)));
                sb.Append(">");
                sb.Append(card.name);
                sb.Append("</color> ");
                sb.Append(card.levelString);
                sb.Append("  ");
                if (short_)
                {
                    sb.Append("<color=#ff6b6b><b>");
                    sb.Append(have);
                    sb.Append(" of ");
                    sb.Append(need);
                    sb.Append("</b>, need ");
                    sb.Append(need - have);
                    sb.Append(" more</color>");
                }
                else
                {
                    sb.Append(have);
                    sb.Append(" of ");
                    sb.Append(need);
                }
                if (owned > have)
                {
                    sb.Append("\n<size=80%>    (");
                    sb.Append(owned - have);
                    sb.Append(" more in the bag, but protected or in a "
                        + "loadout)</size>");
                }
                sb.Append("\n");
            }
            if (couldSplit)
            {
                sb.Append("\n<size=90%>You own higher levels of a card you "
                    + "are short of. DE-LEVEL would split those down for "
                    + "you.</size>");
            }
            return sb.ToString();
        }
        catch (System.Exception e)
        {
            AlchemyQoLPlugin.Log.LogWarning(
                "Alchemy QoL could not list the missing cards: " + e.Message);
            return "{panels.cards.labels.alchemyNotEnoughCards}";
        }
    }

    internal const string PERFECT_MESSAGE =
        "<b>{panels.cards.labels.alchemyQuality.perfect}</b>\n\n"
        + "You already own the best possible roll of this card, so"
        + " synthesis is off to save your resources.";

    private static void SynthesizeMax(CardPanel panel)
    {
        LangText resultLabel = (LangText)ResultLabelField.GetValue(panel);
        CardAlchemyRecipe recipe = ResolveSelectedRecipe(panel);
        if (recipe == null)
        {
            return;
        }
        Player player = Player.shared;

        int made = 0;
        Card best = null;
        string stopReason = "{panels.cards.labels.alchemyNotEnoughCards}";
        while (made < 2000)
        {
            if (AlreadyPerfect(recipe))
            {
                stopReason = PERFECT_MESSAGE;
                break;
            }
            if (player.storage.GetAmount(ElementType.biomass) < recipe.biomassCost)
            {
                stopReason = "{panels.cards.labels.alchemyNotEnoughBiomass}";
                break;
            }
            if (player.stardust < recipe.stardustCost)
            {
                stopReason = "{panels.cards.labels.alchemyNotEnoughStardust}";
                break;
            }
            if (!player.cardBag.HasAlchemyRecipeCards(recipe)
                && (!deLevelEnabled || !DeLevelHelper.EnsureRecipeCards(recipe)))
            {
                stopReason = MissingCardsMessage(recipe);
                break;
            }
            player.storage.TakeElementBatch(new ElementBatch(ElementType.biomass, recipe.biomassCost));
            player.stardust -= recipe.stardustCost;
            player.cardBag.TakeAlchemyRecipeCards(recipe);
            CardLibrary.UnlockAlchemyCard(recipe);
            Card card = recipe.GenerateCard();
            player.cardBag.AddCard(card);
            if (best == null || card.level > best.level)
            {
                best = card;
            }
            made++;
        }

        if (made == 0)
        {
            resultLabel.SetLocalisedText(stopReason);
        }
        else
        {
            resultLabel.SetLocalisedText(
                "<b>{panels.cards.labels.alchemyOutcome}:</b> x" + made
                + "\n\n" + best.name + " " + best.levelString);
        }
        ReloadDropdownsMethod.Invoke(panel, null);
    }
}

// single-click Synthesize: refuse the recipe outright once its output is
// Perfect, and otherwise de-level (when the toggle is on) before the
// vanilla method runs its own guards
[HarmonyPatch(typeof(CardPanel), "ConfirmCardAlchemy")]
public static class DeLevelOnSynthesizePatch
{
    public static bool Prefix(CardPanel __instance)
    {
        CardAlchemyRecipe recipe = MaxSynthesizeButtonPatch.ResolveSelectedRecipe(__instance);
        if (recipe == null || Player.shared == null)
        {
            return true;
        }
        if (MaxSynthesizeButtonPatch.AlreadyPerfect(recipe))
        {
            MaxSynthesizeButtonPatch.SetResult(__instance,
                MaxSynthesizeButtonPatch.PERFECT_MESSAGE);
            return false;
        }
        if (MaxSynthesizeButtonPatch.deLevelEnabled
            && !Player.shared.cardBag.HasAlchemyRecipeCards(recipe))
        {
            DeLevelHelper.EnsureRecipeCards(recipe);
        }
        // Only once the two resource costs are covered, so a recipe that is
        // short of biomass AND of cards still reports the biomass first, the
        // way the base game orders its own three checks. Taking the call over
        // rather than letting it through is what stops ConfirmCardAlchemy
        // overwriting the detail with its one-line version.
        if (!Player.shared.cardBag.HasAlchemyRecipeCards(recipe)
            && Player.shared.storage.GetAmount(ElementType.biomass)
                >= recipe.biomassCost
            && Player.shared.stardust >= recipe.stardustCost)
        {
            MaxSynthesizeButtonPatch.SetResult(__instance,
                MaxSynthesizeButtonPatch.MissingCardsMessage(recipe));
            return false;
        }
        return true;
    }
}

public static class DeLevelHelper
{
    // biomass fee per split = split card's sell value / FEE_DIVISOR
    private static readonly ScientificNotation FEE_DIVISOR = new ScientificNotation(1.0, 1);

    // Makes the bag satisfy every card requirement of the recipe by
    // splitting higher-level copies down (1 of level N -> 4 of level
    // N-1, the exact inverse of merging). Returns false without
    // partial-refunding if any requirement can't be met - already-done
    // splits are harmless since they preserve total card value.
    // a card id lives in exactly one of the two bags
    // What this recipe may actually spend, by the game's own test.
    internal static int UsableCount(Player player, string cardId, int level)
    {
        IdenticalCardSet set = FindSet(player.cardBag, cardId);
        if (set == null) { return 0; }
        return AvailableAt(player, set, level);
    }

    // What is in the bag, spendable or not. The difference between this and
    // UsableCount is protected copies and loadout copies.
    internal static int OwnedCount(Player player, string cardId, int level)
    {
        IdenticalCardSet set = FindSet(player.cardBag, cardId);
        if (set == null || !set.levelDict.ContainsKey(level)) { return 0; }
        return set.levelDict[level].count;
    }

    // Whether DE-LEVEL would have anything to work with here. Only asked
    // when the toggle is off, to decide whether mentioning it is useful.
    internal static bool CouldSplitDownTo(Player player, string cardId,
        int level)
    {
        IdenticalCardSet set = FindSet(player.cardBag, cardId);
        if (set == null) { return false; }
        foreach (KeyValuePair<int, CardSetLevel> pair in set.levelDict)
        {
            if (pair.Key > level && pair.Value.count > 0) { return true; }
        }
        return false;
    }

    internal static IdenticalCardSet FindSet(CardBag bag, string cardId)
    {
        if (bag.battleCardsDict.ContainsKey(cardId))
        {
            return bag.battleCardsDict[cardId];
        }
        if (bag.permanentCardsDict.ContainsKey(cardId))
        {
            return bag.permanentCardsDict[cardId];
        }
        return null;
    }

    public static bool EnsureRecipeCards(CardAlchemyRecipe recipe)
    {
        Player player = Player.shared;
        CardBag bag = player.cardBag;
        foreach (string req in recipe.fromCardIdLevelCountStrings)
        {
            Card reqCard;
            int reqCount;
            CardAlchemyRecipe.ProcessCardIdLevelCountStr(req, out reqCard, out reqCount);
            IdenticalCardSet set = FindSet(bag, reqCard.id);
            if (set == null)
            {
                return false;
            }
            if (!EnsureLevelCount(player, set, reqCard.level, reqCount))
            {
                return false;
            }
        }
        return true;
    }

    private static int AvailableAt(Player player, IdenticalCardSet set, int level)
    {
        if (!set.levelDict.ContainsKey(level))
        {
            return 0;
        }
        return set.levelDict[level].GetUnprotectedCount(
            player.cardBag.CountInLoadout(set.cardId, level));
    }

    private static bool EnsureLevelCount(Player player, IdenticalCardSet set, int reqLevel, int reqCount)
    {
        int safety = 0;
        while (AvailableAt(player, set, reqLevel) < reqCount)
        {
            safety++;
            if (safety > 10000)
            {
                return false;
            }
            // donor: the lowest owned level above the needed one that
            // still has a spare copy (unprotected, not in a loadout,
            // and never the last copy of the set's highest level)
            int donorLevel = -1;
            int highest = set.GetHighestLevel();
            foreach (KeyValuePair<int, CardSetLevel> kv in set.levelDict)
            {
                int lv = kv.Key;
                if (lv <= reqLevel)
                {
                    continue;
                }
                int spare = kv.Value.GetUnprotectedCount(
                    player.cardBag.CountInLoadout(set.cardId, lv));
                if (lv == highest && spare > kv.Value.count - 1)
                {
                    spare = kv.Value.count - 1;
                }
                if (spare >= 1)
                {
                    donorLevel = lv;
                    break;
                }
            }
            if (donorLevel < 0)
            {
                return false;
            }
            Card donorCard = CardLibrary.GetCardWithLevel(set.cardId, donorLevel);
            ScientificNotation fee = donorCard.GetBiomassAmount() / FEE_DIVISOR;
            if (player.storage.GetAmount(ElementType.biomass) < fee)
            {
                return false;
            }
            player.storage.TakeElementBatch(new ElementBatch(ElementType.biomass, fee));
            set.levelDict[donorLevel].count = set.levelDict[donorLevel].count - 1;
            if (set.levelDict[donorLevel].count == 0)
            {
                set.levelDict.Remove(donorLevel);
            }
            set.AddCard(donorLevel - 1, 4);
            AlchemyQoLPlugin.Log.LogInfo("De-level: split 1x " + set.cardId
                + " L" + donorLevel + " into 4x L" + (donorLevel - 1)
                + " (fee " + fee.ToString() + " biomass)");
        }
        return true;
    }
}
