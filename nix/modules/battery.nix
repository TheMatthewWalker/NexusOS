# Battery care (Settings, Power): stop charging at 80%, which keeps a laptop
# battery healthy for longer when it is plugged in most of the time. Where the
# firmware lets Linux set it (ThinkPad, ASUS, Dell, Framework, many others:
# charge_control_end_threshold), the threshold is written whenever the
# battery appears, at start-up and after resume. Charging starts again below
# 75% where the firmware has a start threshold too (the start is written
# first, as ThinkPads need it lower than the end).
# A system change, so Settings proposes it through Guardian
# (schema/guardian-options.json: nexusos.power.chargeLimit.enable).
{ config, lib, ... }:
{
  options.nexusos.power.chargeLimit.enable = lib.mkOption {
    type = lib.types.bool;
    default = false;
    description = "Stop charging the battery at 80% (where the firmware supports it).";
  };

  config = lib.mkIf config.nexusos.power.chargeLimit.enable {
    services.udev.extraRules = ''
      ACTION=="add|change", SUBSYSTEM=="power_supply", KERNEL=="BAT*", TEST=="charge_control_start_threshold", ATTR{charge_control_start_threshold}="75"
      ACTION=="add|change", SUBSYSTEM=="power_supply", KERNEL=="BAT*", TEST=="charge_control_end_threshold", ATTR{charge_control_end_threshold}="80"
    '';
  };
}
