{ self, system }:
let
  pkgs = self.inputs.nixpkgs.legacyPackages.${system};

  sharpclaw-web = pkgs.buildNpmPackage {
    pname = "sharpclaw-web";
    version = "0.1.0";

    src = ../sharpclaw-web;

    # Replace with the hash Nix reports on first build attempt:
    #   nix build .#sharpclaw-web 2>&1 | grep -oP 'sha256-\S+'
    npmDepsHash = "sha256-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    installPhase = ''
      runHook preInstall
      cp -r dist $out
      runHook postInstall
    '';
  };
in
{
  inherit sharpclaw-web;

  sharpclaw-api = pkgs.buildDotnetModule {
    pname = "sharpclaw-api";
    version = "0.1.0";

    src = ../SharpClaw;

    projectFile = "SharpClaw.API/SharpClaw.API.csproj";
    nugetDeps = ./deps.json;

    dotnet-sdk = pkgs.dotnet-sdk_10;
    dotnet-runtime = pkgs.dotnetCorePackages.aspnetcore_10_0-bin;

    executables = [ "SharpClaw.API" ];

    # Bundle the frontend into wwwroot
    postInstall = ''
      mkdir -p $out/lib/SharpClaw.API/wwwroot
      cp -r ${sharpclaw-web}/* $out/lib/SharpClaw.API/wwwroot/
    '';

    meta = with pkgs.lib; {
      description = "SharpClaw AI agent orchestration API";
      license = licenses.mit;
      platforms = platforms.linux;
    };
  };
}
