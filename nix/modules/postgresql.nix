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
      #extensions = ps: with ps; [ pg_trgm pgcrypto vector ];
      extensions = ps: with ps; [ pgcrypto pgtrgm pgvector ];
      authentication = ''
        local all all trust
        host all all 127.0.0.1/32 trust
        host all all ::1/128 trust
      '';
    };
  };
}
