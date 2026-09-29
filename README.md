# Space Travel Idle: Community Patch

A free Community Patch for Space Travel Idle v0.35.43, the community version. Windows and Mac.

Bugfixes, quality of life, new cards, new Big Bang upgrades and quite a lot of balance changes. No Python and no setup since 0.1.1. Everything is on, and one settings file turns things off.

It also comes with a second mod that isn't mine: the [Space Travel Idle Unlocker](https://github.com/Berserker66/STIU) by Berserker, which I call the Balancer. I tuned the whole patch with it on. All credit for it goes to Berserker.

Your saves are never touched. They live in your own user folder, not in the game folder.

## You need the community version first

The patch is made for the community version of the game, v0.35.43, not the plain Steam build. Izm_ shares it on the Space Travel Idle Discord.

Download: [SpaceTravelIdle_0.35.43.community.ver.zip](https://drive.google.com/file/d/12rzd9zlH0ixN5Saq7-1-CaeDvJwAeP3I/view?usp=sharing) (Izm_'s Google Drive)

Izm_'s post on Discord: <https://discord.com/channels/758755842861432832/1517349127204114482/1517403351229272074>

## Download

Go to the **Releases** page on the right and grab the zip for your system:

Windows: `STI-CommunityPatch-0.1.2-Windows.zip`

Mac: `STI-CommunityPatch-0.1.2-Mac.zip`

Already on 0.1.1? `STI-CommunityPatch-0.1.2-update.zip` only swaps 10 files, for Windows and Mac. Its READ ME says how.

You don't need anything else from this page to play. The rest is the source code, for the curious.

## Installing

Close the game first. The READ ME FIRST file in the zip has the details.

### Win:

1. Find the game folder. In Steam: right-click Space Travel Idle, Manage, Browse local files.
2. Open the zip, press Ctrl+A, and drag everything into the game folder. Say yes if Windows asks about replacing files.
3. Start the game from Steam as normal.

Careful with "Extract All": Windows adds the zip's name as one more folder at the end of the path. Delete that part, so it goes straight into the game folder.

### Mac:

1. Unzip, then double-click "Install Community Patch.command". If macOS says it is from an unidentified developer, right-click it, choose Open, and click Open.
2. It finds the game, puts the patch in, and copies one line for you. Paste that into Steam: right-click Space Travel Idle, Properties, General, Launch Options.
3. Start the game from Steam as normal.

The Mac side has not been tested on a real Mac yet. If you try it, please tell me how it went.

### Coming from 0.1

0.1 wrote its changes into the game's data file. 0.1.1 makes them while the game runs instead.

Win: double-click "Undo 0.1 game data.bat" in the game folder once, after unzipping.

Mac: the install script does it for you.

### Turning things off

Everything is on when you install it. The first time the game starts, it makes a settings file in the game folder:

`BepInEx/config/STI Community Patch.cfg`

Open it in Notepad (TextEdit on a Mac). Every part of the patch has its own line there, with a short description above it. Change `true` to `false` on the ones you don't want, save, and restart the game.

### Playing without the Balancer

The patch comes with a second mod, Berserker's Unlocker, which I call the Balancer. I tuned the patch with it on. It is not in the settings file, because it is not mine. To play without it, delete `BepInEx/plugins/STIU` and `BepInEx/config/STIU.cfg` from the game folder.

### Taking it off

Win: delete winhttp.dll from the game folder. To tidy up, also delete the BepInEx folder.

Mac: clear the Launch Options line in Steam. To tidy up, also delete the BepInEx folder, run_bepinex.sh and libdoorstop.dylib.

## What is in here

`plugin/`: The source of every mod in the patch. Each one is a small BepInEx plugin. DataPatches makes the game data changes while the game loads, from `community_patches.json`.

`installer/`: The READ ME FIRST files, the Mac install script and the 0.1 undo. `setup_mod.py` is where every patch is defined, and `tools/export_patches.py` turns it into `community_patches.json`. It was the setup in 0.1.

`data/`: The new Big Bang upgrades the patch adds to the game.

`assets/`: The icons for the new cards and Big Bang upgrades.

`tools/`: The scripts I use to build the release zips and check them.

`PATCH_NOTES.txt`: Everything the patch changes.

## Building it yourself

You need Windows, the game installed, and the patch installed once so BepInEx is in the game folder. Then in PowerShell:

```
powershell -ExecutionPolicy Bypass -File "C:\path\to\STI-CommunityPatch\plugin\build_plugin.ps1" -GameFolder "C:\path\to\Space Travel Idle"
```

## Credits

Space Travel Idle is made by Aya and Berk, two friends who started it in 2020 (on Steam as Ayatsuji_San, published by Spaceive). This patch is a fan project and not made by or with them.

A big thanks to Izm_ for sharing the community version. Without it there would be nothing to patch.

The patch runs on [BepInEx](https://github.com/BepInEx/BepInEx) and [HarmonyX](https://github.com/BepInEx/HarmonyX). The build tools read the game's data with [UnityPy](https://github.com/K0lb3/UnityPy).

The patch also comes with the [Space Travel Idle Unlocker](https://github.com/Berserker66/STIU) by Berserker, which I call the Balancer. I tuned the patch with it on. All credit for it goes to Berserker.

The full licences for all of these are in `THIRD PARTY LICENCES.txt`.

## Licence

GPLv3. See `LICENCE.txt`.

Found a bug, or something you'd like to see? Open an issue or find me on the game's Discord.

Fuzzied
