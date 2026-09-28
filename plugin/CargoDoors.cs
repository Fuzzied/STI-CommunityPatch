// Space Travel Idle community mod - Cargo Bay Doors.
//
// Compiled into CargoBay.dll alongside CargoBay.cs and QuantumPocket.cs. The
// three cargo lines are one system because they talk to each other, and
// BepInEx gives every assembly its own statics, so splitting them across DLLs
// would mean reaching between assemblies by reflection for something that is
// really just a bool.
//
// ---------------------------------------------------------------------------
// What this is for
//
// Ballast Trim makes cargo cheap to carry. Cargo Bay Doors is the other half:
// a way to not carry it at all, for the trips where you want the hull weight
// of 100 and nothing else.
//
// Fuzzied's first sketch was a toggle that empties the hold on departure plus a
// second level that puts everything back on arrival, and the reason a "put it
// back" level was needed is worth writing down, because it is what this design
// avoids. The obvious way to empty a hold in this game is to drag a resource's
// discard slider to 0%, and that is destructive:
//
//     ResourceStorage.NormalizeResource(type)
//         clip = cap * Math.Max(resourceUnit.GetDiscardValue(),
//                               resourceUnit.GetLockValue());
//         if (amount > clip) { amount = clip; ...LoseDurGain(discard, rest); }
//
// The moment the slider moves, everything above the new line is gone. So the
// sliders are the wrong mechanism, and a plugin that drives them has to
// remember and restore what it clobbered. This one keeps its own flag and
// never writes a slider, which means there is nothing to restore and nothing
// to accidentally destroy.
//
// Fuzzied was right about the leak, though, and this is the part that makes a
// naive "dump on departure" useless. Location.Departure sets
// stayingStarIndex = -1, StarMap.GetElementSourceSet falls through to
// elementSourceSets[Count - 1] for any negative index, and that last set is
// the space star. So gas_molecule_collection and plantbased_biomass keep
// producing air and biomass for the whole flight. Empty the hold on departure
// and it fills back up on the way. Sealing has to stop the intake too.
//
// ---------------------------------------------------------------------------
// The one rule
//
//     A sealed hold sits at exactly its lock levels for the whole flight.
//     Anything above them goes overboard at departure, and nothing can rise
//     above them in flight.
//
// Level 1 treats every lock as 0, so the hold empties completely and stays
// empty. Level 2 honours the lock sliders, so you keep precisely what you told
// the game to keep.
//
// v1.2.0 adds a second way of answering the same question, which Fuzzied asked
// for: keep whatever the place you are flying to recommends you bring, and
// throw the rest out. Every star carries one:
//
//   Star.recommendedLocationReq.resourceReqs -> List<ElementBatch>
//
// which is the list the travel panel already shows you, so the rule is "arrive
// with exactly what the panel said to arrive with". Worth knowing before
// turning it on: only the Moon, Venus and Mercury recommend anything in the
// base game. Everywhere else the list is empty and this behaves the same as
// level 1.
//
// It raises the keep line, it never lowers it, so it composes with the lock
// sliders rather than fighting them. At level 2 with both on you keep the
// larger of your own lock and the recommendation, which is the answer nobody
// has to think about.
//
// Fuzzied: "Biomass excluded". Biomass is what space research burns, and a hold
// that arrives without it is the one mistake here that is expensive to undo,
// so while this option is on biomass is never thrown out at all. That is a
// switch of its own rather than a hardcoded exception, because it is a
// judgement about how he plays and not a fact about the game.
//
// v1.3.11: what the destination recommends is also locked for the flight.
// Keeping it was not enough, because research spends from the same hold and
// ate the 3e4 biomass kept for Mercury within 20 seconds. The reserve goes in
// through the same filter the game's own lock slider uses (KeptCargoLockPatch
// below), so research, infra and energy sources all stop at it, and the pocket
// refills the hold far enough that spending never dips into it either.
//
// Reusing the lock slider rather than adding seven tick boxes of my own is the
// second thing that changed from the sketch, and it is a better fit than what
// was drawn. The lock handle already means "this much is mine, don't spend
// below it" - ResourceStorage.AvailableElementBatchPercentage reserves
// cap * lockValue from research - so "and the cargo doors won't throw it out
// either" is the same sentence, not a new one. It is saved by the game
// already, it is per resource already, and it gives a fraction rather than
// all-or-nothing. The plugin only ever READS it.
//
// The default lock is 0 on every resource (Save.cs seeds {0, 100}), so out of
// the box level 2 behaves exactly like level 1 until the player raises a lock
// on purpose. That is the right default: the line is bought to go fast.
//
// Why the intake has to close rather than just discarding what arrives:
// ElementSource.Tick spends energy to produce. Letting a source run and then
// throwing the output away would burn kinetic energy for nothing, which is
// worse than the leak it was meant to fix. So the source is stopped at its own
// early-return instead, the same way the game stops a source when its
// resource is full.
//
// Interaction with the other two lines, which is the whole reason they are one
// plugin:
//
//   Ballast Trim  your existing stock stays usable in flight, at a speed cost
//   Cargo Doors   you fly light, and what you left behind is gone
//   Quantum Pocket  what you EARN in flight is weightless and spendable
//
// A sealed hold cannot feed anything, and that falls out for free rather than
// needing a rule: the jettisoned resources are at zero and the intake is shut,
// so there is nothing there to spend. With a pocket, in-flight income lands in
// the pocket instead of being lost, which is what stops "seal the hold" and
// "earn while flying" from being the same purchase.

using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using LargeNumbers;

public static class CargoDoors
{
    public const string UPGRADE_ID = "cargo_bay_doors";

    private static bool loggedFailure;
    internal static bool broken;

    /// <summary>Levels owned on the line. 0 means the doors do not exist.</summary>
    public static int Level()
    {
        BigBangManager manager = BigBangManager.shared;
        if (manager == null || manager.upgradeLevels == null)
        {
            return 0;
        }
        return manager.GetUpgradeLevel(UPGRADE_ID);
    }

    /// <summary>
    /// True while the hold is shut: the line is owned, the player armed it,
    /// and the ship is in flight. Derived rather than stored on purpose - a
    /// plugin cannot add a field to the save, and a seal that had to survive a
    /// quit would need one. Deriving it means quitting and reloading mid
    /// flight leaves the hold exactly as sealed as it was.
    /// </summary>
    public static bool Sealed()
    {
        if (broken || !CargoBayPlugin.cfgDoorsArmed.Value || Level() <= 0)
        {
            return false;
        }
        Player player = Player.shared;
        return player != null && player.location != null
            && player.location.isTravelling;
    }

    /// <summary>
    /// True when a sealed hold should turn income of this resource away.
    /// The pocket is checked first, because a pocket with room is a perfectly
    /// good destination and nothing needs refusing while it has space.
    ///
    /// This is the same three questions ElementSourceTickPatch asks, pulled
    /// out so the producer path and the income path cannot drift apart. A
    /// producer is stopped before it spends energy; income that arrives from
    /// somewhere else is simply not taken aboard, which comes to the same
    /// thing from the hold's side.
    /// </summary>
    public static bool RefusesIncome(ElementType type)
    {
        if (QuantumPocket.RoomFor(type) > ScientificNotation.zero)
        {
            return false;
        }
        return HoldIsAtItsLock(type);
    }

    /// <summary>
    /// True when the hold itself will not take another unit of this resource,
    /// whatever the pocket is doing.
    /// </summary>
    /// <remarks>
    /// RefusesIncome answers the producer's question, does anything need
    /// turning away, and a pocket with room means no because the pocket is a
    /// perfectly good destination. A readout describing where overflow lands
    /// is asking something narrower: if this gets past the pocket, does the
    /// hold take it. Those two come apart whenever the pocket has room and
    /// something has already spilled earlier in the flight, and the answer the
    /// readout needs is this one.
    /// </remarks>
    public static bool HoldIsAtItsLock(ElementType type)
    {
        if (broken || !Sealed())
        {
            return false;
        }
        Player player = Player.shared;
        if (player == null || player.storage == null)
        {
            return false;
        }
        return player.storage.GetAmount(type) >= KeepAmount(type);
    }

    /// <summary>
    /// The share of cap this resource is allowed to hold during a sealed
    /// flight. Level 1 keeps nothing; level 2 keeps whatever the lock slider
    /// on the storage panel says.
    /// </summary>
    public static double KeepFraction(ElementType type)
    {
        if (Level() < 2)
        {
            return 0.0;
        }
        try
        {
            MainPanel panel = MainPanel.shared;
            if (panel == null || panel.storagePanel == null
                || panel.storagePanel.resourceUnitDict == null)
            {
                return 0.0;
            }
            ResourceUnit unit;
            if (!panel.storagePanel.resourceUnitDict.TryGetValue(type, out unit)
                || unit == null)
            {
                return 0.0;
            }
            double locked = unit.GetLockValue();
            if (!(locked > 0.0))
            {
                return 0.0;
            }
            return (locked > 1.0) ? 1.0 : locked;
        }
        catch (Exception)
        {
            // The storage panel builds its rows in StoragePanel.SetupResources
            // and this can be asked before that has run. Keeping nothing is
            // the safe answer on a line whose whole purpose is to keep
            // nothing.
            return 0.0;
        }
    }

    /// <summary>
    /// True while the doors are set to fire on the next departure: owned,
    /// armed, and still parked. Sealed() answers the flight, this answers
    /// the trip you have not taken yet, and the two never overlap.
    /// </summary>
    public static bool WillJettison()
    {
        if (broken || !CargoBayPlugin.cfgDoorsArmed.Value || Level() <= 0)
        {
            return false;
        }
        Player player = Player.shared;
        return player != null && player.location != null
            && !player.location.isTravelling;
    }

    /// <summary>
    /// The hold weight the ship will carry on the trip, which is the hold
    /// weight it has now unless the doors are about to empty it.
    ///
    /// This exists because the game checks the travel time before it calls
    /// Departure, and the jettison hangs off Departure. Without it a hold
    /// full enough to matter refuses the departure over a travel time that
    /// is thrown out a frame later, and the travel panel quotes a weight, a
    /// speed and an arrival time that none of them will ever be true.
    /// </summary>
    public static ScientificNotation FlightStorageWeight(Player player)
    {
        if (player == null || player.storage == null)
        {
            return ScientificNotation.zero;
        }
        if (!WillJettison() || player.storage.elementDict == null)
        {
            return player.storage.weight;
        }
        try
        {
            ScientificNotation total = ScientificNotation.zero;
            foreach (ElementType type in Enum.GetValues(typeof(ElementType)))
            {
                ElementBatch held;
                if (!player.storage.elementDict.TryGetValue(type, out held)
                    || held == null)
                {
                    continue;
                }
                ScientificNotation keep = KeepAmount(type);
                ScientificNotation left = (held.amount < keep)
                    ? held.amount : keep;
                if (left <= ScientificNotation.zero)
                {
                    continue;
                }
                // Through a batch rather than a weight table of my own, so
                // this can never drift from what the game charges.
                total += new ElementBatch(type, left).weight;
            }
            return total;
        }
        catch (Exception e)
        {
            broken = true;
            ReportFailureOnce("the pre-departure weight", e);
            return player.storage.weight;
        }
    }

    /// <summary>What this resource is allowed to hold during a sealed flight,
    /// for the trip that is about to happen or the one already under way.</summary>
    public static ScientificNotation KeepAmount(ElementType type)
    {
        return KeepAmount(type, TargetStarIndex());
    }

    /// <summary>
    /// The same question for a named destination. Departure knows exactly
    /// where it is going and hands the index straight in, so the one call that
    /// actually destroys anything never has to guess.
    /// </summary>
    public static ScientificNotation KeepAmount(ElementType type, int starIdx)
    {
        ScientificNotation keep = ScientificNotation.zero;
        double fraction = KeepFraction(type);
        if (fraction > 0.0)
        {
            keep = ResourceStorage.cap * new ScientificNotation(fraction);
        }
        ScientificNotation asked = RecommendedAmount(type, starIdx);
        return (asked > keep) ? asked : keep;
    }

    /// <summary>
    /// Where the ship is heading. In flight that is the destination the game
    /// is already carrying; parked it is whatever star the travel panel has
    /// selected, which is the trip the player is looking at.
    /// </summary>
    public static int TargetStarIndex()
    {
        try
        {
            Player player = Player.shared;
            if (player == null || player.location == null)
            {
                return -1;
            }
            if (player.location.isTravelling)
            {
                return player.location.destinationStarIndex;
            }
            SidePanel panel = SidePanel.shared;
            return (panel == null) ? -1 : panel.GetSelectedStarIdx();
        }
        catch (Exception)
        {
            // Nothing selected yet, or the panel has not been built. An index
            // of -1 is the space star, which recommends nothing, so the doors
            // fall back to whatever the lock sliders say.
            return -1;
        }
    }

    /// <summary>
    /// How much of this resource the destination asks you to arrive with.
    /// Zero unless the option is on. Biomass comes back as everything you are
    /// holding when KeepBiomassToo is set, which is how "never throw it out"
    /// is expressed in the one unit the rest of this file speaks.
    /// </summary>
    public static ScientificNotation RecommendedAmount(ElementType type, int starIdx)
    {
        if (CargoBayPlugin.cfgDoorsKeepRecommended == null
            || !CargoBayPlugin.cfgDoorsKeepRecommended.Value)
        {
            return ScientificNotation.zero;
        }
        try
        {
            if (type == ElementType.biomass
                && CargoBayPlugin.cfgDoorsKeepBiomass != null
                && CargoBayPlugin.cfgDoorsKeepBiomass.Value)
            {
                Player player = Player.shared;
                if (player != null && player.storage != null)
                {
                    return player.storage.GetAmount(type);
                }
            }
            return StarRecommends(type, starIdx);
        }
        catch (Exception)
        {
            // The star map is loaded before any save is, so this should not
            // happen. Keeping nothing extra is the answer that behaves like
            // the option was never turned on.
            return ScientificNotation.zero;
        }
    }

    /// <summary>
    /// What the destination's own lists ask for, and nothing else. This is
    /// RecommendedAmount without the KeepBiomassToo answer, which is "all of
    /// it" and means "do not throw it out", not "do not spend it".
    /// </summary>
    public static ScientificNotation StarRecommends(ElementType type, int starIdx)
    {
        Star star = StarMap.GetStarByIndex(starIdx);
        if (star == null)
        {
            return ScientificNotation.zero;
        }
        ScientificNotation most = ScientificNotation.zero;
        // Both lists, and the larger of the two, because a minimum the
        // destination insists on is not something to arrive without
        // either. In this build only the recommended lists carry
        // resources, but that is data and data changes.
        most = Largest(star.minimumLocationReq, type, most);
        most = Largest(star.recommendedLocationReq, type, most);
        return most;
    }

    /// <summary>
    /// The share of cap that research, infra and energy sources may not
    /// spend below while a sealed hold is carrying what the destination
    /// recommends. Zero whenever that option is off or the ship is parked.
    /// </summary>
    /// <remarks>
    /// Fuzzied, 26.09.2026: "lock kept cargo in flight". The in game test on
    /// 25.09.2026 kept 3e4 biomass for Mercury and space research had eaten
    /// all of it within 20 seconds, so the ship arrived short of the very
    /// thing the doors had kept for it.
    ///
    /// Only the star's list is locked. KeepBiomassToo keeps all the biomass
    /// aboard so space research has something to run on, and locking that
    /// would turn the switch against its own reason. Biomass above what the
    /// destination asks for stays spendable.
    ///
    /// Expressed as a fraction of cap because that is the unit the game's own
    /// lock slider is passed in, so the reserve goes through the game's
    /// arithmetic rather than a copy of it.
    /// </remarks>
    public static double LockedFraction(ElementType type)
    {
        if (broken || !KeepingRecommended() || !Sealed())
        {
            return 0.0;
        }
        Player player = Player.shared;
        ScientificNotation asked = StarRecommends(type,
            player.location.destinationStarIndex);
        if (!(asked > ScientificNotation.zero))
        {
            return 0.0;
        }
        return (asked / ResourceStorage.cap).Standard();
    }

    private static ScientificNotation Largest(LocationReq req, ElementType type,
        ScientificNotation so_far)
    {
        if (req == null || req.resourceReqs == null)
        {
            return so_far;
        }
        foreach (ElementBatch asked in req.resourceReqs)
        {
            if (asked != null && asked.type == type && asked.amount > so_far)
            {
                so_far = asked.amount;
            }
        }
        return so_far;
    }

    /// <summary>True when the destination rule is on and this trip has
    /// something to say about. Used by the travel readout.</summary>
    public static bool KeepingRecommended()
    {
        return CargoBayPlugin.cfgDoorsKeepRecommended != null
            && CargoBayPlugin.cfgDoorsKeepRecommended.Value;
    }

    /// <summary>
    /// Throw the excess overboard. Run once, from Location.Departure, on the
    /// departure itself rather than on a timer, so the weight is already gone
    /// by the time the first travel tick reads Engine.totalWeight.
    /// </summary>
    internal static void JettisonNow(int destinationIndex)
    {
        Player player = Player.shared;
        if (player == null || player.storage == null
            || player.storage.elementDict == null)
        {
            return;
        }

        ScientificNotation dumped = ScientificNotation.zero;
        int kinds = 0;
        foreach (ElementType type in Enum.GetValues(typeof(ElementType)))
        {
            ElementBatch held;
            if (!player.storage.elementDict.TryGetValue(type, out held)
                || held == null)
            {
                continue;
            }
            ScientificNotation keep = KeepAmount(type, destinationIndex);
            if (held.amount <= keep)
            {
                continue;
            }
            ScientificNotation excess = held.amount - keep;

            // Through TakeElementBatch rather than by writing the field, so
            // the storage summary records it under ERIOSource.discard the same
            // way NormalizeResource's own clipping does. The player can then
            // see where it went on the Summary tab.
            player.storage.TakeElementBatch(new ElementBatch(type, excess), 1,
                ERIOSource.discard);
            dumped += excess;
            kinds++;
        }

        if (kinds > 0)
        {
            CargoBayPlugin.Log.LogInfo("Cargo doors: jettisoned " + dumped
                + " units across " + kinds + " resources on departure"
                + (KeepingRecommended()
                    ? (", keeping what star " + destinationIndex
                        + " recommends") : ""));
        }
    }

    internal static void ReportFailureOnce(string what, Exception e)
    {
        if (loggedFailure)
        {
            return;
        }
        loggedFailure = true;
        CargoBayPlugin.Log.LogWarning("Cargo doors turned themselves off after "
            + what + " failed: " + e.Message
            + ". The hold behaves the way the game's own does.");
    }
}

// Location.Departure is the single choke point for leaving a star: SidePanel
// .SetOff calls it for a normal journey and for a teleport, and it is the
// method that flips stayingStarIndex to -1. Patching here rather than in
// SidePanel means a departure from anywhere is covered.
//
// Postfix, not prefix, and only when the return value is true - Departure
// refuses if you are already travelling, if the destination is where you
// already are, or if the minimum requirement is not met, and a jettison on a
// departure that did not happen would be a very expensive misfire.
[HarmonyPatch(typeof(Location), "Departure")]
public static class LocationDeparturePatch
{
    // destinationIndex by name rather than TargetStarIndex(), because this is
    // the one call that destroys anything and Departure already knows the
    // answer for certain. Nothing here should depend on what a UI panel
    // happens to have selected.
    public static void Postfix(bool __result, int destinationIndex)
    {
        if (!__result || CargoDoors.broken
            || !CargoBayPlugin.cfgDoorsArmed.Value || CargoDoors.Level() <= 0)
        {
            return;
        }
        try
        {
            CargoDoors.JettisonNow(destinationIndex);
        }
        catch (Exception e)
        {
            CargoDoors.broken = true;
            CargoDoors.ReportFailureOnce("the departure jettison", e);
        }
    }
}

// Stop a producer whose resource is already at its sealed ceiling, before it
// spends any energy on a batch that would be thrown away.
//
// This mirrors the game's own first line:
//
//     if (outputPercentage == 0.0 || storage.IsResourceFullWithOutput(...))
//         { ...zero everything...; return 0; }
//
// so a sealed resource simply looks full to the source, which is a state the
// source already knows how to be in.
[HarmonyPatch(typeof(ElementSource), "Tick")]
public static class ElementSourceTickPatch
{
    private static bool loggedFailure;

    public static bool Prefix(ElementSource __instance, ref int __result)
    {
        if (CargoDoors.broken || !CargoDoors.Sealed())
        {
            return true;
        }
        try
        {
            ElementBatch output = __instance.baseElementOutputPerFill;
            if (output == null)
            {
                return true;
            }
            ElementType type = output.type;

            // A pocket with room is a valid destination, so the source should
            // run. Only stop it when the output has nowhere at all to go.
            if (QuantumPocket.RoomFor(type) > ScientificNotation.zero)
            {
                return true;
            }

            Player player = Player.shared;
            if (player == null || player.storage == null)
            {
                return true;
            }
            ScientificNotation held = player.storage.GetAmount(type);
            if (held >= CargoDoors.KeepAmount(type))
            {
                __result = 0;
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            if (!loggedFailure)
            {
                loggedFailure = true;
                CargoBayPlugin.Log.LogWarning("Cargo doors could not gate a "
                    + "producer: " + e.Message + ". Production runs as normal.");
            }
            return true;
        }
    }
}

// Hold back what the destination recommends while the sealed hold carries it.
//
// Research.Tick, Infra.Tick and EnergySource.Tick all ask this method how
// much of a batch they may take, passing the lock slider as `filter`, and the
// game reserves cap * filter from them. Raising filter to the recommended
// share is the whole lock: every spender stops at it, by the game's own sums.
//
// A prefix, so AvailablePocketPatch's postfix sees the raised filter too and
// offers only what the pocket holds on top of the reserve.
[HarmonyPatch(typeof(ResourceStorage), "AvailableElementBatchPercentage")]
public static class KeptCargoLockPatch
{
    private static bool loggedFailure;

    public static void Prefix(ElementBatch batch, ref double filter)
    {
        if (batch == null || CargoDoors.broken)
        {
            return;
        }
        try
        {
            double locked = CargoDoors.LockedFraction(batch.type);
            if (locked > filter)
            {
                filter = locked;
            }
        }
        catch (Exception e)
        {
            if (!loggedFailure)
            {
                loggedFailure = true;
                CargoBayPlugin.Log.LogWarning("Cargo doors could not lock "
                    + "the kept cargo: " + e.Message + ". Research can spend "
                    + "it as normal.");
            }
        }
    }
}

// --------------------------------------------------------------------------
// Kills, which is the hole Fuzzied found on 20.09.2026.
//
// "Doors should stop kills from adding too or there is no point as that is
// the main(only realistic) source." He is right, and the numbers say so
// plainly. In space only two things collect, because spaceStar.json has
// exactly two element sources: gas_molecule_collection gives air and
// plantbased_biomass gives biomass at 0.001 per fill. Measured on his own
// game that is 5.85 a second, against 1.3e4 a second for air. The one
// planetary biomass source in the game, Mars's biomass_plantation, runs only
// while standing on Mars, because the active source set is chosen by
// location.stayingStarIndex and Departure sets that to -1.
//
// So on a flight, biomass from the flora is a rounding error and biomass from
// Mars is zero. Kills are the whole story. Sealing the hold against producers
// and leaving the battle path open meant the seal did nothing at all for the
// resource that actually moves.
//
// Only ERIOSource.battle is turned away. Two neighbours deliberately are not:
//
//   Card sales (IdenticalCardSet.ConfirmSellOne and friends) come in on the
//   default ERIOSource.none, but the player pressed a button and confirmed a
//   modal to get them. Eating those would destroy cards for nothing.
//
//   Quest and achievement rewards (Player.AddItemBundle) are also none, and
//   throwing away something earned is not what arming the doors asks for.
//
// The offline path gets its own patch because BattleManager's auto-kill goes
// through AddOfflineBiomass instead, and a night of offline kills is exactly
// the case where the weight would matter most.
// --------------------------------------------------------------------------
[HarmonyPatch(typeof(ResourceStorage), "AddElementBatch")]
public static class BattleIncomeDoorsPatch
{
    private static bool loggedFailure;

    public static bool Prefix(ElementBatch batch, ERIOSource source,
        ref ScientificNotation __result)
    {
        if (batch == null || source != ERIOSource.battle)
        {
            return true;
        }
        try
        {
            if (CargoDoors.RefusesIncome(batch.type))
            {
                __result = ScientificNotation.zero;
                return false;
            }
        }
        catch (Exception e)
        {
            if (!loggedFailure)
            {
                loggedFailure = true;
                CargoBayPlugin.Log.LogWarning("Cargo doors could not gate "
                    + "battle income: " + e.Message + ". Kills pay out as "
                    + "normal.");
            }
        }
        return true;
    }
}

[HarmonyPatch(typeof(ResourceStorage), "AddOfflineBiomass")]
public static class OfflineBattleIncomeDoorsPatch
{
    private static bool loggedFailure;

    public static bool Prefix()
    {
        try
        {
            if (CargoDoors.RefusesIncome(ElementType.biomass))
            {
                return false;
            }
        }
        catch (Exception e)
        {
            if (!loggedFailure)
            {
                loggedFailure = true;
                CargoBayPlugin.Log.LogWarning("Cargo doors could not gate "
                    + "offline battle income: " + e.Message + ". Offline "
                    + "kills pay out as normal.");
            }
        }
        return true;
    }
}
