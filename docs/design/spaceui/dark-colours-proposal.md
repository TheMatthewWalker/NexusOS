# Dark colour set: proposal

**Status: proposed (22 Oct 2026). It waits on your approval.** These are the
colours the Nexus shell uses today (`root.dark` in `shell/qml/shell.qml`).
They replace the light Pearl colours in `design-tokens.json` for the approved
dark desktop. The layout and motion tokens are unchanged.

## Surfaces

| Surface | Value | Use |
|---|---|---|
| Wallpaper | radial gradient `#0B0F24` → `#06081A` → `#020309`, with faint stars | Desktop background (`shell/qml/Wallpaper.qml`) |
| Glass | `rgba(0.04, 0.07, 0.15, 0.80)` | Chat bar, app planets, system panel, launcher planets |

## Colours, with contrast measured on the glass over the wallpaper

The ratio shown is the worst case of the glass over the wallpaper's darkest
and lightest points.

| Token | Value | Use | Contrast | Needs |
|---|---|---|---|---|
| `ink` | `#E3EAFF` | Main text: titles, labels, the answer | 15.6:1 | 4.5:1 |
| `inkSecondary` | `#9AA9C9` | Secondary text: status line, hints, chips | 7.9:1 | 4.5:1 |
| `muted` | `#7885A3` | Quiet but essential text: "Not installed" | 5.1:1 | 4.5:1 |
| `error` | `#FF8FA3` | Errors: Core could not answer | 8.6:1 | 4.5:1 |
| `focus` | `#86A8FF` | Focus ring and active borders | 8.1:1 | 3:1 |
| `primary` | `#7C6BFF` | Send button (white icon on it) | 3.9:1 | 3:1 |
| `glassBorder` | `#2A3A66` | Resting borders (decorative) | 1.7:1 | none (decorative) |
| `chip` | `#121B33` | Chip background (decorative) | 1.1:1 | none (decorative) |

Every text colour meets the 4.5:1 rule (binding rule 5), and the focus ring
and send button meet 3:1. `tests/shell/test_contrast.py` fails the build if
a change breaks either rule; it already caught the first `muted` value
(4.47:1). App icons use lighter versions of the original accents
(`shell/qml/icons`).

## Decide

- Approve as is, or say what to change: warmer or cooler surfaces, a
  different accent instead of violet, more or less glass transparency.
- Once approved, this set moves into `design-tokens.json` as a `dark`
  section, and the shell reads it from there instead of `root.dark`.
