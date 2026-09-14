#!/usr/bin/env bash
set -euo pipefail

# ---------------------------------------------------------------------------
# audio.cpp Native Studio — macOS .app bundler
# Usage: ./package-macos.sh [--out <dir>] [--icon <square.png>]
#                           [--no-engine] [--framework-dependent]
#
# Produces "audio.cpp Studio.app": the only form macOS gives a real Dock icon, a
# real name in the menu bar, and a double-clickable launcher. The app also sets
# its Dock icon at runtime (MacDockIcon.cs) precisely because during development
# it is usually NOT bundled.
# ---------------------------------------------------------------------------

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENGINE="$SCRIPT_DIR/external/audio.cpp"
OUT_DIR="$SCRIPT_DIR/dist"
ICON_OVERRIDE=""
WITH_ENGINE=1

# ⚠ Self-contained by DEFAULT, which is not the usual preference. A framework-
# dependent bundle launched from Finder inherits no PATH and finds .NET only via
# DOTNET_ROOT or the official installer's location -- so on a machine where
# dotnet came from Homebrew, double-clicking fails with "you must install .NET"
# even though `dotnet run` works. An .app that cannot be double-clicked has
# missed its point.
SELF_CONTAINED="true"

usage() {
    echo "Usage: $0 [--out <dir>] [--icon <square.png>] [--no-engine] [--framework-dependent]"
    echo "  --out                  Where to write the bundle (default: ./dist)"
    echo "  --icon                 SQUARE PNG to build the icon from"
    echo "                         (default: src/AudioCpp.Bindings.Gui/Assets/AppIcon.png)"
    echo "  --no-engine            Do not bundle libaudiocpp, the specs or the VAD weights"
    echo "  --framework-dependent  Do not bundle the .NET runtime; needs .NET installed"
    echo "                         where Finder can find it (see the note in this script)"
}

# set -u turns a missing option argument into "$2: unbound variable" rather than
# anything a user can act on.
need_arg() {
    if [[ $# -lt 2 || -z "$2" ]]; then
        echo "Option $1 needs a value." >&2; usage >&2; exit 1
    fi
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --out)                 need_arg "$@"; OUT_DIR="$2";       shift 2 ;;
        --icon)                need_arg "$@"; ICON_OVERRIDE="$2"; shift 2 ;;
        --no-engine)           WITH_ENGINE=0; shift ;;
        --framework-dependent) SELF_CONTAINED="false"; shift ;;
        --help|-h)             usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 1 ;;
    esac
done

if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "This packages a macOS .app and only runs on macOS." >&2
    exit 1
fi

case "$(uname -m)" in
    arm64)  RID="osx-arm64" ;;
    x86_64) RID="osx-x64"   ;;
    *) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

APP="$OUT_DIR/audio.cpp Studio.app"
CONTENTS="$APP/Contents"
EXE="AudioCpp.Bindings.Gui"
ICON_SRC="${ICON_OVERRIDE:-$SCRIPT_DIR/src/AudioCpp.Bindings.Gui/Assets/AppIcon.png}"
ENGINE_LIB="$ENGINE/build/bin/libaudiocpp.dylib"
SHIM="$SCRIPT_DIR/native/audioio/build/libaudioio.dylib"

# ── Validate before destroying anything ────────────────────────────────────
# ⚠ EVERY CHECK BELONGS HERE, above the rm -rf and the publish. Validating after
# them means a typo'd --icon deletes a working bundle and burns a full Release
# build before saying so.
if [[ ! -f "$ICON_SRC" ]]; then
    echo "Icon source not found: $ICON_SRC" >&2
    echo "Generate it with ./scripts/make-icons.sh" >&2
    exit 1
fi

# ⚠ THE SOURCE MUST BE SQUARE. `sips -z h w` resizes to EXACT dimensions, so a
# non-square source is stretched, not letterboxed. Fail loudly rather than
# quietly distorting.
SRC_W="$(sips -g pixelWidth  "$ICON_SRC" | awk '/pixelWidth/{print $2}')"
SRC_H="$(sips -g pixelHeight "$ICON_SRC" | awk '/pixelHeight/{print $2}')"
if [[ -z "$SRC_W" || -z "$SRC_H" ]]; then
    echo "Could not read image dimensions from $ICON_SRC -- is it a PNG?" >&2
    exit 1
fi
if [[ "$SRC_W" != "$SRC_H" ]]; then
    echo "Icon source is ${SRC_W}x${SRC_H}, not square: macOS icons are square and" >&2
    echo "this would be stretched to fit. Pass --icon with a square PNG." >&2
    exit 1
fi

if [[ $WITH_ENGINE -eq 1 && ! -f "$ENGINE_LIB" ]]; then
    echo "No engine build at $ENGINE_LIB." >&2
    echo "Build it first:  ./scripts/build-engine.sh" >&2
    echo "or pass --no-engine to bundle the app alone." >&2
    exit 1
fi

echo "Building the bundle ($RID, self-contained=$SELF_CONTAINED)..."
rm -rf "$APP"
mkdir -p "$CONTENTS/MacOS" "$CONTENTS/Resources"

dotnet publish "$SCRIPT_DIR/src/AudioCpp.Bindings.Gui/AudioCpp.Bindings.Gui.csproj" \
    -c Release \
    -r "$RID" \
    --self-contained "$SELF_CONTAINED" \
    -o "$CONTENTS/MacOS" \
    --nologo -v quiet

# Debug symbols do not belong in a distributable bundle, and codesign counts a
# .pdb as a nested code object it cannot sign, which fails the bundle seal.
find "$CONTENTS/MacOS" -type f -name "*.pdb" -delete

# The engine and the shim go where the resolvers already look: both walk up from
# the executable for external/audio.cpp/build/bin and native/audioio/build. That
# is inside Contents/MacOS here, which is also where dyld's default search ends
# up, so the layout needs no special-casing in the app.
if [[ -f "$SHIM" ]]; then
    mkdir -p "$CONTENTS/MacOS/native/audioio/build"
    cp "$SHIM" "$CONTENTS/MacOS/native/audioio/build/"
else
    echo "⚠ No audioio shim at $SHIM; capture and playback will not work." >&2
    echo "  Build it with ./scripts/build-native.sh and run this again." >&2
fi

if [[ $WITH_ENGINE -eq 1 ]]; then
    echo "Bundling the engine..."
    mkdir -p "$CONTENTS/MacOS/external/audio.cpp/build/bin" \
             "$CONTENTS/MacOS/external/audio.cpp/assets/framework/models"
    cp "$ENGINE/build/bin"/libaudiocpp*.dylib "$CONTENTS/MacOS/external/audio.cpp/build/bin/"
    # Without the specs the catalogue is empty; without the VAD weights the app
    # falls back to fixed chunking silently, which is worse than an error.
    cp -R "$ENGINE/model_specs" "$CONTENTS/MacOS/external/audio.cpp/"
    cp -R "$ENGINE/assets/framework/models/silero_vad" \
          "$CONTENTS/MacOS/external/audio.cpp/assets/framework/models/"
fi

# ── Icon ───────────────────────────────────────────────────────────────────
# macOS wants .icns, and iconutil wants a directory of specific sizes with
# specific names.
echo "Generating the icon..."
ICONSET_DIR="$(mktemp -d)"
ICONSET="$ICONSET_DIR/AudioCpp.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
    sips -z $size $size             "$ICON_SRC" --out "$ICONSET/icon_${size}x${size}.png"    >/dev/null 2>&1
    sips -z $((size*2)) $((size*2)) "$ICON_SRC" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null 2>&1
done
iconutil -c icns "$ICONSET" -o "$CONTENTS/Resources/AudioCpp.icns"
rm -rf "$ICONSET_DIR"

# ── Info.plist ─────────────────────────────────────────────────────────────
# NSMicrophoneUsageDescription is not optional theatre: macOS kills an app that
# touches audio input without it, and live capture is a first-class feature.
# NSPrincipalClass is what makes LaunchServices treat this as a Cocoa app at all;
# without it the menu-bar name and activation behaviour stay wrong.
cat > "$CONTENTS/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>              <string>audio.cpp Studio</string>
    <key>CFBundleDisplayName</key>       <string>audio.cpp Studio</string>
    <key>CFBundleIdentifier</key>        <string>com.christopherthompson81.audiocpp-studio</string>
    <key>CFBundleVersion</key>           <string>1.0</string>
    <key>CFBundleShortVersionString</key><string>1.0</string>
    <key>CFBundlePackageType</key>       <string>APPL</string>
    <key>CFBundleExecutable</key>        <string>$EXE</string>
    <key>CFBundleIconFile</key>          <string>AudioCpp</string>
    <key>NSPrincipalClass</key>          <string>NSApplication</string>
    <key>LSMinimumSystemVersion</key>    <string>12.0</string>
    <key>NSHighResolutionCapable</key>   <true/>
    <key>NSMicrophoneUsageDescription</key>
    <string>audio.cpp Studio records audio so it can transcribe and process it.</string>
</dict>
</plist>
PLIST

chmod +x "$CONTENTS/MacOS/$EXE"

# ── Signing ────────────────────────────────────────────────────────────────
# Ad-hoc signing so a locally built app opens without complaint. An UNSIGNED
# bundle that has picked up the quarantine attribute is reported as "damaged and
# can't be opened", which sends people hunting for a corrupt download. This is
# NOT notarization: `spctl -a -t exec` still says rejected, so a downloaded copy
# needs right-click → Open or a real Developer ID.
#
# ⚠ --deep, DESPITE Apple deprecating it for signing. The usual advice is to sign
# nested code explicitly, inside out, and it cannot work on this layout: a .NET
# publish puts the whole payload in Contents/MacOS/, codesign treats every file
# there as nested code, and it refuses to sign the .json config the host needs at
# runtime. The real fix is a layout with only executables under MacOS/, which is
# a bigger change than a packaging script should make.
#
# ⚠ AD-HOC SIGNING CHANGES THE CODE HASH ON EVERY REBUILD, and TCC keys
# permissions to bundle ID + signature, so a rebuilt app can read as a different
# app and quietly lose its microphone grant. Know that before blaming capture.
if SIGN_ERR="$(codesign --force --deep --sign - "$APP" 2>&1)"; then
    echo "Ad-hoc signed."
else
    echo "⚠ Ad-hoc signing failed; it runs locally but Gatekeeper may call it damaged" >&2
    echo "$SIGN_ERR" | sed 's/^/    /' >&2
fi

echo
echo "Built $APP"
echo "  open \"$APP\"                      # run it"
echo "  cp -R \"$APP\" /Applications/       # install it (-R, not -r: -r mangles bundles)"
if [[ "$SELF_CONTAINED" != "true" ]]; then
    echo
    echo "  ⚠ Framework-dependent: double-clicking needs .NET where Finder can find it."
fi
