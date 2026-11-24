# The only unfree software NexusOS allows is what a module asks for by name
# (or name prefix) here, for hardware that needs it: NVIDIA's driver, and
# Broadcom's Wi-Fi driver where it is turned on. Unfree software in general
# stays refused.
{ config, lib, ... }:
let
  cfg = config.nexusos.unfree;
in
{
  options.nexusos.unfree = {
    names = lib.mkOption { type = lib.types.listOf lib.types.str; default = [ ]; internal = true;
                           description = "Unfree package names allowed, for hardware that needs them."; };
    prefixes = lib.mkOption { type = lib.types.listOf lib.types.str; default = [ ]; internal = true;
                              description = "Unfree package name prefixes allowed."; };
  };
  config = lib.mkIf (cfg.names != [ ] || cfg.prefixes != [ ]) {
    nixpkgs.config.allowUnfreePredicate = pkg:
      let name = lib.getName pkg; in
      builtins.elem name cfg.names || lib.any (prefix: lib.hasPrefix prefix name) cfg.prefixes;
  };
}
