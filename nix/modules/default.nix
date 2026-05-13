# Aggregates all SharpClaw NixOS modules.
# Use as: imports = [ ./modules ];  — imports everything
# Or pick individual modules: imports = [ ./modules/sharpclaw.nix ./modules/postgresql.nix ];

{ ... }:
{
  imports = [
    ./sharpclaw.nix
    ./postgresql.nix
    ./searxng.nix
    ./ollama.nix
    ./docker.nix
  ];
}
