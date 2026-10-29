# What an install needs besides the finished system, so it works with no
# internet: the tools that build the parts specific to each computer (its
# initrd and boot entries from hardware-configuration.nix), from nixpkgs'
# own installer test, plus CPU microcode, which the hardware scan turns on for
# Intel and AMD processors. The installer image carries these, and the
# installer VM test (no network) gets exactly this list, so the test proves
# the image installs offline.
pkgs: with pkgs; [
  stdenv bintools brotli brotli.dev brotli.lib desktop-file-utils docbook5 docbook_xsl_ns
  kbd.dev kmod.dev libarchive.dev libxml2.bin libxslt.bin nixos-rebuild-ng ntp
  perlPackages.ConfigIniFiles perlPackages.FileSlurp perlPackages.JSON perlPackages.ListCompare
  perlPackages.XMLLibXML (python3.withPackages (p: [ p.mistune ])) shared-mime-info sudo
  switch-to-configuration-ng texinfo unionfs-fuse lndir shellcheck-minimal systemdMinimal.out
  curl zstd.bin mypy bootspec
  microcode-intel microcode-amd
]
