# nexus-record screen|area: record the screen with what is playing, like
# Windows' Win+Alt+R. Pressed again (either key), it stops; the video is in
# ~/Videos/Screen recordings and a notification says so. Escape while
# choosing an area records nothing.
set -eu
mode="${1:-screen}"
state="${XDG_RUNTIME_DIR:-/tmp}/nexus-record"
if [ -f "$state.pid" ] && kill -0 "$(cat "$state.pid")" 2>/dev/null; then
  # Stopping: wf-recorder finishes the file on SIGINT.
  kill -INT "$(cat "$state.pid")"
  file="$(cat "$state.file" 2>/dev/null || true)"
  rm -f "$state.pid"
  sleep 1
  notify-send --app-name="Screen recording" "Recording saved" "In Videos, Screen recordings${file:+: $(basename "$file")}."
  exit 0
fi
dir="$(xdg-user-dir VIDEOS 2>/dev/null || echo "$HOME/Videos")/Screen recordings"
mkdir -p "$dir"
file="$dir/Recording $(date '+%Y-%m-%d %H.%M.%S').mp4"
case "$mode" in
  area)   geometry="$(slurp -d)" || exit 0; set -- -g "$geometry" ;;
  screen) output="$(hyprctl -j monitors | jq -r '.[] | select(.focused) | .name')"; set -- ${output:+-o "$output"} ;;
  *) echo "nexus-record screen|area" >&2; exit 2 ;;
esac
# The sound of what is playing (the default output's monitor), not the microphone.
wf-recorder "$@" --audio=@DEFAULT_MONITOR@ -f "$file" >/dev/null 2>&1 &
echo $! > "$state.pid"
printf '%s' "$file" > "$state.file"
notify-send --app-name="Screen recording" "Recording the screen" "Press Super+Alt+R again to stop."
