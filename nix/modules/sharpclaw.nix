{ config, lib, pkgs, ... }:
let
  cfg = config.services.sharpclaw;
in
{
  options.services.sharpclaw = {
    enable = lib.mkEnableOption "SharpClaw agent orchestration service";

    package = lib.mkOption {
      type = lib.types.package;
      default = pkgs.sharpclaw-api;
      defaultText = lib.literalExpression "pkgs.sharpclaw-api";
      description = "The SharpClaw API package to use";
    };

    port = lib.mkOption {
      type = lib.types.port;
      default = 5000;
      description = "Port for the SharpClaw API to listen on";
    };

    connectionString = lib.mkOption {
      type = lib.types.str;
      default = "Host=localhost;Database=sharpclaw;Username=sharpclaw;Password=sharpclaw";
      description = "PostgreSQL connection string";
    };

    llmEndpoint = lib.mkOption {
      type = lib.types.str;
      default = "http://localhost:11434/v1";
      description = "Ollama OpenAI-compatible API endpoint";
    };

    llmModel = lib.mkOption {
      type = lib.types.str;
      default = "llama3.2";
      description = "Default LLM model name";
    };

    webSearchProvider = lib.mkOption {
      type = lib.types.enum [ "Searxng" "Brave" ];
      default = "Searxng";
      description = "Web search provider";
    };

    searxngUrl = lib.mkOption {
      type = lib.types.str;
      default = "http://localhost:8888";
      description = "SearXNG instance URL";
    };

    braveApiKeyFile = lib.mkOption {
      type = lib.types.nullOr lib.types.path;
      default = null;
      description = "Path to file containing Brave Search API key";
    };

    githubTokenFile = lib.mkOption {
      type = lib.types.nullOr lib.types.path;
      default = null;
      description = "Path to file containing GitHub personal access token";
    };

    discordTokenFile = lib.mkOption {
      type = lib.types.nullOr lib.types.path;
      default = null;
      description = "Path to file containing Discord bot token";
    };

    dataDir = lib.mkOption {
      type = lib.types.path;
      default = "/var/lib/sharpclaw";
      description = "Data directory for SharpClaw";
    };

    openFirewall = lib.mkOption {
      type = lib.types.bool;
      default = false;
      description = "Open firewall port for SharpClaw API";
    };
  };

  config = lib.mkIf cfg.enable {
    systemd.services.sharpclaw-api = {
      description = "SharpClaw API";
      after = [ "network.target" "postgresql.service" "searxng.service" "ollama.service" ];
      wantedBy = [ "multi-user.target" ];

      serviceConfig = {
        ExecStart = "${cfg.package}/bin/SharpClaw.API";
        Restart = "always";
        RestartSec = 10;
        User = "sharpclaw";
        Group = "sharpclaw";
        StateDirectory = "sharpclaw";
        WorkingDirectory = cfg.dataDir;
        EnvironmentFile = lib.mkIf (cfg.githubTokenFile != null) cfg.githubTokenFile;
        LoadCredential = lib.optionals (cfg.discordTokenFile != null)
          [ "discord_token:${cfg.discordTokenFile}" ]
          ++ lib.optionals (cfg.braveApiKeyFile != null)
          [ "brave_api_key:${cfg.braveApiKeyFile}" ];
      };

      environment = {
        ASPNETCORE_URLS = "http://*:${toString cfg.port}";
        ASPNETCORE_CONTENTROOT = "${cfg.package}/lib/SharpClaw.API";
        ConnectionStrings__sharpclaw = cfg.connectionString;
        LmStudio__Endpoint = cfg.llmEndpoint;
        LmStudio__Model = cfg.llmModel;
        WebSearch__ActiveProvider = cfg.webSearchProvider;
        WebSearch__Searxng__BaseUrl = cfg.searxngUrl;
        SHARPCLAW_DATA_DIR = cfg.dataDir;
      };
    };

    users.users.sharpclaw = {
      isSystemUser = true;
      group = "sharpclaw";
      home = cfg.dataDir;
      createHome = true;
      extraGroups = [ "docker" ];
    };
    users.groups.sharpclaw = { };

    networking.firewall.allowedTCPPorts =
      lib.mkIf cfg.openFirewall [ cfg.port ];
  };
}
