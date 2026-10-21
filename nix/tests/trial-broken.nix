# Lab-only: a system whose kernel cannot find init, standing in for a builder
# that attested a bad build. Never import into a host configuration.
{ ... }:
{
  boot.kernelParams = [ "init=/nexus-intentionally-missing-init" ];
  system.nixos.label = "guardian-trial-broken";
}
