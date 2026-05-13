# SharpClaw Nix Flake

Nix flake for deploying SharpClaw on NixOS, plus a dev shell for working on SharpClaw itself.

## Structure

```
flake.nix                  — Thin entry point
nix/
├── modules/               — NixOS modules (one per service)
│   ├── default.nix        — Aggregates all modules
│   ├── sharpclaw.nix      — SharpClaw API service
│   ├── postgresql.nix     — PostgreSQL database
│   ├── searxng.nix        — SearXNG private search
│   ├── ollama.nix         — Ollama LLM inference
│   └── docker.nix         — Docker for devcontainers
├── templates/vm/          — Starter template for `nix flake init`
│   ├── flake.nix
│   └── configuration.nix
├── vm-config.nix          — Reference VM config (used by CI)
├── devshell.nix           — Dev shell definition
├── packages.nix           — Package definitions
├── deps.nix               — NuGet dependency hashes (placeholder)
└── README.md              — This file
```

## Usage

### Deploy to a NixOS VM

You don't need to copy any `.nix` files from this repo. Just scaffold a new flake:

```bash
mkdir sharpclaw-vm && cd sharpclaw-vm
nix flake init -t github:pakky94/sharpclaw#vm
```

This creates two files:
- `flake.nix` — imports SharpClaw from GitHub
- `configuration.nix` — your VM configuration

Edit `configuration.nix` (set your SSH key, adjust the LLM model, etc.), then:

```bash
nixos-rebuild switch --flake .#sharpclaw
```

### Dev shell

```bash
nix develop
# Gives you: .NET SDK 10, Node.js 22, TypeScript, git, gh, Docker, PostgreSQL
```

### Use individual modules

If you want to pick and choose modules instead of importing `default`:

```nix
{
  inputs.sharpclaw.url = "github:pakky94/sharpclaw";
  outputs = { nixpkgs, sharpclaw, ... }: {
    nixosConfigurations.my-vm = nixpkgs.lib.nixosSystem {
      system = "x86_64-linux";
      modules = [
        { nixpkgs.overlays = [ sharpclaw.overlays.default ]; }
        sharpclaw.nixosModules.sharpclaw
        sharpclaw.nixosModules.postgresql
        sharpclaw.nixosModules.searxng
        sharpclaw.nixosModules.ollama
        sharpclaw.nixosModules.docker
        ./my-config.nix
      ];
    };
  };
}
```

### Regenerate NuGet deps

```bash
cd SharpClaw/SharpClaw.API && dotnet restore
nix run nixpkgs#nuget-to-nix -- deps.nix > ../../nix/deps.nix
```

## Design

- **Remote-first** — users import the flake from GitHub, no file copying needed
- **Overlay** — `sharpclaw-api` is available as `pkgs.sharpclaw-api` via the overlay
- **One file per service** — easy for the agent to edit safely
- **Thin flake.nix** — just wires things together, no logic
- **Template** — `nix flake init -t` scaffolds a working VM config in seconds
