#!/bin/sh
# Space Travel Idle Community Patch: install on a Mac.
#
# 0.1 did this in Python. 0.1.1 needs no Python (Fuzzied, 29.09.2026), and
# on Windows the patch is simply unzipped into the game folder. A Mac needs
# four things a copy cannot do, so this plain shell script does them:
#   - find the game, which sits beside the .app, not in it
#   - tell run_bepinex.sh which .app to start
#   - make run_bepinex.sh executable and clear the download quarantine,
#     or macOS starts the game with no mod and no error
#   - hand over the Launch Options line, the one step only Steam can take
# It also takes out the game data 0.1 wrote on disk, which 0.1.1 cannot
# work on top of.
#
# Everything it finds and does goes into a report on the Desktop, so a
# problem report can be one file.
#
# Usage: double-click. Or in Terminal:  sh "Install Community Patch.command" [game folder]

here=$(cd "$(dirname "$0")" && pwd)
files="$here/files"
report="$HOME/Desktop/space-travel-idle-install-report.txt"
if ! : > "$report" 2>/dev/null; then
    report="$here/space-travel-idle-install-report.txt"
    : > "$report"
fi

say() {
    printf '%s\n' "$*"
    printf '%s\n' "$*" >> "$report"
}

finish() {
    echo
    echo "A report of all this is in: $report"
    echo "Press Enter to close this window..."
    read -r _
    exit "$1"
}

# A path dragged into Terminal arrives with a trailing space and every space
# escaped with a backslash. A pasted one may have quotes round it.
clean_path() {
    printf '%s' "$1" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' \
        -e "s/^['\"]//" -e "s/['\"]\$//" -e 's/\\\(.\)/\1/g' -e 's:/*$::'
}

# The .app inside a folder that holds the game data, or nothing.
find_app() {
    for a in "$1"/*.app; do
        if [ -f "$a/Contents/Resources/Data/resources.assets" ]; then
            printf '%s' "$a"
            return 0
        fi
    done
    return 1
}

# Every folder the game might be in, best guess first: Steam's own library,
# then every extra library in libraryfolders.vdf, each by the folder Steam's
# manifest names and by the usual name.
candidates() {
    steam="$HOME/Library/Application Support/Steam"
    {
        printf '%s\n' "$steam"
        for vdf in "$steam/steamapps/libraryfolders.vdf" "$steam/config/libraryfolders.vdf"; do
            [ -f "$vdf" ] && grep '"path"' "$vdf" | sed 's/.*"path"[[:space:]]*"\(.*\)".*/\1/'
        done
    } | while IFS= read -r lib; do
        [ -n "$lib" ] || continue
        acf="$lib/steamapps/appmanifest_1407860.acf"
        if [ -f "$acf" ]; then
            named=$(grep '"installdir"' "$acf" | sed 's/.*"installdir"[[:space:]]*"\(.*\)".*/\1/')
            [ -n "$named" ] && printf '%s\n' "$lib/steamapps/common/$named"
        fi
        printf '%s\n' "$lib/steamapps/common/Space Travel Idle"
    done
}

say "Space Travel Idle Community Patch 0.1.1, Mac install"
say "$(date)"
say "macOS $(sw_vers -productVersion 2>/dev/null), $(uname -m)"
say ""

if [ ! -d "$files/BepInEx/plugins" ]; then
    say "The 'files' folder is missing next to this script."
    say "Unzip the whole download and run this from inside the unzipped folder."
    finish 1
fi

# --- find the game ---
game=""
if [ -n "$1" ]; then
    game=$(clean_path "$1")
else
    tried=$(candidates)
    game=$(printf '%s\n' "$tried" | while IFS= read -r c; do
        if [ -n "$c" ] && find_app "$c" >/dev/null; then
            printf '%s' "$c"
            break
        fi
    done)
    if [ -z "$game" ]; then
        say "Could not find the game by myself. Looked in:"
        printf '%s\n' "$tried" | sort -u | while IFS= read -r c; do say "  $c"; done
        echo
        echo "Drag the game folder (the one holding Space Travel Idle.app) into"
        echo "this window and press Enter:"
        read -r typed
        game=$(clean_path "$typed")
    fi
fi
case "$game" in
    *.app) game=$(dirname "$game") ;;
esac
app=$(find_app "$game")
if [ -z "$app" ]; then
    say "Not the game folder: $game"
    say "It needs a .app inside it with Contents/Resources/Data/resources.assets."
    finish 1
fi
bundle=$(basename "$app")
data="$app/Contents/Resources/Data"
say "Game folder: $game"
say "App: $bundle"

exe=$(defaults read "$app/Contents/Info" CFBundleExecutable 2>/dev/null)
[ -n "$exe" ] || exe="SpaceTravelIdle"
if pgrep -x "$exe" >/dev/null 2>&1; then
    say ""
    say "The game is running. Quit it from inside the game, then run this again."
    finish 1
fi

# --- take out 0.1's game data ---
backup="$data/resources.assets.backup-original"
if [ -f "$backup" ]; then
    if cmp -s "$backup" "$data/resources.assets"; then
        rm -f "$backup"
    elif cp "$backup" "$data/resources.assets" && cmp -s "$backup" "$data/resources.assets"; then
        rm -f "$backup"
        say "Took out the game data 0.1 wrote. The community version's own data is back."
    else
        say "Could not put back the game data from before 0.1. Nothing is lost,"
        say "the untouched copy is still here: $backup"
        finish 1
    fi
fi

# --- keep a player's own Unlocker settings, once ---
cfg="$game/BepInEx/config/STIU.cfg"
if [ -f "$cfg" ] && ! cmp -s "$cfg" "$files/BepInEx/config/STIU.cfg" \
        && [ ! -f "$cfg.before-community-patch" ]; then
    cp "$cfg" "$cfg.before-community-patch"
    say "Kept your own Balancer settings as STIU.cfg.before-community-patch"
fi

# --- copy ---
if ! cp -R "$files/." "$game/"; then
    say "Copying the files into the game folder failed. See the lines above."
    finish 1
fi
say "Copied the mod loader and $(ls "$game/BepInEx/plugins" | grep -c '\.dll$') plugins."

script="$game/run_bepinex.sh"
tmp="$script.tmp"
sed "s|^executable_name=\"\"|executable_name=\"$bundle\"|" "$script" > "$tmp" && mv "$tmp" "$script"
chmod 755 "$script"
if grep -q "^executable_name=\"$bundle\"" "$script"; then
    say "Launcher set to start: $bundle"
else
    say "WARNING: could not set the app name in run_bepinex.sh"
fi

if command -v xattr >/dev/null 2>&1; then
    for name in run_bepinex.sh libdoorstop.dylib BepInEx; do
        [ -e "$game/$name" ] && xattr -dr com.apple.quarantine "$game/$name" 2>/dev/null
    done
    say "Cleared the download quarantine."
fi

if command -v codesign >/dev/null 2>&1; then
    sign=$(codesign -dv --verbose=2 "$app" 2>&1)
    printf '\nHow the game is signed:\n%s\n\n' "$sign" >> "$report"
    if printf '%s' "$sign" | grep -q 'flags=.*runtime'; then
        say ""
        say "WARNING: the game is signed with the hardened runtime, and macOS may"
        say "refuse to load the mod. Send Fuzzied the report before trying anything."
    fi
fi

line="\"$script\" %command%"
say ""
say "======================================================================"
say "ONE STEP LEFT, and the patch does nothing without it."
say "======================================================================"
say "In Steam: right-click Space Travel Idle, Properties, General."
say "Paste this into Launch Options, exactly as it is:"
say ""
say "  $line"
say ""
if command -v pbcopy >/dev/null 2>&1 && printf '%s' "$line" | pbcopy; then
    say "It is already copied, so just paste it (Cmd+V)."
fi
say "Then start the game from Steam as normal. If it worked there will be a"
say "BepInEx/LogOutput.log file in the game folder."
finish 0
