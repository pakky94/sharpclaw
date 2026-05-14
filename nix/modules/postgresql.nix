{ config, lib, pkgs, ... }:
{
  config = {
    services.postgresql = {
      enable = true;
      ensureDatabases = [ "sharpclaw" ];
      ensureUsers = [
        {
          name = "sharpclaw";
          ensureDBOwnership = true;
        }
      ];
      extensions = [
        pg_trgm
        pgcrypto
        vector
      ];
      authentication = ''
        local all all trust
        host all all 127.0.0.1/32 trust
        host all all ::1/128 trust
      '';
    };
  };
}
