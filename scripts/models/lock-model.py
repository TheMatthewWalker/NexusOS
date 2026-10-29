#!/usr/bin/env python3
"""Pin an Ollama model so the installer image can carry it.

    nix run .#lock-model                 # the default model, qwen3:8b
    nix run .#lock-model -- qwen2.5:7b   # another one

Fetches the model's manifest from the Ollama registry and writes it to
nix/models/<name>-<tag>.json. The manifest lists every file of the model by
its sha256 digest, so the image build (nix/packages/ollama-model.nix) can
download them and Nix checks each one. The file is added to git, because the
flake only sees files git knows about; commit it to keep it.
"""
import json
import subprocess
import sys
import urllib.request
from pathlib import Path

ACCEPT = "application/vnd.docker.distribution.manifest.v2+json"


def main():
    model = sys.argv[1] if len(sys.argv) > 1 else "qwen3:8b"
    name, _, tag = model.partition(":")
    tag = tag or "latest"
    if "/" in name:
        sys.exit("Only models from the Ollama library (no namespace) are supported.")
    request = urllib.request.Request(f"https://registry.ollama.ai/v2/library/{name}/manifests/{tag}",
                                     headers={"Accept": ACCEPT})
    with urllib.request.urlopen(request, timeout=60) as response:
        manifest = response.read().decode()
    parsed = json.loads(manifest)
    layers = [parsed["config"]] + parsed["layers"]
    for layer in layers:
        if not layer["digest"].startswith("sha256:") or len(layer["digest"]) != 71:
            sys.exit(f"Unexpected digest in the manifest: {layer['digest']}")
    root = Path(subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True,
                               check=True).stdout.strip())
    out = root / "nix/models" / f"{name}-{tag}.json"
    out.write_text(json.dumps({"name": name, "tag": tag, "manifest": manifest}, indent=2) + "\n")
    subprocess.run(["git", "-C", str(root), "add", str(out)], check=True)
    size = sum(layer.get("size", 0) for layer in layers)
    print(f"Pinned {name}:{tag} ({size / 1e9:.1f} GB, {len(layers)} files) in {out.relative_to(root)}.")
    print("The next `nix build .#iso` puts it on the image. Commit the file to keep it.")


if __name__ == "__main__":
    main()
