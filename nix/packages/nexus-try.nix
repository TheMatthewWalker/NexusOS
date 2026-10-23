# nexus-try: run an app from the pinned nixpkgs in a throwaway bubblewrap
# sandbox without installing it ("Try before you install", docs/ARCHITECTURE.md).
# The argument-validation and sandbox-construction suite runs as the check phase.
{ lib, stdenvNoCC, makeWrapper, bash, bashInteractive, bubblewrap, coreutils, gnugrep, nix, python3 }:
let root = ../..;
in
stdenvNoCC.mkDerivation {
  pname = "nexus-try";
  version = "0.1.0";
  src = lib.fileset.toSource {
    inherit root;
    fileset = lib.fileset.unions [ ./nexus-try.sh (root + "/tests/try-apps") ];
  };
  nativeBuildInputs = [ makeWrapper ];
  buildInputs = [ bash ];
  nativeCheckInputs = [ python3 bash coreutils ];
  doCheck = true;
  checkPhase = ''
    runHook preCheck
    patchShebangs nix/packages/nexus-try.sh
    ${bash}/bin/bash -n nix/packages/nexus-try.sh
    python3 -m unittest discover -s tests/try-apps -v
    runHook postCheck
  '';
  installPhase = ''
    runHook preInstall
    install -Dm755 nix/packages/nexus-try.sh $out/libexec/nexus-try
    patchShebangs $out/libexec/nexus-try
    # bwrap is pinned; nix is appended so the system's own nix (and its daemon
    # and registry) is used when present.
    makeWrapper $out/libexec/nexus-try $out/bin/nexus-try \
      --set NEXUS_TRY_BWRAP ${bubblewrap}/bin/bwrap \
      --set NEXUS_TRY_SHELL ${bashInteractive}/bin/bash \
      --set NEXUS_TRY_SHELL_PATH ${lib.makeBinPath [ coreutils gnugrep ]} \
      --unset NEXUS_TRY_STORE \
      --prefix PATH : ${lib.makeBinPath [ coreutils ]} \
      --suffix PATH : ${lib.makeBinPath [ nix ]}
    runHook postInstall
  '';
  meta = {
    description = "Try a nixpkgs app in a throwaway sandbox without installing it";
    license = lib.licenses.mit;
    platforms = lib.platforms.linux;
    mainProgram = "nexus-try";
  };
}
