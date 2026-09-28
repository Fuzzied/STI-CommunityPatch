# Space Travel Idle: Community Patch

A free Community Patch for Space Travel Idle v0.35.43, the community version. Windows and Mac.

Bugfixes, quality of life, new cards, new Big Bang upgrades and quite a lot of balance changes. Everything is optional. You tick what you want in the setup, and you can change your mind later by running it again.

Your saves are never touched by the setup. They live in your own user folder, not in the game folder.

## You need the community version first

The patch is made for the community version of the game, v0.35.43, not the plain Steam build. Izm_ shares it on the Space Travel Idle Discord, get it from Izm_'s post: <https://discord.com/channels/758755842861432832/1517349127204114482/1517403351229272074>

## Download

Go to the **Releases** page on the right and grab the zip for your system:

Windows: `STI-CommunityPatch-0.1-Windows.zip`

Mac: `STI-CommunityPatch-0.1-Mac.zip`

You don't need anything else from this page to play. The rest is the source code, for the curious.

## Installing

Close the game first. Then unzip anywhere and open the READ ME FIRST file inside. It walks you through it.

The setup runs on Python, so you need Python 3.9 or newer. You only do this once.

### Win:

1. Go to <https://www.python.org/downloads/> and click the big Download button.
2. On the first screen of the installer, tick "Add python.exe to PATH" at the bottom. This is the one people miss. Then click Install Now.
3. Windows has a fake "python" that only opens the Microsoft Store, and it gets in the way. Search the Start menu for "Manage app execution aliases" and turn off both App Installer lines, python.exe and python3.exe.
4. Run "Setup Space Travel Idle Mod.bat". The first time, it asks to install UnityPy. Just press Enter. It needs internet for that and takes a minute.

To check Python worked: open CMD and type `python --version`. A version number means you're good.

### Mac:

1. Go to <https://www.python.org/downloads/> and download the macOS installer. Use this one and not Homebrew, because Homebrew's Python refuses the package the setup needs.
2. Run it and click through.
3. Run "Setup Space Travel Idle Mod.command" and press Enter when it asks to install UnityPy.

If a box pops up asking to install developer tools, the Python from step 1 isn't in place yet.

### Taking it off

Run the setup again and choose r. Every original game file goes back.

After a Steam update or file check, just run the setup again. Steam puts the original files back, nothing breaks.

## What is in here

`plugin/`: The source of every mod in the patch. Each one is a small BepInEx plugin.

`installer/` and `setup_mod.py`: The setup itself, and the READ ME FIRST files.

`data/`: The new Big Bang upgrades the setup adds to the game.

`assets/`: The icons for the new cards and Big Bang upgrades.

`tools/`: The scripts I use to build the release zips and check them.

`PATCH_NOTES.txt`: Everything the patch changes.

## Building it yourself

You need Windows, the game installed, and the patch installed once so BepInEx is in the game folder. Then in PowerShell:

```
powershell -ExecutionPolicy Bypass -File "C:\path\to\STI-CommunityPatch\plugin\build_plugin.ps1" -GameFolder "C:\path\to\Space Travel Idle"
```

## Credits

Space Travel Idle is made by Ayatsuji. This patch is a fan project and not made by or with them.

A big thanks to Izm_ for sharing the community version. Without it there would be nothing to patch.

The patch runs on [BepInEx](https://github.com/BepInEx/BepInEx) and [HarmonyX](https://github.com/BepInEx/HarmonyX). The setup edits the game's data with [UnityPy](https://github.com/K0lb3/UnityPy).

The setup can also install the [Space Travel Idle Unlocker](https://github.com/Berserker66/STIU) by Berserker, which I call the Balancer. I tuned the patch with it on. All credit for it goes to Berserker.

The full licences for all of these are in `THIRD PARTY LICENCES.txt`.

## Licence

GPLv3. See `LICENCE.txt`.

Found a bug, or something you'd like to see? Open an issue or find me on the game's Discord.

Fuzzied
