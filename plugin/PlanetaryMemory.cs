// Space Travel Idle community mod - Planetary Memory
//
// A new Big Bang upgrade line. Fuzzied, 19.09.2026: "Im thinking a new big bang
// line: Keep 1% of a planet only bonus, per level. 1% on one planet is not
// allot but the more planets and the higher bonus it feels like it could be
// worth it."
//
// The problem it solves
// --------------------
// Research bonuses are global. TechnoManager.AddAllResearchBonuses loops every
// star's research set, so anything you research anywhere counts everywhere,
// forever.
//
// Infrastructure is not. ForceReloadAllBonuses clears currentInfraBonus and
// then adds only the star you are staying at (plus the spaceship, star -1,
// which is the one existing exception). Fly away from Mercury and its x150
// energy multiplier simply stops. A hundred-level infrastructure grind pays
// you nothing the moment you leave, which is why the outer planets feel like
// starting over every time.
//
// This line lets you carry a share of it with you.
//
// What it does
// ------------
// Fifteen levels, 2% each, so 30% at max. For every bonus type, it finds the
// strongest value you have built on any star you are NOT currently standing
// on, and applies a fraction of it wherever you are.
//
//   multiply bonuses:  carried = best ^ share
//   add bonuses:       carried = best * share
//
// The geometric form is the important one. Infra bonuses are multipliers, and
// "1% of x150" has no obvious meaning: read linearly it is x2.49, which hands
// you most of the line's value at level one and leaves the back half worth
// nothing. Raising to the power instead makes every level worth exactly the
// same, which is the shape an idle game wants. At 30% Mercury's x150 becomes
// x4.50 carried, and each level multiplies what you carry by a steady 1.35.
//
// Add bonuses cannot be raised to a power sensibly (0 is their identity, not
// 1), so they take the plain share. Martian Walls' +20 defense becomes +6.
// The game stores those as fractions already - Build A Wall is "0.3*{x}" and
// Temple of Hermes Silicon starts at -0.0000113 - and Modifiers.attackBase
// (line 95) and playerMoveDur (line 109) are ScientificNotation and float with
// no integer cast anywhere, so +6.0 or +0.09 are both fine.
//
// Why the current star is excluded
// --------------------------------
// Its bonuses are already in the set at full strength. BonusComposite.multiply
// composes by multiplying, so including it would stack x150 with x4.50 and
// give x675 on the one planet where the bonus was never missing. Excluding it
// also makes the name literally true: this is what you keep from the planets
// you are away from.
//
// The ceiling that comes out of that is mild. The worst case anywhere is
// standing on Mercury (x150 of its own) while carrying Venus's Lava BBQ
// (x28 ^ 0.3 = x2.72), so x408 against a vanilla best of x150. Every planet
// keeps its identity: Mercury is still 33 times better for energy than
// anything you can carry off it.
//
// Why the station buildings are excluded
// --------------------------------------
// Nine infrastructure lines carry erDeteriorationMinBorderUp (BonusType 21):
// the headquarters, station, camp, shelter and outpost buildings, roughly one
// per planet. Star.cs:110 uses that bonus as the floor under the efficiency
// that decays as you run more jobs at once, and Star.cs:120 clamps the floor
// at 1.0. Mercury's alone is x47.50, so carrying even 30% of it (x3.08) pins
// efficiency at 100% forever and deletes the mechanic outright. It stays
// local, and it is the only exclusion.
//
// Where it hooks
// --------------
// TechnoManager.ForceReloadAllBonuses is the whole story. It runs on start
// (TechnoManager.cs:61), on save load (SaveLoadManager.cs:799) and on arrival
// at a star - that last one indirectly, via UnlockManager.cs:211, which fires
// it whenever needsLocationUpdate is set. Nothing else rebuilds the set from
// scratch.
//
// Individual changes go through TechnoManager.SetInfraBonus instead, and those
// only ever touch the star you are on (Infra.cs:312, AddStarInfraBonuses).
// Since the carried set deliberately excludes the current star, none of them
// can change what this plugin contributes, so there is nothing to hook there.
// Off-star levels cannot move at all while you are away: TechnoPanel.cs:406
// disables the items when you are browsing another star.
//
// Bonuses are never saved. They are rebuilt from scratch on load and on unlock
// changes, so there is no save-format risk in any of this.
//
// A note on keys
// --------------
// BonusSet keys by a plain string, and the id rocket_launcher exists on six
// different stars with that exact same id. Anything looping all stars and
// passing infra.id would silently collapse those six into one. Nothing here
// uses infra.id: the carried entries are keyed by bonus type and method, which
// is what they actually represent.

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LargeNumbers;

[BepInPlugin("sti.community.planetarymemory", "STI Community Planetary Memory", "1.0.1")]
public class PlanetaryMemoryPlugin : BaseUnityPlugin
{
    // Matches the line generated into bigBangUpgrades by tools/gen_bigbang.py.
    // If the data patch is not installed GetUpgradeLevel returns 0 and this
    // plugin does nothing at all, which is the correct behaviour for someone
    // running the code patches without the data ones.
    internal const string UPGRADE_ID = "planetary_memory";

    // 15 levels at 2% each. MAX_LEVEL is a clamp rather than a source of
    // truth: if the data ever grows more levels, the effect stops at 30%
    // until this constant is raised deliberately.
    internal const int MAX_LEVEL = 15;
    internal const double PER_LEVEL = 0.02;

    // BonusType.erDeteriorationMinBorderUp. See the header.
    internal const BonusType EFFICIENCY_FLOOR = (BonusType)21;

    internal static ManualLogSource Log;
    internal static ConfigEntry<bool> cfgEnabled;

    private void Awake()
    {
        Log = Logger;
        cfgEnabled = Config.Bind("Planetary Memory", "Enabled", true,
            "Carry a share of your off-planet infrastructure bonuses with you, "
            + "scaled by the Planetary Memory Big Bang upgrade. Turning this "
            + "off does not refund or reset the upgrade, it just stops it "
            + "doing anything.");

        Harmony harmony = new Harmony("sti.community.planetarymemory");
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        Logger.LogInfo("Planetary Memory active: infrastructure bonuses from "
            + "other stars carry over, at 2% per upgrade level");
    }
}

// One carried entry, remembered so it can be taken back out again. Storing the
// type and method alongside the key is what lets UnsetBonus find the right
// composite without re-deriving anything.
internal struct CarriedEntry
{
    public string key;
    public BonusType type;
    public BonusMethod method;
}

internal static class PlanetaryMemory
{
    private static readonly List<CarriedEntry> applied = new List<CarriedEntry>();

    // Reused across rebuilds so a bonus reload does not allocate. Keyed by the
    // pair the bonus actually lives under in BonusSet.
    private static readonly Dictionary<string, double> best =
        new Dictionary<string, double>();
    private static readonly Dictionary<string, BonusType> bestType =
        new Dictionary<string, BonusType>();
    private static readonly Dictionary<string, BonusMethod> bestMethod =
        new Dictionary<string, BonusMethod>();

    private static bool loggedFailure;
    private static int lastLoggedLevel = -1;

    internal static int Level()
    {
        BigBangManager manager = BigBangManager.shared;
        // upgradeLevels is a plain field (BigBangManager.cs:12), null until
        // LoadUpgradeLevels runs at SaveLoadManager.cs:830, and
        // GetUpgradeLevel dereferences it with no guard of its own. The
        // singleton exists well before that, and ForceReloadAllBonuses fires
        // in between, so checking the manager alone is not enough. The other
        // five plugins that read this already check both.
        if (manager == null || manager.upgradeLevels == null)
        {
            return 0;
        }
        int level = manager.GetUpgradeLevel(PlanetaryMemoryPlugin.UPGRADE_ID);
        if (level < 0)
        {
            return 0;
        }
        if (level > PlanetaryMemoryPlugin.MAX_LEVEL)
        {
            return PlanetaryMemoryPlugin.MAX_LEVEL;
        }
        return level;
    }

    internal static void Apply()
    {
        TechnoManager techno = TechnoManager.shared;
        if (techno == null || techno.currentInfraBonus == null)
        {
            return;
        }
        BonusSet set = techno.currentInfraBonus;

        // Take the previous contribution back out first. After a full
        // ForceReloadAllBonuses the set has already been cleared and every one
        // of these is a no-op (DecomposeValue on an absent key divides by 1),
        // but doing it unconditionally means this is safe to call from
        // anywhere later without leaving a stale multiplier behind.
        for (int i = 0; i < applied.Count; i++)
        {
            set.UnsetBonus(applied[i].key, applied[i].type, applied[i].method);
        }
        applied.Clear();

        if (PlanetaryMemoryPlugin.cfgEnabled != null
            && !PlanetaryMemoryPlugin.cfgEnabled.Value)
        {
            return;
        }

        int level = Level();
        if (level <= 0)
        {
            return;
        }
        double share = level * PlanetaryMemoryPlugin.PER_LEVEL;

        Player player = Player.shared;
        if (player == null || player.location == null)
        {
            return;
        }
        int here = player.location.stayingStarIndex;

        best.Clear();
        bestType.Clear();
        bestMethod.Clear();

        // starCount is the planets only; the spaceship set sits past the end
        // of that range and GetInfraSet returns it for anything out of bounds.
        // Leaving the ship out is deliberate: star -1 already applies
        // everywhere, so carrying a share of it would be double counting.
        int count = StarMap.starCount;
        for (int idx = 0; idx < count; idx++)
        {
            if (idx == here)
            {
                continue;
            }
            InfraSet infraSet = StarMap.GetInfraSet(idx);
            if (infraSet == null)
            {
                continue;
            }
            List<Infra> infras = infraSet.infras;
            for (int i = 0; i < infras.Count; i++)
            {
                Consider(infras[i]);
            }
        }

        foreach (KeyValuePair<string, double> pair in best)
        {
            BonusType type = bestType[pair.Key];
            BonusMethod method = bestMethod[pair.Key];
            double carried;
            if (method == BonusMethod.multiply)
            {
                carried = Math.Pow(pair.Value, share);
            }
            else
            {
                carried = pair.Value * share;
            }
            if (double.IsNaN(carried) || double.IsInfinity(carried))
            {
                continue;
            }

            CarriedEntry entry;
            entry.key = "planetaryMemory:" + pair.Key;
            entry.type = type;
            entry.method = method;
            set.SetBonus(entry.key, type, method,
                new ScientificNotation(carried));
            applied.Add(entry);
        }

        if (level != lastLoggedLevel && PlanetaryMemoryPlugin.Log != null)
        {
            lastLoggedLevel = level;
            PlanetaryMemoryPlugin.Log.LogInfo("Planetary Memory level " + level
                + " (" + (share * 100.0).ToString("0.#") + "%), carrying "
                + applied.Count + " bonus types from "
                + (here < 0 ? "every star" : "every star except " + here));
        }
    }

    // Fold one building into the running best-per-bonus-type.
    private static void Consider(Infra infra)
    {
        if (infra == null)
        {
            return;
        }

        // Infra.bonus already returns null when the building is switched off
        // or its on-level is zero (Infra.cs:116), so a building you turned
        // down before leaving carries nothing. Cost and benefit stay the same
        // switch, exactly as they do on the planet itself.
        Bonus bonus = infra.bonus;
        if (bonus == null || bonus.eqTemplate == null)
        {
            return;
        }
        if (bonus.type == BonusType.none || bonus.method == BonusMethod.none)
        {
            return;
        }
        if (bonus.type == PlanetaryMemoryPlugin.EFFICIENCY_FLOOR)
        {
            return;
        }

        double value = bonus.GetEquationOutput(infra.onLevel,
            infra.maxLevel).Standard();
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return;
        }
        // A multiplier of zero or less has no meaningful fractional power, and
        // nothing in the game data produces one. Skipping beats returning NaN.
        if (bonus.method == BonusMethod.multiply && value <= 0.0)
        {
            return;
        }

        string key = ((int)bonus.type).ToString() + ":"
            + ((int)bonus.method).ToString();

        // "Strongest" has to work in both directions, because a multiplier
        // below 1 and an add below 0 are both improvements for some bonus
        // types. Distance from the method's identity (1 for multiply, 0 for
        // add) is the measure that gets both right, and for multipliers that
        // distance is logarithmic, not linear.
        double strength = (bonus.method == BonusMethod.multiply)
            ? Math.Abs(Math.Log(value))
            : Math.Abs(value);

        double existing;
        if (best.TryGetValue(key, out existing))
        {
            double existingStrength = (bonus.method == BonusMethod.multiply)
                ? Math.Abs(Math.Log(existing))
                : Math.Abs(existing);
            if (strength <= existingStrength)
            {
                return;
            }
        }
        best[key] = value;
        bestType[key] = bonus.type;
        bestMethod[key] = bonus.method;
    }

    internal static void ClearFailureLatch()
    {
        loggedFailure = false;
    }

    internal static void ReportFailure(Exception e)
    {
        if (loggedFailure || PlanetaryMemoryPlugin.Log == null)
        {
            return;
        }
        loggedFailure = true;
        PlanetaryMemoryPlugin.Log.LogError(
            "Planetary Memory could not apply its bonuses, so off-planet "
            + "infrastructure is behaving as it does in vanilla. Everything "
            + "else is unaffected. " + e);
    }
}

// The one hook. Everything that rebuilds currentInfraBonus from scratch comes
// through here, arrival included (UnlockManager.cs:211).
[HarmonyPatch(typeof(TechnoManager), "ForceReloadAllBonuses")]
public static class PlanetaryMemoryReloadPatch
{
    public static void Postfix()
    {
        try
        {
            PlanetaryMemory.Apply();
            // A run that got all the way through re-arms the error log. The
            // latch exists so one broken frame cannot fill the log, but left
            // permanently set it would also hide a real failure that started
            // later, which is the more expensive of the two mistakes.
            PlanetaryMemory.ClearFailureLatch();
        }
        catch (Exception e)
        {
            PlanetaryMemory.ReportFailure(e);
        }
    }
}

// The upgrade card prints the bonus TYPE above the value, and this line's type
// is BonusType.none, which the mod renames to "Automation Level" for the sake
// of Automated Arrival (see the bigbang-plus patch in setup_mod.py). That key
// is shared by every switch-driven line, so it cannot be renamed for this one
// without breaking the others. Rewriting the two labels on this card instead
// keeps the change where it belongs.
//
// LangText.SetText runs the string through Regex.Unescape, so it must carry no
// backslashes. Neither of these does.
[HarmonyPatch(typeof(BigBangUpgradeItem), "UpdateUI")]
public static class PlanetaryMemoryLabelPatch
{
    private static readonly FieldInfo UpgradeSetField =
        AccessTools.Field(typeof(BigBangUpgradeItem), "upgradeSet");
    private static readonly FieldInfo CurrentLevelField =
        AccessTools.Field(typeof(BigBangUpgradeItem), "currentLevel");
    private static readonly FieldInfo CurrentBonusField =
        AccessTools.Field(typeof(BigBangUpgradeItem), "currentBonusLabel");
    private static readonly FieldInfo NextBonusField =
        AccessTools.Field(typeof(BigBangUpgradeItem), "nextLevelBonusLabel");

    private const string CAPTION = "Bonus Kept";

    private static bool loggedFailure;

    public static void Postfix(BigBangUpgradeItem __instance)
    {
        try
        {
            BigBangUpgradeSet set =
                UpgradeSetField.GetValue(__instance) as BigBangUpgradeSet;
            if (set == null || set.id != PlanetaryMemoryPlugin.UPGRADE_ID)
            {
                return;
            }
            int level = (int)CurrentLevelField.GetValue(__instance);

            // Only touch a label the game filled in with a real bonus. At
            // level zero, and at max level, it writes its own "no bonus" text
            // and that is the right thing to leave alone.
            Relabel(CurrentBonusField.GetValue(__instance) as LangText,
                set, level);
            Relabel(NextBonusField.GetValue(__instance) as LangText,
                set, level + 1);
        }
        catch (Exception e)
        {
            if (!loggedFailure && PlanetaryMemoryPlugin.Log != null)
            {
                loggedFailure = true;
                PlanetaryMemoryPlugin.Log.LogWarning(
                    "Planetary Memory card label left as it was: " + e);
            }
        }
    }

    private static void Relabel(LangText label, BigBangUpgradeSet set, int level)
    {
        if (label == null || set.GetUpgrade(level) == null)
        {
            return;
        }
        int percent = level * (int)(PlanetaryMemoryPlugin.PER_LEVEL * 100.0);
        label.SetText(CAPTION + "\n" + percent.ToString() + "%");
    }
}
