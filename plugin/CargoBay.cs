// Space Travel Idle community mod - Ballast Trim, and readable text on the
// Big Bang lines that buy no bonus.
//
// ---------------------------------------------------------------------------
// 1. Ballast Trim
//
// Cargo does not slow the ship down a bit. It slows it down by orders of
// magnitude, and the game never says so anywhere.
//
//   Engine.estimatedSpeed => (kineticEnergy * speedModifier / totalWeight).Sqrt()
//   Engine.totalWeight    => (spaceship.weight + storage.weight) * spaceshipDieting
//   Spaceship.weight      => 100, a flat constant that never changes
//
// So the hull is 100 for the whole game, and the hold is whatever the seven
// resources weigh. Measured on a real save with every filter open: 789 million.
// That is not "the ship is heavier", that is a different ship. Travel time goes
// as the square root of weight, so a full hold multiplies every journey by
// about 2,800.
//
// The result is a mechanic nobody can use. You cannot fly with cargo, so you
// empty the hold before every departure, and then you land somewhere with
// nothing in it and your research and building sit idle until you have
// manually opened the filters again and waited for them to refill.
//
// It is worse than that, because the hold's weight is set by its CAP:
//
//   ResourceStorage.cap => capBase(1e4) * Modifiers.resourceTankMul
//
// Every level of resource_storage_up raises that cap, so in vanilla buying
// storage upgrades makes your ship slower. Two upgrade lines in the same tree
// fighting each other, with no hint that they do.
//
// Cargo Compression (spaceship_dieting) looks like the answer and is not. It
// multiplies the hull AND the hold by the same number, so it can never change
// the ratio between them. It makes the whole ship lighter, which helps, but a
// loaded ship stays 7.7 million times heavier than an empty one no matter how
// far that line is bought.
//
// What this does instead: cargo stops being weighed in absolute units and is
// weighed as a multiple of the hull, scaled by how full the hold is.
//
//   vanilla : totalWeight = (hull + storage.weight) * dieting
//   trimmed : totalWeight = (hull + hull * K * fillFraction) * dieting
//
// K falls with each level of the line. fillFraction is the hold's weight over
// what it would weigh at a full cap on all seven resources, so the whole thing
// is scale free: raising the cap raises the numerator and the denominator
// together and the ship's speed does not notice. That kills the anti-synergy
// on its own.
//
// Because speed is a square root, a full hold costs exactly sqrt(1 + K) times
// the journey. The numbers in the data file are that multiplier rather than K,
// since it is the number a player can act on, and K is recovered here as
// m * m - 1. One source of truth: retuning the line means editing the values in
// tools/gen_bigbang.py and reinstalling, with no rebuild of this DLL.
//
// A partial load now costs a partial penalty, which it never did before. In
// vanilla a filter slider anywhere under 100% was pointless, an on/off switch
// with extra steps, because any cargo at all wrecked the trip. Here half a hold
// is half the K, so the sliders become a dial you can actually set.
//
// Level 0 changes nothing at all. Without the line bought this plugin returns
// out of the patch before it touches anything, so an untouched save behaves
// exactly as the base game does.
//
// ---------------------------------------------------------------------------
// 2. The upgrade cards for the lines that buy no bonus
//
// BigBangUpgradeItem.UpdateUI writes the bonus onto the card with
//
//     currentBonusLabel.SetText(currentUpgrade.GetBonusText());
//
// and GetBonusText is typeString + "\n" + sign + value. Both of the mod's
// switch lines - Automated Arrival and Ballast Trim - carry BonusType.none
// because there is no spare BonusType in the game to hang them on, and every
// BonusType.none in the game shares ONE localisation key. So whatever that key
// says, both cards say. setup_mod.py renames it to "Automation Level", which
// suits Automated Arrival and reads as nonsense on Ballast Trim.
//
// Renaming it again would just move the problem to the other card. Instead the
// card text is written here, per line, after the game has written its own. The
// localisation rename stays where it is as the fallback: with this plugin
// missing, Automated Arrival still reads the way it does today and Ballast Trim
// reads oddly rather than breaking.
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
using LargeNumbers;
using UnityEngine;

[BepInPlugin("sti.community.cargobay", "STI Community Cargo Bay", "1.3.11")]
public class CargoBayPlugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;

    internal static ConfigEntry<bool> cfgBallastTrim;
    internal static ConfigEntry<bool> cfgDoorsArmed;
    internal static ConfigEntry<bool> cfgDoorsKeepRecommended;
    internal static ConfigEntry<bool> cfgDoorsKeepBiomass;
    internal static ConfigEntry<bool> cfgQuantumPocket;
    internal static ConfigEntry<bool> cfgCardText;

    private void Awake()
    {
        // 0.1.1: off if the player set it to false in STI Community Patch.cfg
        if (!CommunityToggle.On("cargo-bay", Logger))
        {
            enabled = false;
            return;
        }
        Log = Logger;

        cfgBallastTrim = Config.Bind("1 Ballast Trim", "Enabled", true,
            "Weigh cargo as a share of a full hold rather than in absolute "
            + "units, at the strength bought on the Ballast Trim line in the "
            + "Big Bang tree. With the line at level 0 this does nothing "
            + "either way, so turning it off only matters once you own it.");

        cfgDoorsArmed = Config.Bind("2 Cargo Bay Doors", "JettisonOnDeparture", false,
            "Empty the hold when you leave a star and keep it shut for the "
            + "whole flight, so nothing leaks back in. At level 1 of the line "
            + "everything goes. At level 2 each resource keeps whatever its "
            + "lock slider on the storage panel protects. This is the arm "
            + "switch a player flips between trips, so it starts off.");

        cfgDoorsKeepRecommended = Config.Bind("2 Cargo Bay Doors",
            "KeepWhatTheDestinationRecommends", false,
            "A second way of deciding what goes overboard. Every planet's "
            + "travel panel lists a recommended amount of certain resources "
            + "to arrive with. With this on, that much of each is kept and "
            + "everything else goes, and what is kept is locked for the "
            + "flight so research cannot spend it on the way. Only the Moon, "
            + "Venus and Mercury "
            + "recommend anything at all in the base game, so on a trip "
            + "anywhere else this behaves the same as leaving it off.");
        cfgDoorsKeepBiomass = Config.Bind("2 Cargo Bay Doors",
            "KeepBiomassToo", true,
            "While the option above is on, never throw biomass overboard, "
            + "whatever the destination recommends. Biomass is what space "
            + "research runs on and losing it is the mistake that is "
            + "expensive to undo.");

        cfgQuantumPocket = Config.Bind("3 Quantum Pocket", "Enabled", true,
            "Catch everything earned during a flight in a weightless pocket "
            + "that research and infrastructure can spend from, and deliver "
            + "whatever is left into the hold on arrival. With the line at "
            + "level 0 this does nothing either way.");

        cfgCardText = Config.Bind("4 Upgrade cards", "RewriteSwitchLineText", true,
            "Write readable text on the four Big Bang lines that buy no bonus "
            + "(Automated Arrival, Ballast Trim, Cargo Bay Doors and Quantum "
            + "Pocket). They share one localisation key between them, so "
            + "without this only one of the four can read correctly.");

        Harmony harmony = new Harmony("sti.community.cargobay");
        harmony.PatchAll(Assembly.GetExecutingAssembly());

        // The pocket needs a heartbeat of its own: it writes its sidecar when
        // something moved and reads it back once per flight. A second is slow
        // enough to cost nothing and fast enough that a crash loses almost
        // nothing.
        gameObject.AddComponent<CargoBayTicker>();

        Logger.LogInfo("Cargo bay active: Ballast Trim '" + BallastTrim.UPGRADE_ID
            + "', Cargo Doors '" + CargoDoors.UPGRADE_ID + "', Quantum Pocket '"
            + QuantumPocket.UPGRADE_ID + "'");
    }
}

/// <summary>
/// One tick a second for the parts that are not driven by a patch. Lives on the
/// plugin's own GameObject, which BepInEx keeps alive for the session, so it
/// survives every scene change the game makes.
/// </summary>
public class CargoBayTicker : MonoBehaviour
{
    private float elapsed;

    private void Update()
    {
        elapsed += Time.unscaledDeltaTime;
        if (elapsed < 1f)
        {
            return;
        }
        elapsed = 0f;
        try
        {
            QuantumPocket.SecondTick();
        }
        catch (Exception e)
        {
            QuantumPocket.broken = true;
            QuantumPocket.ReportFailureOnce("the pocket's own heartbeat", e);
        }
    }
}

public static class BallastTrim
{
    public const string UPGRADE_ID = "cargo_ballast_trim";

    // How much longer a journey takes with a completely full hold, per level.
    // Only used when the data file cannot be read - the real numbers live in
    // tools/gen_bigbang.py and are read back out of the loaded upgrade set
    // below. Kept in step by hand; if the two ever disagree the data wins.
    private static readonly double[] FALLBACK_MULTIPLIER =
    {
        89.0, 55.0, 33.0, 20.0, 12.3, 7.5, 4.6, 3.0, 2.2, 1.7
    };

    private static int cachedLevel = -1;
    private static double cachedK;
    private static bool cachedFromData;

    // The weight of one unit of every resource added together. ElementBatch's
    // table is private, so it is summed through the public weight property
    // once and kept. It is a constant of the game: 16.05 as of 0.35.43.
    private static ScientificNotation unitSum;
    private static bool unitSumReady;

    private static bool loggedFailure;

    public static int Level()
    {
        BigBangManager manager = BigBangManager.shared;
        if (manager == null || manager.upgradeLevels == null)
        {
            return 0;
        }
        return manager.GetUpgradeLevel(UPGRADE_ID);
    }

    /// <summary>How much longer a full hold makes a journey, at this level.</summary>
    public static double Multiplier(int level, out bool fromData)
    {
        fromData = false;
        if (level <= 0)
        {
            return 1.0;
        }
        try
        {
            BigBangUpgradeSet set;
            if (BigBangLibrary.upgrades != null
                && BigBangLibrary.upgrades.TryGetValue(UPGRADE_ID, out set))
            {
                BigBangUpgrade upgrade = set.GetUpgrade(level);
                if (upgrade != null && upgrade.bonus != null)
                {
                    double parsed;
                    if (double.TryParse(upgrade.bonus.eqTemplate, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out parsed)
                        && parsed >= 1.0)
                    {
                        fromData = true;
                        return parsed;
                    }
                }
            }
        }
        catch (Exception)
        {
            // the tree is not loaded yet, or the line is not installed - the
            // table below covers both and this runs on a physics tick, so it
            // stays quiet
        }
        if (level <= FALLBACK_MULTIPLIER.Length)
        {
            return FALLBACK_MULTIPLIER[level - 1];
        }
        return FALLBACK_MULTIPLIER[FALLBACK_MULTIPLIER.Length - 1];
    }

    /// <summary>
    /// Cargo weight as a multiple of the hull, at a completely full hold.
    /// Zero when the line is not bought, which is the "change nothing" case.
    /// </summary>
    public static double K()
    {
        int level = Level();
        if (level != cachedLevel || !cachedFromData)
        {
            bool fromData;
            double multiplier = Multiplier(level, out fromData);
            cachedLevel = level;
            cachedFromData = fromData;
            cachedK = (multiplier <= 1.0) ? 0.0 : (multiplier * multiplier - 1.0);
        }
        return cachedK;
    }

    private static ScientificNotation UnitWeightSum()
    {
        if (!unitSumReady)
        {
            ScientificNotation one = new ScientificNotation(1.0);
            ScientificNotation sum = ScientificNotation.zero;
            foreach (ElementType type in Enum.GetValues(typeof(ElementType)))
            {
                sum += new ElementBatch(type, one).weight;
            }
            unitSum = sum;
            unitSumReady = true;
        }
        return unitSum;
    }

    /// <summary>What the hold would weigh with all seven resources at cap.</summary>
    public static ScientificNotation FullHoldWeight()
    {
        return ResourceStorage.cap * UnitWeightSum();
    }

    internal static void ReportFailureOnce(string what, Exception e)
    {
        if (loggedFailure)
        {
            return;
        }
        loggedFailure = true;
        CargoBayPlugin.Log.LogWarning("Ballast Trim turned itself off after "
            + what + " failed: " + e.Message
            + ". Cargo weight is back to the game's own.");
    }

    internal static bool broken;
}

// Engine.totalWeight is the one place cargo turns into slowness. It is read
// from estimatedSpeed, which Player.FixedUpdate asks for every tick, so this
// has to stay cheap: a dictionary lookup, a division and a multiply.
//
// Two lines meet here. Cargo Doors decides HOW MUCH cargo the ship leaves
// with, and Ballast Trim decides what that cargo COSTS. Doors first, so
// the trim is applied to the load that actually flies, and so the answer
// is right whichever of the two the player owns.
[HarmonyPatch(typeof(Engine), "totalWeight", MethodType.Getter)]
public static class EngineTotalWeightPatch
{
    public static void Postfix(Engine __instance, ref ScientificNotation __result)
    {
        Player player = __instance.player;
        if (player == null)
        {
            player = Player.shared;
        }
        if (player == null || player.spaceship == null || player.storage == null)
        {
            return;
        }

        // What the hold will weigh once the doors have done their work. The
        // hold's own weight when they are not armed, which is every case
        // that does not involve this line.
        ScientificNotation cargo = CargoDoors.FlightStorageWeight(player);
        bool lightened = CargoDoors.WillJettison() && cargo < player.storage.weight;

        if (BallastTrim.broken || !CargoBayPlugin.cfgBallastTrim.Value)
        {
            if (lightened)
            {
                __result = (player.spaceship.weight + cargo)
                    * Modifiers.spaceshipDieting;
            }
            return;
        }
        try
        {
            double k = BallastTrim.K();
            if (k <= 0.0)
            {
                if (lightened)
                {
                    __result = (player.spaceship.weight + cargo)
                        * Modifiers.spaceshipDieting;
                }
                return;
            }
            ScientificNotation full = BallastTrim.FullHoldWeight();
            if (full.IsZero())
            {
                return;
            }

            // How full the hold is by weight, against a full cap on all seven
            // resources. Clamped at both ends: NormalizeAllStorage is skipped
            // while OfflineManager is simulating, so mid catch-up the hold can
            // genuinely hold more than its own cap, and a fill over 1 would
            // brake the ship for a load it is not really carrying.
            double fill = (cargo / full).Standard();
            if (!(fill > 0.0))
            {
                fill = 0.0;
            }
            else if (fill > 1.0)
            {
                fill = 1.0;
            }

            ScientificNotation hull = player.spaceship.weight;
            __result = (hull + hull * (k * fill)) * Modifiers.spaceshipDieting;
        }
        catch (Exception e)
        {
            // Never let a weight calculation take the game down with it. One
            // failure disables the patch for the session and the ship goes
            // back to flying the way the base game flies it.
            BallastTrim.broken = true;
            BallastTrim.ReportFailureOnce("the cargo weight calculation", e);
        }
    }
}

// Both of the mod's switch lines carry BonusType.none and therefore share one
// localisation key, so the game can only ever label one of them correctly.
// This writes both, after UpdateUI has written the game's own text.
[HarmonyPatch(typeof(BigBangUpgradeItem), "UpdateUI")]
public static class SwitchLineCardText
{
    private static FieldInfo setField;
    private static FieldInfo levelField;
    private static FieldInfo currentLabelField;
    private static FieldInfo nextLabelField;
    private static bool resolved;
    private static bool usable;
    private static bool loggedFailure;

    private static bool Resolve()
    {
        if (resolved)
        {
            return usable;
        }
        resolved = true;
        setField = AccessTools.Field(typeof(BigBangUpgradeItem), "upgradeSet");
        levelField = AccessTools.Field(typeof(BigBangUpgradeItem), "currentLevel");
        currentLabelField = AccessTools.Field(typeof(BigBangUpgradeItem), "currentBonusLabel");
        nextLabelField = AccessTools.Field(typeof(BigBangUpgradeItem), "nextLevelBonusLabel");
        usable = setField != null && levelField != null
            && currentLabelField != null && nextLabelField != null;
        if (!usable)
        {
            CargoBayPlugin.Log.LogWarning("Upgrade card text: the fields moved "
                + "in this game version, so the two switch lines keep the text "
                + "the game gives them.");
        }
        return usable;
    }

    /// <summary>The lines this rewrites, all of them BonusType.none.</summary>
    private static bool IsSwitchLine(string id)
    {
        return id == BallastTrim.UPGRADE_ID
            || id == CargoDoors.UPGRADE_ID
            || id == QuantumPocket.UPGRADE_ID
            || id == "auto_start_ir";
    }

    /// <summary>
    /// Two lines of card text for one level of one switch line, or null to
    /// leave whatever the game already wrote.
    /// </summary>
    private static string TextFor(string id, int level)
    {
        if (level <= 0)
        {
            // level 0 has no card of its own; the game prints its own "no
            // bonus" text and that is the right thing to say
            return null;
        }
        if (id == BallastTrim.UPGRADE_ID)
        {
            bool fromData;
            double multiplier = BallastTrim.Multiplier(level, out fromData);
            return "Full hold\nx" + multiplier.ToString("0.#",
                CultureInfo.InvariantCulture) + " time";
        }
        if (id == CargoDoors.UPGRADE_ID)
        {
            return (level >= 2)
                ? "Jettison\nper resource"
                : "Jettison\nwhole hold";
        }
        if (id == QuantumPocket.UPGRADE_ID)
        {
            bool fromData;
            double percent = QuantumPocket.Percent(level, out fromData);
            return "Weightless\n" + percent.ToString("0.#",
                CultureInfo.InvariantCulture) + "% of a hold";
        }
        if (id == "auto_start_ir")
        {
            return "Automation\nLevel " + level;
        }
        return null;
    }

    public static void Postfix(BigBangUpgradeItem __instance)
    {
        if (!CargoBayPlugin.cfgCardText.Value || !Resolve())
        {
            return;
        }
        try
        {
            BigBangUpgradeSet set = setField.GetValue(__instance) as BigBangUpgradeSet;
            if (set == null)
            {
                return;
            }
            if (!IsSwitchLine(set.id))
            {
                return;
            }
            int level = (int)levelField.GetValue(__instance);

            LangText current = currentLabelField.GetValue(__instance) as LangText;
            if (current != null)
            {
                string text = TextFor(set.id, level);
                if (text != null)
                {
                    current.SetText(text);
                }
            }

            LangText next = nextLabelField.GetValue(__instance) as LangText;
            if (next != null && set.GetUpgrade(level + 1) != null)
            {
                string text = TextFor(set.id, level + 1);
                if (text != null)
                {
                    next.SetText(text);
                }
            }
        }
        catch (Exception e)
        {
            if (!loggedFailure)
            {
                loggedFailure = true;
                CargoBayPlugin.Log.LogWarning("Upgrade card text failed: "
                    + e.Message + ". The cards keep the game's own text.");
            }
        }
    }
}
