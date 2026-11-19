# colibri (https://github.com/JustVugg/colibri, Apache-2.0, by Vincenzo
# Fornaro): runs mixture-of-experts models larger than the computer's memory.
# The part of the model every word uses stays in memory; the experts are read
# from the disk when the model asks for them. NexusOS's "very large model"
# (nix/modules/colibri.nix) is this engine behind Core.
#
# Built here from a pinned commit rather than through colibri's own flake, so
# that it uses NexusOS's nixpkgs and only what serving needs:
# - the engines for the chat models NexusOS offers (qwen36: Qwen3.6 and
#   Qwen3-Coder; deepseek_v4: DeepSeek V4 Flash; colibri: GLM-5.2), with
#   Vulkan, so any graphics processor, built-in ones included, can hold part
#   of the model. Vulkan is opened at run time: without a GPU they run on the
#   processor;
# - the coli launcher and its server, which need the Python standard library
#   only (the converter tools that need torch are left out);
# - x86-64-v3 (AVX2, FMA, BMI2: Intel 2013 and later, AMD 2015 and later).
#   Core checks the processor before offering it (Colibri.cs).
#
# Its licence and notices are installed with it, in share/doc/colibri.
{ lib, stdenv, fetchFromGitHub, python3, makeWrapper, shaderc, vulkan-headers, vulkan-loader }:
stdenv.mkDerivation {
  pname = "colibri";
  version = "2.1.0";

  src = fetchFromGitHub {
    owner = "JustVugg";
    repo = "colibri";
    rev = "0a5504a1dc64f86fcd0fe7db298b0448c730e7e6";
    hash = "sha256-ddx8OAU5FsVmrPs96stnDQSm6V/7xshuao890sKYqFQ=";
  };

  nativeBuildInputs = [ makeWrapper shaderc python3 ];
  buildInputs = [ vulkan-headers stdenv.cc.cc.lib ];
  enableParallelBuilding = true;

  # The Makefile's own checks (git describe, the build-config stamp) run Python.
  postPatch = ''
    patchShebangs c
  '';

  buildPhase = ''
    runHook preBuild
    make -C c -j$NIX_BUILD_CORES qwen36 colibri VK=1 ARCH=x86-64-v3
    make -C c deepseek-v4 VK=1 ARCH=x86-64-v3
    runHook postBuild
  '';

  installPhase = ''
    runHook preInstall
    lib=$out/lib/colibri
    install -d $lib/tools $lib/shaders $out/bin $out/share/doc/colibri
    for engine in qwen36 colibri deepseek_v4; do
      install -m 755 c/$engine $lib/$engine
    done
    # The launcher and the server it imports: standard library Python.
    install -m 755 c/coli $lib/coli
    for f in family_registry resource_plan doctor autotune openai_server cluster v4_dsml v41_dsml version \
             image_engine term_image setup_flow setup_hw setup_catalog setup_download mcp_server; do
      install -m 644 c/$f.py $lib/
    done
    install -m 644 c/tools/*.py $lib/tools/
    cp -r c/data $lib/ 2>/dev/null || true
    install -m 644 c/shaders/*.spv $lib/shaders/
    install -m 644 LICENSE NOTICE THIRD_PARTY_NOTICES.md $out/share/doc/colibri/

    # coli finds each engine beside itself. COLI_ENGINE stays unset, so it picks
    # the engine from the model's config.json.
    makeWrapper ${python3}/bin/python3 $out/bin/coli \
      --add-flags $lib/coli \
      --set PYTHONPATH $lib \
      --set PYTHONDONTWRITEBYTECODE 1 \
      --set COLI_VK_SHADERS $lib/shaders \
      --set COLI_VK_LOADER ${vulkan-loader}/lib/libvulkan.so.1
    runHook postInstall
  '';

  doInstallCheck = true;
  installCheckPhase = ''
    export PYTHONDONTWRITEBYTECODE=1
    for engine in qwen36 colibri deepseek_v4; do test -x $out/lib/colibri/$engine; done
    test -f $out/lib/colibri/shaders/qmatmul.spv
    $out/bin/coli --version
    PYTHONPATH=$out/lib/colibri ${python3}/bin/python3 -c 'import openai_server, setup_download, setup_catalog'
  '';

  meta = {
    description = "Runs mixture-of-experts models larger than memory, experts streamed from the disk";
    homepage = "https://github.com/JustVugg/colibri";
    license = lib.licenses.asl20;
    platforms = [ "x86_64-linux" ];
    mainProgram = "coli";
  };
}
