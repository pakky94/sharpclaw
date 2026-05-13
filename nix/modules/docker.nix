{ config, lib, pkgs, ... }:
{
  config = {
    virtualisation.docker = {
      enable = true;
      autoPrune.enable = true;
    };
  };
}
