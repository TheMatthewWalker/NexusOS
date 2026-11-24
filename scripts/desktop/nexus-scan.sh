# nexus-scan [FOLDER]: scan Downloads (or FOLDER) for viruses with ClamAV,
# as the person. Prints one line for Settings and shows a notification.
# Nothing is moved or deleted. Exit: 0 clean, 1 found something, 2 not
# found / not readable, 3 definitions not downloaded yet, 4 the scan failed.
db="${NEXUS_CLAMAV_DB:-/var/lib/clamav}"
target="${1:-$HOME/Downloads}"
name=$(basename -- "$target")

say() {  # urgency, message
    notify-send --app-name="Virus scan" --urgency="$1" "Virus scan" "$2" 2>/dev/null || true
    printf '%s\n' "$2"
}

[ -e "$target" ] && [ -r "$target" ] || { say normal "$name was not found."; exit 2; }
if ! ls "$db"/main.c[vl]d >/dev/null 2>&1; then
    say normal "The virus definitions are still downloading (this computer must be online). Try again in a few minutes."
    exit 3
fi

notify-send --app-name="Virus scan" "Virus scan" "Scanning $name…" 2>/dev/null || true
status=0
out=$(clamscan --recursive --infected --no-summary --database="$db" -- "$target" 2>/dev/null) || status=$?
case "$status" in
    0) say normal "No viruses found in $name." ;;
    1)
        count=$(printf '%s\n' "$out" | grep -c ' FOUND$' || true)
        files=$(printf '%s\n' "$out" | grep ' FOUND$' | sed 's|: .* FOUND$||; s|.*/||' | head -n 3 | paste -sd ',' - | sed 's/,/, /g')
        say critical "Found $count infected file(s) in $name: $files. Nothing was deleted: remove them in Files." ;;
    *) say normal "The scan of $name could not finish." ; exit 4 ;;
esac
exit "$status"
