"""Accessibility gate for the shell's dark theme (design pack binding rule 5).

Text colours must reach 4.5:1 against the glass surfaces they sit on; focus
rings and borders that carry meaning must reach 3:1. The glass is translucent,
so it is first composited over the darkest and lightest parts of the wallpaper.
Run: python3 -m unittest tests/shell/test_contrast.py
"""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SHELL_QML = ROOT / "shell/qml/shell.qml"
WALLPAPER = ["#020309", "#0B0F24"]     # gradient ends in shell/qml/Wallpaper.qml


def rgb(hex_colour):
    h = hex_colour.lstrip("#")
    return tuple(int(h[i:i + 2], 16) / 255 for i in (0, 2, 4))


def luminance(c):
    def lin(v):
        return v / 12.92 if v <= 0.03928 else ((v + 0.055) / 1.055) ** 2.4
    r, g, b = (lin(v) for v in c)
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


def contrast(a, b):
    la, lb = sorted((luminance(a), luminance(b)), reverse=True)
    return (la + 0.05) / (lb + 0.05)


def over(fg, alpha, bg):
    return tuple(f * alpha + b * (1 - alpha) for f, b in zip(fg, bg))


def theme():
    text = SHELL_QML.read_text()
    block = text[text.index("readonly property var dark"):]
    block = block[:block.index("})")]
    colours = dict(re.findall(r'(\w+): "(#[0-9A-Fa-f]{6})"', block))
    glass = re.search(r"glass: Qt\.rgba\(([\d.]+), ([\d.]+), ([\d.]+), ([\d.]+)\)", block)
    return colours, tuple(float(glass.group(i)) for i in range(1, 4)), float(glass.group(4))


class DarkThemeContrast(unittest.TestCase):
    def setUp(self):
        self.colours, glass, alpha = theme()
        self.surfaces = [over(glass, alpha, rgb(w)) for w in WALLPAPER]

    def check(self, name, minimum):
        for surface in self.surfaces:
            ratio = contrast(rgb(self.colours[name]), surface)
            self.assertGreaterEqual(ratio, minimum, f"{name} {self.colours[name]} is {ratio:.2f}:1, needs {minimum}:1")

    def test_text_colours_reach_4_5_to_1(self):
        for name in ("ink", "inkSecondary", "muted", "error"):
            self.check(name, 4.5)

    def test_focus_and_borders_that_carry_state_reach_3_to_1(self):
        self.check("focus", 3.0)

    def test_send_button_icon_is_visible(self):
        # The send icon is white on the primary colour: a graphical object, so 3:1.
        self.assertGreaterEqual(contrast((1, 1, 1), rgb(self.colours["primary"])), 3.0)


if __name__ == "__main__":
    unittest.main()
