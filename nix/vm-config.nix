# SharpClaw VM Configuration
#
# This is the template for the VM's actual configuration.
# Copy this to the VM and customize it, or use it as a reference
# for the agent to edit the live config.
#
# Usage: nixos-rebuild switch --flake .#sharpclaw-vm

{ config, lib, pkgs, ... }:
{
  imports = [
    # All SharpClaw service modules
    ./modules
  ];

  # ── SharpClaw service ──────────────────────────────────────
  services.sharpclaw = {
    enable = true;
    port = 5000;
    llmModel = "llama3.2";
    webSearchProvider = "Searxng";
    openFirewall = true;

    # Uncomment and set paths when secrets are configured:
    # githubTokenFile = "/run/secrets/github-token";
    # discordTokenFile = "/run/secrets/discord-token";
    # braveApiKeyFile = "/run/secrets/brave-api-key";
  };

  # ── System packages ────────────────────────────────────────
  environment.systemPackages = with pkgs; [
    git
    gh
    dotnet-sdk_10
    nodejs_22
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
    # Add your SSH public key here
  ];

  # ── State version ──────────────────────────────────────────
  system.stateVersion = "25.11";
}
