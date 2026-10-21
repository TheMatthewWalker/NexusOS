#!/usr/bin/env python3
"""SpaceUI spike B host: a WebKitGTK view inside a wlr-layer-shell surface.

The page (index.html, app.mjs) draws everything. This host supplies what a web
page cannot do itself, which is the integration cost of the web approach:
  - the layer surface, its input region and keyboard mode (gtk4-layer-shell)
  - Nexus Core answers (runs the CLI, as the native shell does)
  - app launches (scripts/spike/launch-app.sh, shared with the native shell)
  - the open-window list for launcher planets: GTK has no API for it, so this
    host speaks wlr-foreign-toplevel-management itself over a second Wayland
    connection (pywayland)

Needs LD_PRELOAD of libgtk4-layer-shell.so (SPIKE_LAYER_SHELL); run via the harness.
"""
import json
import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path

if os.environ.get("SPIKE_LAYER_SHELL") and "gtk4-layer-shell" not in os.environ.get("LD_PRELOAD", ""):
    os.environ["LD_PRELOAD"] = os.environ["SPIKE_LAYER_SHELL"]
    os.execv(sys.executable, [sys.executable, *sys.argv])

import cairo
import gi

gi.require_version("Gtk", "4.0")
gi.require_version("Gdk", "4.0")
gi.require_version("Gtk4LayerShell", "1.0")
gi.require_version("WebKit", "6.0")
gi.require_foreign("cairo")
from gi.repository import Gdk, Gio, GLib, Gtk, Gtk4LayerShell as Layer, WebKit

# The preload has done its job; keep it out of Core and app launches.
os.environ.pop("LD_PRELOAD", None)

HERE = Path(__file__).resolve().parent
LAYOUT = json.loads((HERE.parent / "shared/layout.json").read_text())


class Toplevels:
    """Open windows from wlr-foreign-toplevel-management, on our own connection."""

    def __init__(self, on_change):
        self.on_change = on_change
        self.handles, self.info, self.seat = {}, {}, None
        out = Path(tempfile.mkdtemp(prefix="spike-proto-"))
        xml = Path(os.environ["SPIKE_WLR_PROTOCOLS"]) / "unstable/wlr-foreign-toplevel-management-unstable-v1.xml"
        subprocess.run([sys.executable, "-m", "pywayland.scanner", "-o", str(out / "tproto"),
                        "-i", os.environ["SPIKE_WAYLAND_XML"], str(xml)], check=True, capture_output=True)
        (out / "tproto/__init__.py").touch()
        sys.path.insert(0, str(out))
        from pywayland.client import Display
        from tproto.wayland import WlSeat
        from tproto.wlr_foreign_toplevel_management_unstable_v1 import ZwlrForeignToplevelManagerV1
        self.display = Display()
        self.display.connect()
        registry = self.display.get_registry()
        found = {}
        registry.dispatcher["global"] = lambda r, name, iface, ver: found.setdefault(iface, name)
        self.display.roundtrip()
        self.seat = registry.bind(found["wl_seat"], WlSeat, 1)
        manager = registry.bind(found["zwlr_foreign_toplevel_manager_v1"], ZwlrForeignToplevelManagerV1, 1)
        manager.dispatcher["toplevel"] = self.added
        self.display.roundtrip()
        GLib.io_add_watch(self.display.get_fd(), GLib.IO_IN, self.readable)

    def added(self, manager, handle):
        key = str(id(handle))
        self.handles[key] = handle
        self.info[key] = {"id": key, "appId": "", "title": ""}
        handle.dispatcher["title"] = lambda h, t: self.info[key].update(title=t)
        handle.dispatcher["app_id"] = lambda h, a: self.info[key].update(appId=a)
        handle.dispatcher["done"] = lambda h: self.changed()
        handle.dispatcher["closed"] = lambda h: (self.handles.pop(key, None), self.info.pop(key, None), self.changed())

    def changed(self):
        self.on_change(list(self.info.values()))

    def activate(self, key):
        if key in self.handles:
            self.handles[key].activate(self.seat)
            self.display.flush()

    def readable(self, *_):
        self.display.dispatch(block=True)
        self.display.flush()
        return True


class Host:
    def __init__(self, app):
        css = Gtk.CssProvider()
        css.load_from_string("window, window > * { background: transparent; }")
        Gtk.StyleContext.add_provider_for_display(Gdk.Display.get_default(), css,
                                                  Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)
        self.win = Gtk.ApplicationWindow(application=app)
        w, h = LAYOUT["surface"]["width"], LAYOUT["surface"]["height"]
        self.win.set_default_size(w, h)
        Layer.init_for_window(self.win)
        Layer.set_layer(self.win, Layer.Layer.TOP)          # below lock and secure surfaces
        Layer.set_namespace(self.win, "nexus-spaceui")
        Layer.set_anchor(self.win, Layer.Edge.BOTTOM, True)
        Layer.set_exclusive_zone(self.win, 0)
        Layer.set_keyboard_mode(self.win, Layer.KeyboardMode.NONE)

        self.view = WebKit.WebView()
        s = self.view.get_settings()
        s.set_enable_webgl(True)
        s.set_hardware_acceleration_policy(WebKit.HardwareAccelerationPolicy.ALWAYS)
        s.set_allow_file_access_from_file_urls(True)
        clear = Gdk.RGBA()
        clear.parse("rgba(0,0,0,0)")
        self.view.set_background_color(clear)
        ucm = self.view.get_user_content_manager()
        ucm.register_script_message_handler("nexus", None)
        ucm.connect("script-message-received::nexus", self.message)
        reduced = os.environ.get("NEXUS_REDUCED_MOTION") == "1"
        self.view.load_uri((HERE / "index.html").as_uri() + ("?reduced=1" if reduced else ""))
        self.win.set_child(self.view)
        self.win.present()
        self.rect = None
        self.toplevels = Toplevels(lambda items: self.call("toplevels", items))

    def call(self, fn, arg):
        self.view.evaluate_javascript(f"window.nexusHost && window.nexusHost.{fn}({json.dumps(arg)})",
                                      -1, None, None, None, None, None)

    def message(self, _ucm, value):
        msg = json.loads(value.to_json(0) if hasattr(value, "to_json") else value.to_string())
        if isinstance(msg, str):
            msg = json.loads(msg)
        kind = msg.get("type")
        if kind == "fps":
            print(f"SPIKE t={time.time():.2f} fps={msg['fps']}", flush=True)
        elif kind == "region":
            self.region(msg["expanded"], msg["rect"])
        elif kind == "ask":
            self.ask(msg["prompt"])
        elif kind == "launch":
            subprocess.Popen([os.environ["SPIKE_LAUNCH"], msg["app"]], start_new_session=True)
        elif kind == "activate":
            self.toplevels.activate(msg["id"])
        elif kind == "ready":
            print("SPIKE ready", flush=True)

    def region(self, expanded, rect):
        Layer.set_keyboard_mode(self.win, Layer.KeyboardMode.ON_DEMAND if expanded else Layer.KeyboardMode.NONE)
        self.rect = rect
        surface = self.win.get_surface()
        if surface:
            x, y, w, h = (int(v) for v in rect)
            surface.set_input_region(cairo.Region(cairo.RectangleInt(x, y, w, h)))

    def ask(self, prompt):
        proc = Gio.Subprocess.new([os.environ["NEXUS_CLI"], "ask", prompt],
                                  Gio.SubprocessFlags.STDOUT_PIPE | Gio.SubprocessFlags.STDERR_PIPE)

        def done(p, res):
            ok, out, err = p.communicate_utf8_finish(res)
            try:
                if not p.get_successful():
                    raise RuntimeError((err or "Nexus Core did not answer").strip())
                r = json.loads(out)
                self.call("answer", {"answer": r["answer"], "provider": r["provider"]})
            except Exception as exc:  # shown to the user as an error, not swallowed
                self.call("answer", {"error": str(exc)})
        proc.communicate_utf8_async(None, None, done)


def main():
    app = Gtk.Application(application_id="os.nexus.spaceui.webspike")
    app.connect("activate", lambda a: setattr(a, "host", Host(a)))
    app.run([])


if __name__ == "__main__":
    main()
