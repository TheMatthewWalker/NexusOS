# nexus-snap left|right|up|down: Windows' Super+arrow keys. A floating window
# goes to the left or right half of its screen, Up maximises it, Down puts it
# back as it was. Tiled windows move in that direction instead.
set -eu
dir="${1:-}"
win="$(hyprctl -j activewindow)"
[ "$(jq -r '.address // empty' <<<"$win")" ] || exit 0
if [ "$(jq -r '.floating' <<<"$win")" != true ]; then
  case "$dir" in left) d=l ;; right) d=r ;; up) d=u ;; down) d=d ;; *) exit 2 ;; esac
  exec hyprctl dispatch movewindow "$d"
fi
mon="$(hyprctl -j monitors | jq -c --argjson id "$(jq '.monitor' <<<"$win")" '.[] | select(.id == $id)')"
read -r mx my mw mh scale top bottom left right < <(jq -r '[.x, .y, .width, .height, .scale, .reserved[1], .reserved[3], .reserved[0], .reserved[2]] | @tsv' <<<"$mon")
# Logical size: the screen's pixels divided by its scale, less the top bar.
w=$(awk -v a="$mw" -v s="$scale" -v l="$left" -v r="$right" 'BEGIN { printf "%d", a / s - l - r }')
h=$(awk -v a="$mh" -v s="$scale" -v t="$top" -v b="$bottom" 'BEGIN { printf "%d", a / s - t - b }')
x0=$((mx + left)); y0=$((my + top)); half=$((w / 2))
addr="address:$(jq -r '.address' <<<"$win")"
case "$dir" in
  left)  hyprctl --batch "dispatch fullscreenstate 0 0 ; dispatch resizewindowpixel exact $half $h,$addr ; dispatch movewindowpixel exact $x0 $y0,$addr" ;;
  right) hyprctl --batch "dispatch fullscreenstate 0 0 ; dispatch resizewindowpixel exact $half $h,$addr ; dispatch movewindowpixel exact $((x0 + half)) $y0,$addr" ;;
  up)    hyprctl dispatch fullscreen 1 ;;
  down)  if [ "$(jq -r '.fullscreen' <<<"$win")" != 0 ]; then hyprctl dispatch fullscreen 1
         else hyprctl --batch "dispatch resizewindowpixel exact $((w * 2 / 3)) $((h * 2 / 3)),$addr ; dispatch centerwindow"; fi ;;
  *) exit 2 ;;
esac
