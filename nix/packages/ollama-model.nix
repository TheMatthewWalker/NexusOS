# An Ollama model as a store path, laid out like Ollama's models directory:
#   manifests/registry.ollama.ai/library/<name>/<tag>
#   blobs/sha256-<hex>
# Copied into Ollama's models directory, the model is ready with no download.
#
# fromLock reads a lock file written by `nix run .#lock-model` (the registry's
# manifest); each blob is fetched by its sha256 digest, so Nix checks every byte.
{ lib, runCommand, fetchurl, writeText }:
let
  build = { name, tag, manifest, blobs }:
    runCommand "ollama-model-${name}-${tag}" { } ''
      mkdir -p $out/blobs $out/manifests/registry.ollama.ai/library/${name}
      cp ${writeText "manifest" manifest} $out/manifests/registry.ollama.ai/library/${name}/${tag}
      ${lib.concatStringsSep "\n" (lib.mapAttrsToList (digest: blob:
        "ln -s ${blob} $out/blobs/${lib.replaceStrings [ ":" ] [ "-" ] digest}") blobs)}
    '';
in
{
  inherit build;
  fromLock = lockFile:
    let
      lock = lib.importJSON lockFile;
      manifest = builtins.fromJSON lock.manifest;
      digests = map (l: l.digest) ([ manifest.config ] ++ manifest.layers);
      fetch = digest: fetchurl {
        name = "ollama-blob-" + lib.removePrefix "sha256:" digest;
        url = "https://registry.ollama.ai/v2/library/${lock.name}/blobs/${digest}";
        sha256 = lib.removePrefix "sha256:" digest;
      };
    in
    build {
      inherit (lock) name tag manifest;
      blobs = lib.genAttrs digests fetch;
    };
}
