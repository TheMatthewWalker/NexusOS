{ writeShellApplication, parted, dosfstools, e2fsprogs, util-linux, nixos-install-tools
, nix, coreutils, gnugrep, gawk, gnused, systemd, pciutils, curl }:
writeShellApplication {
  name = "nexus-install";
  runtimeInputs = [ parted dosfstools e2fsprogs util-linux nixos-install-tools nix coreutils gnugrep gawk gnused systemd pciutils curl ];
  text = builtins.readFile ../installer/nexus-install.sh;
}
