#!/usr/bin/env bash
set -euo pipefail

# ---------------------------------------------------------------------------
# audio.cpp Native Studio — Linux desktop uninstaller
# Usage: ./uninstall.sh [--prefix <dir>] [--purge]
# ---------------------------------------------------------------------------

PREFIX="$HOME/.local/share/audiocpp-studio"
PURGE=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --prefix) PREFIX="${2:?--prefix needs a directory}"; shift 2 ;;
        --purge)  PURGE=1; shift ;;
        --help|-h)
            echo "Usage: $0 [--prefix <dir>] [--purge]"
            echo "  --prefix  Install directory to remove (default: ~/.local/share/audiocpp-studio)"
            echo "  --purge   Also remove settings and the voice library (~/.config/audiocpp-studio)"
            echo
            echo "Downloaded models are never touched: they live in the models root you"
            echo "chose, are large, and are not this installer's to delete."
            exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 1 ;;
    esac
done

DESKTOP_FILE="$HOME/.local/share/applications/audiocpp-studio.desktop"
ICON_DIR="$HOME/.local/share/icons/hicolor"
CONFIG="$HOME/.config/audiocpp-studio"
removed=0

# Refuse to delete something that is not ours. --prefix takes an arbitrary path
# and rm -rf does not ask twice, so check for the executable the installer put
# there before removing the directory.
if [[ -d "$PREFIX" ]]; then
    if [[ -x "$PREFIX/AudioCpp.Bindings.Gui" ]]; then
        echo "Removing $PREFIX ..."
        rm -rf "$PREFIX"
        removed=1
    else
        echo "$PREFIX does not look like an install of this app" >&2
        echo "(no AudioCpp.Bindings.Gui in it); refusing to delete it." >&2
        exit 1
    fi
fi

if [[ -f "$DESKTOP_FILE" ]]; then
    echo "Removing $DESKTOP_FILE ..."
    rm -f "$DESKTOP_FILE"
    removed=1
fi

for size in 16 24 32 48 64 128 256 512; do
    icon="$ICON_DIR/${size}x${size}/apps/audiocpp-studio.png"
    if [[ -f "$icon" ]]; then
        rm -f "$icon"
        removed=1
    fi
done

if [[ $PURGE -eq 1 && -d "$CONFIG" ]]; then
    echo "Purging settings at $CONFIG ..."
    rm -rf "$CONFIG"
    removed=1
fi

if [[ $removed -eq 1 ]]; then
    update-desktop-database "$HOME/.local/share/applications" 2>/dev/null || true
    gtk-update-icon-cache "$ICON_DIR" 2>/dev/null || true
    echo "Done."
    # An if, not "[[ ... ]] && echo": as the last command in the script, a false
    # test makes the AND-list's non-zero status the script's exit status, so a
    # successful uninstall reports failure.
    if [[ $PURGE -eq 0 && -d "$CONFIG" ]]; then
        echo "Settings kept at $CONFIG (--purge removes them)."
    fi
else
    echo "Nothing to uninstall at $PREFIX"
fi
