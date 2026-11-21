# Settings, Storage and Settings, Default apps.
#   nexus-storage        as the person: what uses the disk, empty the trash, clear caches
#   nexus-storage-clean  as root, through pkexec (an administrator's password):
#                        remove system versions older than two weeks (always keeping
#                        the one in use and the two newest) and unused app runtimes
#   nexus-default-apps   as the person: which app opens links, mail, folders, music,
#                        videos, pictures, text and PDFs (~/.config/mimeapps.list)
{ config, lib, pkgs, ... }:
let
  python = "${pkgs.python3}/bin/python3 -I";
  storage = ../../scripts/desktop/nexus_storage.py;
  commands = pkgs.runCommand "nexus-storage" { } ''
    mkdir -p $out/bin
    cat > $out/bin/nexus-storage <<EOF
    #!${pkgs.runtimeShell}
    case "\''${1:-}" in report|empty-trash|clear-cache) ;; *) echo '{"error": "Not an operation"}'; exit 2 ;; esac
    export PATH=${lib.makeBinPath [ pkgs.coreutils ]}:\$PATH
    exec ${python} ${storage} "\$1"
    EOF
    cat > $out/bin/nexus-storage-clean <<EOF
    #!${pkgs.runtimeShell}
    export PATH=${lib.makeBinPath [ config.nix.package pkgs.flatpak pkgs.coreutils ]}
    exec ${python} ${storage} clean-system
    EOF
    cat > $out/bin/nexus-default-apps <<EOF
    #!${pkgs.runtimeShell}
    exec ${python} ${../../scripts/desktop/nexus_default_apps.py} "\$@"
    EOF
    chmod +x $out/bin/*
  '';
  policy = pkgs.writeTextDir "share/polkit-1/actions/org.nexusos.storage.policy" ''
    <?xml version="1.0" encoding="UTF-8"?>
    <!DOCTYPE policyconfig PUBLIC "-//freedesktop//DTD PolicyKit Policy Configuration 1.0//EN"
     "http://www.freedesktop.org/standards/PolicyKit/1/policyconfig.dtd">
    <policyconfig>
      <vendor>NexusOS</vendor>
      <action id="org.nexusos.storage.clean">
        <description>Remove earlier system versions</description>
        <message>An administrator's password is needed to remove earlier system versions and free their space.</message>
        <defaults>
          <allow_any>no</allow_any>
          <allow_inactive>no</allow_inactive>
          <allow_active>auth_admin</allow_active>
        </defaults>
        <annotate key="org.freedesktop.policykit.exec.path">/run/current-system/sw/bin/nexus-storage-clean</annotate>
      </action>
    </policyconfig>
  '';
in
{
  config = {
    security.polkit.enable = true;
    environment.systemPackages = [ commands policy ];
  };
}
