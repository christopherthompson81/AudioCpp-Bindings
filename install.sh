#!/usr/bin/env bash
set -euo pipefail

# ---------------------------------------------------------------------------
# audio.cpp Native Studio — Linux desktop installer
# Usage: ./install.sh [--prefix <dir>] [--no-engine]
# ---------------------------------------------------------------------------
#
# The app is a consumer of a native engine it does not build, so installing it
# is not just a publish: libaudiocpp, the audioio shim, the model specs and the
# VAD weights all have to come along. They are laid out under the prefix exactly
# as they sit in a source checkout, which is why no code changes to find them:
# both native resolvers and both asset lookups walk up from the executable
# looking for external/audio.cpp/... and native/audioio/..., and find them here.

PREFIX="$HOME/.local/share/audiocpp-studio"
WITH_ENGINE=1

while [[ $# -gt 0 ]]; do
    case "$1" in
        --prefix)    PREFIX="${2:?--prefix needs a directory}"; shift 2 ;;
        --no-engine) WITH_ENGINE=0; shift ;;
        --help|-h)
            echo "Usage: $0 [--prefix <dir>] [--no-engine]"
            echo "  --prefix     Install directory (default: ~/.local/share/audiocpp-studio)"
            echo "  --no-engine  Do not copy the engine; the app will need AUDIOCPP_NATIVE_DIR"
            exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 1 ;;
    esac
done

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENGINE="$ROOT/external/audio.cpp"
ENGINE_BIN="$ENGINE/build/bin"
APP="AudioCpp.Bindings.Gui"
ICON_DIR="$HOME/.local/share/icons/hicolor"
DESKTOP_FILE="$HOME/.local/share/applications/audiocpp-studio.desktop"

# ── Validate before writing anything ───────────────────────────────────────
# A publish takes long enough that discovering a missing engine afterwards
# wastes real time, and a half-installed prefix is worse than none.
if [[ $WITH_ENGINE -eq 1 && ! -e "$ENGINE_BIN/libaudiocpp.so" ]]; then
    echo "No engine build at $ENGINE_BIN." >&2
    echo "Build it first:  ./scripts/build-engine.sh -DENGINE_ENABLE_CUDA=ON" >&2
    echo "or pass --no-engine to install the app alone." >&2
    exit 1
fi
if [[ ! -e "$ROOT/native/audioio/build/libaudioio.so" ]]; then
    echo "No audioio shim at native/audioio/build." >&2
    echo "Build it first:  ./scripts/build-native.sh" >&2
    exit 1
fi

echo "Publishing the app..."
dotnet publish "$ROOT/src/AudioCpp.Bindings.Gui/AudioCpp.Bindings.Gui.csproj" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -o "$PREFIX" \
    --nologo -v quiet

# Symbols are for debugging a build tree, not for an install.
find "$PREFIX" -maxdepth 1 -name '*.pdb' -delete

echo "Installing the audio shim..."
install -Dm755 "$ROOT/native/audioio/build/libaudioio.so" \
               "$PREFIX/native/audioio/build/libaudioio.so"

if [[ $WITH_ENGINE -eq 1 ]]; then
    echo "Installing the engine..."
    mkdir -p "$PREFIX/external/audio.cpp/build/bin"
    # -a to keep the soname symlinks: libaudiocpp.so and .so.0 point at
    # .so.0.1.0, and copying them as three separate files triples the size.
    cp -a "$ENGINE_BIN"/libaudiocpp.so* "$PREFIX/external/audio.cpp/build/bin/"

    # Without the specs the catalogue is empty and any safetensors package fails
    # to load with "model spec not found for family"; without the VAD weights the
    # app silently falls back to fixed chunking, which is worse than an error.
    echo "Installing model specs and VAD weights..."
    mkdir -p "$PREFIX/external/audio.cpp/assets/framework/models"
    cp -a "$ENGINE/model_specs" "$PREFIX/external/audio.cpp/"
    cp -a "$ENGINE/assets/framework/models/silero_vad" \
          "$PREFIX/external/audio.cpp/assets/framework/models/"
fi

echo "Installing icons..."
for size in 16 24 32 48 64 128 256 512; do
    src="$ROOT/src/AudioCpp.Bindings.Gui/Assets/icon-${size}.png"
    [[ -f "$src" ]] || continue
    install -Dm644 "$src" "$ICON_DIR/${size}x${size}/apps/audiocpp-studio.png"
done

echo "Creating the desktop entry..."
mkdir -p "$(dirname "$DESKTOP_FILE")"
# StartupWMClass is what ties the running window to this entry, and so to this
# icon, in the taskbar and the switcher. Avalonia sets WM_CLASS from the assembly
# name; without the line the window shows a generic icon next to a correct one in
# the launcher, which looks like two different apps.
cat > "$DESKTOP_FILE" <<EOF
[Desktop Entry]
Type=Application
Name=audio.cpp Native Studio
Comment=Local audio model studio: transcription, diarization, separation and speech
Exec=$PREFIX/$APP
Icon=audiocpp-studio
StartupWMClass=$APP
Categories=AudioVideo;Audio;
Terminal=false
EOF

update-desktop-database "$HOME/.local/share/applications" 2>/dev/null || true
gtk-update-icon-cache "$ICON_DIR" 2>/dev/null || true

echo
echo "Installed to $PREFIX"
echo "Launch it from the application menu, or run: $PREFIX/$APP"
if [[ $WITH_ENGINE -eq 0 ]]; then
    echo
    echo "  Installed without the engine: set AUDIOCPP_NATIVE_DIR to a build's bin/"
    echo "  before launching, or the app will not find libaudiocpp."
fi
