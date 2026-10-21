# Guardian broker, approval console and attestation tool, with the shared
# option schema. The Python unit and protocol suite runs as the check phase.
{ lib, stdenvNoCC, python3, makeWrapper, nix, grub2 }:
let
  python = python3.withPackages (ps: [ ps.cryptography ]);
  root = ../..;
in
stdenvNoCC.mkDerivation {
  pname = "nexus-guardian";
  version = "0.3.0";
  src = lib.fileset.toSource {
    inherit root;
    fileset = lib.fileset.unions [ (root + "/scripts/guardian") (root + "/schema") (root + "/tests/guardian") ];
  };
  nativeBuildInputs = [ makeWrapper ];
  nativeCheckInputs = [ python ];
  doCheck = true;
  checkPhase = ''
    runHook preCheck
    ${python}/bin/python3 -m unittest discover -s tests/guardian
    runHook postCheck
  '';
  installPhase = ''
    runHook preInstall
    share=$out/share/nexus-guardian
    mkdir -p $share $out/bin
    cp -r scripts schema $share/
    # -E -s: ignore PYTHON* variables and user site-packages.
    makeWrapper ${python}/bin/python3 $out/bin/nexus-guardian \
      --add-flags "-E -s $share/scripts/guardian/root_approval_issuer.py"
    makeWrapper ${python}/bin/python3 $out/bin/nexus-guardian-attest \
      --add-flags "-E -s $share/scripts/guardian/attestation.py"
    makeWrapper ${python}/bin/python3 $out/bin/nexus-guardian-client \
      --add-flags "-E -s $share/scripts/guardian/broker_client.py"
    # Staging and boot assessment are the only tools that touch the system
    # profile and bootloader; they get exactly the binaries they need.
    makeWrapper ${python}/bin/python3 $out/bin/nexus-guardian-stage \
      --add-flags "-E -s $share/scripts/guardian/stage_executor.py" \
      --prefix PATH : ${lib.makeBinPath [ nix grub2 ]}
    makeWrapper ${python}/bin/python3 $out/bin/nexus-guardian-boot-check \
      --add-flags "-E -s $share/scripts/guardian/boot_check.py" \
      --prefix PATH : ${lib.makeBinPath [ nix grub2 ]}
    runHook postInstall
  '';
  meta = {
    description = "NexusOS Guardian review broker (decides; never activates the running system)";
    license = lib.licenses.mit;
    platforms = lib.platforms.linux;
  };
}
