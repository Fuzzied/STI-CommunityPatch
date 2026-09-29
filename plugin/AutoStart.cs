// Space Travel Idle community mod - start Research and Infrastructure by
// themselves when you arrive at a planet.
//
// Asked for by Fuzzied: "So the players dont sit idle for hours, days after a
// long flight." Trips to the outer planets take real-world days, and the
// vanilla game lands you with every research and every building switched off,
// so the whole flight is followed by a manual setup pass before anything
// starts earning again.
//
// The feature is bought in the Big Bang tree, on the new 'auto_start_ir' line
// (four levels), so it is a reward rather than something everyone gets for
// free:
//
//   Level 1  Start every research and every building that is unlocked, not
//            finished, and not already running.
//   Level 2  Also respect your energy budget: a building whose running cost
//            you cannot cover is left alone, cheapest first, so arriving
//            somewhere cannot put you into an energy deficit.
//   Level 3  Also order the new jobs sensibly - the ones that unlock new
//            resources, new buildings and new research first, then the cheap
//            ones, then the expensive drains - and keep the priority list as
//            a whole feeding downwards.
//   Level 4  Also carry the priority list itself through a Big Bang: the
//            Accumulation Layers, the limits set on them, and which jobs sat
//            under which layer.
//
// That upgrade line lives in the bigBangUpgrades data patch ("Content: more
// Big Bang upgrades, and 5 new ones"). With that patch not installed the line
// does not exist, GetUpgradeLevel returns 0, and this plugin does nothing - it
// says so in the log once, and the Level setting in the config file can force
// it on for anyone who wants the feature without the data patch.
//
// ---------------------------------------------------------------------------
// How a job is started, and why it is done the long way round
//
// The game has TechnoManager.TurnOnResearch / TurnOnInfra, which look like the
// obvious entry points, but both dereference the item's UI view with no null
// check:
//
//     research.view.isResearching = true;
//
// research.view is only set while the Techno panel has actually built a row
// for that research, which after a fresh arrival it may not have done yet. So
// calling them blind is a NullReferenceException waiting to happen. This does
// what they do, guarded, and adds the two steps they rely on the UI for:
//
//   1. star.controlSettings.researchIdResearchingDict[id] = true
//      TechnoPanel.LoadStarResearchData reads that dictionary back over the
//      model every time it rebuilds its rows, so setting only the model would
//      be silently undone the next time the panel loads.
//
//   2. PriorityJobManager.AttemptInsertJob(...)
//      TechnoManager.LoadEEIRPriorityJobs is what normally turns a flagged
//      item into a running job, but it walks the Techno panel's items with the
//      default GetComponentsInChildren<T>(), which skips inactive ones - a
//      closed panel can mean the job is never picked up. AttemptInsertJob is
//      public, guards on ContainsKey so calling it twice is harmless, and
//      builds its own row under the priority list.
//
// New jobs are always appended to the BOTTOM of the priority list, and nothing
// already in the list is ever moved. Level 3's ordering decides the order of
// the jobs it just added, relative to each other, and nothing else - so it can
// never reshuffle a queue you arranged by hand.
//
// ---------------------------------------------------------------------------
// The in-game switches
//
// Each level you own gets its own toggle in the Settings panel, and a level you
// have not bought has no toggle at all - there is no point offering someone a
// switch for something they cannot use. So a player at level 2 sees two
// toggles, and buying level 3 makes the third appear.
//
// They are separate switches rather than one level slider because the three
// levels are three genuinely different behaviours, and someone may well want
// the ordering without the energy check (say they run a big surplus and would
// rather everything just started). Turning the first one off stops the whole
// feature, since the levels above it have nothing to modify.
//
// The toggles are the same ConfigEntry values that live in
// BepInEx\config\sti.community.autostart.cfg, so changing one in game writes
// the config file and vice versa.
//
// ---------------------------------------------------------------------------
// When it fires (v1.1.0)
//
// v1.0 hooked SidePanel.Arrive and nothing else, which turned out to cover
// only one of the moments where the game leaves you with everything switched
// off. Fuzzied reported the other three, and a fourth gap in what it touched:
//
//   1. A Big Bang. BigBangManager.BigBang wipes the run and drops you back on
//      Earth with every job off - the single biggest "sit and click again"
//      moment in the game - and it never goes through SidePanel.Arrive.
//   2. Departing. stayingStarIndex goes to -1 while you fly, and -1 is not a
//      nowhere: it is the spaceship, a real star with its own research,
//      infrastructure and production (StarMap.GetStarByIndex(-1)). v1.0 read
//      that -1 as "still in flight, come back later" and gave up. Flying to
//      Neptune with the spaceship's own jobs switched off is exactly the
//      multi-day idle this feature exists to prevent.
//   3. Production. TechnoPanel.LoadStarData loads three lists, not two:
//      production, infrastructure and research. v1.0 only ever knew about the
//      last two, so the energy and resource collectors on a new planet sat at
//      0% - which is off - while the research above them ran.
//   4. Things that unlock later. The arrival burst is a snapshot, and most of
//      a planet opens up over the following hours as research completes.
//
// Which star is worked on now follows TechnoPanel.LoadStarData exactly,
// because that is the game's own answer to "what can I control right now":
//
//   in flight     research/infra: the spaceship.  production: the spaceship.
//   at a star     research/infra: that star, plus the spaceship once
//                 'space_ir_anywhere' is researched.
//                 production: that star AND the spaceship, always.
//
// ---------------------------------------------------------------------------
// The engine modules (v1.2.0)
//
// Fuzzied, after a Big Bang: "Launch did not activate, so fuel are not filled."
//
// ProductionSource has THREE subclasses, not two. EnergySource and
// ElementSource belong to a star and appear on the Techno panel; SpeedModule
// belongs to the spaceship's engine and appears on the Spaceship panel, and
// v1.1.0 had never heard of it. 'Launch' is one of those, so the engine sat at
// 0% and the fuel bar never moved.
//
// It is a separate code path in every respect: the templates come from
// EngineLoader.speedModuleTemplates rather than from a star, the instances
// from SpaceshipManager.shared.speedModuleSet, and the saved percentages from
// MainPanel.shared.spaceshipPanel.speedModulesInputDict rather than from any
// star's control settings. Only the shape is shared, which is why TurnOnSource
// takes a ProductionSource and works for all three.
//
// A Big Bang wipes it hardest of all: SpeedModuleSet.BigBangClean() clears the
// dictionary outright, and every module is locked again behind the rocket
// launch pad, so the 1.5 s burst after the reset has literally nothing to turn
// on. This is the case the five-second sweep was written for - the module is
// switched on the moment the launch pad is rebuilt and the module reappears,
// which is hours later and long after any burst has finished.
//
// All unlocked modules are switched on, not just the efficient ones. They are
// a throughput ladder, not alternatives - 'launch' runs at 99% efficiency but
// only moves 5e2 energy a tick, and 'controlled_nuclear_fission' wastes over
// half of what it takes but moves 1e8 - and the engine's own cap
// (Engine.IsFull) bounds what the whole stack can ever draw. New jobs are
// appended to the BOTTOM of the priority list, so the modules take whatever
// energy is left after research and the collectors, never the other way round.
// EngineModules in the config file switches the whole lot off for anyone who
// would rather hoard the energy.
//
// ---------------------------------------------------------------------------
// Every source needs a priority job, or it does nothing at all
//
// This is the same trap the research notes above describe, and v1.1.0 walked
// straight into it for the collectors. A ProductionSource does not tick
// because its percentage is above zero; it ticks because PriorityJobManager
// holds a job for it (PriorityJob.Tick switches on JobType and calls
// energySource / elementSource / speedModule.Tick). The percentage is only
// what isPrioritizable reports.
//
// The game builds those jobs in TechnoManager.LoadEEIRPriorityJobs and
// SpaceshipManager.LoadSpeedModulePriorityJobs, and both walk the panel's rows
// with the default GetComponentsInChildren<T>() - active only - acting on the
// ones flagged pendingStateUpdate. So a collector switched on while its panel
// is shut, or before its row exists at all, reads as ON and produces nothing.
// The spaceship's collectors are the obvious victim: they are in reach from
// every planet, and their rows are as good as never on screen.
//
// So the job is inserted here, exactly as StartJob has always done for
// research and infrastructure. AttemptInsertJob guards on ContainsKey, so it
// is safe if the game gets there first.
//
// The Space Elevator needs no hook of its own, and must not be given one.
// SidePanel.SetOff routes it through Departure like everything else, just with
// the distance set to zero:
//
//     player.location.Departure(selectedStarIdx, ScientificNotation.zero);
//
// so TravelTick sees distanceTravelled >= distance on the next tick, calls
// Arrive, and raises the arrive flag. Both hooks therefore fire, a frame or
// two apart. That is harmless: the second Schedule only replaces the first
// one's timer, so a single pass runs, and it runs after the landing. Adding a
// third hook for it would be a hook for something that is already covered
// twice.
//
// ---------------------------------------------------------------------------
// Keeping the priority list through a Big Bang (level 4, v1.5.0)
//
// ----------------------------------------------------------------------
// The spaceship's own jobs go last but one (v1.12.0)
// ----------------------------------------------------------------------
//
// Fuzzied: "Treasure Magnet and the other Space Research/infra - I asked earlier
// that they are automatically moved above the Engine because they eat a ton of
// Energy and Resources that will stop planet specific stuff from completing."
//
// He is right about the cost, and the reason is structural rather than a
// matter of taste. As far as the priority list is concerned the spaceship is a
// star like any other, but it is the ONE star you never leave: its research
// and buildings are in reach at every planet once 'space_ir_anywhere' is done,
// and its collectors always are. So the spaceship's rows pile up for ever,
// while the planet you are standing on has a finite list you are trying to
// finish before you fly on. Left in the middle of the queue they take energy
// and resources off the jobs that are the reason you are at this planet at all.
//
// So they sink. The order this produces is
//
//     [ everything belonging to the planet you are at ]
//     [ the spaceship's research and buildings ]
//     [ the four money pits - see below ]
//     [ the engine modules ]
//
// which is exactly "above the Engine" and below everything else. SinkSpaceJobs
// runs immediately before SinkEngineJobs and uses the same mechanism, so the
// engine still ends up underneath: sinking twice, in that order, leaves the
// second sink's rows at the very bottom.
//
// The collectors are NOT in that list, and until v1.14.0 they were (v1.14.0)
//
// Fuzzied: "its dumping Solar energy from space, Biomass power and plant based
// biomass to the last slot, resource collection should be moved to the top not
// down to an Accumulator".
//
// The reason to sink the spaceship's rows was that they eat energy and
// resources the planet needs for its own work. That is true of Interplanetary
// Constructions and the boosts. It is the exact opposite of true for Solar
// Energy From Space, Biomass Power, Gas Molecule Collection and Plant-based
// Biomass, which are collectors: they are where the energy and resources come
// FROM. SinkSpaceJobs asked "does the spaceship own this?" when the question
// it meant to ask was "does this drain?", so all four went to the bottom, and
// they crossed every Accumulation Layer on the way.
//
// That is what "everything is squished to the last Accumulation" was. A layer
// stops the rows below it once its limits are met, which is a fence when the
// row below is a drain and a tourniquet when the row below is a supply. With
// four collectors pulled down past all four of his layers on every sweep, the
// layers had nothing left between them and ended up adjacent, and the planet's
// own supply was sitting underneath all of them.
//
// Two halves to the repair, because either alone is not enough:
//
//   1. SinkSpaceJobs skips collectors. Stops it happening again.
//   2. RaiseCollectorJobs pulls any collector that is below a layer back to
//      the top of the list. Needed because the saves are already wrong, and
//      nothing else in here has ever moved a row UP across a layer, so without
//      it a damaged list stays damaged for good.
//
// Moving up across a layer is the one direction that can let something run
// that the player fenced off deliberately, so (2) is a switch,
// CollectorsFirst, on by default.
//
//
// A rule that never stops running is a rule the player cannot argue with
// (v1.15.0)
//
// Fuzzied: "So everything is still janked back down if I try to reorder it, and
// the collectors are janked back up even if it is just within an Accumulator
// with nothing else in it".
//
// RaiseCollectorJobs and SinkSpaceJobs enforce a shape. Both ran on every
// quiet sweep, so twelve times a minute, forever. v1.14.2 narrowed what they
// touch, which helped and did not fix it, because the problem was never the
// blast radius - it was the frequency. Where a rule and the player disagree
// about where a row goes, one of them loses, and a rule that re-runs every
// five seconds always wins.
//
// Both now run on the arrival burst and on a sweep that started, added or
// restored something. A sweep where nothing happened leaves the panel alone,
// and a player dragging rows about generates nothing but sweeps where nothing
// happened. The rules still do their job - they run at exactly the moments
// there is something for them to do - and the rest of the time the list
// belongs to whoever is looking at it.
//
// SinkEngineJobs keeps its every-sweep behaviour on purpose. Fuzzied asked for
// that one in those words: "it should always shuffle those 4 Engine Refuel's
// to the end". EngineLast turns it off for anyone who disagrees.
//
//
// The remembered list and the shaping rules are two authorities on one list
// (v1.16.0)
//
// Fuzzied: "Everything just got squished to the bottom Accumulation again when I
// loaded from menu".
//
// v1.15.0 stopped the shaping passes running on quiet sweeps, which is what
// made dragging stick. It left one door open, and it is the widest one: a load
// is an arrival, so Run(true) sets announce, announce sets reshape, and both
// shaping passes run over a list RestoreRememberedOrder has just put back.
//
// So the restore and the rules fought, and the rules went last. Measured
// rather than reasoned - the ledger beside the save is written on every
// autosave and Merge lets "the live list win outright", so consecutive writes
// are a recording of the panel. Two writes either side of one load, star 3:
//
//     05:31  ... rt_extra_equip_slot_4, ACC1, rt_solar_energy_boost,
//                rt_solar_resource_boost, ACC2, it_interplanetary_...
//     05:33  ... rt_extra_equip_slot_4, ACC1, ACC2, rt_solar_resource_boost,
//                rt_solar_energy_boost, it_interplanetary_...
//
// The two boosts are spaceship research and not collectors, so SinkSpaceJobs
// dragged them under both layers; they even come back in its order rather than
// Fuzzied's, which is the fingerprint of a rule rewriting a row instead of a
// hand moving it. Everything under a satisfied layer then stops.
//
// The mistake is treating a load as a moment that needs shaping. It is the
// opposite: it is the moment the player's own arrangement comes back, and
// there is nothing to decide because the memory already holds the answer -
// including whatever shape these same rules gave those rows when they first
// appeared.
//
// So the rules now only ever place a row the memory has no place for. That
// window is exactly one pass wide, because RefreshMemory runs at the END of
// Run: a brand new spaceship research is unknown on the pass it appears and
// gets sunk under the layers as before, and from the next pass on it is
// remembered and never touched again. Nothing is lost and the player stops
// being overruled.
//
// Below level 4, or with Remember off, there is no memory and both passes
// behave exactly as they always did. That is the fallback, not a special case.
//
// Which rows are the spaceship's is read from the game's own template
// dictionaries for star index -1 - StarMap.researchTemplateDict and its three
// siblings, keyed by star id - and turned into the prefixedId that PriorityJob
// uses for its own ids ("rt_", "it_", "enst_", "elst_"). Reading the star
// rather than matching on the id text means a data patch that adds spaceship
// content is picked up for free, and a planet job that happens to share a name
// is never caught by mistake. The set is built once and cached; templates do
// not change while the game is running.
//
// THE MONEY PITS. Fuzzied, on reaching the sun: "two new extremely long
// researches unlock: Solar Energy/Resource Boost. These needs to be placed
// under the other Space stuff but above the Engines", and then "Interplanetary
// Construction/Knowledge under Space stuff and over Engines as well". These
// four are spaceship jobs already, so they sink with the rest - but they are
// the ones that run for days and swallow everything while they do, so they get
// their own bucket at the bottom of the spaceship block. The list is
// SpaceTailIds in the config file rather than four ids in the code, because
// the next one of these will turn up the same way this one did: in a message
// from somebody playing.
//
// Like the engine sink this is allowed to cross Accumulation Layers on the way
// down, and for the same reason: the running accumulation model is folded
// forward with Math.Max, so moving a row DOWN past a fence can only fence it
// off harder, never hand it something it did not have. Moving up is what would
// be a lie, and nothing here moves up.
//
// Engine modules are excluded, because they belong to the spaceship too and
// would otherwise be sunk here and then sunk again - harmless but pointless.
// Accumulation Layers are excluded because they are the fences themselves.
//
// It is a toggle - "Space last" - and it belongs to level 3, the same level as
// "Engine last", because it is the same kind of thing: an opinion about the
// order of the list rather than a decision to start something.
//
// One thing it deliberately does NOT do: it does not put the spaceship's rows
// below an Accumulation Layer that they were above and then claim to have
// respected where the player dragged them. It sinks them every pass, exactly
// as EngineLast does - Fuzzied: "it should always shuffle those 4 Engine
// Refuel's to the end". If you want one of the spaceship's jobs high up, this
// is the toggle to turn off.
//
// ----------------------------------------------------------------------
// Jobs this mod will not start (v1.12.0)
// ----------------------------------------------------------------------
//
// Fuzzied: "Blackhole also needs to be Blacklisted from being turned on at this
// point in time until we figure out what to do with it". BLACKHOLE is the
// sun's second research, it costs 1e25 energy, and as things stand it does
// nothing for you when it lands - so a mod that starts whatever it can find
// would park your entire energy income on it for ever. NeverStart is a plain
// list of prefixedIds in the config file, checked in Gather - the one place
// both research and buildings become candidates - and in the collector walk.
// It only stops THIS MOD from starting them. Start one yourself and nothing
// here switches it off, because a job you started is a choice you made.
//
// Fuzzied: "Also keep the Added Accumulation Layer between resets" and then,
// sharpening it: "one thing is to keep them and the resource limit they had
// but also what research/infra queue was under them would be huge!"
//
// A Big Bang throws the whole arrangement away. BigBangManager.BigBangClean
// calls StarMap.BigBangClean, which gives every star a brand new
// StarControlSettings - and priorityIdList with it - and then
// PriorityJobManager.BigBangClean, which destroys every row. Layers, limits,
// task counts and the order underneath each layer all go at once, and putting
// them back by hand is half an hour of dragging.
//
// WHAT THE MEMORY IS. The game already has a perfectly good encoding for all
// of this: star.controlSettings.priorityIdList, which
// PriorityJobManager.ExportPriorityIdList writes from the rows' sibling order.
// A normal row exports as its job id; an Accumulation Layer exports as
//
//     *acc_<guid>|energy,air,water,soil,biomass,coal,silicon,iron|prod,res,infra,engine
//
// so the layer's limits and its four parallel-task counts are already in
// there, in place, in order. LoadPriority reads exactly that back. So this
// level does not invent a format - it just stops the game losing the one it
// has.
//
// HOW IT SURVIVES. A prefix on BigBangManager.BigBang snapshots every star's
// list (asking the game to export the current one first, since it only writes
// that on save), and a postfix writes them back into the freshly cleared
// control settings. That is also why the memory needs no file of its own: it
// lives in the save, per save slot, the way the game's own copy does.
//
// The restore only ever writes into a list that is empty, so running it twice
// - once after the reset and once after ConfirmUpgrades, in case level 4 was
// what the player just bought - cannot clobber anything.
//
// HOW THE JOBS FIND THEIR WAY HOME. After a reset the rows come back over
// hours, as research unlocks and buildings are rebuilt, and each one is
// appended to the bottom by AttemptInsertJob. So every pass:
//
//   1. any remembered layer the game no longer has is rebuilt, with its
//      limits, by handing the remembered string straight to the game's own
//      AddAccumulationLayer(string, int);
//   2. the rows the memory knows are permuted back into their remembered
//      order - and ONLY among the slots they already occupy, so a row the
//      memory has never seen is not moved at all;
//   3. level 3's ordering pass runs afterwards, which is the right way round:
//      level 4 decides which layer a job sits under, level 3 tidies within
//      each stretch between two layers and never crosses one. Level 3's sort
//      is stable, so where it has no opinion the remembered order stands.
//
// AND WHY IT DOES NOT FIGHT YOU. Only rows that have JUST APPEARED are ever
// placed. Everything that was already on screen at the end of the previous
// pass keeps its position exactly, whoever put it there.
//
// v1.6.0 and earlier got this wrong, and Fuzzied found it within a day: "It
// moves my reorderings while on the moon, I try to drag energy tank away from
// the Layer and it drops it back." The pass permuted every remembered row into
// remembered order and only then wrote the result back to the memory, so a
// drag was undone by the next sweep five seconds later - and the memory then
// recorded the undone version, which made it permanent. The refresh-at-the-end
// was never going to save it: the apply runs first, in the same pass.
//
// The honest rule is that the memory exists to give a RETURNING job a home,
// not to hold an opinion about a list the player is looking at. So the pass
// keeps a per-star set of the ids that were present last time, and a row is
// only moved if it is not in that set. A returning research still drops into
// its old slot, and a row you dragged one second ago is simply not eligible to
// be moved at all. That set is rebuilt from the live rows at the end of every
// pass, so a job that completes and comes back hours later counts as new
// again, and it is cleared by a Big Bang so that the whole rebuilt list is
// eligible.
//
// Only the rows that are NOT back yet are carried over from the old list,
// anchored after whichever remembered row still exists above them. A layer you
// delete is struck from the memory by the Delete hook, so it is never
// resurrected.
//
// ---------------------------------------------------------------------------
// The record, and why it is not kept in the save (v1.6.0)
//
// Fuzzied: "Keep the list on file when big bang happens, then keeps a record of
// where research/Infra was when completed going forward and the order. So much
// of the old content can be automated if wanted. There should be an options in
// settings to toggle the different levels of this on and off, without needing
// to wipe it at a new big bang."
//
// Three things follow from that, and v1.5.0 got none of them quite right.
//
// FIRST, THE RECORD OUTLIVES THE SESSION. v1.5.0 kept the memory in a plain
// dictionary and only ever wrote it into the game's own field, which is gated
// on owning level 4. Big Bang without the 40,000 dark matter to hand, quit,
// and the arrangement was gone for good. It is now written to a companion file
// beside the save it belongs to - "auto_AutoSave.sti" gets
// "auto_AutoSave.sti.layers" - on a postfix of SaveLoadManager.SaveGameToPath,
// and read back on a postfix of LoadGameFromPath. Both take the full path, so
// each of the fifteen slots keeps its own record and copying a save copies its
// history with it. A new game never calls LoadGameFromPath, so it starts empty
// and overwrites any leftover file on its first autosave.
//
// SECOND, THE RECORD IS BUILT WHATEVER LEVEL YOU OWN. Keeping it costs a few
// kilobytes, and a record that only starts the day you can afford the upgrade
// is worth very little - the whole value is that it has been watching for a
// while. So the ExportPriorityIdList postfix now always folds the live rows
// into the record. What the level and the toggle gate is the other direction:
// handing it back.
//
// That distinction has to be enforced carefully, because the game's own loader
// would otherwise do level 4's job for free. LoadPriority walks the saved list
// and rebuilds every *acc_ entry through AddAccumulationLayer, and its
// SetSiblingIndex(i) walk puts the rows that DO exist into the order the list
// gives them, skipping the ids it does not know (PriorityJobManager:240). So
// writing the longer list into star.controlSettings.priorityIdList is itself
// the whole feature. Only an owner gets that write; everyone else's record
// stays in the companion file, where the game cannot see it.
//
// THIRD, A COMPLETED RESEARCH IS NOT A DELETED ONE. Research leaves the
// priority list when it finishes - AttemptRemoveJob destroys the row - so a
// snapshot of the live list is only ever a picture of what is unfinished. The
// record keeps every id it has ever seen, each anchored after whichever
// remembered row above it still exists, which is exactly Fuzzied's "where
// research/Infra was when completed". Over a few resets it fills out into a
// preferred order for content that has not been in the list for hours.
//
// Layers are the one thing that cannot simply accumulate: a new one gets a new
// guid every time, so a player rebuilding by hand each reset would pile up a
// generation per Big Bang. v1.5.0 solved that by treating "not on screen" as
// "deleted", which is wrong now that the record has to survive the reset that
// wipes the screen. Instead:
//
//   - PriorityAccumulationItem.Delete is patched, so a layer the player
//     actually deletes is struck from the record there and then. That is the
//     honest signal, and it is one line;
//   - the Big Bang snapshot drops any remembered layer that is not live at
//     that moment, which clears out the previous generation. At most one stale
//     generation can exist, between two Big Bangs, and only for a player who
//     is rebuilding layers by hand because they do not own level 4 yet.
//
// AND THE TOGGLES DO NOT WIPE ANYTHING. Turning the level 4 switch off in
// Settings stops the record being handed back. It does not stop it being kept,
// and it does not delete the file, so turning it back on picks up where it
// left off. Same for owning level 3 but not 4, or not owning the upgrade at
// all: the record is running the whole time.
//
// ---------------------------------------------------------------------------
// A ROW THAT COMES BACK IS A NEW OBJECT (v1.12.1)
//
// Fuzzied: "I made backups before exiting, when I start now everything is
// squished to the last Accumulation."
//
// His Mars list came back with all four Accumulation Layers bunched together
// and every Mars research and building below the strictest of them. The five
// rows that moved were exactly the planet's own research and infra; the
// collectors, the layers and the spaceship rows had not moved at all.
//
// That shape is not a sort going wrong, it is rows being REBUILT. The game
// keeps the priority list in step with the panels from
// TechnoManager.LoadEEIRPriorityJobs, which runs on every FixedUpdate and,
// for any item flagged pendingStateUpdate, calls AttemptInsertJob when the
// job is prioritizable and AttemptRemoveJob when it is not. AttemptRemoveJob
// destroys the row outright, and AttemptInsertJob instantiates the new one
// under priorityJobContent - which is to say at the BOTTOM of the list,
// below every layer. Research and infra pass through "not prioritizable" as
// a save is loaded, before researchIdResearchingDict has been applied; a
// collector does not, which is why only those rows moved.
//
// Putting a returned row back is precisely what PlaceReturnedRows is for. It
// did nothing here because seenRows recorded row IDS, and by id a rebuilt row
// is the same row - so the memory read it as "where the player left it" and
// politely left it at the bottom. Then RefreshMemory wrote that arrangement
// into the record, and the next launch had nothing good left to restore.
//
// seenRows therefore records the id AND the instance id of the object that
// carried it. Same id on the same object is the player's arrangement and is
// never touched, which is still what keeps a drag from being undone. Same id
// on a new object is a row that has just come back, and it goes where the
// record says. Nothing else changes, and a rebuilt row that the player HAD
// dragged still lands where they dragged it, because the record learned that
// position on the pass after the drag.
//
// ---------------------------------------------------------------------------
// Departure is the only window, so it cannot afford a delay (v1.4.0)
//
// Fuzzied: "The space stuff says its unavailable though, I cannot activate it
// now. Once you start them they become available no mater what planet you are
// on after."
//
// He is right, and it is a sharper rule than it looks. TechnoPanel.LoadStarData
// only lists the space star (-1) for research and infra when you are IN FLIGHT,
// or when 'space_ir_anywhere' is done. So before that research exists, the
// flight is the ONLY moment the spaceship's own research and buildings can be
// switched on. Afterwards they keep running from anywhere, because nothing
// removes a priority job once it is in: LoadEEIRPriorityJobs only ever calls
// AttemptRemoveJob for a row that exists and is not prioritizable, and at a
// planet those rows do not exist at all. Start it in flight and it runs for
// good.
//
// Every other hook can afford FIRST_DELAY_SECONDS, because the thing it is
// waiting for - locationChangeEvent rebuilding the Techno panel - happens
// straight away and the star is still there afterwards. Departure cannot: a
// well fed engine crosses to the Moon in about a second, which is shorter than
// the 1.5 s settle, so the pass ran after the landing with staying = moon and
// the spaceship was never touched. That is what Fuzzied saw.
//
// So the departure hook now runs a pass on the spot, in the postfix, and arms
// the usual delayed one behind it. Acting that early is safe here for the same
// reason StartJob is written the long way round: it writes the control
// settings as well as the model, so the panel rebuild that follows reads our
// values back rather than overwriting them. Location.Departure has already set
// stayingStarIndex = -1 by the time the postfix runs, so LoadStarData's own
// rule agrees that the spaceship is what is workable.
//
// ---------------------------------------------------------------------------
// Ordering the whole list, not just what we added (v1.4.0)
//
// Fuzzied: "This is why I wanted you to move resource adding to the top and the
// most draining to the bottom, it fills down."
//
// SmartOrdering used to sort only the research and infra it was about to add,
// among themselves. That does nothing about the shape of the list as a whole,
// which is what actually decides who eats first. So at level 3 it now sorts
// the priority list itself:
//
//   energy production -> resource production -> research and infra -> engine
//
// Accumulation layers are the reason this is done in segments rather than in
// one sort. A layer means "everything below me leaves this much alone", so
// moving a job across one silently rewrites what the player fenced off. The
// list is therefore split at every accumulation row and each segment is sorted
// on its own; the layers never move, and nothing ever crosses one.
//
// The sort is stable within a rank - decorated with the original index,
// because List.Sort is not stable - so the order you chose between two
// collectors survives. It only runs in a pass that actually started something,
// since a pass that added nothing cannot have disturbed anything.
//
// ---------------------------------------------------------------------------
// The engine is the one thing that DOES cross a layer (v1.7.0)
//
// Fuzzied, looking at Launch sitting above his Accumulation Layer: "it doesn't
// shuffle Launch at the end, it should always shuffle those 4 Engine Refuel's
// to the end as resources then research/infra are always more important."
//
// He is right, and the segment rule above was the thing stopping it. An engine
// row above a layer is in its own stretch and can only ever sink to the bottom
// of THAT stretch, which is what he was looking at.
//
// Crossing a layer downwards is safe for an engine row, and the reason is in
// RunJobs. The running ERAccumulationModel is only ever folded forward with
// Update(), which takes Math.Max of every field, so the reserve a row has to
// respect can only GROW as you go down the list. Push an engine row further
// down and it is fenced off by more of the player's layers, never fewer. It
// cannot end up eating something a layer was protecting - the failure the
// segment rule exists to prevent is a row moving UP across a fence, and this
// only ever moves down. SpeedModule.ExchangeEnergyForEngineProgress asks for
// Math.Max(storage lock, energyAccumulation), so a layer is in fact the only
// thing in the game that meaningfully restrains a module at all: position
// alone is first refusal within a tick, not a budget (see above).
//
// Two honest consequences.
//
// FIRST, this runs on EVERY pass, not only one that started something.
// "Always" is what was asked for, and a pass that adds nothing is exactly when
// a hand drag would otherwise stand. So this is the one place the mod will
// undo a drag - dragging an engine row above a layer to fill the tank quickly
// will not stick while EngineLast is on. That is the trade, it is deliberate,
// and EngineLast (level 3's second Settings toggle) turns it off.
//
// SECOND, ParallelTaskCountModel.Update REPLACES rather than maxes, so the
// engine slot count that applies below the last layer is that layer's, not the
// one the row had before. A player who has set a layer's engine count to 0 has
// asked for no engine work below it and will now get exactly that. That is
// their own fence doing its job, but it is worth knowing.
//
// ---------------------------------------------------------------------------
// The engine goes back on after landing (v1.8.0)
//
// Fuzzied: "Engine also auto shuts off when you arrive on a planet, regardless
// if I toggled them on this run. Moving back and fourth from Earth <> Moon.
// They should auto on but be moved to the bottom."
//
// This one is not a bug in the mod, it is the base game, and it is not in any
// decompiled method - it is wired in the scene. level1 carries a UnityEvent
// called OnStarArrival with three persistent calls on it:
//
//     PriorityJobManager.RemoveAllTasks
//     SpaceshipPanel.StopAllSpeedModules
//     UnlockManager.ForceAllUpdates
//
// which is why StopAllSpeedModules has no C# caller anywhere. The event is
// SidePanel.locationChangeEvent, the only UnityEvent anything invokes at a
// journey's end. So arriving wipes the priority list, switches every engine
// module to 0, and asks for a full rebuild. The rebuild then makes it
// permanent: UnlockManager.Update calls ExportEngineControlSettings BEFORE
// UnlockRequirementUpdate, so speedModulesInputDict is overwritten with the
// zeros that were just written, and LoadSpeedModules(hardReset: true) hands
// those same zeros back. The player's choice is not merely switched off, it is
// forgotten. That is exactly "regardless if I toggled them on this run".
//
// So the percentages have to be caught before that runs. A prefix on
// StopAllSpeedModules is the precise moment: everything on screen is still
// the player's, and nothing else in the game calls it.
//
// THE WIPE HAPPENS TWICE PER TRIP. locationChangeEvent is invoked from three
// places: Location.SetLocation (a Big Bang or a load), SidePanel line 407
// (pressing Travel) and SidePanel.Arrive. So setting off empties the dials
// too, and that one the game means - the tank you filled before you left is
// what carries you across, and switching the modules back on mid flight would
// burn energy for a speed boost nobody asked this mod for. Only a landing gets
// them back, told apart by Location.isTravelling, which Departure() sets true
// before the event and Arrive() sets false before it.
//
// That is also what engineWiped is for. It is armed by the departure wipe and
// stays armed for the whole crossing, and NOTHING is recorded while it is
// armed - otherwise the sweep, or the second wipe on landing, would write the
// game's zeros over the values caught at the departure gate. When it is clear,
// every pass records the current percentages, so switching a module off
// yourself while you are standing somewhere is remembered and is not undone at
// the next landing. Switching one off mid flight is not, and cannot be: the
// dials are all zero out there and there is nothing to read.
//
// Both halves of this obey that, and only one of them did until v1.11.2.
// Fuzzied: "Engines are turning on when Im in flight, had to manually several
// times turn them off". RestoreEngineChoice had the isTravelling check from
// the day it was written; StartSpeedModules, which is the EngineModules half,
// never got it, so out in the crossing it switched them straight back on -
// and again five seconds after he switched them off. The rule belongs to the
// engine, not to one setting: nothing switches a module on while the ship is
// moving. Repairing the priority job of a module that IS on still happens out
// there, because that starts nothing, it only makes a choice already made
// actually work.
//
// This is restoring a choice, not making one. It never switches on a module
// that was at 0 when you left - that is still EngineModules. It is on by
// default because the state it restores is the state the player themselves set
// a minute earlier, and because v1.7.0 finally gives it somewhere safe to sit:
// below every Accumulation Layer, where the fence you built is what decides how
// fast the tank fills.
//
// ---------------------------------------------------------------------------
// EngineModules gets a switch you can reach (v1.9.0)
//
// Fuzzied, after installing v1.8.0: "Engines are not on though."
//
// Right, and v1.8.0 could not have made them so. Restoring a choice is not
// making one, and the shuttle run he actually described - "moving back and
// fourth from Earth <> Moon", tank refilling by itself - is the second of
// those. That is EngineModules, which has existed since v1.2.0 and lived only
// in the config file, where a player who is not going to open BepInEx\config
// in a text editor could never find it.
//
// So it gets a row in Settings like everything else. The level it is shown at
// is 1, because it does not lean on the ordering or the memory to work.
//
// It is still FALSE by default, and the v1.3.0 section above is still the
// reason: a filling tank takes every joule it can reach, for hours, and a
// stranger's save should not start doing that because they installed a mod.
// What HAS changed is that the honest fix now exists. Before v1.7.0 there was
// nowhere safe to put a running module - "bottom of the list" was worth
// nothing, because energy is a bank and position is only first refusal within
// a tick. Now the modules sit below every Accumulation Layer, and a layer is a
// real floor they cannot dig below. Switching this on next to a layer is a
// reasonable thing to do rather than the trap it used to be, which is why it
// is worth putting in front of people instead of hiding it.
//
// ---------------------------------------------------------------------------
// Why the engine modules are opt-in (v1.3.0)
//
// v1.2.0 switched the engine modules on for you, and the claim in its config
// text - "they only ever use energy nothing else wanted" - was simply wrong.
// Fuzzied hit the consequence within a day: Solar Energy +3.67e5/s, Launch
// -3.66e5/s, and every research, building and collector on the Moon sitting
// at -0/s because there was nothing left for them.
//
// Two separate mistakes were behind that claim.
//
// 1. "Bottom of the list" is not where they end up. AttemptInsertJob
//    instantiates into priorityJobContent as the last sibling AT THE TIME OF
//    INSERTION. Launch went in while we were still at Earth, so every
//    collector the Moon pass added afterwards landed BELOW it. Lunar Soil,
//    the resource the Moon's research needs, was dead last.
//
// 2. Even dead last would not have saved it. AvailableEnergyPercentage reads
//    energy.amount - the STORED pool - so what matters is what is in the bank
//    when a job ticks, not who is above whom. A speed module asks for
//    baseBarFillRequiredEnergy * outputPercentage every single tick and takes
//    whatever fraction of that the bank can cover, so it drains the bank to
//    the floor at the end of every tick and the jobs at the TOP of the list
//    find it empty when the next tick starts. Position decides who gets first
//    refusal within one tick; it is not a budget.
//
// A SpeedModule is meant to be greedy - that is why the game gives you the
// Accumulation Layer button to fence it off, and why filling the tank is
// normally something you do deliberately, just before a trip. Doing it
// permanently and automatically starves the economy for as long as it takes
// to fill, which with Modifiers.engineCap in play is hours. There is no
// signal for "I am about to travel", so there is no honest way to time it.
// EngineModules therefore defaults to FALSE.
//
// What is kept, because it was a real bug and the whole point of the report:
// the game already remembers your own setting across a Big Bang.
// SpeedModuleSet.BigBangClean() clears the modules, but the panel's
// speedModulesInputDict survives, and LoadSpeedModules calls SwitchTo(saved)
// when it rebuilds the row. The reason Fuzzied's Launch still did nothing is
// the priority job, not the percentage - see the next section. So the module
// walk now happens whatever EngineModules says; only the switching-on is
// gated. A module you turned on yourself gets repaired either way.
//
// When they ARE switched on, every module this plugin started is pushed to
// the last sibling at the end of any pass that added something, so at least
// the ordering claim becomes true. Modules you placed yourself are never
// moved, and a pass that starts nothing does not touch the list at all.
//
// ---------------------------------------------------------------------------
// Not acting on an upgrade that has not been paid for (v1.2.1)
//
// Fuzzied: "after a big bang, when you tick off automated Arrival. It activates
// them before you Confirm Uppgrades :)"
//
// BigBangUpgradeItem.UpgradeButtonClicked -> NotifyManager -> SetUpgradeLevel
// writes upgradeLevels[id] straight away, at click time. That is what makes
// the ConfirmUpgrades hook work at all - by the time it fires the level is
// already current - but it also means ResolveLevel's GetUpgradeLevel reads a
// basket that is still open. The sweep runs every five seconds regardless, so
// ticking Automated Arrival started the whole planet while the confirm dialog
// was still up, and a basket the player then reset would have left everything
// running anyway.
//
// BigBangManager.isInUpgradeMode is the game's own answer to "is this basket
// still open", and the ordering around it is exactly what is needed:
//
//   BigBangPanel:120-121   EnterUpgradeMode(); BigBangManager.BigBang();
//   BigBangPanel:160-161   isInUpgradeMode = false; ConfirmUpgrades();
//
// It is raised one line BEFORE BigBang() itself, and cleared one line BEFORE
// ConfirmUpgrades. So holding off while it is true costs nothing: the BigBang
// hook's pass simply waits, and the ConfirmUpgrades hook still schedules a
// fresh pass the moment the basket closes. The wait is done in Update rather
// than only in Run so that a long stay in the upgrade screen does not burn
// the burst's twenty retries and give up with "nothing was ready in time".
//
// The flag is saved with the game (Save.isInUpgradeMode), so quitting with an
// unconfirmed basket keeps this out of the way until it is confirmed. That
// cannot deadlock - confirming is the only way out of the screen.
//
// ---------------------------------------------------------------------------
// The Settings rows move to the left column, and say what they do (v1.11.0)
//
// Fuzzied, with a screenshot of the Settings panel: "The toggle text is really
// hard to read as it is. Can we move that section to under the tutorial
// progress with same size. And is it possible to have a small pop-up text
// explaining what each toggle does? Engine stays on f example is not self
// explanatory."
//
// Both halves of that come from the same shortcut. The rows are clones of the
// game's own hide-card-controls toggle, so they inherited its home under the
// Card heading in the right hand column, and its 20 unit height - half what
// the buttons on the left get, and the smallest text in the panel.
//
// So the block gets a section of its own, cloned from the Tutorial Progress
// container and dropped in directly underneath it: a heading reading "Auto
// start", then one row per level the player owns, each as tall as the buttons
// beside it.
//
// Three things here are easy to get wrong:
//
//   - the labels lose their "Auto start: " prefix. The heading says it once,
//     the column is only 200 units wide, and a bigger font in a narrow column
//     is only an improvement if the words still fit on the line;
//   - the text auto sizes up to a ceiling rather than being set to a number.
//     The panel is scaled to a 1920 wide canvas matched on WIDTH, so a 21:9
//     screen gets a canvas about 810 units tall instead of 1080 and everything
//     in it is tighter. v1.11.0 took that ceiling from the button's own label
//     and Fuzzied got text smaller than what it replaced: that label auto sizes,
//     so fontSize is whatever it last drew at, not the size it is allowed. Ask
//     an auto sizing label for fontSizeMax, and treat anything outside 40-60%
//     of the row height as a number that cannot be right - half the row height
//     is what the buttons look like, and is the fallback;
//   - and for the same reason the rows measure what is left of the column and
//     shrink when seven of them will not fit. A row that falls off the bottom
//     of the panel cannot be clicked at all.
//
// The tooltips are the game's own. TooltipComposite falls back to its public
// defaultTooltip whenever the object has nothing implementing
// ITooltipAvailable on it, which a cloned toggle has not, so one component and
// one string per row is the whole of it: no custom UI, and it behaves like
// every other tooltip in the game, dismiss hint and all. Each row also gets a
// fully transparent Image, so the whole row answers the mouse instead of just
// the tick box and the words.
//
// ---------------------------------------------------------------------------
// A brand new row must not land in a fenced off stretch (v1.10.0)
//
// Fuzzied, with a screenshot of his Venus list: "Also, plant based Biomass
// collector is moved to the Accumilation" - the row was sitting directly
// under his Accumulation Layer.
//
// Nothing moved it. It was PUT there, and by us. AttemptInsertJob appends to
// the end of the list, and the end of the list is below every layer the
// player has built, so any row this plugin adds while a layer exists starts
// life on the wrong side of the fence. EnsureJob is the likeliest way in: a
// collector that is already switched on but was never queued - the spaceship
// ones especially, since their rows are as good as never on screen - gets
// queued by the repair path, and appended.
//
// Then it sets. Level 3 sorts within each stretch and never crosses a layer,
// so the row can only ever be sorted to the TOP of the fenced stretch, which
// is exactly where Fuzzied found it. Level 4 writes down where every row sits,
// so from the next save on, that position is "the player's arrangement" and
// is faithfully restored for good.
//
// Below a layer is the wrong side for a producer in particular. A layer is a
// floor: rows under it can only reach what is above the line it holds. So a
// collector that landed there is the one thing being starved by the fence
// while it is trying to fill what the fence protects.
//
// The fix is at the moment of adding, not in the ordering. A row that has just
// been created has never been anywhere, so placing it is not moving anything
// of the player's - which is the promise the rest of this file keeps. So:
//
//   - only rows added by THIS pass are eligible (addedThisPass, recorded at
//     the four AttemptInsertJob call sites, and only when the job was
//     genuinely absent beforehand);
//   - and only if the memory has never heard of that row. If it has a place
//     for it, that place is the player's arrangement - possibly below a layer
//     on purpose - and it wins. This is what stops the lift from undoing the
//     memory every time you land somewhere;
//   - engine rows are never lifted: they are meant to be at the bottom, and
//     SinkEngineJobs puts them there a few lines later.
//
// A row already stuck below a layer stays stuck, and that is deliberate: it is
// established, the memory holds its place, and moving it would be exactly the
// thing this plugin promises never to do. Drag it up once and it stays - the
// memory records what you are looking at at the end of every pass.
//
// ---------------------------------------------------------------------------
// The sweep, and not fighting the player
//
// Fix 4 means re-running the pass every few seconds, and a pass that simply
// restarted everything it could would make it impossible to switch anything
// off: stop a research, and it comes back five seconds later. So the sweep
// remembers what it has already switched on and never touches the same thing
// twice. If it is off again, somebody turned it off, and that somebody is the
// player.
//
// That memory is cleared on every arrival, departure and Big Bang, which are
// exactly the moments the game itself resets your setup. So those still behave
// as v1.0 did - a clean full pass over everything - and only the quiet time in
// between is governed by "once each". It is deliberately not saved to disk: a
// restarted game gets one fresh pass, which is no worse than an arrival.
//
// Things it deliberately leaves alone:
//   - a building you switched OFF stays off, and is not built;
//   - anything whose target level you set to where it already is;
//   - anything still locked, using the game's own UnlockManager check;
//   - every job that was already running;
//   - a collector or engine module already running at any percentage at all,
//     even 1%, since that is a number somebody chose;
//   - anything the sweep has already started once (see above).
//
// ---------------------------------------------------------------------------
// An Accumulation Layer belongs to one star (v1.19.0)
//
// Fuzzied: "Going to Space keeps adding a bunch of weird Accumulation, are
// these a set of old ones that keep coming back?" They were, and they were
// not copies: the guids on Space matched the guids on his planets exactly.
//
// The game has no notion of a layer belonging anywhere. Job rows do - 
// TechnoManager.LoadEEIRPriorityJobs adds and removes them as the star you
// are standing on changes - but nothing in the game ever removes an
// Accumulation Layer. UnlockManager.UnlockRequirementUpdate reloads the jobs
// and calls PriorityJobManager.LoadPriority, and LoadPriority only ever ADDS:
//
//     if (text.StartsWith("*acc_")) { AddAccumulationLayer(text, i); }
//
// So the layers from the star you just left are still sitting in the list,
// and ExportPriorityIdList writes the whole visible list - layers included -
// into whichever star is current now.
//
// The sink is Space, because Location.stayingStarIndex is -1 for the whole
// flight. Every departure donates the departure planet's layers to star -1,
// where LoadPriority faithfully rebuilds them on every load afterwards. Four
// planets had donated to Fuzzied's Space list by the time he noticed.
//
// This is not cosmetic. RunJobs folds each layer forward with
// ERAccumulationModel.Update, which is Math.Max per resource, and it only
// resets at the NEXT layer - never back to zero. Eight stacked layers meant
// everything below the seventh, which was all of his Space production and all
// four research and infra rows, ran under a 10% energy and 5% biomass
// reserve he had set on Jupiter.
//
// The repair gives layers the one thing they were missing, a home star:
//
//   - layerHome maps layer id -> star index. It is rebuilt from the saved
//     lists whenever a game is loaded, and a layer the player creates is
//     recorded against the star they created it on.
//   - EvictForeignLayers drops any live layer whose home is not the star we
//     are on. It runs on LoadPriority, so the screen is right the moment you
//     arrive, and again as a prefix on ExportPriorityIdList, which is the
//     only moment a layer id can enter a star's saved list at all. Guarding
//     the write is what makes this airtight rather than a mop.
//   - The rebuild also repairs damage already in the save. A layer id found
//     in more than one star's list is kept in the first star that claims it,
//     scanning the planets in order and Space last, and struck from the rest.
//     Planets beat Space because Space is the sink, and the lower planet wins
//     a planet-to-planet tie because you travel outwards, so the copy always
//     lands on the later star. Travelling inwards could in principle leave a
//     layer on the wrong planet; the cost of that is one layer sitting
//     somewhere unwanted, which is one click to delete.
//
// Eviction has to free the dictionary slot as well as destroy the row.
// AddAccumulationLayer(string, int) is guarded by
// !priorityJobDict.ContainsKey(text), so a layer left in the dictionary can
// never be rebuilt - which is exactly why the game's own Delete leaves a
// layer unrebuildable for the rest of the session. AttemptRemoveJob does
// both. The transform is unparented first because Object.Destroy is deferred
// to the end of the frame and GetComponentsInChildren would still find it,
// which would hand the export the very row we just evicted.
//
// The priority memory has to agree, or it would simply put them back: a
// struck id is removed from the remembered list for that star at the same
// time, otherwise MergeExportedList folds it straight back into the save.
//
// v1.19.1. Fuzzied: "Mercury now didnt have any Accumulation". His log says
// the record for Mercury still held the layer, that the record was read, and
// that the rebuild was skipped anyway - and the only branch that does that
// is the dictionary already holding the id. So a row had been destroyed
// somewhere without the dictionary slot being freed, and from that moment
// the layer was unrebuildable: the game's LoadPriority is guarded by the
// same ContainsKey, so neither side could ever put it back.
//
// The paragraph above says that hazard out loud but only eviction was
// taking care of it. ClearDeadLayers now frees the slot of any layer that
// has no row left, wherever the row went, and it runs in the two places
// that matter: before the memory rebuilds a layer, and before LoadPriority
// rebuilds one from the star's saved list. A layer with a row is never
// touched, so there is nothing here for a working list to trip over.
//
// Deleting a layer on purpose now frees the slot too, and takes the id out
// of every star's saved list on the way. Before, the leftover entry was what
// kept a deleted layer from coming back; with the slot freed, the saved list
// would have rebuilt it on the next arrival.
//
// v1.19.2. Fuzzied, in Space: "the Accumulation laters keep changing, adding or
// re sporting the list randomly". The log is unambiguous: "rebuilt the
// accumulation layers", then four "is not this star's layer, taken off the
// list at star -1", then "rebuilt" again, round and round. The restore pass
// rebuilds every layer in this star's record without asking whose it is, and
// the eviction pass removes every live layer whose home is not this star. Put
// a foreign layer in the record and the two fight for as long as you stand
// there.
//
// RebuildLayerHomes was written to prevent exactly that and could not see it,
// because it only reads the game's own priorityIdList per star. The record
// beside the save is a separate list that can hold entries the game's list
// never had - the sidecar has *acc_0642f7e2 under both star 1 and star -1 -
// and a stray that lives only there is invisible to it. It now sweeps the
// record as well, and the restore pass refuses to rebuild a layer whose home
// is another star, so the loop cannot start even if a home is learned later.
//
// v1.19.3. Fuzzied, arriving at Mars: "it doesnt seem like it remembers where I
// had placed Research and Infra last time, is this not saved properly?"
//
// It is saved, and the restore is right. Star 5, the first pass after landing,
// nine rows on screen:
//
//     'keep my layers - restore' changed the priority list at star 5 to:
//     enst_solar_energy_space, enst_biomass_power_space,
//     elst_gas_molecule_collection, elst_plantbased_biomass, sm_launch,
//     ACC#8eef, sm_separation_one, sm_energy_burst, ACC#9c9f
//
// which is the record beside the save, exactly, with the rows that had not
// come back yet taken out. Nothing is lost at that point.
//
// What follows is the damage. AnchorLayers runs whenever the row count is
// different from last pass, and an arrival burst ADDS a row or ten every pass
// for a minute or two, so it ran on nearly all of them. Each time it rebuilds
// the whole panel: every layer is moved to sit under the nearest job above it
// IN THE MEMORY that is still on screen. During a burst the memory and the
// screen disagree about almost everything - the memory is full of rows that
// have not come back - so that anchor lands somewhere unrelated to where the
// player left the layer, and every Research and Infra row below it changes
// which fence it is behind. Two passes into Mars it had already moved ACC#8eef
// seven rows, and the line it wrote to say so, "a finished row had shifted
// your Accumulation Layers", was not true: nothing had finished, ten rows had
// been added.
//
// The trigger was the mistake, not the pass. Read the v1.16.0 note below: a
// layer climbs because a row ABOVE it was destroyed and everything under it
// shifted up a place. Adding a row cannot do that, because the game appends a
// new row at the BOTTOM of the list, below every layer there is. So the count
// going UP is never a reason to re-anchor anything, and the pass now only runs
// when rows have actually gone.
//
// A pass where one row finishes and two are added nets an increase and is
// missed, as it was before. That is the safe side of the trade: the next pass
// where the count drops picks the layers up, and the cost of running too often
// is an arrangement rewritten under the player, which is the whole complaint.
//
// v1.20.0. Fuzzied: "Space has no accumulation now". Both of his layers were
// gone from the Space list and the log says who took them:
//
//     ACC#8eef is not this star's layer, taken off the list at star -1
//     ACC#9c9f is not this star's layer, taken off the list at star -1
//
// That is this plugin. The v1.19.0 note above gives every layer one home
// star and settles a tie by scanning the planets first and Space last, on
// the reasoning that Space is where donated layers pile up so Space is never
// the owner. Fuzzied's two guids sat in the saved list of star 5 and of star
// -1, with identical settings on both. Mars claimed them, so every departure
// evicted them from Space, and the restore pass refused to rebuild them
// because their home was elsewhere.
//
// The ownership model was wrong, not just its tie-break. The game has never
// had one: LoadPriority rebuilds layers out of whichever star you are
// standing on, ExportPriorityIdList writes back to that same star, and
// AddAccumulationLayer(string, int) is keyed on the guid, so the same layer
// in two stars' lists is ordinary and works. A layer on Mars and in Space is
// what a player who accumulates on both places would set up by hand.
//
// So a layer belongs to a star when that star's own saved list has it, or
// the record beside the save does. Those two lists are the player's record
// of where they put it, which is the thing we were trying to infer. A layer
// built this session is in neither yet, so the star it was built on counts
// as well until the first export writes it down.
//
// Donation is still blocked, which was the whole point of v1.19.0. A layer
// left on screen by the star you just came from is in neither of this star's
// lists, so the eviction prefix on ExportPriorityIdList drops it before the
// write, exactly as before. What changes is that a layer the player put here
// on purpose is no longer dropped with it.
//
// Gone with the model: RebuildLayerHomes, which struck a second claim out of
// the save, and StrikeFromMemory behind it. Striking is the wrong move now
// that two claims are legal, and it was destructive: it edited the saved
// list of a star the player was not even standing on. layerHome survives as
// nothing more than "built here this session", written only by the Add
// Accumulation Layer button.
//
// v1.20.1. Fuzzied: "I would never put Biomas gain at the bottom and keep the
// Biomass needed resources above it." He is right, and the log proves the
// memory never asked for it. One sweep apart at the Jupiter arrival:
//
//     ... gas_molecule_collection, elst_plantbased_biomass, ACC#afd2, ...
//     ... gas_molecule_collection, ACC#afd2, ACC#a13f, ACC#ef15,
//         elst_plantbased_biomass, ...
//
// while every sidecar written since 10.09 holds that row above all three
// layers. Two candidates, and the log as it stands cannot tell them apart:
// PlaceReturnedRows moved it while slotting the seven returning rows, or the
// game's own LoadPriority re-imposed the star's saved list in between and we
// left it alone. Leaving it alone is deliberate: only rows that have JUST
// come back are placed, which is what stops a drag being undone. The pass
// prints the list after it runs, so it reads as ours either way.
//
// No behaviour changes here. The restore pass now reports, before it decides
// what to move, any pair of already-on-screen rows that sits the opposite way
// round from the remembered list. The second candidate shows up as a report
// with nothing moved, which the old logging could not show at all. Throttled
// per star on the text of the report, so a standing disagreement is said once
// and the recovery is said once.
//
// v1.22.0. The root cause, and it is the game's own, which is why four
// versions of defending the consumers never finished the job.
//
// The 1.21.1 diagnostic prints the list handed to the game. On Fuzzied's arrival
// the first pass handed over his exact order, biomass at 7 and the layer at
// 19, and the panel came back with the layer at 4 and biomass under it. We
// gave the game the right answer and it returned the wrong one.
//
// The game's LoadPriority:
//
//     for (int i = 0; i < priorityIdList.Count; i++)
//     {
//         string text = priorityIdList[i];
//         if (text.StartsWith("*acc_"))       AddAccumulationLayer(text, i);
//         else if (priorityJobDict.ContainsKey(text))
//             priorityJobDict[text].gameObject.transform.SetSiblingIndex(i);
//     }
//
// `i` is the position in the FULL saved list, applied as a position on the
// panel. A load does not draw the panel in one go: Saturn's came in at 9 rows,
// then 17, then 29. So indices up to 30 get handed to a panel holding 9
// children and Unity clamps every one of them to the last slot. The result is
// not the player's order, it is an artefact of which rows existed at the time.
//
// AddAccumulationLayer(text, i) makes it worse for layers specifically:
//
//     if (!priorityJobDict.ContainsKey(text)) { ... SetSiblingIndex(i); }
//
// A layer is positioned exactly once, on the pass that creates it, at a
// clamped index against a nearly empty panel, and every later pass skips it.
// It is never moved again while the real rows churn around it. That is how a
// layer Fuzzied keeps at row 18 ends up at row 4, fencing off everything below
// it: "Saturn Headquarters under Infra and Ring Harmonics under Research for
// Saturn reads as unavailable". Fenced jobs do not run, nothing changes state,
// so the game never draws the rest and the panel stalls.
//
// Fix: a postfix that re-applies the same list with a running slot that only
// advances for entries actually on the panel. Relative order is then right for
// any subset, and a layer already on screen is moved to where the list has it
// instead of being skipped. It creates nothing and destroys nothing; the
// game's own pass has already made any layer that was missing. Panel rows not
// in the list keep their order below the ones that are, as before.
//
// v1.21.1. 1.21.0 worked: the record was rebuilt 17 -> 31, no lift ran, the
// panel climbed to 30 instead of stalling at 17, and for the first time not one
// of Fuzzied's 31 rows was missing from the save. Two faults were left, and they
// turned out to be one fault.
//
//   ACC#b7e8 came out at row 16 where he has it at 18, with
//   elst_plantbased_biomass pushed from 6 down to 17, under the layer.
//
//   The layer's own SETTINGS came back changed:
//       save   1,1,0,0,1,0,0,0|99,99,99,99   what he had
//       record 1,0,0,0,1,0,0,0|99,99,99,99   what he got
//
// So the record is not just short, it holds an old copy of every layer's
// settings too. The order of the arrival says how that reached the panel:
//
//     read the remembered priority list for 10 place(s)
//     rebuilt the accumulation layers from before the Big Bang  <- reads record
//     ...
//     noted the 31 row(s) star 7 was saved with                 <- snapshot
//     the remembered list for star 7 had 17 row(s) ... rebuilt  <- repair
//
// The repair sits in RefreshMemory, at the end of the first pass.
// RestoreRememberedOrder reads Memory(staying) and calls AddAccumulationLayer
// with the record's entry long before that, so the layer is built from stale
// settings and positioned against a 17 row list. Everything downstream inherits
// both.
//
// Fix: take the snapshot, and with it the repair, at the top of
// RestoreRememberedOrder, one line before it reads Memory(staying). That is the
// earliest consumer of the record after a load, so the layer is rebuilt from
// the save's own entry, settings included, and placed against the real list.
//
// The field is still pristine there: the rebuild runs before the first
// RefreshMemory write, which is the first write to it. SnapshotSavedList keeps
// only the first entry per star, so the calls in RefreshMemory and the export
// prefix stay as backstops and simply become no-ops.
//
// v1.21.0. 1.20.10 fixed the lift and Fuzzied's arrival proved it: the snapshot
// printed early saying 31, no lift ran, and it_saturn_headquarters and
// rt_ring_harmonics stayed below his layer instead of being hauled to rows 4
// and 5. What it uncovered is the bug the lift had been masking. His list came
// out with ACC#b7e8 at row 7 of 20 where he has it at 18 of 31, eleven rows
// never drawn, and almost nothing able to run.
//
// Same supply line as always. Saturn's record holds 17 rows; Saturn holds 31.
// 1.20.6, 1.20.7 and 1.20.10 each taught one consumer to survive that. The data
// is still wrong, and PlaceReturnedRows is simply the next consumer in the
// queue: AttemptInsertJob always appends a re-instantiated row as the LAST
// child, so a row that comes back lands at the bottom, and the mod then puts it
// where the record has it. A record that has never heard of the row leaves it
// at the bottom, under the layer.
//
// Fix, as specified in docs/todo.md: at the moment the snapshot is taken the
// save has just handed over the truth, so if the record is short of it the
// record is stale by definition and gets rebuilt from the save. One change, and
// every consumer above reads a correct list instead of coping with a wrong one.
//
// Only when the record is SHORTER. A save written mid draw is legitimately
// short (Fuzzied has a 20 row one right now) and letting that overwrite a good
// record is the same mistake pointing the other way.
//
// The Big Bang case needs no special test. savedAtLoad is cleared on a reset,
// so the first snapshot afterwards is the post-reset list and this comparison
// never sees the pre-reset record.
//
// seenRows is left alone on purpose. Clearing it would make every row on a
// freshly loaded panel read as "just come back" and hand the whole list to
// PlaceReturnedRows, which is exactly what 1.20.7 exists to prevent.
//
// v1.20.10. 1.20.9 was the right fix in the wrong place, and the arrival log
// said so without any interpretation needed. The snapshot line printed LAST:
//
//     ... started 1 job(s) at saturn+space ...
//     noted the 32 row(s) star 7 was saved with
//
// after all three lifts had already run, and it noted 32 rows for a star the
// save handed over with 31. One explanation covers both. The game's
// ExportPriorityIdList was not the first write to controlSettings
// .priorityIdList after the load. THIS FILE was.
//
// RefreshMemory closes every pass with
//
//     star.controlSettings.priorityIdList = OwnedLayersOnly(merged, staying);
//
// which runs at the end of pass one, before any lift, and again on every pass
// after it. By the time the game exported, the field had been rewritten from
// here six or seven times, so what 1.20.9 captured was this plugin's own merge
// of a stale 17 row record with a half drawn panel.
//
// It also settles 1.20.8, which read the field live at lift time and failed.
// I blamed the game's export for getting there first. The export had not run
// yet at all. RefreshMemory had.
//
// Fix: snapshot immediately before the write in RefreshMemory, which is the
// real first write. The export prefix keeps its call as a backstop for any
// path that reaches an export before RefreshMemory runs. The store only
// accepts the first entry per star, so whichever truly fires first wins and
// the other call is a no-op. FoldIntoSave needs nothing: it is called from the
// export POSTFIX, so the prefix has already been past.
//
// The test, in one line: "noted the N row(s)" must print near the top of an
// arrival, before any lift, and N must be 31 rather than 32.
//
// v1.20.9. 1.20.8 was the right idea reading the wrong variable, and Fuzzied's
// next Saturn arrival said so: "moved 2 new row(s) above your first
// Accumulation Layer", the two rows being it_saturn_headquarters and
// rt_ring_harmonics, which sit at 21 and 13 in Saturn's own saved list of 31.
// SavedListKnows should have said yes to both.
//
// The game explains it. ExportPriorityIdList does:
//
//     StarMap.GetStarByIndex(player.location.stayingStarIndex)
//         .controlSettings.priorityIdList = <the live rows>;
//
// controlSettings.priorityIdList is not what the player saved. It is
// overwritten from the panel on every export, which during a staged build is
// constantly, so it had already shrunk to the 9 or 17 rows then drawn. 1.20.8
// asked a second witness who had already been got at.
//
// LoadPriority is why the build is staged at all: it walks the saved list and
// calls SetSiblingIndex only for ids already in priorityJobDict, silently
// skipping any row the game has not instantiated. Those turn up later, at the
// bottom, with nothing to put them back.
//
// Fix: copy each star's priorityIdList at the last moment it is still the
// player's, and keep it out of the game's reach. That moment is the prefix of
// the FIRST ExportPriorityIdList for that star since load, one instruction
// before the overwrite. A LoadSave postfix would not do: LoadSave calls
// RemoveAllTasks and LoadEEIRPriorityJobs before returning, so an export can
// already have happened. The export prefix cannot be beaten to it, because it
// IS the first write.
//
// Cleared on save load and after a Big Bang, so one world's arrangement never
// leaks into another. Between a Big Bang and the first export afterwards the
// snapshot is empty and the record alone decides, which is the one case the
// record exists for. If that is wrong the cost is a genuinely new job not
// being lifted above a layer, not the player's list being rewritten.
//
// v1.20.8. 1.20.7 was right about what it fixed and wrong about what was
// breaking Fuzzied's list. The adopt worked, "put 9 row(s) back where the
// remembered list has them" never printed, and Saturn came out shredded in
// exactly the same shape as before: plantbased 6 -> 21, Saturn Headquarters
// 21 -> 6, Ring Harmonics 13 -> 7, Path Knowledge 4 22 -> 10, Rocket Launcher
// 20 -> 11, Boss Dojo 19 -> 13.
//
// PlaceReturnedRows was never the culprit. The log had been naming the culprit
// the whole time: "moved 2 new row(s) above your first Accumulation Layer",
// then "moved 10 new row(s) above your first Accumulation Layer". That is
// LiftNewRowsAboveLayers, and 12 of the 14 rows it moved were Fuzzied's own.
//
// The game builds the priority panel in stages after a load. Fuzzied's log walks
// up 9 rows, 17, 29, 30. The mod is auto starting jobs throughout, so Insert()
// marks every row the game had not materialised yet as added this pass. True,
// and no use at all as evidence that the player has no place for it.
//
// The eligibility test then asked MemoryKnows, which calls Memory(staying),
// which returns the SIDECAR RECORD when one exists and only falls back to the
// star's own priorityIdList when one does not. The record beside that save had
// 17 rows. The save had 31. So 14 of Fuzzied's rows read as "nobody has a place
// for me" and were hauled above his layer.
//
// Fix: MemoryKnows asks "does anything already hold a place for this row?" so
// it should mean anything. Check the record AND the star's own saved list, and
// lift only a row that neither has heard of. This can only make FEWER rows
// eligible, never more, which is the safe direction, and a genuinely new job
// is in neither list so the reason the feature exists is untouched.
//
// v1.20.7. Fuzzied, arriving at Saturn with 1.20.6 installed: "plant based
// biomass is under acumulation again while several biomass ones are above
// accumulation. I know I moved them last time we discussed this because if
// they are not below the 1% limit, they steal from the Biomass->Energy
// convertion when Biomass goes to 0%". And: "This looks like the previous old
// stale one".
//
// He is right on both counts. His Saturn order had been identical across three
// saves spanning a week, 31 rows, elst_plantbased_biomass at 6 and ACC#b7e8 at
// 18. After one load and arrival it was ACC#b7e8 at 20 and plantbased at 21,
// with a dozen other rows moved: Saturn Headquarters 21 -> 6, Ring Harmonics
// 13 -> 7, Path Knowledge 4 22 -> 10.
//
// The record beside the save he loaded holds 17 of those 31 rows, in a
// different order, with the layer sitting between sm_separation_one and
// rt_solar_energy_boost. The log then shows precisely that shape coming back:
// "'keep my layers - restore' changed the priority list at star 7 to: ...
// sm_launch, sm_separation_one, ACC#b7e8, sm_energy_burst ...".
//
// The mechanism is PlaceReturnedRows, and the stale record is only half of it.
// A row is allowed to move when the record knows it and seenRows does not have
// it on the same object. Mid session that is exactly right: a research row
// that completes is destroyed and reinstantiated at the BOTTOM of the list,
// and putting it back is what the pass is for.
//
// But seenRows is filled at the end of RefreshMemory from the live panel, so
// on a freshly loaded game it is EMPTY for that star. Every row therefore
// looks like a row that has just come back, and the whole panel is sorted into
// the record's order. "put 9 row(s) back where the remembered list has them",
// 9 rows out of 9. With a current record this is invisible because it sorts
// into the order it already had. With a truncated week old record it shreds
// the list.
//
// Fix: on the FIRST pass at a star since the game loaded, the panel was built
// by the game out of that star's own saved priorityIdList. It is the player's
// arrangement and nothing has returned from anywhere, so adopt it into
// seenRows and move nothing. Second pass onward is unchanged, because by then
// seenRows holds the real arrangement and only a genuinely rebuilt row looks
// new.
//
// This is Fuzzied's principle from earlier the same day: "I want the logic to
// preserv were things where and stop rearragning everything around". It also
// puts the star's own saved list back in charge of ORDER and leaves the record
// doing the job it exists for, surviving a Big Bang when the star has no list.
//
// Not touched: the layer rebuild loop above it. A layer a reset genuinely
// removed is still rebuilt from the record, still gated by LayerRecordedHere.
//
// v1.20.6. 1.20.5 did stop the strays. The 12:14 flight is clean on that
// count: all four were refused and none reached the panel or the save. And
// Fuzzied then said "there are no accumulation layers in space now", and the
// bytes agree:
//
//     departureAuto_Jupiter.sti  12:14:36  Space: 49 rows, ACC#8eef, ACC#9c9f
//     auto_AutoSave.sti          12:23:09  Space: 45 rows, NO layers
//
// His own two were deleted out of Space's saved list on arrival. 49 minus the
// four strays the filter dropped is 45, so what landed in the save is exactly
// OwnedLayersOnly(merged) and merged never held 8eef or 9c9f.
//
// RefreshMemory, which is where it happens:
//
//     live   = the rows on the PANEL
//     merged = Merge(Memory(staying), live)
//     star.controlSettings.priorityIdList = OwnedLayersOnly(merged, staying)
//
// On arrival the panel still belongs to the star just left, so live is
// Jupiter's, and Memory(-1) was the stale record holding the four strays and
// not Fuzzied's two. Neither source contains Space's own layers, so the write
// replaces a list that had them with one that does not. The star's own saved
// list is the only place those two existed and it is never read as a source.
//
// This is not a 1.20.5 regression and it is not new. At 11:54 Space went from
// 8eef/9c9f to the four strays and I read that as the strays pushing them
// out. It was not. The wipe and the strays were always two separate faults,
// and 1.20.2 through 1.20.5 only ever addressed the strays.
//
// The fix: the panel is not the authority on WHICH layers a star has, the
// star's own saved list is. After filtering, any layer still in that list
// which the filtered result lost is put back at the place it held.
//
// Binning a layer is safe and needs no extra bookkeeping, because ForgetLayer
// (the prefix of PriorityAccumulationItem.Delete) already strips the layer
// out of every star's controlSettings.priorityIdList. A binned layer is
// therefore not in the saved list to be preserved. That is exactly the
// distinction that matters: a row gone because the player binned it, versus a
// row absent because the panel belongs to a different star.
//
// AMENDED, same day. Fuzzied, looking at the game rather than the log: "the air
// accumulation 100% does not belong in space". He is right, and it changes
// what the example above means. ACC#8eef and ACC#9c9f sit in BOTH star 5 and
// Space with identical ids, masks and caps, and star 5 is Mars:
//
//     star 5 (mars)  ACC#8eef 1,10,0,0,1,0,0,0   ACC#9c9f 0,0,0,0,5,0,0,0
//     space          ACC#8eef 1,10,0,0,1,0,0,0   ACC#9c9f 0,0,0,0,5,0,0,0
//
// They are Mars's pair, stamped onto Space by the original Vanilla bug that
// LayersStayHome exists to stop. So the 45 row no-layer Space state was
// CORRECT, and KeepOwnLayers was pinning a stamp in place rather than saving
// anything. The rule below is still right; it was being fed a contaminated
// list. Space's saved list has been cleaned by tools/clear_space_layers.py.
//
// layerHome cannot bring them back, which had to be checked before cleaning.
// The single write at AdoptNewLayers is guarded by !LayerKnownAnywhere, and
// Mars has both, so Space can never claim them. Once they are out of Space's
// saved list LayerRecordedHere(id, -1) is false and stays false.
//
// Nothing in the save bytes could have told these two apart from genuine
// Space layers, because at the byte level there is no difference. That is the
// nature of the stamp. It took the player looking at the panel.
//
// v1.20.5. 1.20.4 filtered unowned layers out of the list written to the
// star in RefreshMemory, and the 12:02 test flight failed anyway. The log
// says exactly how:
//
//     the remembered list at star -1 still has ACC#d561 ... not put back
//     ... ff14, f396, 3596 the same             <- 1.20.3 gate, fired
//     ACC#afd2 / a13f / ef15 ... never had it written down
//                                               <- 1.20.2 gate, fired
//     the priority list at star -1 is showing 18 row(s): ... no layers
//     the priority list at star -1 is showing 9 row(s):  ... no layers
//     rebuilt the accumulation layers from before the Big Bang
//     the priority list at star -1 is showing 13 row(s): ... ACC#d561,
//         ACC#ff14, ACC#f396, ACC#3596 ...
//
// Space really was clean for two sweeps. Then the 1.20.3 rebuild gate let the
// four through, and it only does that when the star's own saved list has
// them. So the saved list gained them in between, and auto_AutoSave.sti at
// 12:03:01 confirms it: block 11 back to ACC#d561, ff14, f396, 3596 with
// ACC#8eef and ACC#9c9f pushed out again.
//
// There are three writers of star.controlSettings.priorityIdList in this
// file and 1.20.4 covered one of them:
//
//     RefreshMemory        gated in 1.20.4
//     MergeExportedList    NOT gated. This is the one.
//     RestoreAllStars      not gated, and must stay that way, see below
//
// MergeExportedList is a postfix of the game's own ExportPriorityIdList and
// holds a verbatim copy of the same three lines: the same Merge, the same
// tail loop re-emitting remembered rows that are not live, and so the same
// laundering of a refused layer into the save. Its own comment says what it
// costs: "Writing it into the game's own field is the part that has to be
// earned, because LoadPriority would then rebuild the layers and honour the
// order for free." That is precisely what it was buying for the strays.
//
// RestoreAllStars is deliberately left alone. It writes the remembered list
// into a star whose list is EMPTY, which is the Big Bang survival path, and
// at that moment the star's list is the very thing LayerRecordedHere would
// consult. Gating it would make it refuse everything and layers would stop
// surviving a Big Bang at all. It can therefore still put a stale layer back
// after a reset. That hole is real, it is narrower than this one, and it is
// written down rather than papered over.
//
// v1.20.4. Third pass at the same symptom, and the log clears both of the
// gates already built. On the 11:54 departure, under 1.20.3:
//
//     the remembered list at star -1 still has ACC#d561, but star -1's own
//         saved list does not, so it is not being put back on screen
//     ... ff14, f396, 3596 the same            <- 1.20.3 rebuild gate, fired
//     ACC#afd2 / a13f / ef15 is on the panel but star -1 has never had it
//         written down                          <- 1.20.2 record gate, fired
//     the priority list at star -1 is showing 18 row(s): ... no layers
//
// and six lines later:
//
//     the priority list at star -1 is showing 13 row(s): ... ACC#d561,
//         ACC#ff14, ACC#f396, ACC#3596 ...
//
// They reached the panel with both gates holding. The saves date it:
//
//     departureAuto_Jupiter.sti  11:54:20  Space: ACC#8eef, ACC#9c9f
//     auto_AutoSave.sti          11:54:43  Space: ACC#d561, ff14, f396, 3596
//
// Twenty three seconds, and Fuzzied's real two pushed out of the list.
//
// PriorityJobManager.LoadPriority builds the panel from
// GetStarByIndex(staying).controlSettings.priorityIdList and nothing else, so
// that list gained the four. Its only writer here is RefreshMemory:
//
//     List<string> merged = Merge(Memory(staying), live);
//     remembered[staying] = merged;
//     star.controlSettings.priorityIdList = merged;
//
// and Merge's tail loop re-emits every remembered row that is not live. That
// is correct for a research that has finished and will come back. Applied to
// a layer the gates have just refused, it hands the row back through a third
// door, into the star's saved list, from where the game puts it on the panel
// on the next sweep. Both gates worked. Both were bypassed.
//
// So the two lists are now deliberately different. remembered[staying] still
// takes the full merge, because the memory must never forget a layer: at a
// Big Bang the star lists are all that survives and a drop taken at the
// wrong moment loses a real layer for good. The STAR's list takes the merge
// with unowned layers filtered out, because that list is what the game
// rebuilds from. A stale entry then sits inert in the record rather than
// being laundered into the save.
//
// Still open, and still feeding this: the record beside a save goes stale.
// departureAuto_Jupiter.sti.layers was dated 11:36:33 against a save written
// at 11:54:20.
//
// v1.20.3. Fuzzied, straight after installing 1.20.2: "Again several
// Accumulators in space". 1.20.2 did work. The log refused a record to all
// seven layers standing at star -1:
//
//     ACC#d561 is on the panel but star -1 has never had it written down
//     ACC#ff14, ACC#f396, ACC#3596, ACC#afd2, ACC#a13f, ACC#ef15, the same
//
// But refusing to write a row down does nothing about it being on screen,
// and the two lines immediately above those seven are:
//
//     rebuilt the accumulation layers from before the Big Bang
//     'keep my layers - restore' changed the priority list at star -1 to:
//         ... ACC#d561, ACC#ff14, ACC#f396, ACC#3596 ...
//
// RestoreRememberedOrder put them there. Its layer rebuild was the one place
// left with no ownership test, by an explicit decision in 1.20.0: "No home
// test here any more. This memory IS the record for this star."
//
// That holds only while the memory cannot be wrong, and it can. He loaded
// 2_Save Slot.sti, written 17.09 01:18, whose record beside it is dated
// 10.09 02:53, a week older than the save it belongs to. Its star -1 carried
// seven week-old layers. DropDeadLayers culled the three whose rows no longer
// exist and the rebuild put the other four back onto the Space panel.
//
// Once a row is on the panel the game's own ExportPriorityIdList writes it
// into that star's saved list, which this mod does not gate and should not.
// That is how auto_AutoSave.sti came to hold, at block 11 (Space),
// ACC#d561, ACC#ff14, ACC#f396, ACC#3596, with Fuzzied's real ACC#8eef and
// ACC#9c9f pushed out of it entirely.
//
// So the rebuild now asks the same question 1.20.2 asked of recording: does
// the STAR'S OWN SAVED LIST have this layer, or was it built here this
// session. The save is the truth. The record beside it is an ordering hint,
// and a hint that disagrees with the save loses.
//
// A failing entry is skipped, never dropped from the memory. At a Big Bang
// the star lists are all that survives, and a drop taken at the wrong moment
// would lose a real layer for good. Skipping costs nothing: the row never
// appears, so it never reaches the save, and if the star's list ever gains
// the layer honestly the rebuild starts working again by itself.
//
// Still open, upstream of all of it: a slot save's record can be older than
// the save, so something rewrites the slot files without going through
// SaveGameToPath, where WriteLedger hangs. After this a stale record is
// harmless rather than destructive.
//
// v1.20.2. Fuzzied: "space gave several accumulation layers again". His own
// saves, six minutes apart, name the moment:
//
//     departureAuto_Jupiter.sti 10:23:18  Space: 49 rows, ACC#8eef, ACC#9c9f
//     auto_AutoSave.sti         10:29:17  Space: 56 rows, ACC#8eef, ACC#9c9f,
//                                                         ACC#afd2, ACC#ef15
//
// afd2 and ef15 are Jupiter's, and they arrived DURING the flight with seven
// other Jupiter rows. Not on an arrival, and not from a Big Bang.
//
// Location.Depart sets stayingStarIndex = -1 and returns. Nothing rebuilds
// the panel in one go: LoadPriority runs only from SaveLoadManager, and the
// rows are swapped one at a time by TechnoManager.LoadEEIRPriorityJobs on
// FixedUpdate as each pendingStateUpdate fires. Layers are in neither sweep.
// So for a stretch of frames the panel is Space with Jupiter's fences still
// standing on it, and RefreshMemory, which runs twelve times a minute, writes
// down what it sees.
//
// v1.20.0 is what turned that into a permanent claim. LayerBelongsHere takes
// the mod's own remembered list as proof of ownership. That is right for
// eviction and is the whole reason a layer may live on Mars and in Space at
// once. But RefreshMemory WRITES the remembered list, and it writes
// star.controlSettings.priorityIdList too, so a single sweep taken
// mid-handover forges both receipts and eviction never asks again. Today's
// log carries zero "not this star's layer" lines, which is that silence.
//
// So recording uses a narrower rule than eviction: LayerRecordedHere asks
// only what the PLAYER wrote down, the star's own saved list or a layer built
// here this session, and never the record we are about to write. A layer on
// screen that fails it is skipped for that sweep, not deleted: it is still
// live, still on its own star's list, and the eviction pass takes it off the
// panel in its own time. Nothing about two legal homes changes.
//
// Not fixed here, and not guessed at: Jupiter lost ACC#a13f from its saved
// list across the same flight, and the Big Bang pass then propagated that
// loss into the record through DropDeadLayers. Same theme, different path,
// and the log does not yet say which write dropped it.
//
// Compiled with the .NET Framework 4 csc against the game's own
// Assembly-CSharp.dll and the BepInEx 5 core DLLs (see build_plugin.ps1).

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LargeNumbers;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[BepInPlugin("sti.community.autostart", "STI Community Auto Start", "1.22.1")]
public class AutoStartPlugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;
    internal static AutoStartPlugin shared;

    // The Big Bang upgrade line that pays for this. Three levels; see the
    // header. Added by tools/gen_bigbang.py.
    public const string UPGRADE_ID = "auto_start_ir";
    private const int MAX_LEVEL = 4;

    // How the game marks an Accumulation Layer inside a priority id list.
    // PriorityJobManager.LoadPriority tests for exactly this prefix.
    private const string LAYER_PREFIX = "*acc_";

    // Which star each Accumulation Layer belongs to. The game has no such
    // notion, and that absence is the whole bug. See the header.
    private static readonly Dictionary<string, int> layerHome =
        new Dictionary<string, int>();

    // Arrival fires a chain of UI reloads (locationChangeEvent rebuilds the
    // Techno panel, which rewrites isResearching / isBuilding from the star's
    // control settings). Waiting a beat means we act on the settled state
    // rather than racing it, and the retries cover a slow rebuild.
    private const float FIRST_DELAY_SECONDS = 1.5f;
    private const float RETRY_SECONDS = 0.5f;
    private const int MAX_ATTEMPTS = 20;

    // How often the quiet background sweep looks for things that have unlocked
    // since the last pass. A pass is a few dictionary walks over one or two
    // stars, so this is cheap; it is this slow only because there is no reason
    // for it to be faster.
    private const float SWEEP_SECONDS = 5f;

    // The game's own gate for using the spaceship's research and buildings
    // while standing on a planet (TechnoPanel.LoadStarData).
    private const string SPACE_ANYWHERE_RESEARCH = "space_ir_anywhere";

    // Full output. 0 is off, and the game's own toggle button switches between
    // the two.
    private const int FULL_OUTPUT = 100;

    // What the engine modules are remembered under in place of a star id. No
    // star can collide with it: star ids come from the map data.
    private const string ENGINE_KEY = "engine";

    // These four are what the in-game toggles write to, one per level. They
    // are internal so the settings patch at the bottom of this file can reach
    // them; nothing else should.
    internal static ConfigEntry<bool> cfgEnabled;
    internal static ConfigEntry<bool> cfgUseBudget;
    internal static ConfigEntry<bool> cfgUseOrdering;
    internal static ConfigEntry<bool> cfgRemember;
    internal static ConfigEntry<bool> cfgEngineModules;
    internal static ConfigEntry<bool> cfgEngineLast;
    internal static ConfigEntry<bool> cfgEngineStaysOn;
    internal static ConfigEntry<bool> cfgSpaceLast;
    internal static ConfigEntry<bool> cfgCollectorsFirst;
    internal static ConfigEntry<string> cfgSpaceTail;
    internal static ConfigEntry<string> cfgNeverStart;
    private static ConfigEntry<int> cfgLevel;
    private static ConfigEntry<float> cfgEnergyMargin;
    private static ConfigEntry<bool> cfgNewJobsAboveLayers;
    private static ConfigEntry<bool> cfgLayerHome;

    private bool pending;
    private int attempts;
    private float nextAttemptTime;
    private bool warnedMissingUpgrade;
    private float nextToggleRefresh;
    private float nextSweepTime;

    // Everything this plugin has switched on since the last arrival, departure
    // or Big Bang, as "<star id>/<thing id>". See the header: this is what
    // stops the sweep from undoing a player who switches something back off.
    private readonly HashSet<string> alreadyStarted = new HashSet<string>();

    // prefixedIds of engine modules this plugin switched on, so they can be
    // kept at the bottom without ever touching one the player placed.
    private readonly List<string> engineJobIds = new List<string>();

    // Every row THIS pass put into the priority list, by the key
    // AttemptInsertJob was given - which is what PriorityItem.job.id reads
    // back as. Cleared at the top of each pass, because the only question it
    // ever answers is "did I add this one just now?". See the header.
    private readonly List<string> addedThisPass = new List<string>();

    // How many rows the priority panel had at the end of the last sweep, per
    // star. AnchorLayers only runs when this number has gone DOWN, which is
    // what keeps it from fighting a player who drags a layer on purpose, and
    // from re-anchoring everything over and over through an arrival burst.
    // See the header.
    private readonly Dictionary<int, int> lastRowCount =
        new Dictionary<int, int>();

    // The percentage the player last had each engine module at, keyed by
    // module id, so that the OnStarArrival wipe can be undone. See the header.
    private readonly Dictionary<string, int> engineChoice =
        new Dictionary<string, int>();

    // Set by the StopAllSpeedModules prefix and cleared once the modules are
    // back. While it is set the sweep does not sample the percentages, because
    // what it would read is the game's zeros rather than the player's choice.
    private bool engineWiped;

    // Passes spent waiting for the modules to be rebuilt. A module whose
    // unlock has gone - a Big Bang that took the launch pad with it - would
    // otherwise keep the flag armed for the rest of the session, and an armed
    // flag means nothing is ever recorded again.
    private int engineRestoreTries;
    private const int ENGINE_RESTORE_TRIES = 12;

    // Level 4's memory: star index (-1 is the spaceship) to that star's
    // priority list, in the game's own ExportPriorityIdList form, including
    // the rows that are not back yet. See the header.
    // Each star's priorityIdList as the save handed it over, taken before the
    // game's first export overwrote it with the live panel. Unlike
    // controlSettings.priorityIdList this does not move, which is the entire
    // point. See the v1.20.9 header.
    private readonly Dictionary<int, List<string>> savedAtLoad =
        new Dictionary<int, List<string>>();

    private readonly Dictionary<int, List<string>> remembered =
        new Dictionary<int, List<string>>();

    // What was last written to the companion file, so the thirty-second
    // autosave does not rewrite an identical one all day.
    private string lastLedger;

    // Per star, the rows that were on screen at the end of the last pass:
    // the row's id, and the instance id of the object that carried it.
    // Anything not in here is newly arrived and may be placed; anything in it
    // is where the player left it and is never touched. See the header.
    //
    // The object identity is the half that earns its keep. The game destroys
    // and rebuilds a research or infrastructure row every time its job stops
    // being prioritizable for a moment - TechnoManager.LoadEEIRPriorityJobs
    // runs AttemptRemoveJob/AttemptInsertJob off pendingStateUpdate on every
    // FixedUpdate - and AttemptInsertJob creates the new row at the BOTTOM of
    // the list. Matched on the id alone, that row still reads as "where the
    // player left it", so the memory politely leaves it stranded under every
    // Accumulation Layer. Matched on the object it reads as what it actually
    // is: a row that has just come back, which is the one case
    // PlaceReturnedRows exists for. See the header.
    private readonly Dictionary<int, Dictionary<string, int>> seenRows =
        new Dictionary<int, Dictionary<string, int>>();

    // The last on-screen order written to the log, per star, and how many
    // times. Fuzzied: "Mars list is still borked" - the save and the record both
    // read back correctly, so the only thing left to look at is the panel
    // itself. Logged when it changes rather than every pass, and capped,
    // because the quiet sweep runs every five seconds for the whole session.
    private readonly Dictionary<int, string> loggedOrder =
        new Dictionary<int, string>();
    private readonly Dictionary<int, int> loggedOrderCount =
        new Dictionary<int, int>();
    private const int MAX_ORDER_LOGS = 6;

    // How many times each star has named the pass that moved its list. Same
    // budget and the same reset on arrival as the order log above. Fuzzied has
    // now reported "everything is squished to the bottom Accumulation" four
    // separate times, and every one of those reports was answered by reading
    // the order log, seeing the damage, and having to GUESS which of the six
    // passes did it - because one pass writes one line at the end of Run and
    // by then they have all had a turn. This notes the panel between passes
    // instead, so the next report names the culprit outright. See ShapeOf.
    private readonly Dictionary<int, int> moverLogCount =
        new Dictionary<int, int>();

    private void Awake()
    {
        // 0.1.1: off if the player set it to false in STI Community Patch.cfg
        if (!CommunityToggle.On("auto-start", Logger))
        {
            enabled = false;
            return;
        }
        Log = Logger;
        shared = this;
        cfgEnabled = Config.Bind("Auto start", "Enabled", true,
            "Start research, buildings and the energy/resource collectors by "
            + "themselves when you arrive at a planet, when you set off, and "
            + "after a Big Bang - and keep starting things as they unlock. "
            + "How much it does depends on the 'Automated Arrival' Big "
            + "Bang upgrade; with that upgrade at 0 this does nothing. Same "
            + "switch as the first Settings toggle in game.");
        cfgUseBudget = Config.Bind("Auto start", "RespectEnergyBudget", true,
            "Needs level 2 of the upgrade. Leave a building alone when its "
            + "running cost does not fit your spare energy income. Turn this "
            + "off to start everything regardless. Same switch as the second "
            + "Settings toggle in game.");
        cfgUseOrdering = Config.Bind("Auto start", "SmartOrdering", true,
            "Needs level 3 of the upgrade. Starts the jobs that unlock the "
            + "most first, and keeps your priority list in the order that "
            + "actually feeds down: energy production at the top, then "
            + "resource production, then research and buildings, with the "
            + "engine last. Accumulation Layers stay exactly where you put "
            + "them and nothing but an engine module is ever moved across one "
            + "- see EngineLast. Same switch as the third Settings toggle in "
            + "game.");
        cfgEngineLast = Config.Bind("Auto start", "EngineLast", true,
            "Needs level 3 of the upgrade. Keeps every engine module at the "
            + "very bottom of the priority list, below your Accumulation "
            + "Layers, so the tank only ever drinks what your resources, "
            + "research and buildings have finished with. This is the one "
            + "thing that will move a row you dragged: if you like to drag a "
            + "module above a layer to fill the tank quickly before a trip, "
            + "turn this off first. Same switch as the fifth Settings toggle "
            + "in game.");
        cfgEngineStaysOn = Config.Bind("Auto start", "EngineStaysOn", true,
            "Needs level 1 of the upgrade. Landing somewhere switches every "
            + "engine module off and forgets what you had them set to - that "
            + "is the base game, not this mod. This puts them back at the "
            + "percentage you had chosen. It never switches on a module you "
            + "had left at 0; that is EngineModules, and it is a different "
            + "setting. Switch a module off yourself and it stays off. Same "
            + "switch as the seventh Settings toggle in game.");
        cfgRemember = Config.Bind("Auto start", "RememberPriorityList", true,
            "Needs level 4 of the upgrade. A Big Bang throws your priority "
            + "list away - the Accumulation Layers, the limits you set on "
            + "them, and everything you had queued underneath each one. This "
            + "carries the whole arrangement across, rebuilds the layers "
            + "straight away, and puts each research, building and collector "
            + "back where it was as it becomes available again. Anything you "
            + "drag afterwards becomes the new arrangement, so it never "
            + "fights you. Turning this off stops the arrangement being "
            + "handed back; it does NOT stop it being recorded, and nothing "
            + "is thrown away, so turning it back on picks up where it left "
            + "off. The record is kept beside your save file, one per slot, "
            + "and is built whether or not you own the upgrade. Same switch "
            + "as the fourth Settings toggle in game.");
        cfgLevel = Config.Bind("Auto start", "Level", -1,
            "Force a level instead of reading the Big Bang upgrade. -1 uses "
            + "the upgrade (normal play). 0 off, 1 start everything, 2 also "
            + "respect the energy budget, 3 also order the priority list, 4 "
            + "also keep that list through a Big Bang. Set this to 4 if you "
            + "want the feature without installing the extra Big Bang "
            + "upgrades data patch.");
        cfgCollectorsFirst = Config.Bind("Auto start", "CollectorsFirst",
            true,
            "Needs level 3 of the upgrade. Keeps every collector - energy and "
            + "resource both - above every Accumulation Layer, so the things "
            + "that FEED the planet are never fenced off by a limit meant for "
            + "the things that drain it. This is the one pass that moves a "
            + "collector across a layer, so turn it off if you deliberately "
            + "want one gated. Same switch as the ninth Settings toggle in "
            + "game.");
        cfgSpaceLast = Config.Bind("Auto start", "SpaceLast", true,
            "Needs level 3 of the upgrade. Keeps the spaceship's own research, "
            + "buildings and collectors at the bottom of the priority list, "
            + "just above your engine modules, so they take energy and "
            + "resources only after the planet you are standing on has had "
            + "its share. The spaceship is the one star you never leave, so "
            + "its list grows for ever while a planet's is something you are "
            + "trying to finish. Turn this off to place them yourself. Same "
            + "switch as the sixth Settings toggle in game.");

        cfgSpaceTail = Config.Bind("Auto start", "SpaceTailIds",
            "rt_solar_energy_boost,rt_solar_resource_boost,"
            + "rt_interplanetary_knowledge,it_interplanetary_constructions",
            "The spaceship jobs that go at the BOTTOM of the spaceship block, "
            + "still above the engine modules. These are the ones that run for "
            + "days and swallow everything while they do. Needs SpaceLast on. "
            + "Comma separated, and they are priority list ids: 'rt_' for a "
            + "research, 'it_' for a building, 'enst_' and 'elst_' for the "
            + "collectors. Empty means no bottom bucket.");

        cfgNeverStart = Config.Bind("Auto start", "NeverStart", "rt_blackhole",
            "Research, buildings and collectors this mod will never start on "
            + "its own, however much energy you have. BLACKHOLE is here "
            + "because it costs 1e25 energy and gives you nothing back yet, so "
            + "starting it would park your whole income on it. This only stops "
            + "the mod: start one yourself and it is left alone, running. "
            + "Comma separated priority list ids, same 'rt_' / 'it_' / 'enst_' "
            + "/ 'elst_' prefixes as above. Empty means nothing is blocked.");

        cfgEngineModules = Config.Bind("Auto start", "EngineModules", false,
            "Needs level 1 of the upgrade. Switches your engine modules on by "
            + "itself when you land, so the fuel bar refills without you "
            + "asking. Only ever when you land: the tank you filled before "
            + "you left is what carries you across, so nothing is switched on "
            + "while the ship is moving. Off by default because a filling tank takes every "
            + "joule it can reach until it is full, which can be hours. With "
            + "EngineLast on and an Accumulation Layer above them that is a "
            + "reasonable trade - the layer is a floor they cannot dig below "
            + "- but without a layer they will empty the bank and your "
            + "research, buildings and collectors will get nothing. Turn one "
            + "off yourself and it stays off until you next land. Whatever "
            + "this is set to, a module YOU switched on is still repaired if "
            + "the game forgot to queue it. Same switch as the eighth "
            + "Settings toggle in game.");
        cfgNewJobsAboveLayers = Config.Bind("Auto start", "NewJobsAboveLayers",
            true,
            "A job started for the first time goes into the main list rather "
            + "than below your Accumulation Layers. The game adds every new "
            + "row to the very end of the priority list, which is under every "
            + "layer you have built, and a collector down there is fenced off "
            + "from the energy it needs while it fills the very thing the "
            + "layer is protecting. Only ever applies to a row that has just "
            + "been created and that nothing has a place for yet: a row you "
            + "put below a layer yourself is left exactly where you put it, "
            + "and so is one the priority memory already knows.");
        cfgLayerHome = Config.Bind("Auto start", "LayersStayHome", true,
            "Fixes a Vanilla bug where an Accumulation Layer follows you "
            + "off the planet you built it on. The game never removes a "
            + "layer when the star you are on changes, and it saves the "
            + "whole visible list against the star you are on now, so every "
            + "departure stamps that planet's layers onto Space. They stack "
            + "up there and quietly hold back everything below them. A "
            + "layer now only shows up on a star whose own list has it, "
            + "so a departure cannot stamp one onto Space. A layer you "
            + "set up in Space is yours and stays there.");
        cfgEnergyMargin = Config.Bind("Auto start", "EnergyMargin", 0.2f,
            "At level 2 and up, the share of your energy income to leave "
            + "unspent when deciding which buildings to start. 0.2 means new "
            + "buildings may claim at most 80% of the income you have spare.");
        Harmony harmony = new Harmony("sti.community.autostart");
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        Logger.LogInfo("Auto start active: research, infrastructure and collectors start on arrival, on departure and after a Big Bang; engine modules stay at the percentage you chose (EngineStaysOn=" + cfgEngineStaysOn.Value + ") and are switched on for you only if you ask (EngineModules=" + cfgEngineModules.Value + ")");
    }

    // Called from the Harmony patches: arriving, departing, and either end of
    // a Big Bang. Each of those is the game resetting your setup for you, so
    // each one earns a clean full pass rather than a "once each" sweep.
    internal void Schedule()
    {
        if (!cfgEnabled.Value)
        {
            return;
        }
        alreadyStarted.Clear();
        pending = true;
        attempts = 0;
        nextAttemptTime = Time.unscaledTime + FIRST_DELAY_SECONDS;
    }

    // Departure only: do the pass now as well as arming the delayed one. See
    // the header - the flight may be over before the delayed pass is due, and
    // in flight is the only time the spaceship's research can be started.
    internal static void ScheduleAndRunNow(string what)
    {
        if (shared == null)
        {
            return;
        }
        try
        {
            shared.Schedule();
            if (!shared.pending)
            {
                return; // the feature is switched off
            }
            if (InUpgradeMode())
            {
                return;
            }
            shared.Run(true);
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not hook " + what + ": "
                + e.Message);
        }
    }

    // Shared by the three new hooks, which have nothing else to say.
    internal static void ScheduleSafely(string what)
    {
        if (shared == null)
        {
            return;
        }
        try
        {
            shared.Schedule();
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not hook " + what + ": "
                + e.Message);
        }
    }

    // What the settings toggles ask, to decide how many of themselves to show.
    // Wrapped because it is called from UI code, where a throw would be far
    // more annoying than a missing row.
    internal int OwnedLevel()
    {
        try
        {
            return ResolveLevel();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private void Update()
    {
        // The Big Bang level can change mid-session (a reset, or simply buying
        // the next tier), and the settings rows are built once. Rechecking a
        // few times a second is a dictionary lookup, and SetActive on rows
        // that are already in the right state costs nothing.
        if (Time.unscaledTime >= nextToggleRefresh)
        {
            nextToggleRefresh = Time.unscaledTime + 0.5f;
            AutoStartSettingsPatch.RefreshVisibility();
        }

        // Nothing at all while the Big Bang upgrade basket is open - the level
        // it would read has been picked but not paid for. See the header.
        // Pushing both timers out means no retry is spent waiting either.
        if (InUpgradeMode())
        {
            nextSweepTime = Time.unscaledTime + SWEEP_SECONDS;
            nextAttemptTime = Time.unscaledTime + RETRY_SECONDS;
            return;
        }

        // Same treatment while the offline catch-up is simulating. The burst
        // is twenty attempts half a real second apart, and a long absence
        // takes far more than eleven real seconds to simulate - so without
        // this the whole burst is spent, and given up on, before the game is
        // interactive. That is the "nothing was ready in time, skipping this
        // arrival" line in Fuzzied's log. The work for the catch-up itself is
        // done by the arrival hook, which runs its pass inline.
        if (OfflineManager.shared != null && OfflineManager.shared.simulating)
        {
            nextSweepTime = Time.unscaledTime + SWEEP_SECONDS;
            nextAttemptTime = Time.unscaledTime + RETRY_SECONDS;
            return;
        }

        if (!pending)
        {
            // The quiet sweep: picks up research and buildings that have
            // unlocked since the last pass, and collectors that have just
            // become available. It says nothing unless it actually starts
            // something, because unlike the arrival burst it runs forever.
            if (Time.unscaledTime >= nextSweepTime)
            {
                nextSweepTime = Time.unscaledTime + SWEEP_SECONDS;
                try
                {
                    Run(false);
                }
                catch (Exception e)
                {
                    Log.LogWarning("Auto start sweep failed: " + e.Message);
                }
            }
            return;
        }
        if (Time.unscaledTime < nextAttemptTime)
        {
            return;
        }
        nextAttemptTime = Time.unscaledTime + RETRY_SECONDS;
        nextSweepTime = Time.unscaledTime + SWEEP_SECONDS;
        attempts++;
        bool done;
        try
        {
            done = Run(true);
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start gave up: " + e);
            pending = false;
            return;
        }
        if (done || attempts >= MAX_ATTEMPTS)
        {
            if (!done)
            {
                Log.LogInfo("Auto start: nothing was ready in time, skipping this arrival");
            }
            pending = false;
        }
    }

    // Returns true when the pass has been handled (or there is nothing to
    // handle); false means "not ready yet, try again". Only the star you are
    // actually at decides that. The spaceship is worked on opportunistically,
    // so a spaceship whose data has not loaded never holds up a planet, and
    // the retry loop never burns its twenty attempts waiting for one.
    //
    // `announce` is on for the burst after an arrival, a departure or a Big
    // Bang, and off for the background sweep, which would otherwise write a
    // line to Player.log every five seconds for the rest of the session.
    private bool Run(bool announce)
    {
        if (InUpgradeMode())
        {
            return false;
        }
        addedThisPass.Clear();
        int level = ResolveLevel();
        if (level <= 0)
        {
            return true;
        }
        Player player = Player.shared;
        if (player == null || player.location == null)
        {
            return false;
        }
        PriorityJobManager jobs = PriorityJobManager.shared;
        if (jobs == null || jobs.priorityJobDict == null)
        {
            return false;
        }
        if (UnlockManager.shared == null)
        {
            return false;
        }

        // A level you own only does its thing while its toggle is on, so a
        // player can keep the upgrade and still switch the behaviour off.
        bool useOrdering = level >= 3 && cfgUseOrdering.Value;
        bool useBudget = level >= 2 && cfgUseBudget.Value;
        bool useMemory = level >= 4 && cfgRemember.Value;

        // Straight out of TechnoPanel.LoadStarData. The spaceship's collectors
        // are always in reach; its research and buildings only once
        // 'space_ir_anywhere' is done, unless you are aboard it.
        int staying = player.location.stayingStarIndex;

        // A fresh budget of order lines for each arrival, departure, load and
        // Big Bang. The cap exists so a long session cannot fill the log with
        // the same list over and over, but spending it once and staying spent
        // meant the diagnostic went quiet long before the interesting moment:
        // Fuzzied's "squished again when I loaded from menu" produced no order
        // line at all, because this star had used its six hours earlier. The
        // moments worth recording are exactly the ones announce marks.
        if (announce)
        {
            loggedOrderCount.Remove(staying);
            moverLogCount.Remove(staying);
        }

        List<int> jobStars = new List<int>();
        List<int> productionStars = new List<int>();
        productionStars.Add(-1);
        if (staying < 0)
        {
            jobStars.Add(-1);
        }
        else
        {
            jobStars.Add(staying);
            productionStars.Add(staying);
            if (SpaceWorkAllowed())
            {
                jobStars.Add(-1);
            }
        }

        // Gathered across both stars before anything is started, so the energy
        // budget below is spent once rather than once per star - two separate
        // passes would each think they had the whole headroom to themselves.
        bool primaryReady = false;
        List<Candidate> candidates = new List<Candidate>();
        for (int i = 0; i < jobStars.Count; i++)
        {
            int idx = jobStars[i];
            Star star = StarMap.GetStarByIndex(idx);
            if (star == null || star.controlSettings == null)
            {
                continue;
            }
            List<Candidate> found = Gather(star, idx, useOrdering);
            if (found == null)
            {
                continue; // this star's data is not loaded yet
            }
            if (i == 0)
            {
                primaryReady = true; // the star we are at is the one that counts
            }
            candidates.AddRange(found);
        }

        if (useOrdering)
        {
            candidates.Sort(CompareCandidates);
        }
        if (useBudget)
        {
            candidates = WithinEnergyBudget(candidates, announce);
        }

        int started = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (StartJob(candidates[i], jobs))
            {
                started++;
            }
        }

        for (int i = 0; i < productionStars.Count; i++)
        {
            Star star = StarMap.GetStarByIndex(productionStars[i]);
            if (star == null || star.controlSettings == null)
            {
                continue;
            }
            started += StartProduction(star, productionStars[i], jobs);
        }

        // Before StartSpeedModules, so a module that is coming back is
        // already on when the walk reaches it and is not treated as one the
        // player left at 0. See the header.
        if (cfgEngineStaysOn.Value)
        {
            started += RestoreEngineChoice(jobs);
        }
        else
        {
            // Otherwise the flag would stay armed for good and recording would
            // never start again, so switching this back on months later would
            // hand back a percentage from another era.
            engineWiped = false;
        }
        int engineStarted = StartSpeedModules(jobs);
        started += engineStarted;

        // Level 4 first, then level 3: the memory decides which Accumulation
        // Layer a job belongs under, and the ordering pass then tidies inside
        // each stretch between two layers without ever crossing one. Doing it
        // the other way round would mean the memory kept undoing the tidying.
        // See the header.
        // Read once here and re-read after every pass that can move a row,
        // so a line in the log names the pass rather than the aftermath.
        // See NoteMover.
        string shape = ShapeOf(jobs);

        bool moved = false;
        if (useMemory)
        {
            moved = RestoreRememberedOrder(jobs, staying);
            shape = NoteMover(staying, shape, "keep my layers - restore",
                jobs);
            // Straight after, because a layer that has climbed makes every
            // pass below this one draw the fences in the wrong places.
            if (AnchorLayers(jobs, staying)) { moved = true; }
            shape = NoteMover(staying, shape, "keep my layers - anchor",
                jobs);
        }

        // A row we have just created has never been anywhere, so putting it
        // on the right side of a fence is not moving anything of the player's.
        // After the memory restore, because the memory's placement wins where
        // it has one. See the header.
        bool lifted = LiftNewRowsAboveLayers(jobs, staying);
        shape = NoteMover(staying, shape, "new rows above layers", jobs);

        // Anything added this pass may have landed below a module we started
        // in an earlier one, so put them back underneath. See the header.
        if (started > 0 || moved || lifted)
        {
            if (useOrdering)
            {
                ReorderPriorityList(jobs);
                shape = NoteMover(staying, shape, "unlocks first", jobs);
            }
        }
        // Only when something actually happened to the list.
        //
        // Fuzzied: "So everything is still janked back down if I try to reorder
        // it, and the collectors are janked back up even if it is just within
        // an Accumulator with nothing else in it".
        //
        // These two enforce a SHAPE, and until v1.15.0 they enforced it on
        // every quiet sweep, which is twelve times a minute for as long as
        // the game is open. That is not a tidy-up, it is a guard standing
        // over the list, and it makes an arrangement by hand impossible: the
        // player drags a row and five seconds later the rule puts it back.
        // No amount of narrowing what the rules touch fixes that, because
        // where a rule and the player disagree one of them has to lose, and
        // it should not be the player.
        //
        // So they run on the arrival burst, and on a sweep that actually
        // started something, added something or restored the list from the
        // memory. On a sweep where nothing happened - which is nearly all of
        // them, and every single one while somebody is dragging rows about -
        // they do not touch the panel at all.
        //
        // The engine sink below is deliberately NOT gated this way. Fuzzied
        // asked for that one outright: "it should always shuffle those 4
        // Engine Refuel's to the end". EngineLast turns it off.
        bool reshape = announce || started > 0 || moved || lifted;

        // Every row the memory already has a place for, which is every row
        // that has been on screen for more than one pass. Both passes below
        // leave these alone: the memory IS the player's arrangement, and a
        // load restores it only for a rule to flatten it again otherwise.
        // Empty when there is no memory to consult, which puts both passes
        // back exactly as they were. See the header.
        HashSet<string> placed = useMemory
            ? RememberedIds(staying)
            : new HashSet<string>();

        // Before both sinks, so a collector rescued from under a layer is not
        // then sunk back down by the next pass. See the header.
        if (useOrdering && cfgCollectorsFirst.Value && reshape)
        {
            RaiseCollectorJobs(jobs, placed);
            shape = NoteMover(staying, shape, "collectors first", jobs);
        }
        // Before the engine sink, so the engine still ends up underneath.
        // See the header.
        if (useOrdering && cfgSpaceLast.Value && reshape)
        {
            SinkSpaceJobs(jobs, placed);
            shape = NoteMover(staying, shape, "space last", jobs);
        }
        if (useOrdering && cfgEngineLast.Value)
        {
            SinkEngineJobs(jobs, true);
            shape = NoteMover(staying, shape, "engine last", jobs);
        }
        else if (started > 0)
        {
            SinkEngineJobs(jobs, false);
            shape = NoteMover(staying, shape, "engine last (on a start)",
                jobs);
        }

        // Last, so the memory records the arrangement the player is actually
        // looking at - including anything they dragged since the last pass.
        if (useMemory)
        {
            RefreshMemory(jobs, staying);
        }

        // Same idea for the engine dials: whatever they read now is the
        // player's, unless the game has just wiped them and we have not put
        // them back yet. See the header.
        if (!engineWiped)
        {
            RememberEngineChoice(false);
        }

        if (started > 0 || announce)
        {
            string where = StarNames(jobStars, productionStars);
            if (engineStarted > 0)
            {
                where += "+engine";
            }
            Log.LogInfo("Auto start: started " + started + " job(s) at "
                + where + " (level " + level + ", energy budget " + useBudget
                + ", ordering " + useOrdering + ")");
        }
        return primaryReady;
    }

    // Whether the spaceship's own research and buildings can be worked on from
    // where the player is standing.
    private static bool SpaceWorkAllowed()
    {
        TechnoManager techno = TechnoManager.shared;
        if (techno == null)
        {
            return false;
        }
        try
        {
            return techno.SatisfiesResearchReq(SPACE_ANYWHERE_RESEARCH);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Just for the log line: every star this pass looked at, once each.
    private static string StarNames(List<int> a, List<int> b)
    {
        List<string> names = new List<string>();
        for (int pass = 0; pass < 2; pass++)
        {
            List<int> list = (pass == 0) ? a : b;
            for (int i = 0; i < list.Count; i++)
            {
                Star star = StarMap.GetStarByIndex(list[i]);
                if (star == null || star.id == null || names.Contains(star.id))
                {
                    continue;
                }
                names.Add(star.id);
            }
        }
        return string.Join("+", names.ToArray());
    }

    // ------------------------------------------------------------------
    // The energy and resource collectors
    // ------------------------------------------------------------------
    //
    // These are not priority jobs and have no build step - a collector is a
    // dial from 0 to 100 where 0 means off, and a brand new planet hands you
    // every one of them at 0. So there is nothing to queue here: set the
    // model, set the star's control settings, and tell the row if one exists.
    //
    // All three steps matter. TechnoPanel.LoadStarEnergyData reads the control
    // settings back over the model every time it rebuilds, so setting only the
    // model would be silently undone the next time you opened the panel - the
    // same trap StartJob works around for research. And a row that already
    // exists keeps its own copy of the percentage, which only SwitchTo
    // updates, so a row built before we ran would carry on showing "Off".
    private int StartProduction(Star star, int starIndex, PriorityJobManager jobs)
    {
        int started = 0;
        try
        {
            EnergySourceSet energySet = StarMap.GetEnergySourceSet(starIndex);
            Dictionary<string, EnergySourceTemplate> energyTemplates =
                EnergyTemplatesOf(star);
            if (energySet != null && energyTemplates != null)
            {
                foreach (EnergySourceTemplate template in energyTemplates.Values)
                {
                    if (!UnlockManager.shared.SatisfiesUnlockReq(template.unlockReq)
                        || NeverStart(template.prefixedId))
                    {
                        continue;
                    }
                    EnergySource source = energySet.GetEnergySource(template.id);
                    if (TurnOnSource(star.id, source, template.id,
                            star.controlSettings.energySourceInputDict, jobs))
                    {
                        started++;
                    }
                }
            }

            ElementSourceSet elementSet = StarMap.GetElementSourceSet(starIndex);
            Dictionary<string, ElementSourceTemplate> elementTemplates =
                ElementTemplatesOf(star);
            if (elementSet != null && elementTemplates != null)
            {
                foreach (ElementSourceTemplate template in elementTemplates.Values)
                {
                    if (!UnlockManager.shared.SatisfiesUnlockReq(template.unlockReq)
                        || NeverStart(template.prefixedId))
                    {
                        continue;
                    }
                    ElementSource source = elementSet.GetElementSource(template.id);
                    if (TurnOnSource(star.id, source, template.id,
                            star.controlSettings.elementSourceInputDict, jobs))
                    {
                        started++;
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not switch on the collectors at "
                + star.id + ": " + e.Message);
        }
        return started;
    }

    private bool TurnOnSource(string owner, ProductionSource source, string id,
        Dictionary<string, int> inputs, PriorityJobManager jobs)
    {
        if (source == null || inputs == null)
        {
            return false;
        }
        if (source.outputPercentage > 0.0)
        {
            // Already running, at whatever the player chose - not ours to
            // start. But "on" is only a dial, and the game may never have
            // queued it. Repair that, and still report no start.
            //
            // Claim it anyway. This branch used to return without claiming,
            // which left every collector that was already on when we first
            // looked unknown to us, and that is most of them: a loaded save
            // arrives with its collectors running. Turn one off through Techno
            // then Production and the next sweep, five seconds later, saw a
            // dial at 0 with no claim against it, read that as one it had
            // never started, and switched it straight back on. Fuzzied hit it on
            // 21.09.2026 trying to pause Air mid flight, said he "had to click
            // twice", then proved it on Biomass with the restart in Player.log.
            // Being on is the same evidence as having started it: either way,
            // the next time it reads 0 somebody moved that dial and it was not
            // this mod. Nothing here ever switches a job off, so the worst this
            // costs is one collector left off that the player wanted on, and
            // they are one click from that. The other way round, they cannot
            // turn anything off at all.
            Claim(owner, id);
            EnsureJob(source, jobs);
            return false;
        }
        PriorityJob job = JobFor(source);
        if (job == null)
        {
            return false; // a fourth kind of source we have not been told about
        }
        if (!Claim(owner, id))
        {
            return false;
        }
        source.outputPercentage = 1.0;
        inputs[id] = FULL_OUTPUT;
        FlexProgressItem view = source.view;
        if (view != null)
        {
            // Model and button colours in one, and it is what the player's own
            // click on that row calls.
            view.SwitchTo(FULL_OUTPUT);
        }
        // Without this it reads as on and produces nothing - see the header.
        Insert(jobs, source.prefixedId, job);
        return true;
    }

    // PriorityJob has a constructor per kind and no common one, and JobType is
    // set from whichever it was, so the concrete type has to be recovered.
    // A source produces because PriorityJobManager holds a job for it, not
    // because its dial reads 100. The game's own loaders only ever queue rows
    // that are on screen, so anything switched on behind a shut panel reads as
    // ON and makes nothing. This puts that right wherever we find it, and is
    // deliberately not gated on any setting: it starts nothing that was not
    // already started, it only makes an existing choice work.
    private void EnsureJob(ProductionSource source, PriorityJobManager jobs)
    {
        try
        {
            if (source == null || jobs == null || jobs.priorityJobDict == null)
            {
                return;
            }
            string id = source.prefixedId;
            if (jobs.priorityJobDict.ContainsKey(id)) { return; }
            PriorityJob job = JobFor(source);
            if (job == null) { return; }
            Insert(jobs, id, job);
        }
        catch (Exception)
        {
        }
    }

    // ------------------------------------------------------------------
    // Level 4: the priority list survives a Big Bang
    // ------------------------------------------------------------------

    // Called from the Big Bang prefix. Takes a copy of every star's priority
    // list while they still exist, the current star's straight from the rows
    // on screen. Never gated on the level: capturing costs a dictionary and
    // the player may well be about to buy level 4 with the dark matter this
    // very reset just paid out.
    internal static void RememberPriorityLists()
    {
        if (shared == null)
        {
            return;
        }
        try
        {
            shared.CaptureAllStars();
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not remember the priority list: "
                + e.Message);
        }
    }

    // Called from the Big Bang postfix and again after ConfirmUpgrades, since
    // level 4 may be what was just bought. Only ever fills a list that is
    // empty, so the second call cannot undo the first.
    internal static void RestorePriorityLists()
    {
        if (shared == null)
        {
            return;
        }
        try
        {
            // The reset destroyed every row, so nothing on screen afterwards
            // is "where the player left it" - the whole rebuilt list has to be
            // eligible for placing. See the header.
            shared.seenRows.Clear();
            // The reset rewrote every star's list, so the old snapshot is a
            // different world's arrangement. Retaken on the next export.
            shared.savedAtLoad.Clear();
            shared.RestoreAllStars();
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not restore the priority list: "
                + e.Message);
        }
    }

    // Loading a save - or starting a new game - hands us a different world,
    // and SaveLoadManager.LoadSave is the one funnel both go through. Without
    // this, save slot A's arrangement would be imposed on save slot B, layers
    // and all. Memory(...) reseeds itself from whatever the new save holds.
    internal static void ForgetPriorityLists()
    {
        if (shared == null)
        {
            return;
        }
        shared.remembered.Clear();
        shared.seenRows.Clear();
        shared.savedAtLoad.Clear();
        shared.lastLedger = null;
        // A different world has different layers. See the header.
        layerHome.Clear();
    }

    // Called from the ExportPriorityIdList postfix. The game has just written
    // the live rows over the star's list, which would drop every row that has
    // not come back yet; this puts them back in. That hook is what makes the
    // memory survive a save, and therefore a restart part way through the
    // rebuild.
    internal static void MergeExportedList()
    {
        if (shared == null || cfgRemember == null)
        {
            return;
        }
        try
        {
            Player player = Player.shared;
            if (player == null || player.location == null)
            {
                return;
            }
            int idx = player.location.stayingStarIndex;
            Star star = StarMap.GetStarByIndex(idx);
            if (star == null || star.controlSettings == null)
            {
                return;
            }
            List<string> live = star.controlSettings.priorityIdList;
            if (live == null || live.Count == 0)
            {
                // Nothing on screen to learn from - between the reset and the
                // first pass that rebuilds the layers, for instance. Merging
                // an empty list would read as "every layer was deleted".
                return;
            }
            List<string> merged = Merge(shared.Memory(idx), live);
            shared.remembered[idx] = merged;

            // The record itself is kept whatever level you own and whatever
            // the toggle says - Fuzzied: "without needing to wipe it at a new
            // big bang". Writing it into the game's own field is the part
            // that has to be earned, because LoadPriority would then rebuild
            // the layers and honour the order for free. See the header.
            if (shared.OwnedLevel() >= 4 && cfgRemember.Value)
            {
                // Not "merged", for the same reason as in RefreshMemory:
                // Merge re-emits remembered rows that are not live, layers
                // included, and this list is what LoadPriority rebuilds the
                // panel from. This is the copy 1.20.4 missed. See the header.
                star.controlSettings.priorityIdList =
                    OwnedLayersOnly(merged, idx);
            }
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not fold the remembered priority "
                + "list into the save: " + e.Message);
        }
    }

    private void CaptureAllStars()
    {
        PriorityJobManager jobs = PriorityJobManager.shared;
        if (jobs != null)
        {
            // Only a save writes controlSettings normally, so the star we are
            // standing on may be hours out of date. Asking the game to export
            // costs nothing and runs the merge hook above on the way.
            try
            {
                jobs.ExportPriorityIdList();
            }
            catch (Exception)
            {
            }
        }
        for (int idx = -1; idx < StarMap.starCount; idx++)
        {
            Star star = StarMap.GetStarByIndex(idx);
            if (star == null || star.controlSettings == null)
            {
                continue;
            }
            List<string> list = star.controlSettings.priorityIdList;
            if (list == null || list.Count == 0)
            {
                continue;
            }
            // Layers are the one thing that cannot accumulate: rebuilding
            // one by hand gives it a fresh guid, so a player without level 4
            // would gather a generation per reset. Anything remembered that
            // is not live at the moment of the reset goes. See the header.
            remembered[idx] = Merge(DropDeadLayers(Memory(idx), list), list);
        }
    }

    // Every remembered layer that is no longer among the live entries. Only
    // used at the Big Bang, where the live list is still the real one.
    private static List<string> DropDeadLayers(List<string> memory,
        List<string> live)
    {
        HashSet<string> liveIds = new HashSet<string>();
        for (int i = 0; i < live.Count; i++)
        {
            liveIds.Add(EntryId(live[i]));
        }
        List<string> kept = new List<string>();
        for (int i = 0; i < memory.Count; i++)
        {
            if (IsLayer(memory[i]) && !liveIds.Contains(EntryId(memory[i])))
            {
                continue;
            }
            kept.Add(memory[i]);
        }
        return kept;
    }

    private void RestoreAllStars()
    {
        if (OwnedLevel() < 4 || !cfgRemember.Value)
        {
            return;
        }
        int restored = 0;
        foreach (KeyValuePair<int, List<string>> pair in remembered)
        {
            Star star = StarMap.GetStarByIndex(pair.Key);
            if (star == null || star.controlSettings == null)
            {
                continue;
            }
            List<string> current = star.controlSettings.priorityIdList;
            if (current != null && current.Count > 0)
            {
                continue; // already has one; never overwrite a live list
            }
            star.controlSettings.priorityIdList = new List<string>(pair.Value);
            restored++;
        }
        if (restored > 0)
        {
            Log.LogInfo("Auto start: kept the priority list for " + restored
                + " star(s) through the Big Bang");
        }
    }

    // Puts every Accumulation Layer back under the job the memory has it
    // under. Only the layers move; not one job row changes place relative to
    // another.
    //
    // Fuzzied: "We saw that Mars specific research infra was affected too, is
    // this accounted for?" It was not, and it is a different fault from the
    // collectors one. Two sweeps five seconds apart in his log, with the
    // layers taken out, read the same apart from the two rows that finished
    // in between: his Mars research and infrastructure never moved at all.
    // The four layers did. They slid up until they were touching, which left
    // everything that had been below the first one stranded below the last.
    //
    // A layer is placed by its index into the saved priority list. Finish a
    // research that sits above one and its row is destroyed, every row below
    // shifts up a place, and the layer follows - so a layer climbs one rung
    // per row completed above it, and once several have climbed they collect
    // at the top of the list in a block. That is what "everything is squished
    // to the last Accumulation" looks like from underneath.
    //
    // The mod could not see it happening. PlaceReturnedRows recognises a row
    // by the object carrying it, and these are the same objects they always
    // were, moved rather than rebuilt - which is exactly the signature of the
    // player dragging something, the one thing it must never undo.
    //
    // So the trigger is the row COUNT changing. A row finishing changes it; a
    // player dragging a layer does not. That way this only ever runs on the
    // sweep where the damage is actually done, and a layer moved on purpose
    // is recorded by RefreshMemory at the end of that same pass and becomes
    // the new truth.
    private bool AnchorLayers(PriorityJobManager jobs, int staying)
    {
        try
        {
            if (jobs == null || jobs.priorityJobDict == null) { return false; }
            Transform content = PriorityContent(jobs);
            if (content == null) { return false; }
            int count = content.childCount;

            int had;
            bool knew = lastRowCount.TryGetValue(staying, out had);
            lastRowCount[staying] = count;
            // Only when rows have GONE. A row finishing is what makes a layer
            // climb; a row being added cannot move a layer at all, because the
            // game appends it below every layer there is. Firing on an add
            // meant re-anchoring every layer against a memory that is still
            // catching up, on every pass of an arrival burst. See the header.
            if (!knew || count >= had) { return false; }

            List<string> memory = Memory(staying);
            if (memory.Count == 0) { return false; }

            Transform[] before = new Transform[count];
            List<Transform> live = new List<Transform>();
            Dictionary<string, Transform> jobRow =
                new Dictionary<string, Transform>();
            Dictionary<string, Transform> layerRow =
                new Dictionary<string, Transform>();
            for (int i = 0; i < count; i++)
            {
                Transform child = content.GetChild(i);
                before[i] = child;
                PriorityItem item = child.GetComponent<PriorityItem>();
                if (item == null || item.job == null || item.job.id == null)
                {
                    return false; // something unfamiliar; leave it alone
                }
                if (item.job.type == JobType.accumulation)
                {
                    layerRow[item.job.id] = child;
                }
                else
                {
                    live.Add(child);
                    jobRow[item.job.id] = child;
                }
            }
            if (layerRow.Count == 0) { return false; }

            // Each layer belongs after the nearest job ABOVE it in the memory
            // that is still on screen. Nearest still on screen matters: the
            // row that finished is gone, so the anchor has to fall through to
            // the one above that rather than give up.
            List<string> atTop = new List<string>();
            Dictionary<string, List<string>> under =
                new Dictionary<string, List<string>>();
            string anchor = null;
            for (int i = 0; i < memory.Count; i++)
            {
                string id = EntryId(memory[i]);
                if (!IsLayer(memory[i]))
                {
                    if (jobRow.ContainsKey(id)) { anchor = id; }
                    continue;
                }
                if (!layerRow.ContainsKey(id)) { continue; }
                if (anchor == null) { atTop.Add(id); continue; }
                List<string> list;
                if (!under.TryGetValue(anchor, out list))
                {
                    list = new List<string>();
                    under[anchor] = list;
                }
                list.Add(id);
            }

            // Rebuild the panel: the job rows in the order they are already
            // in, each followed by whatever layers belong under it.
            List<Transform> want = new List<Transform>();
            HashSet<Transform> placed = new HashSet<Transform>();
            for (int i = 0; i < atTop.Count; i++)
            {
                want.Add(layerRow[atTop[i]]);
                placed.Add(layerRow[atTop[i]]);
            }
            for (int i = 0; i < live.Count; i++)
            {
                Transform row = live[i];
                want.Add(row);
                placed.Add(row);
                PriorityItem item = row.GetComponent<PriorityItem>();
                List<string> list;
                if (!under.TryGetValue(item.job.id, out list)) { continue; }
                for (int k = 0; k < list.Count; k++)
                {
                    want.Add(layerRow[list[k]]);
                    placed.Add(layerRow[list[k]]);
                }
            }
            // A layer the memory has never heard of keeps the place the panel
            // gave it. Without this the count check below would fail and the
            // whole pass would quietly do nothing.
            for (int i = 0; i < count; i++)
            {
                if (!placed.Contains(before[i])) { want.Add(before[i]); }
            }
            if (want.Count != count) { return false; }

            bool changed = false;
            for (int i = 0; i < count; i++)
            {
                if (before[i] != want[i]) { changed = true; break; }
            }
            if (!changed) { return false; }
            for (int i = 0; i < count; i++)
            {
                want[i].SetSiblingIndex(i);
            }
            Log.LogInfo("Auto start: a finished row had shifted your "
                + "Accumulation Layers; put them back under the jobs the "
                + "remembered list has them under");
            return true;
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not anchor the accumulation "
                + "layers: " + e.Message);
            return false;
        }
    }

    // Rebuilds any Accumulation Layer the reset took away, then puts the rows
    // the memory knows about back into their remembered order. Returns true if
    // anything actually moved, so the caller knows the list is worth tidying.
    private bool RestoreRememberedOrder(PriorityJobManager jobs, int staying)
    {
        try
        {
            if (jobs == null || jobs.priorityJobDict == null)
            {
                return false;
            }
            // Earliest thing to read the record after a load, and it goes on
            // to hand the record's layer entries straight to
            // AddAccumulationLayer. A stale record here means a layer rebuilt
            // with week old settings, so the repair has to happen before the
            // read below, not after it. See the v1.21.1 header.
            SnapshotSavedList(staying);

            List<string> memory = Memory(staying);
            if (memory.Count == 0)
            {
                return false;
            }

            bool changed = false;
            // Without this the test below reads "already on screen" for a
            // layer whose row is gone, and the rebuild never happens. That
            // is the Mercury fault; see the header.
            ClearDeadLayers(jobs);
            for (int i = 0; i < memory.Count; i++)
            {
                string entry = memory[i];
                if (!IsLayer(entry))
                {
                    continue;
                }
                if (jobs.priorityJobDict.ContainsKey(EntryId(entry)))
                {
                    continue;
                }
                // 1.20.0 had no test here, on the grounds that this
                // memory IS the record for this star. It is not. The record
                // beside a slot save can be a week older than the save, and
                // then this loop cheerfully rebuilds a week-old arrangement
                // onto the panel, from where the game's own export writes it
                // into the save. Same question as the recording gate: the
                // star's own saved list, or built here this session. See the
                // header.
                if (!LayerRecordedHere(EntryId(entry), staying))
                {
                    NoteNotRebuilt(EntryId(entry), staying);
                    continue;
                }
                try
                {
                    // The game's own parser, so the limits and the four task
                    // counts come back exactly as they were written.
                    jobs.AddAccumulationLayer(entry, i);
                    changed = true;
                }
                catch (Exception e)
                {
                    Log.LogWarning("Auto start could not rebuild an "
                        + "accumulation layer: " + e.Message);
                }
            }
            if (changed)
            {
                Log.LogInfo("Auto start: rebuilt the accumulation layers from "
                    + "before the Big Bang");
            }

            Transform content = PriorityContent(jobs);
            if (content == null)
            {
                return changed;
            }
            Dictionary<string, int> established;
            if (!seenRows.TryGetValue(staying, out established))
            {
                // First pass at this star since the game loaded. The panel was
                // built by the game out of this star's own saved list, so it
                // IS the player's arrangement and nothing has returned from
                // anywhere. Treating it as returning rows is what sorted
                // Fuzzied's Saturn into a week old record. Adopt it, move
                // nothing, and let the second pass work as it always has.
                // See the header.
                AdoptPanelOrder(content, staying);
                return changed;
            }
            return PlaceReturnedRows(content, memory, established, staying)
            || changed;
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not restore the priority order: "
                + e.Message);
            return false;
        }
    }

    // Slots the rows that have JUST APPEARED into the places the memory holds
    // for them, and touches nothing else. Every row that was already on screen
    // at the end of the last pass keeps its exact position relative to the
    // other such rows, so a drag can never be undone - see the header.
    //
    // A new row goes immediately after the last established row that the
    // memory lists above it. New rows are placed in memory order and count as
    // established for the ones after them, so several coming back at once land
    // in the right order among themselves.
    // The panel as it stands becomes "where the player left it", without a
    // single row moving. Same shape as the block at the end of RefreshMemory
    // that normally fills seenRows, but run before anything is allowed to
    // reorder rather than after.
    private void AdoptPanelOrder(Transform content, int staying)
    {
        Dictionary<string, int> seen = new Dictionary<string, int>();
        int count = content.childCount;
        for (int i = 0; i < count; i++)
        {
            Transform child = content.GetChild(i);
            PriorityItem item = child.GetComponent<PriorityItem>();
            if (item == null || item.job == null || item.job.id == null)
            {
                continue;
            }
            seen[item.job.id] = child.GetInstanceID();
        }
        if (seen.Count == 0)
        {
            // The panel is not built yet. Storing an empty set would mark the
            // star as established and let the real rows through unrecorded,
            // so leave it and pick it up on the next pass.
            return;
        }
        seenRows[staying] = seen;
        NotePanelAdopted(staying, seen.Count);
    }

    // Said once per star per load, like the other notes.
    private static readonly HashSet<int> adoptedSaid = new HashSet<int>();

    private static void NotePanelAdopted(int staying, int rows)
    {
        if (!adoptedSaid.Add(staying))
        {
            return;
        }
        Log.LogInfo("Auto start: star " + staying + " came back from the save "
            + "with " + rows + " row(s) already in your order, so they are "
            + "being left exactly where they are");
    }

    private static bool PlaceReturnedRows(Transform content,
        List<string> memory, Dictionary<string, int> established,
        int staying)
    {
        Dictionary<string, int> where = new Dictionary<string, int>();
        for (int i = 0; i < memory.Count; i++)
        {
            string id = EntryId(memory[i]);
            if (!where.ContainsKey(id))
            {
                where[id] = i;
            }
        }

        int count = content.childCount;
        List<Transform> order = new List<Transform>();
        List<Transform> fresh = new List<Transform>();
        List<int> freshRank = new List<int>();
        Dictionary<Transform, int> rankOf = new Dictionary<Transform, int>();
        Transform[] before = new Transform[count];
        for (int i = 0; i < count; i++)
        {
            Transform child = content.GetChild(i);
            before[i] = child;
            PriorityItem item = child.GetComponent<PriorityItem>();
            string id = null;
            if (item != null && item.job != null)
            {
                id = item.job.id;
            }
            int pos;
            int seenObject;
            // The same id carried by the same object is the row the player
            // left there. The same id on a NEW object is a row the game has
            // just rebuilt at the bottom of the list, and that one is ours to
            // put back. See the note on seenRows.
            if (id == null
                || (established.TryGetValue(id, out seenObject)
                    && seenObject == child.GetInstanceID())
                || !where.TryGetValue(id, out pos))
            {
                // Established, or brand new and unheard of. Either way it stays
                // exactly where it is relative to the others like it.
                order.Add(child);
                continue;
            }
            fresh.Add(child);
            freshRank.Add(pos);
            rankOf[child] = pos;
        }
        ReportOutOfOrder(staying, before, fresh, where, memory);

        if (fresh.Count == 0)
        {
            return false;
        }

        // Stable insertion sort by remembered position.
        for (int i = 1; i < fresh.Count; i++)
        {
            Transform row = fresh[i];
            int r = freshRank[i];
            int j = i - 1;
            while (j >= 0 && freshRank[j] > r)
            {
                fresh[j + 1] = fresh[j];
                freshRank[j + 1] = freshRank[j];
                j--;
            }
            fresh[j + 1] = row;
            freshRank[j + 1] = r;
        }

        for (int f = 0; f < fresh.Count; f++)
        {
            Transform row = fresh[f];
            int mine = freshRank[f];
            int insertAt = 0;
            for (int k = 0; k < order.Count; k++)
            {
                int theirs;
                if (!rankOf.TryGetValue(order[k], out theirs))
                {
                    PriorityItem item = order[k].GetComponent<PriorityItem>();
                    if (item == null || item.job == null
                        || item.job.id == null
                        || !where.TryGetValue(item.job.id, out theirs))
                    {
                        continue; // no opinion about this one
                    }
                    rankOf[order[k]] = theirs;
                }
                if (theirs < mine)
                {
                    insertAt = k + 1;
                }
            }
            order.Insert(insertAt, row);
        }

        bool moved = false;
        for (int i = 0; i < count; i++)
        {
            if (before[i] != order[i])
            {
                moved = true;
                break;
            }
        }
        if (!moved)
        {
            return false;
        }
        for (int i = 0; i < count; i++)
        {
            order[i].SetSiblingIndex(i);
        }
        Log.LogInfo("Auto start: put " + fresh.Count + " row(s) back where the "
            + "remembered list has them");
        return true;
    }

    // v1.20.1 diagnostic, no behaviour. The rows already on screen are the
    // ones this pass will not touch, so if one of THOSE is the wrong way
    // round against the memory, something outside this pass put it there.
    // See the header.
    private static readonly Dictionary<int, string> lastOutOfOrder =
        new Dictionary<int, string>();

    private static void ReportOutOfOrder(int staying, Transform[] before,
        List<Transform> fresh, Dictionary<string, int> where,
        List<string> memory)
    {
        try
        {
            List<string> ids = new List<string>();
            List<int> ranks = new List<int>();
            for (int i = 0; i < before.Length; i++)
            {
                Transform child = before[i];
                if (child == null || fresh.Contains(child))
                {
                    continue;
                }
                PriorityItem item = child.GetComponent<PriorityItem>();
                if (item == null || item.job == null || item.job.id == null)
                {
                    continue;
                }
                int rank;
                if (!where.TryGetValue(item.job.id, out rank))
                {
                    continue;  // the memory has no opinion about this one
                }
                ids.Add(item.job.id);
                ranks.Add(rank);
            }

            List<string> wrong = new List<string>();
            for (int a = 0; a < ids.Count && wrong.Count < 6; a++)
            {
                for (int b = a + 1; b < ids.Count && wrong.Count < 6; b++)
                {
                    if (ranks[a] > ranks[b])
                    {
                        wrong.Add(ShortId(ids[a]) + " above " + ShortId(ids[b])
                            + ", remembered below it");
                    }
                }
            }

            string said = string.Join("; ", wrong.ToArray());
            string before2;
            if (lastOutOfOrder.TryGetValue(staying, out before2)
                && before2 == said)
            {
                return;  // already said this one
            }
            lastOutOfOrder[staying] = said;

            if (wrong.Count == 0)
            {
                Log.LogInfo("Auto start: the rows on screen agree with the "
                    + "remembered list again at star " + staying);
                return;
            }

            Log.LogInfo("Auto start: " + wrong.Count + " row(s) already on "
                + "screen at star " + staying + " sit the opposite way round "
                + "from the remembered list, and this pass does not move "
                + "those: " + said);
            Log.LogInfo("Auto start: on screen at star " + staying + ": "
                + Spell(ids));
            List<string> shortMemory = new List<string>();
            for (int i = 0; i < memory.Count; i++)
            {
                shortMemory.Add(ShortId(EntryId(memory[i])));
            }
            Log.LogInfo("Auto start: remembered at star " + staying + ": "
                + string.Join(", ", shortMemory.ToArray()));
            Log.LogInfo("Auto start: " + fresh.Count + " row(s) have just come "
                + "back and are the only ones this pass will place");
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not compare the screen with the "
                + "remembered list: " + e.Message);
        }
    }

    private static string Spell(List<string> ids)
    {
        List<string> shown = new List<string>();
        for (int i = 0; i < ids.Count; i++)
        {
            shown.Add(ShortId(ids[i]));
        }
        return string.Join(", ", shown.ToArray());
    }

    // The memory follows the player. Everything on screen is taken as it
    // stands; the rows that are not back yet keep their old place relative to
    // whichever remembered row above them still exists.
    private void RefreshMemory(PriorityJobManager jobs, int staying)
    {
        try
        {
            Transform content = PriorityContent(jobs);
            if (content == null)
            {
                return;
            }

            // Before the merge below reads the record, and before the write at
            // the end of this method overwrites the star's own list. This is
            // the first write to that field after a load, ahead of the game's
            // export. See the v1.20.10 and v1.21.0 headers.
            SnapshotSavedList(staying);
            List<string> live = new List<string>();
            List<int> carriedBy = new List<int>();
            for (int i = 0; i < content.childCount; i++)
            {
                Transform child = content.GetChild(i);
                PriorityItem item = child.GetComponent<PriorityItem>();
                if (item == null || item.job == null)
                {
                    continue;
                }
                string entry = item.ExportJob();
                if (entry == null)
                {
                    continue;
                }
                // A fence on screen that this star has never had written
                // down belongs to the star we have just left and has not
                // been taken off the panel yet. Writing it down here is how
                // Space ended up holding Jupiter's. Skipped, not removed:
                // the eviction pass owns removing it. See the header.
                if (IsLayer(entry)
                    && !LayerRecordedHere(EntryId(entry), staying))
                {
                    NoteStrayLayer(EntryId(entry), staying);
                    continue;
                }
                live.Add(entry);
                carriedBy.Add(child.GetInstanceID());
            }
            if (live.Count == 0)
            {
                return; // the panel is not built yet; do not forget anything
            }
            List<string> merged = Merge(Memory(staying), live);
            remembered[staying] = merged;

            // Everything on screen now counts as "where the player left it"
            // from here on, so the next pass will not move any of it. Rebuilt
            // rather than added to, so a job that completes and comes back
            // hours later is new again. See the header.
            Dictionary<string, int> seen = new Dictionary<string, int>();
            for (int i = 0; i < live.Count; i++)
            {
                seen[EntryId(live[i])] = carriedBy[i];
            }
            seenRows[staying] = seen;

            LogLiveOrder(staying, live);

            Star star = StarMap.GetStarByIndex(staying);
            if (star != null && star.controlSettings != null)
            {
                // Not "merged". Merge puts back remembered rows that are not
                // live, layers included, and this list is the one the game
                // rebuilds the panel from. See the header.
                List<string> given = OwnedLayersOnly(merged, staying);
                star.controlSettings.priorityIdList = given;
                LogHandedToGame(staying, given);
            }
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not update the remembered "
                + "priority list: " + e.Message);
        }
    }

    // The panel as one line, read straight off the screen rather than off
    // any list we keep. Null when there is no panel to read, which is not the
    // same as an empty one and must not read as a change.
    private string ShapeOf(PriorityJobManager jobs)
    {
        try
        {
            if (jobs == null || jobs.priorityJobDict == null) { return null; }
            Transform content = PriorityContent(jobs);
            if (content == null) { return null; }
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < content.childCount; i++)
            {
                PriorityItem item =
                    content.GetChild(i).GetComponent<PriorityItem>();
                if (i > 0) { sb.Append(", "); }
                if (item == null || item.job == null || item.job.id == null)
                {
                    sb.Append("?");
                    continue;
                }
                sb.Append(ShortId(item.job.id));
            }
            return sb.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // A layer's guid says nothing to anyone reading the log, but the three
    // layers on a star have to be told apart or a line saying one of them
    // moved is unreadable - which is exactly what happened to the report
    // this was written for. Four characters of the guid is enough.
    private static string ShortId(string id)
    {
        if (id == null) { return "?"; }
        if (!id.StartsWith("*acc_")) { return id; }
        string guid = id.Substring(5);
        return "ACC#" + (guid.Length > 4 ? guid.Substring(0, 4) : guid);
    }

    // Names the pass that changed the panel, between passes. Returns the
    // shape it read so the caller can hand it to the next check.
    private string NoteMover(int staying, string was, string who,
        PriorityJobManager jobs)
    {
        try
        {
            string now = ShapeOf(jobs);
            if (now == null || was == null || now == was) { return now; }
            int times;
            moverLogCount.TryGetValue(staying, out times);
            if (times < MAX_ORDER_LOGS)
            {
                moverLogCount[staying] = times + 1;
                Log.LogInfo("Auto start: '" + who + "' changed the priority "
                    + "list at star " + staying + " to: " + now);
            }
            return now;
        }
        catch (Exception)
        {
            return was;
        }
    }

    // What the priority panel is actually showing, in the order it is showing
    // it, written out the first time it is seen and again whenever it changes.
    // A layer is written as "ACC" plus its limits rather than its guid, which
    // says nothing to anyone reading the log.
    // v1.21.1 diagnostic, no behaviour. The game rebuilds the panel by
    // walking this list, and it is the last thing written to it before the
    // game draws its next batch of rows. Two rows came back from the 1.21.0
    // arrival the wrong way round between one pass and the next with nothing
    // of ours logged in between, and there is no way to tell from the log
    // whether we handed the game a bad list or a good one it then reordered.
    // This says which. See the v1.21.1 header.
    private static readonly Dictionary<int, string> lastHandedOver =
        new Dictionary<int, string>();

    private void LogHandedToGame(int staying, List<string> given)
    {
        try
        {
            if (given == null || given.Count == 0)
            {
                return;
            }
            List<string> names = new List<string>();
            for (int i = 0; i < given.Count; i++)
            {
                names.Add(ShortId(EntryId(given[i])));
            }
            string shape = string.Join(", ", names.ToArray());

            string last;
            if (lastHandedOver.TryGetValue(staying, out last) && last == shape)
            {
                return; // unchanged since the last time, so nothing to say
            }
            lastHandedOver[staying] = shape;
            Log.LogInfo("Auto start: handed the game " + given.Count
                + " row(s) to rebuild star " + staying + " from: " + shape);
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not write the handed over "
                + "priority list: " + e.Message);
        }
    }

    private void LogLiveOrder(int staying, List<string> live)
    {
        try
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < live.Count; i++)
            {
                if (i > 0) { sb.Append(", "); }
                string id = EntryId(live[i]);
                if (id != null && id.StartsWith("*acc_"))
                {
                    int bar = live[i].LastIndexOf('|');
                    // Tagged, because a star with three layers used to print
                    // three identical words and a line saying one of them had
                    // moved could not be read. See ShortId.
                    sb.Append(ShortId(id));
                    sb.Append("[");
                    sb.Append(bar < 0 ? "?" : live[i].Substring(bar + 1));
                    sb.Append("]");
                }
                else
                {
                    sb.Append(id);
                }
            }
            string shape = sb.ToString();

            string last;
            if (loggedOrder.TryGetValue(staying, out last) && last == shape)
            {
                return; // nothing has moved since the last time
            }
            int times;
            loggedOrderCount.TryGetValue(staying, out times);
            if (times >= MAX_ORDER_LOGS)
            {
                loggedOrder[staying] = shape;
                return;
            }
            loggedOrder[staying] = shape;
            loggedOrderCount[staying] = times + 1;
            Log.LogInfo("Auto start: the priority list at star " + staying
                + " is showing " + live.Count + " row(s): " + shape);
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not write the priority list "
                + "order: " + e.Message);
        }
    }

    // The live list wins outright for everything it contains - that is what
    // makes dragging a row stick. Remembered rows that are not live are kept
    // and re-emitted just before the live row that used to follow them, which
    // is what holds a place open for a research that has finished or has not
    // come back from the reset yet.
    //
    // Layers used to be the exception here, dropped as soon as they stopped
    // being live, on the grounds that only deleting one could do that. A Big
    // Bang does it too, and since v1.6.0 the record has to outlive that. So
    // deletion is taken from PriorityAccumulationItem.Delete instead, and the
    // reset's own clear-out is done by DropDeadLayers. See the header.
    private static List<string> Merge(List<string> memory, List<string> live)
    {
        List<string> result = new List<string>();
        if (memory == null || memory.Count == 0)
        {
            result.AddRange(live);
            return result;
        }

        HashSet<string> liveIds = new HashSet<string>();
        for (int i = 0; i < live.Count; i++)
        {
            liveIds.Add(EntryId(live[i]));
        }

        Dictionary<string, int> at = new Dictionary<string, int>();
        bool[] skip = new bool[memory.Count];
        for (int i = 0; i < memory.Count; i++)
        {
            string id = EntryId(memory[i]);
            if (!at.ContainsKey(id))
            {
                at[id] = i;
            }
            skip[i] = liveIds.Contains(id);
        }

        int cursor = 0;
        for (int i = 0; i < live.Count; i++)
        {
            int pos;
            if (at.TryGetValue(EntryId(live[i]), out pos) && pos >= cursor)
            {
                for (int k = cursor; k < pos; k++)
                {
                    if (!skip[k])
                    {
                        result.Add(memory[k]);
                        skip[k] = true;
                    }
                }
                cursor = pos + 1;
            }
            result.Add(live[i]);
        }
        for (int k = cursor; k < memory.Count; k++)
        {
            if (!skip[k])
            {
                result.Add(memory[k]);
            }
        }
        return result;
    }

    // Seeded from the save the first time a star is asked about, so a game
    // loaded part way through a rebuild picks up where it left off.
    private List<string> Memory(int starIndex)
    {
        List<string> list;
        if (remembered.TryGetValue(starIndex, out list) && list != null)
        {
            return list;
        }
        list = new List<string>();
        Star star = StarMap.GetStarByIndex(starIndex);
        if (star != null && star.controlSettings != null
            && star.controlSettings.priorityIdList != null)
        {
            list.AddRange(star.controlSettings.priorityIdList);
        }
        remembered[starIndex] = list;
        return list;
    }

    // The player pressed the bin on an Accumulation Layer. That is the only
    // honest "this layer is gone" signal there is - the row also vanishes on a
    // Big Bang, and that one has to be survivable. Note the game leaves the id
    // in priorityJobDict afterwards, so the layer could never be rebuilt in
    // this session anyway; striking it from the record keeps it from coming
    // back in the next one either.
    internal static void ForgetLayer(PriorityAccumulationItem item)
    {
        if (shared == null || item == null || item.job == null
            || item.job.id == null)
        {
            return;
        }
        try
        {
            string id = item.job.id;
            foreach (KeyValuePair<int, List<string>> pair in shared.remembered)
            {
                List<string> list = pair.Value;
                if (list == null)
                {
                    continue;
                }
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (EntryId(list[i]) == id)
                    {
                        list.RemoveAt(i);
                    }
                }
            }

            // And out of the game's own lists, because ClearDeadLayers now
            // frees the dictionary slot of a destroyed row: the saved list
            // would otherwise rebuild the layer the player just binned on
            // the next arrival. See the header.
            for (int idx = -1; idx < StarMap.starCount; idx++)
            {
                Star star = StarMap.GetStarByIndex(idx);
                if (star == null || star.controlSettings == null
                    || star.controlSettings.priorityIdList == null)
                {
                    continue;
                }
                List<string> live = star.controlSettings.priorityIdList;
                for (int i = live.Count - 1; i >= 0; i--)
                {
                    if (EntryId(live[i]) == id)
                    {
                        live.RemoveAt(i);
                    }
                }
            }

            PriorityJobManager jobs = PriorityJobManager.shared;
            if (jobs != null && jobs.priorityJobDict != null)
            {
                jobs.priorityJobDict.Remove(id);
            }
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not forget a deleted "
                + "accumulation layer: " + e.Message);
        }
    }

    // ------------------------------------------------------------------
    // The record on disk

    // Beside the save it belongs to, so each of the fifteen slots keeps its
    // own and copying a save copies its history. See the header.
    private static string LedgerPathFor(string savePath)
    {
        if (string.IsNullOrEmpty(savePath))
        {
            return null;
        }
        return savePath + ".layers";
    }

    // Postfix of SaveLoadManager.SaveGameToPath. ExportSaveCaught has already
    // called ExportPriorityIdList by then, and that runs the merge above, so
    // what is in hand here is current.
    internal static void WriteLedger(string savePath)
    {
        if (shared == null)
        {
            return;
        }
        try
        {
            string path = LedgerPathFor(savePath);
            if (path == null || shared.remembered.Count == 0)
            {
                // Nothing learned yet. Leaving any existing file alone matters:
                // this is also what a save taken before the panel is built
                // looks like, and it must not wipe the record.
                return;
            }
            StringBuilder sb = new StringBuilder();
            sb.Append("# STI community auto start - remembered priority lists\n");
            sb.Append("# One 'star' line per place, then its rows in order.\n");
            sb.Append("v 1\n");
            foreach (KeyValuePair<int, List<string>> pair in shared.remembered)
            {
                List<string> list = pair.Value;
                if (list == null || list.Count == 0)
                {
                    continue;
                }
                sb.Append("star ").Append(pair.Key).Append("\n");
                for (int i = 0; i < list.Count; i++)
                {
                    string entry = list[i];
                    if (string.IsNullOrEmpty(entry)
                        || entry.IndexOf('\n') >= 0
                        || entry.IndexOf('\r') >= 0)
                    {
                        continue;
                    }
                    sb.Append("e ").Append(entry).Append("\n");
                }
            }
            string text = sb.ToString();
            if (text == shared.lastLedger && File.Exists(path))
            {
                return; // the autosave runs every thirty seconds
            }
            File.WriteAllText(path, text);
            shared.lastLedger = text;
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not write the remembered "
                + "priority list: " + e.Message);
        }
    }

    // Postfix of SaveLoadManager.LoadGameFromPath, which runs after LoadSave
    // has cleared the old one. A missing file is the normal case for a save
    // that predates v1.6.0 and is not worth a warning.
    internal static void ReadLedger(string savePath)
    {
        if (shared == null)
        {
            return;
        }
        try
        {
            string path = LedgerPathFor(savePath);
            if (path == null || !File.Exists(path))
            {
                return;
            }
            string[] lines = File.ReadAllLines(path);
            List<string> current = null;
            int stars = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrEmpty(line) || line[0] == '#')
                {
                    continue;
                }
                if (line.StartsWith("star "))
                {
                    int idx;
                    if (int.TryParse(line.Substring(5).Trim(), out idx))
                    {
                        current = new List<string>();
                        shared.remembered[idx] = current;
                        stars++;
                    }
                    else
                    {
                        current = null;
                    }
                    continue;
                }
                if (line.StartsWith("e ") && current != null)
                {
                    current.Add(line.Substring(2));
                }
            }
            shared.lastLedger = null;
            if (stars > 0)
            {
                Log.LogInfo("Auto start: read the remembered priority list for "
                    + stars + " place(s)");
            }
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not read the remembered "
                + "priority list: " + e.Message);
        }
    }

    // AttemptInsertJob, plus a note of the fact for LiftNewRowsAboveLayers.
    // The ContainsKey check is the point of the wrapper: AttemptInsertJob is
    // safe to call for a job that is already queued and does nothing in that
    // case, and a row that was already in the list is not one we just added -
    // lifting it would be moving somebody else's row across their fence.
    private void Insert(PriorityJobManager jobs, string id, PriorityJob job)
    {
        bool isNew = jobs == null || jobs.priorityJobDict == null
            || !jobs.priorityJobDict.ContainsKey(id);
        jobs.AttemptInsertJob(id, job);
        if (isNew && !addedThisPass.Contains(id))
        {
            addedThisPass.Add(id);
        }
    }

    // Does anything already hold a place for this row? BOTH the remembered
    // list and the star's own saved priorityIdList, because either one saying
    // yes means somebody has already decided where this row goes and it is not
    // ours to decide again.
    //
    // This used to consult Memory(staying) alone, which returns the record
    // when there is one and only falls back to the saved list when there is
    // not. A record that is older and shorter than the save then made the
    // player's own rows look brand new: Fuzzied's Saturn record held 17 of his
    // 31 rows, and LiftNewRowsAboveLayers hauled 12 of the other 14 above his
    // Accumulation Layer on a single load. See the v1.20.8 header.
    private bool MemoryKnows(int staying, string id)
    {
        try
        {
            List<string> memory = Memory(staying);
            for (int i = 0; i < memory.Count; i++)
            {
                if (EntryId(memory[i]) == id)
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
        }
        return SavedListKnows(staying, id);
    }

    // The star's list as the SAVE had it, from the snapshot. Reading
    // controlSettings.priorityIdList live here is what 1.20.8 got wrong: the
    // game overwrites that field from the panel on every export, so during a
    // staged build it no longer holds what the player saved.
    private bool SavedListKnows(int staying, string id)
    {
        try
        {
            List<string> saved;
            if (!savedAtLoad.TryGetValue(staying, out saved) || saved == null)
            {
                return false;
            }
            for (int i = 0; i < saved.Count; i++)
            {
                if (EntryId(saved[i]) == id)
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
        }
        return false;
    }

    // Backstop entry point, from the ExportPriorityIdList PREFIX. The call
    // that actually matters is the one in RefreshMemory, because that is the
    // first write to the field after a load. See the v1.20.10 header.
    internal static void SnapshotSavedList()
    {
        if (shared == null)
        {
            return;
        }
        SnapshotSavedList(CurrentStarIndex());
    }

    // Take a copy of the star's list at the last moment it is still what the
    // save put there. Only the first call per star per load is kept; every
    // later one would be copying a rewritten list back over the snapshot,
    // which is exactly the mistake being fixed.
    internal static void SnapshotSavedList(int staying)
    {
        if (shared == null)
        {
            return;
        }
        try
        {
            if (staying == int.MinValue
                || shared.savedAtLoad.ContainsKey(staying))
            {
                return;
            }
            Star star = StarMap.GetStarByIndex(staying);
            if (star == null || star.controlSettings == null
                || star.controlSettings.priorityIdList == null)
            {
                return;
            }
            List<string> copy =
                new List<string>(star.controlSettings.priorityIdList);
            if (copy.Count == 0)
            {
                // Nothing to learn from an empty list, and storing it would
                // mark the star done and lock the real one out for the rest of
                // the session. Leave it for the next export.
                return;
            }
            shared.savedAtLoad[staying] = copy;
            NoteSnapshot(staying, copy.Count);
            RepairStaleRecord(staying, copy);
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not note the saved priority "
                + "list: " + e.Message);
        }
    }

    // The record beside the save can be older than the save, and that is the
    // supply line for this whole family of bugs: a 17 row record for a 31 row
    // star. The save has just handed over the truth, so when the record is
    // plainly short of it, rebuild it. See the v1.21.0 header for why it is
    // only ever done in the shorter direction, why the Big Bang needs no
    // special case, and why seenRows is left alone.
    private static void RepairStaleRecord(int staying, List<string> saved)
    {
        List<string> record;
        if (!shared.remembered.TryGetValue(staying, out record)
            || record == null || record.Count >= saved.Count)
        {
            return;
        }
        shared.remembered[staying] = new List<string>(saved);
        Log.LogInfo("Auto start: the remembered list for star " + staying
            + " had " + record.Count + " row(s) but the save has "
            + saved.Count + ", so it was out of date and has been rebuilt "
            + "from the save; a row that comes back later now goes where you "
            + "had it rather than to the bottom");
    }

    private static void NoteSnapshot(int staying, int rows)
    {
        Log.LogInfo("Auto start: noted the " + rows + " row(s) star " + staying
            + " was saved with, so a row the game has not drawn yet is not "
            + "mistaken for a brand new job");
    }

    // Rows this pass has just created, moved from the end of the list - which
    // is below every Accumulation Layer - to just above the first one. Nothing
    // else in the list is touched, and a row anything already has a place for
    // is not eligible. See the header.
    private bool LiftNewRowsAboveLayers(PriorityJobManager jobs, int staying)
    {
        try
        {
            if (cfgNewJobsAboveLayers == null || !cfgNewJobsAboveLayers.Value
                || addedThisPass.Count == 0 || jobs == null)
            {
                return false;
            }
            Transform content = PriorityContent(jobs);
            if (content == null) { return false; }

            int count = content.childCount;
            Transform[] before = new Transform[count];
            int firstLayer = -1;
            for (int i = 0; i < count; i++)
            {
                Transform child = content.GetChild(i);
                before[i] = child;
                if (firstLayer >= 0) { continue; }
                PriorityItem item = child.GetComponent<PriorityItem>();
                if (item != null && item.job != null
                    && item.job.type == JobType.accumulation)
                {
                    firstLayer = i;
                }
            }
            // No fences, so the end of the list is just the end of the list.
            if (firstLayer < 0) { return false; }

            List<Transform> head = new List<Transform>();
            List<Transform> lift = new List<Transform>();
            List<Transform> rest = new List<Transform>();
            for (int i = 0; i < count; i++)
            {
                if (i < firstLayer)
                {
                    head.Add(before[i]);
                    continue;
                }
                PriorityItem item = before[i].GetComponent<PriorityItem>();
                if (item == null || item.job == null
                    || item.job.type == JobType.accumulation
                    || item.job.type == JobType.engine
                    // Lifting a spaceship row above the first layer only for
                    // SinkSpaceJobs to drop it to the bottom a few lines later
                    // would be two moves and a log line saying the opposite of
                    // what happened. Collectors are no longer sunk, so they are
                    // no longer excluded here either.
                    || (cfgSpaceLast.Value && IsSpaceJob(item.job.id)
                        && !IsCollector(item.job.type))
                    || !addedThisPass.Contains(item.job.id)
                    || MemoryKnows(staying, item.job.id))
                {
                    rest.Add(before[i]);
                    continue;
                }
                lift.Add(before[i]);
            }
            if (lift.Count == 0) { return false; }

            head.AddRange(lift);
            head.AddRange(rest);
            for (int i = 0; i < count; i++)
            {
                head[i].SetSiblingIndex(i);
            }
            Log.LogInfo("Auto start: moved " + lift.Count + " new row(s) above "
                + "your first Accumulation Layer, so a job that had never been "
                + "queued before did not start life fenced off");
            return true;
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not place the new rows: "
                + e.Message);
            return false;
        }
    }

    // ------------------------------------------------------------------
    // A layer belongs to one star (v1.19.0). See the header.

    // int.MinValue rather than -1, because -1 is Space and Space is a real
    // star with real layers of its own.
    private static int CurrentStarIndex()
    {
        Player player = Player.shared;
        if (player == null || player.location == null)
        {
            return int.MinValue;
        }
        return player.location.stayingStarIndex;
    }

    // Ownership as the PLAYER wrote it down, and nothing else. This is
    // deliberately narrower than LayerBelongsHere, which also accepts the
    // mod's remembered list: that one is asked by the eviction pass, which
    // does not write either list, so a record can safely vouch for itself
    // there. Recording is the opposite case. RefreshMemory writes BOTH the
    // remembered list and the star's own, so if the record counted as its own
    // proof, one sweep taken while the previous star's fences were still on
    // screen would make them that star's for ever. See the header.
    private static bool LayerRecordedHere(string id, int here)
    {
        Star star = StarMap.GetStarByIndex(here);
        if (star != null && star.controlSettings != null
            && ListHasLayer(star.controlSettings.priorityIdList, id))
        {
            return true;
        }
        int born;
        return layerHome.TryGetValue(id, out born) && born == here;
    }

    // The merge, minus any layer this star does not actually own. Used for
    // the STAR's saved list only, never for the memory. Merge re-emits
    // remembered rows that are not live, which is what holds a place open
    // for a research that has finished, and it cannot tell that a layer the
    // two gates have just refused is not in that category. The star's list
    // is what PriorityJobManager.LoadPriority builds the panel from, so a
    // stale entry reaching it comes straight back on screen. See the header.
    private static List<string> OwnedLayersOnly(List<string> merged, int here)
    {
        List<string> result = new List<string>(merged.Count);
        for (int i = 0; i < merged.Count; i++)
        {
            string entry = merged[i];
            if (IsLayer(entry) && !LayerRecordedHere(EntryId(entry), here))
            {
                continue;
            }
            result.Add(entry);
        }
        return KeepOwnLayers(result, here);
    }

    // The other half, and the one that cost Fuzzied his two Space layers.
    //
    // "merged" is the star's memory plus what is on the panel. On arrival the
    // panel still belongs to the star just left, so neither source holds this
    // star's own layers, and writing merged over the saved list DELETES them.
    // The star's own saved list is the only place they exist, and nothing
    // above ever reads it as a source. So read it here, and put back any
    // layer the result lost, at the place it held.
    //
    // Binning a layer stays safe with no extra bookkeeping: ForgetLayer, the
    // prefix of PriorityAccumulationItem.Delete, already strips the layer out
    // of every star's priorityIdList, so a binned layer is not in "saved" to
    // be put back. Absent because the player binned it, versus absent because
    // the panel belongs elsewhere, is exactly the difference. See the header.
    private static List<string> KeepOwnLayers(List<string> result, int here)
    {
        Star star = StarMap.GetStarByIndex(here);
        if (star == null || star.controlSettings == null)
        {
            return result;
        }
        List<string> saved = star.controlSettings.priorityIdList;
        if (saved == null)
        {
            return result;
        }
        for (int i = 0; i < saved.Count; i++)
        {
            string entry = saved[i];
            if (!IsLayer(entry))
            {
                continue;
            }
            string id = EntryId(entry);
            if (ListHasLayer(result, id))
            {
                continue;
            }
            result.Insert(i <= result.Count ? i : result.Count, entry);
            NoteLayerKept(id, here);
        }
        return result;
    }

    // Said once per layer per star, like the other two notes.
    private static readonly Dictionary<string, int> keptSaid =
        new Dictionary<string, int>();

    private static void NoteLayerKept(string id, int here)
    {
        int last;
        if (keptSaid.TryGetValue(id, out last) && last == here)
        {
            return;
        }
        keptSaid[id] = here;
        Log.LogInfo("Auto start: " + ShortId(id) + " is star " + here
            + "'s own layer but it is not on the panel, so it is being kept "
            + "in star " + here + "'s saved list rather than dropped");
    }

    // Said once per layer per star, the same way the recording note is.
    private static readonly Dictionary<string, int> notRebuilt =
        new Dictionary<string, int>();

    private static void NoteNotRebuilt(string id, int here)
    {
        int last;
        if (notRebuilt.TryGetValue(id, out last) && last == here)
        {
            return;
        }
        notRebuilt[id] = here;
        Log.LogInfo("Auto start: the remembered list at star " + here
            + " still has " + ShortId(id) + ", but star " + here + "'s own "
            + "saved list does not, so it is not being put back on screen");
    }

    // Said once per layer per star, because the handover lasts many sweeps
    // and this would otherwise be the loudest line in the log.
    private static readonly Dictionary<string, int> straySaid =
        new Dictionary<string, int>();

    private static void NoteStrayLayer(string id, int here)
    {
        int last;
        if (straySaid.TryGetValue(id, out last) && last == here)
        {
            return;
        }
        straySaid[id] = here;
        Log.LogInfo("Auto start: " + ShortId(id) + " is on the panel but star "
            + here + " has never had it written down, so it is not being "
            + "recorded there");
    }

    // Where a layer belongs, asked of the player's own record rather than
    // worked out. A star's saved list and the remembered list beside it are
    // both written from what was on screen when the player was last here, so
    // between them they say what the player put on this star. A layer built
    // this session is in neither until the first export, so the star it was
    // built on counts too. See the header.
    private static bool LayerBelongsHere(string id, int here)
    {
        Star star = StarMap.GetStarByIndex(here);
        if (star != null && star.controlSettings != null
            && star.controlSettings.priorityIdList != null
            && ListHasLayer(star.controlSettings.priorityIdList, id))
        {
            return true;
        }
        if (shared != null)
        {
            List<string> memory;
            if (shared.remembered.TryGetValue(here, out memory)
                && memory != null && ListHasLayer(memory, id))
            {
                return true;
            }
        }
        int born;
        return layerHome.TryGetValue(id, out born) && born == here;
    }

    // An entry carries its settings after a bar, so compare the id only.
    private static bool ListHasLayer(List<string> list, string id)
    {
        if (list == null)
        {
            return false;
        }
        for (int i = 0; i < list.Count; i++)
        {
            if (IsLayer(list[i]) && EntryId(list[i]) == id)
            {
                return true;
            }
        }
        return false;
    }

    // Whether any star has this layer written down. A layer the player has
    // just built is in nobody's list, which is how the button tells a new
    // layer from a row left over from somewhere else.
    private static bool LayerKnownAnywhere(string id)
    {
        for (int idx = 0; idx < StarMap.starCount; idx++)
        {
            if (LayerWrittenAt(id, idx))
            {
                return true;
            }
        }
        return LayerWrittenAt(id, -1);
    }

    private static bool LayerWrittenAt(string id, int idx)
    {
        Star star = StarMap.GetStarByIndex(idx);
        if (star != null && star.controlSettings != null
            && ListHasLayer(star.controlSettings.priorityIdList, id))
        {
            return true;
        }
        List<string> memory;
        return shared != null
            && shared.remembered.TryGetValue(idx, out memory)
            && ListHasLayer(memory, id);
    }

    // The Add Accumulation Layer button, and nothing else. A layer nobody
    // has written down anywhere is one the player has just built, so it
    // belongs to the star they are standing on until the first export
    // writes it into that star's list. Anything already written down is a
    // row from somewhere else and is left alone for the eviction pass.
    internal static void AdoptNewLayers(PriorityJobManager jobs)
    {
        if (jobs == null || jobs.priorityJobDict == null)
        {
            return;
        }
        int here = CurrentStarIndex();
        if (here == int.MinValue)
        {
            return;
        }
        foreach (KeyValuePair<string, PriorityJob> pair in jobs.priorityJobDict)
        {
            if (IsLayer(pair.Key) && !layerHome.ContainsKey(pair.Key)
                && !LayerKnownAnywhere(pair.Key))
            {
                layerHome[pair.Key] = here;
            }
        }
    }

    // Drop every live layer that belongs to some other star. Runs on the
    // way in (LoadPriority) so the panel is right on arrival, and on the way
    // out (ExportPriorityIdList) because that write is the only way a layer
    // id reaches a star's saved list.
    // Puts the saved list back onto the panel using a running slot that only
    // advances for entries the panel actually holds, so a half drawn panel
    // comes out in the right relative order instead of an order made of
    // clamped indices. A layer already on screen is moved too, which the
    // game's own pass never does. See the v1.22.0 header.
    internal static void ReapplySavedOrder(PriorityJobManager jobs)
    {
        try
        {
            if (jobs == null || jobs.priorityJobDict == null)
            {
                return;
            }
            Star star = StarMap.GetStarByIndex(CurrentStarIndex());
            if (star == null || star.controlSettings == null)
            {
                return;
            }
            List<string> saved = star.controlSettings.priorityIdList;
            if (saved == null || saved.Count == 0)
            {
                return;
            }

            int slot = 0;
            for (int i = 0; i < saved.Count; i++)
            {
                string id = EntryId(saved[i]);
                PriorityJob job;
                if (id == null || id.Length == 0
                    || !jobs.priorityJobDict.TryGetValue(id, out job)
                    || !HasRow(job))
                {
                    continue;   // not drawn yet, so it takes no slot either
                }
                job.gameObject.transform.SetSiblingIndex(slot);
                slot++;
            }
            NoteReapplied(slot, saved.Count);
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not put the saved priority "
                + "order back: " + e.Message);
        }
    }

    // Deduped, because LoadPriority runs on every pass and an unchanged line
    // repeated forty times is what made the earlier logs unreadable.
    private static string lastReapplied = null;

    private static void NoteReapplied(int placed, int saved)
    {
        if (placed == 0)
        {
            return;
        }
        string said = placed + " of " + saved;
        if (said == lastReapplied)
        {
            return;
        }
        lastReapplied = said;
        Log.LogInfo("Auto start: the game had just spread your saved list "
            + "over a panel that only holds part of it, so the order was put "
            + "back: " + placed + " row(s) on screen out of the " + saved
            + " you have saved here");
    }

    internal static void EvictForeignLayers(PriorityJobManager jobs)
    {
        if (cfgLayerHome == null || !cfgLayerHome.Value || jobs == null
            || jobs.priorityJobDict == null)
        {
            return;
        }
        int here = CurrentStarIndex();
        if (here == int.MinValue)
        {
            return;
        }
        try
        {
            // This is a prefix of LoadPriority, so freeing the slot here
            // is what lets the game rebuild the layer from this star's own
            // saved list a moment later. See the header.
            ClearDeadLayers(jobs);

            // Collected first: evicting walks the same dictionary.
            List<string> doomed = null;
            foreach (KeyValuePair<string, PriorityJob> pair in jobs.priorityJobDict)
            {
                if (!IsLayer(pair.Key) || LayerBelongsHere(pair.Key, here))
                {
                    continue;
                }
                if (doomed == null) { doomed = new List<string>(); }
                doomed.Add(pair.Key);
            }
            if (doomed == null)
            {
                return;
            }
            for (int i = 0; i < doomed.Count; i++)
            {
                PriorityJob job;
                if (jobs.priorityJobDict.TryGetValue(doomed[i], out job)
                    && job != null && job.gameObject != null)
                {
                    // Out of the parent before Destroy, which is deferred to
                    // the end of the frame. See the header.
                    job.gameObject.transform.SetParent(null);
                }
                // Frees the dictionary slot as well as destroying the row,
                // so the owning star can rebuild it when you go back.
                jobs.AttemptRemoveJob(doomed[i]);
                Log.LogInfo("Auto start: " + ShortId(doomed[i])
                    + " is not on this star's list, taken off the screen at "
                    + "star " + here);
            }
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not tidy the Accumulation "
                + "Layers: " + e.Message);
        }
    }

    // The player pressed the bin. The layer is gone for good, so its home
    // goes with it - otherwise a later rebuild would refuse to adopt a new
    // layer that happened to reuse the id.
    internal static void ForgetLayerHome(string id)
    {
        if (id != null)
        {
            layerHome.Remove(id);
        }
    }

    private static bool IsLayer(string entry)
    {
        return entry != null && entry.StartsWith(LAYER_PREFIX);
    }

    // Whether a layer still has a row on the panel behind it. Unity's == is
    // overloaded, so a destroyed object compares equal to null even though
    // the reference is still sitting in the dictionary; a row on its way out
    // is unparented before it is destroyed, so that counts as gone as well.
    private static bool HasRow(PriorityJob job)
    {
        return job != null && job.gameObject != null
            && job.gameObject.transform.parent != null;
    }

    // Frees the dictionary slot of every layer that has lost its row.
    // AddAccumulationLayer(string, int) is guarded by
    // !priorityJobDict.ContainsKey(text), so an entry left behind means that
    // layer can never be rebuilt again - not by the game reading the star's
    // saved list, and not by the memory either. See the header.
    internal static int ClearDeadLayers(PriorityJobManager jobs)
    {
        if (jobs == null || jobs.priorityJobDict == null)
        {
            return 0;
        }
        List<string> dead = null;
        // Collected first: removing walks the same dictionary.
        foreach (KeyValuePair<string, PriorityJob> pair in jobs.priorityJobDict)
        {
            if (!IsLayer(pair.Key) || HasRow(pair.Value))
            {
                continue;
            }
            if (dead == null) { dead = new List<string>(); }
            dead.Add(pair.Key);
        }
        if (dead == null)
        {
            return 0;
        }
        for (int i = 0; i < dead.Count; i++)
        {
            jobs.priorityJobDict.Remove(dead[i]);
            Log.LogInfo("Auto start: " + ShortId(dead[i]) + " was still in "
                + "the job list with no row behind it, which is what stops a "
                + "layer ever coming back; freed it");
        }
        return dead.Count;
    }

    // A layer carries its limits after a '|'; everything else is a bare id.
    private static string EntryId(string entry)
    {
        if (entry == null)
        {
            return "";
        }
        int bar = entry.IndexOf('|');
        if (bar < 0)
        {
            return entry;
        }
        return entry.Substring(0, bar);
    }

    // Producers up, engine down, as Fuzzied put it: "resource adding to the top
    // and the most draining to the bottom, it fills down". Segment-wise, so an
    // Accumulation Layer keeps both its place and its meaning - see the header.
    private void ReorderPriorityList(PriorityJobManager jobs)
    {
        try
        {
            if (jobs == null || jobs.priorityJobDict == null) { return; }
            Transform content = PriorityContent(jobs);
            if (content == null) { return; }

            List<PriorityItem> items = new List<PriorityItem>();
            for (int i = 0; i < content.childCount; i++)
            {
                PriorityItem item =
                    content.GetChild(i).GetComponent<PriorityItem>();
                if (item == null || item.job == null) { return; }
                items.Add(item);
            }

            List<PriorityItem> ordered = new List<PriorityItem>();
            List<PriorityItem> segment = new List<PriorityItem>();
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].job.type == JobType.accumulation)
                {
                    SortSegment(segment);
                    ordered.AddRange(segment);
                    segment.Clear();
                    ordered.Add(items[i]); // a layer never moves
                    continue;
                }
                segment.Add(items[i]);
            }
            SortSegment(segment);
            ordered.AddRange(segment);

            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].transform.SetSiblingIndex(i);
            }
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not order the priority list: "
                + e.Message);
        }
    }

    // List.Sort is not stable, and the order the player chose between two jobs
    // of the same rank is theirs to keep, so the original index is the last
    // tie-break.
    private static void SortSegment(List<PriorityItem> segment)
    {
        int count = segment.Count;
        if (count < 2) { return; }
        int[] rank = new int[count];
        int[] was = new int[count];
        PriorityItem[] copy = new PriorityItem[count];
        for (int i = 0; i < count; i++)
        {
            copy[i] = segment[i];
            rank[i] = RankOf(segment[i].job.type);
            was[i] = i;
        }
        // Small lists (a priority list is tens of rows), so an insertion sort
        // is both stable by construction and plenty fast.
        for (int i = 1; i < count; i++)
        {
            PriorityItem item = copy[i];
            int r = rank[i];
            int w = was[i];
            int j = i - 1;
            while (j >= 0 && (rank[j] > r || (rank[j] == r && was[j] > w)))
            {
                copy[j + 1] = copy[j];
                rank[j + 1] = rank[j];
                was[j + 1] = was[j];
                j--;
            }
            copy[j + 1] = item;
            rank[j + 1] = r;
            was[j + 1] = w;
        }
        segment.Clear();
        for (int i = 0; i < count; i++)
        {
            segment.Add(copy[i]);
        }
    }

    private static int RankOf(JobType type)
    {
        if (type == JobType.enProd) { return 0; } // energy feeds everything
        if (type == JobType.reProd) { return 1; } // then the resources
        if (type == JobType.engine) { return 3; } // the tank drinks the rest
        return 2;                                 // research and infra
    }

    // The rows all share one parent, so any job that has a GameObject can name
    // it. Cheaper and less brittle than reflecting priorityJobContent out of
    // PriorityJobManager.
    private static Transform PriorityContent(PriorityJobManager jobs)
    {
        foreach (KeyValuePair<string, PriorityJob> pair in jobs.priorityJobDict)
        {
            PriorityJob job = pair.Value;
            if (job != null && job.gameObject != null)
            {
                return job.gameObject.transform.parent;
            }
        }
        return null;
    }

    // Push engine modules to the very bottom of the priority list, keeping
    // their order among themselves, and let them cross Accumulation Layers on
    // the way down - the one place anything is allowed to. Safe because the
    // running accumulation model is folded forward with Math.Max, so moving
    // down can only fence a row off harder. See the header.
    //
    // With all = false only the modules THIS PLUGIN switched on are moved,
    // which is the older and much narrower v1.2.0 repair: a job added later
    // can land underneath one of ours. A module the player placed by hand is
    // left where they put it in that mode.
    private bool SinkEngineJobs(PriorityJobManager jobs, bool all)
    {
        try
        {
            if (jobs == null || jobs.priorityJobDict == null) { return false; }
            Transform content = PriorityContent(jobs);
            if (content == null) { return false; }

            int count = content.childCount;
            Transform[] before = new Transform[count];
            List<Transform> keep = new List<Transform>();
            List<Transform> sink = new List<Transform>();
            for (int i = 0; i < count; i++)
            {
                Transform child = content.GetChild(i);
                before[i] = child;
                PriorityItem item = child.GetComponent<PriorityItem>();
                if (item == null || item.job == null
                    || item.job.type != JobType.engine
                    || (!all && !engineJobIds.Contains(item.job.id)))
                {
                    keep.Add(child);
                    continue;
                }
                sink.Add(child);
            }
            if (sink.Count == 0) { return false; }

            keep.AddRange(sink);
            bool moved = false;
            for (int i = 0; i < count; i++)
            {
                if (before[i] != keep[i]) { moved = true; break; }
            }
            if (!moved) { return false; }
            for (int i = 0; i < count; i++)
            {
                keep[i].SetSiblingIndex(i);
            }
            return true;
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not sink the engine rows: "
                + e.Message);
            return false;
        }
    }

    // Every prefixedId the spaceship owns. Built once - templates are loaded
    // with the game and do not change - and left null until they are there, so
    // an early pass rebuilds rather than caching an empty set. See the header.
    private static HashSet<string> spaceJobIds;

    private static HashSet<string> SpaceJobIds()
    {
        if (spaceJobIds != null) { return spaceJobIds; }
        try
        {
            Star ship = StarMap.GetStarByIndex(-1);
            if (ship == null || ship.id == null) { return null; }

            HashSet<string> ids = new HashSet<string>();
            Dictionary<string, ResearchTemplate> research =
                ResearchTemplatesOf(ship);
            if (research != null)
            {
                foreach (ResearchTemplate t in research.Values)
                {
                    if (t != null) { ids.Add(t.prefixedId); }
                }
            }
            Dictionary<string, InfraTemplate> infra = InfraTemplatesOf(ship);
            if (infra != null)
            {
                foreach (InfraTemplate t in infra.Values)
                {
                    if (t != null) { ids.Add(t.prefixedId); }
                }
            }
            Dictionary<string, EnergySourceTemplate> energy =
                EnergyTemplatesOf(ship);
            if (energy != null)
            {
                foreach (EnergySourceTemplate t in energy.Values)
                {
                    if (t != null) { ids.Add(t.prefixedId); }
                }
            }
            Dictionary<string, ElementSourceTemplate> element =
                ElementTemplatesOf(ship);
            if (element != null)
            {
                foreach (ElementSourceTemplate t in element.Values)
                {
                    if (t != null) { ids.Add(t.prefixedId); }
                }
            }
            if (ids.Count == 0)
            {
                return null; // the ship's data has not loaded yet
            }
            spaceJobIds = ids;
            Log.LogInfo("Auto start: the spaceship owns " + ids.Count
                + " job(s); they will sit just above your engine modules");
            return spaceJobIds;
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not read the spaceship's jobs: "
                + e.Message);
            return null;
        }
    }

    private static bool IsSpaceJob(string id)
    {
        if (id == null) { return false; }
        HashSet<string> ids = SpaceJobIds();
        return ids != null && ids.Contains(id);
    }

    // A comma separated config list, parsed once and re-parsed whenever the
    // player edits it. Blank entries and stray spaces are dropped, so a list
    // typed by hand with spaces after the commas still works.
    private static HashSet<string> ParseIdList(ConfigEntry<string> entry,
        ref string cachedText, ref HashSet<string> cachedSet)
    {
        string text = (entry == null || entry.Value == null) ? "" : entry.Value;
        if (cachedSet != null && text == cachedText) { return cachedSet; }
        HashSet<string> set = new HashSet<string>();
        string[] parts = text.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            string one = parts[i].Trim();
            if (one.Length > 0) { set.Add(one); }
        }
        cachedText = text;
        cachedSet = set;
        return set;
    }

    private static string spaceTailText;
    private static HashSet<string> spaceTailSet;

    private static bool IsSpaceTailJob(string id)
    {
        if (id == null) { return false; }
        return ParseIdList(cfgSpaceTail, ref spaceTailText,
            ref spaceTailSet).Contains(id);
    }

    private static string neverStartText;
    private static HashSet<string> neverStartSet;

    // "This mod does not start this one." Nothing here ever switches a job
    // OFF - see the header.
    private static bool NeverStart(string prefixedId)
    {
        if (prefixedId == null) { return false; }
        return ParseIdList(cfgNeverStart, ref neverStartText,
            ref neverStartSet).Contains(prefixedId);
    }

    // Is this row something that produces rather than consumes?
    private static bool IsCollector(JobType type)
    {
        return type == JobType.enProd || type == JobType.reProd;
    }

    // Pull every collector to the top of the list, above every Accumulation
    // Layer, keeping their order among themselves. The mirror of the engine
    // sink, and needed for the same reason in reverse.
    //
    // Fuzzied: "its dumping Solar energy from space, Biomass power and plant
    // based biomass to the last slot, resource collection should be moved to
    // the top not down to an Accumulator". Those three are collectors that
    // happen to belong to the spaceship, so SinkSpaceJobs took them down with
    // Interplanetary Constructions and the boosts, across all four of his
    // layers, into the bottom segment where an accumulation limit stops them
    // producing. An empty limit on a drain is a fence; the same limit on a
    // supply is a tourniquet.
    //
    // It also explains why his layers looked "squished" together: with four
    // collectors dragged down past all of them every sweep, the rows that used
    // to sit between the layers were gone and the layers ended up adjacent.
    //
    // Moving a row UP across a layer is the one direction that could let
    // something run that the player had fenced off, which is why it is a
    // switch (CollectorsFirst) rather than simply the way it works. On by
    // default, because a fenced collector is almost always an accident.
    //
    // v1.14.2 narrowed what it touches. The first version gathered every
    // collector and put the lot at the very top of the list, on every sweep -
    // Fuzzied: "Cannot move the res/infra nor move the Bars, everything
    // resets". Of course it did: rewriting a whole region of the panel five
    // times a minute means nothing inside that region can be arranged by
    // hand. It now lifts ONLY the collectors that are actually below a layer,
    // and only as far as just above the FIRST layer, leaving every other row
    // exactly where it is. A collector already on the right side of the
    // fences is never touched, so the top of the list is the player's again.
    // The ids the remembered list already holds a place for. A row in here
    // has been arranged - by the player, or by these same rules on the pass it
    // first appeared - and re-deciding where it goes can only overrule that.
    // See the header.
    private HashSet<string> RememberedIds(int staying)
    {
        HashSet<string> ids = new HashSet<string>();
        try
        {
            List<string> memory = Memory(staying);
            for (int i = 0; i < memory.Count; i++)
            {
                string id = EntryId(memory[i]);
                if (id != null) { ids.Add(id); }
            }
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not read the remembered "
                + "placements: " + e.Message);
        }
        return ids;
    }

    private bool RaiseCollectorJobs(PriorityJobManager jobs,
        HashSet<string> placed)
    {
        try
        {
            if (jobs == null || jobs.priorityJobDict == null) { return false; }
            Transform content = PriorityContent(jobs);
            if (content == null) { return false; }

            int count = content.childCount;
            Transform[] before = new Transform[count];
            int firstLayer = -1;
            for (int i = 0; i < count; i++)
            {
                Transform child = content.GetChild(i);
                before[i] = child;
                if (firstLayer >= 0) { continue; }
                PriorityItem item = child.GetComponent<PriorityItem>();
                if (item != null && item.job != null
                    && item.job.type == JobType.accumulation)
                {
                    firstLayer = i;
                }
            }
            // No fences, so no collector can be behind one.
            if (firstLayer < 0) { return false; }

            List<Transform> lift = new List<Transform>();
            for (int i = firstLayer + 1; i < count; i++)
            {
                PriorityItem item = before[i].GetComponent<PriorityItem>();
                if (item == null || item.job == null) { continue; }
                // A collector the memory puts here is a collector somebody
                // put here. See the header.
                if (placed.Contains(item.job.id)) { continue; }
                if (IsCollector(item.job.type)) { lift.Add(before[i]); }
            }
            if (lift.Count == 0) { return false; }

            HashSet<Transform> lifted = new HashSet<Transform>();
            for (int i = 0; i < lift.Count; i++) { lifted.Add(lift[i]); }

            List<Transform> want = new List<Transform>();
            for (int i = 0; i < firstLayer; i++) { want.Add(before[i]); }
            want.AddRange(lift);
            for (int i = firstLayer; i < count; i++)
            {
                if (!lifted.Contains(before[i])) { want.Add(before[i]); }
            }
            if (want.Count != count) { return false; }

            for (int i = 0; i < count; i++)
            {
                want[i].SetSiblingIndex(i);
            }
            Log.LogInfo("Auto start: lifted " + lift.Count + " collector(s) "
                + "back above your first Accumulation Layer, where they can "
                + "actually supply the rows underneath");
            return true;
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not raise the collector rows: "
                + e.Message);
            return false;
        }
    }

    // Push the spaceship's own rows to the bottom, keeping their order among
    // themselves, with the money pits below the rest of them, and let them
    // cross Accumulation Layers on the way down - safe for the same reason the
    // engine sink is. Engine modules are left to SinkEngineJobs, which runs
    // straight after this and puts them below. See the header.
    private bool SinkSpaceJobs(PriorityJobManager jobs,
        HashSet<string> placed)
    {
        try
        {
            if (jobs == null || jobs.priorityJobDict == null) { return false; }
            if (SpaceJobIds() == null) { return false; }
            Transform content = PriorityContent(jobs);
            if (content == null) { return false; }

            int count = content.childCount;
            Transform[] before = new Transform[count];
            List<Transform> keep = new List<Transform>();
            List<Transform> sink = new List<Transform>();
            List<Transform> tail = new List<Transform>();
            for (int i = 0; i < count; i++)
            {
                Transform child = content.GetChild(i);
                before[i] = child;
                PriorityItem item = child.GetComponent<PriorityItem>();
                if (item == null || item.job == null
                    || item.job.type == JobType.engine
                    || item.job.type == JobType.accumulation
                    // A collector is not a money pit. Solar Energy From Space,
                    // Biomass Power and Plant-based Biomass are all space jobs
                    // and all of them PRODUCE, so sinking them with the rest of
                    // the spaceship's rows put the planet's own supply below
                    // every Accumulation Layer it had. See the header.
                    || IsCollector(item.job.type)
                    // The memory already has a place for this one, so it is
                    // not ours to decide. This is what stops a load flattening
                    // the arrangement it has just restored. See the header.
                    || placed.Contains(item.job.id)
                    || !IsSpaceJob(item.job.id))
                {
                    keep.Add(child);
                    continue;
                }
                if (IsSpaceTailJob(item.job.id)) { tail.Add(child); }
                else { sink.Add(child); }
            }
            if (sink.Count == 0 && tail.Count == 0) { return false; }

            keep.AddRange(sink);
            keep.AddRange(tail);
            bool moved = false;
            for (int i = 0; i < count; i++)
            {
                if (before[i] != keep[i]) { moved = true; break; }
            }
            if (!moved) { return false; }
            for (int i = 0; i < count; i++)
            {
                keep[i].SetSiblingIndex(i);
            }
            return true;
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not sink the spaceship rows: "
                + e.Message);
            return false;
        }
    }

    private static PriorityJob JobFor(ProductionSource source)
    {
        EnergySource energy = source as EnergySource;
        if (energy != null)
        {
            return new PriorityJob(energy);
        }
        ElementSource element = source as ElementSource;
        if (element != null)
        {
            return new PriorityJob(element);
        }
        SpeedModule speed = source as SpeedModule;
        if (speed != null)
        {
            return new PriorityJob(speed);
        }
        return null;
    }

    // ------------------------------------------------------------------
    // The spaceship's engine modules
    // ------------------------------------------------------------------
    //
    // Same shape as a collector, different home in every other respect - see
    // the header. The key it is claimed under is "engine" rather than a star
    // id, because it does not belong to one; no star can be called that, since
    // star ids come from the map data.
    // Walked whatever EngineModules says: a module the player switched on
    // still needs its priority job, and that repair is the actual fix for
    // "Launch did not activate after big bang". Only the switching-on is
    // gated - see the header for why that is off by default.
    private int StartSpeedModules(PriorityJobManager jobs)
    {
        int started = 0;
        try
        {
            // Fuzzied: "Engines are turning on when Im in flight, had to
            // manually several times turn them off." Nothing gets switched on
            // out there - see the header. The walk still happens, because the
            // repair below starts nothing and a module the player switched on
            // themselves needs its priority job wherever they did it.
            bool mayStart = cfgEngineModules.Value && !Travelling();
            SpaceshipManager ships = SpaceshipManager.shared;
            MainPanel main = MainPanel.shared;
            if (ships == null || ships.speedModuleSet == null || main == null
                || main.spaceshipPanel == null)
            {
                return 0;
            }
            Dictionary<string, int> inputs =
                main.spaceshipPanel.speedModulesInputDict;
            if (inputs == null)
            {
                return 0;
            }
            List<SpeedModuleTemplate> templates =
                EngineLoader.speedModuleTemplates;
            if (templates == null)
            {
                return 0;
            }
            for (int i = 0; i < templates.Count; i++)
            {
                SpeedModuleTemplate template = templates[i];
                if (!UnlockManager.shared.SatisfiesUnlockReq(template.unlockReq))
                {
                    continue;
                }
                // Null until the Spaceship panel has built the module, which
                // after a Big Bang is not until the launch pad is back. Not
                // claimed in that case, so the next sweep tries again.
                SpeedModule module =
                    ships.speedModuleSet.GetSpeedModule(template.id);
                if (module == null) { continue; }
                if (module.outputPercentage > 0.0)
                {
                    EnsureJob(module, jobs);
                    continue;
                }
                if (!mayStart) { continue; }
                if (TurnOnSource(ENGINE_KEY, module, template.id, inputs, jobs))
                {
                    started++;
                    if (!engineJobIds.Contains(module.prefixedId))
                    {
                        engineJobIds.Add(module.prefixedId);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not switch on the engine "
                + "modules: " + e.Message);
        }
        return started;
    }

    // Write down what every engine module is set to right now. Called from
    // the end of an ordinary pass, and from the StopAllSpeedModules prefix
    // with wiping = true, which is the last instant before the game zeroes
    // them all. See the header.
    internal void RememberEngineChoice(bool wiping)
    {
        try
        {
            // Armed means the dials on screen are the game's zeros rather than
            // anybody's choice, so there is nothing here worth writing down.
            // See the header.
            if (engineWiped) { return; }
            SpaceshipManager ships = SpaceshipManager.shared;
            if (ships == null || ships.speedModuleSet == null) { return; }
            List<SpeedModuleTemplate> templates =
                EngineLoader.speedModuleTemplates;
            if (templates == null) { return; }
            for (int i = 0; i < templates.Count; i++)
            {
                SpeedModule module =
                    ships.speedModuleSet.GetSpeedModule(templates[i].id);
                if (module == null) { continue; }
                engineChoice[templates[i].id] = PercentOf(module);
            }
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not read the engine settings: "
                + e.Message);
        }
        finally
        {
            if (wiping)
            {
                engineWiped = true;
                engineRestoreTries = 0;
            }
        }
    }

    // The dial as a whole number. The view is the honest source while it
    // exists, since that is what the player clicked; outputPercentage is the
    // model behind it and is all that is left once a hard reset has destroyed
    // the row.
    private static int PercentOf(SpeedModule module)
    {
        FlexProgressItem view = module.view;
        if (view != null)
        {
            return view.outputPercentageValue;
        }
        return (int)Math.Round(module.outputPercentage * 100.0);
    }

    // Put the modules back the way the player had them, once, after the game
    // has wiped them on arrival. Returns how many were switched back on.
    //
    // The flag is cleared when every module that should be on actually is, so
    // a pass that runs before LoadSpeedModules has rebuilt the rows does not
    // count as the restore and the next sweep tries again - or after
    // ENGINE_RESTORE_TRIES passes, so a module that is never coming back
    // cannot stop the recording for the rest of the session.
    private int RestoreEngineChoice(PriorityJobManager jobs)
    {
        if (!engineWiped) { return 0; }
        // Setting off wipes the dials as well, and that one is the game being
        // right: filling the tank in flight is not what the engine is for.
        // See the header.
        if (Travelling()) { return 0; }
        engineRestoreTries++;
        int restored = 0;
        bool complete = true;
        try
        {
            SpaceshipManager ships = SpaceshipManager.shared;
            MainPanel main = MainPanel.shared;
            if (ships == null || ships.speedModuleSet == null || main == null
                || main.spaceshipPanel == null)
            {
                return 0;
            }
            Dictionary<string, int> inputs =
                main.spaceshipPanel.speedModulesInputDict;
            foreach (KeyValuePair<string, int> pair in engineChoice)
            {
                if (pair.Value <= 0) { continue; }
                SpeedModule module =
                    ships.speedModuleSet.GetSpeedModule(pair.Key);
                if (module == null)
                {
                    complete = false; // not rebuilt yet; try again next sweep
                    continue;
                }
                if (PercentOf(module) >= pair.Value) { continue; }
                FlexProgressItem view = module.view;
                if (view == null)
                {
                    complete = false;
                    continue;
                }
                // Same call the player's own click makes: model, dial and
                // button colours together.
                view.SwitchTo(pair.Value);
                if (inputs != null)
                {
                    inputs[pair.Key] = pair.Value;
                }
                // RemoveAllTasks emptied the list, and SwitchTo only marks the
                // row as pending for SpaceshipManager's next FixedUpdate.
                EnsureJob(module, jobs);
                restored++;
            }
        }
        catch (Exception e)
        {
            complete = false;
            Log.LogWarning("Auto start could not switch the engine modules "
                + "back on: " + e.Message);
        }
        if (complete || engineRestoreTries >= ENGINE_RESTORE_TRIES)
        {
            engineWiped = false;
            engineRestoreTries = 0;
        }
        if (restored > 0)
        {
            Log.LogInfo("Auto start: put " + restored + " engine module(s) "
                + "back on after the arrival wipe");
        }
        return restored;
    }

    // Deliberately fails to "standing still": if the player object is not
    // there to ask, the worst that follows is a restore that had no dials to
    // put back.
    private static bool Travelling()
    {
        try
        {
            Player player = Player.shared;
            return player != null && player.location != null
                && player.location.isTravelling;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Dictionary<string, EnergySourceTemplate> EnergyTemplatesOf(Star star)
    {
        if (StarMap.energySourceTemplateDict == null || star.id == null
            || !StarMap.energySourceTemplateDict.ContainsKey(star.id))
        {
            return null;
        }
        return StarMap.energySourceTemplateDict[star.id];
    }

    private static Dictionary<string, ElementSourceTemplate> ElementTemplatesOf(Star star)
    {
        if (StarMap.elementSourceTemplateDict == null || star.id == null
            || !StarMap.elementSourceTemplateDict.ContainsKey(star.id))
        {
            return null;
        }
        return StarMap.elementSourceTemplateDict[star.id];
    }

    // "Have I seen this one running since the last arrival, departure or
    // Big Bang?" A no means somebody turned it off afterwards, and that
    // somebody is the player. See the header.
    //
    // Seen running, not switched on by us. Those came apart badly: a save
    // loads with its collectors already going, so claiming only the ones we
    // switched on ourselves left the common case unclaimed and the sweep
    // undoing the player's own clicks every five seconds.
    private bool Claim(string owner, string id)
    {
        string key = owner + "/" + id;
        if (alreadyStarted.Contains(key))
        {
            return false;
        }
        alreadyStarted.Add(key);
        return true;
    }

    // True while the Big Bang upgrade screen is open with a basket that has
    // not been confirmed. Deliberately fails open: if the manager is not there
    // yet there is no basket to be in the middle of.
    private static bool InUpgradeMode()
    {
        try
        {
            BigBangManager bigBang = BigBangManager.shared;
            return bigBang != null && bigBang.isInUpgradeMode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private int ResolveLevel()
    {
        if (cfgLevel.Value >= 0)
        {
            return Math.Min(MAX_LEVEL, cfgLevel.Value);
        }
        BigBangManager bigBang = BigBangManager.shared;
        if (bigBang == null || bigBang.upgradeLevels == null)
        {
            return 0;
        }
        if (BigBangLibrary.upgrades != null
            && !BigBangLibrary.upgrades.ContainsKey(UPGRADE_ID))
        {
            if (!warnedMissingUpgrade)
            {
                warnedMissingUpgrade = true;
                Log.LogWarning("Auto start: the '" + UPGRADE_ID + "' Big Bang "
                    + "upgrade is not in this game's data, so there is nothing "
                    + "to buy and this plugin will stay idle. Install the "
                    + "'more Big Bang upgrades' data patch, or set Level in "
                    + "BepInEx\\config\\sti.community.autostart.cfg to 1-4.");
            }
            return 0;
        }
        return Math.Min(MAX_LEVEL, bigBang.GetUpgradeLevel(UPGRADE_ID));
    }

    // ------------------------------------------------------------------
    // Picking what to start
    // ------------------------------------------------------------------

    private class Candidate
    {
        // Which star it belongs to - one pass can now cover the planet you are
        // standing on and the spaceship at the same time.
        public Star star;
        public Research research;
        public Infra infra;
        public string prefixedId;
        public string name;
        public int unlockScore;
        public bool hasUpkeep;
        public ScientificNotation upkeepPerSecond;
        public ScientificNotation buildCost;
    }

    // Returns null while the star's research / infrastructure data is not
    // loaded yet, so the caller knows to come back rather than concluding
    // there is nothing to do.
    private List<Candidate> Gather(Star star, int starIndex, bool scoreUnlocks)
    {
        ResearchSet researchSet = StarMap.GetResearchSet(starIndex);
        InfraSet infraSet = StarMap.GetInfraSet(starIndex);
        if (researchSet == null || infraSet == null)
        {
            return null;
        }
        Dictionary<string, ResearchTemplate> researchTemplates =
            ResearchTemplatesOf(star);
        Dictionary<string, InfraTemplate> infraTemplates = InfraTemplatesOf(star);
        if (researchTemplates == null || infraTemplates == null)
        {
            return null;
        }
        if (researchSet.IsEmpty() && infraSet.IsEmpty()
            && (researchTemplates.Count > 0 || infraTemplates.Count > 0))
        {
            return null; // the sets have not been filled in for this star yet
        }

        List<Candidate> list = new List<Candidate>();

        List<Research> researches = researchSet.researches;
        for (int i = 0; i < researches.Count; i++)
        {
            Research research = researches[i];
            if (research == null || research.isResearching || research.IsMaxLevel()
                || research.targetLevel <= research.level)
            {
                continue;
            }
            if (!researchTemplates.ContainsKey(research.id))
            {
                continue;
            }
            ResearchTemplate template = researchTemplates[research.id];
            if (!UnlockManager.shared.SatisfiesUnlockReq(template.unlockReq))
            {
                continue;
            }
            if (NeverStart(research.prefixedId))
            {
                continue; // see the header - BLACKHOLE and friends
            }
            Candidate candidate = new Candidate();
            candidate.star = star;
            candidate.research = research;
            candidate.prefixedId = research.prefixedId;
            candidate.name = research.id;
            candidate.hasUpkeep = false;
            candidate.upkeepPerSecond = ScientificNotation.zero;
            candidate.buildCost = research.researchEnergyCost;
            candidate.unlockScore = scoreUnlocks
                ? UnlockScore(star, research.id, null) : 0;
            list.Add(candidate);
        }

        List<Infra> infras = infraSet.infras;
        for (int i = 0; i < infras.Count; i++)
        {
            Infra infra = infras[i];
            if (infra == null || infra.isBuilding || infra.IsMaxLevel()
                || infra.targetLevel <= infra.level)
            {
                continue;
            }
            if (!infra.isOn)
            {
                continue; // the player switched this building off; leave it off
            }
            if (!infraTemplates.ContainsKey(infra.id))
            {
                continue;
            }
            InfraTemplate template = infraTemplates[infra.id];
            if (!UnlockManager.shared.SatisfiesUnlockReq(template.unlockReq))
            {
                continue;
            }
            if (NeverStart(infra.prefixedId))
            {
                continue; // see the header
            }
            Candidate candidate = new Candidate();
            candidate.star = star;
            candidate.infra = infra;
            candidate.prefixedId = infra.prefixedId;
            candidate.name = infra.id;
            // One more level of this building costs this much energy per
            // second to keep running, on top of what it costs today. Infra.
            // tickEnergyCost is per TICK and reports zero while the building
            // is off, so the per-level figure from the template is the honest
            // one to plan with.
            candidate.hasUpkeep = !infra.noCost;
            candidate.upkeepPerSecond = template.tickEnergyCostPerLevel
                * Modifiers.infraCostReduction * (double)Utils.SEC_TICK_COUNT;
            candidate.buildCost = infra.buildEnergyCost;
            candidate.unlockScore = scoreUnlocks
                ? UnlockScore(star, null, infra.id) : 0;
            list.Add(candidate);
        }
        return list;
    }

    private static Dictionary<string, ResearchTemplate> ResearchTemplatesOf(Star star)
    {
        if (StarMap.researchTemplateDict == null || star.id == null
            || !StarMap.researchTemplateDict.ContainsKey(star.id))
        {
            return null;
        }
        return StarMap.researchTemplateDict[star.id];
    }

    private static Dictionary<string, InfraTemplate> InfraTemplatesOf(Star star)
    {
        if (StarMap.infraTemplateDict == null || star.id == null
            || !StarMap.infraTemplateDict.ContainsKey(star.id))
        {
            return null;
        }
        return StarMap.infraTemplateDict[star.id];
    }

    // ------------------------------------------------------------------
    // Level 3: how much is still locked behind this one thing
    // ------------------------------------------------------------------
    //
    // Fuzzied's rule of thumb for what should go first is "the ones that unlock
    // new resources". Every template in the game carries the UnlockReq that
    // gates it, so that question is answerable directly: count the things on
    // this planet that are still locked AND name this research / building in
    // their requirements. Resource and energy sources count double, because a
    // new resource opens up everything downstream of it.
    private static int UnlockScore(Star star, string researchId, string infraId)
    {
        int score = 0;
        Dictionary<string, ElementSourceTemplate> elements = null;
        Dictionary<string, EnergySourceTemplate> energies = null;
        if (StarMap.elementSourceTemplateDict != null && star.id != null
            && StarMap.elementSourceTemplateDict.ContainsKey(star.id))
        {
            elements = StarMap.elementSourceTemplateDict[star.id];
        }
        if (StarMap.energySourceTemplateDict != null && star.id != null
            && StarMap.energySourceTemplateDict.ContainsKey(star.id))
        {
            energies = StarMap.energySourceTemplateDict[star.id];
        }
        if (elements != null)
        {
            foreach (ElementSourceTemplate template in elements.Values)
            {
                if (Gates(template.unlockReq, researchId, infraId))
                {
                    score += 4;
                }
            }
        }
        if (energies != null)
        {
            foreach (EnergySourceTemplate template in energies.Values)
            {
                if (Gates(template.unlockReq, researchId, infraId))
                {
                    score += 4;
                }
            }
        }
        Dictionary<string, ResearchTemplate> researches = ResearchTemplatesOf(star);
        if (researches != null)
        {
            foreach (ResearchTemplate template in researches.Values)
            {
                if (Gates(template.unlockReq, researchId, infraId))
                {
                    score += 2;
                }
            }
        }
        Dictionary<string, InfraTemplate> infras = InfraTemplatesOf(star);
        if (infras != null)
        {
            foreach (InfraTemplate template in infras.Values)
            {
                if (Gates(template.unlockReq, researchId, infraId))
                {
                    score += 2;
                }
            }
        }
        return score;
    }

    // True when this requirement names the given research / building AND is
    // not satisfied yet - something that is already unlocked is not waiting on
    // anybody.
    private static bool Gates(UnlockReq req, string researchId, string infraId)
    {
        if (req == null)
        {
            return false;
        }
        bool mentioned = false;
        if (researchId != null && req.researchLevels != null)
        {
            for (int i = 0; i < req.researchLevels.Count; i++)
            {
                if (req.researchLevels[i] != null
                    && req.researchLevels[i].id == researchId)
                {
                    mentioned = true;
                    break;
                }
            }
        }
        if (!mentioned && infraId != null && req.infraLevels != null)
        {
            for (int i = 0; i < req.infraLevels.Count; i++)
            {
                if (req.infraLevels[i] != null
                    && req.infraLevels[i].id == infraId)
                {
                    mentioned = true;
                    break;
                }
            }
        }
        if (!mentioned)
        {
            return false;
        }
        return !UnlockManager.shared.SatisfiesUnlockReq(req);
    }

    private static int CompareCandidates(Candidate a, Candidate b)
    {
        // Most still-locked content behind it first.
        if (a.unlockScore != b.unlockScore)
        {
            return b.unlockScore.CompareTo(a.unlockScore);
        }
        // Then the free ones - research never costs upkeep, and neither do
        // some buildings, so those are pure gain.
        if (a.hasUpkeep != b.hasUpkeep)
        {
            return a.hasUpkeep ? 1 : -1;
        }
        // Then whatever finishes soonest, so its unlocks land sooner.
        int byCost = a.buildCost.CompareTo(b.buildCost);
        if (byCost != 0)
        {
            return byCost;
        }
        return string.CompareOrdinal(a.prefixedId, b.prefixedId);
    }

    // ------------------------------------------------------------------
    // Level 2: do not start what you cannot afford to run
    // ------------------------------------------------------------------
    //
    // The running cost of a building is charged forever once it is built, so
    // this is the one way arriving somewhere can make you worse off. Headroom
    // is what your energy production makes per second minus what your existing
    // buildings already drain, both of which the game measures for us.
    //
    // When there is no measurement to go on - offline simulation freezes these
    // counters, and a planet you have only just landed on is not producing
    // anything yet - this steps aside and behaves exactly like level 1, so
    // level 2 is never worse than the level below it.
    private List<Candidate> WithinEnergyBudget(List<Candidate> candidates,
        bool announce)
    {
        StorageManager storage = StorageManager.shared;
        if (storage == null || storage.summary == null)
        {
            return candidates;
        }
        StorageElementSummary energy = storage.summary.GetEnergySummary();
        if (energy == null)
        {
            return candidates;
        }
        // averageInfraCostGain is already negative - it is recorded as a loss.
        ScientificNotation headroom = energy.averageResourceProductionGain
            + energy.averageInfraCostGain;
        double headroomPerSecond = headroom.Standard();
        if (headroomPerSecond <= 0.0)
        {
            return candidates; // nothing measured, or already in deficit
        }
        double margin = cfgEnergyMargin.Value;
        if (margin < 0.0)
        {
            margin = 0.0;
        }
        if (margin > 0.9)
        {
            margin = 0.9;
        }
        double budget = headroomPerSecond * (1.0 - margin);

        List<Candidate> affordable = new List<Candidate>();
        int skipped = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            Candidate candidate = candidates[i];
            if (!candidate.hasUpkeep)
            {
                affordable.Add(candidate);
                continue;
            }
            double cost = candidate.upkeepPerSecond.Standard();
            if (cost > budget)
            {
                skipped++;
                continue;
            }
            budget -= cost;
            affordable.Add(candidate);
        }
        if (skipped > 0 && announce)
        {
            Log.LogInfo("Auto start: left " + skipped + " building(s) alone, "
                + "their running cost does not fit the energy budget");
        }
        return affordable;
    }

    // ------------------------------------------------------------------
    // Starting one job
    // ------------------------------------------------------------------

    // Deliberately NOT called Start. Unity reserves that name on a
    // MonoBehaviour for its own lifecycle callback and logs
    // "Start() can not take parameters" for any overload that takes
    // arguments, which puts a scary looking error in every player's log.
    private bool StartJob(Candidate candidate, PriorityJobManager jobs)
    {
        Star star = candidate.star;
        if (star == null || star.controlSettings == null)
        {
            return false;
        }
        try
        {
            // The sweep runs forever, so it has to leave alone anything it has
            // already started once. A burst starts from a cleared list, so it
            // is unaffected. See the header.
            if (!Claim(star.id, candidate.prefixedId))
            {
                return false;
            }
            if (candidate.research != null)
            {
                Research research = candidate.research;
                research.isResearching = true;
                ResearchItem view = research.view;
                if (view != null)
                {
                    view.isResearching = true;
                    view.pendingStateUpdate = true;
                    view.UpdateControlState();
                }
                star.controlSettings.researchIdResearchingDict[research.id] = true;
                Insert(jobs, research.prefixedId, new PriorityJob(research));
                return true;
            }
            if (candidate.infra != null)
            {
                Infra infra = candidate.infra;
                infra.isBuilding = true;
                InfraItem view = infra.view;
                if (view != null)
                {
                    view.isBuilding = true;
                    view.pendingStateUpdate = true;
                    view.UpdateBuildControlState();
                }
                star.controlSettings.infraIdBuildingDict[infra.id] = true;
                Insert(jobs, infra.prefixedId, new PriorityJob(infra));
                return true;
            }
        }
        catch (Exception e)
        {
            Log.LogWarning("Auto start could not start " + candidate.name
                + ": " + e.Message);
        }
        return false;
    }
}

// SidePanel.Arrive is the game's single "you have landed" moment: it collects
// the arrival flag, tells the engine, and fires locationChangeEvent so every
// panel rebuilds for the new star. It runs for an ordinary arrival and for one
// that happened while the game was closed, which is the case this feature
// exists for.
//
// This runs the pass NOW as well as arming the delayed one, for the same
// reason departure does. OfflineManager.RunSimulation calls
// SidePanel.CheckArrivalStatus every simulated tick, so an arrival that
// happens mid catch-up fires here while the simulation is still going. The
// delayed burst is driven by Time.unscaledTime, which is real seconds, so it
// cannot land inside a simulation that covers hours: by the time it fires,
// the whole absence has been simulated with the destination's jobs switched
// off. Fuzzied's 16.78h absence spent its first 28 minutes flying and the
// remaining 16.3h parked at Saturn with nothing running, which is why the
// engines came back empty.
[HarmonyPatch(typeof(SidePanel), "Arrive")]
public static class SidePanelArrivePatch
{
    private static void Postfix()
    {
        AutoStartPlugin.ScheduleAndRunNow("the arrival");
    }
}

// The catch-up itself. Whatever happened during it, the burst that matters to
// the player is the one that runs once they are actually looking at the game,
// so the arrival burst is armed again here from scratch.
[HarmonyPatch(typeof(OfflineManager), "FinishSimulation")]
public static class OfflineFinishSimulationPatch
{
    private static void Postfix()
    {
        AutoStartPlugin.ScheduleSafely("the end of the offline catch-up");
    }
}

// Departing is boarding the spaceship, which is a star like any other with
// its own research, infrastructure and collectors - and the flight is the
// longest idle stretch in the game, which is the whole reason this feature
// exists. Departure returns false when the trip was refused, so the result is
// checked rather than assumed.
[HarmonyPatch(typeof(Location), "Departure")]
public static class LocationDeparturePatch
{
    private static void Postfix(bool __result)
    {
        if (!__result)
        {
            return;
        }
        AutoStartPlugin.ScheduleAndRunNow("the departure");
    }
}

// A Big Bang wipes the run and puts you back on Earth with everything off, and
// never goes anywhere near SidePanel.Arrive.
//
// Both ends of it are hooked on purpose. BigBang is the reset itself, and
// covers every run after the first. ConfirmUpgrades is the moment upgrade mode
// ends, and it is the only one that covers the reset on which 'Automated
// Arrival' was BOUGHT: dark matter is spent after the reset, so at BigBang
// time the upgrade level still reads 0 and this feature correctly does
// nothing. By ConfirmUpgrades the level is current - BigBangUpgradeItem tells
// the manager at click time, well before the Confirm button.
//
// Scheduling twice over is harmless: a pass never starts something that is
// already running, and the second Schedule just replaces the first one's
// timer.
[HarmonyPatch(typeof(BigBangManager), "BigBang")]
public static class BigBangResetPatch
{
    // Everything the reset is about to destroy is still here. See the header.
    private static void Prefix()
    {
        AutoStartPlugin.RememberPriorityLists();
    }

    private static void Postfix()
    {
        AutoStartPlugin.RestorePriorityLists();
        AutoStartPlugin.ScheduleSafely("the Big Bang");
    }
}

[HarmonyPatch(typeof(BigBangManager), "ConfirmUpgrades")]
public static class BigBangConfirmPatch
{
    private static void Postfix()
    {
        // Again, because level 4 may be exactly what was just bought. It only
        // fills a list that is still empty, so this cannot undo the first go.
        AutoStartPlugin.RestorePriorityLists();
        AutoStartPlugin.ScheduleSafely("the Big Bang upgrades");
    }
}

[HarmonyPatch(typeof(SaveLoadManager), "LoadSave")]
public static class SaveLoadForgetPatch
{
    private static void Prefix()
    {
        AutoStartPlugin.ForgetPriorityLists();
    }
}

// A save writes the live rows over the star's list, which would drop every
// row that has not come back from the Big Bang yet. Folding the memory back
// in here is what lets the rebuild survive closing the game.
[HarmonyPatch(typeof(PriorityJobManager), "ExportPriorityIdList")]
public static class PriorityExportPatch
{
    // The export is the only moment a layer id can enter a star's saved
    // list, so this is where a foreign layer has to be stopped. See the
    // header.
    private static void Prefix(PriorityJobManager __instance)
    {
        // Before anything else: this is the last instant the star's own list
        // is still what the save put there. See the v1.20.9 header.
        AutoStartPlugin.SnapshotSavedList();
        AutoStartPlugin.EvictForeignLayers(__instance);
    }

    private static void Postfix()
    {
        AutoStartPlugin.MergeExportedList();
    }
}

// Arriving somewhere reloads the job rows but not the layers, so the ones
// belonging to the star you just left are still on the panel. Clearing them
// here means the list is right on screen straight away rather than at the
// next save. See the header.
[HarmonyPatch(typeof(PriorityJobManager), "LoadPriority")]
public static class PriorityLoadLayerPatch
{
    private static void Prefix(PriorityJobManager __instance)
    {
        AutoStartPlugin.EvictForeignLayers(__instance);
    }

    // The game has just applied the saved list using each entry's position in
    // that list as a position on the panel, which is only ever right when the
    // whole list has been drawn. See the v1.22.0 header.
    private static void Postfix(PriorityJobManager __instance)
    {
        AutoStartPlugin.ReapplySavedOrder(__instance);
    }
}

// A layer the player builds by hand belongs to the star they built it on.
// This is the parameterless overload, which is the Add Accumulation Layer
// button; the other one is the game rebuilding a layer it already knows.
[HarmonyPatch(typeof(PriorityJobManager), "AddAccumulationLayer", new Type[] { })]
public static class LayerCreatedPatch
{
    private static void Postfix(PriorityJobManager __instance)
    {
        AutoStartPlugin.AdoptNewLayers(__instance);
    }
}

// The record beside the save. Both of these take the full path of the save
// file, which is what makes the record per slot; see the header. The load one
// is a postfix so it runs after LoadSave has cleared the previous game's.
[HarmonyPatch(typeof(SaveLoadManager), "SaveGameToPath")]
public static class LedgerWritePatch
{
    private static void Postfix(string path)
    {
        AutoStartPlugin.WriteLedger(path);
    }
}

[HarmonyPatch(typeof(SaveLoadManager), "LoadGameFromPath")]
public static class LedgerReadPatch
{
    private static void Postfix(string path)
    {
        AutoStartPlugin.ReadLedger(path);
    }
}

// OnStarArrival, wired in level1, calls this to switch every engine module
// to 0 - and the rebuild behind it overwrites speedModulesInputDict with those
// zeros, so the player's choice is forgotten as well as switched off. This is
// the last instant at which it can be read. See the header.
[HarmonyPatch(typeof(SpaceshipPanel), "StopAllSpeedModules")]
public static class EngineStopPatch
{
    private static void Prefix()
    {
        if (AutoStartPlugin.shared == null) { return; }
        AutoStartPlugin.shared.RememberEngineChoice(true);
    }
}

// Deleting a layer is the only honest signal that it is meant to be gone; a
// Big Bang destroys the row too, and that one has to be survivable.
[HarmonyPatch(typeof(PriorityAccumulationItem), "Delete")]
public static class LayerDeletePatch
{
    private static void Prefix(PriorityAccumulationItem __instance)
    {
        AutoStartPlugin.ForgetLayer(__instance);
        if (__instance != null && __instance.job != null)
        {
            AutoStartPlugin.ForgetLayerHome(__instance.job.id);
        }
    }
}

// One toggle per level of the upgrade, in the Settings panel.
//
// The panel is a prefab, so a row has to be cloned from one that already
// exists. SettingsManager.hideCardControlUIToggle is the one to copy: it is a
// plain label-plus-checkbox row, unlike the fullscreen one which the game
// drives from Screen.fullScreen.
//
// Hooking LoadSystemSettings rather than Awake is deliberate. That method
// early-returns when the toggles are not wired up, so by the time our Postfix
// runs there is definitely a real row to clone.
[HarmonyPatch(typeof(SettingsManager), "LoadSystemSettings")]
public static class AutoStartSettingsPatch
{
    private const string CLONE_PREFIX = "CommunityAutoStartToggle";
    private const string SECTION_NAME = "CommunityAutoStartContainer";

    // The rows scroll inside the section, placed and sized by
    // CommunitySettingsFit in CommunitySettings.cs, the same as the
    // community mod list beside it. They used to squash to fit the column
    // and ran under the Discord icon, the version number and "Login Failed"
    // on a short window, and past the bottom of the panel on a shorter one.
    private const string SCROLL_NAME = "CommunityAutoStartScroll";
    private const string ROWS_NAME = "CommunityAutoStartRows";

    // The one thing in the left hand column we can name. Everything else
    // about the panel is found by looking, so a reshuffle upstream costs us
    // nothing but the nicer position.
    private const string GUIDE_CONTAINER = "GuideProgressContainer";

    private static readonly FieldInfo HideCardUiToggleField =
        AccessTools.Field(typeof(SettingsManager), "hideCardControlUIToggle");

    // Short, because they sit under a heading that already says "Auto start"
    // and the column they sit in is 200 units wide. See the header.
    private static readonly string[] Labels =
    {
        "On arrival",
        "Watch energy",
        "Unlocks first",
        "Keep my layers",
        "Engine last",
        "Space last",
        "Engine stays on",
        "Fill the tank",
        "Collectors first"
    };

    // One per row, shown on hover. Fuzzied: "Engine stays on f example is not
    // self explanatory." They say what the setting does, in the order a
    // player would ask it: what happens, then the catch.
    private static readonly string[] Tooltips =
    {
        "Landing somewhere switches everything you own there back on for "
        + "you: research, buildings and collectors. It happens when you set "
        + "off as well, and after a Big Bang.",

        "Only starts a building when its running cost fits the energy you "
        + "have spare, so starting things does not starve what is already "
        + "running.",

        "Starts whatever unlocks the most first, and keeps your priority "
        + "list in the order that feeds down: energy at the top, then "
        + "resources, then research and buildings.",

        "A Big Bang throws your priority list away. This carries the whole "
        + "arrangement across and puts every row back where you had it, "
        + "Accumulation Layers and their limits included.",

        "Keeps your engine modules at the very bottom of the priority list, "
        + "so the tank only drinks what everything else has finished with.",

        "Keeps the spaceship's own research, buildings and collectors at the "
        + "bottom of the list, just above your engine modules, with the very "
        + "long ones below the rest of them. The spaceship is the one place "
        + "you never leave, so its jobs would otherwise take energy and "
        + "resources off the planet you are trying to finish.",

        "Landing switches your engine modules off and forgets what you had "
        + "them set to. That is the base game, not this mod. This puts them "
        + "back at the percentage you chose. One you left at 0 stays off.",

        "Switches your engine modules on by itself when you land, so the "
        + "fuel bar fills without you asking. A filling tank takes every "
        + "joule it can reach until it is full, which can be hours.",

        "Keeps your collectors above every Accumulation Layer, so the rows "
        + "that FEED the planet are never fenced off by a limit meant for "
        + "the rows that drain it. Turn it off if you want a collector "
        + "sitting under a layer on purpose."
    };

    // Our own section under Tutorial Progress, null when the panel did not
    // look the way we expected and the rows went in beside the template
    // instead. Everything that resizes rows checks this first.
    private static GameObject section;

    // How much bigger than the game's own buttons these rows are.
    //
    // Four attempts at this all sized the rows to match a button in the
    // Tutorial Progress block, and Fuzzied said the text was too small every
    // single time - "Toggle text size is still small, are you registering
    // this? I've said it many times now." The log from the last of them says
    // the rows came out 40 tall with a font ceiling of 20, which IS a button,
    // exactly. So a button is not the target to hit. It is the thing that is
    // too small.
    //
    // The floor stays at the vanilla size and only the ceiling moves, so a
    // label short enough to be drawn twice as big is drawn twice as big and a
    // label too long for that is drawn at the size it would have had anyway.
    // Nothing can come out smaller than it used to.
    private const float ROW_SCALE = 2f;

    // Measured off the Tutorial Progress block, so the rows keep their
    // relationship to the game's own if the game ever changes them.
    // templateRowHeight and fontCeiling are already scaled up; fontFloor is
    // the vanilla size, which is where auto sizing may give up and no further.
    private static float templateRowHeight;
    private static float templateFontSize;
    private static float fontCeiling;
    private static float fontFloor;
    private static float headingHeight;
    private static float appliedRowHeight;

    // The button we measured, kept so the one-off line in the log can compare
    // what our rows actually came out as against what it actually came out
    // as. Pixel counting a screenshot is guesswork; this is not.
    private static RectTransform measuredButton;
    private static bool loggedShape;

    // Held so RefreshVisibility can show and hide them as the upgrade is
    // bought. Sized from Labels, so adding a row means touching one array,
    // ConfigFor and LevelFor, and nothing else. Rows 0..3 are levels 1..4;
    // row 4 is a second row for level 3, rows 5 and 6 more rows for level 1 -
    // see LevelFor.
    private static readonly GameObject[] rows =
        new GameObject[Labels.Length];

    public static void Postfix(SettingsManager __instance)
    {
        try
        {
            if (HideCardUiToggleField == null)
            {
                return;
            }
            Toggle template = (Toggle)HideCardUiToggleField.GetValue(__instance);
            if (template == null || template.transform.parent == null)
            {
                return;
            }
            Transform parent = template.transform.parent;

            // LoadSystemSettings can run again, so build this once only - and
            // look in both places, because the fallback below still puts the
            // rows beside the template.
            if (parent.Find(CLONE_PREFIX + "1") != null)
            {
                return;
            }
            Transform guide = FindGuideContainer(parent);
            if (guide != null && guide.parent != null
                && guide.parent.Find(SECTION_NAME) != null)
            {
                return;
            }

            // Our own section under Tutorial Progress when the panel looks
            // the way we expect, and next to the template when it does not.
            // A row in the wrong column still works; no rows at all does not.
            Transform host = parent;
            section = null;
            if (guide != null)
            {
                MeasureFrom(guide);
                GameObject built = MakeSection(guide);
                if (built != null)
                {
                    section = built;
                    host = built.transform;
                    // Rows straight into the section if the list cannot be
                    // built: squashed rows still work, missing ones do not.
                    try
                    {
                        Transform list = CommunitySettings.ScrollList(
                            built.transform, SCROLL_NAME, ROWS_NAME,
                            CommunitySettingsFit.AUTO_START, templateRowHeight,
                            fontCeiling, fontFloor, false, AutoStartPlugin.Log);
                        if (list != null) { host = list; }
                    }
                    catch (Exception e)
                    {
                        AutoStartPlugin.Log.LogWarning(
                            "Could not make the auto start list scroll: "
                            + e.Message);
                    }
                }
            }

            bool laidOutForUs = host.GetComponent<LayoutGroup>() != null;
            int baseIndex = section == null
                ? template.transform.GetSiblingIndex() + 1
                : host.childCount;

            for (int i = 0; i < Labels.Length; i++)
            {
                GameObject clone = UnityEngine.Object.Instantiate(
                    template.gameObject, host);
                clone.name = CLONE_PREFIX + (i + 1);
                clone.transform.SetSiblingIndex(baseIndex + i);

                // A settings list is usually laid out by a LayoutGroup, which
                // overrides anchoredPosition every frame; only position the
                // clone by hand when nothing else is going to.
                if (!laidOutForUs)
                {
                    RectTransform src = template.GetComponent<RectTransform>();
                    RectTransform rt = clone.GetComponent<RectTransform>();
                    rt.anchorMin = src.anchorMin;
                    rt.anchorMax = src.anchorMax;
                    rt.pivot = src.pivot;
                    rt.sizeDelta = src.sizeDelta;
                    rt.anchoredPosition = src.anchoredPosition
                        - new Vector2(0f, (src.rect.height + 6f) * (i + 1));
                }

                Toggle toggle = clone.GetComponent<Toggle>();
                if (toggle == null)
                {
                    UnityEngine.Object.Destroy(clone);
                    continue;
                }

                // ORDER MATTERS. Object.Instantiate copies the serialized
                // listener, so writing isOn first would fire the TEMPLATE's
                // callback, SettingsManager.SetCardHideControlUIs, and quietly
                // switch the player's card control UIs to hover-only. Drop the
                // copied listeners, set the state while nothing is wired, then
                // add ours. DevConsoleOff v1.0.0 shipped this the wrong way
                // round once already.
                toggle.onValueChanged = new Toggle.ToggleEvent();
                toggle.isOn = ConfigFor(i).Value;
                int which = i; // captured per iteration, not by reference
                toggle.onValueChanged.AddListener(
                    delegate(bool isOn) { OnChanged(which, isOn); });

                LangText label = clone.GetComponentInChildren<LangText>(true);
                if (label != null)
                {
                    label.SetLocalisedText(Labels[i]);
                }

                StyleRow(clone, section == null ? 0f : templateRowHeight,
                    fontCeiling, fontFloor);

                // The game's own tooltip, with nothing to do but hold a
                // string: TooltipComposite reads defaultTooltip whenever the
                // object has no ITooltipAvailable on it. See the header.
                TooltipComposite tip = clone.GetComponent<TooltipComposite>();
                if (tip == null)
                {
                    tip = clone.AddComponent<TooltipComposite>();
                }
                tip.defaultTooltip = Tooltips[i];

                rows[i] = clone;
            }

            RefreshVisibility();
            AutoStartPlugin.Log.LogInfo(
                "Settings: added the auto start toggles");
        }
        catch (Exception e)
        {
            // A throw here would repeat on every settings load, so it is
            // swallowed deliberately. Worst case the rows are missing and the
            // config file is still the way to change these.
            AutoStartPlugin.Log.LogWarning(
                "Could not add the auto start toggles: " + e.Message);
        }
    }

    // The Tutorial Progress block, found by name anywhere under the panel's
    // container - two levels up from the toggle we clone. Null if the panel
    // is not laid out the way this was written against, which is a reason to
    // fall back rather than a reason to throw.
    private static Transform FindGuideContainer(Transform parent)
    {
        try
        {
            Transform container = parent.parent == null
                ? null : parent.parent.parent;
            if (container == null) { return null; }
            Transform[] all = container.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].name == GUIDE_CONTAINER) { return all[i]; }
            }
        }
        catch (Exception)
        {
        }
        return null;
    }

    // How tall a button in that block is, and how big its text is. Measured
    // rather than written down, so "the same size as the buttons" survives
    // the game changing the buttons.
    private static void MeasureFrom(Transform guide)
    {
        templateRowHeight = 40f;
        templateFontSize = 0f;
        headingHeight = 30f;
        for (int i = 0; i < guide.childCount; i++)
        {
            Transform child = guide.GetChild(i);
            RectTransform rt = child as RectTransform;
            if (child.GetComponent<Button>() != null)
            {
                if (rt != null && rt.sizeDelta.y > 0f)
                {
                    templateRowHeight = rt.sizeDelta.y;
                    measuredButton = rt;
                }
                TMP_Text text = child.GetComponentInChildren<TMP_Text>(true);
                if (text != null)
                {
                    // An auto sizing label's fontSize is whatever it last drew
                    // at, which before the panel has ever been laid out is not
                    // a size at all - that is how v1.11.0 ended up with a
                    // ceiling of about 12. The ceiling it is allowed is the
                    // number somebody actually chose.
                    float size = text.enableAutoSizing
                        ? text.fontSizeMax : text.fontSize;
                    if (size > 0f) { templateFontSize = size; }
                }
            }
            else if (rt != null && rt.sizeDelta.y > 0f
                && child.GetComponent<LangText>() != null)
            {
                headingHeight = rt.sizeDelta.y;
            }
        }

        // A measurement outside 0.4-0.6 of the row height is a number we
        // misread rather than a number to obey: before the panel has ever been
        // laid out, an auto sizing label's fontSize is whatever it last drew
        // at, which is how v1.11.0 ended up with a ceiling of about 12. The
        // number somebody actually chose is fontSizeMax.
        float vanilla = templateFontSize;
        if (vanilla < templateRowHeight * 0.4f
            || vanilla > templateRowHeight * 0.6f)
        {
            vanilla = templateRowHeight * 0.5f;
        }
        fontFloor = vanilla;
        fontCeiling = vanilla * ROW_SCALE;
        templateRowHeight = templateRowHeight * ROW_SCALE;
        appliedRowHeight = templateRowHeight;
    }

    // A copy of the Tutorial Progress container with its button thrown away
    // and its heading retitled, dropped in right underneath the original. It
    // is cloned rather than built from nothing so that the layout group, the
    // size fitter and the heading's font all come along without this file
    // having to know what they were set to.
    private static GameObject MakeSection(Transform guide)
    {
        try
        {
            Transform column = guide.parent;
            if (column == null) { return null; }
            GameObject clone = UnityEngine.Object.Instantiate(
                guide.gameObject, column);
            clone.name = SECTION_NAME;
            clone.transform.SetSiblingIndex(guide.GetSiblingIndex() + 1);

            // Backwards, so "the first label" is the one nearest the top once
            // the list is reversed - the heading, not something inside the
            // button. Deactivated before Destroy because Destroy does not
            // take effect until the end of the frame and the rows go in now.
            LangText heading = null;
            for (int i = clone.transform.childCount - 1; i >= 0; i--)
            {
                Transform child = clone.transform.GetChild(i);
                LangText text = child.GetComponent<LangText>();
                if (heading == null && text != null
                    && child.GetComponent<Button>() == null)
                {
                    heading = text;
                    continue;
                }
                child.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(child.gameObject);
            }
            if (heading != null)
            {
                heading.SetLocalisedText("Auto start");
            }
            return clone;
        }
        catch (Exception e)
        {
            AutoStartPlugin.Log.LogWarning(
                "Could not add the auto start section: " + e.Message);
            return null;
        }
    }

    // A row at the height of the buttons beside it, with the tick box and the
    // words centred in it. A rowHeight of 0 means "leave the size alone" -
    // the fallback position, where the row is one of the game's own small
    // ones and should go on looking like its neighbours.
    private static void StyleRow(GameObject clone, float rowHeight,
        float fontSize, float fontMin)
    {
        try
        {
            // The whole row answers the mouse, not just the tick box and the
            // words. A tooltip you have to take aim at is not much of a
            // tooltip, and a fully transparent Image still takes a raycast.
            if (clone.GetComponent<Image>() == null)
            {
                Image hit = clone.AddComponent<Image>();
                hit.color = new Color(0f, 0f, 0f, 0f);
                hit.raycastTarget = true;
            }
            if (rowHeight <= 0f) { return; }

            RectTransform rt = clone.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.sizeDelta = new Vector2(rt.sizeDelta.x, rowHeight);
            }
            // Belt and braces: a vertical layout group with childControlHeight
            // on reads the LayoutElement and ignores sizeDelta, and with it
            // off does the opposite. Set both and it does not matter which.
            LayoutElement element = clone.GetComponent<LayoutElement>();
            if (element == null)
            {
                element = clone.AddComponent<LayoutElement>();
            }
            element.minHeight = rowHeight;
            element.preferredHeight = rowHeight;

            float box = rowHeight * 0.55f;
            if (box < 18f) { box = 18f; }
            Transform background = clone.transform.Find("Background");
            if (background != null)
            {
                RectTransform brt = background as RectTransform;
                if (brt != null)
                {
                    brt.anchorMin = new Vector2(0f, 0.5f);
                    brt.anchorMax = new Vector2(0f, 0.5f);
                    brt.pivot = new Vector2(0.5f, 0.5f);
                    brt.sizeDelta = new Vector2(box, box);
                    brt.anchoredPosition = new Vector2(6f + box * 0.5f, 0f);
                }
                Transform check = background.Find("Checkmark");
                RectTransform crt = check == null
                    ? null : check as RectTransform;
                if (crt != null)
                {
                    crt.sizeDelta = new Vector2(box, box);
                }
            }

            Transform label = clone.transform.Find("Label");
            if (label == null) { return; }
            RectTransform lrt = label as RectTransform;
            if (lrt != null)
            {
                lrt.anchorMin = new Vector2(0f, 0f);
                lrt.anchorMax = new Vector2(1f, 1f);
                lrt.offsetMin = new Vector2(box + 14f, 2f);
                lrt.offsetMax = new Vector2(-6f, -2f);
            }
            TMP_Text text = label.GetComponent<TMP_Text>();
            if (text != null && fontSize > 0f)
            {
                // Auto sizing rather than a number, because the column is
                // narrow and how tall the canvas is depends on the shape of
                // the monitor. As big as the buttons where there is room for
                // it, and legible rather than clipped where there is not.
                text.enableWordWrapping = false;
                text.enableAutoSizing = true;
                text.fontSizeMax = fontSize;
                text.fontSizeMin = (fontMin > 0f && fontMin < fontSize)
                    ? fontMin : fontSize * 0.7f;
                text.fontSize = fontSize;
            }
        }
        catch (Exception e)
        {
            AutoStartPlugin.Log.LogWarning(
                "Could not size an auto start row: " + e.Message);
        }
    }

    // The fallback for when the scrolling list could not be built (see
    // SCROLL_NAME).
    //
    // Seven full height rows are about 280 units of a column that only has so
    // much to give: the canvas is scaled to a 1920 wide reference matched on
    // WIDTH, so a 21:9 monitor gets about 810 units of height rather than
    // 1080. A row that falls off the bottom of the panel cannot be clicked at
    // all, so measure what is left and shrink to fit. Runs from
    // RefreshVisibility, where the layout has definitely happened, rather than
    // at build time where the rects are all still zero.
    private static void FitSection(int owned)
    {
        try
        {
            if (section == null || templateRowHeight <= 0f) { return; }
            // Only when the scrolling list could not be built and the rows
            // sit in the section itself. In the list, CommunitySettingsFit
            // sizes them every frame by the rule both columns share.
            if (rows[0] == null || rows[0].transform.parent != section.transform)
            {
                return;
            }
            RectTransform column = section.transform.parent as RectTransform;
            RectTransform outer = column == null
                ? null : column.parent as RectTransform;
            if (outer == null) { return; }
            float available = outer.rect.height;
            if (available <= 1f) { return; }

            float used = 0f;
            for (int i = 0; i < column.childCount; i++)
            {
                RectTransform child = column.GetChild(i) as RectTransform;
                if (child == null || child.gameObject == section) { continue; }
                if (!child.gameObject.activeSelf) { continue; }
                used += child.rect.height;
            }

            // Once, when there is a laid out panel to measure: what our
            // rows came out as beside what we were copying. If a size is ever
            // wrong again this says so outright.
            if (!loggedShape)
            {
                loggedShape = true;
                RectTransform first = rows[0] == null
                    ? null : rows[0].GetComponent<RectTransform>();
                AutoStartPlugin.Log.LogInfo("Settings: auto start row is "
                    + (first == null ? 0f : first.rect.height)
                    + " tall and a button is "
                    + (measuredButton == null ? 0f : measuredButton.rect.height)
                    + "; font " + fontFloor + " to " + fontCeiling
                    + " from a measured " + templateFontSize
                    + "; the column has " + available + " with " + used
                    + " already spoken for");
            }

            int visible = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i] != null && owned >= LevelFor(i)) { visible++; }
            }
            if (visible == 0) { return; }

            float free = available - used - headingHeight - 24f;
            float want = templateRowHeight;
            if (free < want * visible) { want = free / visible; }
            if (want > templateRowHeight) { want = templateRowHeight; }
            if (want < 22f) { want = 22f; }
            if (Mathf.Abs(want - appliedRowHeight) < 0.5f) { return; }

            appliedRowHeight = want;
            // Only the height gives when the column is tight. The words stay
            // as big as they can be drawn, which is the whole point.
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i] != null)
                {
                    StyleRow(rows[i], want, fontCeiling, fontFloor);
                }
            }
            AutoStartPlugin.Log.LogInfo(
                "Settings: auto start rows sized to " + want
                + " so they fit the column");
        }
        catch (Exception e)
        {
            AutoStartPlugin.Log.LogWarning(
                "Could not fit the auto start rows: " + e.Message);
        }
    }

    private static ConfigEntry<bool> ConfigFor(int index)
    {
        if (index == 0) { return AutoStartPlugin.cfgEnabled; }
        if (index == 1) { return AutoStartPlugin.cfgUseBudget; }
        if (index == 2) { return AutoStartPlugin.cfgUseOrdering; }
        if (index == 3) { return AutoStartPlugin.cfgRemember; }
        if (index == 4) { return AutoStartPlugin.cfgEngineLast; }
        if (index == 5) { return AutoStartPlugin.cfgSpaceLast; }
        if (index == 6) { return AutoStartPlugin.cfgEngineStaysOn; }
        if (index == 7) { return AutoStartPlugin.cfgEngineModules; }
        return AutoStartPlugin.cfgCollectorsFirst;
    }

    // Which upgrade level each row belongs to. Was "index + 1" until v1.7.0
    // added a second row for level 3, so the rows are no longer one per level.
    private static int LevelFor(int index)
    {
        if (index == 4) { return 3; } // engine last is part of smart ordering
        if (index == 5) { return 3; } // and so is space last
        if (index == 6) { return 1; } // engine stays on needs nothing else
        if (index == 7) { return 1; } // filling the tank needs nothing else
        if (index == 8) { return 3; } // collectors first is smart ordering
        return index + 1;
    }

    private static void OnChanged(int index, bool isOn)
    {
        try
        {
            ConfigFor(index).Value = isOn;
            AutoStartPlugin.Log.LogInfo("Auto start setting '" + Labels[index]
                + "' set to " + isOn);
        }
        catch (Exception e)
        {
            AutoStartPlugin.Log.LogWarning(
                "Could not save an auto start setting: " + e.Message);
        }
    }

    // A level the player has not bought gets no row at all. Offering a switch
    // for something that cannot do anything only invites bug reports.
    internal static void RefreshVisibility()
    {
        if (rows[0] == null || AutoStartPlugin.shared == null)
        {
            return;
        }
        int owned = AutoStartPlugin.shared.OwnedLevel();
        for (int i = 0; i < rows.Length; i++)
        {
            GameObject row = rows[i];
            if (row == null)
            {
                continue;
            }
            bool shouldShow = owned >= LevelFor(i);
            if (row.activeSelf != shouldShow)
            {
                row.SetActive(shouldShow);
            }
        }

        // A heading on its own is worse than nothing, so the whole section
        // goes when there is not a single row to put under it.
        if (section == null) { return; }
        bool any = false;
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i] != null && owned >= LevelFor(i)) { any = true; break; }
        }
        if (section.activeSelf != any) { section.SetActive(any); }
        if (any) { FitSection(owned); }
    }
}
