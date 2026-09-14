#!/usr/bin/env bash
# Draws the app icon from the same description the web UI's badge uses, and
# renders every size the platforms want.
#
#   ./make-icons.sh [--font <otf/ttf>] [--out <dir>]
#
# The mark is not a drawing anyone made: audio.cpp's web UI builds it in CSS --
# a rounded square filled with a 135deg cyan-to-blue gradient, with a 900-weight
# "A" on it (webui/native/src/app.css, .brand .mark). There is no image file
# upstream to copy, so this reproduces the rule rather than tracing it, which is
# also why it is a script and not an opaque PNG with no provenance.
#
# The dark palette is used at every size. An icon sits on a desktop, not inside
# the app, so it cannot follow the app's theme -- and the dark variant's cyan is
# the brighter, more legible one against both light and dark backgrounds.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/src/AudioCpp.Bindings.Gui/Assets"
FONT=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --font) FONT="${2:?--font needs a path}"; shift 2 ;;
        --out)  OUT="${2:?--out needs a path}";   shift 2 ;;
        --help|-h) sed -n '2,12p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 1 ;;
    esac
done

command -v convert >/dev/null || { echo "ImageMagick (convert) is required." >&2; exit 1; }

# Inter is the typeface the app itself renders in (Avalonia.Fonts.Inter), and
# Black is the 900 weight the CSS asks for. Anything else is a near miss, so
# say which font was used rather than silently substituting one.
if [[ -z "$FONT" ]]; then
    for candidate in \
        /usr/share/fonts/opentype/inter/Inter-Black.otf \
        /usr/share/fonts/truetype/inter/Inter-Black.ttf \
        /Library/Fonts/Inter-Black.otf \
        "$HOME/Library/Fonts/Inter-Black.otf"
    do
        [[ -f "$candidate" ]] && { FONT="$candidate"; break; }
    done
fi
if [[ -z "$FONT" || ! -f "$FONT" ]]; then
    echo "Inter Black not found; pass --font <file>. The committed assets were" >&2
    echo "generated with Inter-Black, and another face will not match the app." >&2
    exit 1
fi
echo "font: $FONT"

# .brand .mark, at 1024: 8/30 of the side is the corner radius, and the glyph
# fills the same proportion of the tile as the CSS does at 30px.
SIDE=1024
RADIUS=273          # 1024 * 8/30, the CSS radius scaled up
CYAN="#42e8d5"
BLUE="#6aa8ff"
GLYPH="#041619"     # --text-invert

# A smooth gradient defeats PNG's default adaptive filter: measured on the 1024
# master, the Sub filter writes 407 KB where adaptive writes 539 KB, for the same
# pixels. Worth 130 KB across assets that are committed to the repository.
# -depth 8 because ImageMagick's Q16 build writes 16 bits per channel by default,
# which doubles an icon nothing will ever render at that precision.
PNG_OPTS=(-depth 8 -define png:compression-level=9 -define png:compression-filter=1)

mkdir -p "$OUT"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# A 135deg CSS gradient on a square runs corner to corner. ImageMagick's linear
# gradient only goes top-to-bottom, so it is drawn at sqrt(2) the side, rotated,
# and cropped back -- which is the diagonal, exactly.
DIAG=$(python3 -c "print(int($SIDE * 1.41421356 + 2))")
convert -size "${DIAG}x${DIAG}" "gradient:${CYAN}-${BLUE}" \
        -rotate -45 -gravity center -extent "${SIDE}x${SIDE}" \
        "$WORK/fill.png"

# The rounded square, as a mask: white where the tile is, transparent elsewhere.
convert -size "${SIDE}x${SIDE}" xc:none \
        -fill white -draw "roundrectangle 0,0 $((SIDE-1)),$((SIDE-1)) $RADIUS,$RADIUS" \
        "$WORK/mask.png"

convert "$WORK/fill.png" "$WORK/mask.png" -alpha off -compose CopyOpacity -composite \
        "$WORK/tile.png"

# The glyph is rendered on its own and trimmed to its ink before being centred.
# -annotate positions by the baseline and carries the font's side bearings with
# it, so a centred annotation puts the letter's METRICS in the middle and the
# visible letter noticeably right of, and below, centre.
convert -background none -fill "$GLYPH" -font "$FONT" -pointsize 700 \
        label:A -trim +repage "$WORK/glyph.png"
convert "$WORK/tile.png" "$WORK/glyph.png" \
        -gravity center -composite +repage "${PNG_OPTS[@]}" "$OUT/AppIcon.png"
echo "wrote $OUT/AppIcon.png (${SIDE}x${SIDE})"

# Windows wants one .ico carrying every size it might draw; 256 is the largest
# it reads, and the small sizes are separate renders rather than downscales of
# 256 so the glyph stays legible at 16.
ICO_SIZES=(16 24 32 48 64 128 256)
for size in "${ICO_SIZES[@]}"; do
    convert "$OUT/AppIcon.png" -resize "${size}x${size}" "$WORK/ico-$size.png"
done
convert "${ICO_SIZES[@]/#/$WORK/ico-}" "$OUT/icon.ico" 2>/dev/null \
    || convert $(printf "$WORK/ico-%s.png " "${ICO_SIZES[@]}") "$OUT/icon.ico"
echo "wrote $OUT/icon.ico (${ICO_SIZES[*]})"

# Freedesktop icon themes want a PNG per size. 256 is what install.sh registers;
# the rest are there so a panel or switcher picks its own best match instead of
# rescaling one that does not fit.
for size in 16 24 32 48 64 128 256 512; do
    convert "$OUT/AppIcon.png" -resize "${size}x${size}" "${PNG_OPTS[@]}" "$OUT/icon-${size}.png"
done
echo "wrote $OUT/icon-{16,24,32,48,64,128,256,512}.png"
