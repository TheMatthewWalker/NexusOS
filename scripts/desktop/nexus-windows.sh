# nexus-windows: Windows as an app, for computers with Windows installed
# beside NexusOS. The computer always starts NexusOS (no start-up menu);
# this restarts into Windows once, through systemd-boot's one-time entry
# (allowed for the person at the screen without a password). The restart
# after that comes back to NexusOS by itself.
#   nexus-windows          ask, then restart into Windows
#   nexus-windows entry    at login: put "Windows" in the apps only where it is installed
set -eu
entry="$(systemctl reboot --boot-loader-entry=help 2>/dev/null | grep -xE 'auto-windows|windows[A-Za-z0-9._-]*' | head -1 || true)"
app="${XDG_DATA_HOME:-$HOME/.local/share}/applications/nexus-windows.desktop"
case "${1:-ask}" in
  entry)
    if [ -n "$entry" ]; then
      mkdir -p "$(dirname "$app")"
      printf '%s\n' '[Desktop Entry]' 'Type=Application' 'Name=Windows' \
        'Comment=Restart into Windows (NexusOS starts again the time after)' \
        'Exec=nexus-windows' 'Icon=system-reboot' 'Categories=System;' 'Keywords=windows;dual boot;restart;games;' > "$app"
    else
      rm -f "$app"
    fi ;;
  ask)
    if [ -z "$entry" ]; then
      notify-send --app-name=Windows "Windows was not found" "Windows is not installed beside NexusOS on this computer."
      exit 1
    fi
    zenity --question --title="Windows" --ok-label="Restart into Windows" --cancel-label="Cancel" \
      --text="Restart into Windows?\n\nSave your work first. The computer starts Windows this once, and NexusOS again the time after." || exit 0
    exec systemctl reboot --boot-loader-entry="$entry" ;;
  *) echo "nexus-windows [entry]" >&2; exit 2 ;;
esac
