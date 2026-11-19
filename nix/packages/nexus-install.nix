{ writeShellApplication, kbd, parted, dosfstools, e2fsprogs, util-linux, nixos-install-tools
, nix, coreutils, gnugrep, gawk, gnused, systemd, pciutils, curl, cryptsetup, jq }:
writeShellApplication {
  name = "nexus-install";
  runtimeInputs = [ kbd parted dosfstools e2fsprogs util-linux nixos-install-tools nix coreutils gnugrep gawk gnused systemd pciutils curl cryptsetup jq ];
  # Its jq and awk programs are in single quotes on purpose: their $names are theirs.
  excludeShellChecks = [ "SC2016" ];
  # The keyboard layouts it offers, the same file nix/modules/keyboard.nix reads.
  text = ''
    : "''${NEXUS_KEYBOARDS:=${../installer/keyboards.json}}"
    export NEXUS_KEYBOARDS
  '' + builtins.readFile ../installer/nexus-install.sh;
}
