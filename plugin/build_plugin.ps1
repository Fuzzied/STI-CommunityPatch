# Builds the mod plugins against the game's own DLLs + BepInEx core.
# Resolves all paths relative to this script; run from anywhere.
# -GameFolder is the folder holding SpaceTravelIdle.exe. BepInEx core comes
# from this project's bepinex payload when it is there, otherwise from the
# game folder, which has it once the patch is installed.
param(
    [string]$GameFolder = "C:\Games\Steam\steamapps\common\Space Travel Idle"
)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Split-Path -Parent $here
$managed = Join-Path $GameFolder "SpaceTravelIdle_Data\Managed"
$bepcore = Join-Path $proj "bepinex\payload\BepInEx\core"
if (-not (Test-Path $bepcore)) { $bepcore = Join-Path $GameFolder "BepInEx\core" }
foreach ($need in @((Join-Path $managed "Assembly-CSharp.dll"), (Join-Path $bepcore "BepInEx.dll"))) {
    if (-not (Test-Path $need)) { throw ("NOT FOUND: " + $need + "  (pass -GameFolder, and install the patch once so BepInEx is there)") }
}
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

# CommunitySettings.cs is the shared settings section the mod's own toggle rows
# live in. BepInEx gives every plugin its own assembly and there is no shared
# library to put it in, so every plugin that adds a settings row gets its own
# copy compiled in. See the header of that file. AutoStart has no row in that
# section, but its own Auto start list scrolls and sizes by the same rule, so
# it compiles the file in for the scrolling list.
$shared = @("DevConsoleOff", "TooltipTime", "Hotkeys", "TopOff", "CargoBay",
    "LoadoutPlus", "FilterMemory", "AutoStart")

# Plugins built from more than one source file. The three cargo lines are one
# system - the doors ask the pocket whether a producer still has somewhere to
# put its output, and the pocket is what stops a sealed hold from losing your
# in-flight income - and BepInEx gives every assembly its own statics, so
# splitting them into separate DLLs would mean reaching across assemblies by
# reflection for what is really just a bool. One DLL, three files, three
# toggles in its config.
$extra = @{
    "CargoBay" = @("CargoDoors", "QuantumPocket", "CargoBayUI")
}

# 0.1.1: every plugin asks CommunityToggle.cs whether the player turned it
# off in BepInEx\config\STI Community Patch.cfg, so every one compiles it in.
# DataPatches carries the game data changes, exported from setup_mod.py by
# tools\export_patches.py, as an embedded resource, and reads it with its own
# small JSON reader (Unity's JsonUtility returned no patches in the game).
$patchList = Join-Path $here "community_patches.json"
foreach ($name in @("SmoothDamage", "CommunityFixes", "NoDiscard", "AlchemyQoL", "LoadoutPlus", "UIFixes", "OfflineFix", "DevConsoleOff", "ScrollKeeper", "TooltipTime", "Hotkeys", "BigBangExtras", "FilterMemory", "AutoStart", "BigBangIcons", "CardIcons", "TopOff", "CargoBay", "PlanetaryMemory", "FormulaCache", "DataPatches")) {
    $out = Join-Path $here ($name + ".dll")
    $sources = @((Join-Path $here ($name + ".cs")), (Join-Path $here "CommunityToggle.cs"))
    $more_args = @()
    if ($name -eq "DataPatches") {
        if (-not (Test-Path $patchList)) { throw ("NOT FOUND: " + $patchList + "  (run tools\export_patches.py)") }
        $more_args += ("/resource:" + $patchList + ",community_patches.json")
        $more_args += ("/r:" + (Join-Path $managed "UnityEngine.TextRenderingModule.dll"))
    }
    if ($shared -contains $name) {
        $sources += (Join-Path $here "CommunitySettings.cs")
    }
    if ($extra.ContainsKey($name)) {
        foreach ($more in $extra[$name]) {
            $sources += (Join-Path $here ($more + ".cs"))
        }
    }
    & $csc /nologo /target:library /optimize+ ("/out:" + $out) `
        ("/r:" + (Join-Path $bepcore "BepInEx.dll")) `
        ("/r:" + (Join-Path $bepcore "0Harmony.dll")) `
        ("/r:" + (Join-Path $managed "Assembly-CSharp.dll")) `
        ("/r:" + (Join-Path $managed "UnityEngine.dll")) `
        ("/r:" + (Join-Path $managed "UnityEngine.CoreModule.dll")) `
        ("/r:" + (Join-Path $managed "UnityEngine.UI.dll")) `
        ("/r:" + (Join-Path $managed "UnityEngine.UIModule.dll")) `
        ("/r:" + (Join-Path $managed "UnityEngine.InputLegacyModule.dll")) `
        ("/r:" + (Join-Path $managed "UnityEngine.IMGUIModule.dll")) `
        ("/r:" + (Join-Path $managed "UnityEngine.ImageConversionModule.dll")) `
        ("/r:" + (Join-Path $managed "Unity.TextMeshPro.dll")) `
        $more_args $sources
    if ($LASTEXITCODE -ne 0) { throw ("BUILD FAILED: " + $name) }
    if (Test-Path $out) { Write-Output ("built: " + $out) }
}

# Patchers go in BepInEx\patchers and run before any game code loads, so they
# are built against BepInEx and Mono.Cecil only, never the game's DLLs.
foreach ($name in @("UIFixesEarlyWindow")) {
    $out = Join-Path $here ($name + ".dll")
    & $csc /nologo /target:library /optimize+ ("/out:" + $out) `
        ("/r:" + (Join-Path $bepcore "BepInEx.dll")) `
        ("/r:" + (Join-Path $bepcore "Mono.Cecil.dll")) `
        (Join-Path $here ($name + ".cs")) (Join-Path $here "CommunityToggle.cs")
    if ($LASTEXITCODE -ne 0) { throw ("BUILD FAILED: " + $name) }
    if (Test-Path $out) { Write-Output ("built: " + $out) }
}
