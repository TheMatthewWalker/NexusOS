# Language and region (Settings, Language and region). Each person chooses
# their own language, formats (dates, numbers, money, paper), time zone and
# extra keyboard layouts; none needs a system change:
#  - The session launcher (desktop.nix) reads ~/.config/nexusos/region.env at
#    login and exports LANG, LANGUAGE, LC_* and TZ, so every app follows.
#  - Extra keyboard layouts are applied to Hyprland at once by the shell
#    (DesktopSettings.qml); Alt+Shift switches between them.
# The locales people can choose are built in (i18n.supportedLocales), from
# nix/modules/locales.json, which the shell reads from /etc/nexus too.
{ config, lib, ... }:
let
  locales = lib.importJSON ./locales.json;
in
{
  config = {
    i18n.supportedLocales = lib.mkDefault (lib.unique (
      [ "C.UTF-8/UTF-8" "${config.i18n.defaultLocale}/UTF-8" ]
      # glibc's own name (SUPPORTED), which for a few is without ".UTF-8"
      # (en_IN/UTF-8); LANG=en_IN.UTF-8 still finds it.
      ++ map (l: l.glibc or "${l.locale}/UTF-8") locales));
    environment.etc."nexus/locales.json".source = ./locales.json;
    environment.etc."nexus/keyboards.json".source = ../installer/keyboards.json;
  };
}
