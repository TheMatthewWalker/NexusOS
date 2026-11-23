# Fingerprint sign-in (Settings, Accounts), as Windows Hello: off until
# turned on through Guardian (nexusos.fingerprint.enable) on a computer with
# a reader. A finger then unlocks the lock screen (hyprlock reads it itself)
# and approves Guardian and administrator prompts (polkit, sudo). The first
# login of the day still asks for the password: it is what unlocks the
# keyring, and the disk where it is encrypted.
{ config, lib, ... }:
{
  options.nexusos.fingerprint.enable = lib.mkOption {
    type = lib.types.bool;
    default = false;
    description = "Fingerprint for the lock screen and approvals (fprintd), on computers with a reader.";
  };

  config = lib.mkIf config.nexusos.fingerprint.enable {
    services.fprintd.enable = true;
    # The password, never a finger, at login (it unlocks the keyring) and in
    # hyprlock's own password check (its fingerprint support is separate).
    security.pam.services.greetd.fprintAuth = false;
    security.pam.services.login.fprintAuth = false;
    security.pam.services.hyprlock.fprintAuth = false;
  };
}
