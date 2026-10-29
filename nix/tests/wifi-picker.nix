# Settings, Network: the Wi-Fi list, end to end. A simulated Wi-Fi radio
# (mac80211_hwsim) runs two access points: "NexusHome" (WPA2, with a password)
# and "NexusCafe" (open). NetworkManager drives a second simulated radio, and
# real kernel input (ydotool) clicks the orbital controls' Network planet,
# picks a network, types the password and presses Enter.
{ pkgs, desktopModule }:
let
  password = "correct horse battery";
in
pkgs.testers.runNixOSTest {
  name = "wifi-picker";
  nodes.machine = { pkgs, lib, ... }: {
    imports = [ desktopModule ];
    system.stateVersion = "26.05";
    virtualisation.memorySize = 4096;
    virtualisation.cores = 4;
    virtualisation.qemu.options = [ "-vga none" "-device virtio-gpu-pci,xres=1280,yres=800" ];
    users.users.alice = { isNormalUser = true; uid = 1000; password = "nexus"; extraGroups = [ "networkmanager" ]; };
    services.nexus-desktop = {
      enable = true;
      # No Welcome window over the desktop the test drives.
      firstRun = false;
      autologinUser = "alice";
      reducedMotion = true;
      blur = false;
      coreDetail = 16;
      # Without title bars the window's content starts at its position, which
      # keeps the clicks below simple.
      titleBars = false;
      # No key repeat: under software emulation a key can stay "down" long
      # enough to repeat, which would garble typed passwords.
      extraHyprlandConfig = ''
        input {
          accel_profile = flat
          repeat_rate = 0
        }
      '';
    };

    # Two simulated radios: wlan0 hosts the access points, wlan1 is the laptop's.
    boot.kernelModules = [ "mac80211_hwsim" ];
    # NixOS test VMs turn wpa_supplicant off (qemu-vm.nix, mkVMOverride); this
    # test has Wi-Fi, so give NetworkManager its supplicant back.
    networking.wireless.enable = lib.mkOverride 9 true;
    networking.networkmanager.unmanaged = [ "interface-name:wlan0*" ];
    services.hostapd = {
      enable = true;
      radios.wlan0 = {
        band = "2g";
        channel = 6;
        countryCode = "GB";
        networks.wlan0 = {
          ssid = "NexusHome";
          bssid = "02:00:00:00:00:00";
          authentication = { mode = "wpa2-sha1"; wpaPasswordFile = pkgs.writeText "psk" password; };
        };
        networks.wlan0-1 = {
          ssid = "NexusCafe";
          bssid = "02:00:00:00:00:01";
          authentication.mode = "none";
        };
      };
    };
    # Addresses for whoever joins, so NetworkManager finishes connecting.
    networking.interfaces.wlan0.ipv4.addresses = [ { address = "192.168.60.1"; prefixLength = 24; } ];
    networking.interfaces.wlan0-1.ipv4.addresses = [ { address = "192.168.61.1"; prefixLength = 24; } ];
    services.dnsmasq = {
      enable = true;
      resolveLocalQueries = false;
      settings = {
        port = 0;
        interface = [ "wlan0" "wlan0-1" ];
        bind-dynamic = true;
        dhcp-range = [ "192.168.60.10,192.168.60.50,1h" "192.168.61.10,192.168.61.50,1h" ];
      };
    };
    # The access points and the laptop's radio share this one machine, so let
    # DHCP through the firewall, and accept replies that come from one of the
    # machine's own addresses (the access point's), which Linux drops by default.
    networking.firewall.trustedInterfaces = [ "wlan0" "wlan0-1" "wlan1" ];
    networking.firewall.checkReversePath = false;
    boot.kernel.sysctl = {
      "net.ipv4.conf.all.accept_local" = 1;
      "net.ipv4.conf.default.accept_local" = 1;
      "net.ipv4.conf.all.rp_filter" = 0;
      "net.ipv4.conf.default.rp_filter" = 0;
    };
    systemd.services.dnsmasq.after = [ "hostapd.service" ];
    systemd.services.dnsmasq.wants = [ "hostapd.service" ];

    programs.ydotool.enable = true;
  };

  testScript = ''
    import json
    import time

    W, H = 1280, 800
    MARK = (W - 23 - 17, H - 16 - 17)    # the collapsed orbital controls, bottom right (OrbitalControls.qml)
    PASSWORD = "${password}"

    def hypr(args):
        cmd = "su - alice -c " + repr("HYPRLAND_INSTANCE_SIGNATURE=$(ls /run/user/1000/hypr | head -1) hyprctl " + args)
        for _ in range(5):
            status, out = machine.execute(cmd)
            if status == 0:
                return out
            time.sleep(3)
        raise AssertionError(f"hyprctl {args} failed: {out}")

    def move(x, y):
        hypr(f"dispatch movecursor {x} {y}")
        time.sleep(0.5)

    def click(x, y):
        move(x, y)
        machine.succeed("YDOTOOL_SOCKET=/run/ydotoold/socket ydotool click 0xC0")

    def key(*codes):
        presses = " ".join(f"{c}:1" for c in codes) + " " + " ".join(f"{c}:0" for c in reversed(codes))
        machine.succeed(f"YDOTOOL_SOCKET=/run/ydotoold/socket ydotool key {presses}")

    def type_text(text):
        machine.succeed("YDOTOOL_SOCKET=/run/ydotoold/socket ydotool type --key-delay 40 " + repr(text))

    def ipc(args):
        return machine.succeed("su - alice -c 'XDG_RUNTIME_DIR=/run/user/1000 nexus-shell-ipc call " + args + "'")

    def window_origin():
        """Where the Settings window's content starts on screen."""
        clients = json.loads(hypr("clients -j"))
        win = next(c for c in clients if c["title"] == "Settings")
        return win["at"]

    def panel():
        """Settings' Wi-Fi state, with each row's centre in screen coordinates."""
        state = json.loads(ipc("settings wifi"))
        if state["rows"]:
            ox, oy = window_origin()
            for r in state["rows"]:
                r["x"] += ox; r["y"] += oy
                if r["forget"]:
                    r["forget"] = [r["forget"][0] + ox, r["forget"][1] + oy]
        state["expanded"] = state["visible"] and state["page"] == "network"
        return state

    def diagnostics():
        print(machine.execute("journalctl -b --no-pager -t nexus-shell | tail -60")[1])
        print(machine.execute("nmcli -f all device wifi list ifname wlan1; nmcli device; nmcli connection")[1])
        print(machine.execute("journalctl -b --no-pager -u NetworkManager | tail -40")[1])
        print(hypr("clients") + hypr("layers") + hypr("cursorpos"))
        print(ipc("controls state"))
        machine.screenshot("diagnostics")

    def laid_out(s):
        """Every row has its own place on screen (none mid-layout)."""
        return len({r["y"] for r in s["rows"]}) == len(s["rows"])

    def click_row(name, done, what, tries=4):
        """Click a network's row; retry if software rendering dropped the click."""
        for _ in range(tries):
            s = until(lambda s: row(s, name) and laid_out(s), f"{name} is not on screen", timeout=120)
            r = row(s, name)
            click(r["x"], r["y"])
            deadline = time.time() + 20
            while time.time() < deadline:
                if done(panel()):
                    return
                time.sleep(2)
        diagnostics()
        raise AssertionError(what + ": " + json.dumps(panel()))

    def until(check, what, timeout=300, every=3, poke=None):
        deadline = time.time() + timeout
        last = None
        while time.time() < deadline:
            try:
                last = panel()
                if check(last):
                    return last
            except Exception as e:
                last = repr(e)
            if poke:
                poke()
            time.sleep(every)
        diagnostics()
        raise AssertionError(f"{what}: {last}")

    def row(state, name):
        return next((r for r in state["rows"] if r["name"] == name), None)

    def screenshot(name, still):
        """Software rendering draws a second or two behind: wait, take the
        picture, then check the state it should show still holds."""
        time.sleep(6)
        machine.screenshot(name)
        state = panel()
        assert still(state), f"{name}: the state changed before the screenshot: {state}"

    def open_panel():
        """Hover the orbital controls, then click their Network planet: Settings opens on Network."""
        move(*MARK)
        for _ in range(10):
            if json.loads(ipc("controls state"))["expanded"]:
                break
            move(*MARK)
            time.sleep(2)
        # The Network planet (design tokens: orbitalControl.planetTopLeft.network, 51 px across),
        # in the expanded panel anchored bottom right: 365 wide, 12 from the bottom, 16 from the right.
        height = 28 + 242 + 10 + 96
        click(W - 16 - 365 + 120 + 25, H - 12 - height + 23 + 25)
        until(lambda s: s["expanded"], "the Network planet did not open Settings, Network", timeout=120)

    def active_ssid():
        return machine.succeed("nmcli -t -f ACTIVE,SSID device wifi list ifname wlan1 --rescan no | grep '^yes:' | cut -d: -f2 || true").strip()

    machine.start()
    machine.wait_for_unit("multi-user.target")
    machine.wait_for_unit("hostapd.service")
    machine.wait_until_succeeds("ls /run/user/1000/hypr/*/.socket.sock", timeout=600)
    machine.wait_until_succeeds(
        "su - alice -c 'HYPRLAND_INSTANCE_SIGNATURE=$(ls /run/user/1000/hypr | head -1) hyprctl layers' | grep -q nexus-orbital-controls",
        timeout=600)

    with subtest("the orbital controls' Network planet opens Settings, which lists the nearby networks"):
        open_panel()
        state = until(lambda s: s["wifiOn"] and row(s, "NexusHome") and row(s, "NexusCafe"),
                      "the networks never appeared")
        assert row(state, "NexusHome")["security"] == "password", state
        assert row(state, "NexusCafe")["security"] == "open", state
        screenshot("01-networks", lambda s: row(s, "NexusHome") and row(s, "NexusCafe"))

    with subtest("a secured network asks for its password; a short one is refused before sending"):
        click_row("NexusHome", lambda s: s["passwordFor"] == "NexusHome", "no password box")
        type_text("short")
        key(28)                                    # Enter
        until(lambda s: "at least 8" in s["error"], "the short password was not refused")
        assert active_ssid() == "", "connected with a short password"
        screenshot("02-short-password", lambda s: "at least 8" in s["error"])

    with subtest("the right password connects through NetworkManager"):
        key(29, 30)                                # Ctrl+A: select what is there, then replace it
        type_text(PASSWORD)
        key(28)
        until(lambda s: (row(s, "NexusHome") or {}).get("connected"), "NexusHome did not connect", timeout=180)
        assert active_ssid() == "NexusHome", active_ssid()
        machine.succeed("ip -4 addr show wlan1 | grep -q 'inet 192.168.60.'")
        screenshot("03-connected", lambda s: (row(s, "NexusHome") or {}).get("connected"))

    with subtest("the password is kept by NetworkManager, not logged or kept by the shell"):
        machine.succeed("grep -rqs " + repr("psk=" + PASSWORD) + " /etc/NetworkManager/system-connections/")
        machine.fail("journalctl -b --no-pager | grep -qF " + repr(PASSWORD))
        machine.fail("grep -rqsF " + repr(PASSWORD) + " /home/alice /run/user/1000")

    with subtest("an open network connects with one click"):
        click_row("NexusCafe", lambda s: (row(s, "NexusCafe") or {}).get("state") in ("Connecting", "Connected"),
                  "clicking NexusCafe did nothing")
        until(lambda s: (row(s, "NexusCafe") or {}).get("connected"), "NexusCafe did not connect", timeout=180)
        assert active_ssid() == "NexusCafe", active_ssid()

    with subtest("a saved network reconnects without asking again"):
        assert row(panel(), "NexusHome")["known"], panel()
        click_row("NexusHome", lambda s: (row(s, "NexusHome") or {}).get("state") in ("Connecting", "Connected"),
                  "clicking NexusHome did nothing")
        until(lambda s: (row(s, "NexusHome") or {}).get("connected"), "NexusHome did not reconnect", timeout=180)
        assert panel()["passwordFor"] == "", "asked for a saved password again"

    with subtest("Forget removes the saved password"):
        r = row(panel(), "NexusHome")
        click(r["x"], r["y"])                      # a connected network shows Disconnect and Forget
        screenshot("04-details", lambda s: (row(s, "NexusHome") or {}).get("forget"))
        machine.succeed("nmcli -t -f NAME connection show | grep -qx NexusHome")
        # Software rendering can drop a click that lands mid-frame: retry until
        # the panel confirms it.
        for _ in range(4):
            state = panel()
            if "forgotten" in state["note"]:
                break
            if not (row(state, "NexusHome") or {}).get("forget"):
                click_row("NexusHome", lambda s: (row(s, "NexusHome") or {}).get("forget"), "no Forget button")
                state = panel()
            click(*row(state, "NexusHome")["forget"])
            time.sleep(5)
        until(lambda s: "NexusHome forgotten" in s["note"], "the panel never confirmed Forget", timeout=60)
        try:
            machine.wait_until_succeeds("! nmcli -t -f NAME connection show | grep -q '^NexusHome$'", timeout=120)
        except Exception:
            diagnostics()
            raise

    with subtest("a wrong password says so and asks again"):
        click_row("NexusHome", lambda s: s["passwordFor"] == "NexusHome", "no password box after forgetting")
        type_text("not the password")
        key(28)
        until(lambda s: "password" in s["error"].lower() and s["passwordFor"] == "NexusHome",
              "a wrong password was not reported", timeout=240)
        assert active_ssid() != "NexusHome", "connected with a wrong password"
        screenshot("05-wrong-password", lambda s: s["passwordFor"] == "NexusHome" and "password" in s["error"].lower())
  '';
}
