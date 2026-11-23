# Mail, the cloud and documents, as a Windows computer has them:
#  - Thunderbird for email, calendars and contacts; it signs in to Microsoft
#    365, Outlook.com, Gmail and others itself (OAuth in its own window);
#  - cloud drives as folders (nexus-cloud, Settings, Accounts): OneDrive,
#    Google Drive, Dropbox and Nextcloud mounted by rclone at ~/NAME, with a
#    local cache, started at each login;
#  - websites as apps (nexus-webapp, Apps, Web apps): Outlook, Teams,
#    Microsoft 365, Google Docs, WhatsApp… in their own window and in the
#    launcher, through Chromium;
#  - PDF: Papers reads and fills in forms; PDF Arranger merges, splits,
#    reorders and rotates pages; Xournal++ writes and signs on them;
#  - spell checking in the installer's languages (hunspell), for LibreOffice,
#    Firefox and GTK apps.
{ config, lib, pkgs, ... }:
let
  cfg = config.nexusos.online;
  python = pkgs.python3;
  cloud = pkgs.writeShellScriptBin "nexus-cloud" ''
    export PATH=${lib.makeBinPath [ pkgs.rclone pkgs.systemd pkgs.xdg-utils ]}:$PATH
    exec ${python}/bin/python3 -I ${../../scripts/desktop/nexus_cloud.py} "$@"
  '';
  webapp = pkgs.writeShellScriptBin "nexus-webapp" ''
    exec ${python}/bin/python3 -I ${../../scripts/desktop/nexus_webapp.py} "$@"
  '';
  # One per language offered by the installer (nix/modules/locales.json) that has one.
  dictionaries = with pkgs.hunspellDicts; [
    en_GB-ise en_US en_AU en_CA de_DE de_AT de_CH fr-moderne es_ES es_MX es_AR it_IT pt_PT pt_BR nl_NL
    da_DK sv_SE nb_NO cs_CZ sk_SK pl_PL hu_HU ro_RO hr_HR et_EE el_GR ru_RU uk_UA tr_TR he_IL ko_KR cy_GB
  ];
in
{
  options.nexusos.online.enable = lib.mkOption {
    type = lib.types.bool;
    default = true;
    description = "Thunderbird, cloud drives as folders, websites as apps, PDF tools and spell checking.";
  };

  config = lib.mkIf cfg.enable {
    environment.systemPackages = [ cloud webapp pkgs.rclone pkgs.thunderbird pkgs.pdfarranger pkgs.xournalpp pkgs.hunspell ] ++ dictionaries;

    # LibreOffice and hunspell look in DICPATH; Firefox is told the same folder.
    environment.sessionVariables.DICPATH = "/run/current-system/sw/share/hunspell";
    programs.firefox.preferences."spellchecker.dictionary_path" = "/run/current-system/sw/share/hunspell";

    # Mail links open Thunderbird unless the person chooses otherwise (Settings, Default apps).
    xdg.mime.defaultApplications."x-scheme-handler/mailto" = lib.mkDefault "thunderbird.desktop";

    # One cloud drive: `rclone mount NAME: ~/NAME`. Files open from a local
    # cache (kept up to 10 GB, a month) and changes upload in the background.
    systemd.user.services."nexus-cloud@" = {
      description = "Cloud drive %i in your home folder";
      unitConfig.StartLimitIntervalSec = 0;
      # rclone finds fusermount3 (setuid) here.
      path = [ "/run/wrappers" ];
      serviceConfig = {
        Type = "notify";
        ExecStartPre = "${pkgs.coreutils}/bin/mkdir -p %h/%i";
        ExecStart = "${pkgs.rclone}/bin/rclone mount %i: %h/%i --vfs-cache-mode full --vfs-cache-max-size 10G "
          + "--vfs-cache-max-age 720h --dir-cache-time 5m --poll-interval 1m --volname %i";
        ExecStop = "/run/wrappers/bin/fusermount3 -uz %h/%i";
        # Offline at login: try again until the network is there.
        Restart = "on-failure";
        RestartSec = 30;
      };
    };
    systemd.user.services.nexus-cloud-start = {
      description = "Start your cloud drives";
      wantedBy = [ "nexus-session.target" ];
      partOf = [ "nexus-session.target" ];
      after = [ "nexus-session.target" ];
      serviceConfig = { Type = "oneshot"; ExecStart = "${cloud}/bin/nexus-cloud start-all"; };
    };
  };
}
