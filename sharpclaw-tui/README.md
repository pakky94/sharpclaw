# sharpclaw-tui

Terminal UI client for SharpClaw, implemented in Go.

## Features

- Chat-oriented TUI for agents/sessions/messages, inspired by CLI agent workflows.
- Agent management: list, create, edit model + temperature.
- Workspace management: list, create, assign/unassign, delete.
- Pending approvals handling from chat sessions.
- Docker Compose management in both:
  - TUI (`Compose` tab)
  - CLI subcommands (`compose start|stop|status`)
- Persistent config in `~/.sharpclaw/config.json`.

## Build

```bash
go build ./cmd/sharpclaw-tui
```

## Run

```bash
./sharpclaw-tui
```

## Compose via CLI

```bash
./sharpclaw-tui compose start --build
./sharpclaw-tui compose stop
./sharpclaw-tui compose stop --volumes
./sharpclaw-tui compose status
```

## Config

```bash
./sharpclaw-tui config show
./sharpclaw-tui config set --api-url http://localhost:5846 --compose-command docker --compose-file docker-compose.yml
```

Default config:

```json
{
  "api_base_url": "http://localhost:5846",
  "compose_command": "docker",
  "compose_file": "docker-compose.yml"
}
```
