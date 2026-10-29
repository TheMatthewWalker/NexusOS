# The NexusOS login screen (shell/qml/greeter.qml in cage, under greetd):
# it shows, a wrong password is refused and says so, and the right one starts
# the desktop. Typed with real kernel key events.
{ pkgs, desktopModule }:
pkgs.testers.runNixOSTest {
  name = "login-screen";
  nodes.machine = { ... }: {
    imports = [ desktopModule ];
    system.stateVersion = "26.05";
    virtualisation.memorySize = 4096;
    virtualisation.cores = 4;
    virtualisation.qemu.options = [ "-vga none" "-device virtio-gpu-pci,xres=1280,yres=800" ];
    users.users.alice = { isNormalUser = true; uid = 1000; password = "nexus-login"; description = "Alice Example"; };
    services.nexus-desktop = {
      enable = true;
      reducedMotion = true;
      blur = false;
      coreDetail = 16;
      greeter = { user = "alice"; fullName = "Alice Example"; };
    };
  };

  testScript = ''
    def journal():
        print(machine.execute("journalctl -b --no-pager | grep -iE 'greet|quickshell|cage|pam' | tail -120")[1])

    machine.start()
    machine.wait_for_unit("greetd.service")

    with subtest("the graphical login screen shows, not the text one"):
        machine.wait_until_succeeds("pgrep -f '[q]uickshell.*greeter[.]qml'", timeout=600)
        machine.fail("pgrep -x tuigreet")
        # Under software emulation the screen takes a minute or more to load:
        # type only once the password box has the keyboard.
        machine.wait_until_succeeds("journalctl -b -t nexus-greeter | grep -q 'password box has the keyboard'", timeout=900)
        machine.sleep(5)
        machine.screenshot("01-login-screen")

    with subtest("a wrong password is refused, and the screen stays"):
        machine.send_chars("not-the-password\n")
        machine.wait_until_succeeds("journalctl -b -t nexus-greeter | grep -q 'login refused'", timeout=300)
        machine.fail("pgrep -u alice Hyprland")
        machine.succeed("pgrep -f '[q]uickshell.*greeter[.]qml'")
        machine.screenshot("02-wrong-password")

    with subtest("the right password starts the desktop"):
        machine.sleep(5)                        # the refused attempt hands the keyboard back to the password box
        machine.send_chars("nexus-login\n")
        try:
            machine.wait_until_succeeds("pgrep -u alice -f '[s]tart-hyprland'", timeout=300)
        except Exception:
            machine.screenshot("03-failed")
            journal()
            raise
        machine.wait_until_succeeds("pgrep -u alice -f '[n]exus-shell/shell[.]qml'", timeout=600)
        try:
            machine.wait_until_fails("pgrep -f '[q]uickshell.*greeter[.]qml'", timeout=120)
        except Exception:
            print(machine.execute("ps -eo pid,ppid,user,stat,etime,args | grep -E 'cage|quickshell|Hyprland|greet' | grep -v grep")[1])
            journal()
            raise
        machine.sleep(30)
        machine.screenshot("03-desktop")
  '';
}
