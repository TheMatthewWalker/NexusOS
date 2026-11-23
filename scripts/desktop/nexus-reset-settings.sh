# nexus-reset-settings: start fresh with NexusOS's own settings, keeping
# every file and app (Settings, About). The settings folder
# (~/.config/nexusos: desktop, language and region, Nexus's notes and
# choices) is moved aside, never deleted, and the desktop restarts on its
# defaults. "undo" puts the most recent one back.
#   nexus-reset-settings          reset
#   nexus-reset-settings undo     put the settings from before the last reset back
#   nexus-reset-settings status   prints the folder a reset kept, if any
set -eu
config="${XDG_CONFIG_HOME:-$HOME/.config}"
current="$config/nexusos"
latest() { find "$config" -maxdepth 1 -type d -name 'nexusos-before-reset-*' 2>/dev/null | sort | tail -1; }
case "${1:-reset}" in
  reset)
    if [ -d "$current" ]; then mv "$current" "$config/nexusos-before-reset-$(date '+%Y-%m-%d-%H%M%S')"; fi
    systemctl --user restart nexus-shell.service ;;
  undo)
    kept="$(latest)"
    if [ -z "$kept" ]; then echo "Nothing to put back." >&2; exit 1; fi
    if [ -d "$current" ]; then mv "$current" "$config/nexusos-after-reset-$(date '+%Y-%m-%d-%H%M%S')"; fi
    mv "$kept" "$current"
    systemctl --user restart nexus-shell.service ;;
  status) latest ;;
  *) echo "nexus-reset-settings [undo|status]" >&2; exit 2 ;;
esac
