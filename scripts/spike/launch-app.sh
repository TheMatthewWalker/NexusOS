#!/usr/bin/env bash
# Open an app chosen in the SpaceUI spike and snap it to the left work-area tile.
# Both shells call this, so window placement is identical: snapping is the
# compositor's job, not the shell's. Usage: launch-app.sh <app-id>
set -euo pipefail
app="$1"
# Work area from design-tokens.json: side margin 14, top 58, bottom 96, gutter 12.
read -r W H < <(swaymsg -t get_outputs -r | jq -r '.[0].current_mode | "\(.width) \(.height)"')
x=14; y=58; w=$(( (W - 28 - 12) / 2 )); h=$(( H - 58 - 96 ))
case "$app" in
  terminal) cmd=(foot --app-id=nexus-spike-terminal) ;;
  # The spike has no Files app yet; a labelled stand-in window proves launch and snapping.
  *) cmd=(foot --app-id="nexus-spike-$app" --title="${app^} (spike stand-in)" sh -c "echo '${app^} stand-in window: the real app is not built yet.'; exec sleep infinity") ;;
esac
"${cmd[@]}" >/dev/null 2>&1 &
for _ in $(seq 1 50); do
  if swaymsg -t get_tree -r | jq -e --arg id "nexus-spike-$app" '.. | objects | select(.app_id? == $id)' >/dev/null; then break; fi
  sleep 0.1
done
swaymsg "[app_id=\"nexus-spike-$app\"] floating enable, resize set $w $h, move absolute position $x $y, focus" >/dev/null
