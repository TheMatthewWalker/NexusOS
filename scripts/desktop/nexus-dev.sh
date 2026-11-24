# nexus-dev init LANGUAGE [FOLDER]: set a project up with its tools, the Nix
# way: a flake.nix with a development shell and an .envrc, so the tools are
# there whenever you are in the folder (direnv) and nowhere else, at the
# versions this NexusOS was built with (no download when they are already here).
#   python node rust go java dotnet c cloud
# nexus-dev db postgres|mysql|redis [stop]: a database for development, in a
# container (Podman) that only this computer can reach (127.0.0.1), with its
# data kept in a named volume between starts. The password is "dev".
set -eu
usage() { echo "nexus-dev init python|node|rust|go|java|dotnet|c|cloud [folder]" >&2
          echo "nexus-dev db postgres|mysql|redis [stop]" >&2; exit 2; }

if [ "${1:-}" = db ]; then
  kind="${2:-}"
  case "$kind" in
    postgres) image=docker.io/library/postgres:17; port=5432; data=/var/lib/postgresql/data; env="POSTGRES_PASSWORD=dev"
              url="postgresql://postgres:dev@127.0.0.1:5432/postgres" ;;
    mysql)    image=docker.io/library/mysql:8.4; port=3306; data=/var/lib/mysql; env="MYSQL_ROOT_PASSWORD=dev"
              url="mysql://root:dev@127.0.0.1:3306" ;;
    redis)    image=docker.io/library/redis:7; port=6379; data=/data; env=""
              url="redis://127.0.0.1:6379" ;;
    *) usage ;;
  esac
  name="nexus-$kind"
  if [ "${3:-}" = stop ]; then podman stop "$name" >/dev/null && echo "$kind stopped; its data stays (volume $name)."; exit 0; fi
  [ -z "${3:-}" ] || usage
  if podman container exists "$name"; then
    podman start "$name" >/dev/null
  else
    set -- run -d --name "$name" -p "127.0.0.1:$port:$port" -v "$name:$data"
    [ -z "$env" ] || set -- "$@" -e "$env"
    podman "$@" "$image" >/dev/null
  fi
  echo "$kind is running for this computer only: $url"
  echo "Stop it with: nexus-dev db $kind stop"
  exit 0
fi
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
  cloud)  pkgs="kubectl kubernetes-helm k9s awscli2 azure-cli google-cloud-sdk opentofu"; hook='' ;;
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
