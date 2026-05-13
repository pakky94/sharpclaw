{
  description = "SharpClaw — AI agent orchestration system";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    flake-utils.url = "github:numtide/flake-utils";
  };

  outputs = { self, nixpkgs, flake-utils, ... }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" ];
    in
    {
      # ── NixOS modules ──────────────────────────────────────
      nixosModules = {
        sharpclaw  = import ./nix/modules/sharpclaw.nix;
        postgresql = import ./nix/modules/postgresql.nix;
        searxng    = import ./nix/modules/searxng.nix;
        ollama     = import ./nix/modules/ollama.nix;
        docker     = import ./nix/modules/docker.nix;
        default    = import ./nix/modules;
      };

      # ── Overlay (puts sharpclaw-api in pkgs) ───────────────
      overlays.default = final: prev: {
        sharpclaw-api = self.packages.${final.system}.sharpclaw-api or
          (throw "sharpclaw-api is not available for ${final.system}");
      };

      # ── Reference VM configuration ─────────────────────────
      nixosConfigurations.sharpclaw-vm = nixpkgs.lib.nixosSystem {
        system = "x86_64-linux";
        modules = [
          { nixpkgs.overlays = [ self.overlays.default ]; }
          ./nix/vm-config.nix
        ];
      };

      # ── Templates ──────────────────────────────────────────
      templates.vm = {
        path = ./nix/templates/vm;
        description = "SharpClaw VM — starter flake for deploying on NixOS";
      };

    } // flake-utils.lib.eachSystem systems (system:
      let
        pkgs = nixpkgs.legacyPackages.${system};
      in
      {
        # ── Dev shell ────────────────────────────────────────
        devShells.default = import ./nix/devshell.nix {
          inherit pkgs;
        };

        # ── Packages ─────────────────────────────────────────
        packages = import ./nix/packages.nix {
          inherit self system;
        };
      });
}
