{ self, system }:
let
  pkgs = self.inputs.nixpkgs.legacyPackages.${system};
in
{
  sharpclaw-api = pkgs.buildDotnetModule {
    pname = "sharpclaw-api";
    version = "0.1.0";

    src = ../SharpClaw;

    projectFile = "SharpClaw.API/SharpClaw.API.csproj";
    nugetDeps = ./deps.nix; # Generate with: nix run nixpkgs#nuget-to-nix -- deps.nix

    dotnet-sdk = pkgs.dotnet-sdk_10;
    dotnet-runtime = pkgs.dotnetCorePackages.aspnetcore_10_0-bin;

    executables = [ "SharpClaw.API" ];

    meta = with pkgs.lib; {
      description = "SharpClaw AI agent orchestration API";
      license = licenses.mit;
      platforms = platforms.linux;
    };
  };
}
