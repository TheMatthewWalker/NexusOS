# The Nexus desktop shell: the SpaceUI Quickshell config plus its generated
# tokens, Nova core shader and noise atlas, and a `nexus-shell` launcher.
{ lib, stdenvNoCC, python3, qt6, quickshell, makeWrapper }:
stdenvNoCC.mkDerivation {
  pname = "nexus-shell";
  version = "0.1.0";
  src = lib.fileset.toSource {
    root = ../..;
    fileset = lib.fileset.unions [
      ../../shell/qml
      ../../shell/tools
      ../../shell/layout.json
      ../../docs/design/spaceui/design-tokens.json
    ];
  };
  nativeBuildInputs = [ (python3.withPackages (ps: [ ps.pillow ])) qt6.qtshadertools makeWrapper ];
  dontConfigure = true;
  # quickshell is already a wrapped Qt app; this package only adds QML and data.
  dontWrapQtApps = true;
  buildPhase = ''
    runHook preBuild
    python3 shell/tools/build.py --out build
    runHook postBuild
  '';
  installPhase = ''
    runHook preInstall
    mkdir -p $out/share
    cp -r build $out/share/nexus-shell
    makeWrapper ${quickshell}/bin/quickshell $out/bin/nexus-shell \
      --add-flags "-p $out/share/nexus-shell/shell.qml"
    # Talks to the running shell, e.g. `nexus-shell-ipc call nexus toggle`.
    makeWrapper ${quickshell}/bin/quickshell $out/bin/nexus-shell-ipc \
      --add-flags "ipc -p $out/share/nexus-shell/shell.qml"
    runHook postInstall
  '';
  meta = {
    description = "SpaceUI desktop shell for NexusOS (Quickshell on Hyprland)";
    license = lib.licenses.mit;
    mainProgram = "nexus-shell";
  };
}
