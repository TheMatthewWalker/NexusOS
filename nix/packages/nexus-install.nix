{ writeShellApplication, parted, dosfstools, e2fsprogs, util-linux, nixos-install-tools
, nix, coreutils, gnugrep, gawk, gnused, systemd }:
writeShellApplication {
  name = "nexus-install";
  runtimeInputs = [ parted dosfstools e2fsprogs util-linux nixos-install-tools nix coreutils gnugrep gawk gnused systemd ];
  text = builtins.readFile ../installer/nexus-install.sh;
}
