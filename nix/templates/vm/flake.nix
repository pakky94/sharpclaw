{
  description = "My SharpClaw VM";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    sharpclaw.url = "github:pakky94/sharpclaw";
  };

  outputs = { nixpkgs, sharpclaw, ... }: {
    nixosConfigurations.sharpclaw = nixpkgs.lib.nixosSystem {
      system = "x86_64-linux";
      modules = [
        # Apply the SharpClaw overlay so pkgs.sharpclaw-api is available
        { nixpkgs.overlays = [ sharpclaw.overlays.default ]; }

        # SharpClaw and all its dependencies
        sharpclaw.nixosModules.default

        # Your VM configuration
        ./configuration.nix
      ];
    };
  };
}
