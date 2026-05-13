{ config, lib, pkgs, ... }:
let
  cfg = config.services.sharpclaw-ollama;
in
{
  options.services.sharpclaw-ollama = {
    acceleration = lib.mkOption {
      type = lib.types.enum [ "cuda" "rocm" "vulkan" "cpu" ];
      default = "cuda";
      description = "GPU acceleration backend for Ollama";
    };
  };

  config = {
    services.ollama = {
      enable = true;
      package = {
        cuda   = pkgs.ollama-cuda;
        rocm   = pkgs.ollama-rocm;
        vulkan = pkgs.ollama-vulkan;
        cpu    = pkgs.ollama;
      }.${cfg.acceleration};
    };
  };
}
