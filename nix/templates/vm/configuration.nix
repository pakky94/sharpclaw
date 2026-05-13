# SharpClaw VM Configuration
#
# This is YOUR configuration. Customize everything below.
# The SharpClaw service and its dependencies (PostgreSQL, SearXNG,
# Ollama, Docker) are imported from the SharpClaw flake — you don't
# need to copy any .nix files.
#
# After editing, apply with:
#   nixos-rebuild switch --flake .#sharpclaw

{ config, pkgs, ... }:

{
  # ── SharpClaw service ──────────────────────────────────────
  services.sharpclaw = {
    enable = true;
    port = 5000;
    llmModel = "llama3.2";          # Model to pull from Ollama
    webSearchProvider = "Searxng";  # or "Brave"
    openFirewall = true;

    # Uncomment when you have secrets configured:
    # githubTokenFile = "/run/secrets/github-token";
    # discordTokenFile = "/run/secrets/discord-token";
    # braveApiKeyFile = "/run/secrets/brave-api-key";
  };

  # ── Ollama acceleration ────────────────────────────────────
  # Pick the GPU backend: "cuda" (NVIDIA), "rocm" (AMD), "vulkan", or "cpu"
  services.sharpclaw-ollama.acceleration = "cuda";

  # ── Filesystems ────────────────────────────────────────────
  # You MUST configure your root filesystem. Example:
  # fileSystems."/" = {
  #   device = "/dev/disk/by-uuid/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx";
  #   fsType = "ext4";
  # };
  #
  # If you have a boot partition:
  # fileSystems."/boot" = {
  #   device = "/dev/disk/by-uuid/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx";
  #   fsType = "vfat";
  # };

  # ── System packages ────────────────────────────────────────
  environment.systemPackages = with pkgs; [
    git
    gh
    htop
    jq
    curl
    tmux
  ];

  # ── Boot ───────────────────────────────────────────────────
  boot.loader.systemd-boot.enable = true;
  boot.loader.efi.canTouchEfiVariables = true;

  # ── Network ────────────────────────────────────────────────
  networking.hostName = "sharpclaw";
  networking.networkmanager.enable = true;

  # ── Time ───────────────────────────────────────────────────
  time.timeZone = "Europe/Rome";

  # ── SSH ────────────────────────────────────────────────────
  services.openssh = {
    enable = true;
    settings.PasswordAuthentication = false;
  };

  users.users.root.openssh.authorizedKeys.keys = [
    # Add your SSH public key here, e.g.:
    # "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI..."
  ];

  # ── State version ──────────────────────────────────────────
  system.stateVersion = "25.11";
}
