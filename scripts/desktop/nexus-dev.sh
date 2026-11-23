# nexus-dev init LANGUAGE [FOLDER]: set a project up with its tools, the Nix
# way: a flake.nix with a development shell and an .envrc, so the tools are
# there whenever you are in the folder (direnv) and nowhere else, at the
# versions this NexusOS was built with (no download when they are already here).
#   python node rust go java dotnet c
set -eu
usage() { echo "nexus-dev init python|node|rust|go|java|dotnet|c [folder]" >&2; exit 2; }
[ "${1:-}" = init ] || usage
lang="${2:-}"; dir="${3:-.}"
case "$lang" in
  python) pkgs="python3 python3Packages.pip python3Packages.virtualenv uv ruff pyright"; hook='[ -d .venv ] || python -m venv .venv; . .venv/bin/activate' ;;
  node)   pkgs="nodejs_22 pnpm typescript typescript-language-server"; hook='' ;;
  rust)   pkgs="cargo rustc rust-analyzer clippy rustfmt pkg-config openssl"; hook='' ;;
  go)     pkgs="go gopls golangci-lint"; hook='' ;;
  java)   pkgs="jdk21 maven gradle jdt-language-server"; hook='' ;;
  dotnet) pkgs="dotnet-sdk_8 omnisharp-roslyn"; hook='export DOTNET_CLI_TELEMETRY_OPTOUT=1' ;;
  c)      pkgs="gcc gnumake cmake ninja gdb pkg-config clang-tools"; hook='' ;;
  *) usage ;;
esac
mkdir -p "$dir"
cd "$dir"
if [ -e flake.nix ]; then echo "There is a flake.nix here already; nothing changed." >&2; exit 1; fi
list=""
for p in $pkgs; do list="$list pkgs.$p"; done
cat > flake.nix <<NIX
{
  description = "A $lang project (made with nexus-dev)";
  # The nixpkgs this computer is built with (nix registry): no download for
  # tools that are already on it.
  inputs.nixpkgs.url = "nixpkgs";
  outputs = { self, nixpkgs }:
    let pkgs = nixpkgs.legacyPackages.x86_64-linux; in {
      devShells.x86_64-linux.default = pkgs.mkShell {
        packages = [$list ];
        shellHook = ''
          $hook
        '';
      };
    };
}
NIX
[ -e .envrc ] || echo "use flake" > .envrc
if [ -d .git ] || git rev-parse --git-dir > /dev/null 2>&1; then git add flake.nix .envrc 2>/dev/null || true; fi
direnv allow . 2>/dev/null || true
echo "Ready: the $lang tools are here whenever you are in $(pwd). Add more to flake.nix (packages = [ ... ]). The first time may take a moment."
