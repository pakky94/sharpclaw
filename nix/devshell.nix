{ self, system }:
let
  pkgs = self.inputs.nixpkgs.legacyPackages.${system};
in
pkgs.mkShell {
  name = "sharpclaw-dev";
  buildInputs = with pkgs; [
    dotnet-sdk_10
    nodejs_22
    nodePackages.typescript
    git
    gh
    docker
    postgresql
  ];

  shellHook = ''
    echo "🦞 SharpClaw development environment"
    echo "  .NET SDK: $(dotnet --version)"
    echo "  Node.js:  $(node --version)"
    echo ""
    echo "  dotnet build   — build the API"
    echo "  npm run dev    — start the frontend (in sharpclaw-web/)"
  '';

  DOTNET_ROOT = "${pkgs.dotnet-sdk_10}";
  DOTNET_CLI_TELEMETRY_OPTOUT = "1";
}
