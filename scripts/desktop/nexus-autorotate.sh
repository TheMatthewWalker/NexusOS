# Convertible laptops and tablets: the built-in screen, its touch and the
# pen turn with the computer (iio-sensor-proxy's accelerometer), as Windows
# does. Started by Hyprland; quits at once on a computer with no
# accelerometer or no built-in screen. Rotation lock (Settings, Display)
# is the file ~/.config/nexusos/rotation-lock: while it exists, nothing turns.

lock="${XDG_CONFIG_HOME:-$HOME/.config}/nexusos/rotation-lock"

# "normal", "left-up", "bottom-up", "right-up" -> Hyprland's transform.
transform_of() {
    case "$1" in
        normal) echo 0 ;;
        left-up) echo 1 ;;
        bottom-up) echo 2 ;;
        right-up) echo 3 ;;
        *) echo "" ;;
    esac
}

screen=$(hyprctl monitors -j | jq -r '[.[] | select(.name | startswith("eDP") or startswith("DSI"))][0].name // empty')
[ -n "$screen" ] || exit 0

monitor-sensor --accel | while IFS= read -r line; do
    case "$line" in
        *"No accelerometer"*) exit 0 ;;
        *"orientation changed: "*) orientation=${line##*orientation changed: } ;;
        *"Has accelerometer (orientation: "*) orientation=${line##*orientation: }; orientation=${orientation%)} ;;
        *) continue ;;
    esac
    [ -e "$lock" ] && continue
    t=$(transform_of "$orientation")
    [ -n "$t" ] || continue
    hyprctl --batch "keyword monitor $screen,transform,$t ; keyword input:touchdevice:transform $t ; keyword input:tablet:transform $t" >/dev/null
done
