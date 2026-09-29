// Space Travel Idle community mod - offline save-load crash fix, and the
// offline travel speed fix.
//
// ---------------------------------------------------------------------------
// 1. The save-load crash
//
// The community build has no game server, but SaveLoadManager.LoadSave still
// tries to upload your "beta roles" to one:
//
//   if (save.areBetaRolesUploaded) { UserManager.shared.areBetaRolesUploaded = true; }
//   else                          { UserManager.shared.UpdateUserRoles(); }
//
// UpdateUserRoles asks CardBag.GatherBetaRoles which of beta_medal_1/2/3 you
// own. If you own none it just sets the flag and returns. If you own ANY, it
// calls NetworkManager.shared.serverAPI.UpdateUserRoles, which does
//
//   HttpApi.GetCookies()["csrftoken"].Value
//
// with no server and no cookie jar - NullReferenceException. That throws out
// of LoadSave partway through, so the rest of the load never runs: the game
// falls back to a new game (tutorial), dark matter reads as 0, and the guide
// flags reset. Then the autosave writes THAT state back, with
// areBetaRolesUploaded still false - so the next load fails the same way.
// One bad save makes every later one bad; owning a beta medal is enough.
//
// Fix: skip the upload entirely and mark the roles as uploaded. There is
// nothing to upload to in an offline build, and the flag is only ever read to
// decide whether to try again. The roles themselves are still gathered and
// applied to the local user by the original method's first two lines, so
// medal ownership is unaffected - we only drop the network call.
//
// Compiled with the .NET Framework 4 csc against the game's own
// Assembly-CSharp.dll and the BepInEx 5 core DLLs (see build_plugin.ps1).

using System;
using System.Globalization;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LargeNumbers;

[BepInPlugin("sti.community.offlinefix", "STI Community Offline Fix", "1.5.0")]
public class OfflineFixPlugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;

    private void Awake()
    {
        // 0.1.1: off if the player set it to false in STI Community Patch.cfg
        if (!CommunityToggle.On("offline-fix", Logger))
        {
            enabled = false;
            return;
        }
        Log = Logger;

        OfflineAwayTime.cfgShow = Config.Bind("Offline", "ShowHowLongYouWereAway",
            true,
            "Puts a line at the top of the Welcome Back panel saying how long "
            + "you were actually gone. The base game never says. The same line "
            + "shows on the Last Offline tab of the Stats panel, so you can go "
            + "back and look at it after you have closed the welcome panel.");
        OfflineAwayTime.cfgLabel = Config.Bind("Offline", "TimeAwayLabel",
            "Time away",
            "What that line is called. Everything else on the panel follows the "
            + "language you picked in the game. This line is added by the mod "
            + "and it is English, so change it here if you play in another "
            + "language.");

        OfflineCap.cfgEnabled = Config.Bind("Offline cap", "CapOfflineTime",
            true,
            "The base game has no offline limit at all, so being gone for a "
            + "month credits you the whole month. This puts a lid on it, and "
            + "the two Big Bang upgrades Offline Reserve and Offline Vault "
            + "raise that lid. Turn this off and you are back to the base "
            + "game, with no limit and the two upgrades doing nothing.");
        OfflineCap.cfgBaseHours = Config.Bind("Offline cap", "BaseHours",
            24,
            "How much time you get credited with no Big Bang upgrades at all, "
            + "in hours. 24 is one day. Every level of Offline Reserve adds a "
            + "day on top of this and every level of Offline Vault adds a "
            + "week.");
        OfflineCap.cfgCappedNote = Config.Bind("Offline cap", "CappedNote",
            ", capped at {0}. Big Bang has two upgrades that raise that",
            "What gets added to the Time away line when you were gone longer "
            + "than the cap. {0} is the cap itself. Leave it empty and the "
            + "line just says how long you were away, with no explanation.");

        Harmony harmony = new Harmony("sti.community.offlinefix");
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        Logger.LogInfo("Offline fix active: beta-role upload on save load skipped");
        Logger.LogInfo("Offline travel fix active: the cruise speed is taken before the offline loot lands in the hold");
        Logger.LogInfo("Offline time away display: "
            + (OfflineAwayTime.cfgShow.Value ? "on" : "off"));
        Logger.LogInfo("Offline cap: " + OfflineCap.Describe());
        Logger.LogInfo("Offline popup: Esc closes it, and it shrinks to keep its close button on screen");
    }

    private void Update()
    {
        OfflinePopupReach.Tick();
    }
}

[HarmonyPatch(typeof(UserManager), "UpdateUserRoles")]
public static class UpdateUserRolesPatch
{
    public static bool Prefix(UserManager __instance)
    {
        try
        {
            // keep the local half of the original method: work out which
            // medals you hold and record them on the local user object
            Player player = Player.shared;
            if (player != null && player.cardBag != null
                && GlobalSave.shared != null && GlobalSave.shared.user != null)
            {
                GlobalSave.shared.user.AddRole(player.cardBag.GatherBetaRoles());
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning("[OfflineFix] role gather skipped: " + e.Message);
        }

        // no server to upload to - say it is done so the load continues and
        // the save never comes back here
        __instance.areBetaRolesUploaded = true;
        return false;
    }
}

// ---------------------------------------------------------------------------
// 2. Offline travel crawls, and the ship arrives days late or never
//
// OfflineManager flies the whole offline stretch at ONE frozen number. It is
// taken on the last line of ScheduleFastSimulation and used for every
// simulated tick:
//
//   ScheduleFastSimulation(): engineSpeed = engine.estimatedSpeed;
//   RunSimulation(speed):     player.location.TravelTick(engineSpeed, speed);
//
// Online, Player.FixedUpdate reads the speed fresh every tick instead:
//
//   location.TravelTick(spaceship.engine.estimatedSpeed);
//
// Freezing it would be harmless on its own. The problem is WHEN it is frozen.
// OfflineManager.SkipTime runs these two lines back to back:
//
//   BattleManager.shared.SkipTime((int)seconds);   // offline boss auto-kill loot
//   StartBackgroundSimulation(seconds);            // -> snapshot, one line later
//
// BattleManager.SkipTime awards every auto-kill zone its offline boss kills
// (up to 168 per zone) and pours the drops straight into the cargo hold. So
// the snapshot is taken at the single heaviest moment the ship will ever have,
// holding hours of loot that the simulation is about to spend on research
// within its first few simulated minutes.
//
// Speed is
//
//   estimatedSpeed = sqrt(kineticEnergy * speedModifier / totalWeight)
//   totalWeight    = (spaceship.weight + storage.weight) * spaceshipDieting
//
// and spaceship.weight is a flat 100. A hold full of loot does not add a few
// percent to the weight, it multiplies it. Measured on a real save (6.285 h
// offline, Sun to Saturn):
//
//   at the snapshot     cargoWeight 7.89e8   totalWeight 325.23   speed 5.22e8
//   after the run       cargoWeight 2.27     totalWeight 4.22e-5  speed 1.45e12
//
// 7.7 million times the weight, so 2,778 times the speed, because of the
// square root. The remaining 2.3e15 of that trip is 26 minutes at the real
// speed and 1,226 hours at the frozen one. That is the whole "offline does not
// progress travel" report: it does progress, at one 2,778th of the speed.
//
// The fix takes the snapshot BEFORE any offline reward is credited, in a
// prefix on OfflineManager.SkipTime, and writes it into the private
// engineSpeed field after ScheduleFastSimulation has set its own. That is the
// speed the ship genuinely had at the moment the game closed, which is the
// number the online path would have been using all along.
//
// Re-reading the speed live on every RunSimulation call was the other
// candidate and is deliberately not what this does. Player.FixedUpdate skips
// NormalizeAllStorage while the simulation runs, and GetOfflineTickResources
// adds resources with the batch multiplier and no normalisation, so the hold
// can balloon far past its cap mid-run. A live read would let that inflated
// weight brake the ship, which is the same bug wearing a different hat.
//
// One free extra: ScheduleFastSimulation returns early for gaps under 12
// seconds and never assigns engineSpeed at all, leaving whatever was there
// (zero, on a fresh load). Writing the field unconditionally covers that too.
// ---------------------------------------------------------------------------

public static class OfflineTravelFix
{
    private static FieldInfo engineSpeedField;

    internal static ScientificNotation capturedSpeed = ScientificNotation.zero;
    internal static bool hasCapture;

    private static string Num(ScientificNotation n)
    {
        try
        {
            return n.ToString();
        }
        catch (Exception)
        {
            return "?";
        }
    }

    // The state of the three terms that decide cruise speed. Cheap, and it is
    // the only record of what the ship looked like before the offline rewards
    // landed, so it stays in the release build.
    public static void Report(string when)
    {
        try
        {
            Player player = Player.shared;
            if (player == null || player.spaceship == null)
            {
                OfflineFixPlugin.Log.LogInfo("Offline travel [" + when + "]: no player yet");
                return;
            }
            Engine engine = player.spaceship.engine;
            Location loc = player.location;
            OfflineFixPlugin.Log.LogInfo("Offline travel [" + when + "]"
                + " kineticEnergy=" + Num(engine.kineticEnergy)
                + " cargoWeight=" + Num(player.storage.weight)
                + " totalWeight=" + Num(engine.totalWeight)
                + " speedModifier=" + Num(engine.speedModifier)
                + " estimatedSpeed=" + Num(engine.estimatedSpeed)
                + " travelling=" + loc.isTravelling
                + " distance=" + Num(loc.distance)
                + " travelled=" + Num(loc.distanceTravelled));
        }
        catch (Exception e)
        {
            OfflineFixPlugin.Log.LogWarning("Offline travel [" + when + "] report failed: " + e.Message);
        }
    }

    public static bool TryReadFrozen(OfflineManager manager, out ScientificNotation value)
    {
        value = ScientificNotation.zero;
        try
        {
            if (engineSpeedField == null)
            {
                engineSpeedField = AccessTools.Field(typeof(OfflineManager), "engineSpeed");
            }
            if (engineSpeedField == null)
            {
                return false;
            }
            value = (ScientificNotation)engineSpeedField.GetValue(manager);
            return true;
        }
        catch (Exception e)
        {
            OfflineFixPlugin.Log.LogWarning("Offline travel could not read the frozen speed: " + e.Message);
            return false;
        }
    }

    public static bool TryWriteFrozen(OfflineManager manager, ScientificNotation value)
    {
        try
        {
            if (engineSpeedField == null)
            {
                engineSpeedField = AccessTools.Field(typeof(OfflineManager), "engineSpeed");
            }
            if (engineSpeedField == null)
            {
                OfflineFixPlugin.Log.LogWarning("Offline travel: no engineSpeed field, the speed was left as the game set it");
                return false;
            }
            engineSpeedField.SetValue(manager, value);
            return true;
        }
        catch (Exception e)
        {
            OfflineFixPlugin.Log.LogWarning("Offline travel could not set the cruise speed: " + e.Message);
            return false;
        }
    }
}

// Before anything offline is credited. This is the last moment the ship still
// looks the way it did when the game was closed.
[HarmonyPatch(typeof(OfflineManager), "SkipTime")]
public static class SkipTimeSpeedCapture
{
    // ref, so the clamp happens once and BattleManager.SkipTime,
    // StartBackgroundSimulation and player.totalGameTime += seconds all see the
    // same number. See section 4.
    public static void Prefix(ref double seconds)
    {
        // Before the null checks below, because this number has to survive even
        // on a load where the ship is not built yet. It is the only place the
        // offline gap exists at all - see section 3. Recorded before the clamp,
        // so the panel can say how long you were really gone.
        OfflineAwayTime.lastSeconds = seconds;
        OfflineAwayTime.hasTime = seconds >= 1.0;

        OfflineCap.Apply(ref seconds);

        OfflineTravelFix.hasCapture = false;
        try
        {
            Player player = Player.shared;
            if (player == null || player.spaceship == null || player.spaceship.engine == null)
            {
                return;
            }
            OfflineTravelFix.capturedSpeed = player.spaceship.engine.estimatedSpeed;
            OfflineTravelFix.hasCapture = true;
            OfflineFixPlugin.Log.LogInfo("Offline travel: catching up "
                + ((double)seconds / 3600.0).ToString("0.00") + " h ("
                + (OfflineAwayTime.Pretty(seconds) ?? "?") + ")");
            OfflineTravelFix.Report("before offline rewards");
        }
        catch (Exception e)
        {
            OfflineFixPlugin.Log.LogWarning("Offline travel could not take the cruise speed: " + e.Message);
        }
    }
}

// ScheduleFastSimulation has just overwritten engineSpeed with the speed of a
// ship weighed down by the loot that arrived two lines earlier. Put the real
// one back.
[HarmonyPatch(typeof(OfflineManager), "ScheduleFastSimulation")]
public static class ScheduleFastSimulationSpeedFix
{
    public static void Postfix(OfflineManager __instance, int ticks)
    {
        if (!OfflineTravelFix.hasCapture)
        {
            return;
        }
        ScientificNotation was;
        bool read = OfflineTravelFix.TryReadFrozen(__instance, out was);
        if (!OfflineTravelFix.TryWriteFrozen(__instance, OfflineTravelFix.capturedSpeed))
        {
            return;
        }
        OfflineFixPlugin.Log.LogInfo("Offline travel: flying "
            + ticks + " ticks at " + OfflineTravelFix.capturedSpeed
            + (read ? (", the game would have used " + was) : ""));
    }
}

[HarmonyPatch(typeof(OfflineManager), "FinishSimulation")]
public static class FinishSimulationReport
{
    public static void Postfix(OfflineManager __instance)
    {
        OfflineTravelFix.Report("after the catch-up");
    }
}


// ---------------------------------------------------------------------------
// 3. The game never tells you how long you were away
//
// Fuzzied asked for this and it turned out to be missing entirely. The offline
// gap is computed once, in SaveLoadManager.LoadSave:
//
//   double seconds = Utils.SecondsSinceEpoch() - save.saveTime;
//   OfflineManager.shared.SkipTime(seconds);
//
// and after that it is gone. SkipTime does player.totalGameTime += seconds,
// which is cumulative, so the delta cannot be recovered afterwards. Nothing
// stores it: StatsProfile has 32 stats and not one of them is a duration, and
// the Welcome Back panel has no field for it. The player sees what they
// earned while away and never learns how long away was.
//
// So the number has to be caught on its way past. The SkipTime prefix above
// already has it as a parameter, so it costs one assignment.
//
// Where to show it. Both places that display offline results build their text
// from the same call:
//
//   OfflineProgressPanel.ShowOfflineProgress()  ->  lastOfflineStatsProfile.ToString()
//   StatsPanel.ShowOfflineStatsProfile()        ->  lastOfflineStatsProfile.ToString()
//
// so a postfix on StatsProfile.ToString covers both at once, with no reflection
// into private serialised label fields and no prefab edit. ToString is also
// called every FixedUpdate for the lifetime and current-Big-Bang profiles, so
// the guard is a reference comparison against lastOfflineStatsProfile itself
// rather than a read of the private domain field. Two reference compares per
// frame is nothing, and it cannot ever match the wrong profile.
//
// The line is added at the top and it is shaped like the game's own stat lines
// (SingleStat.STATS_PROPERTY_FORMAT is "{0}: {1}"), bold because it is the
// heading for everything under it.
// ---------------------------------------------------------------------------

public static class OfflineAwayTime
{
    internal static ConfigEntry<bool> cfgShow;
    internal static ConfigEntry<string> cfgLabel;

    internal static double lastSeconds;
    internal static bool hasTime;

    // ToDHMSFormat is the game's own duration formatter and returns the unit
    // names as unresolved localisation tags: "2{general.time.hour}15{general.
    // time.minute}". Labels that go through LangText.SetLocalisedText get those
    // resolved on the way in, but both offline panels assign .text directly, so
    // nothing downstream would resolve them and the player would read the
    // braces. GetLocalisationFromLocPath is exactly what LangText calls, so
    // running it here makes this read like every other duration in the game.
    internal static string Pretty(double seconds)
    {
        try
        {
            if (seconds < 1.0)
            {
                return null;
            }
            string raw = seconds.ToDHMSFormat();
            if (string.IsNullOrEmpty(raw) || raw == "-")
            {
                // ToDHMSFormat answers "-" for infinity, NaN and anything past
                // TimeSpan.MaxValue. A clock that jumped backwards can get here.
                return null;
            }
            string text = Localisation.GetLocalisationFromLocPath(raw);
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception e)
        {
            OfflineFixPlugin.Log.LogWarning("Offline time away could not be formatted: " + e.Message);
            return null;
        }
    }
}

[HarmonyPatch(typeof(StatsProfile), "ToString")]
public static class StatsProfileAwayTimePatch
{
    public static void Postfix(StatsProfile __instance, ref string __result)
    {
        try
        {
            if (!OfflineAwayTime.hasTime
                || OfflineAwayTime.cfgShow == null
                || !OfflineAwayTime.cfgShow.Value)
            {
                return;
            }
            if (StatsManager.shared == null
                || !object.ReferenceEquals(__instance, StatsManager.shared.lastOfflineStatsProfile))
            {
                return;
            }
            string pretty = OfflineAwayTime.Pretty(OfflineAwayTime.lastSeconds);
            if (pretty == null)
            {
                return;
            }
            ConfigEntry<string> labelCfg = OfflineAwayTime.cfgLabel;
            string label = (labelCfg == null || string.IsNullOrEmpty(labelCfg.Value))
                ? "Time away" : labelCfg.Value;
            __result = "<b>" + label + "</b>: " + pretty
                + OfflineCap.CappedNote() + "\n\n" + __result;
        }
        catch (Exception e)
        {
            // This runs every FixedUpdate. One warning, then stand down, rather
            // than a log file that is nothing but this line.
            OfflineAwayTime.hasTime = false;
            OfflineFixPlugin.Log.LogWarning("Offline time away could not be shown: " + e.Message);
        }
    }
}

// ---------------------------------------------------------------------------
// 4. The offline cap, which the game never had
//
// Fuzzied asked for Big Bang upgrades that add offline days, and it turned out
// there was nothing for them to add to. Vanilla credits the whole gap, however
// long it is. SaveLoadManager.LoadSave:
//
//   double seconds = Utils.SecondsSinceEpoch() - save.saveTime;
//   OfflineManager.shared.SkipTime(seconds);
//
// and nothing in SaveLoadManager, OfflineManager or BattleManager clamps it,
// no data file carries a limit, and no localisation string mentions one. The
// developers never needed a cap because ScheduleFastSimulation is logarithmic,
// so a year offline loads in about the same time as an hour. There was no load
// time to protect.
//
// So selling extra offline days means introducing the cap those days lift. The
// base cap is one day, which is roughly where people assume it already is.
//
//   cap = BaseHours + (Offline Reserve level) days + (Offline Vault level) weeks
//
// The day and week counts are read out of the loaded upgrade set rather than
// hardcoded, so retuning either line is an edit to tools/gen_bigbang.py and a
// reinstall, with no plugin rebuild. FALLBACK_RESERVE_DAYS and
// FALLBACK_VAULT_WEEKS carry the same numbers for a save where the data patch
// is not installed but the DLL is.
//
// Where the clamp goes. OfflineManager.SkipTime is the single funnel: it feeds
// BattleManager.SkipTime, StartBackgroundSimulation and
// player.totalGameTime += seconds, in that order, from one parameter. Taking
// it by ref in the prefix clamps all three at once and there is nowhere for
// the two numbers to disagree.
//
// Two things the clamp does not reach on its own.
//
// SkipTime has three callers. LoadSave is the real one; the other two are
// cheats (AddOfflineTimeCheat and the console skip command), and capping those
// would be capping the wrong thing. So the cap only fires while LoadSave is on
// the stack, which is what the guard below tracks.
//
// BattleManager's offline boss auto-kills do not read the seconds parameter at
// all. They read the wall clock against each zone's own lastBossKillTimestamp:
//
//   int num2 = (int)(num - zoneProgress.lastBossKillTimestamp);
//   int num4 = Mathf.Min(num2 / num3, 168);
//
// so clamping seconds would leave boss kills credited for the full time away
// and the cap would only half apply. PullBossClocksForward pulls those
// timestamps up to the start of the credited window before BattleManager gets
// to them, which is the same write BattleManager itself makes one line later.
// Only zones that are already past the cap are touched and the timestamp only
// ever moves forward.
// ---------------------------------------------------------------------------

public static class OfflineCap
{
    public const string RESERVE_ID = "offline_reserve";
    public const string VAULT_ID = "offline_vault";

    private const double DAY = 86400.0;
    private const double WEEK = 604800.0;

    internal static ConfigEntry<bool> cfgEnabled;
    internal static ConfigEntry<int> cfgBaseHours;
    internal static ConfigEntry<string> cfgCappedNote;

    // Days per level of Offline Reserve and weeks per level of Offline Vault,
    // as running totals. Only used when the data patch is not installed; the
    // real numbers live in tools/gen_bigbang.py and are read out of the loaded
    // upgrade set below.
    private static readonly double[] FALLBACK_RESERVE_DAYS =
    {
        1.0, 2.0, 3.0, 4.0, 5.0, 6.0
    };

    private static readonly double[] FALLBACK_VAULT_WEEKS =
    {
        1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0, 11.0, 12.0, 13.0,
        14.0, 15.0
    };

    // Set by the LoadSave guard below, and cleared the moment it is used, so a
    // load that throws before SkipTime cannot leave it stuck on.
    internal static bool inSaveLoad;

    internal static double lastCapSeconds;
    internal static bool lastWasCapped;

    private static bool broken;

    private static int Level(string id)
    {
        BigBangManager manager = BigBangManager.shared;
        if (manager == null || manager.upgradeLevels == null)
        {
            return 0;
        }
        return manager.GetUpgradeLevel(id);
    }

    /// <summary>The line's value at this level: days for Reserve, weeks for
    /// Vault. Reads the loaded data first, falls back to the table above.</summary>
    private static double Value(string id, int level, double[] fallback)
    {
        if (level <= 0)
        {
            return 0.0;
        }
        try
        {
            BigBangUpgradeSet set;
            if (BigBangLibrary.upgrades != null
                && BigBangLibrary.upgrades.TryGetValue(id, out set))
            {
                BigBangUpgrade upgrade = set.GetUpgrade(level);
                if (upgrade != null && upgrade.bonus != null)
                {
                    double parsed;
                    if (double.TryParse(upgrade.bonus.eqTemplate, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out parsed)
                        && parsed > 0.0)
                    {
                        return parsed;
                    }
                }
            }
        }
        catch (Exception)
        {
            // tree not loaded yet, or the line is not installed
        }
        if (level <= fallback.Length)
        {
            return fallback[level - 1];
        }
        return fallback[fallback.Length - 1];
    }

    /// <summary>How much time away actually gets credited, in seconds.</summary>
    internal static double CapSeconds()
    {
        double baseHours = (cfgBaseHours == null) ? 24.0 : cfgBaseHours.Value;
        if (baseHours < 0.0)
        {
            baseHours = 0.0;
        }
        double cap = baseHours * 3600.0;
        cap += Value(RESERVE_ID, Level(RESERVE_ID), FALLBACK_RESERVE_DAYS) * DAY;
        cap += Value(VAULT_ID, Level(VAULT_ID), FALLBACK_VAULT_WEEKS) * WEEK;
        return cap;
    }

    // Called from Awake, where BigBangManager.shared is still null and no
    // save is loaded, so there is no cap to report yet. Report the setting.
    // The real cap goes in the log once per load, from Apply.
    // 0.1.1: two switches. The cap's own line in sti.community.offlinefix.cfg,
    // and the offline-cap line in STI Community Patch.cfg, where every other
    // part of the patch is switched. Either one set to false means no cap.
    private static bool IsOn()
    {
        return cfgEnabled != null && cfgEnabled.Value
            && CommunityToggle.IsOn("offline-cap");
    }

    internal static string Describe()
    {
        if (!IsOn())
        {
            return "off, time away is credited in full like the base game";
        }
        int hours = (cfgBaseHours == null) ? 24 : cfgBaseHours.Value;
        return "on, " + hours + "h before Big Bang upgrades";
    }

    /// <summary>The cap and where it came from, for the log.</summary>
    private static string Explain(double cap)
    {
        return (OfflineAwayTime.Pretty(cap) ?? (cap.ToString("0") + "s"))
            + " (Offline Reserve " + Level(RESERVE_ID)
            + ", Offline Vault " + Level(VAULT_ID) + ")";
    }

    internal static void Apply(ref double seconds)
    {
        bool wasLoad = inSaveLoad;
        inSaveLoad = false;
        lastWasCapped = false;
        lastCapSeconds = 0.0;

        if (broken || !wasLoad || seconds <= 0.0
            || !IsOn())
        {
            return;
        }
        try
        {
            double cap = CapSeconds();
            lastCapSeconds = cap;
            if (cap <= 0.0 || seconds <= cap)
            {
                OfflineFixPlugin.Log.LogInfo("Offline cap: away "
                    + (OfflineAwayTime.Pretty(seconds) ?? "?")
                    + ", under the cap of " + Explain(cap));
                return;
            }
            OfflineFixPlugin.Log.LogInfo("Offline cap: away "
                + (OfflineAwayTime.Pretty(seconds) ?? "?") + ", crediting "
                + Explain(cap));
            PullBossClocksForward(cap);
            seconds = cap;
            lastWasCapped = true;
        }
        catch (Exception e)
        {
            // Never cost anyone a load over this. Stand down and behave like
            // the base game for the rest of the session.
            broken = true;
            lastWasCapped = false;
            OfflineFixPlugin.Log.LogWarning("Offline cap could not be applied, "
                + "time away will be credited in full: " + e.Message);
        }
    }

    // BattleManager.SkipTime works out offline boss auto-kills from the wall
    // clock against each zone's lastBossKillTimestamp, not from the seconds it
    // is handed, so the clamp above does not reach them. Move those clocks up
    // to the start of the credited window first.
    private static void PullBossClocksForward(double cap)
    {
        BattleManager battle = BattleManager.shared;
        if (battle == null || battle.battleProgress == null
            || battle.battleProgress.zoneProgressDict == null)
        {
            return;
        }
        double floor = Utils.SecondsSinceEpoch() - cap;
        int moved = 0;
        foreach (ZoneProgress zone in battle.battleProgress.zoneProgressDict.Values)
        {
            if (zone == null || !zone.autoKillActivated
                || zone.lastBossKillTimestamp <= 0.0
                || zone.lastBossKillTimestamp >= floor)
            {
                continue;
            }
            zone.lastBossKillTimestamp = floor;
            moved++;
        }
        if (moved > 0)
        {
            OfflineFixPlugin.Log.LogInfo("Offline cap: boss auto-kill clocks "
                + "pulled forward on " + moved + " zone(s)");
        }
    }

    /// <summary>The tail of the Time away line when the cap bit. Empty
    /// otherwise.</summary>
    internal static string CappedNote()
    {
        try
        {
            if (!lastWasCapped || cfgCappedNote == null
                || string.IsNullOrEmpty(cfgCappedNote.Value))
            {
                return "";
            }
            string pretty = OfflineAwayTime.Pretty(lastCapSeconds);
            if (pretty == null)
            {
                return "";
            }
            return string.Format(cfgCappedNote.Value, pretty);
        }
        catch (Exception)
        {
            // A hand-edited CappedNote with a stray brace in it lands here.
            return "";
        }
    }
}

// SkipTime has three callers and only one of them is real offline time. The
// other two are cheats: OfflineManager.AddOfflineTimeCheat and the console
// skip command. Capping those would be capping the wrong thing, so the cap
// only fires while LoadSave is on the stack.
[HarmonyPatch(typeof(SaveLoadManager), "LoadSave")]
public static class LoadSaveOfflineCapGuard
{
    public static void Prefix()
    {
        OfflineCap.inSaveLoad = true;
    }

    public static void Postfix()
    {
        OfflineCap.inSaveLoad = false;
    }
}

// ---------------------------------------------------------------------------
// 1.5.0. The Welcome Back panel could not be closed in a short window.
//
// Fuzzied, 30.09.2026: "Currently I am not able to click away the offline
// screen, it needs to be removed if the player clicks Esc too", then "The x is
// outside it". The close button sits outside the panel's top right corner, and
// the panel is laid out for a tall screen. In his 1306x533 window the panel's
// top was 8 pixels from the top of the window, so the button was above it,
// with no other way out: the game has no key for it and the dark layer behind
// the panel swallows every click.
//
// Two ways out now:
//   - Esc closes it, through the panel's own Close, the same call the button
//     makes (Overlay.HideOfflineProgressPanel).
//   - When the panel and its button do not fit in the window, the panel is
//     shrunk and nudged until they do, with a small margin. In a window where
//     it already fits nothing changes. It measures again when the panel opens
//     and whenever the window changes size, and it always works from the
//     panel's original size and spot, so it can grow back.
// ---------------------------------------------------------------------------
public static class OfflinePopupReach
{
    private const float MARGIN = 8f;
    // The layout settles over the first frames after the panel opens, and the
    // game rescales a moment after a resize, so it measures for a few frames.
    private const int SETTLE_FRAMES = 5;

    private static UnityEngine.RectTransform panel;
    private static UnityEngine.RectTransform button;
    private static UnityEngine.Vector3 baseScale;
    private static UnityEngine.Vector2 basePos;
    private static bool wasOpen;
    private static int lastW;
    private static int lastH;
    private static int pending;
    private static string lastShape;

    internal static void Tick()
    {
        try
        {
            OfflineProgressPanel shown = OfflineProgressPanel.shared;
            bool open = shown != null && shown.gameObject.activeInHierarchy;
            if (!open)
            {
                wasOpen = false;
                return;
            }

            if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Escape))
            {
                shown.Close();
                wasOpen = false;
                OfflineFixPlugin.Log.LogInfo("Offline popup: closed with Esc");
                return;
            }

            if (panel == null || panel.gameObject != shown.gameObject)
            {
                panel = shown.transform as UnityEngine.RectTransform;
                UnityEngine.Transform b = shown.transform.Find("ExitButton");
                button = b == null ? null : b as UnityEngine.RectTransform;
                if (panel == null)
                {
                    return;
                }
                baseScale = panel.localScale;
                basePos = panel.anchoredPosition;
                if (button == null)
                {
                    OfflineFixPlugin.Log.LogWarning("Offline popup: no ExitButton under the panel, only the panel itself is kept on screen");
                }
            }

            if (!wasOpen || UnityEngine.Screen.width != lastW || UnityEngine.Screen.height != lastH)
            {
                wasOpen = true;
                lastW = UnityEngine.Screen.width;
                lastH = UnityEngine.Screen.height;
                pending = SETTLE_FRAMES;
            }
            if (pending > 0)
            {
                pending--;
                Fit();
            }
        }
        catch (Exception e)
        {
            pending = 0;
            OfflineFixPlugin.Log.LogWarning("Offline popup: " + e.Message);
        }
    }

    private static void Fit()
    {
        UnityEngine.Canvas canvas = panel.GetComponentInParent<UnityEngine.Canvas>();
        UnityEngine.Camera cam = null;
        if (canvas != null && canvas.rootCanvas.renderMode != UnityEngine.RenderMode.ScreenSpaceOverlay)
        {
            cam = canvas.rootCanvas.worldCamera;
        }

        // Measure from the original size and spot every time.
        SetIfChanged(baseScale, basePos);
        UnityEngine.Rect box = Bounds(cam, true);
        float px = Bounds(cam, false).height / (panel.rect.height * baseScale.y);
        if (box.width <= 0f || box.height <= 0f || float.IsNaN(px) || px <= 0f)
        {
            return;
        }
        float needW = box.width;
        float needH = box.height;

        float roomW = UnityEngine.Screen.width - 2f * MARGIN;
        float roomH = UnityEngine.Screen.height - 2f * MARGIN;
        float s = UnityEngine.Mathf.Min(1f, UnityEngine.Mathf.Min(roomW / needW, roomH / needH));
        UnityEngine.Vector3 scale = baseScale * s;
        SetIfChanged(scale, basePos);

        // Scaling works around the panel's pivot, so the button can still hang
        // over an edge. Slide the whole thing back in.
        box = Bounds(cam, true);
        float dx = 0f;
        float dy = 0f;
        if (box.xMin < MARGIN) { dx = MARGIN - box.xMin; }
        else if (box.xMax > UnityEngine.Screen.width - MARGIN) { dx = UnityEngine.Screen.width - MARGIN - box.xMax; }
        if (box.yMin < MARGIN) { dy = MARGIN - box.yMin; }
        else if (box.yMax > UnityEngine.Screen.height - MARGIN) { dy = UnityEngine.Screen.height - MARGIN - box.yMax; }
        UnityEngine.Vector2 pos = basePos + new UnityEngine.Vector2(dx / px, dy / px);
        SetIfChanged(scale, pos);

        string shape = UnityEngine.Screen.width + "x" + UnityEngine.Screen.height + " " + s.ToString("0.000") + " " + dx.ToString("0") + "," + dy.ToString("0");
        if (pending == 0 && shape != lastShape)
        {
            lastShape = shape;
            string head = "Offline popup: window " + UnityEngine.Screen.width + "x" + UnityEngine.Screen.height
                + ", panel and close button need " + needW.ToString("0") + "x" + needH.ToString("0") + " pixels";
            if (s < 1f || dx != 0f || dy != 0f)
            {
                OfflineFixPlugin.Log.LogInfo(head + ", shrunk to " + (s * 100f).ToString("0") + "% and moved "
                    + dx.ToString("0") + "," + dy.ToString("0") + " so the close button is on screen");
            }
            else
            {
                OfflineFixPlugin.Log.LogInfo(head + ", they fit as they are");
            }
        }
    }

    private static void SetIfChanged(UnityEngine.Vector3 scale, UnityEngine.Vector2 pos)
    {
        if (panel.localScale != scale) { panel.localScale = scale; }
        if (panel.anchoredPosition != pos) { panel.anchoredPosition = pos; }
    }

    // The panel, and with withButton its close button too, in screen pixels,
    // origin bottom left.
    private static UnityEngine.Rect Bounds(UnityEngine.Camera cam, bool withButton)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        UnityEngine.Vector3[] c = new UnityEngine.Vector3[4];
        foreach (UnityEngine.RectTransform r in new UnityEngine.RectTransform[] { panel, withButton ? button : null })
        {
            if (r == null || !r.gameObject.activeInHierarchy) { continue; }
            r.GetWorldCorners(c);
            for (int i = 0; i < 4; i++)
            {
                UnityEngine.Vector2 p = UnityEngine.RectTransformUtility.WorldToScreenPoint(cam, c[i]);
                x0 = UnityEngine.Mathf.Min(x0, p.x);
                y0 = UnityEngine.Mathf.Min(y0, p.y);
                x1 = UnityEngine.Mathf.Max(x1, p.x);
                y1 = UnityEngine.Mathf.Max(y1, p.y);
            }
        }
        return x1 < x0 ? new UnityEngine.Rect(0f, 0f, 0f, 0f) : new UnityEngine.Rect(x0, y0, x1 - x0, y1 - y0);
    }
}
