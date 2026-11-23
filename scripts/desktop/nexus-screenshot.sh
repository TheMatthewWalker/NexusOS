# nexus-screenshot area|screen|window: a picture of the screen, like Windows'
# Snipping Tool (Super+Shift+S), Print Screen and Alt+Print Screen. It is saved
# in ~/Pictures/Screenshots, copied to the clipboard, and a notification says
# where; Escape while choosing an area takes nothing.
set -eu
mode="${1:-area}"
dir="$(xdg-user-dir PICTURES 2>/dev/null || echo "$HOME/Pictures")/Screenshots"
mkdir -p "$dir"
file="$dir/Screenshot $(date '+%Y-%m-%d %H.%M.%S').png"
case "$mode" in
  area)   geometry="$(slurp -d)" || exit 0; grim -g "$geometry" "$file" ;;
  window) geometry="$(hyprctl -j activewindow | jq -r 'if .at then "\(.at[0]),\(.at[1]) \(.size[0])x\(.size[1])" else empty end')"
          [ -n "$geometry" ] || exit 0
          grim -g "$geometry" "$file" ;;
  screen) output="$(hyprctl -j monitors | jq -r '.[] | select(.focused) | .name')"
          grim ${output:+-o "$output"} "$file" ;;
  *) echo "nexus-screenshot area|screen|window" >&2; exit 2 ;;
esac
wl-copy --type image/png < "$file"
notify-send --app-name=Screenshot --icon="$file" "Screenshot taken" "Copied, and saved in Pictures, Screenshots."
