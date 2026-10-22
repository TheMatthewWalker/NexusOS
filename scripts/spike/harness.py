#!/usr/bin/env python3
"""Headless Wayland harness for the SpaceUI shell spike.

Runs one candidate shell on a headless sway (wlroots) output, with Mesa's
llvmpipe for GL, a real Nexus Core answering from the mock model, and real
input sent through the virtual pointer and keyboard protocols. Both shells get
the identical scenario, so screenshots and numbers are comparable.

    nix develop .#spaceui-spike --command python3 scripts/spike/harness.py native out/spike/native
    nix develop .#spaceui-spike --command python3 scripts/spike/harness.py web out/spike/web

Writes numbered screenshots and metrics.json into the output directory.
Numbers are software-rendered (no GPU), so compare the shells with each other,
not with real hardware.
"""
import json
import os
import shutil
import signal
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1].parent
SPIKE = ROOT / "spike/spaceui"
WIDTH, HEIGHT = 1600, 1000
LAYOUT = json.loads((SPIKE / "shared/layout.json").read_text())
SX = (WIDTH - LAYOUT["surface"]["width"]) // 2          # surface origin on screen
SY = HEIGHT - LAYOUT["surface"]["height"]
LAUNCHER = (SX + LAYOUT["launcher"]["x"], SY + LAYOUT["launcher"]["y"])
CORE = (SX + LAYOUT["core"]["x"], SY + LAYOUT["core"]["y"])
CHAT = (SX + LAYOUT["chat"]["x"] + LAYOUT["chat"]["width"] // 2, SY + LAYOUT["chat"]["y"] + LAYOUT["chat"]["height"] // 2)
TICK = os.sysconf("SC_CLK_TCK")


def log(*a):
    print("[harness]", *a, flush=True)


class Session:
    """A headless sway, a mock model, Nexus Core, and helpers to drive them."""

    def __init__(self, out, background="#F7F9FD"):
        self.out = out
        self.background = background
        self.tmp = Path(tempfile.mkdtemp(prefix="spaceui-spike-"))
        self.procs = []
        mesa = os.environ.get("SPIKE_MESA", "")
        self.env = dict(os.environ,
                        XDG_RUNTIME_DIR=str(self.tmp / "run"),
                        __EGL_VENDOR_LIBRARY_FILENAMES=f"{mesa}/share/glvnd/egl_vendor.d/50_mesa.json",
                        LIBGL_ALWAYS_SOFTWARE="1",
                        NEXUS_STATE_DIR=str(self.tmp / "state"),
                        NEXUS_OLLAMA_URL="http://127.0.0.1:11435",
                        SPIKE_LAUNCH=str(ROOT / "scripts/spike/launch-app.sh"),
                        NEXUS_CLI=str(self.tmp / "nexus"))
        (self.tmp / "run").mkdir(mode=0o700)
        (self.tmp / "state").mkdir(mode=0o700)

    def spawn(self, cmd, name, env=None, **kw):
        logf = open(self.out / f"{name}.log", "w")
        p = subprocess.Popen(cmd, env=env or self.env, stdout=logf, stderr=subprocess.STDOUT,
                             start_new_session=True, **kw)
        logf.close()
        self.procs.append(p)
        return p

    def start(self):
        conf = self.tmp / "sway.conf"
        conf.write_text(f"output HEADLESS-1 resolution {WIDTH}x{HEIGHT} bg {self.background} solid_color\n"
                        "default_border none\nfocus_follows_mouse no\n")
        sway_env = dict(self.env, WLR_BACKENDS="headless", WLR_RENDERER="pixman",
                        WLR_LIBINPUT_NO_DEVICES="1", WLR_HEADLESS_OUTPUTS="1")
        self.spawn(["sway", "-c", str(conf)], "sway", env=sway_env)
        sock = self.wait_for(lambda: next((p for p in (self.tmp / "run").glob("wayland-*") if p.suffix != ".lock"), None),
                             20, "sway socket")
        self.env["WAYLAND_DISPLAY"] = sock.name
        self.env["SWAYSOCK"] = str(next((self.tmp / "run").glob("sway-ipc.*")))
        self.start_input()
        self.start_core()

    def start_core(self):
        self.spawn(["python3", str(ROOT / "scripts/mock-ollama.py")], "mock-ollama")
        core, cli = ROOT / "out/core/NexusOS.Core.dll", ROOT / "out/cli/NexusOS.Cli.dll"
        if not core.exists() or not cli.exists():
            sys.exit("Build Core first: dotnet publish src/NexusOS.Core -c Release -o out/core "
                     "&& dotnet publish src/NexusOS.Cli -c Release -o out/cli")
        dotnet = shutil.which("dotnet") or "/root/bin/dotnet"
        self.spawn([dotnet, str(core)], "core", cwd=self.tmp)
        wrapper = Path(self.env["NEXUS_CLI"])
        wrapper.write_text(f"#!/bin/sh\nexec {dotnet} {cli} \"$@\"\n")
        wrapper.chmod(0o755)
        self.wait_for(lambda: subprocess.run([str(wrapper), "health"], env=self.env, capture_output=True).returncode == 0,
                      90, "Nexus Core health")
        log("Core ready")

    def wait_for(self, fn, timeout, what):
        end = time.time() + timeout
        while time.time() < end:
            v = fn()
            if v:
                return v
            time.sleep(0.1)
        raise TimeoutError(what)

    # -- observation -------------------------------------------------------
    def frame(self):
        """Grab the output as raw RGB (grim's PPM is trivial to parse)."""
        raw = subprocess.run(["grim", "-t", "ppm", "-"], env=self.env, capture_output=True, check=True).stdout
        parts = raw.split(b"\n", 3)
        return parts[3]

    def pixel(self, data, x, y):
        i = (y * WIDTH + x) * 3
        return tuple(data[i:i + 3])

    def screenshot(self, name):
        subprocess.run(["grim", str(self.out / f"{name}.png")], env=self.env, check=True)
        log("screenshot", name)

    # -- input (scripts/spike/vinput.py keeps one virtual pointer and keyboard alive) --
    def start_input(self):
        self.vin = subprocess.Popen([sys.executable, str(ROOT / "scripts/spike/vinput.py")],
                                    env=dict(self.env, SPIKE_OUTPUT=f"{WIDTH}x{HEIGHT}"),
                                    stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True,
                                    start_new_session=True)
        self.procs.append(self.vin)
        if self.vin.stdout.readline().strip() != "ready":
            raise RuntimeError("virtual input did not start")

    def send(self, line):
        self.vin.stdin.write(line + "\n")
        self.vin.stdin.flush()
        if self.vin.stdout.readline().strip() != "ok":
            raise RuntimeError(f"virtual input failed on {line!r}")

    def pointer_to(self, x, y):
        self.send(f"move {x} {y}")

    def click(self, x, y):
        self.pointer_to(x, y)
        time.sleep(0.1)
        self.send("click")

    def type(self, text):
        self.send(f"type {text}")

    def key(self, name):
        self.send(f"key {name}")

    def stop(self):
        def kill(p, sig):
            try:
                os.killpg(p.pid, sig)
            except (ProcessLookupError, PermissionError):
                try:
                    p.send_signal(sig)
                except ProcessLookupError:
                    pass
        for p in reversed(self.procs):
            kill(p, signal.SIGTERM)
        for p in self.procs:
            try:
                p.wait(5)
            except subprocess.TimeoutExpired:
                kill(p, signal.SIGKILL)
        shutil.rmtree(self.tmp, ignore_errors=True)


def tree(pid):
    """pid plus all descendants (WebKit runs separate web and network processes)."""
    kids = {}
    for d in Path("/proc").iterdir():
        if d.name.isdigit():
            try:
                ppid = int((d / "stat").read_text().rsplit(")", 1)[1].split()[1])
            except (OSError, IndexError):
                continue
            kids.setdefault(ppid, []).append(int(d.name))
    out, todo = [], [pid]
    while todo:
        p = todo.pop()
        out.append(p)
        todo += kids.get(p, [])
    return out


def pss_mib(pid):
    """Proportional set size of the process tree: shared libraries split fairly."""
    total = 0
    for p in tree(pid):
        try:
            for line in Path(f"/proc/{p}/smaps_rollup").read_text().splitlines():
                if line.startswith("Pss:"):
                    total += int(line.split()[1])
        except OSError:
            pass
    return round(total / 1024, 1)


def cpu_ticks(pid):
    total = 0
    for p in tree(pid):
        try:
            f = Path(f"/proc/{p}/stat").read_text().rsplit(")", 1)[1].split()
            total += int(f[11]) + int(f[12])
        except (OSError, IndexError):
            pass
    return total


def cpu_percent(pid, seconds):
    a, t = cpu_ticks(pid), time.time()
    time.sleep(seconds)
    return round(100 * (cpu_ticks(pid) - a) / TICK / (time.time() - t), 1)


def fps_since(logfile, start):
    """Shells print 'SPIKE t=<epoch s> fps=<n>' each second; average those after start."""
    vals = []
    for line in logfile.read_text(errors="replace").splitlines():
        # Native logs carry a "DEBUG qml:" prefix (with colour codes); web logs do not.
        tail = line[line.index("SPIKE t="):] if "SPIKE t=" in line else ""
        parts = dict(p.split("=", 1) for p in tail.split()[1:3] if "=" in p)
        if parts and float(parts["t"]) >= start:
            vals.append(float(parts["fps"]))
    return round(sum(vals) / len(vals), 1) if vals else None


def shell_command(kind):
    if kind == "native":
        return ["quickshell", "-p", str(SPIKE / "native/shell.qml")]
    if kind == "web":
        return ["python3", str(SPIKE / "web/host.py")]
    raise SystemExit(f"unknown shell {kind!r}: native or web")


def run(kind, out, reduced=False):
    out.mkdir(parents=True, exist_ok=True)
    for old in out.glob("*.png"):
        old.unlink()
    # The native shell carries the approved dark theme; the web spike stays on the bright baseline it was measured on.
    s = Session(out, background="#05060D" if kind == "native" else "#F7F9FD")
    m = {"shell": kind, "reducedMotion": reduced, "renderer": "llvmpipe (software)",
         "output": f"{WIDTH}x{HEIGHT}"}
    try:
        s.start()
        env = dict(s.env, NEXUS_REDUCED_MOTION="1" if reduced else "0")
        if kind == "web":
            # The container cannot create the user namespaces WebKit's sandbox needs.
            # A real install keeps the sandbox on.
            env["WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS"] = "1"
        empty = s.frame()
        launched = time.time()
        shell = s.spawn(shell_command(kind), "shell", env=env)
        # Startup: until the launcher's pixels differ from the bare desktop.
        def launcher_drawn():
            f = s.frame()
            return f if s.pixel(f, *LAUNCHER) != s.pixel(empty, *LAUNCHER) else None
        s.wait_for(launcher_drawn, 60, "launcher to appear")
        m["startupSeconds"] = round(time.time() - launched, 2)
        time.sleep(3)
        s.screenshot("01-launcher-idle")
        m["idleLauncher"] = {"pssMiB": pss_mib(shell.pid), "cpuPercent": cpu_percent(shell.pid, 10)}
        m["idleLauncher"]["fps"] = fps_since(out / "shell.log", time.time() - 10)

        s.click(*LAUNCHER)
        time.sleep(1.5)
        s.screenshot("02-core-open")
        s.click(*CORE)                       # clicking the core brings every app into orbit
        time.sleep(1.5)
        s.screenshot("03-all-apps-orbit")
        s.click(*CORE)                       # and clicking again retracts them
        time.sleep(1.2)

        s.click(*CHAT)
        time.sleep(0.8)                      # let keyboard focus arrive before typing
        s.type("help me organise my research notes")
        time.sleep(1.5)                      # intent debounce + emergence
        s.screenshot("04-intent-suggestions")
        t0 = time.time()
        s.key("Return")
        time.sleep(0.4)
        s.screenshot("05-core-responding")
        m["responding"] = {"cpuPercent": cpu_percent(shell.pid, 3), "pssMiB": pss_mib(shell.pid)}
        m["responding"]["fps"] = fps_since(out / "shell.log", t0)
        time.sleep(3)
        s.screenshot("06-core-reply")

        # Choose Files from the orbit: the shell asks the launcher helper to
        # open it, the compositor snaps it to the left tile, Nexus collapses.
        fx, fy = slot_on_screen("files")
        s.click(fx, fy)
        time.sleep(2.5)
        s.screenshot("07-app-snapped-launcher-planet")
        s.pointer_to(*LAUNCHER)
        time.sleep(1.5)                      # delayed hover widens the open-app orbit
        s.screenshot("08-launcher-hover-orbit")
        m["withAppOpen"] = {"pssMiB": pss_mib(shell.pid), "cpuPercent": cpu_percent(shell.pid, 5)}

        # Pass-through: a second window takes focus, then a click on the Files
        # window, inside the shell surface's transparent area, must reach Files.
        s.pointer_to(WIDTH - 50, 100)
        subprocess.run([s.env["SPIKE_LAUNCH"], "terminal"], env=s.env, check=True)
        subprocess.run(["swaymsg", '[app_id="nexus-spike-terminal"] move absolute position 806 58'],
                       env=s.env, check=True, capture_output=True)
        time.sleep(0.5)
        before = focused_app(s.env)
        s.click(SX + 150, SY + 450)
        time.sleep(0.5)
        m["inputPassThrough"] = {"focusBefore": before, "focusAfterClick": focused_app(s.env),
                                 "ok": before == "nexus-spike-terminal" and focused_app(s.env) == "nexus-spike-files"}
        s.screenshot("09-pass-through")
        m["exitedEarly"] = shell.poll() is not None
    finally:
        (out / "metrics.json").write_text(json.dumps(m, indent=2) + "\n")
        log(json.dumps(m))
        s.stop()


def focused_app(env):
    tree = json.loads(subprocess.run(["swaymsg", "-t", "get_tree", "-r"], env=env,
                                     capture_output=True, check=True).stdout)
    todo = [tree]
    while todo:
        n = todo.pop()
        if n.get("focused"):
            return n.get("app_id")
        todo += n.get("nodes", []) + n.get("floating_nodes", [])
    return None


def slot_on_screen(app):
    tokens = json.loads((ROOT / "docs/design/spaceui/design-tokens.json").read_text())
    import math
    o = tokens["baseline"]["appOrbit"]
    a = math.radians(o["angles"][app])
    return round(CORE[0] + o["radius"] * math.cos(a)), round(CORE[1] + o["radius"] * math.sin(a))


if __name__ == "__main__":
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    run(sys.argv[1], Path(sys.argv[2]).resolve(), reduced="--reduced-motion" in sys.argv)
