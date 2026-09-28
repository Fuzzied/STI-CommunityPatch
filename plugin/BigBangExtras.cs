// Space Travel Idle community mod - Big Bang Plus gating + Star Essence catch-up
//
// Two things Fuzzied asked for on 2026-09-09, both about the Big Bang tree:
//
// 1. "Big Bang Plus should not be obtainable until the players get to Neptune,
//    have it say this somewhere in the Big Bang Upgrade menu."
//    ...revised the same day: "I meant Uranus, Neptune is too harsh." So the
//    gate star is URANUS (index 8). Uranus is the last planet the mod itself
//    adds anything to, and the first one past Saturn, which is where the game's
//    combat content stops - so it is still well past the point where the
//    extended tree could trivialise anything, without charging a 22-hour trip
//    to a planet that has nothing on it.
//
//    The bigbang-plus DATA patch extends all 22 vanilla lines from 10 tiers to
//    15 and adds 5 brand new lines. Data alone cannot gate that: a tier is just
//    another entry in the line's list, and the game has no unlock requirement on
//    Big Bang upgrades at all. So the gate lives here, at the one place a tier
//    is ever bought - BigBangUpgradeItem.UpgradeButtonClicked - and the reason
//    is written onto the upgrade button itself by a postfix on UpdateUI, which
//    is the game's own "redraw this card" method.
//
//    What counts as "Big Bang Plus" is any tier above what vanilla shipped:
//    tier 11+ on the 22 original lines, and tier 1+ on the 5 new ones. Vanilla
//    lines are all exactly 10 tiers long, so that is one number, not a table.
//
//    Tiers you already own keep working - the gate only refuses new purchases.
//    That matters because a Big Bang RESETS travel progress
//    (TravelProgress.BigBangClean sets farthestStarIdx = 0) while Big Bang
//    upgrade levels survive it. Locking a player out of a tree they had already
//    paid for would be a punishment, not a gate, so reaching the gate star is
//    remembered permanently once it happens - once EVER, not once per run
//    (Fuzzied: "It has to be a one time toggle, not per reset").
//    RequireGateStarEveryRun turns that off for anyone who wants the trip to be
//    the price every single run.
//
//    "Once, ever" needs a record that outlives a Big Bang, and farthestStarIdx
//    is not one. Two are used together:
//
//      - PlayerPrefs, written the first time this plugin ever sees
//        farthestStarIdx >= GATE_STAR_IDX on this machine.
//      - The game's own travel achievement. MissionManager is NOT in
//        BigBangManager.BigBangClean's list, so achievementProgressDict
//        survives a Big Bang, and achievement list "pr1" is the travel ladder:
//        its Nth mission has completionReqs.travelProgressIdx N-1, and progress
//        is a claimed-count, so >= 9 means Uranus was reached and collected at
//        some point in this save's history.
//
//    The second one is what rescues a player who reached the gate star and Big
//    Banged BEFORE installing this patch - PlayerPrefs would know nothing
//    about them. It can only undercount (an unclaimed achievement), never
//    overcount, so it is safe to trust in the player's favour.
//
// 2. "Star Essence Big Bang Upgrade needs to be retroactively applied along
//    with any future gains."
//
//    Player.AddStarEssence multiplies by Modifiers.starEssenceMul at the moment
//    the essence is earned, so the star_essence_gain_up line only ever affects
//    what you earn AFTER buying it. Star Essence is mostly paid out by
//    achievements, which you only collect once, so buying the multiplier late
//    does almost nothing. This scales the balance you are already holding by the
//    same factor whenever the multiplier changes, in both directions, so the
//    number in the corner always reads as "what this would be worth at today's
//    rate". The last multiplier applied is remembered in PlayerPrefs, so it is
//    applied exactly once per change and never compounds.
//
//    HONEST WARNING, unchanged by this patch: Star Essence is a DEAD currency in
//    the community build. Its shop needs a server (ShopPanel.FetchAndLoadProducts
//    -> NetworkManager.serverAPI.GetProducts) and ShopManager.AttemptPurchaseItem
//    / UseItemTakeEffect are stubbed empty. This makes the number correct; it
//    does not make it spendable.
//
// 3. v1.2.0, Fuzzied on 2026-09-15: "I don't think neptune mission was
//    registered, I think I reached that on the last reset."
//
//    He is right, and the save says exactly how it happened.
//    achievementProgressDict["pr1"] is 9, so he claimed the ladder up to
//    pr1_9, Uranus. gameProgress.farthestStarIdx is 5, Mars. The Neptune
//    mission is pr1_10, completionReqs.travelProgressIdx 9, and
//    UnlockManager.SatisfiesUnlockReq answers it with
//    player.travelProgress.Passed(9), which is farthestStarIdx >= 9. He flew
//    out to Neptune, did not open the achievements panel and press Claim,
//    and Big Banged. BigBangClean set farthestStarIdx to 0 and the claim
//    button went with it.
//
//    This is the vanilla game's own behaviour and no mod plugin was touching
//    it. It is still wrong: every other thing a Big Bang resets is something
//    you go and earn again on purpose, and "fly 22 hours to Neptune a second
//    time to collect a reward you already earned" is not that. Worse, the
//    reward is 5 Star Essence (bundleType 103), which the community build
//    cannot spend at all, so the trip buys nothing but the tick.
//
//    So the farthest star ever reached is latched in PlayerPrefs the same way
//    the Uranus gate above is, seeded from the same pr1 claimed-count rescue
//    so a player who has never run this patch still starts with everything
//    their claims already prove.
//
//    Only the achievements panel is allowed to see that number. The patch is
//    a Prefix on AchievementItem.UpdateProgressUI, the game's own per-frame
//    redraw of one achievement row, which raises farthestStarIdx for the
//    length of that one call and a Finalizer puts it back. A Finalizer rather
//    than a Postfix because a Finalizer still runs if the original throws,
//    and leaving the number raised is the one outcome that must not happen.
//    Nothing else in the game ever sees it: travel gating, research and infra
//    unlocks, GetMaxReachableStarIdx and the star map all read the real
//    field, so a reset still locks everything a reset is supposed to lock.
//
//    pr1 is the only achievement list in the game with a travelProgressIdx
//    requirement, so this touches the travel ladder and nothing else.
//
//    What it cannot do is prove a star nobody ever claimed. pr1 = 9 proves
//    Uranus, not Neptune, so this stops the next one being lost but does not
//    bring back one already gone. FarthestStarEverAtLeast in the config is
//    there for that: the player states what they reached, once.
//
// 4. v1.3.0, Fuzzied on 2026-09-20: "Offline Vault should not be available
//    until Offline Reserve has been maxed."
//
//    A second kind of lock, and a much simpler one: a line can name another
//    line it waits for. Only offline_vault does, and it waits for
//    offline_reserve. The game has no unlock requirement on Big Bang
//    upgrades at all, so this rides the same three patches the Uranus gate
//    uses - the click is swallowed, the button says so, the tooltip says
//    why - with different words, because sending a player to Uranus over a
//    line that is waiting for six cheap tiers would be a wild goose chase.
//
//    Levels already owned are kept, same as the gate. And if the data patch
//    that adds offline_reserve is not installed, nothing locks: a
//    requirement that can never be met is worse than no requirement.
//
// 5. v1.3.1, Fuzzied on 2026-09-25: taking Offline Reserve below max in
//    upgrade mode (Reset or Downgrade) now resets Offline Vault with it and
//    refunds its dark matter. See VaultFollowsReservePatch.
//
// 6. v1.3.2, Fuzzied on 2026-09-25: Offline Vault's button now switches between
//    Locked and its price the moment Offline Reserve is maxed or taken back
//    down, instead of only when the panel is rebuilt. See
//    VaultFollowsReservePatch.Redraw.
//
// 7. v1.3.3, Fuzzied on 2026-09-27: "can we add in the installer that if they
//    dont want the offline change specifically we remove that for them?"
//    and, asked what the two offline lines should do then: lock them.
//
//    The installer now has a tick box for the offline limit, and unticking
//    it writes CapOfflineTime = false into OfflineFix's config. With no
//    limit there is nothing for Offline Reserve or Offline Vault to raise,
//    so buying a tier would be dark matter for nothing. Both lines lock
//    with the button saying Off and the tooltip saying why. Same rules as
//    the other two locks: levels already owned are kept, and taking them
//    back down for a refund still works, because only the Upgrade click is
//    refused. OfflineFix not being installed at all counts as off too,
//    since then nothing reads those levels.
//
//    The setting is read from OfflineFix's own config entry at the moment
//    it is needed, not copied, so the two plugins cannot disagree about it.
//
// 8. v1.3.4, Fuzzied on 2026-09-27: "yes, fix the labels". The Offline Reserve
//    and Offline Vault cards printed their bonus as "Automation Level 3",
//    because both lines carry BonusType.none and setup_mod.py renames that
//    one shared key for Automated Arrival. OfflineCardTextPatch writes
//    "Offline Time" and the days or weeks instead, the same way Cargo Bay
//    and Planetary Memory relabel their own none lines.
//
// Compiled with the .NET Framework 4 csc against the game's own
// Assembly-CSharp.dll and the BepInEx 5 core DLLs (see build_plugin.ps1).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

// The two offline-time lines, by id. OfflineFix owns what they do; this
// plugin only needs to know that they are never behind the Uranus gate, and
// that they lock when OfflineFix's limit is switched off.
internal static class OfflineIds
{
    internal const string RESERVE = "offline_reserve";
    internal const string VAULT = "offline_vault";

    // OfflineFix's plugin id and the entry the installer writes.
    private const string OFFLINE_FIX_GUID = "sti.community.offlinefix";
    private static readonly ConfigDefinition CapEntry =
        new ConfigDefinition("Offline cap", "CapOfflineTime");

    private static ConfigEntryBase cap;

    internal static bool IsOfflineLine(string setId)
    {
        return setId == RESERVE || setId == VAULT;
    }

    // True when there is no offline limit for these two lines to raise:
    // OfflineFix is not loaded, or its CapOfflineTime is false.
    //
    // Looked up on first use rather than in Awake, because BepInEx does not
    // promise OfflineFix has loaded by the time this plugin wakes. The entry
    // is kept once found and its value read live every time.
    internal static bool CapSwitchedOff()
    {
        if (cap == null)
        {
            PluginInfo info;
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(OFFLINE_FIX_GUID, out info)
                || info == null || info.Instance == null)
            {
                return true;
            }
            ConfigFile config = info.Instance.Config;
            if (config == null || !config.ContainsKey(CapEntry))
            {
                // OfflineFix is there but has no such setting: an older
                // build from before the limit existed, so there is no limit.
                return true;
            }
            cap = config[CapEntry];
        }
        return !(cap.BoxedValue is bool) || !(bool)cap.BoxedValue;
    }
}

[BepInPlugin("sti.community.bigbangextras", "STI Community Big Bang Extras", "1.3.4")]
public class BigBangExtrasPlugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;

    // The star that opens the extended tree: URANUS. Order is earth 0, moon 1,
    // venus 2, mercury 3, sun 4, mars 5, jupiter 6, saturn 7, uranus 8,
    // neptune 9, pluto 10.
    internal const int GATE_STAR_IDX = 8;

    // Every vanilla Big Bang line ships exactly 10 tiers; anything above that
    // came from the bigbang-plus data patch.
    internal const int VANILLA_MAX_TIER = 10;

    // The five lines bigbang-plus adds from nothing, so tier 1 is already a
    // Plus tier on them.
    private static readonly HashSet<string> NewLines = new HashSet<string>
    {
        "deterioration_floor_up",
        "deterioration_slowdown",
        "path_knowledge_up",
        "spaceship_dieting",
        "star_essence_gain_up"
        // auto_start_ir is deliberately NOT here. It is the sixth new line but
        // it buys quality of life rather than power, and gating it behind
        // Uranus would keep it from exactly the players it helps most - the
        // ones still flying the long early trips. With it left out it falls
        // through to the "level > 10" rule below, and its three tiers can
        // never reach that, so it is never locked.
    };

    // Lines that are never gated at any tier, however many tiers they have.
    //
    // Leaving a line out of NewLines is only half an exemption: anything past
    // tier 10 still falls through to the rule below. auto_start_ir gets away
    // with it because it only has three tiers. offline_vault has fifteen, so
    // tiers 11 to 15 would have locked behind Uranus, and those two lines are
    // for the player who cannot log in every day. That player needs them on
    // their first run, not their fifth.
    private static readonly HashSet<string> Ungated = new HashSet<string>
    {
        OfflineIds.RESERVE,
        OfflineIds.VAULT
    };

    // Lines that want another line finished first. Fuzzied: "Offline Vault
    // should not be available until Offline Reserve has been maxed."
    //
    // The two lines are one ladder sold as two cards. Reserve banks days and
    // is cheap, Vault banks weeks on top of it and is the expensive half, and
    // the card text for Vault already says "on top of Offline Reserve". A
    // player who buys weeks before they own the days has paid for the big
    // number and skipped the small one, which is the one purchase order in
    // the tree that cannot be what anybody meant.
    //
    // This is a different lock from the Uranus gate above, which is why the
    // label and the tooltip both ask which one is in force. It is also why
    // these two stay in Ungated: the ladder is the requirement here, not the
    // trip.
    private static readonly Dictionary<string, string> Prerequisite =
        new Dictionary<string, string>
        {
            { OfflineIds.VAULT, OfflineIds.RESERVE }
        };

    // The game's travel achievement list, and the number of claimed missions
    // in it that means "arrived at the gate star" - pr1_9 carries
    // completionReqs.travelProgressIdx 8, and progress counts claims, so the
    // count is the star index plus one.
    private const string TRAVEL_ACHIEVEMENT = "pr1";
    private const int GATE_ACHIEVEMENT_COUNT = GATE_STAR_IDX + 1;

    private const string PREF_REACHED = "communityGateStarReached";
    private const string PREF_SE_MUL = "communityStarEssenceMul";
    private const string PREF_FARTHEST = "communityFarthestStarEver";

    internal static ConfigEntry<bool> cfgGate;
    internal static ConfigEntry<bool> cfgEveryRun;
    internal static ConfigEntry<bool> cfgRetroEssence;
    internal static ConfigEntry<string> cfgLockedLabel;
    internal static ConfigEntry<bool> cfgPrereq;
    internal static ConfigEntry<string> cfgPrereqLabel;
    internal static ConfigEntry<string> cfgOfflineOffLabel;
    internal static ConfigEntry<bool> cfgTravelMemory;
    internal static ConfigEntry<int> cfgFarthestFloor;

    private void Awake()
    {
        Log = Logger;

        cfgGate = Config.Bind("1 Big Bang Plus", "RequireUranus", true,
            "Upgrade tiers beyond the ones the base game shipped can only be "
            + "bought once you have reached Uranus.");
        cfgEveryRun = Config.Bind("1 Big Bang Plus", "RequireGateStarEveryRun", false,
            "Leave this false. Reaching Uranus ONCE unlocks the extended tiers "
            + "permanently, which is the intended behaviour. Set it to true only "
            + "if you want a Big Bang to lock them again until you have "
            + "travelled back out to Uranus.");
        cfgLockedLabel = Config.Bind("1 Big Bang Plus", "LockedButtonText", "Uranus",
            "What the Upgrade button says while the extended tiers are locked.");
        cfgPrereq = Config.Bind("1 Big Bang Plus", "RequireOfflineReserveFirst", true,
            "Offline Vault cannot be bought until Offline Reserve is at max "
            + "level. The two are one ladder and Vault is the expensive half, "
            + "so buying it first only wastes dark matter.");
        cfgPrereqLabel = Config.Bind("1 Big Bang Plus", "PrereqButtonText", "Locked",
            "What the Upgrade button says while a line is waiting on the line "
            + "below it.");
        cfgOfflineOffLabel = Config.Bind("1 Big Bang Plus", "OfflineOffButtonText", "Off",
            "What the Upgrade button on Offline Reserve and Offline Vault says "
            + "while the offline limit is switched off. With no limit there is "
            + "nothing for them to raise, so they cannot be bought. The limit is "
            + "CapOfflineTime in sti.community.offlinefix.cfg.");

        cfgTravelMemory = Config.Bind("3 Travel achievements", "RememberFarthestStar", true,
            "The travel achievements go by the furthest star you have ever "
            + "reached, instead of only the furthest since your last Big Bang. "
            + "Without this, flying out to a planet and resetting before you "
            + "press Claim loses that reward until you fly all the way out "
            + "again. Nothing else in the game sees the remembered number: "
            + "research, buildings, the travel map and the Big Bang gate all "
            + "still go by this run.");
        cfgFarthestFloor = Config.Bind("3 Travel achievements", "FarthestStarEverAtLeast", -1,
            "Only needed if you reached a planet and Big Banged BEFORE "
            + "installing this, because nothing is left in the save that "
            + "proves it. Set it to the star you know you got to and the claim "
            + "buttons up to there come back. earth 0, moon 1, venus 2, "
            + "mercury 3, sun 4, mars 5, jupiter 6, saturn 7, uranus 8, "
            + "neptune 9, pluto 10. Leave it at -1 otherwise.");

        cfgRetroEssence = Config.Bind("2 Star Essence", "ApplyRetroactively", true,
            "When your Star Essence multiplier changes, scale the Star Essence "
            + "you already hold by the same amount, so the bonus counts for "
            + "essence you earned before you bought it.");

        Harmony harmony = new Harmony("sti.community.bigbangextras");
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        Logger.LogInfo("Big Bang Extras active: Uranus gate on extended tiers, "
            + "Offline Vault waits for a maxed Offline Reserve ("
            + (cfgPrereq.Value ? "on" : "off")
            + "), both offline lines lock while the offline limit is off, retroactive Star Essence, travel achievements remember the "
            + "furthest star ever reached");
    }

    // Both travel facts are recorded the moment they happen, not asked about
    // later, because a Big Bang wipes farthestStarIdx back to 0.
    private void Update()
    {
        try
        {
            LatchGateStar();
            LatchFarthestStar();
        }
        catch (Exception)
        {
            // never spam: the checks are cheap and will simply run again
        }
    }

    private static void LatchGateStar()
    {
        if (PlayerPrefs.GetInt(PREF_REACHED, 0) == 1)
        {
            return;
        }
        if (AtGateStarNow() || GateStarAchievementClaimed())
        {
            PlayerPrefs.SetInt(PREF_REACHED, 1);
            PlayerPrefs.Save();
            Log.LogInfo("Uranus reached - extended Big Bang tiers unlocked "
                + "permanently");
        }
    }

    // ------------------------------------------- farthest star ever reached

    // Read once and kept here, so Update is not asking PlayerPrefs every frame
    // for a number that changes about ten times in a playthrough.
    private static bool farthestLoaded;
    private static int farthestEver;

    private static int StoredFarthest()
    {
        if (!farthestLoaded)
        {
            farthestLoaded = true;
            farthestEver = PlayerPrefs.GetInt(PREF_FARTHEST, 0);
        }
        return farthestEver;
    }

    // What the achievements panel is allowed to treat as reached. The config
    // floor is applied here rather than latched into PlayerPrefs, so a player
    // who sets it and changes their mind can simply take it out again.
    internal static int FarthestEver()
    {
        int best = StoredFarthest();
        if (cfgFarthestFloor != null && cfgFarthestFloor.Value > best)
        {
            best = cfgFarthestFloor.Value;
        }
        return best;
    }

    // pr1's Nth mission carries completionReqs.travelProgressIdx N-1 and
    // progress counts claims, so a progress of N proves star N-1 was reached
    // at some point in this save's history. Same rescue as the gate star: it
    // can only undercount, never overcount.
    private static int ClaimedFarthest()
    {
        MissionManager missions = MissionManager.shared;
        if (missions == null || missions.achievementProgressDict == null)
        {
            return -1; // save not loaded yet
        }
        return missions.GetAchievementProgress(TRAVEL_ACHIEVEMENT) - 1;
    }

    private static void LatchFarthestStar()
    {
        int stored = StoredFarthest();
        int high = stored;

        Player player = Player.shared;
        if (player != null && player.travelProgress != null
            && player.travelProgress.farthestStarIdx > high)
        {
            high = player.travelProgress.farthestStarIdx;
        }
        int claimed = ClaimedFarthest();
        if (claimed > high)
        {
            high = claimed;
        }

        if (high <= stored)
        {
            return;
        }
        farthestEver = high;
        PlayerPrefs.SetInt(PREF_FARTHEST, high);
        PlayerPrefs.Save();
        Log.LogInfo("furthest star ever reached is now " + high + ", was "
            + stored + " - travel achievements will keep it through a Big Bang");
    }

    // Where the player is on THIS run.
    private static bool AtGateStarNow()
    {
        Player player = Player.shared;
        return player != null && player.travelProgress != null
            && player.travelProgress.farthestStarIdx >= GATE_STAR_IDX;
    }

    // Whether this save's history says Uranus was reached and collected,
    // which a Big Bang does not erase.
    private static bool GateStarAchievementClaimed()
    {
        MissionManager missions = MissionManager.shared;
        if (missions == null || missions.achievementProgressDict == null)
        {
            return false; // save not loaded yet
        }
        return missions.GetAchievementProgress(TRAVEL_ACHIEVEMENT)
            >= GATE_ACHIEVEMENT_COUNT;
    }

    // True when this upgrade line, at this level, is asking for a tier that only
    // exists because of the bigbang-plus data patch.
    internal static bool IsPlusTier(string setId, int level)
    {
        if (setId != null && Ungated.Contains(setId))
        {
            return false;
        }
        if (setId != null && NewLines.Contains(setId))
        {
            return level >= 1;
        }
        return level > VANILLA_MAX_TIER;
    }

    internal static bool GateStarReached()
    {
        if (cfgEveryRun != null && cfgEveryRun.Value)
        {
            return AtGateStarNow(); // opt-in: this run only
        }
        // Default: once, ever. Update() latches the flag, but check the live
        // signals too so the very first frames after a save loads are right.
        return PlayerPrefs.GetInt(PREF_REACHED, 0) == 1
            || AtGateStarNow() || GateStarAchievementClaimed();
    }

    // The line this one is waiting on, or null when it is free to buy.
    //
    // Returns null rather than locking when the required line is missing from
    // the loaded upgrade set, because that means the data patch that adds it
    // is not installed and there is nothing there to max out. A lock nobody
    // can ever open is worse than no lock.
    internal static string UnmetPrereq(string setId)
    {
        if (cfgPrereq == null || !cfgPrereq.Value || setId == null)
        {
            return null;
        }
        string required;
        if (!Prerequisite.TryGetValue(setId, out required))
        {
            return null;
        }
        BigBangUpgradeSet set;
        if (BigBangLibrary.upgrades == null
            || !BigBangLibrary.upgrades.TryGetValue(required, out set)
            || set == null || set.upgrades == null || set.upgrades.Count == 0)
        {
            return null;
        }
        BigBangManager manager = BigBangManager.shared;
        if (manager == null || manager.upgradeLevels == null)
        {
            return null;
        }
        // GetUpgradeLevel sees clicks made this session, because the card
        // writes every purchase straight back through SetUpgradeLevel. So
        // maxing Reserve opens Vault immediately, with no confirm step in
        // between.
        return (manager.GetUpgradeLevel(required) >= set.upgrades.Count)
            ? null : required;
    }

    /// <summary>The lines that wait on <paramref name="required"/>, e.g. offline_vault for offline_reserve.</summary>
    internal static List<string> LinesWaitingOn(string required)
    {
        List<string> result = new List<string>();
        foreach (KeyValuePair<string, string> pair in Prerequisite)
        {
            if (pair.Value == required)
            {
                result.Add(pair.Key);
            }
        }
        return result;
    }

    /// <summary>The name a line shows on its card, or its id as a fallback.</summary>
    internal static string LineName(string setId)
    {
        try
        {
            BigBangUpgradeSet set;
            if (BigBangLibrary.upgrades != null
                && BigBangLibrary.upgrades.TryGetValue(setId, out set)
                && set != null && !string.IsNullOrEmpty(set.name))
            {
                return set.name;
            }
        }
        catch (Exception)
        {
            // localisation is never worth an exception
        }
        return setId;
    }

    // One of the two offline lines, while there is no offline limit for it
    // to raise.
    internal static bool OfflineOff(string setId)
    {
        return OfflineIds.IsOfflineLine(setId) && OfflineIds.CapSwitchedOff();
    }

    // Locked = an offline line with the limit off, waiting on the line below
    // it, or the gate is on, the tier is a
    // Plus tier and Uranus has not been reached.
    internal static bool IsLocked(string setId, int level)
    {
        if (OfflineOff(setId))
        {
            return true;
        }
        if (UnmetPrereq(setId) != null)
        {
            return true;
        }
        if (cfgGate == null || !cfgGate.Value)
        {
            return false;
        }
        if (!IsPlusTier(setId, level))
        {
            return false;
        }
        return !GateStarReached();
    }

    // ------------------------------------------------------- star essence

    private static bool essenceReady;

    internal static void SyncStarEssence()
    {
        if (cfgRetroEssence == null || !cfgRetroEssence.Value)
        {
            return;
        }
        Player player = Player.shared;
        if (player == null)
        {
            return;
        }
        double now = Modifiers.starEssenceMul;
        if (double.IsNaN(now) || double.IsInfinity(now) || now <= 0.0)
        {
            return;
        }

        float storedRaw = PlayerPrefs.GetFloat(PREF_SE_MUL, 0f);
        double stored = storedRaw;
        if (!essenceReady || stored <= 0.0)
        {
            // First sight of this save: adopt the current rate as the baseline
            // rather than inventing a windfall out of an unknown history.
            essenceReady = true;
            PlayerPrefs.SetFloat(PREF_SE_MUL, (float)now);
            PlayerPrefs.Save();
            return;
        }

        double factor = now / stored;
        if (factor > 0.9999 && factor < 1.0001)
        {
            return; // nothing moved
        }

        int before = player.starEssence;
        double scaled = Math.Floor((double)before * factor);
        if (scaled < 0.0)
        {
            scaled = 0.0;
        }
        if (scaled > int.MaxValue)
        {
            scaled = int.MaxValue;
        }
        player.starEssence = (int)scaled;
        PlayerPrefs.SetFloat(PREF_SE_MUL, (float)now);
        PlayerPrefs.Save();
        Log.LogInfo("Star Essence multiplier " + stored.ToString("0.###") + " -> "
            + now.ToString("0.###") + ", balance " + before + " -> "
            + player.starEssence);
    }
}

// The travel achievements, and ONLY the travel achievements, read the
// remembered number instead of this run's.
//
// UpdateProgressUI is the game's own redraw of one achievement row, called
// from that row's FixedUpdate, and it is the only place both halves of the
// problem live: the bar (Mission.progressPercentage, which calls
// SatisfiesUnlockReq) and the claim button (IsMissionComplete ->
// StatsManager.SatisfiesAchievementMissionReq). Raising farthestStarIdx for
// the length of that one call keeps the two consistent and leaves every other
// reader of the field - travel gating, research and infra unlocks,
// GetMaxReachableStarIdx, the star map, the Big Bang gate above - looking at
// the real one.
//
// Finalizer rather than Postfix: a Finalizer runs even when the original
// method throws, and the field being left raised is the single outcome that
// would actually matter.
[HarmonyPatch(typeof(AchievementItem), "UpdateProgressUI")]
public static class TravelAchievementMemoryPatch
{
    private static bool raised;
    private static int realValue;
    private static bool loggedFailure;

    public static void Prefix()
    {
        raised = false;
        try
        {
            if (BigBangExtrasPlugin.cfgTravelMemory == null
                || !BigBangExtrasPlugin.cfgTravelMemory.Value)
            {
                return;
            }
            Player player = Player.shared;
            if (player == null || player.travelProgress == null)
            {
                return;
            }
            int remembered = BigBangExtrasPlugin.FarthestEver();
            if (remembered <= player.travelProgress.farthestStarIdx)
            {
                return; // this run has already gone at least as far
            }
            realValue = player.travelProgress.farthestStarIdx;
            player.travelProgress.farthestStarIdx = remembered;
            raised = true;
        }
        catch (Exception e)
        {
            raised = false;
            if (!loggedFailure)
            {
                loggedFailure = true;
                BigBangExtrasPlugin.Log.LogWarning("travel memory skipped: " + e);
            }
        }
    }

    public static void Finalizer()
    {
        if (!raised)
        {
            return;
        }
        raised = false;
        Player player = Player.shared;
        if (player != null && player.travelProgress != null)
        {
            player.travelProgress.farthestStarIdx = realValue;
        }
    }
}

// The one place a Big Bang tier is ever bought. Refusing here is exactly what
// the game itself does when you cannot afford a tier: the click does nothing.
[HarmonyPatch(typeof(BigBangUpgradeItem), "UpgradeButtonClicked")]
public static class BigBangUpgradeGatePatch
{
    private static readonly FieldInfo UpgradeSetField =
        AccessTools.Field(typeof(BigBangUpgradeItem), "upgradeSet");
    private static readonly FieldInfo CurrentLevelField =
        AccessTools.Field(typeof(BigBangUpgradeItem), "currentLevel");

    public static bool Prefix(BigBangUpgradeItem __instance)
    {
        try
        {
            BigBangUpgradeSet set = UpgradeSetField.GetValue(__instance) as BigBangUpgradeSet;
            int level = (int)CurrentLevelField.GetValue(__instance);
            if (set == null)
            {
                return true;
            }
            if (BigBangExtrasPlugin.IsLocked(set.id, level + 1))
            {
                return false; // swallow the click
            }
        }
        catch (Exception e)
        {
            BigBangExtrasPlugin.Log.LogWarning("gate check skipped: " + e.Message);
        }
        return true;
    }
}

// v1.3.1, Fuzzied on 2026-09-25 (test plan E10): "You can max OR, then add
// points in Offline Vault(OV), then reset OR and keep the points in OV. Fix:
// Resetting OR needs to Reset OV as well."
//
// The lock above only ever looked at the moment of purchase. In upgrade mode
// the game lets you take levels back off any line, one at a time (Downgrade)
// or all at once (Reset), and nothing asked the Vault about it. So the order
// max Reserve, buy Vault, reset Reserve got every Vault level with no Reserve
// under it, which is the one state the lock exists to prevent.
//
// SetUpgradeLevel is where both of those buttons end up (through the card's
// NotifyManager), and so does a purchase. So this runs after every change to
// a line, and when a line that others wait on stops being maxed, the waiting
// lines are reset as well, with their dark matter handed back. The refund goes
// through the Vault card's own ResetUpgrade, the same code its Reset button
// runs, so the price, the dark matter and the card on screen all agree.
//
// Only in upgrade mode, because that is the only time a level can be taken
// back. Vault levels on a save that owned them before the lock existed are
// not touched on load (E12): this reacts to Reserve going down, nothing else.
[HarmonyPatch(typeof(BigBangManager), "SetUpgradeLevel")]
public static class VaultFollowsReservePatch
{
    private static readonly FieldInfo UpgradeSetField =
        AccessTools.Field(typeof(BigBangUpgradeItem), "upgradeSet");

    private static bool busy;

    public static void Postfix(BigBangManager __instance, string upgradeId)
    {
        if (busy || __instance == null || !__instance.isInUpgradeMode)
        {
            return;
        }
        try
        {
            busy = true;
            foreach (string waiting in BigBangExtrasPlugin.LinesWaitingOn(upgradeId))
            {
                int owned = __instance.GetUpgradeLevel(waiting);
                if (owned > 0 && BigBangExtrasPlugin.UnmetPrereq(waiting) != null)
                {
                    ResetLine(__instance, waiting, owned, upgradeId);
                }
                Redraw(waiting);
            }
        }
        catch (Exception e)
        {
            BigBangExtrasPlugin.Log.LogWarning("vault follows reserve skipped: " + e);
        }
        finally
        {
            busy = false;
        }
    }

    // v1.3.2, Fuzzied on 2026-09-25 (E10 retest): "the "Locked" status on OV
    // doesnt change when OR is maxed". The label is written by the card's own
    // UpdateUI (BigBangUpgradeLabelPatch below), and a card only redraws when
    // it is clicked itself. So maxing Reserve unlocked Vault's clicks but left
    // its button saying Locked until the panel was rebuilt, and taking Reserve
    // back down left an empty Vault showing a price. UpdateUI only writes text
    // and the sprite, so calling it here costs nothing and changes no level.
    private static void Redraw(string lineId)
    {
        foreach (BigBangUpgradeItem item in Resources.FindObjectsOfTypeAll<BigBangUpgradeItem>())
        {
            BigBangUpgradeSet set = UpgradeSetField.GetValue(item) as BigBangUpgradeSet;
            if (set != null && set.id == lineId)
            {
                item.UpdateUI();
            }
        }
    }

    private static void ResetLine(BigBangManager manager, string lineId, int owned, string cause)
    {
        foreach (BigBangUpgradeItem item in Resources.FindObjectsOfTypeAll<BigBangUpgradeItem>())
        {
            BigBangUpgradeSet set = UpgradeSetField.GetValue(item) as BigBangUpgradeSet;
            if (set == null || set.id != lineId)
            {
                continue;
            }
            item.ResetUpgrade();
            BigBangExtrasPlugin.Log.LogInfo(BigBangExtrasPlugin.LineName(cause)
                + " is no longer maxed, so " + BigBangExtrasPlugin.LineName(lineId)
                + " was reset with it: level " + owned + " to 0, dark matter refunded");
            return;
        }

        // No card on screen to do it for us. Do what its Reset button does.
        BigBangUpgradeSet lineSet;
        if (!BigBangLibrary.upgrades.TryGetValue(lineId, out lineSet) || lineSet == null)
        {
            return;
        }
        LargeNumbers.ScientificNotation cost = BigBangUpgrade.GetCost(lineSet.GetUpgrade(owned));
        Player.shared.AddDarkMatter(cost);
        manager.currentSpentDarkMatter -= cost;
        manager.SetUpgradeLevel(lineId, 0);
        BigBangExtrasPlugin.Log.LogInfo(BigBangExtrasPlugin.LineName(cause)
            + " is no longer maxed, so " + BigBangExtrasPlugin.LineName(lineId)
            + " was reset with it: level " + owned + " to 0, " + cost
            + " dark matter refunded (no card on screen)");
    }
}

// UpdateUI is the game's own redraw for one upgrade card, and it runs on every
// click, so writing the locked message here keeps it correct without a second
// update loop of our own.
[HarmonyPatch(typeof(BigBangUpgradeItem), "UpdateUI")]
public static class BigBangUpgradeLabelPatch
{
    private static readonly FieldInfo UpgradeSetField =
        AccessTools.Field(typeof(BigBangUpgradeItem), "upgradeSet");
    private static readonly FieldInfo CurrentLevelField =
        AccessTools.Field(typeof(BigBangUpgradeItem), "currentLevel");
    private static readonly FieldInfo UpgradeButtonTextField =
        AccessTools.Field(typeof(BigBangUpgradeItem), "upgradeButtonText");

    private static bool loggedFailure;

    public static void Postfix(BigBangUpgradeItem __instance)
    {
        try
        {
            BigBangUpgradeSet set = UpgradeSetField.GetValue(__instance) as BigBangUpgradeSet;
            int level = (int)CurrentLevelField.GetValue(__instance);
            if (set == null || !BigBangExtrasPlugin.IsLocked(set.id, level + 1))
            {
                return;
            }
            LangText label = UpgradeButtonTextField.GetValue(__instance) as LangText;
            if (label != null)
            {
                // Two different locks, two different words, because "Uranus"
                // on a line that is waiting for Offline Reserve would send
                // the player on a trip that changes nothing.
                // Off comes first: with no limit, waiting on Offline Reserve
                // is beside the point.
                bool off = BigBangExtrasPlugin.OfflineOff(set.id);
                bool waiting = BigBangExtrasPlugin.UnmetPrereq(set.id) != null;
                // SetText runs Regex.Unescape, so the text must carry no
                // backslashes. None of these does.
                label.SetText(off
                    ? BigBangExtrasPlugin.cfgOfflineOffLabel.Value
                    : waiting
                    ? BigBangExtrasPlugin.cfgPrereqLabel.Value
                    : BigBangExtrasPlugin.cfgLockedLabel.Value);
            }
        }
        catch (Exception e)
        {
            if (!loggedFailure)
            {
                loggedFailure = true;
                BigBangExtrasPlugin.Log.LogWarning("locked label skipped: " + e);
            }
        }
    }
}

// The tooltip is where there is room to say WHY, and it is already the place
// the game explains what the next tier does.
[HarmonyPatch(typeof(BigBangUpgradeItem), "GetDetailTooltip")]
public static class BigBangTooltipPatch
{
    private static readonly FieldInfo UpgradeSetField =
        AccessTools.Field(typeof(BigBangUpgradeItem), "upgradeSet");
    private static readonly FieldInfo CurrentLevelField =
        AccessTools.Field(typeof(BigBangUpgradeItem), "currentLevel");

    public static void Postfix(BigBangUpgradeItem __instance, ref string __result)
    {
        try
        {
            BigBangUpgradeSet set = UpgradeSetField.GetValue(__instance) as BigBangUpgradeSet;
            int level = (int)CurrentLevelField.GetValue(__instance);
            if (set == null || !BigBangExtrasPlugin.IsLocked(set.id, level + 1))
            {
                return;
            }
            string waitingOn = BigBangExtrasPlugin.UnmetPrereq(set.id);
            string note = BigBangExtrasPlugin.OfflineOff(set.id)
                ? ("Locked: the offline limit is switched off, so there is no "
                    + "limit for this to raise and buying it would do nothing. "
                    + "Levels you already own are kept, and you can still take "
                    + "them back down for a refund.")
                : (waitingOn != null)
                ? ("Locked: " + BigBangExtrasPlugin.LineName(set.id)
                    + " opens up once "
                    + BigBangExtrasPlugin.LineName(waitingOn)
                    + " is at max level. They are one ladder, and this is the "
                    + "expensive half.")
                : ("Locked: the community mod's extra Big Bang upgrades "
                    + "open up once you have reached Uranus.");
            __result = string.IsNullOrEmpty(__result) ? note : (__result + "\n\n" + note);
        }
        catch (Exception)
        {
            // a tooltip is never worth an exception
        }
    }
}

// ConfirmUpgrades is the game's "the Big Bang bonuses are now these" moment: it
// runs on save load (through LoadUpgradeLevels) and again whenever upgrades are
// confirmed, which is exactly when the Star Essence multiplier can have moved.
[HarmonyPatch(typeof(BigBangManager), "ConfirmUpgrades")]
public static class StarEssenceSyncPatch
{
    private static bool loggedFailure;

    public static void Postfix()
    {
        try
        {
            BigBangExtrasPlugin.SyncStarEssence();
        }
        catch (Exception e)
        {
            if (!loggedFailure)
            {
                loggedFailure = true;
                BigBangExtrasPlugin.Log.LogWarning("star essence sync skipped: " + e);
            }
        }
    }
}

// Offline Reserve and Offline Vault buy no game bonus, so their BonusType is
// none, and the card prints that type's name above the value. setup_mod.py
// renames the none key to "Automation Level" for Automated Arrival, and every
// none line shares that one key, so these two cards read "Automation Level 3".
// Renaming the key again would only move the problem, so the two labels are
// rewritten here after the game has written its own, as Cargo Bay and
// Planetary Memory do for theirs.
//
// The number is the tier's own value from the loaded data, the same field
// OfflineFix reads to set the limit, so the card and the limit cannot drift.
// It is a running total: Reserve 3 means three extra days, not the third day.
//
// LangText.SetText runs the string through Regex.Unescape, so it must carry no
// backslashes. None of these does.
[HarmonyPatch(typeof(BigBangUpgradeItem), "UpdateUI")]
public static class OfflineCardTextPatch
{
    private static readonly FieldInfo UpgradeSetField =
        AccessTools.Field(typeof(BigBangUpgradeItem), "upgradeSet");
    private static readonly FieldInfo CurrentLevelField =
        AccessTools.Field(typeof(BigBangUpgradeItem), "currentLevel");
    private static readonly FieldInfo CurrentBonusField =
        AccessTools.Field(typeof(BigBangUpgradeItem), "currentBonusLabel");
    private static readonly FieldInfo NextBonusField =
        AccessTools.Field(typeof(BigBangUpgradeItem), "nextLevelBonusLabel");

    private const string CAPTION = "Offline Time";

    private static bool loggedFailure;

    public static void Postfix(BigBangUpgradeItem __instance)
    {
        try
        {
            BigBangUpgradeSet set = UpgradeSetField.GetValue(__instance) as BigBangUpgradeSet;
            if (set == null || !OfflineIds.IsOfflineLine(set.id))
            {
                return;
            }
            int level = (int)CurrentLevelField.GetValue(__instance);

            // Level 0 and the tier past max have no upgrade of their own, and
            // the game writes its own "no bonus" text there. Leave that alone.
            Relabel(CurrentBonusField.GetValue(__instance) as LangText, set, level);
            Relabel(NextBonusField.GetValue(__instance) as LangText, set, level + 1);
        }
        catch (Exception e)
        {
            if (!loggedFailure)
            {
                loggedFailure = true;
                BigBangExtrasPlugin.Log.LogWarning("offline card label left as it was: " + e);
            }
        }
    }

    private static void Relabel(LangText label, BigBangUpgradeSet set, int level)
    {
        if (label == null || level <= 0)
        {
            return;
        }
        BigBangUpgrade upgrade = set.GetUpgrade(level);
        if (upgrade == null)
        {
            return;
        }
        int amount = level;
        double parsed;
        if (upgrade.bonus != null
            && double.TryParse(upgrade.bonus.eqTemplate, NumberStyles.Float,
                CultureInfo.InvariantCulture, out parsed)
            && parsed > 0.0)
        {
            amount = (int)Math.Round(parsed);
        }
        string unit = (set.id == OfflineIds.VAULT) ? "week" : "day";
        label.SetText(CAPTION + "\n+" + amount.ToString(CultureInfo.InvariantCulture)
            + " " + unit + (amount == 1 ? "" : "s"));
    }
}
