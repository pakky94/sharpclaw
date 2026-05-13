# SharpClaw Nix Flake

Nix flake for deploying SharpClaw on NixOS, plus a dev shell for working on SharpClaw itself.

## Structure

```
flake.nix                  — Thin entry point (~50 lines)
nix/
├── modules/               — NixOS modules (one per service)
│   ├── default.nix        — Aggregates all modules
│   ├── sharpclaw.nix      — SharpClaw API service
│   ├── postgresql.nix     — PostgreSQL database
│   ├── searxng.nix        — SearXNG private search
│   ├── ollama.nix         — Ollama LLM inference
│   └── docker.nix         — Docker for devcontainers
├── vm-config.nix          — VM configuration template
├── devshell.nix           — Dev shell definition
├── packages.nix           — Package definitions
├── deps.nix               — NuGet dependency hashes (placeholder)
└── README.md              — This file
```

## Usage

### Dev shell
```bash
nix develop
# Gives you: .NET SDK 10, Node.js 22, TypeScript, git, gh, Docker, PostgreSQL
```

### Deploy to VM
```bash
# Copy this flake to the VM, customize nix/vm-config.nix, then:
nixos-rebuild switch --flake .#sharpclaw-vm
```

### Use individual modules
```nix
# In your own flake:
{
  inputs.sharpclaw.url = "github:your/sharpclaw";
  outputs = { sharpclaw, ... }: {
    nixosConfigurations.my-vm = nixpkgs.lib.nixosSystem {
      modules = [
        sharpclaw.nixosModules.sharpclaw
        sharpclaw.nixosModules.postgresql
        # ... or just:
        sharpclaw.nixosModules.default
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

- **One file per service** — easy for the agent to edit safely
- **Thin flake.nix** — just wires things together, no logic
- **Template vs. live** — this repo has the template; the VM has the live config
- **Agent edits the VM's flake** directly when new system tools are needed (rare)
