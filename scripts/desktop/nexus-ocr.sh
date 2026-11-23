# nexus-ocr: copy the text in part of the screen (Super+Shift+T), like
# PowerToys' Text Extractor: choose an area, and the text in it is read on
# this computer (Tesseract) and copied, ready to paste. Nothing leaves the
# computer. Escape while choosing copies nothing.
set -eu
geometry="$(slurp -d)" || exit 0
shot="$(mktemp --suffix=.png)"
trap 'rm -f "$shot"' EXIT
grim -g "$geometry" "$shot"
# English, plus the person's own language where Tesseract knows it.
code="${LANG%%_*}"
case "$code" in
  de) extra=deu ;; fr) extra=fra ;; es) extra=spa ;; it) extra=ita ;; pt) extra=por ;; nl) extra=nld ;;
  da) extra=dan ;; sv) extra=swe ;; nb|no) extra=nor ;; fi) extra=fin ;; pl) extra=pol ;; cs) extra=ces ;;
  sk) extra=slk ;; hu) extra=hun ;; ro) extra=ron ;; hr) extra=hrv ;; el) extra=ell ;; ru) extra=rus ;;
  uk) extra=ukr ;; tr) extra=tur ;; he) extra=heb ;; ar) extra=ara ;; hi) extra=hin ;; ja) extra=jpn ;;
  ko) extra=kor ;; zh) extra=chi_sim ;; *) extra="" ;;
esac
text="$(tesseract "$shot" - -l "eng${extra:++$extra}" 2>/dev/null | sed -e 's/[[:space:]]*$//' | sed -e '/./,$!d')"
if [ -z "$text" ]; then
  notify-send --app-name="Copy text" "No text found" "Try choosing the area again, a little larger."
  exit 0
fi
printf '%s' "$text" | wl-copy
notify-send --app-name="Copy text" "Text copied" "$(printf '%s' "$text" | head -c 120)"
