{ config, lib, pkgs, ... }:
{
  config = {
    services.ollama = {
      enable = true;
      acceleration = "cuda"; # Change to "rocm" for AMD, or false for CPU-only
    };
  };
}
