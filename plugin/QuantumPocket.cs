// Space Travel Idle community mod - Quantum Pocket.
//
// Compiled into CargoBay.dll alongside CargoBay.cs and CargoDoors.cs.
//
// ---------------------------------------------------------------------------
// The problem
//
// Space research is not free, which is easy to assume and wrong. Reading
// spaceStar.json, the space star's research and infrastructure want:
//
//     experienced_fighter          1,000,000 biomass
//     extra_equip_slot_6           2,000,000 silicon
//     bulk_transfer_capacity       1,000,000 silicon
//     interplanetary_knowledge       100,000 iron, per level, 10,000 levels
//     interplanetary_constructions   100,000 iron, per level, 10,000 levels
//     engine_cap_1                   150,000 iron, per level
//
// and iron and silicon are the two heaviest things in the hold (7.874 and 2.33
// per unit, against 0.001275 for air). So the only way to fund space research
// during a flight is to carry the exact cargo that makes the flight take
// weeks. That is not a trade-off, it is a dead end, and it is why a player
// ends up flying empty and doing nothing for the whole journey.
//
// While travelling there is income to be had, too. Location.Departure sets
// stayingStarIndex = -1 and StarMap.GetElementSourceSet returns the last set
// for any negative index, which is the space star, so gas_molecule_collection
// and plantbased_biomass produce the whole way. Battles keep dropping biomass
// through BattleManager. In vanilla all of that lands in the hold and starts
// braking the ship the moment it arrives.
//
// ---------------------------------------------------------------------------
// What this does
//
// A parallel buffer, one number per resource, that is not part of
// ResourceStorage.weight and therefore costs nothing to carry at any fill
// level, ever. While the ship is in flight:
//
//   income   anything that would enter the hold goes into the pocket instead,
//            up to the pocket's capacity. Over capacity it falls through to
//            the hold and behaves exactly as it does today.
//   spending research and infra can spend straight out of it.
//   arrival  the pocket empties into the hold through the game's own path, so
//            the usual cap and discard rules apply to the landing.
//
// Capacity is a share of the hold's own per-resource cap rather than a flat
// number, for the same reason Ballast Trim measures fill against cap: a flat
// number goes stale the moment resource_storage_up is bought, and a share
// never does. The share per level lives in tools/gen_bigbang.py and is read
// back out of the loaded upgrade set, so retuning the line needs no rebuild.
//
// The rule that keeps this from making Ballast Trim pointless: the pocket only
// takes in what you EARN during the flight. There is no way to load it at the
// station. Ballast Trim is about the stock you already have, the pocket is
// about income, and owning one does not answer the question the other asks.
//
// ---------------------------------------------------------------------------
// How the spending works, which is the part that looks harder than it is
//
// Research.Tick and Infra.Tick both spend resources through exactly two calls,
// and EnergySource.Tick uses the same pair:
//
//     storage.AvailableElementBatchPercentage(batch, filter, speed)   how much
//     storage.TakeElementBatch(batch, speed, source)                  take it
//
// So the pocket only has to lie in two places. AvailableElementBatchPercentage
// gets a postfix that answers as though the pocket's contents were in the
// hold, and TakeElementBatch gets a prefix that tops the hold up out of the
// pocket just before the game subtracts from it. The hold stays the register
// everything is counted in and the pocket refills it on demand, which means
// none of the game's own accounting had to be reimplemented.
//
// The income side is a prefix/postfix pair around the game's add methods
// rather than a replacement, for the same reason. The prefix notes what the
// hold holds, the postfix sees what landed and moves that much into the
// pocket. Whatever the game does in between - the gain multipliers, the
// summary bookkeeping, the stats counters, NormalizeResource's clipping -
// happens exactly as it always did, and the resource is relocated afterwards.
//
// ---------------------------------------------------------------------------
// Persistence
//
// A plugin cannot add a field to the save file, so the pocket lives in a small
// sidecar next to it: fourteen numbers and three identifiers, in
// Application.persistentDataPath. It is keyed to the FLIGHT rather than the
// save slot - departure star, destination star, and how far along the trip was
// when it was written - because a save slot is not a run. Restoring is refused
// unless the player is travelling the same leg and is no further back than the
// file says, which makes the failure mode "the pocket is empty", never "the
// pocket belongs to a different game".

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using LargeNumbers;
using UnityEngine;

public static class QuantumPocket
{
    public const string UPGRADE_ID = "quantum_pocket";

    private const string SIDECAR = "community_quantum_pocket.txt";

    // Capacity per level, as a percent of the hold's per-resource cap. Only
    // used when the data file cannot be read; the real numbers live in
    // tools/gen_bigbang.py and are read out of the loaded upgrade set below.
    private static readonly double[] FALLBACK_PERCENT =
    {
        2.0, 4.0, 6.0, 9.0, 12.0, 16.0, 21.0, 27.0, 34.0,
        42.0, 52.0, 64.0, 78.0, 90.0, 100.0
    };

    private const int KINDS = 7;

    // Indexed by (int)ElementType: air, water, soil, biomass, coal, silicon,
    // iron. A plain array rather than a dictionary because TakeElementBatch
    // runs on a physics tick.
    private static readonly ScientificNotation[] held = new ScientificNotation[KINDS];

    private static int cachedLevel = -1;
    private static double cachedPercent;
    private static bool cachedFromData;

    private static bool loggedFailure;
    internal static bool broken;

    // Set while the pocket is emptying itself into the hold on arrival, so the
    // income patches do not put it straight back.
    internal static bool flushing;

    private static bool restoreAttempted;
    private static int dirtyTicks;

    // What the last arrival put in the hold. The travel panel has nothing to
    // say about the pocket once the ship is on the ground, which is exactly
    // the moment the player wants to know whether the trip paid. Fuzzied, on
    // landing: "I could not see if it moved the content into the hold or its
    // status."
    internal static ScientificNotation lastLanded = ScientificNotation.zero;

    // The same arrival, broken down by resource, because "6,8e8 units" tells
    // the player nothing about what they actually brought home. Fuzzied, on the
    // readout: "Showing one for air and one for Biomass."
    internal static readonly ScientificNotation[] landed =
        new ScientificNotation[KINDS];

    // What went past a full pocket and into the hold on this flight, per
    // resource. Recorded the moment it happens rather than worked out later,
    // because Absorb already knows the difference between what was offered and
    // what it could take. This is what turns "your speed is falling" from a
    // guess into a number: see TrySpeedCostPercent below.
    // Seconds since each resource last had something go past the pocket,
    // counted by SecondTick. NEVER means not on this flight.
    //
    // spilled[] is a running total and answers "did anything ever spill".
    // Every sentence on the two panels is in the present tense and wants a
    // different question, "is anything spilling now", and the whole of
    // session H is named after the gap between those two. The first attempt
    // at closing it asked whether the pocket was full instead, which is
    // better but still a state and not a rate: stop the collectors mid
    // flight and the pocket stays full while nothing whatsoever spills.
    // This is the honest version. It is a measurement of the thing itself.
    private const int NEVER = 9999;
    private const int GRACE = 3;
    private static readonly int[] spillAge = FreshAges();

    // A zeroed int array would read as "spilled this very second" for all
    // seven before a flight has even started. ResetFlight puts it right on
    // every load, but a default that lies is not worth relying on.
    private static int[] FreshAges()
    {
        int[] ages = new int[KINDS];
        for (int i = 0; i < KINDS; i++)
        {
            ages[i] = NEVER;
        }
        return ages;
    }

    private static readonly ScientificNotation[] spilled =
        new ScientificNotation[KINDS];

    // Whether this resource has moved at all on this flight, so a row can be
    // shown for the two things space actually pays and not for the five that
    // will sit at zero all trip.
    private static readonly bool[] seen = new bool[KINDS];

    // Net change per second, smoothed. Net rather than income, on purpose: a
    // pocket that research is draining faster than the collectors fill it is
    // never going to fill, and the readout should not promise that it will.
    private static readonly ScientificNotation[] rate =
        new ScientificNotation[KINDS];
    private static readonly ScientificNotation[] lastSample =
        new ScientificNotation[KINDS];
    private static bool sampled;

    /// <summary>What one resource held, for a per line readout.</summary>
    public static ScientificNotation HeldAt(int index)
    {
        return (index < 0 || index >= KINDS)
            ? ScientificNotation.zero : held[index];
    }

    /// <summary>What one resource delivered on the last arrival.</summary>
    public static ScientificNotation LandedAt(int index)
    {
        return (index < 0 || index >= KINDS)
            ? ScientificNotation.zero : landed[index];
    }

    /// <summary>Net change per second for one resource, zero when falling.</summary>
    public static ScientificNotation RateAt(int index)
    {
        if (index < 0 || index >= KINDS)
        {
            return ScientificNotation.zero;
        }
        return (rate[index] > ScientificNotation.zero)
            ? rate[index] : ScientificNotation.zero;
    }

    /// <summary>
    /// Whether this resource earns a row on the readout. Anything it holds,
    /// anything it has caught this flight, or anything it has let through.
    /// </summary>
    public static bool Shows(int index)
    {
        if (index < 0 || index >= KINDS)
        {
            return false;
        }
        return seen[index] || held[index] > ScientificNotation.zero
            || spilled[index] > ScientificNotation.zero;
    }

    /// <summary>
    /// Note that the pocket could not take all of what arrived, so the rest
    /// stayed in the hold. Called with the difference, which is usually zero.
    /// </summary>
    internal static void NoteSpill(ElementType type, ScientificNotation missed)
    {
        if (missed > ScientificNotation.zero)
        {
            // A single spill bigger than the whole pocket did not trickle in,
            // it arrived in a lump, and a lump is worth knowing about. The
            // offline boss auto-kill hands over a night's biomass in one call
            // and the pocket can only ever take one pocketful of it, so the
            // total jumps by billions in a single tick and then never moves
            // again. Fuzzied had exactly that on save B on 21.09.2026 and the
            // only way to tell it apart from a slow leak was to read the file
            // twice twenty seconds apart. Once per flight, so a genuinely
            // leaky trip does not fill the log.
            if (!loggedLump && missed > Capacity())
            {
                loggedLump = true;
                try
                {
                    CargoBayPlugin.Log.LogInfo("Quantum pocket: " + missed
                        + " of " + type + " went past in one go, which is more "
                        + "than the whole pocket holds (" + Capacity()
                        + "). Something handed the hold a lump rather than a "
                        + "trickle, most likely offline catch up.");
                }
                catch (Exception)
                {
                }
            }
            spilled[(int)type] += missed;
            spillAge[(int)type] = 0;
            // The sidecar carries these totals now, and a spill moves nothing
            // in or out of the pocket, so without this the only thing that
            // ever marked the file dirty was the pocket's own contents. A
            // pocket sitting full while everything overflows past it would
            // have written nothing at all, which is precisely the flight
            // where the totals matter most.
            dirtyTicks = 1;
        }
    }

    /// <summary>
    /// True when this resource has gone past the pocket in the last few
    /// seconds, which is the only honest basis for a sentence written in the
    /// present tense. GRACE is wide enough that a quiet tick between two
    /// income ticks does not make the readout blink.
    /// </summary>
    public static bool SpillingNow(int index)
    {
        return index >= 0 && index < KINDS && spillAge[index] <= GRACE;
    }

    /// <summary>
    /// True when anything at all is going past the pocket right now. The
    /// speed row asks this rather than the per resource version, because the
    /// ship only has one speed and it does not care which resource is the one
    /// weighing it down.
    /// </summary>
    public static bool AnythingSpillingNow()
    {
        for (int i = 0; i < KINDS; i++)
        {
            if (spillAge[i] <= GRACE)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// How much speed the overflow has cost, as a percent, or zero when
    /// nothing has spilled.
    /// </summary>
    /// <remarks>
    /// Engine.estimatedSpeed is sqrt(energy * modifier / totalWeight), so
    /// speed goes as one over the square root of weight and the cost is
    /// 1 - sqrt(weightWithout / weightNow). Modifiers.spaceshipDieting scales
    /// both halves of that fraction and cancels, so this works in raw weight
    /// and never has to read the modifier.
    ///
    /// This exists because the honest answer is usually "nothing". A hold
    /// carrying 5.58e9 iron at 7.874 a unit weighs 4.4e10, and air at 0.001275
    /// cannot move that in a lifetime of flying. Printing a red warning there
    /// would be crying wolf, which is exactly the mistake the 20.09.2026 test
    /// session caught me making.
    ///
    /// The answer and whether there is one are two separate values on
    /// purpose. This used to return a plain double and hand 0.0 back from
    /// five different guards, one of them the case where the overflow
    /// outweighs the entire ship. Zero is also what a genuinely trivial
    /// overflow returns, so the panel said "too little of it to change the
    /// ship's speed" at the exact moment the overflow was the heaviest thing
    /// aboard. A sentinel that happens to equal a plausible answer is the
    /// worst kind, because nothing downstream can tell the two apart.
    ///
    /// False means there is no honest figure. It does not mean zero, and a
    /// caller that treats it as zero is back where this started.
    /// </remarks>
    public static bool TrySpeedCostPercent(out double percent)
    {
        percent = 0.0;
        try
        {
            Player player = Player.shared;
            if (player == null || player.storage == null
                || player.spaceship == null)
            {
                return false;
            }
            ScientificNotation spill = ScientificNotation.zero;
            for (int i = 0; i < KINDS; i++)
            {
                if (spilled[i] > ScientificNotation.zero)
                {
                    // Only the part of it that is still aboard. spilled[] is a
                    // running total of everything that went past the pocket on
                    // this leg, and that is the right figure for a history
                    // line, but it is the wrong one for a speed cost. Overflow
                    // you have since spent is not weighing you down any more.
                    //
                    // Fuzzied hit this on save B on 21.09.2026. The offline boss
                    // auto-kill handed him 5,57e9 of biomass in one lump at
                    // load, all of it past a pocket that holds 1,25e8, and the
                    // biomass power plant burned every unit of it for energy
                    // within seconds. The hold reads 0 biomass and the total
                    // still read 5,57e9, which outweighed the entire ship, so
                    // the guard below correctly refused to quote a percentage
                    // and the panel went quiet about a ship that was fine.
                    //
                    // The hold's current contents are the honest ceiling: none
                    // of the overflow can still be aboard if the resource is
                    // not. It can understate, if you spent units that were
                    // never overflow to begin with, and that is the safe
                    // direction. Understating a penalty never claims a cost
                    // you are not paying.
                    ScientificNotation aboard = InHold(player, (ElementType)i);
                    ScientificNotation counted =
                        (aboard < spilled[i]) ? aboard : spilled[i];
                    if (!(counted > ScientificNotation.zero))
                    {
                        continue;
                    }
                    // Through ElementBatch.weight rather than a table of our
                    // own, so a balance change to the game's weights is picked
                    // up without a rebuild.
                    spill += new ElementBatch((ElementType)i, counted).weight;
                }
            }
            if (!(spill > ScientificNotation.zero))
            {
                // Nothing has spilled. Zero really is the answer here, and it
                // is the one case in this method where it is.
                return true;
            }
            ScientificNotation now = player.spaceship.weight + player.storage.weight;
            if (!(now > spill))
            {
                // The overflow weighs as much as the whole ship or more. The
                // formula has nothing to divide by, and this is the heaviest
                // the overflow will ever be, so it is the last moment to
                // claim it costs nothing.
                LogTheFailureOnce(spill, now, 0.0);
                return false;
            }
            double ratio = ((now - spill) / now).Standard();
            if (ratio >= 1.0)
            {
                // The line above proved now > spill, so the true ratio is
                // below 1 and this can only be the division rounding a very
                // small spill away against a very large hold. That is an
                // honest zero and it has a sentence of its own. Returning
                // false here sent it to the no figure branch instead, which
                // is the branch reserved for an overflow heavier than the
                // ship: the two extremes swapped places. Fuzzied caught it on
                // save B on 21.09.2026, where a reload had reset the spill
                // counter mid leg and the panel then refused to quote a
                // percentage it could easily have worked out.
                percent = 0.0;
                return true;
            }
            if (ratio <= 0.0)
            {
                // Same rounding from the other end, spill so close to the
                // whole ship that the difference vanishes. Genuinely no
                // figure, and the caller says so rather than guessing.
                LogTheFailureOnce(spill, now, ratio);
                return false;
            }
            percent = (1.0 - Math.Sqrt(ratio)) * 100.0;
            LogTheWorkingOnce(spill, now, percent);
            return true;
        }
        catch (Exception)
        {
            // A readout is not worth a throw on a physics tick.
            percent = 0.0;
            return false;
        }
    }

    // Set once a flight, so the breakdown lands in the log the first time the
    // overflow is big enough to report and not on every frame after that.
    private static bool loggedWorking;

    private static bool loggedNoFigure;
    private static bool loggedLump;

    /// <summary>
    /// Say in the log why there is no figure, once per flight.
    /// </summary>
    /// <remarks>
    /// The two failing guards used to return false in silence, so the only
    /// evidence that reached anybody was a sentence on the panel with no
    /// number in it, and that sentence cannot say which guard produced it.
    /// Fuzzied hit exactly that on save B on 21.09.2026 and the diagnosis took
    /// a reading of the source rather than a reading of the log, which is the
    /// wrong way round. A refusal is a result and it gets written down.
    /// </remarks>
    private static void LogTheFailureOnce(ScientificNotation spill,
        ScientificNotation now, double ratio)
    {
        if (loggedNoFigure)
        {
            return;
        }
        loggedNoFigure = true;
        try
        {
            CargoBayPlugin.Log.LogInfo(
                "Quantum pocket: no speed figure this flight. The overflow "
                + "weighs " + spill + " against a ship and hold of " + now
                + ", so the ratio came out at " + ratio.ToString("0.000000",
                    CultureInfo.InvariantCulture)
                + ". Anything at or below zero means the overflow is as heavy "
                + "as the whole ship, which is the case with no honest "
                + "answer.");
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Put the overflow arithmetic in the log, once per flight.
    /// </summary>
    /// <remarks>
    /// The first build of this readout claimed 4.1% on a ship where the hold
    /// barely moved, and there was no way to tell from the outside whether the
    /// number was right or the counter was. A percent on its own is not
    /// evidence. The units that produced it are.
    /// </remarks>
    private static void LogTheWorkingOnce(ScientificNotation spill,
        ScientificNotation now, double percent)
    {
        // Not until the figure is one the player would actually be shown.
        // The first version logged on the very first tick with a spill of
        // 5,21e4 and 0.00%, set its flag, and then sat silent through the
        // 3,53e9 of biomass that arrived a moment later, which was the one
        // number worth having in the log.
        if (loggedWorking || percent < 0.05)
        {
            return;
        }
        loggedWorking = true;
        try
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("Quantum pocket overflow: ")
              .Append(percent.ToString("0.00",
                  System.Globalization.CultureInfo.InvariantCulture))
              .Append("% speed, spilled weight ").Append(spill.ToString())
              .Append(" of ").Append(now.ToString()).Append(" carried.");
            for (int i = 0; i < KINDS; i++)
            {
                if (spilled[i] > ScientificNotation.zero)
                {
                    sb.Append(" ").Append(((ElementType)i).ToString())
                      .Append(" ").Append(spilled[i].ToString())
                      .Append(" units weighing ")
                      .Append(new ElementBatch((ElementType)i,
                          spilled[i]).weight.ToString()).Append(".");
                }
            }
            CargoBayPlugin.Log.LogInfo(sb.ToString());
        }
        catch (Exception)
        {
            // Diagnostics are never worth a throw.
        }
    }

    /// <summary>
    /// How much of one resource is in the hold right now. Zero when the game
    /// has no batch for it, which is the same thing said a different way.
    /// </summary>
    private static ScientificNotation InHold(Player player, ElementType type)
    {
        if (player == null || player.storage == null
            || player.storage.elementDict == null)
        {
            return ScientificNotation.zero;
        }
        ElementBatch batch;
        if (!player.storage.elementDict.TryGetValue(type, out batch)
            || batch == null)
        {
            return ScientificNotation.zero;
        }
        return batch.amount;
    }

    /// <summary>True when this one resource has gone past the pocket.</summary>
    public static bool HasSpilledAt(int index)
    {
        return index >= 0 && index < KINDS
            && spilled[index] > ScientificNotation.zero;
    }

    /// <summary>True when anything at all has gone past the pocket.</summary>
    public static bool HasSpilled()
    {
        for (int i = 0; i < KINDS; i++)
        {
            if (spilled[i] > ScientificNotation.zero)
            {
                return true;
            }
        }
        return false;
    }

    public static int Level()
    {
        BigBangManager manager = BigBangManager.shared;
        if (manager == null || manager.upgradeLevels == null)
        {
            return 0;
        }
        return manager.GetUpgradeLevel(UPGRADE_ID);
    }

    /// <summary>Pocket capacity at this level, as a percent of the hold's cap.</summary>
    public static double Percent(int level, out bool fromData)
    {
        fromData = false;
        if (level <= 0)
        {
            return 0.0;
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
                        && parsed > 0.0)
                    {
                        fromData = true;
                        return parsed;
                    }
                }
            }
        }
        catch (Exception)
        {
            // tree not loaded yet, or the line is not installed
        }
        if (level <= FALLBACK_PERCENT.Length)
        {
            return FALLBACK_PERCENT[level - 1];
        }
        return FALLBACK_PERCENT[FALLBACK_PERCENT.Length - 1];
    }

    /// <summary>How much of one resource the pocket can hold in total.</summary>
    public static ScientificNotation Capacity()
    {
        int level = Level();
        if (level <= 0)
        {
            return ScientificNotation.zero;
        }
        if (level != cachedLevel || !cachedFromData)
        {
            bool fromData;
            cachedPercent = Percent(level, out fromData);
            cachedLevel = level;
            cachedFromData = fromData;
        }
        return ResourceStorage.cap * (cachedPercent / 100.0);
    }

    /// <summary>True while the pocket is catching things: owned, on, in flight.</summary>
    public static bool Active()
    {
        if (broken || flushing || !CargoBayPlugin.cfgQuantumPocket.Value
            || Level() <= 0)
        {
            return false;
        }
        Player player = Player.shared;
        return player != null && player.location != null
            && player.location.isTravelling;
    }

    public static ScientificNotation Held(ElementType type)
    {
        return held[(int)type];
    }

    /// <summary>
    /// Space left for this resource, zero when the pocket is not active.
    /// </summary>
    /// <remarks>
    /// A crumb of room is not room. Absorb tops the pocket up to exactly its
    /// cap, and subtracting two ScientificNotation values that should cancel
    /// leaves a residue about fifteen digits down: on save B on 21.09.2026 the
    /// biomass pocket sat at 1,24799999999999e8 against a cap of 1,248e8, a gap
    /// of a millionth of a unit. Every question downstream reads that as room.
    /// Absorb then moves a millionth of a unit per tick and spills the rest,
    /// which is the right behaviour, while the row stops saying full and the
    /// doors stop saying they are refusing, which is not. Anything under a
    /// billionth of capacity is treated as no room at all, because nothing the
    /// game produces arrives in quantities that small.
    /// </remarks>
    public static ScientificNotation RoomFor(ElementType type)
    {
        if (!Active())
        {
            return ScientificNotation.zero;
        }
        ScientificNotation capacity = Capacity();
        ScientificNotation room = capacity - held[(int)type];
        return (room > capacity * 1e-9) ? room : ScientificNotation.zero;
    }

    /// <summary>
    /// Move up to <paramref name="amount"/> of this resource out of the hold
    /// and into the pocket. Returns what actually moved, which is less than
    /// asked for when the pocket is full.
    /// </summary>
    internal static ScientificNotation Absorb(ElementType type,
        ScientificNotation amount)
    {
        if (!(amount > ScientificNotation.zero))
        {
            return ScientificNotation.zero;
        }
        ScientificNotation room = RoomFor(type);
        if (!(room > ScientificNotation.zero))
        {
            return ScientificNotation.zero;
        }
        ScientificNotation moved = (amount < room) ? amount : room;

        Player player = Player.shared;
        if (player == null || player.storage == null)
        {
            return ScientificNotation.zero;
        }
        ElementBatch batch;
        if (!player.storage.elementDict.TryGetValue(type, out batch)
            || batch == null)
        {
            return ScientificNotation.zero;
        }
        if (batch.amount < moved)
        {
            moved = batch.amount;
        }
        if (!(moved > ScientificNotation.zero))
        {
            return ScientificNotation.zero;
        }

        // Straight off the field rather than through TakeElementBatch: this is
        // not a loss, it is the same units in a different place, and routing it
        // through the summary would show the player a discard that never
        // happened.
        batch.amount -= moved;
        held[(int)type] += moved;
        seen[(int)type] = true;
        dirtyTicks = 1;
        return moved;
    }

    // The reserve the last availability question worked out, handed to the
    // TakeElementBatch that follows it. Every spender asks and then takes in
    // the same breath for the same resource, so one slot is enough. It is
    // good once and only in the frame it was noted, so a take with no
    // question in front of it (alchemy, the jettison) never sees a stale one.
    private static int reserveType = -1;
    private static int reserveFrame = -1;
    private static ScientificNotation reserveAmount = ScientificNotation.zero;

    internal static void NoteReserve(ElementType type, ScientificNotation amount)
    {
        reserveType = (int)type;
        reserveFrame = Time.frameCount;
        reserveAmount = amount;
    }

    internal static ScientificNotation TakeNotedReserve(ElementType type)
    {
        bool fresh = reserveType == (int)type && reserveFrame == Time.frameCount;
        reserveType = -1;
        return fresh ? reserveAmount : ScientificNotation.zero;
    }

    /// <summary>
    /// Move up to <paramref name="amount"/> back out of the pocket and into the
    /// hold, which is how spending works: the hold is topped up just before the
    /// game subtracts from it.
    /// </summary>
    internal static ScientificNotation Release(ElementType type,
        ScientificNotation amount)
    {
        if (!(amount > ScientificNotation.zero))
        {
            return ScientificNotation.zero;
        }
        ScientificNotation stored = held[(int)type];
        if (!(stored > ScientificNotation.zero))
        {
            return ScientificNotation.zero;
        }
        ScientificNotation moved = (amount < stored) ? amount : stored;

        Player player = Player.shared;
        if (player == null || player.storage == null)
        {
            return ScientificNotation.zero;
        }
        ElementBatch batch;
        if (!player.storage.elementDict.TryGetValue(type, out batch)
            || batch == null)
        {
            return ScientificNotation.zero;
        }
        batch.amount += moved;
        held[(int)type] -= moved;
        dirtyTicks = 1;
        return moved;
    }

    /// <summary>Everything in the pocket, for the travel panel readout.</summary>
    public static ScientificNotation TotalHeld()
    {
        ScientificNotation total = ScientificNotation.zero;
        for (int i = 0; i < KINDS; i++)
        {
            total += held[i];
        }
        return total;
    }

    /// <summary>
    /// Everything that has gone past the pocket on this leg. Only the log uses
    /// it, and only to say what was carried over from before a restart, but a
    /// restore that quietly reads nothing and a restore that reads a real
    /// number look identical without it.
    /// </summary>
    public static ScientificNotation TotalSpilled()
    {
        ScientificNotation total = ScientificNotation.zero;
        for (int i = 0; i < KINDS; i++)
        {
            total += spilled[i];
        }
        return total;
    }

    /// <summary>
    /// Empty the pocket into the hold. Called from Location.Arrive, which has
    /// already cleared isTravelling by the time the postfix runs, so Active()
    /// is false and nothing catches it on the way back in. The flushing flag
    /// covers the case where that ever stops being true.
    /// </summary>
    internal static void FlushToHold()
    {
        Player player = Player.shared;
        if (player == null || player.storage == null)
        {
            Clear();
            return;
        }
        flushing = true;
        try
        {
            ScientificNotation landedTotal = ScientificNotation.zero;
            for (int i = 0; i < KINDS; i++)
            {
                landed[i] = held[i];
                if (!(held[i] > ScientificNotation.zero))
                {
                    continue;
                }
                // AddElementBatchRaw, not AddElementBatch: the gain multipliers
                // were already applied when this was earned, and applying them
                // a second time on the way out of the pocket would pay the
                // player twice. Raw still calls NormalizeResource, so the cap
                // and the discard slider get their say on the landing.
                player.storage.AddElementBatchRaw(new ElementBatch(
                    (ElementType)i, held[i]));
                landedTotal += held[i];
                held[i] = ScientificNotation.zero;
            }
            lastLanded = landedTotal;
            if (landedTotal > ScientificNotation.zero)
            {
                CargoBayPlugin.Log.LogInfo("Quantum pocket: delivered "
                    + landedTotal + " units into the hold on arrival");
            }
        }
        finally
        {
            flushing = false;
            dirtyTicks = 1;
            ResetFlight();
            DeleteSidecar();
        }
    }

    /// <summary>
    /// Forget everything that is true of one flight and not of the next: what
    /// spilled, what moved, and how fast. The contents are not touched here,
    /// because the two callers that want them gone say so themselves.
    /// </summary>
    private static void ResetFlight()
    {
        for (int i = 0; i < KINDS; i++)
        {
            spilled[i] = ScientificNotation.zero;
            spillAge[i] = NEVER;
            seen[i] = false;
            rate[i] = ScientificNotation.zero;
            lastSample[i] = ScientificNotation.zero;
        }
        sampled = false;
        loggedWorking = false;
        loggedNoFigure = false;
        loggedLump = false;
    }

    /// <summary>
    /// One second of fill rate, smoothed. Kills arrive in lumps and a raw
    /// delta would show a number that was true for one second and misleading
    /// for the rest, so this leans on the running figure.
    /// </summary>
    private static void Sample()
    {
        for (int i = 0; i < KINDS; i++)
        {
            if (sampled)
            {
                rate[i] = rate[i] * 0.6 + (held[i] - lastSample[i]) * 0.4;
            }
            lastSample[i] = held[i];
        }
        sampled = true;
    }

    internal static void Clear()
    {
        for (int i = 0; i < KINDS; i++)
        {
            held[i] = ScientificNotation.zero;
            landed[i] = ScientificNotation.zero;
        }
        ResetFlight();
        dirtyTicks = 0;
        lastLanded = ScientificNotation.zero;
        DeleteSidecar();
    }

    /// <summary>
    /// Empty the pocket because a different save is being loaded.
    /// </summary>
    /// <remarks>
    /// The pocket is static, so it outlives the save that filled it. Loading
    /// one save while another is mid flight used to carry the first save's
    /// pocket straight into the second one. The restore flag matters just as
    /// much as the contents: it is only cleared while standing on a planet,
    /// so a flight loaded on top of a flight would never look at its own
    /// sidecar. The sidecar itself is left alone on purpose. It might belong
    /// to the save now being loaded.
    /// </remarks>
    internal static void ResetForLoad()
    {
        for (int i = 0; i < KINDS; i++)
        {
            held[i] = ScientificNotation.zero;
            landed[i] = ScientificNotation.zero;
        }
        ResetFlight();
        dirtyTicks = 0;
        restoreAttempted = false;
        lastLanded = ScientificNotation.zero;
    }

    // -----------------------------------------------------------------------
    // Sidecar
    // -----------------------------------------------------------------------

    private static string SidecarPath()
    {
        return Path.Combine(Application.persistentDataPath, SIDECAR);
    }

    /// <summary>
    /// Identifies the leg being flown, so a pocket cannot be restored onto a
    /// different journey. Null when not travelling.
    /// </summary>
    private static string LegKey(Location location)
    {
        if (location == null || !location.isTravelling)
        {
            return null;
        }
        return location.departureStarIndex.ToString(CultureInfo.InvariantCulture)
            + ">" + location.destinationStarIndex.ToString(CultureInfo.InvariantCulture);
    }

    internal static void SaveSidecar()
    {
        try
        {
            Player player = Player.shared;
            if (player == null)
            {
                return;
            }
            string leg = LegKey(player.location);
            if (leg == null)
            {
                DeleteSidecar();
                return;
            }

            StringBuilder sb = new StringBuilder();
            // v2 adds seven more lines after the pocket contents: what has
            // gone PAST the pocket on this leg, per resource. Without them a
            // reload mid flight left the speed cost reading from zero, so a
            // ship genuinely slowed by an earlier overflow was told the
            // overflow was too small to matter. Fuzzied saw exactly that on
            // save B on 21.09.2026, on a hold carrying 2,17e8 of air that had
            // spilled before he reloaded.
            sb.Append("v2\n");
            sb.Append(leg).Append("\n");
            sb.Append(player.location.distanceTravelled.coefficient
                .ToString("R", CultureInfo.InvariantCulture));
            sb.Append(" ").Append(player.location.distanceTravelled.magnitude
                .ToString(CultureInfo.InvariantCulture)).Append("\n");
            for (int i = 0; i < KINDS; i++)
            {
                AppendPair(sb, held[i]);
            }
            for (int i = 0; i < KINDS; i++)
            {
                AppendPair(sb, spilled[i]);
            }
            File.WriteAllText(SidecarPath(), sb.ToString());
        }
        catch (Exception e)
        {
            ReportFailureOnce("writing the pocket sidecar", e);
        }
    }

    private static void DeleteSidecar()
    {
        try
        {
            string path = SidecarPath();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // a stale sidecar is refused on its leg key anyway
        }
    }

    /// <summary>
    /// Read the pocket back after a restart, once, and only if the player is
    /// still flying the leg it was written on and has not gone backwards. Any
    /// doubt at all and the pocket starts empty, which costs the player what
    /// was in it and cannot corrupt anything.
    /// </summary>
    /// <remarks>
    /// Declining does not delete. Every refusal below used to delete the file
    /// on the way out, which quietly made "this pocket is not mine" mean "so
    /// nobody may have it".
    ///
    /// Fuzzied hit the harm on 21.09.2026. His save B is staged at the very
    /// start of the leg to Uranus, distanceTravelled 0, so loading it rewinds
    /// the ship behind the sidecar his live run had written an hour further
    /// along. The distance guard correctly said the file was not this ship's
    /// pocket, and then deleted it. Resuming the live run afterwards found
    /// nothing to restore, because merely looking at another save had thrown
    /// its pocket away.
    ///
    /// Leaving the file costs nothing. Whichever save is actually flying
    /// rewrites it within a second, landing deletes it, and a file for another
    /// leg is refused on the leg key every time it is read. So a refusal is
    /// now just a refusal.
    /// </remarks>
    internal static void TryRestoreOnce()
    {
        if (restoreAttempted)
        {
            return;
        }
        restoreAttempted = true;
        try
        {
            string path = SidecarPath();
            if (!File.Exists(path))
            {
                LogRestore("there was no file to read");
                return;
            }
            string[] lines = File.ReadAllText(path)
                .Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            // v1 is still read. A player mid flight when they install this
            // build has a v1 file on disk and there is no reason to throw
            // their pocket away over a format they never chose. They lose the
            // spill totals for that one leg, which is what v1 never had.
            string stamp = lines[0].Trim();
            bool hasSpill = stamp == "v2";
            if ((stamp != "v1" && !hasSpill)
                || lines.Length < 3 + KINDS
                || (hasSpill && lines.Length < 3 + KINDS + KINDS))
            {
                LogRestore("the file says '" + stamp + "' and has "
                    + lines.Length + " line(s), which is not a shape this "
                    + "build knows how to read");
                return;
            }

            Player player = Player.shared;
            string leg = (player == null) ? null : LegKey(player.location);
            if (leg == null || leg != lines[1].Trim())
            {
                LogRestore("the file belongs to leg '" + lines[1].Trim()
                    + "' and this ship is on '" + (leg ?? "nowhere") + "'");
                return;
            }

            ScientificNotation writtenAt = ParsePair(lines[2]);
            if (player.location.distanceTravelled < writtenAt)
            {
                // An older save was loaded onto the same leg. The pocket in the
                // file is from further along the trip than the game now is, so
                // it is not this flight's pocket.
                LogRestore("the file was written at " + writtenAt
                    + " along the leg and this ship has only reached "
                    + player.location.distanceTravelled
                    + ", so an older save was loaded onto the same leg");
                return;
            }

            for (int i = 0; i < KINDS; i++)
            {
                held[i] = ParsePair(lines[3 + i]);
            }
            if (hasSpill)
            {
                for (int i = 0; i < KINDS; i++)
                {
                    spilled[i] = ParsePair(lines[3 + KINDS + i]);
                }
            }
            // spillAge stays at NEVER on purpose. The totals say what this leg
            // has lost, which survives a restart. Whether anything is going
            // past the pocket at this second does not: the game has only just
            // started running again and has not seen a single tick yet. Left
            // at NEVER the panel says "in the hold" until something actually
            // spills, and then it says "spilling" on its own within a second.
            CargoBayPlugin.Log.LogInfo("Quantum pocket: restored " + TotalHeld()
                + " units carried over from before the restart, and "
                + TotalSpilled() + " units that went past it earlier on this "
                + "leg");
        }
        catch (Exception e)
        {
            Clear();
            ReportFailureOnce("reading the pocket sidecar", e);
        }
    }

    /// <summary>
    /// Say why the pocket did not come back.
    /// </summary>
    /// <remarks>
    /// Every refusal in TryRestoreOnce used to return in silence, so a launch
    /// where nothing was restored and a launch where there was nothing to
    /// restore read identically in the log: as nothing at all. On 21.09.2026
    /// that cost an hour of guessing at which of four guards had fired, on a
    /// build whose whole point was the restore. Same lesson as the speed
    /// figure, one file over. A refusal is a result and it gets written down.
    /// </remarks>
    private static void LogRestore(string why)
    {
        try
        {
            CargoBayPlugin.Log.LogInfo(
                "Quantum pocket: nothing restored, because " + why + ".");
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// One value as "coefficient magnitude", the only shape the sidecar uses.
    /// The round trip partner of ParsePair, and written once rather than
    /// three times so the two can never drift apart.
    /// </summary>
    private static void AppendPair(StringBuilder sb, ScientificNotation value)
    {
        sb.Append(value.coefficient.ToString("R", CultureInfo.InvariantCulture));
        sb.Append(" ");
        sb.Append(value.magnitude.ToString(CultureInfo.InvariantCulture));
        sb.Append("\n");
    }

    private static ScientificNotation ParsePair(string line)
    {
        string[] parts = line.Trim().Split(' ');
        if (parts.Length != 2)
        {
            return ScientificNotation.zero;
        }
        double coefficient;
        int magnitude;
        if (!double.TryParse(parts[0], NumberStyles.Float,
                CultureInfo.InvariantCulture, out coefficient)
            || !int.TryParse(parts[1], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out magnitude))
        {
            return ScientificNotation.zero;
        }
        return new ScientificNotation(coefficient, magnitude);
    }

    /// <summary>
    /// Called once a second from the plugin's own component. Writes the
    /// sidecar only when something moved, so an idle flight does no file IO at
    /// all and a busy one does one small write a second.
    /// </summary>
    internal static void SecondTick()
    {
        if (broken)
        {
            return;
        }
        Player player = Player.shared;
        if (player == null || player.location == null)
        {
            return;
        }
        if (player.location.isTravelling)
        {
            TryRestoreOnce();
            Sample();
            for (int i = 0; i < KINDS; i++)
            {
                if (spillAge[i] < NEVER)
                {
                    spillAge[i]++;
                }
            }
        }
        else
        {
            // Back on the ground, so the next flight gets a fresh look at the
            // file rather than this session's answer.
            restoreAttempted = false;
        }
        if (dirtyTicks > 0)
        {
            dirtyTicks = 0;
            SaveSidecar();
        }
    }

    internal static void ReportFailureOnce(string what, Exception e)
    {
        if (loggedFailure)
        {
            return;
        }
        loggedFailure = true;
        CargoBayPlugin.Log.LogWarning("Quantum pocket turned itself off after "
            + what + " failed: " + e.Message
            + ". Resources go straight into the hold again.");
    }
}

// -------------------------------------------------------------------------
// Income: everything that enters the hold in flight is relocated afterwards
// -------------------------------------------------------------------------
//
// Prefix notes the hold, postfix sees what landed and moves it. Doing it
// around the game's own method rather than instead of it means the gain
// multipliers, the storage summary, the stats counters and NormalizeResource's
// clipping all still happen exactly as they did - the units are simply
// somewhere else a moment later.

[HarmonyPatch(typeof(ResourceStorage), "AddElementBatch")]
public static class AddElementBatchPocketPatch
{
    public static void Prefix(ElementBatch batch, ref ScientificNotation __state)
    {
        __state = PocketIncome.NotWatching;
        if (batch == null || !QuantumPocket.Active())
        {
            return;
        }
        __state = PocketIncome.Before(batch.type);
    }

    public static void Postfix(ElementBatch batch, ScientificNotation __state)
    {
        if (batch == null)
        {
            return;
        }
        PocketIncome.After(batch.type, __state);
    }
}

[HarmonyPatch(typeof(ResourceStorage), "AddElementBatchRaw")]
public static class AddElementBatchRawPocketPatch
{
    public static void Prefix(ElementBatch batch, ref ScientificNotation __state)
    {
        __state = PocketIncome.NotWatching;
        if (batch == null || !QuantumPocket.Active())
        {
            return;
        }
        __state = PocketIncome.Before(batch.type);
    }

    public static void Postfix(ElementBatch batch, ScientificNotation __state)
    {
        if (batch == null)
        {
            return;
        }
        PocketIncome.After(batch.type, __state);
    }
}

// The offline boss auto-kill path writes the field directly rather than going
// through AddElementBatch, so it needs its own pair or a night's worth of
// offline biomass would land in the hold and brake the catch-up - which is the
// exact bug offline-fix was written for.
[HarmonyPatch(typeof(ResourceStorage), "AddOfflineBiomass")]
public static class AddOfflineBiomassPocketPatch
{
    public static void Prefix(ref ScientificNotation __state)
    {
        __state = PocketIncome.NotWatching;
        if (!QuantumPocket.Active())
        {
            return;
        }
        __state = PocketIncome.Before(ElementType.biomass);
    }

    public static void Postfix(ScientificNotation __state)
    {
        PocketIncome.After(ElementType.biomass, __state);
    }
}

internal static class PocketIncome
{
    private static bool loggedFailure;

    // A sentinel the hold can never legitimately be at, so "the prefix did not
    // run" and "the hold was empty" stay distinguishable.
    internal static readonly ScientificNotation NotWatching =
        new ScientificNotation(-1.0);

    internal static ScientificNotation Before(ElementType type)
    {
        try
        {
            Player player = Player.shared;
            if (player == null || player.storage == null)
            {
                return NotWatching;
            }
            ElementBatch batch;
            if (!player.storage.elementDict.TryGetValue(type, out batch)
                || batch == null)
            {
                return NotWatching;
            }
            return batch.amount;
        }
        catch (Exception)
        {
            return NotWatching;
        }
    }

    internal static void After(ElementType type, ScientificNotation before)
    {
        if (before < ScientificNotation.zero || QuantumPocket.broken
            || !QuantumPocket.Active())
        {
            return;
        }
        try
        {
            Player player = Player.shared;
            if (player == null || player.storage == null)
            {
                return;
            }
            ElementBatch batch;
            if (!player.storage.elementDict.TryGetValue(type, out batch)
                || batch == null)
            {
                return;
            }
            ScientificNotation gained = batch.amount - before;
            if (!(gained > ScientificNotation.zero))
            {
                return;
            }
            // Whatever the pocket cannot take stays in the hold and behaves the
            // way it always has, which is what makes a small pocket a partial
            // help rather than a cliff. The difference is exactly the overflow
            // the readout reports, so it is noted here where it is known for
            // certain rather than reconstructed from the hold afterwards.
            ScientificNotation moved = QuantumPocket.Absorb(type, gained);
            QuantumPocket.NoteSpill(type, gained - moved);
        }
        catch (Exception e)
        {
            QuantumPocket.broken = true;
            if (!loggedFailure)
            {
                loggedFailure = true;
                QuantumPocket.ReportFailureOnce("catching in-flight income", e);
            }
        }
    }
}

// -------------------------------------------------------------------------
// Spending: the pocket answers as though it were in the hold, and tops the
// hold up the instant before the game subtracts from it
// -------------------------------------------------------------------------

// Research.Tick, Infra.Tick and EnergySource.Tick all ask this first, and then
// scale the work they do by what comes back. Without this they would never ask
// for more than the hold can pay, and the pocket would fill up and never be
// spent.
[HarmonyPatch(typeof(ResourceStorage), "AvailableElementBatchPercentage")]
public static class AvailablePocketPatch
{
    public static void Postfix(ResourceStorage __instance, ElementBatch batch,
        double filter, int offlineSimulationSpeed, ref double __result)
    {
        if (QuantumPocket.broken || !QuantumPocket.Active())
        {
            return;
        }
        try
        {
            if (batch != null)
            {
                QuantumPocket.NoteReserve(batch.type, ResourceStorage.cap
                    * filter * (double)offlineSimulationSpeed);
            }
            if (__result >= 1.0)
            {
                return;
            }
            if (batch == null || batch.amount <= ScientificNotation.zero)
            {
                return;
            }
            ScientificNotation pocket = QuantumPocket.Held(batch.type);
            if (!(pocket > ScientificNotation.zero))
            {
                return;
            }

            // The game's own arithmetic, with the pocket added to the hold:
            //   available = (held + pocket) - cap * filter * speed
            //   want      = batch.amount * speed
            //   result    = clamp(available, 0, want) / want
            ScientificNotation held = __instance.elementDict[batch.type].amount;
            ScientificNotation reserved =
                ResourceStorage.cap * filter * (double)offlineSimulationSpeed;
            ScientificNotation available = held + pocket - reserved;
            ScientificNotation want = batch.amount * (double)offlineSimulationSpeed;
            if (want <= ScientificNotation.zero)
            {
                return;
            }
            if (available > want)
            {
                available = want;
            }
            if (available <= ScientificNotation.zero)
            {
                return;
            }
            double improved = (available / want).Standard();
            if (improved > __result)
            {
                __result = (improved > 1.0) ? 1.0 : improved;
            }
        }
        catch (Exception e)
        {
            QuantumPocket.broken = true;
            QuantumPocket.ReportFailureOnce("offering the pocket to research", e);
        }
    }
}

// Top the hold up out of the pocket before the subtraction, rather than
// letting the hold go negative and fixing it afterwards: TakeElementBatch
// calls NormalizeResource, which clamps a negative amount to zero and would
// quietly swallow the difference.
[HarmonyPatch(typeof(ResourceStorage), "TakeElementBatch")]
public static class TakePocketPatch
{
    public static void Prefix(ResourceStorage __instance, ElementBatch batch,
        int offlineSimulationSpeed)
    {
        if (QuantumPocket.broken || !QuantumPocket.Active())
        {
            return;
        }
        try
        {
            if (batch == null || !(batch.amount > ScientificNotation.zero))
            {
                return;
            }
            ScientificNotation pocket = QuantumPocket.Held(batch.type);
            if (!(pocket > ScientificNotation.zero))
            {
                return;
            }
            int speed = (offlineSimulationSpeed < 1) ? 1 : offlineSimulationSpeed;
            ScientificNotation need = batch.amount * (double)speed;
            // What the hold can give without going under the reserve the
            // availability question just worked out: the lock slider, or the
            // cargo the doors are keeping for the destination. Topping up
            // only to `need` would let the subtraction eat into that reserve
            // whenever the pocket was paying part of the bill.
            ScientificNotation have = __instance.elementDict[batch.type].amount
                - QuantumPocket.TakeNotedReserve(batch.type);
            if (have < ScientificNotation.zero)
            {
                have = ScientificNotation.zero;
            }
            if (have >= need)
            {
                return;
            }
            QuantumPocket.Release(batch.type, need - have);
        }
        catch (Exception e)
        {
            QuantumPocket.broken = true;
            QuantumPocket.ReportFailureOnce("spending out of the pocket", e);
        }
    }
}

// -------------------------------------------------------------------------
// Arrival
// -------------------------------------------------------------------------
//
// Location.Arrive is private, which Harmony does not mind. It clears
// isTravelling on its first line, so by the time this runs Active() is already
// false and the flush cannot catch its own output.
[HarmonyPatch(typeof(Location), "Arrive")]
public static class LocationArrivePocketPatch
{
    public static void Postfix()
    {
        if (QuantumPocket.broken)
        {
            return;
        }
        try
        {
            QuantumPocket.FlushToHold();
        }
        catch (Exception e)
        {
            QuantumPocket.broken = true;
            QuantumPocket.ReportFailureOnce("delivering the pocket on arrival", e);
        }
    }
}

// A Big Bang wipes the hold, so it has to wipe the pocket too or the reset
// would hand the player a free carry-over.
[HarmonyPatch(typeof(ResourceStorage), "BigBangClean")]
public static class BigBangCleanPocketPatch
{
    public static void Postfix()
    {
        QuantumPocket.Clear();
    }
}

// Loading a save has to empty the pocket, because the pocket is static and
// the save being replaced may have left something in it. Prefix rather than
// postfix so a load that throws part way through still leaves the pocket
// empty instead of half belonging to each save.
[HarmonyPatch(typeof(SaveLoadManager), "LoadGameFromPath")]
public static class LoadGamePocketPatch
{
    public static void Prefix()
    {
        try
        {
            QuantumPocket.ResetForLoad();
        }
        catch (Exception e)
        {
            QuantumPocket.ReportFailureOnce("emptying the pocket for a load", e);
        }
    }
}
