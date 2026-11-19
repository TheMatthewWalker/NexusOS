# The keyboard layout, the same everywhere: the password prompt that unlocks
# the disk at start-up, the text consoles, the login screen and the desktop.
# nexus-install writes nexusos.keyboard.layout to local.nix, from the
# person's choice (guessed from their time zone).
#
# The prompt that unlocks the disk comes before the desktop, so its layout is
# loaded early (console.earlySetup): a password typed on a UK keyboard in
# the installer must work on the same keyboard at start-up. The reference
# system the installer image carries has early setup too, so an offline
# install with any layout builds nothing new but a small settings file.
{ config, lib, ... }:
let
  cfg = config.nexusos.keyboard;
  # XKB layout (desktop) -> kbd keymap (consoles and the unlock prompt), its
  # name, and the countries that mostly use it (the installer's first guess).
  # Latin layouts only: the disk's password is typed before any layout switching.
  layouts = lib.importJSON ../installer/keyboards.json;
in
{
  options.nexusos.keyboard = {
    layout = lib.mkOption {
      type = lib.types.enum (builtins.attrNames layouts);
      default = "us";
      description = "The keyboard layout (an XKB name), for the desktop, the consoles and the disk's unlock prompt.";
    };
    layouts = lib.mkOption {
      type = lib.types.attrs;
      readOnly = true;
      default = layouts;
      description = "The layouts NexusOS offers (nix/installer/keyboards.json, which the installer reads too).";
    };
  };

  config = {
    services.nexus-desktop.keyboardLayout = lib.mkDefault cfg.layout;
    console.keyMap = lib.mkDefault layouts.${cfg.layout}.console;
    console.earlySetup = lib.mkDefault true;
  };
}
