# The NexusOS desktop end to end in a NixOS guest: greetd logs alice into
# Hyprland, which starts the Nexus shell; Nexus Core answers from a mock model.
# Real kernel input (ydotool) drives the shell. There is no host GPU: the guest
# renders through Mesa's software rasteriser on virtio-gpu.
{ pkgs, desktopModule }:
pkgs.testers.runNixOSTest {
  name = "hyprland-desktop";
  extraPythonPackages = p: [ p.pillow ];
  nodes.machine = { pkgs, ... }: {
    imports = [ desktopModule ];
    system.stateVersion = "26.05";
    virtualisation.memorySize = 4096;
    virtualisation.cores = 4;
    virtualisation.qemu.options = [ "-vga none" "-device virtio-gpu-pci,xres=1280,yres=800" ];
    users.users.alice = { isNormalUser = true; uid = 1000; password = "nexus"; };
    services.nexus-desktop = {
      enable = true;
      autologinUser = "alice";
      # Software rendering in a VM: keep the test fast and deterministic.
      reducedMotion = true;
      blur = false;
      coreDetail = 16;
      apps = { terminal = "kitty"; files = "thunar"; browser = "firefox"; };
      extraHyprlandConfig = ''
        input {
          accel_profile = flat
        }
      '';
    };
    services.nexus-core.extraEnvironment.NEXUS_OLLAMA_URL = "http://127.0.0.1:11435";
    systemd.services.mock-model = {
      wantedBy = [ "multi-user.target" ];
      serviceConfig.ExecStart = "${pkgs.python3}/bin/python3 ${../../scripts/mock-ollama.py}";
    };
    programs.ydotool.enable = true;
    environment.systemPackages = [ pkgs.jq ];
  };

  testScript = ''
    import time
    from PIL import Image

    W, H = 1280, 800
    SX, SY = (W - 760) // 2, H - 800          # the shell surface, from shell/layout.json
    LAUNCHER = (SX + 380, SY + 756)
    CORE = (SX + 380, SY + 290)
    CHAT = (SX + 380, SY + 548 + 31)
    TERMINAL = (CORE[0] - 211, CORE[1])       # the terminal slot sits at -180 degrees

    def alice(cmd):
        return machine.succeed("su - alice -c " + repr(cmd))

    def hypr(args):
        return alice("HYPRLAND_INSTANCE_SIGNATURE=$(ls /run/user/1000/hypr | head -1) hyprctl " + args)

    def click(x, y):
        hypr(f"dispatch movecursor {x} {y}")
        time.sleep(0.5)
        machine.succeed("YDOTOOL_SOCKET=/run/ydotoold/socket ydotool click 0xC0")

    def shell_state():
        return alice("XDG_RUNTIME_DIR=/run/user/1000 nexus-shell-ipc call nexus state")

    def shot(name):
        machine.screenshot(name)
        return Image.open(f"{machine.out_dir}/{name}.png").convert("RGB")

    def bright(img, x, y, r=10):
        return max(sum(img.getpixel((x + dx, y + dy))) for dx in range(-r, r + 1, 2) for dy in range(-r, r + 1, 2))

    def wait_drawn(name, x, y, r, threshold, timeout=240):
        """Software rendering in a VM can take seconds per frame: poll the screen."""
        deadline = time.time() + timeout
        while True:
            img = shot(name)
            if bright(img, x, y, r) > threshold:
                return img
            if time.time() > deadline:
                diagnostics()
                raise AssertionError(f"{name}: nothing drawn at {x},{y} after {timeout}s")
            time.sleep(5)

    def diagnostics():
        print(machine.execute("journalctl -b --no-pager -t nexus-shell | tail -60")[1])
        print(machine.execute("journalctl -b --no-pager --user-unit nexus-core -M alice@ | tail -20")[1])
        print(machine.execute("ps -eo pid,user,args | grep -E 'quickshell|nexus|Hyprland' | grep -v grep")[1])

    def open_from_orbit(app, clients_before):
        """Bring every app into orbit, then open one. Software rendering can drop a
        click that lands mid-frame, so each step is confirmed and retried."""
        for _ in range(5):
            if f'"{app}"' in shell_state().split('"orbit"')[1]:
                break
            click(*CORE)
            time.sleep(3)
        else:
            diagnostics()
            raise AssertionError(f"{app} never came into orbit: {shell_state()}")
        count = "su - alice -c 'HYPRLAND_INSTANCE_SIGNATURE=$(ls /run/user/1000/hypr | head -1) hyprctl clients' | grep -c 'class: kitty'"
        for _ in range(4):
            click(*TERMINAL)
            deadline = time.time() + 45
            while time.time() < deadline:
                if int(machine.execute(count)[1].strip() or 0) > clients_before:
                    return
                time.sleep(3)
        diagnostics()
        raise AssertionError(f"{app} did not open: {shell_state()}")

    def wait(cmd, timeout):
        try:
            machine.wait_until_succeeds(cmd, timeout=timeout)
        except Exception:
            diagnostics()
            raise

    machine.start()
    machine.wait_for_unit("multi-user.target")

    with subtest("greetd logs alice into Hyprland, which starts the shell and Core"):
        machine.wait_until_succeeds("ls /run/user/1000/hypr/*/.socket.sock", timeout=600)
        machine.wait_until_succeeds("su - alice -c 'nexus health'", timeout=600)
        machine.wait_until_succeeds(
            "su - alice -c 'HYPRLAND_INSTANCE_SIGNATURE=$(ls /run/user/1000/hypr | head -1) hyprctl layers' | grep -q nexus-spaceui",
            timeout=600)
        layers = hypr("layers")
        for ns in ("nexus-spaceui", "nexus-wallpaper", "nexus-system-panel", "nexus-reserve"):
            assert ns in layers, f"{ns} missing:\n{layers}"
        print(machine.execute("su - alice -c 'XDG_RUNTIME_DIR=/run/user/1000 nexus-shell-ipc call nexus state' 2>&1")[1])
        wait("su - alice -c 'XDG_RUNTIME_DIR=/run/user/1000 nexus-shell-ipc call nexus state' | grep -q '\"online\":true'", 300)
        wait_drawn("01-desktop", *LAUNCHER, r=10, threshold=300)

    with subtest("the launcher opens the full core"):
        click(*LAUNCHER)
        wait("su - alice -c 'XDG_RUNTIME_DIR=/run/user/1000 nexus-shell-ipc call nexus state' | grep -q '\"expanded\":true'", 60)
        wait_drawn("02-core", *CORE, r=20, threshold=450)

    with subtest("a question goes to Nexus Core and the answer streams back"):
        click(*CHAT)
        machine.sleep(1)
        machine.succeed("YDOTOOL_SOCKET=/run/ydotoold/socket ydotool type --key-delay 30 'help me organise my research notes'")
        machine.succeed("YDOTOOL_SOCKET=/run/ydotoold/socket ydotool key 28:1 28:0")
        wait("su - alice -c 'nexus history' | grep -q 'organise my research notes'", 120)
        machine.sleep(3)
        shot("03-answer")
        history = alice("nexus history")
        assert "Nexus mock response: help me organise my research notes" in history, history

    with subtest("an app opens from the orbit and Nexus steps back"):
        open_from_orbit("terminal", 0)
        wait("su - alice -c 'XDG_RUNTIME_DIR=/run/user/1000 nexus-shell-ipc call nexus state' | grep -q '\"expanded\":false'", 60)
        machine.sleep(3)
        shot("04-app-open")

    with subtest("with Core stopped the shell says so and apps still open"):
        alice("XDG_RUNTIME_DIR=/run/user/1000 systemctl --user stop nexus-core")
        wait("su - alice -c 'XDG_RUNTIME_DIR=/run/user/1000 nexus-shell-ipc call nexus state' | grep -q '\"online\":false'", 60)
        click(*LAUNCHER)
        wait("su - alice -c 'XDG_RUNTIME_DIR=/run/user/1000 nexus-shell-ipc call nexus state' | grep -q '\"expanded\":true'", 60)
        wait_drawn("05-offline", *CORE, r=20, threshold=450)
        open_from_orbit("terminal", 1)
        alice("XDG_RUNTIME_DIR=/run/user/1000 systemctl --user start nexus-core")
        wait("su - alice -c 'XDG_RUNTIME_DIR=/run/user/1000 nexus-shell-ipc call nexus state' | grep -q '\"online\":true'", 60)

    with subtest("the lock screen covers the shell"):
        before = shot("06-before-lock")
        hypr("dispatch exec 'hyprlock --config /etc/nexus/hyprlock.conf'")
        machine.wait_until_succeeds("pgrep -x hyprlock", timeout=60)
        assert bright(before, *LAUNCHER) > 300
        deadline = time.time() + 120
        while bright(locked := shot("07-locked"), *LAUNCHER) >= 120:
            assert time.time() < deadline, "the Nexus launcher shows through the lock screen"
            time.sleep(5)
  '';
}
