#!/usr/bin/env python3
"""Long-lived virtual pointer and keyboard for the headless spike compositor.

One-shot tools such as wlrctl and wtype create a virtual device and destroy it
when they exit. A headless seat has no other devices, so the pointer capability
flickers and clients drop the event. This keeps one pointer and one keyboard
alive for the whole session and reads commands from stdin:

    move X Y       absolute pointer position on the output
    click          left button press and release
    type TEXT      types ASCII text (US layout)
    key NAME       Return, Escape, Tab or BackSpace

It answers "ok" after each command once the compositor has processed it.
"""
import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path

VIRTUAL_KEYBOARD_XML = """<?xml version="1.0" encoding="UTF-8"?>
<protocol name="virtual_keyboard_unstable_v1">
  <interface name="zwp_virtual_keyboard_v1" version="1">
    <request name="keymap">
      <arg name="format" type="uint"/><arg name="fd" type="fd"/><arg name="size" type="uint"/>
    </request>
    <request name="key">
      <arg name="time" type="uint"/><arg name="key" type="uint"/><arg name="state" type="uint"/>
    </request>
    <request name="modifiers">
      <arg name="mods_depressed" type="uint"/><arg name="mods_latched" type="uint"/>
      <arg name="mods_locked" type="uint"/><arg name="group" type="uint"/>
    </request>
    <request name="destroy" type="destructor"/>
  </interface>
  <interface name="zwp_virtual_keyboard_manager_v1" version="1">
    <request name="create_virtual_keyboard">
      <arg name="seat" type="object" interface="wl_seat"/>
      <arg name="id" type="new_id" interface="zwp_virtual_keyboard_v1"/>
    </request>
  </interface>
</protocol>
"""

BTN_LEFT = 0x110
SHIFT = 42
KEYS = {"Return": 28, "Escape": 1, "Tab": 15, "BackSpace": 14}
PLAIN = {c: k for k, c in zip(range(16, 26), "qwertyuiop")}
PLAIN.update({c: k for k, c in zip(range(30, 39), "asdfghjkl")})
PLAIN.update({c: k for k, c in zip(range(44, 51), "zxcvbnm")})
PLAIN.update({c: k for k, c in zip(range(2, 12), "1234567890")})
PLAIN.update({" ": 57, "-": 12, "=": 13, ",": 51, ".": 52, "/": 53, ";": 39, "'": 40})
SHIFTED = {c.upper(): k for c, k in PLAIN.items() if c.isalpha()}
SHIFTED.update({"?": 53, "!": 2, ":": 39, '"': 40, "_": 12})


def protocols():
    out = Path(tempfile.mkdtemp(prefix="vinput-proto-"))
    vk = out / "virtual-keyboard-unstable-v1.xml"
    vk.write_text(VIRTUAL_KEYBOARD_XML)
    vp = Path(os.environ["SPIKE_WLR_PROTOCOLS"]) / "unstable/wlr-virtual-pointer-unstable-v1.xml"
    # The core protocol is scanned alongside so the generated modules can refer to wl_seat.
    subprocess.run([sys.executable, "-m", "pywayland.scanner", "-o", str(out / "vproto"), "-i", os.environ["SPIKE_WAYLAND_XML"], str(vp), str(vk)],
                   check=True)
    (out / "vproto/__init__.py").touch()
    sys.path.insert(0, str(out))


def main():
    protocols()
    from pywayland.client import Display
    from vproto.wayland import WlSeat
    from vproto.wlr_virtual_pointer_unstable_v1 import ZwlrVirtualPointerManagerV1
    from vproto.virtual_keyboard_unstable_v1 import ZwpVirtualKeyboardManagerV1

    display = Display()
    display.connect()
    found = {}

    def on_global(registry, name, interface, version):
        found.setdefault(interface, (name, version))

    registry = display.get_registry()
    registry.dispatcher["global"] = on_global
    display.roundtrip()
    for need in ("wl_seat", "zwlr_virtual_pointer_manager_v1", "zwp_virtual_keyboard_manager_v1"):
        if need not in found:
            sys.exit(f"compositor lacks {need}")
    seat = registry.bind(found["wl_seat"][0], WlSeat, 1)
    pointer = registry.bind(found["zwlr_virtual_pointer_manager_v1"][0], ZwlrVirtualPointerManagerV1, 1) \
        .create_virtual_pointer(seat)
    keyboard = registry.bind(found["zwp_virtual_keyboard_manager_v1"][0], ZwpVirtualKeyboardManagerV1, 1) \
        .create_virtual_keyboard(seat)

    keymap = subprocess.run(["xkbcli", "compile-keymap", "--layout", "us"], capture_output=True, check=True).stdout + b"\0"
    fd = os.memfd_create("keymap")
    os.write(fd, keymap)
    keyboard.keymap(1, fd, len(keymap))           # 1 = XKB_V1
    display.roundtrip()

    width, height = (int(v) for v in os.environ.get("SPIKE_OUTPUT", "1600x1000").split("x"))
    now = lambda: int(time.monotonic() * 1000) & 0xFFFFFFFF

    def press(code, shift=False):
        if shift:
            keyboard.modifiers(1, 0, 0, 0)
        keyboard.key(now(), code, 1)
        keyboard.key(now(), code, 0)
        if shift:
            keyboard.modifiers(0, 0, 0, 0)
        display.roundtrip()
        time.sleep(0.012)

    print("ready", flush=True)
    for line in sys.stdin:
        cmd, _, arg = line.rstrip("\n").partition(" ")
        if cmd == "move":
            x, y = (int(v) for v in arg.split())
            pointer.motion_absolute(now(), x, y, width, height)
            pointer.frame()
        elif cmd == "click":
            pointer.button(now(), BTN_LEFT, 1)
            pointer.frame()
            display.roundtrip()
            time.sleep(0.05)
            pointer.button(now(), BTN_LEFT, 0)
            pointer.frame()
        elif cmd == "type":
            for ch in arg:
                if ch in PLAIN:
                    press(PLAIN[ch])
                elif ch in SHIFTED:
                    press(SHIFTED[ch], shift=True)
                else:
                    sys.exit(f"cannot type {ch!r}")
        elif cmd == "key":
            press(KEYS[arg])
        else:
            sys.exit(f"unknown command {cmd!r}")
        display.roundtrip()
        print("ok", flush=True)


if __name__ == "__main__":
    main()
