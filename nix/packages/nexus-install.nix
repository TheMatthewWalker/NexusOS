{ writeShellApplication, parted, dosfstools, e2fsprogs, util-linux, nixos-install-tools
, nix, coreutils, gnugrep, gawk, gnused, systemd, pciutils, curl, cryptsetup, jq }:
writeShellApplication {
  name = "nexus-install";
  runtimeInputs = [ parted dosfstools e2fsprogs util-linux nixos-install-tools nix coreutils gnugrep gawk gnused systemd pciutils curl cryptsetup jq ];
  text = builtins.readFile ../installer/nexus-install.sh;
}
