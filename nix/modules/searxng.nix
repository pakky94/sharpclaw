{ config, lib, pkgs, ... }:
{
  config = {
    services.searx = {
      enable = true;
      settings = {
        server = {
          secret_key = "@SEARX_SECRET@"; # Override on VM
          bind_address = "127.0.0.1";
          port = 8888;
        };
        search = {
          safe_search = 0;
          autocomplete = "";
        };
        engines = lib.mapAttrsToList (name: value: value) {
          duckduckgo = {
            name = "duckduckgo";
            engine = "duckduckgo";
            shortcut = "ddg";
            disabled = false;
          };
          google = {
            name = "google";
            engine = "google";
            shortcut = "go";
            disabled = false;
          };
          wikipedia = {
            name = "wikipedia";
            engine = "wikipedia";
            shortcut = "wp";
            disabled = false;
          };
          brave = {
            name = "brave";
            engine = "brave";
            shortcut = "br";
            disabled = false;
          };
        };
      };
    };
  };
}
