# Nexus Core daemon and the `nexus` CLI (.NET 8). After changing NuGet
# references, regenerate the lock: nix build .#nexus-core.fetch-deps && ./result nix/packages/nexus-core-deps.json
{ lib, buildDotnetModule, dotnetCorePackages }:
let root = ../..;
in
buildDotnetModule {
  pname = "nexus-core";
  version = "0.3.0";
  src = lib.fileset.toSource {
    inherit root;
    fileset = lib.fileset.unions [ (root + "/src") (root + "/schema") (root + "/global.json") ];
  };
  projectFile = [ "src/NexusOS.Core/NexusOS.Core.csproj" "src/NexusOS.Cli/NexusOS.Cli.csproj" ];
  nugetDeps = ./nexus-core-deps.json;
  dotnet-sdk = dotnetCorePackages.sdk_8_0;
  dotnet-runtime = dotnetCorePackages.aspnetcore_8_0;
  executables = [ "NexusOS.Core" "NexusOS.Cli" ];
  postFixup = ''
    ln -s $out/bin/NexusOS.Core $out/bin/nexus-core
    ln -s $out/bin/NexusOS.Cli $out/bin/nexus
  '';
  meta = {
    description = "NexusOS Core: local-first AI daemon and CLI";
    license = lib.licenses.mit;
    platforms = lib.platforms.linux;
    mainProgram = "nexus";
  };
}
