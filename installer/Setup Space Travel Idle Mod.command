#!/bin/sh
# Space Travel Idle - Community Mod Setup launcher for macOS.
# Runs setup_mod.py from the folder this file lives in, wherever that is.
here=$(cd "$(dirname "$0")" && pwd)
cd "$here" || exit 1

if command -v python3 >/dev/null 2>&1; then
    python3 "$here/setup_mod.py"
else
    echo "Python 3 is not installed on this Mac."
    echo
    echo "Get it from https://www.python.org/downloads/macos/ or, if you have"
    echo "Homebrew, run:  brew install python"
    echo
    echo "Then run this again."
fi

echo
echo "Press Enter to close this window..."
read -r _
