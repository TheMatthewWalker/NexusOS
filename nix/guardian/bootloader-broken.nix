{ lib, ... }:
{
  # Deliberately unusable kernel init, confined to the isolated recovery lab.
  # Never import this into a host configuration.
  boot.kernelParams = lib.mkForce [
    "console=ttyS0,115200n8"
    "init=/nexus-intentionally-missing-init"
  ];
  system.nixos.label = "guardian-intentionally-broken";
}
