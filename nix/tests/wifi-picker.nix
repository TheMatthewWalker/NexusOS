# The system panel's Wi-Fi list, end to end. A simulated Wi-Fi radio
# (mac80211_hwsim) runs two access points: "NexusHome" (WPA2, with a password)
# and "NexusCafe" (open). NetworkManager drives a second simulated radio, and
# real kernel input (ydotool) opens the panel, picks a network, types the
# password and presses Enter.
{ pkgs, desktopModule }:
let
  password = "correct horse battery";
in
pkgs.testers.runNixOSTest {
  name = "wifi-picker";
  nodes.machine = { pkgs, ... }: {
    imports = [ desktopModule ];
    system.stateVersion = "26.05";
    virtualisation.memorySize = 4096;
    virtualisation.cores = 4;
    virtualisation.qemu.options = [ "-vga none" "-device virtio-gpu-pci,xres=1280,yres=800" ];
    users.users.alice = { isNormalUser = true; uid = 1000; password = "nexus"; extraGroups = [ "networkmanager" ]; };
    services.nexus-desktop = {
      enable = true;
      autologinUser = "alice";
      reducedMotion = true;
      blur = false;
      coreDetail = 16;
      extraHyprlandConfig = ''
        input {
          accel_profile = flat
        }
      '';
    };

    # Two simulated radios: wlan0 hosts the access points, wlan1 is the laptop's.
    boot.kernelModules = [ "mac80211_hwsim" ];
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
    systemd.services.dnsmasq.after = [ "hostapd.service" ];
    systemd.services.dnsmasq.wants = [ "hostapd.service" ];

    programs.ydotool.enable = true;
  };

  testScript = ''
    import json
    import time

    W, H = 1280, 800
    MARK = (W - 23 - 17, 10 + 17)        # the collapsed system panel (design tokens: orbitalControl.collapsed)
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

    def panel():
        out = machine.succeed("su - alice -c 'XDG_RUNTIME_DIR=/run/user/1000 nexus-shell-ipc call systempanel wifi'")
        return json.loads(out)

    def diagnostics():
        print(machine.execute("journalctl -b --no-pager -t nexus-shell | tail -60")[1])
        print(machine.execute("nmcli -f all device wifi list ifname wlan1; nmcli device; nmcli connection")[1])
        print(machine.execute("journalctl -b --no-pager -u NetworkManager | tail -40")[1])

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

    def open_panel():
        move(*MARK)
        until(lambda s: s["expanded"], "the panel did not open on hover", timeout=120, poke=lambda: move(*MARK))

    def active_ssid():
        return machine.succeed("nmcli -t -f ACTIVE,SSID device wifi list ifname wlan1 --rescan no | grep '^yes:' | cut -d: -f2 || true").strip()

    machine.start()
    machine.wait_for_unit("multi-user.target")
    machine.wait_for_unit("hostapd.service")
    machine.wait_until_succeeds("ls /run/user/1000/hypr/*/.socket.sock", timeout=600)
    machine.wait_until_succeeds(
        "su - alice -c 'HYPRLAND_INSTANCE_SIGNATURE=$(ls /run/user/1000/hypr | head -1) hyprctl layers' | grep -q nexus-system-panel",
        timeout=600)

    with subtest("hovering the system panel lists the nearby networks"):
        open_panel()
        state = until(lambda s: s["wifiOn"] and row(s, "NexusHome") and row(s, "NexusCafe"),
                      "the networks never appeared", poke=lambda: move(*MARK))
        assert row(state, "NexusHome")["security"] == "password", state
        assert row(state, "NexusCafe")["security"] == "open", state
        machine.screenshot("01-networks")

    with subtest("a secured network asks for its password; a short one is refused before sending"):
        r = row(panel(), "NexusHome")
        click(r["x"], r["y"])
        until(lambda s: s["passwordFor"] == "NexusHome", "no password box")
        type_text("short")
        key(28)                                    # Enter
        until(lambda s: "at least 8" in s["error"], "the short password was not refused")
        assert active_ssid() == "", "connected with a short password"
        machine.screenshot("02-short-password")

    with subtest("the right password connects through NetworkManager"):
        key(29, 30)                                # Ctrl+A: select what is there, then replace it
        type_text(PASSWORD)
        key(28)
        until(lambda s: (row(s, "NexusHome") or {}).get("connected"), "NexusHome did not connect", timeout=180)
        assert active_ssid() == "NexusHome", active_ssid()
        machine.succeed("ip -4 addr show wlan1 | grep -q 'inet 192.168.60.'")
        machine.screenshot("03-connected")

    with subtest("the password is kept by NetworkManager, not logged or kept by the shell"):
        machine.succeed("grep -rqs " + repr("psk=" + PASSWORD) + " /etc/NetworkManager/system-connections/")
        machine.fail("journalctl -b --no-pager | grep -qF " + repr(PASSWORD))
        machine.fail("grep -rqsF " + repr(PASSWORD) + " /home/alice /run/user/1000")

    with subtest("an open network connects with one click"):
        r = row(panel(), "NexusCafe")
        click(r["x"], r["y"])
        until(lambda s: (row(s, "NexusCafe") or {}).get("connected"), "NexusCafe did not connect", timeout=180)
        assert active_ssid() == "NexusCafe", active_ssid()

    with subtest("a saved network reconnects without asking again"):
        r = row(panel(), "NexusHome")
        assert r["known"], r
        click(r["x"], r["y"])
        until(lambda s: (row(s, "NexusHome") or {}).get("connected"), "NexusHome did not reconnect", timeout=180)
        assert panel()["passwordFor"] == "", "asked for a saved password again"

    with subtest("Forget removes the saved password"):
        r = row(panel(), "NexusHome")
        click(r["x"], r["y"])                      # a connected network shows Disconnect and Forget
        time.sleep(3)
        machine.screenshot("04-details")
        machine.succeed("nmcli -t -f NAME connection show | grep -qx NexusHome")
        r = until(lambda s: (row(s, "NexusHome") or {}).get("forget"), "no Forget button")
        r = row(r, "NexusHome")
        click(*r["forget"])
        machine.wait_until_succeeds("! nmcli -t -f NAME connection show | grep -q '^NexusHome$'", timeout=120)

    with subtest("a wrong password says so and asks again"):
        r = row(panel(), "NexusHome")
        click(r["x"], r["y"])
        until(lambda s: s["passwordFor"] == "NexusHome", "no password box after forgetting")
        type_text("not the password")
        key(28)
        until(lambda s: "password" in s["error"].lower() and s["passwordFor"] == "NexusHome",
              "a wrong password was not reported", timeout=240)
        assert active_ssid() != "NexusHome", "connected with a wrong password"
        machine.screenshot("05-wrong-password")
  '';
}
