"""Space Travel Idle - Community Mod Setup
=========================================

Interactive installer for the community mod. Lets you pick which additions
you want and writes a patched resources.assets into your game folder.

- Works with any install of Space Travel Idle 0.35.43 community ver
  (Normal, Beta, or Beta Danger builds - just point it at the right folder).
- Makes a backup (resources.assets.backup-original) the first time it runs.
- Always patches from that clean backup, so you can re-run it any time to
  add or remove patches, or restore the original game.

Requirements: Python 3.9+ and the UnityPy package. If UnityPy is missing the
script offers to install it for you.

Run it by double-clicking "Setup Space Travel Idle Mod.bat", or from a
terminal:  python setup_mod.py

Advanced / non-interactive use:
  python setup_mod.py --game "C:\\Path\\To\\Space Travel Idle" --patches travel-fix,combat-curve --yes
  python setup_mod.py --game "C:\\Path\\To\\Space Travel Idle" --restore --yes
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import textwrap

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

# Two version numbers on purpose. PUBLIC_VERSION is what Fuzzied announced
# to the community and what players call it, and it is locked. BUILD_VERSION
# keeps counting internally so a bug report can name an exact build. Players
# never see it: not in the zip names, not on the setup's first screen
# (Fuzzied, 28.09.2026).
# The mapping between them is kept in docs/version-map.md.
PUBLIC_VERSION = "0.1"
BUILD_VERSION = "0.9.39"
# Kept as an alias: six tools and the Mac packager already read this name.
MOD_VERSION = BUILD_VERSION
GAME_EXE = "SpaceTravelIdle.exe"
IS_MAC = sys.platform == "darwin"
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
# Two layouts. In the project this file lives in installer\ with the
# payload folders one level up. In a release zip it sits at the top with
# the payload folders beside it. Tell them apart by looking for one.
if os.path.isdir(os.path.join(SCRIPT_DIR, "bepinex")):
    ROOT_DIR = SCRIPT_DIR
else:
    ROOT_DIR = os.path.dirname(SCRIPT_DIR)
# BepInEx ships a different loader per platform: a winhttp.dll shim on
# Windows, a launcher script plus a .dylib on macOS. The managed DLLs
# under BepInEx/core differ too, so the two payloads are kept whole and
# separate rather than merged.
PAYLOAD_NAME = "payload-macos" if IS_MAC else "payload"
BEPINEX_PAYLOAD = os.path.join(ROOT_DIR, "bepinex", PAYLOAD_NAME)
PLUGIN_DIR = os.path.join(ROOT_DIR, "plugin")
# Extra files some plugins read at runtime, one folder per patch that needs
# them. A code patch names its folder with an "assets" key and the installer
# copies it in beside the DLL.
ASSETS_DIR = os.path.join(ROOT_DIR, "assets")

# ---------------------------------------------------------------------------
# Patch definitions
#
# Each patch edits one TextAsset (a JSON data file) inside resources.assets.
# Ops:
#   ("replace", old, new, expected_count)      - replace ALL occurrences;
#                                                abort if count differs.
#   ("replace_seq", old, [new1, new2, ...])    - occurrences replaced in
#                                                document order; abort if the
#                                                occurrence count differs.
#   ("clone_entry", old_key, new_key)          - duplicate a whole
#                                                "old_key": { ... } block under
#                                                new_key, keeping the original.
#   ("set_asset", hash_file, content_file)     - replace the WHOLE data file
#                                                with content_file, but only if
#                                                what is already there hashes
#                                                to the sha256 in hash_file.
#                                                For rewrites too large to
#                                                express as replaces; the hash
#                                                check makes it fail just as
#                                                loudly on a changed game.
# A patch that has to edit a second data file lists it under "also" as
# {"asset": ..., "ops": [...]}; those install and uninstall with the parent key.
# ---------------------------------------------------------------------------
# Uranus cost cut: every resource requirement on the planet, halved. The
# amounts below are the ones left after the outer-planets re-pointing ops
# above run, so these have to stay in that same patch. Keys inside a
# template are alphabetical, which puts "maxLevel" between the id and the
# cost - anchoring on all three pins each edit to exactly one template.
URANUS_HALVED = [
    # id, maxLevel, amount now, halved
    ("silicon_core",             1,     250000,    125000),   # water
    ("alien_wisdom_3",           1,   95000000,  47500000),   # silicon
    ("long_distance_journeys",   1,   95000000,  47500000),   # water
    ("path_knowledge_5",       100,     100000,     50000),   # biomass
    ("ice_clouds",               1,  450000000, 225000000),   # biomass
    ("brave_the_hailstorm",      1,  500000000, 250000000),   # biomass
    ("biomass_freezing",       100,    8000000,   4000000),   # silicon
    # quartered rather than halved because it also moves to water, which
    # produces roughly a ninth of what silicon does on this planet
    ("defrosting_equipment",   100,    9200000,   2300000),   # -> water
    ("glacier_station",        100,      50000,     25000),   # biomass
    ("rocket_launcher_uranus",   1,      35000,     17500),   # biomass
    # cut to a sixth rather than halved: its bonus (BonusType 16) is an
    # infra bonus, so it is LOCAL to Uranus and stops paying the moment you
    # leave - a 100-level quadratic grind for a benefit you cannot take
    # with you is not worth the planet's whole silicon output
    ("university_of_herschel", 100,   85000000,   7083333),   # silicon
]

# Uranus ended up with five silicon sinks and only two water ones, so three
# researches compete for silicon at once while a large water stockpile sits
# unused. defrosting_equipment moves to water to even that out. Runs after
# the halving above, so it anchors on the already-reduced amount.
URANUS_RETYPED = [
    # id, maxLevel, amount, element type now, element type wanted
    ("defrosting_equipment", 100, 2300000, 5, 1),
]


def _cost_anchor(research_id, max_level, amount, element_type=None):
    text = ('"id": "%s",\n                "maxLevel": %d,\n'
            '                "resourceCost": {\n'
            '                    "amount": %d,' % (research_id, max_level, amount))
    if element_type is not None:
        text += '\n                    "type": %d' % element_type
    return text


HALVE_URANUS_OPS = [
    ("replace", _cost_anchor(i, m, old), _cost_anchor(i, m, new), 1)
    for i, m, old, new in URANUS_HALVED
] + [
    ("replace", _cost_anchor(i, m, a, old), _cost_anchor(i, m, a, new), 1)
    for i, m, a, old, new in URANUS_RETYPED
]

def _loc_entry(key, name, desc):
    """One localisation entry, formatted the way the shipped files are."""
    return ('"%s": {\n'
            '            "desc": "%s",\n'
            '            "name": "%s"\n'
            '        }' % (key, desc, name))


# Three pieces of vanilla content only ever got en-US and zh-CN strings; the
# other six languages fall back to showing the raw lookup key. English text
# is a poor translation but a much better default than "researches.x.name".
UNTRANSLATED = [
    ("god_of_war", "after", _loc_entry(
        "gravitational_slingshot", "Gravitational Slingshot",
        "Use planetary gravity wells to boost engine speed.")),
    ("silicon_core", "before", _loc_entry(
        "ring_harmonics", "Ring Harmonics",
        "Study Saturn's ring resonances to fold space more efficiently.")),
    ("star_wand", "after", _loc_entry(
        "storm_core", "Storm Core",
        "A legendary card containing the essence of Jupiter's eternal storm.")),
]

# Data files this script reads itself, rather than through a set_asset op.
# The packagers ship everything listed here. Until 26.09.2026 they only
# shipped what set_asset names, so this one was left out of every release zip
# and a player's new Big Bang cards would have shown raw lookup keys.
NEW_LINES_FILE = os.path.join("data", "bigBangUpgrades_NEWLINES.json")
EXTRA_DATA_FILES = [NEW_LINES_FILE]


def _load_new_bigbang_lines():
    """Names/descriptions for the Big Bang lines tools/gen_bigbang.py added.

    Kept in the generated file rather than typed out twice, so the wording can
    never drift away from the upgrade data it belongs to. Missing or unreadable
    returns nothing here, so the menu still opens; patch_game refuses to
    install 'bigbang-plus' without it, because the cards would have no names.
    """
    path = os.path.join(ROOT_DIR, NEW_LINES_FILE)
    try:
        with open(path, encoding="utf-8") as fh:
            return json.load(fh)
    except Exception:
        return []


NEW_BIGBANG_LINES = _load_new_bigbang_lines()

PATCHES = [
    {
        "key": "travel-fix",
        "title": "Bugfix: unlock travel to Uranus, Neptune and Pluto",
        "desc": (
            "The base game ships Uranus/Neptune/Pluto with a broken departure\n"
            "   requirement ('Have Visited: Space') that can never be satisfied,\n"
            "   so travel past Saturn is impossible. This fixes the requirement\n"
            "   to 'have visited the previous planet'."
        ),
        "default": True,
        "asset": "starMap",
        "ops": [
            # uranus, neptune, pluto appear in this order in the file
            ("replace_seq", '"travelProgressIdx": 100000',
             ['"travelProgressIdx": 7',
              '"travelProgressIdx": 8',
              '"travelProgressIdx": 9']),
        ],
        "verify": [("uranus", '"travelProgressIdx": 7'),
                   ("neptune", '"travelProgressIdx": 8'),
                   ("pluto", '"travelProgressIdx": 9')],
    },
    {
        "key": "neptune-reach",
        "title": "Bugfix: Neptune and Pluto are physically unreachable",
        "desc": (
            "A trip's length is the distance between the two planets times the\n"
            "   DIFFERENCE in their 'trajectoryDifficulty', and the base game's\n"
            "   ladder runs 1e6, 1e7, 1e9, 1e11, 1e15 - so the hop to Neptune is\n"
            "   133x the hop to Uranus, and Pluto is another 13,600x on top.\n"
            "   With every travel research in the game maxed out and an empty\n"
            "   hold, Uranus->Neptune takes 61 days and Neptune->Pluto takes\n"
            "   2,290 years, against a hard departure limit of 36 hours. No\n"
            "   amount of play can close that gap - like the other outer-planet\n"
            "   faults, these numbers were never play-tested because travel past\n"
            "   Saturn was blocked. Neptune becomes 2.5e9 and Pluto 4e9, which\n"
            "   keeps each leg longer than the last (Saturn->Uranus ~11h, then\n"
            "   ~22h, then ~30h) while leaving both inside the 36-hour limit."
        ),
        "default": True,
        "asset": "starMap",
        "ops": [
            ("replace", '"trajectoryDifficulty": "1e11"',
             '"trajectoryDifficulty": "2.5e9"', 1),
            ("replace", '"trajectoryDifficulty": "1e15"',
             '"trajectoryDifficulty": "4e9"', 1),
        ],
        "verify": [("neptune", '"trajectoryDifficulty": "2.5e9"'),
                   ("pluto", '"trajectoryDifficulty": "4e9"')],
    },
    {
        "key": "pilot-training",
        "title": "Bugfix: Advanced Pilot Training does nothing at all",
        "desc": (
            "Jupiter's Advanced Pilot Training raises your departure time\n"
            "   limit, and it is one of only two researches in the whole game\n"
            "   that can. It costs a billion energy, 50,000 air and 158 Big\n"
            "   Bang score - and it adds 0.05 hours. The game then rounds the\n"
            "   total limit down to a whole number of hours, so those three\n"
            "   minutes are thrown away and the research changes nothing\n"
            "   whatsoever. It reads like a typo for a number that was meant to\n"
            "   matter. It now adds 14 hours, taking the departure limit from 36\n"
            "   to 50 with both researches bought - enough that the first hop to\n"
            "   Neptune, the one your Path Knowledge cannot shorten because you\n"
            "   have never been there, is actually flyable."
        ),
        "default": True,
        "asset": "starMap",
        "ops": [
            # The only "0.05" in the file, and the only type-38 bonus on
            # Jupiter, so this cannot land on anything else.
            ("replace", '''"bonus": {
                    "eqTemplate": "0.05",
                    "method": 2,
                    "type": 38
                }''', '''"bonus": {
                    "eqTemplate": "14",
                    "method": 2,
                    "type": 38
                }''', 1),
        ],
        "verify": [("advanced_pilot_training", '"eqTemplate": "14"')],
    },
    {
        "key": "outer-planets",
        "title": "Bugfix: Uranus, Neptune & Pluto production and dead ends",
        "desc": (
            "The outer planets were unreachable in the community version, so\n"
            "   their economies were never play-tested, and they have three\n"
            "   separate faults that together make Uranus unplayable:\n"
            "   (1) every planet's output decays the longer you stay, down to a\n"
            "   floor - Jupiter and Saturn settle at 0.7%, but Uranus/Neptune/\n"
            "   Pluto were set to 0.001%, 0.00005% and 0.000001%, which after a\n"
            "   few hours leaves production at essentially zero while their\n"
            "   costs stay in the tens of millions. The floors are raised to\n"
            "   0.7%, the same as Jupiter and Saturn (a maxed local station\n"
            "   still multiplies it x40, as everywhere else).\n"
            "   (2) Uranus's power plant produces 0.1 energy per fill, while\n"
            "   Neptune's identical methane plant produces 2000 - clearly a\n"
            "   dropped decimal, so it is set to 2000, and its ice pump's\n"
            "   500,000 energy per fill (100x Saturn's for 1.5x the water) is\n"
            "   brought to 50,000.\n"
            "   (3) 'Silicon Core' (the gate to Uranus's whole silicon\n"
            "   economy) costs metal, but nothing past Saturn produces metal,\n"
            "   so arriving without metal banked is a dead end. Neptune's\n"
            "   first infrastructure has the same problem: it costs soil,\n"
            "   which nothing past Mars produces.\n"
            "   Silicon Core now costs 250k water (available from arrival),\n"
            "   Biomass Freezing costs silicon (Uranus's abundant resource\n"
            "   rather than its scarcest), and Reclaim Land costs the air\n"
            "   Neptune recomposes. No cost is made cheaper than vanilla in\n"
            "   real terms - they are moved onto resources the planet can\n"
            "   actually produce."
        ),
        "default": True,
        "asset": "starMap",
        "ops": [
            # These planets were unreachable in the community version (see
            # travel-fix), so their economies were never play-tested: each
            # planet only runs its OWN element sources, storage persists
            # across travel (cleared only by Big Bang), and biomass drops
            # from battles anywhere - but iron ends at Saturn and soil at
            # Mars, so any cost in those is a soft-lock for arrivals with
            # an empty bank. type: 0=air 1=water 2=soil 5=silicon 6=iron.
            # Output decays with time spent on a planet, bottoming out at
            # this floor (Star.DeterioratedEfficiency). Jupiter/Saturn use
            # 0.007; the outer three were 700x to 700,000x lower, so their
            # own multi-million costs could never be paid locally.
            ("replace", '"resourceGainDeteriorationMinBorder": "1e-5"',
             '"resourceGainDeteriorationMinBorder": "0.007"', 1),   # uranus
            ("replace", '"resourceGainDeteriorationMinBorder": "5e-7"',
             '"resourceGainDeteriorationMinBorder": "0.007"', 1),   # neptune
            ("replace", '"resourceGainDeteriorationMinBorder": "1e-8"',
             '"resourceGainDeteriorationMinBorder": "0.007"', 1),   # pluto
            # Uranus's methane plant vs Neptune's identical one: 0.1 vs 2000
            ("replace",
             '''"baseEnergyOutputPerFill": 0.1,
                "id": "burning_methane"''',
             '''"baseEnergyOutputPerFill": 2000.0,
                "id": "burning_methane"''', 1),
            # ice pump: 500k energy per 150 water is 100x Saturn's cost for
            # 1.5x the output; 50k keeps it the priciest water in the game
            ("replace",
             '''"baseBarFillRequiredEnergy": 500000.0,
                "baseElementOutputPerFill": {
                    "amount": 150.0,
                    "type": 1
                },
                "id": "heating_ice"''',
             '''"baseBarFillRequiredEnergy": 50000.0,
                "baseElementOutputPerFill": {
                    "amount": 150.0,
                    "type": 1
                },
                "id": "heating_ice"''', 1),
            ("replace",
             '''"id": "silicon_core",
                "maxLevel": 1,
                "resourceCost": {
                    "amount": 25000,
                    "type": 6
                }''',
             '''"id": "silicon_core",
                "maxLevel": 1,
                "resourceCost": {
                    "amount": 250000,
                    "type": 1
                }''', 1),
            ("replace",
             '''"id": "biomass_freezing",
                "maxLevel": 100,
                "resourceCost": {
                    "amount": 8000000,
                    "type": 6
                }''',
             '''"id": "biomass_freezing",
                "maxLevel": 100,
                "resourceCost": {
                    "amount": 8000000,
                    "type": 5
                }''', 1),
            ("replace",
             '''"id": "reclaim_land",
                "maxLevel": 1,
                "resourceCost": {
                    "amount": 100000,
                    "type": 2
                }''',
             '''"id": "reclaim_land",
                "maxLevel": 1,
                "resourceCost": {
                    "amount": 100000,
                    "type": 0
                }''', 1),
            # the resource cut above is only half the price of a level -
            # both of these also charge energy per level on the same
            # base x level curve, so the energy has to come down with it
            # or the cut does nothing. Both to two fifths.
            ("replace",
             '''"energyCost": 5700000000,
                "id": "university_of_herschel",''',
             '''"energyCost": 2280000000,
                "id": "university_of_herschel",''', 1),
        ] + HALVE_URANUS_OPS,
        # Bulk Transfer Capacity is a spaceship research, so it lives in
        # spaceStar rather than starMap - but it unlocks on Uranus
        # (travelProgressIdx 8) and is priced like the rest of that era.
        # 2,000 levels at base x level means finishing it costs ~2e12
        # silicon for a bigBangScore of 35 out of the game's 72,058: the
        # single worst score-per-resource in the game. A sixth of the cost
        # keeps it a long-term sink without it being a trap.
        "also": [{"asset": "spaceStar", "ops": [
            # 100000000 is not unique in spaceStar, so anchor on the id line
            ("replace",
             '''"energyCost": 100000000,
            "id": "bulk_transfer_capacity",''',
             '''"energyCost": 40000000,
            "id": "bulk_transfer_capacity",''', 1),
            ("replace",
             '''"id": "bulk_transfer_capacity",
            "maxLevel": 2000,
            "resourceCost": {
                "amount": 1000000,''',
             '''"id": "bulk_transfer_capacity",
            "maxLevel": 2000,
            "resourceCost": {
                "amount": 166667,''', 1),
        ], "verify": [("bulk_transfer_capacity", '''"amount": 166667,
                "type": 5'''),
                      ("bulk_transfer_capacity", '"energyCost": 40000000,')]}],
        "verify": [("silicon_core", '''"resourceCost": {
                    "amount": 125000,
                    "type": 1
                }'''),
                   ("university_of_herschel", '''"amount": 7083333,
                    "type": 5'''),
                   ("university_of_herschel", '"energyCost": 2280000000,'),
                   ("biomass_freezing", '''"amount": 4000000,
                    "type": 5'''),
                   ("defrosting_equipment", '''"amount": 2300000,
                    "type": 1'''),
                   ("reclaim_land", '''"amount": 100000,
                    "type": 0'''),
                   ("burning_methane", '''"baseEnergyOutputPerFill": 2000.0,
                "id": "burning_methane"'''),
                   ("heating_ice", '"baseBarFillRequiredEnergy": 50000.0'),
                   ("uranus", '"resourceGainDeteriorationMinBorder": "0.007"'),
                   ("neptune", '"resourceGainDeteriorationMinBorder": "0.007"'),
                   ("pluto", '"resourceGainDeteriorationMinBorder": "0.007"')],
    },
    {
        "key": "perm-slot-uranus",
        "title": "Content: a 7th Extra Perm Slot, researchable on Uranus",
        "desc": (
            "The base game has six Extra Perm Slot researches, and the last one\n"
            "   you can earn is available from the spaceship before you even reach\n"
            "   Mars - so the perm bag stops growing for the entire back half of\n"
            "   the game. This adds a seventh on Uranus, gated behind the sixth\n"
            "   and behind Silicon Core, priced at 25x a level of Biomass\n"
            "   Freezing (100M silicon + 25bn energy) - heavier than any other\n"
            "   perm slot in the game."
        ),
        "default": True,
        "asset": "starMap",
        "ops": [
            ("replace",
             '{\n'
             '                "bigBangScore": 516,\n'
             '                "energyCost": 9000000000,\n'
             '                "id": "alien_wisdom_3",',
             '{\n'
             '                "bigBangScore": 900,\n'
             '                "bonus": {\n'
             '                    "eqTemplate": "1",\n'
             '                    "method": 2,\n'
             '                    "type": 1001\n'
             '                },\n'
             '                "energyCost": 25000000000,\n'
             '                "id": "extra_equip_slot_7",\n'
             '                "maxLevel": 1,\n'
             '                "resourceCost": {\n'
             '                    "amount": 100000000,\n'
             '                    "type": 5\n'
             '                },\n'
             '                "unlockReq": {\n'
             '                    "researchLevels": [\n'
             '                        "extra_equip_slot_6-1",\n'
             '                        "silicon_core-1"\n'
             '                    ]\n'
             '                }\n'
             '            },\n'
             '            {\n'
             '                "bigBangScore": 516,\n'
             '                "energyCost": 9000000000,\n'
             '                "id": "alien_wisdom_3",', 1),
        ],
        "verify": [("extra_equip_slot_7", '"amount": 100000000')],
        # the six shipped slots share one wording in every language; cloning
        # slot 6's block keeps the new one translated instead of English-only
        "also": [{"asset": "localisation_" + lang,
                  "ops": [("clone_entry", "extra_equip_slot_6",
                           "extra_equip_slot_7")]}
                 for lang in ("en-US", "de-GE", "es-ES", "ja-JP", "pl-PL",
                              "th-TH", "tr-TR", "zh-CN")],
    },
    {
        "key": "loc-fallback",
        "title": "Bugfix: untranslated text showing as raw keys",
        "desc": (
            "Gravitational Slingshot, Ring Harmonics and the Storm Core card\n"
            "   were added to the game without translations, so in six of the\n"
            "   eight languages they render as raw lookup keys like\n"
            "   'researches.ring_harmonics.name'. This fills them in with the\n"
            "   English text - not a translation, but readable. English and\n"
            "   Chinese already have them and are left alone."
        ),
        "default": True,
        # every language file needs the same three inserts, and the anchor
        # keys are ids so they are identical across languages
        "asset": "localisation_de-GE",
        "ops": [("insert_entry", anchor, position, entry)
                for anchor, position, entry in UNTRANSLATED],
        "verify": [],
        "also": [{"asset": "localisation_" + lang,
                  "ops": [("insert_entry", anchor, position, entry)
                          for anchor, position, entry in UNTRANSLATED]}
                 for lang in ("es-ES", "ja-JP", "pl-PL", "th-TH", "tr-TR")],
    },
    {
        "key": "combat-curve",
        "title": "Rebalance: fix the three impossible challenge zones",
        "desc": (
            "Acid Clouds (Venus), Martian Dungeons (Mars) and Great Red Spot\n"
            "   (Jupiter) scale exponentially (x2 to x2.5 per level) and end up\n"
            "   hundreds of times stronger than the zones that come after them -\n"
            "   Martian Dungeons' boss is the strongest thing in the game and\n"
            "   Great Red Spot's level-20 regulars out-stat its own boss 3 to 1.\n"
            "   This re-slopes them so each is conquerable roughly one planet\n"
            "   later than where you find it, still growing faster than linear:\n"
            "   Acid Clouds during Mars, Martian Dungeons during Jupiter/Saturn,\n"
            "   Great Red Spot as the endgame push at near-max cards & Big Bang.\n"
            "   Boss attacks are brought down to where HP/defense builds actually\n"
            "   survive hits (no build in the game can survive a million-attack\n"
            "   one-shot), and the Martian Dungeons boss finally drops real loot."
        ),
        "default": True,
        "asset": "battleZones",
        "ops": [
            # Damage in this game is a plain subtraction (attack - defense,
            # BattleUnit.ReceiveAttack), and player stats span many orders of
            # magnitude (research/infra multiply attack up to ~x144, defense
            # even more). These numbers are anchored to measured late-game
            # stats (~8.7e6 atk / 7.3e4 def / 8e5 hp): challenge-boss attack
            # sits a few multiples above late-game defense so hits land for a
            # survivable fraction of HP, making defense, heal and Concentration
            # genuinely matter, while big HP pools carry the difficulty.
            # --- acid_clouds: regulars pow(2,x) -> gentler curves
            ("replace", '"hpEq": "2e3+pow(2,{x})*100"', '"hpEq": "2e3+pow(1.55,{x})*100"', 1),
            ("replace", '"atkEq": "750+pow(2,{x})*30"', '"atkEq": "750+pow(1.4,{x})*15"', 1),
            ("replace", '"defEq": "200+pow(2,{x})*10"', '"defEq": "200+pow(1.4,{x})*10"', 1),
            # acid_clouds boss: 1e8/3e7/1e7 -> 2e6/1.2e4/1.5e4 (conquerable during Mars)
            ("replace", '"hp": "1e8"', '"hp": "2e6"', 1),
            ("replace", '"atk": "3e7"', '"atk": "1.2e4"', 1),
            ("replace", '"def": "1e7"', '"def": "1.5e4"', 1),
            # --- martian_dungeons: 3 identical enemies, tamed
            ("replace", '"hpEq": "6e4+pow(2.5,{x})*160"', '"hpEq": "6e4+pow(1.7,{x})*160"', 3),
            ("replace", '"atkEq": "2e3+pow(2.3,{x})*60"', '"atkEq": "2e3+pow(1.45,{x})*40"', 3),
            ("replace", '"defEq": "300+pow(2.2,{x})*30"', '"defEq": "300+pow(1.55,{x})*30"', 3),
            # martian_dungeons boss: 1e10/1e9/1.5e8 -> 3e8/1.2e5/1e6 (conquerable during Jupiter/Saturn)
            ("replace", '"hp": "1e10"', '"hp": "3e8"', 1),
            ("replace", '"atk": "1e9"', '"atk": "1.2e5"', 1),
            ("replace", '"def": "1.5e8"', '"def": "1e6"', 1),
            # martian_dungeons boss reward was a lone level-1 Atk Boost at 0.04%;
            # give the hardest fight on Mars a pool worth winning (guaranteed
            # gain cards a notch above jupiter_clouds' 30, real stat-card odds,
            # and Concentration - the key card for the tanky boss meta)
            ("replace", '"atk_boost_1-0/0.000375"',
             '"energy_gain_1-32/1", "resource_gain_1-32/1", "atk_boost_1-2/0.05", '
             '"def_boost_1-2/0.05", "hp_boost_1-2/0.05", "concentration-1/0.02"', 1),
            # --- great_red_spot: 2 enemies, tamed
            ("replace", '"hpEq": "5e5+pow(2.5,{x})*200"', '"hpEq": "5e5+pow(1.8,{x})*200"', 2),
            ("replace", '"atkEq": "1e4+pow(2.3,{x})*80"', '"atkEq": "2e4+pow(1.5,{x})*70"', 2),
            ("replace", '"defEq": "5000+pow(2.2,{x})*40"', '"defEq": "5000+pow(1.65,{x})*40"', 2),
            # great_red_spot boss: 5e9/5e8/5e7 -> 4e9/4e5/3e6. The endgame push:
            # atk 4e5 hits a late-game defense build for ~12-35% of max HP, and
            # 4e9 hp takes ~5 full charge cycles - playtest at 6e9 showed a
            # sustain build survives but runs out of damage output.
            ("replace", '"hp": "5e9"', '"hp": "4e9"', 1),
            ("replace", '"atk": "5e8"', '"atk": "4e5"', 1),
            ("replace", '"def": "5e7"', '"def": "3e6"', 1),
            # storm_core at 0.0008 was a 200-day chase even with boss forging
            ("replace", '"storm_core-0/0.0008"', '"storm_core-0/0.004"', 1),
            # saturns_core_guardian (extremely_dense_gases boss): 3e6 atk is
            # 7.5x the endgame red spot boss. Under vanilla subtraction a
            # def-stack zeroed it (binary defense); under the smooth formula
            # it one-shots any era-appropriate build. 1e6 hits a saturn-era
            # def build for a heavy-but-survivable chunk (sim-verified).
            ("replace", '"atk": "3e6"', '"atk": "1e6"', 1),
        ],
        "verify": [("acid_clouds", '"hp": "2e6"'),
                   ("martian_dungeons", '"hp": "3e8"'),
                   ("great_red_spot", '"hp": "4e9"')],
    },
    {
        "key": "drop-rates",
        "title": "Bugfix: Treasure Hunter X can never be levelled",
        "desc": (
            "Treasure Hunter X is the game's best drop-rate card, and it has\n"
            "   exactly one source: the Acid Clouds boss on Venus, at a chance of\n"
            "   one in a hundred thousand, from a boss that revives every three\n"
            "   hours. That is 34 years for a single copy at the start, and still\n"
            "   two months once you own every drop upgrade in the game. Worse, it\n"
            "   only ever drops at level 0, and levelling a card takes four copies\n"
            "   of the level below - so level 9 is 262,144 of those one-in-a-\n"
            "   hundred-thousand rolls. It is not a rare card, it is an\n"
            "   unreachable one, and the only way to farm it faster is to already\n"
            "   own it.\n"
            "   Acid Clouds' chance goes up eight times, and three of the later\n"
            "   bosses start dropping it too: Rings of Saturn at level 0, and the\n"
            "   two endgame bosses at levels 1 and 2, so the higher tiers can be\n"
            "   reached by playing rather than by merging alone. Every value is\n"
            "   picked so that even a maxed-out farming loadout stays well under\n"
            "   the game's 100% cap, and no other card is touched - the rest of\n"
            "   the pool is already in a sane place once you own the extra Big\n"
            "   Bang drop tiers."
        ),
        "default": True,
        "asset": "battleZones",
        "ops": [
            # Chances here are expected values, not dice rolls: the game does
            # drops = chance * killCount (BattleZoneLibrary.CalcCardDrops), and
            # multiplies chance by Modifiers.dropChance, which stacks to x1535
            # with a full farming loadout under bigbang-plus. Nothing below goes
            # above 0.00025, which is 38% at that ceiling - so no entry can ever
            # reach the min(1, ...) cap and quietly become a guaranteed drop.
            # --- the existing tap, opened up 8x (61 days -> 7.6 days a copy
            # at the bigbang-plus ceiling). Last entry in its pool, so this is
            # a plain value change.
            ("replace", '"treasure_hunter_x-0/0.00001"',
             '"treasure_hunter_x-0/0.00008"', 1),
            # --- a second level-0 tap once you reach Saturn. Each of the three
            # ops below appends to a boss pool by rewriting its first entry as
            # a pair; that first entry is unique to its zone, so none of these
            # can land in the wrong pool.
            ("replace", '"energy_gain_1-42/1"',
             '"energy_gain_1-42/1",\n            '
             '"treasure_hunter_x-0/0.00015"', 1),
            # --- the ladder, which is the half that actually matters. A source
            # that only drops level 0 can never take the card past the low
            # tiers no matter how generous it is, because merging is 4:1. A
            # level-1 drop is worth 4 base copies and a level-2 drop 16, so
            # these two are what turn level 9 from impossible into a grind.
            ("replace", '"energy_gain_1-45/1"',
             '"energy_gain_1-45/1",\n            '
             '"treasure_hunter_x-1/0.0002"', 1),
            # great_red_spot already drops taichi_master at level 3, so an
            # endgame boss handing out a mid-level copy is the game's own idiom.
            ("replace", '"energy_gain_1-40/1"',
             '"energy_gain_1-40/1",\n            '
             '"treasure_hunter_x-2/0.00025"', 1),
        ],
        "verify": [("acid_clouds", '"treasure_hunter_x-0/0.00008"'),
                   ("rings_of_saturn", '"treasure_hunter_x-0/0.00015"'),
                   ("extremely_dense_gases", '"treasure_hunter_x-1/0.0002"'),
                   ("great_red_spot", '"treasure_hunter_x-2/0.00025"')],
    },
    {
        "key": "def-base-drops",
        "title": "Bugfix: Defense Base 1 fell out of the drop tables",
        "desc": (
            "Meteor Shower is built from six cards: attack, defense and HP, in\n"
            "   a base and a boost version each. Five of them drop from three to\n"
            "   seven places in the game. Defense Base 1 drops from exactly one,\n"
            "   the Martian Sand Dunes boss, at level 0 only. That is about one\n"
            "   copy every 33 hours, and the recipe wants level 2, which is 16\n"
            "   copies, once per craft. So the whole recipe waits on that single\n"
            "   card while the other five pile up.\n"
            "   Nothing else in the game is shaped like that. Defense Boost 1 is\n"
            "   the same card's twin and it drops on Jupiter and on the endgame\n"
            "   boss, at levels 0, 1 and 3. This gives Defense Base 1 the exact\n"
            "   four entries its twin already has and changes nothing else. Its\n"
            "   supply goes from 0.03 copies an hour to 98, which lands it right\n"
            "   next to its twin at 106, and Meteor Shower stops being gated on\n"
            "   it. Attack Base 1 becomes the slowest ingredient instead, which\n"
            "   is where the recipe was always meant to sit."
        ),
        "default": True,
        "asset": "battleZones",
        "ops": [
            # Mirroring def_boost_1 entry for entry is the whole design here.
            # Picking fresh numbers would be a balance change; copying the twin
            # is a bug fix, and it is checkable by anyone reading the file.
            # --- Great Red Spot, regular pool: def_boost_1 level 0.
            ("replace", '"def_boost_1-0/0.00001*pow(2,{x})"',
             '"def_boost_1-0/0.00001*pow(2,{x})",\n            '
             '"def_base_1-0/0.00001*pow(2,{x})"', 1),
            # --- Great Red Spot, boss pool: def_boost_1 level 3. Anchored on
            # the atk_boost_1 line above it because "def_boost_1-3/0.001" alone
            # appears in two zones.
            ("replace", '"atk_boost_1-3/0.001",\n            '
                        '"def_boost_1-3/0.001"',
             '"atk_boost_1-3/0.001",\n            '
             '"def_boost_1-3/0.001",\n            '
             '"def_base_1-3/0.001"', 1),
            # --- Extremely Dense Gases, regular pool: def_boost_1 level 1.
            ("replace", '"def_boost_1-1/0.00001*pow(2,{x})"',
             '"def_boost_1-1/0.00001*pow(2,{x})",\n            '
             '"def_base_1-1/0.00001*pow(2,{x})"', 1),
            # --- Extremely Dense Gases, boss pool: def_boost_1 level 3. Same
            # two-zone problem, so anchored on the hp_base_1 line above it.
            ("replace", '"hp_base_1-3/0.001",\n            '
                        '"def_boost_1-3/0.001"',
             '"hp_base_1-3/0.001",\n            '
             '"def_boost_1-3/0.001",\n            '
             '"def_base_1-3/0.001"', 1),
        ],
        "verify": [("great_red_spot", '"def_base_1-3/0.001"'),
                   ("extremely_dense_gases",
                    '"def_base_1-1/0.00001*pow(2,{x})"')],
    },
    {
        "key": "petri-dish-drops",
        "title": "Bugfix: Petri Dish stops dead at level 9",
        "desc": (
            "Petri Dish is the biomass card and it goes to level 99, the same\n"
            "   as Energy Gain and Resource Gain. Where it drops is another\n"
            "   story. Those two turn up in thirteen zones between them, at\n"
            "   drop levels climbing to 45, and every outer-planet boss hands\n"
            "   you a guaranteed copy. Petri Dish drops in four zones, never\n"
            "   above level 9, and the last of them is on Mars. Past Mars\n"
            "   there is nothing at all.\n"
            "   Levelling a card takes four copies of the one below, so from a\n"
            "   level 9 source alone, level 20 is four to the eleventh. It is\n"
            "   not a slow card, it is a walled-off one.\n"
            "   It now gets the shape its two siblings already have out there:\n"
            "   a scaling drop in the normal pool and a guaranteed copy from\n"
            "   the boss, in Jupiter Clouds, Core Exploration, the Great Red\n"
            "   Spot, the Rings of Saturn and Extremely Dense Gases. Drop\n"
            "   levels run 12 to 24 in the normal pools and 18 to 33 from the\n"
            "   bosses, well under the 45 the other two reach. No other card\n"
            "   is touched."
        ),
        "default": True,
        "asset": "battleZones",
        "ops": [
            # The design is copied, not invented: biomass_gain_1 gets the same
            # two entries per zone that energy_gain_1 and resource_gain_1
            # already have there, at its own lower drop levels. Each anchor is
            # unique in the vanilla file, so none of these can land in the
            # wrong pool.
            # --- normal pools. Base chances match the zone's own gain-card
            # entries, so Petri Dish farms at the same rate the other two do.
            ("replace", '"energy_gain_1-19/0.0001*pow(2,{x})"',
             '"energy_gain_1-19/0.0001*pow(2,{x})",\n            '
             '"biomass_gain_1-12/0.0001*pow(2,{x})"', 1),
            ("replace", '"energy_gain_1-23/0.0001*pow(2,{x})"',
             '"energy_gain_1-23/0.0001*pow(2,{x})",\n            '
             '"biomass_gain_1-15/0.0001*pow(2,{x})"', 1),
            ("replace", '"energy_gain_1-27/0.00015*pow(2,{x})"',
             '"energy_gain_1-27/0.00015*pow(2,{x})",\n            '
             '"biomass_gain_1-18/0.00015*pow(2,{x})"', 1),
            ("replace", '"energy_gain_1-31/0.00015*pow(2,{x})"',
             '"energy_gain_1-31/0.00015*pow(2,{x})",\n            '
             '"biomass_gain_1-21/0.00015*pow(2,{x})"', 1),
            ("replace", '"energy_gain_1-36/0.00015*pow(2,{x})"',
             '"energy_gain_1-36/0.00015*pow(2,{x})",\n            '
             '"biomass_gain_1-24/0.00015*pow(2,{x})"', 1),
            # --- boss pools. Chance 1 is the game's own idiom for a gain card
            # from a boss: energy_gain_1 and resource_gain_1 are already
            # guaranteed in every one of these five. This is the half that
            # actually moves the ladder, because a chance drop at level 12
            # can never reach level 30 on merges alone.
            ("replace", '"resource_gain_1-30/1"',
             '"resource_gain_1-30/1",\n            '
             '"biomass_gain_1-18/1"', 1),
            ("replace", '"resource_gain_1-35/1"',
             '"resource_gain_1-35/1",\n            '
             '"biomass_gain_1-22/1"', 1),
            ("replace", '"resource_gain_1-40/1"',
             '"resource_gain_1-40/1",\n            '
             '"biomass_gain_1-26/1"', 1),
            # rings_of_saturn and extremely_dense_gases are anchored off other
            # lines, because their resource_gain_1 boss entries are also where
            # drop-rates appends Treasure Hunter X.
            ("replace", '"hp_base_1-2/0.001"',
             '"hp_base_1-2/0.001",\n            '
             '"biomass_gain_1-29/1"', 1),
            ("replace", '"lightning_action-6/0.003"',
             '"lightning_action-6/0.003",\n            '
             '"biomass_gain_1-33/1"', 1),
        ],
        "verify": [("jupiter_clouds", '"biomass_gain_1-18/1"'),
                   ("great_red_spot",
                    '"biomass_gain_1-18/0.00015*pow(2,{x})"'),
                   ("rings_of_saturn", '"biomass_gain_1-29/1"'),
                   ("extremely_dense_gases", '"biomass_gain_1-33/1"')],
    },
    {
        "key": "accelerator-drops",
        "title": "Bugfix: Research and Infra Accelerator cannot be levelled",
        "desc": (
            "These two are the cards that speed up research and building, and\n"
            "   they stop at level 10 rather than 100, so they are meant to be\n"
            "   finished. They cannot be. Research Accelerator drops from one\n"
            "   place in the entire game, the Primitive Alien Invaders on\n"
            "   Earth, and Infra Accelerator from one, Low Earth Orbit. Both\n"
            "   only ever drop at level 0, and only from zone level 6 upward,\n"
            "   so the tap closes the moment you leave the first two zones\n"
            "   behind.\n"
            "   Four copies make one of the level above, so the top level is\n"
            "   262,144 of those level-0 drops, out of a starter zone you have\n"
            "   no reason to be standing in. That is the same dead end\n"
            "   Treasure Hunter X had.\n"
            "   The Earth taps open up four times, and both cards now drop\n"
            "   from the outer-planet bosses at rising levels: 2 on the\n"
            "   Martian Highlands, then 3, 4, 5, 6 and 7 through Jupiter and\n"
            "   Saturn. The last two levels are still a proper grind, they are\n"
            "   just reachable by playing now. Neither card's own numbers are\n"
            "   touched."
        ),
        "default": True,
        "asset": "battleZones",
        "ops": [
            # --- the existing taps, opened up 4x. Both are the last entry in
            # their zone's normal pool and the strings are unique, so these
            # are plain value changes.
            ("replace", '"research_acceleration_1-0/5/0.0002*pow(2.2,{x})"',
             '"research_acceleration_1-0/5/0.0008*pow(2.2,{x})"', 1),
            ("replace", '"infra_acceleration_1-0/5/0.0005*pow(2.2,{x})"',
             '"infra_acceleration_1-0/5/0.002*pow(2.2,{x})"', 1),
            # --- the ladder, which is the half that matters. A source that
            # only ever drops level 0 can never reach the top no matter how
            # generous it is, because merging is 4:1. A level-7 copy is worth
            # 16,384 base ones.
            # Chance 1 from a boss is the game's own idiom for a gain card, and
            # a boss respawns on a timer, so a guaranteed copy is roughly one
            # every few hours rather than a flood. Level 9 from level 7 is 16
            # of them.
            ("replace", '"alien_meat-6/1"',
             '"alien_meat-6/1",\n            '
             '"research_acceleration_1-2/1",\n            '
             '"infra_acceleration_1-2/1"', 1),
            ("replace", '"energy_gain_1-30/1"',
             '"energy_gain_1-30/1",\n            '
             '"research_acceleration_1-3/1",\n            '
             '"infra_acceleration_1-3/1"', 1),
            ("replace", '"energy_gain_1-35/1"',
             '"energy_gain_1-35/1",\n            '
             '"research_acceleration_1-4/1",\n            '
             '"infra_acceleration_1-4/1"', 1),
            # Anchored on resource_gain_1 rather than the storm_core line
            # above it, because combat-curve rewrites storm_core's chance.
            ("replace", '"resource_gain_1-40/1"',
             '"resource_gain_1-40/1",\n            '
             '"research_acceleration_1-5/1",\n            '
             '"infra_acceleration_1-5/1"', 1),
            ("replace", '"resource_gain_1-42/1"',
             '"resource_gain_1-42/1",\n            '
             '"research_acceleration_1-6/1",\n            '
             '"infra_acceleration_1-6/1"', 1),
            ("replace", '"resource_gain_1-45/1"',
             '"resource_gain_1-45/1",\n            '
             '"research_acceleration_1-7/1",\n            '
             '"infra_acceleration_1-7/1"', 1),
        ],
        "verify": [("primitive_alien_invaders",
                    '"research_acceleration_1-0/5/0.0008*pow(2.2,{x})"'),
                   ("low_earth_orbit",
                    '"infra_acceleration_1-0/5/0.002*pow(2.2,{x})"'),
                   ("martian_highlands", '"research_acceleration_1-2/1"'),
                   ("great_red_spot", '"infra_acceleration_1-5/1"'),
                   ("extremely_dense_gases", '"infra_acceleration_1-7/1"')],
    },
    {
        "key": "enemy-deck-fix",
        "title": "Bugfix: Jupiter fights freeze on the enemy's turn",
        "desc": (
            "Four enemies on Jupiter are dealt a card that cannot be dealt.\n"
            "   Energy Gain and Resource Gain are permanent cards, the kind that\n"
            "   sit in your perm set, and the game will not build a battle card\n"
            "   out of one. It hands back nothing instead, and nothing goes into\n"
            "   the enemy's hand anyway. The moment that enemy takes a turn the\n"
            "   game throws an error and the fight stops dead, so the only way\n"
            "   through Core Exploration and the Great Red Spot is to kill every\n"
            "   enemy in a single hit.\n"
            "   The four bad cards are taken out of those decks. Those enemies\n"
            "   fight with four cards instead of five now, which is what they\n"
            "   were really doing all along, except now their turn finishes.\n"
            "   Careful: this means Jupiter enemies start hitting back, so those\n"
            "   two zones will feel harder than the frozen version did."
        ),
        "default": True,
        "asset": "battleZones",
        "ops": [
            # CardLibrary.GetBattleCard returns null when classType is not
            # battle, CardDeck.InsertCard takes the null without complaint, and
            # BattleUnit.GetCardFromAction then does handCard.isUsed on it. The
            # deck is the same size as the hand, so the null is always in hand
            # and the enemy's very first turn throws.
            # Anchored on the line above each bad entry so the trailing comma
            # goes with it and the JSON stays valid.
            # --- Core Exploration
            ("replace", '"lightning_action-3",\n                        '
                        '"energy_gain_1-10"', '"lightning_action-3"', 1),
            ("replace", '"heal_1-5",\n                        '
                        '"resource_gain_1-10"', '"heal_1-5"', 1),
            # --- Great Red Spot
            ("replace", '"lightning_action-5",\n                        '
                        '"energy_gain_1-15"', '"lightning_action-5"', 1),
            ("replace", '"dodge_1-6",\n                        '
                        '"resource_gain_1-15"', '"dodge_1-6"', 1),
        ],
        "verify": [("core_exploration", '"lightning_action-3"'),
                   ("great_red_spot", '"lightning_action-5"')],
    },
    {
        "key": "outer-research-fix",
        "title": "Bugfix: three outer-planet researches can never unlock",
        "desc": (
            "Brave the Hailstorm on Uranus, Explore the Dark Spot on Neptune\n"
            "   and Darkness on Pluto each wait on something filed under the\n"
            "   wrong heading. They ask for an infrastructure called Ice Clouds,\n"
            "   Icy Excursion and Explore the Heart, but all three of those are\n"
            "   researches, not infrastructure. The game looks for them in the\n"
            "   infrastructure list, never finds them, and the three researches\n"
            "   stay locked forever no matter what you build.\n"
            "   That is 4,478 Big Bang score nobody has ever been able to buy.\n"
            "   The fix moves each requirement to the research list, which is\n"
            "   where its twin on Jupiter already sits."
        ),
        "default": True,
        "asset": "starMap",
        "ops": [
            # UnlockManager runs req.researchLevels through
            # TechnoManager.SatisfiesResearchReq and req.infraLevels through
            # SatisfiesInfraReq. InfraSet.SatisfiesReq returns false whenever
            # infraDict has no such key, so a research id listed under
            # infraLevels is unsatisfiable rather than an error. Jupiter's
            # discover_the_storm gets this right; Saturn's
            # deep_gas_exploration really does want an infra. These three are
            # the only mix-ups in the file (tools/data_audit.py).
            # Anchored on the requirement id, each of which is unique.
            ("replace", '"infraLevels": [\n                        '
                        '"ice_clouds-1"',
             '"researchLevels": [\n                        '
             '"ice_clouds-1"', 1),
            ("replace", '"infraLevels": [\n                        '
                        '"icy_excursion-1"',
             '"researchLevels": [\n                        '
             '"icy_excursion-1"', 1),
            ("replace", '"infraLevels": [\n                        '
                        '"explore_the_heart-1"',
             '"researchLevels": [\n                        '
             '"explore_the_heart-1"', 1),
        ],
        "verify": [("brave_the_hailstorm",
                    '"researchLevels": [\n                        '
                    '"ice_clouds-1"'),
                   ("explore_the_dark_spot",
                    '"researchLevels": [\n                        '
                    '"icy_excursion-1"'),
                   ("darkness",
                    '"researchLevels": [\n                        '
                    '"explore_the_heart-1"')],
    },
    {
        "key": "mars-costs",
        "title": "Rebalance: Mars water and soil costs",
        "desc": (
            "Improved Solar Panels on Mars asks for water, and the amount is\n"
            "   steep next to everything else on the planet: 100,000 per level,\n"
            "   and the cost climbs with the level, so the full ladder to 100\n"
            "   wants over half a billion water. Martian Walls is the same\n"
            "   shape with soil. Both cost less per level now: the solar\n"
            "   panels drop to 15% of what they were, the walls a fifth off.\n"
            "   Nothing else about either one changes, and progress is\n"
            "   stored as a percentage of the cost, so a job you are part\n"
            "   way through keeps exactly where it was and simply needs\n"
            "   less to finish."
        ),
        "default": True,
        "asset": "starMap",
        "ops": [
            # Both anchors carry the entry's own id, which occurs once in the
            # file, so neither can land on another planet's job. Checked
            # against the earlier starMap patches too: travel-fix,
            # neptune-reach, pilot-training, outer-planets, perm-slot-uranus,
            # outer-research-fix and full-hand all touch other ids, so nothing
            # has rewritten these two lines by the time this runs.
            ("replace", '''"id": "improved_solar_panels",
                "maxLevel": 100,
                "resourceCost": {
                    "amount": 100000,
                    "type": 1
                }''', '''"id": "improved_solar_panels",
                "maxLevel": 100,
                "resourceCost": {
                    "amount": 15000,
                    "type": 1
                }''', 1),
            ("replace", '''"id": "martian_walls",
                "maxLevel": 100,
                "resourceCost": {
                    "amount": 30000,
                    "type": 2
                }''', '''"id": "martian_walls",
                "maxLevel": 100,
                "resourceCost": {
                    "amount": 24000,
                    "type": 2
                }''', 1),
        ],
        "verify": [("improved_solar_panels", '"amount": 15000'),
                   ("martian_walls", '"amount": 24000')],
    },
    {
        "key": "outer-costs",
        "title": "Rebalance: Defense Booster and Tombaugh Treasures",
        "desc": (
            "Two lines out past Mars are priced so far above everything around\n"
            "   them that they end up being the only thing still running, hours\n"
            "   after the rest of the planet is finished.\n"
            "   Saturn's Defense Booster goes to level 5000. Nothing else in the\n"
            "   game goes past 1000, and cost climbs with the level, so the full\n"
            "   ladder wants 13.75 trillion energy. That is 95% of what Saturn's\n"
            "   research and infrastructure cost all put together. It now asks\n"
            "   50,000 energy and 2,500 Water per level instead of 1.1 million\n"
            "   and 50,000, which puts it at 49% of its planet. That is the same\n"
            "   share Jupiter, Uranus and Neptune each give their biggest line,\n"
            "   so it stops being a special case.\n"
            "   Pluto's Tombaugh Treasures is the worse of the two. It wants\n"
            "   3.75 quadrillion energy, 99% of the planet, and it has no bonus\n"
            "   at all: Big Bang score is the only thing it ever gives you. It\n"
            "   drops to 100 million energy and 13,000 Metal per level, which\n"
            "   lands it just under Advanced Telescopes, so the one line that\n"
            "   does nothing you can feel is no longer the most expensive thing\n"
            "   on the planet.\n"
            "   Neither one loses a level or a point of bonus, and progress is\n"
            "   stored as a percentage of the cost, so a job you are part way\n"
            "   through keeps exactly where it was and simply needs less to\n"
            "   finish."
        ),
        "default": True,
        "asset": "starMap",
        "ops": [
            # Each anchor carries the entry's own id, which occurs once in the
            # whole file, so neither can land on another planet's job. No other
            # starMap patch mentions defense_booster or tombaugh_treasures, so
            # nothing has rewritten these lines by the time this runs.
            # type 1 = water, type 6 = iron (shown as "Metal" in game).
            ("replace", '''"energyCost": 1100000,
                "id": "defense_booster",
                "maxLevel": 5000,
                "resourceCost": {
                    "amount": 50000,
                    "type": 1
                }''', '''"energyCost": 50000,
                "id": "defense_booster",
                "maxLevel": 5000,
                "resourceCost": {
                    "amount": 2500,
                    "type": 1
                }''', 1),
            ("replace", '''"energyCost": 7500000000,
                "id": "tombaugh_treasures",
                "maxLevel": 1000,
                "resourceCost": {
                    "amount": 1000000,
                    "type": 6
                }''', '''"energyCost": 100000000,
                "id": "tombaugh_treasures",
                "maxLevel": 1000,
                "resourceCost": {
                    "amount": 13000,
                    "type": 6
                }''', 1),
        ],
        # The marker carries the id as well, because a bare '"energyCost": N'
        # could match a neighbouring entry inside the 25k verify window and
        # report success on a patch that did nothing.
        "verify": [("defense_booster",
                    '"energyCost": 50000,\n                '
                    '"id": "defense_booster"'),
                   ("tombaugh_treasures",
                    '"energyCost": 100000000,\n                '
                    '"id": "tombaugh_treasures"')],
    },
    {
        "key": "neptune-costs",
        "title": "Rebalance: Neptune costs more than the planet after it",
        "desc": (
            "Neptune costs 503 trillion energy to finish. Pluto, the planet\n"
            "   after it, costs 100 trillion. So the second to last planet is\n"
            "   five times the wall that the last one is, and Pluto gives you\n"
            "   nearly twice the Big Bang score for the trouble. The big jump\n"
            "   is supposed to land on Pluto, and right now it lands a planet\n"
            "   early.\n"
            "   Three lines carry all of Neptune. Cold Resistant Generators\n"
            "   wants 250 trillion on its own, Cold Resistant Batteries 172\n"
            "   trillion, and Airflow Optimization 81 trillion. Everything\n"
            "   else on the planet put together is under a tenth of a percent.\n"
            "   All three drop to about a twelfth of what they were, so\n"
            "   Neptune lands at 40 trillion. That puts it above Uranus and\n"
            "   below Pluto, and makes Pluto the biggest single step in the\n"
            "   game at two and a half times the planet before it.\n"
            "   The three keep their levels, their bonuses and their Big Bang\n"
            "   score, and they keep costing the same relative to each other,\n"
            "   so they still finish at around the same time instead of one\n"
            "   being left running alone. Progress is stored as a percentage\n"
            "   of the cost, so anything part way through stays where it is\n"
            "   and just needs less to finish."
        ),
        "default": True,
        "asset": "starMap",
        "ops": [
            # Each anchor carries the entry's own id, which occurs once in the
            # whole file, so none of these can land on another planet's job.
            # No other starMap patch mentions these three ids, so the vanilla
            # bytes are still intact by the time this runs.
            # type 0 = air, type 1 = water.
            ("replace", '''"energyCost": 7980000000,
                "id": "cold_resistant_generators",
                "maxLevel": 250,
                "resourceCost": {
                    "amount": 50000000,
                    "type": 0
                }''', '''"energyCost": 625000000,
                "id": "cold_resistant_generators",
                "maxLevel": 250,
                "resourceCost": {
                    "amount": 4000000,
                    "type": 0
                }''', 1),
            ("replace", '''"energyCost": 5470000000,
                "id": "cold_resistant_batteries",
                "maxLevel": 250,
                "resourceCost": {
                    "amount": 10000000,
                    "type": 0
                }''', '''"energyCost": 440000000,
                "id": "cold_resistant_batteries",
                "maxLevel": 250,
                "resourceCost": {
                    "amount": 800000,
                    "type": 0
                }''', 1),
            ("replace", '''"energyCost": 650000000,
                "id": "airflow_optimization",
                "maxLevel": 500,
                "resourceCost": {
                    "amount": 250000,
                    "type": 1
                }''', '''"energyCost": 52000000,
                "id": "airflow_optimization",
                "maxLevel": 500,
                "resourceCost": {
                    "amount": 20000,
                    "type": 1
                }''', 1),
        ],
        # Markers carry the id, because a bare '"energyCost": N' could match a
        # neighbouring entry inside the 25k verify window and report success on
        # a patch that did nothing.
        "verify": [("cold_resistant_generators",
                    '"energyCost": 625000000,\n                '
                    '"id": "cold_resistant_generators"'),
                   ("cold_resistant_batteries",
                    '"energyCost": 440000000,\n                '
                    '"id": "cold_resistant_batteries"'),
                   ("airflow_optimization",
                    '"energyCost": 52000000,\n                '
                    '"id": "airflow_optimization"')],
    },
    {
        "key": "taichi-rework",
        "title": "Rework: Taichi Master actually playable",
        "desc": (
            "Taichi Master's battle-speed effect scales down to x0.004 - holding\n"
            "   it makes combat run 250 times slower, so fights never even start.\n"
            "   Reworked to match its own description ('slow down but become\n"
            "   tougher'): battle runs 1.25x-1.9x slower while it is in hand, and\n"
            "   its attack/defense/HP buffs grow from +10% to +50% at max level."
        ),
        "default": True,
        "asset": "cardTemplates",
        "ops": [
            # time effect: x0.1..x0.004 (10x-250x slower) -> x0.8..x0.53
            ("replace", '"eqTemplate": "0.1*pow(0.7,{x})"', '"eqTemplate": "0.8-0.03*{x}"', 1),
            # in-hand atk/def/hp buffs: x1.05..x1.2 -> x1.1..x1.5
            ("replace", '"eqTemplate": "1.05+0.01666666666*{x}"', '"eqTemplate": "1.1+0.0444444*{x}"', 3),
        ],
        "verify": [("taichi_master", '"eqTemplate": "0.8-0.03*{x}"')],
    },
    {
        "key": "def-heal-rework",
        "title": "Rework: Defense, First Aid and The Bomb's burn actually scale",
        "desc": (
            "Three card effects use FLAT numbers in a game where late-game\n"
            "   stats reach the millions, making them dead after the first\n"
            "   planets. The Defense card (+38 def at max) becomes a defense\n"
            "   MULTIPLIER like Concentration's: x1.35 at level 0 up to x2.7 at\n"
            "   max, for 3 turns. First Aid's heal is raised from 5%-14% of max\n"
            "   HP to 8%-35%, so a max-level heal roughly cancels one big boss\n"
            "   hit. The Bomb's burn (flat 20-320 damage/turn for an absurd 50\n"
            "   turns) becomes a 10-turn burn of 0.5%-8% of the TARGET's max HP\n"
            "   per turn, and the blast now also breaks the target's armor:\n"
            "   enemy defense x0.75 (x0.5 at max level) for those 10 turns."
        ),
        "default": True,
        "asset": "cardTemplates",
        "ops": [
            # defense_1: flat amount (type 1) -> multiplier (type 2), the same
            # mechanism concentration's def buff uses
            ("replace",
             '''            "effectAmount": {
                "eqTemplate": "ceiling(pow(1.5,{x}))",
                "type": 1
            },''',
             '''            "effectAmount": {
                "eqTemplate": "1.35+0.15*{x}",
                "type": 2
            },''', 1),
            # heal_1 (First Aid): 5%..14% -> 8%..35% of max HP
            ("replace", '"eqTemplate": "0.05+0.01*{x}"', '"eqTemplate": "0.08+0.03*{x}"', 1),
            # bomb burn: flat 20*2^x per turn for 50 turns -> 0.5%..8% of
            # target max HP per turn for 10 turns (amount type 1 -> 2 switches
            # it to percentage-based, the alchemy x_burning mechanism; fights
            # never last 50 turns, so the total payload moved into 10)
            ("replace",
             '''            "effectAmount": {
                "eqTemplate": "20.0*pow(2,{x})",
                "type": 1
            },''',
             '''            "effectAmount": {
                "eqTemplate": "0.005*pow(2,{x})",
                "type": 2
            },''', 1),
            ("replace", '"turnCount": 50,', '"turnCount": 10,', 1),
            # bomb armor break: new subEffect3 - a defense "buff" of x0.75
            # (x0.5 at max level) cast ON THE TARGET (affectsTarget true, the
            # same BuffType.defense percentage slot Concentration multiplies
            # up; BattleUnit.def multiplies by it, so <1 = broken armor)
            ("replace",
             '''        "subEffect2": {
            "affectsTarget": true,
            "effectAmount": {
                "eqTemplate": "1.15+0.0125*{x}",
                "type": 2
            },
            "isInHandEffect": true,
            "successChanceEq": "1",
            "turnCount": 0,
            "type": 25
        },''',
             '''        "subEffect2": {
            "affectsTarget": true,
            "effectAmount": {
                "eqTemplate": "1.15+0.0125*{x}",
                "type": 2
            },
            "isInHandEffect": true,
            "successChanceEq": "1",
            "turnCount": 0,
            "type": 25
        },
        "subEffect3": {
            "affectsTarget": true,
            "effectAmount": {
                "eqTemplate": "0.75-0.0625*{x}",
                "type": 2
            },
            "isInHandEffect": false,
            "successChanceEq": "1",
            "turnCount": 10,
            "type": 3
        },''', 1),
        ],
        "verify": [("defense_1", '"eqTemplate": "1.35+0.15*{x}"'),
                   ("heal_1", '"eqTemplate": "0.08+0.03*{x}"'),
                   ("bomb", '"eqTemplate": "0.005*pow(2,{x})"'),
                   ("bomb", '"eqTemplate": "0.75-0.0625*{x}"')],
    },
    {
        "key": "full-hand",
        "title": "QoL: researchable 6th battle hand slot",
        "desc": (
            "The deck holds 6 cards but the battle hand holds only 5, so one\n"
            "   random deck card sits out of every fight - which also makes\n"
            "   passive in-hand cards (Taichi Master, Lightning Action)\n"
            "   unreliable. This adds a second level to Venus's 'Extra Hand\n"
            "   Slot' research: research it in-game and your whole deck is in\n"
            "   hand every battle. Level 1 is unchanged, so nothing is free."
        ),
        "default": True,
        "asset": "starMap",
        "ops": [
            # extra_hand_slot_2 (venus): bonus was a constant +1 at maxLevel 1;
            # make the bonus equal the research level and allow level 2.
            ("replace",
             '''                "bigBangScore": 5,
                "bonus": {
                    "eqTemplate": "1",
                    "method": 2,
                    "type": 1002
                },
                "energyCost": 10000000,
                "id": "extra_hand_slot_2",
                "maxLevel": 1,''',
             '''                "bigBangScore": 5,
                "bonus": {
                    "eqTemplate": "{x}",
                    "method": 2,
                    "type": 1002
                },
                "energyCost": 10000000,
                "id": "extra_hand_slot_2",
                "maxLevel": 2,''', 1),
        ],
        "verify": [("extra_hand_slot_2", '"eqTemplate": "{x}"')],
    },
    {
        "key": "bigbang-plus",
        "title": "Content: more Big Bang upgrades, and 12 new ones",
        "desc": (
            "Every one of the 22 Big Bang upgrades stops dead at tier 10, and\n"
            "   nothing in the game goes past it. Once you own them all (about\n"
            "   1.7 million dark matter) a Big Bang has nothing left to sell\n"
            "   you, so travelling further stops making you stronger.\n"
            "   This adds 5 more tiers to all 22 lines, continuing each one's\n"
            "   own curve, and adds 12 completely new upgrades on effects the\n"
            "   Big Bang tree never touched:\n"
            "     Stable Universe      - raise the floor a planet's output\n"
            "                            decays to, on EVERY planet at once\n"
            "                            (a planet's own station only helps\n"
            "                            on that planet)\n"
            "     Patient Universe     - make that decay happen more slowly\n"
            "     Stellar Cartography  - shorter trips to planets you have\n"
            "                            already reached\n"
            "     Cargo Compression    - a lighter ship, so a faster one\n"
            "     Essence Refinement   - more Star Essence\n"
            "     Planetary Memory     - keep a share of the building\n"
            "                            bonuses from the planets you\n"
            "                            are not standing on, wherever\n"
            "                            you fly (needs the 'planetary\n"
            "                            memory' code patch below to do\n"
            "                            anything)\n"
            "     Automated Arrival    - research and buildings start by\n"
            "                            themselves when you land somewhere\n"
            "                            (needs the 'start research and\n"
            "                            infrastructure on arrival' code\n"
            "                            patch below to do anything)\n"
            "     Ballast Trim         - a lighter empty hold\n"
            "     Cargo Bay Doors      - a bigger hold\n"
            "     Quantum Pocket       - keeps catching resources while the\n"
            "                            hold is already full, and empties\n"
            "                            itself into it when you land\n"
            "     Offline Reserve      - one more day of offline time per\n"
            "                            level, on top of the one day the\n"
            "                            mod gives everybody\n"
            "     Offline Vault        - one more week of offline time per\n"
            "                            level, on top of Offline Reserve\n"
            "   The last two need the 'offline save-load and travel fix' code\n"
            "   patch below, which is what owns the offline cap they lift.\n"
            "   Buying everything goes from 1.7 million dark matter to about\n"
            "   39 million, with the new lines starting cheap so the first\n"
            "   tiers are affordable on your very next Big Bang.\n"
            "   The extra tiers only become buyable once you have reached\n"
            "   Uranus (see 'Gate the extra Big Bang upgrades' below). Three\n"
            "   lines are never locked: Automated Arrival, Offline Reserve and\n"
            "   Offline Vault, because the players they help most are the ones\n"
            "   still flying the long early trips or unable to log in daily.\n"
            "   Your existing upgrade levels are kept - nothing is reset."
        ),
        "default": True,
        # The whole file is rewritten (see tools/gen_bigbang.py), because
        # appending five tiers to 22 lines plus five new lines is far past
        # what a list of text replacements can express readably. The sha256
        # check makes it refuse just as loudly on a game version it does not
        # recognise.
        "asset": "bigBangUpgrades",
        "ops": [("set_asset", os.path.join("data", "bigBangUpgrades_VANILLA.sha256"),
                 os.path.join("data", "bigBangUpgrades_PLUS.json"))],
        "verify": [("reduce_travel_time", '"eqTemplate": "65"'),
                   ("deterioration_floor_up", '"type": 21'),
                   ("star_essence_gain_up", '"type": 34')],
        # the five new lines need names and descriptions or they show up as
        # raw lookup keys; English text in every language beats that
        "also": [{"asset": "localisation_" + lang,
                  "ops": [("insert_entry", "alchemy_roll_curve", "after",
                           _loc_entry(spec["id"], spec["name"], spec["desc"]))
                          for spec in NEW_BIGBANG_LINES]
                         # An upgrade card shows its bonus in an auto-sizing
                         # label, so a long bonus NAME shrinks the whole line
                         # into the single digits - Fuzzied: "almost impossible
                         # to read". These two are 38 and 39 characters, by
                         # far the longest in the table. English only: the
                         # other languages have their own (shorter) wording.
                         + ([("replace",
                              '"erDeteriorationMinBorderUp": "Production Efficiency Min Value Improve"',
                              '"erDeteriorationMinBorderUp": "Min Production Efficiency"', 1),
                             ("replace",
                              '"erDeteriorationSlowdown": "Production Efficiency Decrease Slowdown"',
                              '"erDeteriorationSlowdown": "Efficiency Decay Slowdown"', 1),
                            # The Automated Arrival line buys no bonus, so its
                            # bonus TYPE is BonusType.none, and the upgrade card
                            # prints the type name above the value - which would
                            # read "None / 1". This key is dead weight in
                            # vanilla: the only two places that print a bonus
                            # (Research.details and Infra.details) skip type 0
                            # entirely, so nothing else can be affected by
                            # renaming it. The anchor carries the line above it
                            # because '"none": "None"' on its own appears three
                            # times in this file.
                            ("replace",
                             '"looting": "Looting",\n        "none": "None",',
                             '"looting": "Looting",\n        "none": "Automation Level",', 1)]
                            if lang == "en-US" else [])}
                 for lang in ("en-US", "de-GE", "es-ES", "ja-JP", "pl-PL",
                              "th-TH", "tr-TR", "zh-CN")],
    },
    {
        "key": "lightning-mastery",
        "title": "New card: Lightning Mastery, an Epic Lightning Action",
        "desc": (
            "Lightning Action is the game's farming card: it makes battles run\n"
            "   up to 3x faster, which means up to 3x the kills and 3x the card\n"
            "   drops per hour - but it cuts your attack, defense and HP to a\n"
            "   fifth, so it only works in a zone you have already outgrown.\n"
            "   This adds an Epic card built from it in the alchemy lab, for\n"
            "   people who want that speed in a zone that still fights back:\n"
            "   Lightning Mastery gives a THIRD of the speed bonus (x1.17 up to\n"
            "   x1.67) but its penalty starts at only x0.75 and shrinks as the\n"
            "   card levels up, reaching x1.00 - no penalty at all - at max\n"
            "   level. Craft it from 2x Lightning Action and 1x Taichi Master;\n"
            "   like the game's other alchemy cards the level you get is rolled,\n"
            "   so a good one is worth chasing. Available from Mars onwards."
        ),
        "default": True,
        # Two notes on the numbers, both checked against the decompiled code:
        #
        # 1. Battle speed is EffectType 24, and BattleManager.ApplyInHandEffects
        #    turns it into 'timeMod = 1 / multiplier', so x1.67 really is 1.67
        #    times the kills per hour. Lightning Action runs 1.5 -> 3.0; a third
        #    of the BONUS (not of the number) is 1.1667 -> 1.6667, which is what
        #    these equations give at level 0 and at max.
        #
        # 2. maxLevel is 99, not 9, because that is what the alchemy system
        #    needs: CardAlchemyRecipe.GenerateCard rolls a level as
        #    pow(random, rollCurve) * 100 and hands it straight to
        #    CardLibrary.GetCardWithLevel WITHOUT clamping it to the card's
        #    maxLevel. A 9-level card would routinely come out of the lab at
        #    level 40 and evaluate its equations far past where they were
        #    designed to stop. 99 levels also gives it the quality names the
        #    game already shows on alchemy cards (poor .. perfect).
        "asset": "cardTemplates",
        "ops": [
            ("replace",
             '            "type": 101\n        }\n    }\n]',
             '            "type": 101\n        }\n    },\n'
             '    {\n'
             '        "biomassConvertionEq": "0.1*log(1+({x}+1)*31.0/({xMax}+1),2)",\n'
             '        "classType": 1,\n'
             '        "id": "lightning_mastery",\n'
             '        "isAlchemyCard": true,\n'
             '        "isCollectible": true,\n'
             '        "isEffectiveInBag": false,\n'
             '        "isRenewable": false,\n'
             '        "isVisible": true,\n'
             '        "mainEffect": {\n'
             '            "affectsTarget": true,\n'
             '            "effectAmount": {\n'
             '                "eqTemplate": "1.1666666667+0.5*pow({x}/99.0,0.8)",\n'
             '                "type": 2\n'
             '            },\n'
             '            "isInHandEffect": true,\n'
             '            "successChanceEq": "1",\n'
             '            "turnCount": 1,\n'
             '            "type": 24\n'
             '        },\n'
             '        "maxLevel": 99,\n'
             '        "rarity": 5,\n'
             '        "reunlockReq": {\n'
             '            "travelProgressIdx": 5\n'
             '        },\n'
             '        "subEffect1": {\n'
             '            "affectsTarget": true,\n'
             '            "effectAmount": {\n'
             '                "eqTemplate": "0.75+0.25*pow({x}/99.0,0.5)",\n'
             '                "type": 2\n'
             '            },\n'
             '            "isInHandEffect": true,\n'
             '            "successChanceEq": "1",\n'
             '            "turnCount": 1,\n'
             '            "type": 25\n'
             '        },\n'
             '        "subEffect2": {\n'
             '            "affectsTarget": true,\n'
             '            "effectAmount": {\n'
             '                "eqTemplate": "0.75+0.25*pow({x}/99.0,0.5)",\n'
             '                "type": 2\n'
             '            },\n'
             '            "isInHandEffect": true,\n'
             '            "successChanceEq": "1",\n'
             '            "turnCount": 1,\n'
             '            "type": 26\n'
             '        },\n'
             '        "subEffect3": {\n'
             '            "affectsTarget": true,\n'
             '            "effectAmount": {\n'
             '                "eqTemplate": "0.75+0.25*pow({x}/99.0,0.5)",\n'
             '                "type": 2\n'
             '            },\n'
             '            "isInHandEffect": true,\n'
             '            "successChanceEq": "1",\n'
             '            "turnCount": 1,\n'
             '            "type": 27\n'
             '        },\n'
             '        "tier": 5\n'
             '    }\n]', 1),
        ],
        "verify": [("lightning_mastery",
                    '"eqTemplate": "0.75+0.25*pow({x}/99.0,0.5)"')],
        "also": (
            # the recipe itself: 2x Lightning Action at level 6 and 1x Taichi
            # Master at level 4 (both mid-level, so it is a real decision to
            # break them up), priced between Meteor Shower and Pek Shirt
            [{"asset": "cardAlchemyRecipes",
              "ops": [("replace",
                       '        "stardustCost": 20000\n    }\n]',
                       '        "stardustCost": 20000\n    },\n'
                       '    {\n'
                       '        "biomassCost": 20000,\n'
                       '        "cardId": "lightning_mastery",\n'
                       '        "fromCardIdLevelCountStrings": [\n'
                       '            "lightning_action-5/2",\n'
                       '            "taichi_master-3/1"\n'
                       '        ],\n'
                       '        "id": "lightning_mastery:1",\n'
                       '        "rollCurve": 4,\n'
                       '        "stardustCost": 50000\n'
                       '    }\n]', 1)]}]
            # and its name and description, English in every language for the
            # same reason the loc-fallback patch uses English: a readable
            # sentence beats "cards.lightning_mastery.name" on screen
            + [{"asset": "localisation_" + lang,
                "ops": [("insert_entry", "lightning_action", "after", _loc_entry(
                    "lightning_mastery", "Lightning Mastery",
                    "Speeds up the battle far less than Lightning Action does, "
                    "but barely weakens your stats - and at max level it does "
                    "not weaken them at all. (Casting it will remove the "
                    "effects)"))]}
               for lang in ("en-US", "de-GE", "es-ES", "ja-JP", "pl-PL",
                            "th-TH", "tr-TR", "zh-CN")]
        ),
    },
]

# ---------------------------------------------------------------------------
# Code patches - these are fundamentally different from the data patches
# above: they change the game's CODE at launch, not its data tables. They
# need the BepInEx mod loader (bundled with this mod, from
# https://github.com/BepInEx/BepInEx), which is installed into the game
# folder automatically when at least one code patch is selected and removed
# again when none are. The game files themselves are never modified by these;
# uninstalling = deleting the BepInEx files.
# ---------------------------------------------------------------------------
CODE_PATCHES = [
    {
        "key": "smooth-damage",
        "title": "Smooth damage formula (fixes zero-or-oneshot combat)",
        "desc": (
            "The base game calculates every hit as attack MINUS defense, so\n"
            "   any hit is either a one-shot or exactly 0 - defense and healing\n"
            "   are useless until they suddenly make you immortal. This changes\n"
            "   the rule to: damage = atk x atk / (atk + def). Defense always\n"
            "   reduces damage by a percentage and never to zero (defense equal\n"
            "   to the attack halves it). Applies to you AND enemies."
        ),
        "default": True,
        "plugin": "SmoothDamage.dll",
    },
    {
        "key": "offline-fix",
        "title": "Bugfix: save refuses to load (drops you into the tutorial)",
        "desc": (
            "If you own any of the three beta/contributor medal cards, loading\n"
            "   a save can dump you into the tutorial with your dark matter\n"
            "   showing as 0. The game tries to upload your medal 'roles' to the\n"
            "   developer's server, which the community build cannot reach, and\n"
            "   the error aborts the rest of the load. Worse, the autosave then\n"
            "   writes that half-loaded state back, so every later load breaks\n"
            "   the same way. This skips the upload - there is nothing to upload\n"
            "   to offline - and lets the load finish normally. The same file\n"
            "   carries the offline time limit below and the Time away line on\n"
            "   the Welcome Back panel, so turning this off takes those too."
        ),
        "default": True,
        "plugin": "OfflineFix.dll",
    },
    # Not a DLL of its own. It is one setting inside OfflineFix.dll, given its
    # own line because Fuzzied asked on 27.09.2026: "can we add in the installer
    # that if they dont want the offline change specifically we remove that
    # for them?" The offline limit is the one thing the patch takes away from
    # the base game, so it is the one thing a player may want to refuse
    # without losing the save fix it ships with. See apply_settings.
    {
        "key": "offline-cap",
        "title": "Offline time limit: one day, Big Bang upgrades raise it",
        "desc": (
            "The base game credits all the time you were away, however long.\n"
            "   This adds a limit: one day for free, and two cheap lines in the\n"
            "   Big Bang tree stretch it, up to sixteen weeks. Offline boss\n"
            "   kills follow the same limit. Turn this off to keep the base\n"
            "   game's unlimited offline time. The two Big Bang lines then lock,\n"
            "   since there is nothing for them to raise, and any levels you\n"
            "   already bought can still be taken back down for a refund. It\n"
            "   lives inside the save fix above, so it needs that one on."
        ),
        "default": True,
        "setting": {
            "needs": "offline-fix",
            "file": "sti.community.offlinefix.cfg",
            "section": "Offline cap",
            "name": "CapOfflineTime",
        },
    },
    {
        "key": "battle-fixes",
        "title": "Battle fixes: HP buffs fill up, 6-card hand fits on screen",
        "desc": (
            "Three small battle bugfixes. (1) In-hand HP buffs (Taichi Master,\n"
            "   The Bomb) raised your MAX HP but not your current HP, so the\n"
            "   buff only added empty health bar - current HP now scales with\n"
            "   it. (2) The battle hand was drawn for 5 cards; with the modded\n"
            "   6th hand slot the last card clipped into the Skip/Surrender\n"
            "   buttons - the hand now scales to fit however many cards you have.\n"
            "   (3) Biomass and stardust drops in the battle log printed raw\n"
            "   text like 'panels.storage.resources.biomass.name' instead of\n"
            "   the word. The game looks those two words up once, before its\n"
            "   own language file is ready, and keeps the failure for the whole\n"
            "   session - they are now checked and repaired at startup."
        ),
        "default": True,
        "plugin": "CommunityFixes.dll",
    },
    {
        "key": "no-discard",
        "title": "Remove the discard (trash can) zone from battles",
        "desc": (
            "Discarding a card costs your whole turn - the same as Skip,\n"
            "   except you also lose the card, and with the 6th hand slot the\n"
            "   discarded card is the only thing that can be redrawn so it comes\n"
            "   straight back anyway. This removes the trash-can drop zone from\n"
            "   the battle screen. Skip it if you want to keep vanilla discarding."
        ),
        "default": True,
        "plugin": "NoDiscard.dll",
    },
    {
        "key": "alchemy-qol",
        "title": "Alchemy forge: 'MAX' button, DE-LEVEL toggle, Perfect guard",
        "desc": (
            "Adds a MAX button under the forge's Synthesize button. One click\n"
            "   repeats the synthesis until biomass, stardust or ingredient\n"
            "   cards run out, then shows how many cards were made and the best\n"
            "   level rolled. Each craft uses the game's own costs and random\n"
            "   level roll - it just saves you hundreds of clicks.\n"
            "   Also adds a DE-LEVEL toggle: when ON, synthesis that is short of\n"
            "   ingredient cards splits your merged-up copies back down to the\n"
            "   level the recipe needs (1 card of level N -> 4 of level N-1, the\n"
            "   exact mirror of merging, so no card value is lost). Never touches\n"
            "   equipped or protected cards, always keeps one copy of your\n"
            "   highest level, and charges 10% of the split card's sell value\n"
            "   in biomass per split.\n"
            "   Finally, once a recipe has produced a Perfect (max-level) card,\n"
            "   both Synthesize and MAX refuse that recipe - there is no better\n"
            "   roll to chase, so your resources stay in the bank."
        ),
        "default": True,
        "plugin": "AlchemyQoL.dll",
    },
    {
        "key": "more-loadouts",
        "title": "More deck & permanent-set loadouts (up to 10 each)",
        "desc": (
            "The game hardcodes 2 saved loadouts each for the battle deck and\n"
            "   the permanent card set, but every save already has room for 100.\n"
            "   This unlocks one extra loadout slot per planet you reach beyond\n"
            "   Venus (Mercury: 3 ... Mars: 5 ... Pluto: 10). Slots appear\n"
            "   automatically as you travel; saves are untouched.\n"
            "   It also keeps the dropdown that lists them on screen. That\n"
            "   popup was drawn for three lines, so a longer list either\n"
            "   ran off the panel and got cut away or had nothing to\n"
            "   scroll with. One that already fits is left alone.\n"
            "   A slot you have put cards in or given a name of your own\n"
            "   stays in the list after a Big Bang, instead of hiding\n"
            "   until you have flown back out to the planet that unlocked\n"
            "   it. And the dropdown no longer snaps shut every time a\n"
            "   research or infrastructure level finishes. Loadouts also\n"
            "   keep their cards when you level those cards up, instead of\n"
            "   quietly dropping the ones whose level has changed."
        ),
        "default": True,
        "plugin": "LoadoutPlus.dll",
    },
    {
        "key": "ui-fixes",
        "title": "UI fixes: the Big Bang window, and the message log",
        "desc": (
            "On the Big Bang upgrades tab, the dark-matter total is drawn on\n"
            "   top of the bottom-left upgrade item. This parks the total in\n"
            "   the panel's bottom-left corner and resizes the upgrade list to\n"
            "   fill everything above it, so every upgrade is fully visible and\n"
            "   clickable and the freed space is actually used. Measured at\n"
            "   runtime, and reverted when you leave the tab.\n"
            "   Also fixes the vanilla bug where the game slows to a crawl and\n"
            "   stops registering clicks once your research and infrastructure\n"
            "   are fast: every level writes a message, the game ticks ten\n"
            "   times a second, and each message rebuilds the whole log's\n"
            "   layout. Level up messages are now folded together and the log\n"
            "   is capped, so it cannot grow without limit.\n"
            "   And it makes a previewed loadout read only. Selecting a\n"
            "   saved loadout shows you a copy of it, but clicking a card\n"
            "   in that copy used to take the card out of your live set\n"
            "   instead, with nothing moving on screen to tell you. The row\n"
            "   now greys out like the bag beside it already did.\n"
            "   And the game window opens where you last left it, instead\n"
            "   of in the same default spot every launch."
        ),
        "default": True,
        "plugin": "UIFixes.dll",
        # Moves the window the moment it appears, seconds before any plugin
        # can. See the top of plugin/UIFixesEarlyWindow.cs.
        "patcher": "UIFixesEarlyWindow.dll",
    },
    {
        "key": "dev-console",
        "title": "Hide the developer error console",
        "desc": (
            "On the beta and beta-danger branches, and on every Mac, the game\n"
            "   is a Unity DEVELOPMENT build. There any engine error throws a\n"
            "   black 'Development Console' box across the middle of the screen\n"
            "   with red text on it. The usual trigger is nothing to do with the\n"
            "   game: plug in a headset or switch your sound device while playing\n"
            "   and the audio engine complains. It cannot be dismissed for good.\n"
            "   It returns on the next error.\n"
            "   This adds a 'Hide error console' switch to the game's own\n"
            "   settings, on by default, that keeps the box off the screen.\n"
            "   On the normal Windows branch the box never appears, so there\n"
            "   this does nothing and costs nothing.\n"
            "   Note: the small 'Development Build' watermark in the corner is\n"
            "   drawn by the engine itself and cannot be removed this way."
        ),
        "default": True,
        "plugin": "DevConsoleOff.dll",
    },
    {
        "key": "scroll-keeper",
        "title": "Stop lists jumping back to the top when research completes",
        "desc": (
            "Vanilla bug. Finishing a research level makes the game rebuild the\n"
            "   card loadouts, the research/infra lists, the star strip and the\n"
            "   destination selector. While a list is being rebuilt it is empty\n"
            "   for a frame, so Unity snaps it back to the top - and there it\n"
            "   stays. In the late game research finishes constantly, so long\n"
            "   lists become almost unreadable. This remembers where every list\n"
            "   was and puts it back once the rebuild is done. It lets go the\n"
            "   instant you touch the wheel or drag, so it never fights you."
        ),
        "default": True,
        "plugin": "ScrollKeeper.dll",
    },
    {
        "key": "tooltip-time",
        "title": "Choose how long tooltips stay on screen",
        "desc": (
            "The pop-up that explains an upgrade, a card or a research is\n"
            "   hardcoded to vanish after five seconds, whether or not you are\n"
            "   still pointing at it - long descriptions simply cannot be read\n"
            "   in that time. This adds a 'Tooltip time' row to the game's own\n"
            "   settings; click it to cycle 5, 10, 15, 20 or 30 seconds. It is\n"
            "   capped at 30 so a tooltip can never get stuck on screen. It\n"
            "   also fixes a smaller vanilla annoyance: moving onto a second\n"
            "   thing used to inherit what was left of the first one's five\n"
            "   seconds, so the second pop-up could vanish almost at once."
        ),
        "default": True,
        "plugin": "TooltipTime.dll",
    },
    {
        "key": "hotkeys",
        "title": "Keyboard shortcuts",
        "desc": (
            "The game uses almost no keys at all, so nearly everything you do\n"
            "   often needs the mouse. This adds shortcuts for the things that\n"
            "   get repeated: up/down walk the left-hand menu and left/right\n"
            "   walk the tabs inside a panel, the number keys jump straight to\n"
            "   a combat zone (Shift for zones 11-20), Ctrl and a number loads\n"
            "   a deck loadout, and there are keys for auto levelling, auto\n"
            "   fight, zone level up/down, merge all, the travel destination\n"
            "   and a quick save. Press F1 in game for the full list. Every\n"
            "   key can be changed in BepInEx\\config\\sti.community.hotkeys.cfg,\n"
            "   and the shortcuts are ignored while you are typing in a name\n"
            "   or filter box. Departing and quick loading need the key pressed\n"
            "   twice, so neither can happen by accident. The keys that\n"
            "   change which combat zone you are in only work while the Combat\n"
            "   panel is open, so a stray number key cannot drop you into a\n"
            "   boss zone from another screen; there is a Settings toggle for\n"
            "   that if you want them everywhere."
        ),
        "default": True,
        "plugin": "Hotkeys.dll",
    },
    {
        "key": "bigbang-extras",
        "title": "Big Bang gate, Star Essence, and travel achievements",
        "desc": (
            "Three things. First, the extra upgrade tiers this mod adds\n"
            "   (level 11 and up on the original lines, and the five brand\n"
            "   new bonus lines) stay locked until you have reached Uranus,\n"
            "   so they are a late-game reward rather than something you buy\n"
            "   on your first run; the Upgrade button and its pop-up say so\n"
            "   while they are locked, and anything you already own keeps\n"
            "   working. Reaching Uranus once unlocks them for good, since a\n"
            "   Big Bang sends you back to Earth. Second, the Star Essence\n"
            "   multiplier used to apply only to essence you earned after\n"
            "   buying it, which made it nearly worthless because most\n"
            "   essence comes from achievements you collect once. Now the\n"
            "   essence you are already holding is scaled too. Third, the\n"
            "   travel achievements go by the furthest star you have ever\n"
            "   reached rather than the furthest since your last Big Bang,\n"
            "   so flying out to Neptune and resetting before you press\n"
            "   Claim no longer loses the reward. All three can be\n"
            "   turned off in\n"
            "   BepInEx\\config\\sti.community.bigbangextras.cfg."
        ),
        "default": True,
        "plugin": "BigBangExtras.dll",
    },
    {
        "key": "planetary-memory",
        "title": "Planetary Memory: buildings that keep paying after you leave",
        "desc": (
            "Buildings only count while you are standing on that planet. Fly\n"
            "   on and a hundred levels of Mercury solar panels stop paying\n"
            "   you completely. Research never worked that way, and that is\n"
            "   the thing that makes a long infrastructure grind feel wasted.\n"
            "   This runs the 'Planetary Memory' Big Bang upgrade, which lets\n"
            "   you keep a share of what you built everywhere else. Each\n"
            "   level keeps 2% more, up to 30% at level 15.\n"
            "   For each kind of bonus it takes your single best planet, not\n"
            "   all of them added together, so this can never hand you more\n"
            "   than you would get by flying there. A x150 energy planet at\n"
            "   30% carries x4.50 with you, and the planet itself is still\n"
            "   worth 33 times that, so where you park still matters.\n"
            "   The planet you are standing on is left out on purpose, since\n"
            "   it is already paying you in full. Station and headquarters\n"
            "   buildings are left out too: there are nine of them, they set\n"
            "   the floor your output decays to, and carrying even a little\n"
            "   of that around would pin every planet at full efficiency\n"
            "   forever.\n"
            "   Needs the 'more Big Bang upgrades' data patch above, since\n"
            "   without it the upgrade does not exist. On its own it changes\n"
            "   nothing. Can be turned off in\n"
            "   BepInEx\\config\\sti.community.planetarymemory.cfg."
        ),
        "default": True,
        "plugin": "PlanetaryMemory.dll",
    },
    {
        "key": "formula-cache",
        "title": "Performance: the stutter while your fleet fights",
        "desc": (
            "Late in the game the screen hitches every second or so, worse\n"
            "   with many jobs running. The cause is the drop roll: every\n"
            "   enemy you kill makes the game read every card's drop chance\n"
            "   from text and work it out again from scratch, which throws\n"
            "   away about 10 MB of memory per kill. Cleaning that up is\n"
            "   the hitch you see. This remembers each answer the first\n"
            "   time it is worked out and hands it back after that, and\n"
            "   lets the text reader keep the patterns it uses instead of\n"
            "   rebuilding them. Drop chances, enemy stats and bonuses come\n"
            "   out exactly the same; it was checked against every battle\n"
            "   zone at every level. Can be turned off in\n"
            "   BepInEx\\config\\sti.community.formulacache.cfg."
        ),
        "default": True,
        "plugin": "FormulaCache.dll",
    },
    {
        "key": "filter-memory",
        "title": "Bugfix: filters forget themselves on load",
        "desc": (
            "Under Techno, the Completion / Cost / Bonus filters on the\n"
            "   Research and Infrastructure tabs reset to 'All' every time\n"
            "   you start the game, so if you play with Completion set to\n"
            "   'Not Max Level' to see what is left to do, you have to set\n"
            "   it again every session. The base game does save your\n"
            "   choice - it is reading it back that fails - so this just\n"
            "   re-applies what your save already contains, a moment after\n"
            "   the save has finished loading.\n"
            "   The Effect and Rarity filters on the Deck and Permanent\n"
            "   card tabs have the same bug and get the same fix. Their\n"
            "   Tier filter is worse off - the base game never saves it at\n"
            "   all - so this also starts saving it, into two fields your\n"
            "   save file has always carried unused. Can be turned off in\n"
            "   BepInEx\\config\\sti.community.filtermemory.cfg."
        ),
        "default": True,
        "plugin": "FilterMemory.dll",
    },
    {
        "key": "auto-start",
        "title": "Start a planet up by itself",
        "desc": (
            "A trip to one of the outer planets takes real days, and the base\n"
            "   game lands you with every research, every building and every\n"
            "   collector switched off - so the flight is followed by a long\n"
            "   manual setup pass before anything starts earning again. This\n"
            "   starts them for you the moment you land, including when you\n"
            "   landed while the game was closed.\n"
            "   It covers the other three moments the game leaves you\n"
            "   switched off too: setting off, since the spaceship has its\n"
            "   own research, buildings and collectors and a flight is the\n"
            "   longest idle stretch in the game; a Big Bang, including the\n"
            "   very reset you buy the upgrade on, from the moment you press\n"
            "   Confirm on the upgrades; and anything that unlocks\n"
            "   later, which starts on its own as it appears rather than\n"
            "   waiting for your next flight.\n"
            "   How much it does is bought in the Big Bang tree, on the new\n"
            "   'Automated Arrival' line, so it is earned rather than free:\n"
            "     Level 1 - start every research, building and collector\n"
            "               that is unlocked, unfinished and not already\n"
            "               running\n"
            "     Level 2 - also leave alone any building whose running cost\n"
            "               your energy income cannot cover, so landing\n"
            "               somewhere can never put you into a deficit\n"
            "     Level 3 - also order the new jobs, putting the ones that\n"
            "               unlock new resources, buildings and research\n"
            "               first, and keep the priority list feeding\n"
            "               downwards: energy production at the top, then\n"
            "               resource production, then research and\n"
            "               buildings, with the engine right at the\n"
            "               bottom. Accumulation Layers stay where you put\n"
            "               them and nothing crosses one except an engine\n"
            "               module, which always goes below them all\n"
            "     Level 4 - also carry the whole priority list through a Big\n"
            "               Bang: the Accumulation Layers, the limits you\n"
            "               set on them, and which research, buildings and\n"
            "               collectors sat under which layer. The layers\n"
            "               come back straight away and everything else\n"
            "               returns to its old place as it becomes\n"
            "               available again. Anything you drag afterwards\n"
            "               becomes the new arrangement. The record itself is\n"
            "               kept from the moment you install this, at any\n"
            "               level and with the toggle either way, in a text\n"
            "               file next to your save - so you can Big Bang now,\n"
            "               buy the level whenever you can afford it, and the\n"
            "               arrangement you had is still there\n"
            "   Want the fuel tank to fill itself? Turn on 'Fill the\n"
            "   tank' in Settings - it is the one switch that ships\n"
            "   off, because a filling tank eats your whole energy bank until\n"
            "   it is full. Put an Accumulation Layer above the modules first\n"
            "   and the layer holds them back.\n"
            "   Landing somewhere switches every engine module off and\n"
            "   forgets what you had them set to - that is the base game. The\n"
            "   mod now puts them back at the percentage you chose, and with\n"
            "   level 3 they sit below your Accumulation Layers so the tank\n"
            "   refills out of what everything else has finished with.\n"
            "   Each level you own adds its own toggle to the Settings\n"
            "   panel, under 'Auto start' in the left hand column, so you can\n"
            "   keep the upgrade and still switch any part of it off. Hover\n"
            "   over a toggle to see what it does. A level you have not\n"
            "   bought has no toggle.\n"
            "   It never touches a job that is already running, never moves\n"
            "   anything you put in order yourself. A job it starts for the\n"
            "   first time goes into the main list rather than below your\n"
            "   Accumulation Layers, where the game itself would put it.\n"
            "   A building you switched off stays off.\n"
            "   Because it keeps watching rather than firing once, it\n"
            "   also never restarts something it has already started - turn a\n"
            "   job off and it stays off. That slate is wiped when you\n"
            "   arrive, set off or Big Bang, so each of those still gets a\n"
            "   clean full pass. Needs the 'more Big Bang upgrades' data patch above for\n"
            "   the upgrade line to exist; without it you can still turn the\n"
            "   feature on by hand with the Level setting in\n"
            "   BepInEx\\config\\sti.community.autostart.cfg.\n"
            "   The spaceship's own research, buildings and collectors are\n"
            "   kept at the bottom of the priority list, just above your\n"
            "   engine modules, with the four multi-day ones below the rest -\n"
            "   the spaceship is the one place you never leave, so its jobs\n"
            "   would otherwise take energy and resources off the planet you\n"
            "   are trying to finish. And BLACKHOLE is never started for you:\n"
            "   it costs 1e25 energy and does nothing yet."
        ),
        "default": True,
        "plugin": "AutoStart.dll",
    },
    {
        "key": "top-off",
        "title": "Collectors fill the tank right to the top",
        "desc": (
            "Before a resource collector is allowed to run, the game checks\n"
            "   whether there is room for a whole BAR's output - not a tick's -\n"
            "   so a collector goes quiet up to a full bar short of your\n"
            "   discard/lock line and parks there. Being turned away also resets\n"
            "   its progress bar to zero, so every tick it waits it throws away\n"
            "   energy you already spent.\n"
            "   Collectors now run until the tank is actually at the line; a\n"
            "   delivery that does not fit is trimmed to the room available\n"
            "   rather than discarded; and whatever was not delivered stays on\n"
            "   the bar. Sitting on a full tank costs nothing, and spending\n"
            "   something picks the collector up where it paused. Offline\n"
            "   catch-up and energy plants are untouched. There is a toggle\n"
            "   for it in the game's own Settings panel, and the same switch\n"
            "   in BepInEx\\config\\sti.community.topoff.cfg."
        ),
        "default": True,
        "plugin": "TopOff.dll",
    },
    {
        "key": "bigbang-icons",
        "title": "Icons for the new Big Bang upgrades",
        "desc": (
            "The 12 new upgrade lines have no artwork in the base game, so\n"
            "   they all show the same grey 'no image' placeholder. This gives\n"
            "   each of them its own icon, drawn in the game's own style.\n"
            "   The icons are ordinary PNG files in\n"
            "   BepInEx\\plugins\\bigbang_icons, named after the upgrade, so\n"
            "   you can replace any of them with your own - and dropping in a\n"
            "   file named after one of the game's own upgrades replaces that\n"
            "   one too.\n"
            "   Needs the 'more Big Bang upgrades' data patch above, since\n"
            "   without it those upgrades do not exist. On its own it changes\n"
            "   nothing."
        ),
        "default": True,
        "plugin": "BigBangIcons.dll",
        "assets": "bigbang_icons",
    },
    {
        "key": "card-icons",
        "title": "Artwork for cards that never had any",
        "desc": (
            "Storm Core has no picture in the base game - it is a real card\n"
            "   that really drops, and out of all 52 cards it is the only one\n"
            "   whose artwork was never drawn, so it shows as an empty frame.\n"
            "   This gives it one, in the game's own card style, and does the\n"
            "   same for Lightning Mastery, which had been borrowing Lightning\n"
            "   Action's picture.\n"
            "   The pictures are ordinary PNG files in\n"
            "   BepInEx\\plugins\\card_icons, named after the card, so you can\n"
            "   replace them with your own - and dropping in a file named\n"
            "   after one of the game's own cards replaces that one too.\n"
            "   Needs nothing else installed. A card with no file keeps the\n"
            "   picture it has today."
        ),
        "default": True,
        "plugin": "CardIcons.dll",
        "assets": "card_icons",
    },
    {
        "key": "cargo-bay",
        "title": "Cargo you can actually fly with (three Big Bang lines)",
        "desc": (
            "In the base game your ship's hull weighs a flat 100 and a full\n"
            "   cargo hold weighs millions, so flying with cargo makes a\n"
            "   journey take thousands of times as long. Nothing in\n"
            "   the game tells you that. It also means every level of the\n"
            "   Resource Storage upgrade makes your ship slower, since the\n"
            "   hold's weight comes straight from its cap.\n"
            "   This adds three Big Bang lines that each attack that from a\n"
            "   different side.\n"
            "   Ballast Trim: 10 levels. Weighs your cargo as a share of a\n"
            "   full hold instead. At level 1 a full hold costs 89x the travel\n"
            "   time, at level 10 it costs 1.7x, and half a load costs half as\n"
            "   much again. Storage upgrades stop slowing you down at all.\n"
            "   Cargo Bay Doors: 2 levels. Throws the hold overboard when you\n"
            "   leave and keeps it shut for the whole flight, so the air and\n"
            "   biomass the empty space between stars produces cannot trickle\n"
            "   back in and weigh you down. Level 2 keeps whatever each\n"
            "   resource's lock slider protects, so you can dump the metal and\n"
            "   keep the biomass your Space Research needs. A second switch\n"
            "   keeps exactly what the planet you are flying to recommends\n"
            "   you bring, and never throws out biomass while it is on. Only\n"
            "   the Moon, Venus and Mercury recommend anything in the base\n"
            "   game, so anywhere else it behaves the same as leaving it off.\n"
            "   Both switches are in Settings and on the travel tab, and both\n"
            "   are off until you turn them on.\n"
            "   Quantum Pocket: 15 levels. A weightless pocket that catches\n"
            "   everything you earn while travelling. Research and\n"
            "   infrastructure can spend straight out of it in flight and it\n"
            "   never slows you down. It empties into your hold when you land.\n"
            "   It also writes readable text on the upgrade cards that buy no\n"
            "   bonus, since the game gives all of them the same label.\n"
            "   Needs the 'more Big Bang upgrades' data patch above. Without\n"
            "   the lines bought it changes nothing."
        ),
        "default": True,
        "plugin": "CargoBay.dll",
    },
    # Not ours. SpaceTravelIdleUnlocker (STIU) by Berserker, MIT licensed,
    # https://github.com/Berserker66/STIU. Every balance number in 0.1 was
    # measured with it loaded and Fuzzied's settings below, which nobody knew
    # until 27.09.2026. Measured without it on the same saves: every high
    # level fight lost, trips twice as long, no Big Bang payout, a third of
    # the drops (docs/todo.md). Fuzzied, 28.09.2026: "Include an option in the setup file
    # with Balancer and another option with the settings I have used", and
    # "Bundle it and in 0.2 make our own version of it that handles it".
    #
    # The DLL is Fuzzied's own copy, byte for byte, not the GitHub 1.0 release:
    # that one is an older build, and the newest source adds extra equip
    # slots that would meet our loadout and hand slot changes. A copy the
    # player already had (the community build can carry one) is kept aside
    # the first time and put back by "restore the original game".
    {
        "key": "balancer",
        "title": "Balancer: the Unlocker mod by Berserker (the patch is tuned with it)",
        "desc": (
            "A small mod by Berserker that lets you set the game's bonuses\n"
            "   in a settings file. I tuned this whole patch with it on.\n"
            "   Without it and my settings, I lost every high level fight,\n"
            "   trips took twice as long, and Big Bang paid nothing unless I\n"
            "   beat my best score. Version 0.2 will be balanced for the\n"
            "   game without it.\n"
            "   On its own default settings it changes nothing except the\n"
            "   Big Bang, which then pays your whole score every time. The\n"
            "   next line holds the settings I used.\n"
            "   Not my work, shared under its MIT licence, which is copied\n"
            "   next to it. Source: https://github.com/Berserker66/STIU\n"
            "   If you already had it, your own copy is kept aside and\n"
            "   comes back when you restore the original game."
        ),
        "default": True,
        "plugin": "SpaceTravelIdleUnlocker.dll",
        "plugin_folder": os.path.join("plugins", "STIU"),
        "licence": "STIU-LICENSE.txt",
    },
    # Fuzzied's STIU.cfg as it was on 27.09.2026, every value in it, so the
    # player gets exactly the game the numbers in PATCH_NOTES came from.
    # Only the values are written; the Unlocker adds its own comments the
    # first time the game starts. See apply_cfg_values.
    {
        "key": "balancer-settings",
        "title": "Fuzzied's Balancer settings (the ones this patch was tuned with)",
        "desc": (
            "The settings I played and tuned the patch with. Compared with\n"
            "   the Unlocker's defaults: fleet attack, defense and HP x2,\n"
            "   drop chance x3, stardust x5, biomass x3, water and iron x2,\n"
            "   energy and resources x1.2 with twice the storage, research,\n"
            "   infrastructure and engine x2, building and production costs\n"
            "   halved, and a bigger Big Bang payout (factor 0.9 instead of\n"
            "   0.35). It also hands out the three beta medal cards.\n"
            "   Your own settings file is kept aside and comes back if you\n"
            "   turn this off. Needs the Balancer line above."
        ),
        "default": True,
        "cfg_values": {
            "needs": "balancer",
            "file": "STIU.cfg",
            "values": [
                ("Bonus", "MulerDeteriorationSlowdown", "1"),
                ("Bonus", "MulerDeteriorationMinBorderUp", "1"),
                ("Bonus", "MulprodSpeed", "1"),
                ("Bonus", "MulerTank", "1"),
                ("Bonus", "MulenergyGain", "1.2"),
                ("Bonus", "MulenergyTank", "2"),
                ("Bonus", "MuleProdSpeed", "1"),
                ("Bonus", "MulstardustGain", "5"),
                ("Bonus", "MulresourceGain", "1.2"),
                ("Bonus", "MulresourceTank", "2"),
                ("Bonus", "MulrProdSpeed", "1"),
                ("Bonus", "MulairGain", "1"),
                ("Bonus", "MulwaterGain", "2"),
                ("Bonus", "MulsoilGain", "1"),
                ("Bonus", "MulbiomassGain", "3"),
                ("Bonus", "MulcoalGain", "1"),
                ("Bonus", "MulsiliconGain", "1"),
                ("Bonus", "MulironGain", "2"),
                ("Bonus", "MulcardAlchemyRollCurve", "1"),
                ("Bonus", "MuldropChance", "3"),
                ("Bonus", "MulpermAtk", "2"),
                ("Bonus", "MulpermDef", "2"),
                ("Bonus", "MulbaseHP", "2"),
                ("Bonus", "MulspaceFolding", "1.1"),
                ("Bonus", "MultrajOpti", "1.5"),
                ("Bonus", "MulspaceshipDieting", "1.5"),
                ("Bonus", "MulpathKnowledge", "1"),
                ("Bonus", "MulengineCap", "2"),
                ("Bonus", "MulengineSpeed", "2"),
                ("Bonus", "MulresearchSpeed", "2"),
                ("Bonus", "MulinfraSpeed", "2"),
                ("Bonus", "MulinfraCostReduction", "0.5"),
                ("Bonus", "MulprodEnergyCostReduction", "0.5"),
                ("Bonus", "MulprodResourceCostReduction", "1"),
                ("Bonus", "AddpermAtk", "0"),
                ("Bonus", "AddpermDef", "0"),
                ("Bonus", "AddbaseHP", "0"),
                ("Bonus", "AddenemyGen", "0"),
                ("Bonus", "AddplayerMove", "0"),
                ("Bonus", "AddenemyMove", "0"),
                ("Bonus", "AddtravelTimeMaxLimit", "0"),
                ("Bonus", "AddcardAlchemyRollCurve", "0"),
                ("DarkMatter", "Exponent", "0.9"),
                ("DarkMatter", "Factor", "0.9"),
                ("General", "ResourceGainMultiplier", "1"),
                ("General", "GrantBetaCards", "true"),
            ],
        },
    },
]

GAME_FOLDER_NAME = "Space Travel Idle"
# Steam's own id for the game. An appmanifest under this name is Steam
# telling us the folder outright, which beats any guess we could make.
STEAM_APP_ID = "1407860"
# Words that mean "this is somebody's spare copy, not the install they
# play". A backup has the same files inside it as the real thing, so
# nothing in the bytes can tell them apart and offering one in the menu is
# how a player patches the wrong folder and wonders why nothing changed.
COPY_WORDS = ("backup", "copy", "old", "bak", "orig", "kopi")


def clean_path(path):
    """One spelling per folder, so the same place cannot be listed twice.

    The registry hands back whatever case the writer used, so the same Steam
    folder arrives as both c:\\games\\steam and C:\\Games\\Steam. For a folder
    that exists, Windows can be asked how it is really spelled, which is
    what a player expects to see in the menu.
    """
    path = os.path.normpath(path)
    if not IS_MAC:
        try:
            import pathlib
            real = str(pathlib.Path(path).resolve())
            if os.path.isdir(real):
                path = real
        except Exception:
            pass
    # Whether or not that worked, the drive letter is ours to tidy.
    if len(path) > 1 and path[1] == ":":
        path = path[0].upper() + path[1:]
    return path


def path_key(path):
    """What counts as "the same folder" for dedupe. Windows ignores case."""
    return os.path.normcase(os.path.abspath(path))


def add_unique(out, seen, path):
    """Append path to out if that folder is not already in it."""
    if not path:
        return
    path = clean_path(path)
    key = path_key(path)
    if key in seen:
        return
    seen.add(key)
    out.append(path)


def registry_steam_roots():
    """Where Windows itself says Steam is.

    This is the only answer that is not a guess. Steam writes its own
    location on install, so it is right for a default install and right for
    someone who put Steam on D:\\ or in a folder of their own. Everything
    below this function is fallback for when the registry cannot be read.
    """
    if IS_MAC:
        return []
    try:
        import winreg
    except ImportError:
        return []
    out, seen = [], set()
    keys = [
        (winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam", "SteamPath"),
        (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Valve\Steam", "InstallPath"),
        (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Valve\Steam",
         "InstallPath"),
    ]
    for hive, sub, name in keys:
        try:
            with winreg.OpenKey(hive, sub) as key:
                value = winreg.QueryValueEx(key, name)[0]
        except OSError:
            continue
        # HKCU writes this with forward slashes and usually in lower case,
        # HKLM with backslashes. Same folder, two spellings, so it has to be
        # deduped on a normalised key or everything gets listed twice.
        add_unique(out, seen, value)
    return out


def fixed_drives():
    """Drive letters that are real local disks.

    Asked of Windows rather than guessed, because probing a letter that
    happens to be an empty card reader or a disconnected network share can
    hang for seconds or pop a dialog at the player.
    """
    if IS_MAC:
        return []
    try:
        import ctypes
        kernel32 = ctypes.windll.kernel32
        mask = kernel32.GetLogicalDrives()
    except Exception:
        return ["C:\\"]
    drive_fixed = 3
    out = []
    for i in range(26):
        if not (mask >> i) & 1:
            continue
        root = chr(ord("A") + i) + ":\\"
        try:
            if kernel32.GetDriveTypeW(root) == drive_fixed:
                out.append(root)
        except Exception:
            continue
    return out or ["C:\\"]


def steam_roots():
    """Everywhere Steam itself might be, best answer first.

    The registry first, then the spots Steam and people actually use, on
    every fixed disk rather than only C:. These are guesses and most of them
    will not exist; that costs nothing, because every path this produces is
    checked for the game before it is offered to anyone.
    """
    if IS_MAC:
        return [os.path.expanduser("~/Library/Application Support/Steam")]
    out, seen = [], set()
    for path in registry_steam_roots():
        add_unique(out, seen, path)
    tails = (r"Program Files (x86)\Steam",  # the Windows default
             r"Program Files\Steam",
             "Steam",
             "SteamLibrary",
             r"Games\Steam")
    for drive in fixed_drives():
        for tail in tails:
            add_unique(out, seen, os.path.join(drive, tail))
    return out


def steam_library_dirs():
    """Extra Steam libraries, read out of libraryfolders.vdf.

    Steam keeps one library per drive and the game can sit in any of them,
    so the install paths above miss anyone with a second disk or an
    external one, which is common enough on a Mac. The file is a plain
    key/value text format and all we want are the "path" lines. Anything
    unreadable just means we fall back to the paths above.
    """
    libs, seen = [], set()
    for root in steam_roots():
        for vdf in (os.path.join(root, "steamapps", "libraryfolders.vdf"),
                    os.path.join(root, "config", "libraryfolders.vdf")):
            if not os.path.isfile(vdf):
                continue
            try:
                with open(vdf, encoding="utf-8", errors="replace") as fh:
                    for line in fh:
                        line = line.strip()
                        if not line.startswith('"path"'):
                            continue
                        parts = line.split('"')
                        if len(parts) >= 4:
                            add_unique(libs, seen,
                                       parts[3].replace("\\\\", "\\"))
            except OSError:
                continue
    return libs


def manifest_install_dir(steamapps):
    """The folder Steam's own appmanifest names, or None.

    Steam records the folder it installed into, so this finds the game even
    when the folder is not called what we expect, which is what happens on a
    Beta or Beta Danger branch.
    """
    acf = os.path.join(steamapps, "appmanifest_%s.acf" % STEAM_APP_ID)
    if not os.path.isfile(acf):
        return None
    try:
        with open(acf, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                line = line.strip()
                if not line.startswith('"installdir"'):
                    continue
                parts = line.split('"')
                if len(parts) >= 4:
                    return parts[3].replace("\\\\", "\\")
    except OSError:
        return None
    return None


def looks_like_a_spare_copy(name):
    """True for folder names like "Space Travel Idle - Backup".

    A backup holds the same files as the real install, so is_game_dir says
    yes to it just as readily. Nothing in the bytes can tell them apart,
    which makes a backup in the menu a way to patch the wrong folder and
    then wonder why the game did not change.
    """
    cleaned = "".join(c if c.isalnum() else " " for c in name.lower())
    return any(word in COPY_WORDS for word in cleaned.split())


def candidate_dirs():
    """Folders to offer in the menu, best guess first.

    Three passes per Steam library, cheapest and most certain first: what
    Steam's own manifest says, then the folder name we expect, then anything
    under steamapps\\common that looks like this game. Anything typed in by
    hand still works too, so an install none of this finds is not a dead end.
    """
    out, seen = [], set()

    def add(path):
        add_unique(out, seen, path)

    for root in steam_roots() + steam_library_dirs():
        steamapps = os.path.join(root, "steamapps")
        common = os.path.join(steamapps, "common")

        named = manifest_install_dir(steamapps)
        if named:
            add(os.path.join(common, named))

        add(os.path.join(common, GAME_FOLDER_NAME))

        # Last pass, for an install sitting under a name of its own, such as
        # a branch folder. Only worth the listing if the library is there.
        if not os.path.isdir(common):
            continue
        try:
            names = sorted(os.listdir(common))
        except OSError:
            continue
        for name in names:
            if "space travel idle" not in name.lower():
                continue
            if looks_like_a_spare_copy(name):
                continue
            add(os.path.join(common, name))

    return out


class PatchError(Exception):
    pass


def app_bundle(game_dir):
    """macOS: the name of the .app folder inside game_dir, or None.

    Looked up rather than assumed. The bundle name is the game's to choose
    and a Steam depot can rename it between builds; what identifies it is
    that the game data is inside.
    """
    try:
        names = sorted(os.listdir(game_dir))
    except OSError:
        return None
    for name in names:
        if not name.endswith(".app"):
            continue
        inner = os.path.join(game_dir, name, "Contents", "Resources",
                             "Data", "resources.assets")
        if os.path.isfile(inner):
            return name
    return None


def mac_executable_name(game_dir):
    """The binary inside the .app bundle, e.g. "SpaceTravelIdle".

    A bundle says its own name in Info.plist under CFBundleExecutable, and
    that is what macOS itself runs when you double click the icon. It is
    also exactly what run_bepinex.sh reads, so asking the same question
    keeps the two in step.

    This used to take the first file in Contents/MacOS in sorted order,
    which is right until anything else is in there. A dot sorts before a
    letter, and macOS writes a .DS_Store into any folder someone has opened
    in Finder, so a player who had once looked inside the bundle would have
    had the installer checking whether a process called ".DS_Store" was
    running. It never is, so the "close the game first" guard would have
    waved every install through while the game was open.
    """
    bundle = app_bundle(game_dir)
    if not bundle:
        return None
    contents = os.path.join(game_dir, bundle, "Contents")

    try:
        import plistlib
        with open(os.path.join(contents, "Info.plist"), "rb") as fh:
            name = plistlib.load(fh).get("CFBundleExecutable")
        if name and os.path.isfile(os.path.join(contents, "MacOS", name)):
            return name
    except Exception:
        pass

    # No readable Info.plist. Fall back to looking, but skip the dotfiles
    # that made the old version wrong, and prefer the name the bundle has.
    macos_dir = os.path.join(contents, "MacOS")
    try:
        names = [n for n in sorted(os.listdir(macos_dir))
                 if not n.startswith(".")
                 and os.path.isfile(os.path.join(macos_dir, n))]
    except OSError:
        return None
    stem = bundle[:-4]
    if stem in names:
        return stem
    return names[0] if names else None


def assets_path(game_dir):
    """Where resources.assets lives on this platform.

    Windows keeps it in SpaceTravelIdle_Data next to the .exe; macOS buries
    it inside the app bundle. When nothing is found the Windows layout is
    returned anyway, so a caller printing the path in an error message
    still prints something the player can go and look at.
    """
    bundle = app_bundle(game_dir)
    if bundle:
        return os.path.join(game_dir, bundle, "Contents", "Resources",
                            "Data", "resources.assets")
    return os.path.join(game_dir, "SpaceTravelIdle_Data", "resources.assets")


def layout_hint():
    if IS_MAC:
        return "no SpaceTravelIdle.app/Contents/Resources/Data/resources.assets inside"
    return "no SpaceTravelIdle_Data\\resources.assets inside"


def normalize_game_dir(path):
    """Accept the .app itself as well as the folder holding it.

    On a Mac the game *looks* like a single icon, so dragging that into the
    prompt is the obvious thing to do. Everything we install goes beside
    the bundle, not in it, so step up one level when that happens.
    """
    path = path.rstrip("/\\")
    if path.endswith(".app") and os.path.isdir(path):
        parent = os.path.dirname(path)
        if parent:
            return parent
    return path


def is_game_dir(game_dir):
    return os.path.isfile(assets_path(game_dir))


def game_is_running(game_dir=None):
    try:
        if IS_MAC:
            name = mac_executable_name(game_dir) if game_dir else None
            if not name:
                return False  # cannot tell, so don't block the install
            return subprocess.run(["pgrep", "-x", name],
                                  capture_output=True, text=True,
                                  timeout=15).returncode == 0
        out = subprocess.run(
            ["tasklist", "/FI", f"IMAGENAME eq {GAME_EXE}", "/FO", "CSV", "/NH"],
            capture_output=True, text=True, timeout=15,
        ).stdout
        return GAME_EXE.lower() in out.lower()
    except Exception:
        return False  # if the check is unavailable, don't block the install


def find_entry(text, key, patch_key):
    """Locate a '"key": { ... }' block. Returns (start, end-exclusive, indent)."""
    needle = '"%s": {' % key
    if text.count(needle) != 1:
        raise PatchError(
            f"[{patch_key}] expected exactly one '{key}' entry but found "
            f"{text.count(needle)}. Your game files differ from version "
            f"0.35.43 community ver - not applying this patch.")
    start = text.index(needle)
    indent = text[text.rfind("\n", 0, start) + 1:start]
    i = text.index("{", start)
    depth = 0
    while True:  # walk to the brace that closes this block
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                break
        i += 1
    return start, i + 1, indent


def apply_ops(text, patch):
    for op in patch["ops"]:
        if op[0] == "replace":
            _, old, new, expected = op
            count = text.count(old)
            if count != expected:
                raise PatchError(
                    f"[{patch['key']}] expected {expected} occurrence(s) of\n  {old}\n"
                    f"but found {count}. Your game files differ from version 0.35.43 "
                    f"community ver - not applying this patch.")
            text = text.replace(old, new)
        elif op[0] == "replace_seq":
            _, old, news = op
            count = text.count(old)
            if count != len(news):
                raise PatchError(
                    f"[{patch['key']}] expected {len(news)} occurrence(s) of\n  {old}\n"
                    f"but found {count}. Your game files differ from version 0.35.43 "
                    f"community ver - not applying this patch.")
            parts = text.split(old)
            text = "".join(p + n for p, n in zip(parts, news)) + parts[-1]
        elif op[0] == "set_asset":
            _, hash_file, content_file = op
            hash_path = os.path.join(ROOT_DIR, hash_file)
            content_path = os.path.join(ROOT_DIR, content_file)
            for path in (hash_path, content_path):
                if not os.path.isfile(path):
                    raise PatchError(
                        f"[{patch['key']}] a file this patch needs is missing:\n"
                        f"  {path}\nThe download may be incomplete - "
                        f"not applying this patch.")
            with open(hash_path, encoding="utf-8") as fh:
                want = fh.read().strip()
            import hashlib
            got = hashlib.sha256(text.encode("utf-8", "surrogateescape")).hexdigest()
            if got != want:
                raise PatchError(
                    f"[{patch['key']}] the game's own copy of this data file is "
                    f"not the one this patch was built against\n"
                    f"  expected sha256 {want}\n  found    sha256 {got}\n"
                    f"Your game files differ from version 0.35.43 community ver "
                    f"- not applying this patch.")
            with open(content_path, encoding="utf-8") as fh:
                text = fh.read()
        elif op[0] == "clone_entry":
            _, old_key, new_key = op
            if new_key in text:
                raise PatchError(
                    f"[{patch['key']}] '{new_key}' already exists - your game "
                    f"files differ from version 0.35.43 community ver.")
            start, end, indent = find_entry(text, old_key, patch["key"])
            block = text[start:end].replace(old_key, new_key, 1)
            text = text[:end] + ",\n" + indent + block + text[end:]
        elif op[0] == "insert_entry":
            _, anchor_key, position, entry = op
            new_key = entry.split('"')[1]
            if '"%s": {' % new_key in text:
                continue  # this language already has it
            start, end, indent = find_entry(text, anchor_key, patch["key"])
            if position == "after":
                text = text[:end] + ",\n" + indent + entry + text[end:]
            else:
                text = text[:start] + entry + ",\n" + indent + text[start:]
        else:
            raise PatchError(f"unknown op {op[0]}")
    # sanity: each verify marker must appear within 25k chars of its anchor id
    for anchor, marker in patch.get("verify", []):
        pos = text.find(f'"id": "{anchor}"')
        if pos < 0 or marker not in text[max(0, pos - 25000):pos + 25000]:
            raise PatchError(f"[{patch['key']}] verification failed near '{anchor}'")
    return text


def patch_game(game_dir, selected_keys):
    import UnityPy  # imported late so the menu works before deps are checked

    assets = assets_path(game_dir)
    backup = assets + ".backup-original"
    if not os.path.exists(backup):
        print("Making a one-time backup of your original game data...")
        shutil.copy2(assets, backup)
        print(f"  backup: {backup}")

    if "bigbang-plus" in selected_keys and not NEW_BIGBANG_LINES:
        raise PatchError(
            "%s is missing or unreadable, so the new Big Bang upgrades would "
            "have no names or descriptions. Nothing was changed. Unpack the "
            "whole zip again and rerun the setup." % NEW_LINES_FILE)

    # Always patch from the clean backup so re-runs are repeatable.
    env = UnityPy.load(backup)
    wanted = {}
    for p in PATCHES:
        if p["key"] not in selected_keys:
            continue
        wanted.setdefault(p["asset"], []).append(p)
        # sub-entries edit a second data file under the parent's key; they
        # print nothing of their own so one patch still reports one line
        for sub in p.get("also", []):
            entry = dict(sub, key=p["key"], title=None)
            wanted.setdefault(entry["asset"], []).append(entry)

    touched = 0
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        data = obj.read()
        if data.m_Name not in wanted:
            continue
        text = data.m_Script
        if isinstance(text, bytes):
            text = text.decode("utf-8", "surrogateescape")
        # The game's data files are inconsistent about line endings: some
        # (cardTemplates, the localisation files) use \n and some
        # (cardAlchemyRecipes, bigBangUpgrades) use \r\n. Normalising here
        # means a patch's search text only ever has to be written one way -
        # otherwise a multi-line search string silently matches nothing in
        # half the files. JSON does not care which the file uses.
        text = text.replace("\r\n", "\n")
        for p in wanted[data.m_Name]:
            text = apply_ops(text, p)
            if p["title"]:
                print(f"  applied: {p['title']}")
        data.m_Script = text
        data.save()
        touched += 1

    expected_assets = len(wanted)
    if touched != expected_assets:
        raise PatchError(f"only found {touched} of {expected_assets} data files to patch - aborting, nothing written")

    out_bytes = env.file.save()
    with open(assets, "wb") as f:
        f.write(out_bytes)
    print(f"\nDone! Patched game data written to:\n  {assets}")
    print("Note: if Steam verifies or updates the game it will undo the mod -")
    print("just run this setup again afterwards.")


# Config files whose DEFAULTS changed in a later version of the mod. BepInEx
# never rewrites an entry that already exists, so someone upgrading would
# silently keep the old value. Deleting the file lets it regenerate.
#
# The "stale" marker is what keeps this honest: the file is only removed
# while it still holds the OLD default. Once it has regenerated the marker is
# gone, so re-running this installer never wipes bindings a player has since
# edited for themselves.
STALE_CONFIGS = [
    {
        "file": "sti.community.hotkeys.cfg",
        "stale": "PreviousPanel = LeftArrow",
        "why": "the arrow keys changed in 0.9.10 - up/down now walk the menu",
    },
]


def clear_stale_configs(game_dir):
    config_dir = os.path.join(game_dir, "BepInEx", "config")
    for entry in STALE_CONFIGS:
        path = os.path.join(config_dir, entry["file"])
        if not os.path.isfile(path):
            continue
        try:
            with open(path, "r", encoding="utf-8-sig", errors="replace") as fh:
                text = fh.read()
        except OSError:
            continue
        if entry["stale"] not in text:
            continue
        os.remove(path)
        print(f"  reset config: {entry['file']} ({entry['why']})")


def patch_dlls(p):
    """Every DLL one code patch installs, with the BepInEx folder it goes in.

    Almost all of them are one plugin. A patch can also carry a patcher, which
    BepInEx runs seconds before plugins, and it has to sit in the BepInEx
    patchers folder or BepInEx never looks at it. Everything that installs, removes, packs or
    checks a code patch goes through this, so the two folders cannot drift.

    An entry that is only a setting inside another patch's plugin has no DLL
    at all, and gets an empty list.

    A plugin can name its own folder under BepInEx (plugin_folder). Only the
    Balancer does, because the Unlocker has always lived in plugins/STIU and
    a copy the player already has must land on the same path, not beside it.
    """
    out = [(p["plugin"], p.get("plugin_folder", "plugins"))] if p.get("plugin") else []
    if p.get("patcher"):
        out.append((p["patcher"], "patchers"))
    return out


def install_code_patches(game_dir, selected_keys):
    # Settings are not plugins: ticking only a setting installs no loader.
    selected = [p for p in CODE_PATCHES
                if p["key"] in selected_keys and p.get("plugin")]
    plugins_dir = os.path.join(game_dir, "BepInEx", "plugins")

    if selected:
        if not os.path.isdir(BEPINEX_PAYLOAD):
            raise PatchError(
                "The bundled BepInEx files are missing (expected in the "
                "'bepinex/%s' folder next to this setup). Code patches were "
                "NOT installed." % PAYLOAD_NAME)
        print("\nInstalling the BepInEx mod loader (needed by the code patches)...")
        shutil.copytree(BEPINEX_PAYLOAD, game_dir, dirs_exist_ok=True)
        if IS_MAC:
            finish_macos_loader(game_dir)
        os.makedirs(plugins_dir, exist_ok=True)
        for p in selected:
            for dll, folder in patch_dlls(p):
                src = os.path.join(PLUGIN_DIR, dll)
                if not os.path.isfile(src):
                    raise PatchError(f"Plugin file missing: {src} - '{p['key']}' was NOT installed.")
                target = os.path.join(game_dir, "BepInEx", folder)
                os.makedirs(target, exist_ok=True)
                if p.get("licence"):
                    keep_players_copy(target, dll)
                shutil.copy2(src, target)
            if p.get("licence"):
                licence = os.path.join(PLUGIN_DIR, p["licence"])
                if not os.path.isfile(licence):
                    raise PatchError(f"Licence file missing: {licence} - '{p['key']}' was NOT installed.")
                shutil.copy2(licence, os.path.join(game_dir, "BepInEx", patch_dlls(p)[0][1]))
            if p.get("assets"):
                asset_src = os.path.join(ASSETS_DIR, p["assets"])
                if not os.path.isdir(asset_src):
                    raise PatchError(
                        f"Asset folder missing: {asset_src} - '{p['key']}' was NOT installed.")
                shutil.copytree(asset_src,
                                os.path.join(plugins_dir, p["assets"]),
                                dirs_exist_ok=True)
            print(f"  applied (code): {p['title']}")
        clear_stale_configs(game_dir)
        if IS_MAC:
            print_macos_launch_option(game_dir)

    # remove plugins that are no longer selected
    for p in CODE_PATCHES:
        if p["key"] not in selected_keys:
            removed = False
            for dll, folder in patch_dlls(p):
                leftover = os.path.join(game_dir, "BepInEx", folder, dll)
                if os.path.exists(leftover):
                    os.remove(leftover)
                    removed = True
            if p.get("assets"):
                asset_leftover = os.path.join(plugins_dir, p["assets"])
                if os.path.isdir(asset_leftover):
                    shutil.rmtree(asset_leftover)
                    removed = True
            if p.get("licence"):
                tidy_third_party(game_dir, p, put_back=False)
            if removed:
                print(f"  removed (code): {p['title']}")

    # Before the loader can go, so a player's own settings file is back in
    # place whatever happens to the BepInEx folder next.
    apply_cfg_values(game_dir, selected_keys)

    # no code patches wanted at all -> take the loader out too
    if not selected:
        remove_bepinex(game_dir)
    else:
        apply_settings(game_dir, selected_keys)


# A third party plugin or settings file the player had before this setup
# first touched it is kept under its own name plus this, and put back on
# restore. BepInEx only loads *.dll, so a kept DLL is never loaded.
KEPT_SUFFIX = ".before-community-patch"
# Written next to a third party plugin this setup put where nothing was, so
# a later run can tell "ours" from "theirs" when the bytes are the same.
OURS_MARKER = "installed-by-community-patch.txt"


def keep_players_copy(target_dir, dll):
    """Set aside a copy of a third party plugin the player already had.

    Only the first time. After that the file on the path is ours, and the
    marker or the kept copy says so.
    """
    path = os.path.join(target_dir, dll)
    kept = path + KEPT_SUFFIX
    marker = os.path.join(target_dir, OURS_MARKER)
    if os.path.exists(kept) or os.path.exists(marker):
        return
    if os.path.isfile(path):
        shutil.copy2(path, kept)
        print(f"  kept aside: your own {dll}, it comes back when you restore the original game")
    else:
        with open(marker, "w", encoding="utf-8") as fh:
            fh.write("The Community Patch setup installed %s here.\n"
                     "Nothing was here before it.\n" % dll)


def tidy_third_party(game_dir, p, put_back):
    """Remove what came with a third party plugin once its DLL is gone.

    put_back is for restoring the original game: the player's own copy goes
    back where it was. Unticking the line leaves the kept copy where it is,
    so a later restore can still find it. An empty folder is removed, or
    remove_bepinex would count it as someone else's plugin forever.
    """
    for dll, folder in patch_dlls(p):
        target = os.path.join(game_dir, "BepInEx", folder)
        for name in (p["licence"], OURS_MARKER):
            leftover = os.path.join(target, name)
            if os.path.exists(leftover):
                os.remove(leftover)
        kept = os.path.join(target, dll) + KEPT_SUFFIX
        if put_back and os.path.isfile(kept):
            shutil.move(kept, os.path.join(target, dll))
            print(f"  put back: your own {dll}")
        if os.path.isdir(target) and not os.listdir(target):
            os.rmdir(target)


def apply_cfg_values(game_dir, selected_keys):
    """Write or take back a line that is a whole settings file's values.

    Ours are in the file only while the line AND the plugin it needs are
    ticked. The first time they go in, the player's file is kept aside (or
    a note that there was none), and any other outcome puts that back. The
    player's own edits are never merged with ours: it is one or the other,
    so turning it off gives back exactly what they had.
    """
    config_dir = os.path.join(game_dir, "BepInEx", "config")
    for p in CODE_PATCHES:
        spec = p.get("cfg_values")
        if not spec:
            continue
        path = os.path.join(config_dir, spec["file"])
        kept = path + KEPT_SUFFIX
        none_before = os.path.join(config_dir, spec["file"] + ".none" + KEPT_SUFFIX)
        wanted = p["key"] in selected_keys
        on = wanted and spec["needs"] in selected_keys
        if on:
            if not os.path.exists(kept) and not os.path.exists(none_before):
                os.makedirs(config_dir, exist_ok=True)
                if os.path.isfile(path):
                    shutil.copy2(path, kept)
                else:
                    with open(none_before, "w", encoding="utf-8") as fh:
                        fh.write("There was no %s before the Community Patch setup "
                                 "wrote one.\n" % spec["file"])
            for section, name, value in spec["values"]:
                set_cfg_value(path, section, name, value)
            print(f"  applied (code): {p['title']}")
            continue
        if os.path.isfile(kept):
            shutil.copy2(kept, path)
            os.remove(kept)
            print(f"  put back: your own {spec['file']}")
        elif os.path.isfile(none_before):
            if os.path.isfile(path):
                os.remove(path)
            os.remove(none_before)
            print(f"  removed (code): {p['title']}")
        if wanted:
            print(f"  not applied: {p['title']} (it needs a line that is off)")


def set_cfg_value(path, section, name, value):
    """Set one entry in a BepInEx .cfg file, creating what is missing.

    Everything else in the file is left exactly as it was, because it may
    hold a player's own edits. The line ending already in the file is kept.
    Returns True when the file changed.

    BepInEx reads an entry that is already in the file and keeps it, so a
    file holding only this one line is enough: the plugin fills in the rest,
    with their descriptions, the first time the game starts.
    """
    if os.path.isfile(path):
        with open(path, "rb") as fh:
            raw = fh.read()
    else:
        raw = b""
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig", errors="replace")
    nl = "\r\n" if "\r\n" in text else ("\n" if text else os.linesep)
    lines = text.splitlines(True)
    wanted = "%s = %s" % (name, value)
    header = "[%s]" % section
    entry = re.compile(r"\s*" + re.escape(name) + r"\s*=")

    start = None
    for i, line in enumerate(lines):
        if line.strip() == header:
            start = i
            break
    if start is None:
        if lines and not lines[-1].endswith(("\n", "\r")):
            lines[-1] += nl
        if lines:
            lines.append(nl)
        lines += [header + nl, nl, wanted + nl]
    else:
        end = len(lines)
        for i in range(start + 1, len(lines)):
            if lines[i].lstrip().startswith("["):
                end = i
                break
        for i in range(start + 1, end):
            if entry.match(lines[i]):
                ending = lines[i][len(lines[i].rstrip("\r\n")):]
                lines[i] = wanted + (ending or nl)
                break
        else:
            lines.insert(start + 1, wanted + nl)

    new = "".join(lines).encode("utf-8")
    if bom:
        new = b"\xef\xbb\xbf" + new
    if new == raw:
        return False
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as fh:
        fh.write(new)
    return True


def apply_settings(game_dir, selected_keys):
    """Write the menu lines that are a setting inside another patch.

    Ticked writes true, unticked writes false, every time the setup runs, so
    changing your mind is just running it again. A setting whose plugin is
    not being installed is left alone: nothing would read it.

    The one case that writes nothing is ticked with no file yet. true is the
    plugin's own default, so there is nothing to say until the game has
    started once and written the file itself.
    """
    config_dir = os.path.join(game_dir, "BepInEx", "config")
    for p in CODE_PATCHES:
        setting = p.get("setting")
        if not setting:
            continue
        on = p["key"] in selected_keys
        if setting["needs"] not in selected_keys:
            if on:
                print(f"  not applied: {p['title']} (it lives in a patch "
                      f"that is off)")
            continue
        path = os.path.join(config_dir, setting["file"])
        if on and not os.path.isfile(path):
            print(f"  applied (code): {p['title']}")
            continue
        set_cfg_value(path, setting["section"], setting["name"],
                      "true" if on else "false")
        if on:
            print(f"  applied (code): {p['title']}")
        else:
            print(f"  turned off (code): {p['title']}")


def finish_macos_loader(game_dir):
    """Make the copied loader actually usable on macOS.

    Copying the files is not enough, for three separate reasons:

    - run_bepinex.sh has to be executable. Copying from a Windows machine,
      or out of a zip, loses the bit.
    - it has to know which .app to launch. The stock script ships that
      field empty and expects a human to fill it in.
    - macOS flags everything that arrived inside a downloaded zip as
      quarantined, and a quarantined unsigned .dylib is refused at load
      time. The game then starts perfectly normally with no mod and no
      error, which is the worst way for this to fail.
    """
    script = os.path.join(game_dir, "run_bepinex.sh")
    bundle = app_bundle(game_dir)
    if os.path.isfile(script):
        if bundle:
            with open(script, encoding="utf-8") as fh:
                text = fh.read()
            marker = 'executable_name=""'
            if marker in text:
                text = text.replace(marker,
                                    'executable_name="%s"' % bundle, 1)
                with open(script, "w", encoding="utf-8", newline="\n") as fh:
                    fh.write(text)
                print("  launcher set to start: " + bundle)
        os.chmod(script, 0o755)
    for name in ("run_bepinex.sh", "libdoorstop.dylib", "BepInEx"):
        target = os.path.join(game_dir, name)
        if not os.path.exists(target):
            continue
        try:
            subprocess.run(["xattr", "-dr", "com.apple.quarantine", target],
                           capture_output=True)
        except OSError:
            # No xattr on this machine. Nothing else here depends on it, and
            # failing the whole install over a flag that may not even be set
            # would be worse than letting the player hit the Gatekeeper
            # message the readme already covers.
            pass


def print_macos_launch_option(game_dir):
    """The one step this installer cannot do for the player.

    On Windows the loader hooks itself in just by being in the folder. On
    macOS the game has to be started through run_bepinex.sh instead, and
    only Steam can be told to do that.
    """
    script = os.path.join(game_dir, "run_bepinex.sh")
    print("\n" + "=" * 70)
    print("ONE STEP LEFT, and the code patches do nothing without it.")
    print("=" * 70)
    print("In Steam: right-click Space Travel Idle, Properties, General.")
    print("Paste this into Launch Options, exactly as it is including the")
    print("quotes and the %command% at the end:\n")
    print('  "%s" %%command%%\n' % script)
    print("Then start the game from Steam as normal. If it worked there")
    print("will be a BepInEx/LogOutput.log file in the game folder.")
    print("=" * 70)


def remove_bepinex(game_dir, put_back=False):
    # Only remove the loader if no OTHER plugins remain: some installs (e.g.
    # the community version) already ship BepInEx with their own plugins,
    # and those must survive our uninstall. put_back also returns a third
    # party plugin the player had before us (the Unlocker) to its place.
    bep_dir = os.path.join(game_dir, "BepInEx")
    if not os.path.isdir(bep_dir):
        return
    plugins_dir = os.path.join(bep_dir, "plugins")
    patchers_dir = os.path.join(bep_dir, "patchers")
    for p in CODE_PATCHES:
        for dll, folder in patch_dlls(p):
            ours = os.path.join(bep_dir, folder, dll)
            if os.path.exists(ours):
                os.remove(ours)
        # Asset folders count as ours too. Leaving one behind would make the
        # "is anything else installed?" check below say yes forever, and
        # BepInEx would never be uninstalled.
        if p.get("assets"):
            ours_assets = os.path.join(plugins_dir, p["assets"])
            if os.path.isdir(ours_assets):
                shutil.rmtree(ours_assets)
        if p.get("licence"):
            tidy_third_party(game_dir, p, put_back)
    others =[d for d in (plugins_dir, patchers_dir)
              if os.path.isdir(d) and os.listdir(d)]
    if others:
        print("This mod's code patches were removed. BepInEx itself was kept because")
        print("other plugins (not from this mod) are installed in the BepInEx")
        print("%s folder." % " and ".join(os.path.basename(d) for d in others))
        return
    # Both platforms' loader files are listed. Removing one that was never
    # there costs nothing, and it means a folder copied from a Windows
    # machine to a Mac, or the other way, still uninstalls cleanly.
    for name in ("winhttp.dll", "doorstop_config.ini", ".doorstop_version",
                 "changelog.txt", "run_bepinex.sh", "libdoorstop.dylib"):
        path = os.path.join(game_dir, name)
        if os.path.exists(path):
            os.remove(path)
    shutil.rmtree(bep_dir)
    print("BepInEx mod loader removed from the game folder.")


def restore_original(game_dir):
    assets = assets_path(game_dir)
    backup = assets + ".backup-original"
    if not os.path.exists(backup):
        print("No backup found - the game files have not been modified by this setup.")
    else:
        shutil.copy2(backup, assets)
        print(f"Original game data restored:\n  {assets}")
    apply_cfg_values(game_dir, set())
    remove_bepinex(game_dir, put_back=True)


def ensure_unitypy(auto_yes):
    try:
        import UnityPy  # noqa: F401
        return True
    except ImportError:
        pass
    print("This setup needs the 'UnityPy' Python package (used to edit Unity game data).")
    if not auto_yes:
        ans = input("Install it now with pip? [Y/n] ").strip().lower()
        if ans not in ("", "y", "yes"):
            print("Cannot continue without UnityPy. Nothing was changed.")
            return False
    code = subprocess.call([sys.executable, "-m", "pip", "install", "UnityPy"])
    if code != 0:
        print("pip install failed. Nothing was changed.")
        return False
    try:
        import UnityPy  # noqa: F401
        return True
    except ImportError:
        print("UnityPy still not importable. Nothing was changed.")
        return False


def pick_game_dir(cli_dir, assume_yes=False):
    if cli_dir:
        cli_dir = normalize_game_dir(cli_dir)
        if is_game_dir(cli_dir):
            return cli_dir
        print(f"'{cli_dir}' does not look like a Space Travel Idle folder "
              f"({layout_hint()}).")
        return None
    found = [d for d in candidate_dirs() if is_game_dir(d)]
    # --yes means "no prompts", so it has to answer this one too. One
    # install found is an unambiguous answer; none or several is not, and
    # guessing which copy of the game to patch is not a guess worth making.
    if assume_yes:
        if len(found) == 1:
            return found[0]
        print("--yes was given but the game folder is not obvious "
              "(found %d). Pass --game with the full path." % len(found))
        return None
    print("\nWhere is the game installed?")
    print("(You can point this at any install: Normal, Beta, or Beta Danger.)")
    for i, d in enumerate(found, 1):
        print(f"  {i}. {d}")
    print(f"  {len(found) + 1}. Type a different folder path")
    while True:
        ans = input(f"Choose [1-{len(found) + 1}], or paste a folder path: ").strip()
        if not ans:
            continue
        if ans.isdigit() and 1 <= int(ans) <= len(found):
            return found[int(ans) - 1]
        if ans.isdigit() and int(ans) == len(found) + 1:
            ans = input("Game folder path: ").strip()
        # A path dragged from Finder or Explorer arrives quoted, and on a
        # Mac it usually arrives with the spaces backslash-escaped too.
        path = ans.strip().strip('"').strip("'")
        if IS_MAC:
            path = path.replace("\\ ", " ")
        path = normalize_game_dir(path)
        if is_game_dir(path):
            return path
        print("That folder has %s - try again." % layout_hint())


# Titles that give something away, and a version of each that does not.
#
# A player who has never left Mars should not learn from an installer that
# there is a seventh permanent slot waiting on Uranus, that twelve new Big
# Bang upgrades exist, or that a card called Lightning Mastery is in there.
# The first screen never shows a title at all, so this only matters once
# somebody presses c, but that is exactly the curious player most likely to
# be spoiled.
#
# Anything not listed here is already safe: "Bugfix: filters forget
# themselves on load" tells you nothing you would rather find out yourself.
# Press s in the menu to see the real ones.
SAFE_TITLES = {
    "travel-fix": "Bugfix: three destinations you cannot travel to",
    "neptune-reach": "Bugfix: two destinations you can never reach",
    "pilot-training": "Bugfix: a travel research that does nothing",
    "outer-planets": "Bugfix: production and dead ends on the far planets",
    "perm-slot-uranus": "Content: one more permanent slot, late game",
    "drop-rates": "Bugfix: a drop card that can never be levelled",
    "def-base-drops": "Bugfix: a card that fell out of the drop tables",
    "petri-dish-drops": "Bugfix: a card that stops dead partway up",
    "accelerator-drops": "Bugfix: two upgrade cards that cannot be levelled",
    "enemy-deck-fix": "Bugfix: some fights freeze on the enemy's turn",
    "outer-research-fix": "Bugfix: three late researches can never unlock",
    "outer-costs": "Rebalance: two late game upgrades",
    "neptune-costs": "Rebalance: one planet costs more than the next one",
    "taichi-rework": "Rework: a battle card nobody could use",
    "def-heal-rework": "Rework: three battle cards that stop scaling",
    "bigbang-plus": "Content: more Big Bang upgrades",
    "lightning-mastery": "New card: an Epic Action card",
    "bigbang-extras": "Big Bang and travel achievements",
    "planetary-memory": "Buildings that keep paying after you leave",
    "cargo-bay": "Cargo you can actually fly with",
}


def state_word(is_on):
    """The on/off column in the list.

    An empty tick box is the conventional way to say this and it was the
    first thing here, but it reads badly at this length. Someone who has
    just turned three things off is scanning 40 lines for three gaps,
    and a gap is exactly the shape the eye skips. Fuzzied asked why the
    list did not say OFF on 21.09.2026, having read the word in the
    Changed block directly underneath and then not found it above.

    So the common state is lowercase and quiet, and the exception is
    uppercase and loud. Nothing needs counting to see which three are
    the odd ones out.
    """
    return "on" if is_on else "OFF"


def shown_title(patch, spoilers):
    """What the menu prints for one patch right now."""
    if spoilers:
        return patch["title"]
    return SAFE_TITLES.get(patch["key"], patch["title"])


def describe_patch(number, patch, spoilers):
    """Print the full description of one patch, on request.

    None of the writing is thrown away by the shorter menu. It just stops
    being shouted at everyone who only wanted to press Enter. Asking about
    one patch by number is a deliberate act, so the real description is
    what comes back, with a word of warning first if the list is currently
    hiding names.
    """
    if not spoilers and patch["key"] in SAFE_TITLES:
        print("\n (this one names something you may not have found yet)")
    print("\n %d. %s" % (number, patch["title"]))
    for line in textwrap.wrap(patch["desc"], 70):
        print("    " + line)


def choose_patches(all_patches, selected):
    """The detailed screen, titles only, shown only when asked for.

    The menu this replaces printed all 40 titles AND all 40 descriptions
    every time round the loop. 610 lines, in a window that shows 30. Every
    toggle reprinted the lot, so the single line that had just changed
    scrolled about 300 lines out of view, and the honest impression was
    that typing a number did nothing at all. Fuzzied hit exactly that on
    21.09.2026 while testing this installer and asked whether the numbers
    were even working.

    Three things fix it. Titles only, which fits the whole list in about 46
    lines. The descriptions still available, one at a time, behind '?8'.
    And a line directly above the prompt that says what the last thing you
    typed actually did, so the answer is never somewhere you have to scroll
    to find.

    Returns the keys to install, or None if the player quit.
    """
    told = "Nothing has changed yet."
    spoilers = False
    while True:
        print("\n--- DATA PATCHES: edit the game's data tables in place "
              + "-" * 14)
        for i, p in enumerate(PATCHES, 1):
            print(" %2d. %-4s %s"
                  % (i, state_word(selected[p["key"]]),
                     shown_title(p, spoilers)))
        print("\n--- CODE PATCHES: run inside the game at launch " + "-" * 21)
        for i, p in enumerate(CODE_PATCHES, len(PATCHES) + 1):
            print(" %2d. %-4s %s"
                  % (i, state_word(selected[p["key"]]),
                     shown_title(p, spoilers)))
        on = sum(1 for v in selected.values() if v)
        print("\n" + told)
        print("Type numbers to turn things off or on. Several at once is "
              "fine: 8 17 21")
        print("?8 tells you what number 8 does. a turns all on, n turns all "
              "off.")
        if spoilers:
            print("s hides the names again, for anyone reading over your "
                  "shoulder.")
        else:
            print("Some names are hidden so nothing is spoiled. s shows them.")
        ans = input("Enter installs the %d still on, or q to quit: "
                    % on).strip().lower()

        if ans == "":
            return [k for k, v in selected.items() if v]
        if ans == "q":
            return None
        if ans == "a":
            for k in selected:
                selected[k] = True
            told = "Turned everything on."
            continue
        if ans == "n":
            for k in selected:
                selected[k] = False
            told = "Turned everything off."
            continue
        if ans == "s":
            spoilers = not spoilers
            told = ("Showing the real names now."
                    if spoilers
                    else "Hiding the names that give things away again.")
            continue
        if ans.startswith("?"):
            rest = ans[1:].strip()
            if rest.isdigit() and 1 <= int(rest) <= len(all_patches):
                describe_patch(int(rest), all_patches[int(rest) - 1], spoilers)
                told = "That was number %s, described above the list." % rest
            else:
                told = ("There is no number %s to describe. They run 1 to %d."
                        % (rest, len(all_patches)))
            continue

        # Numbers, one or several, separated by anything at all. A player
        # unticking three things will type "8, 17, 21" or "8 17 21" long
        # before they try them one per line, and refusing that taught
        # nothing except that the installer was fussy.
        wanted = [int(x) for x in re.findall(r"\d+", ans)]
        if not wanted:
            told = ("'%s' is not a number, and not a, n or q either." % ans)
            continue
        done = []
        missing = []
        for number in wanted:
            if not 1 <= number <= len(all_patches):
                missing.append(str(number))
                continue
            p = all_patches[number - 1]
            selected[p["key"]] = not selected[p["key"]]
            # One line each, with the title in full. Squeezing three of
            # them onto one line meant chopping every title mid word, and
            # the whole point of this line is that it can be read without
            # going looking for anything.
            done.append(" %2d %-3s %s"
                        % (number, "ON" if selected[p["key"]] else "OFF",
                           shown_title(p, spoilers)))
        lines = []
        if done:
            lines.append("Changed:")
            lines.extend(done)
        if missing:
            lines.append("There is no number %s. They run 1 to %d."
                         % (", ".join(missing), len(all_patches)))
        told = "\n".join(lines)
        continue


def interactive_menu():
    all_patches = PATCHES + CODE_PATCHES
    selected = {p["key"]: p["default"] for p in all_patches}
    while True:
        on = sum(1 for v in selected.values() if v)
        print("\n=== Space Travel Idle, Community Patch " + PUBLIC_VERSION
              + " ===\n")
        print("%d additions are ready to install. %d of them change the "
              "game's data" % (len(all_patches), len(PATCHES)))
        print("tables, %d add code that runs at launch. They are all on by "
              "default.\n" % len(CODE_PATCHES))
        print("  Enter   Install all %d (this is what most people want)" % on)
        print("  c       Choose which ones yourself")
        print("  r       Restore the original game files")
        print("  q       Quit without changing anything")
        ans = input("\nYour choice: ").strip().lower()
        if ans == "":
            return ("install", [k for k, v in selected.items() if v])
        if ans == "q":
            return ("quit", None)
        if ans == "r":
            return ("restore", None)
        if ans == "c":
            keys = choose_patches(all_patches, selected)
            if keys is None:
                return ("quit", None)
            if not keys:
                print("\nNothing is on, so there is nothing to install. "
                      "Use r to uninstall instead.")
                continue
            return ("install", keys)
        print("\n'%s' is not one of the four choices above." % ans)


def main():
    ap = argparse.ArgumentParser(description="Space Travel Idle community mod setup")
    ap.add_argument("--game", help="game install folder (the one holding "
                                   "SpaceTravelIdle_Data, or the .app on macOS)")
    ap.add_argument("--patches", help="comma-separated patch keys to install (skips the menu)")
    ap.add_argument("--restore", action="store_true", help="restore original game files")
    ap.add_argument("--yes", action="store_true",
                    help="no prompts: install the default patch set")
    args = ap.parse_args()

    # The build number stays out of sight. Fuzzied, 28.09.2026: "drop our
    # internal build number". It is still in BUILD_VERSION for our tools.
    print("Space Travel Idle - Community Patch " + PUBLIC_VERSION)
    print("For game version 0.35.43 community ver (Normal / Beta / Beta Danger)")
    print("Full patch notes: PATCH_NOTES.txt (next to this setup)")

    if args.patches:
        valid = {p["key"] for p in PATCHES} | {p["key"] for p in CODE_PATCHES}
        keys = [k.strip() for k in args.patches.split(",") if k.strip()]
        bad = [k for k in keys if k not in valid]
        if bad:
            sys.exit(f"unknown patch key(s): {', '.join(bad)}. valid: {', '.join(sorted(valid))}")
        action = ("install", keys)
    elif args.restore:
        action = ("restore", None)
    elif args.yes:
        # The same set the menu starts with, so "--yes" and "press Enter
        # at the menu" install exactly the same thing.
        action = ("install", [p["key"] for p in PATCHES + CODE_PATCHES
                              if p["default"]])
    else:
        action = interactive_menu()

    if action[0] == "quit":
        print("Nothing was changed.")
        return

    game_dir = pick_game_dir(args.game, args.yes)
    if not game_dir:
        sys.exit(1)
    print(f"\nGame folder: {game_dir}")

    if game_is_running(game_dir):
        sys.exit("The game is currently running. Close Space Travel Idle first, then run this setup again.")

    if action[0] == "restore":
        restore_original(game_dir)
        return

    if not ensure_unitypy(args.yes):
        sys.exit(1)

    data_keys = [k for k in action[1] if k in {p["key"] for p in PATCHES}]
    try:
        if data_keys:
            patch_game(game_dir, data_keys)
        else:
            # no data patches selected: put the original data back
            assets = assets_path(game_dir)
            backup = assets + ".backup-original"
            if os.path.exists(backup):
                shutil.copy2(backup, assets)
        install_code_patches(game_dir, action[1])
    except PatchError as e:
        sys.exit(f"\nPATCH FAILED.\n{e}")


if __name__ == "__main__":
    main()
    if os.name == "nt" and len(sys.argv) == 1:
        input("\nPress Enter to close...")
