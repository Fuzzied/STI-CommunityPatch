#!/bin/sh
# Space Travel Idle - Community Mod: look at this Mac and write a report.
# Changes nothing. Run this first.
here=$(cd "$(dirname "$0")" && pwd)
cd "$here" || exit 1

if command -v python3 >/dev/null 2>&1; then
    python3 "$here/tools/mac_check.py"
else
    echo "Python 3 is not installed on this Mac."
    echo
    echo "Get it from https://www.python.org/downloads/macos/ or, if you have"
    echo "Homebrew, run:  brew install python"
fi

echo
echo "Press Enter to close this window..."
read -r _
