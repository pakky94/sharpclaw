# SharpClaw

Run the backend (`SharpClaw.API`) and frontend (`sharpclaw-web`) in one container, with PostgreSQL + pgvector in a second container.

## Requirements

- Docker Desktop (or Docker Engine with Compose)
- Or Podman 5+ (with a Compose provider: `podman-compose` or `docker-compose`)

## Build and Run (Docker)

From the repository root:

```powershell
docker compose up --build
```

Services:

- App: `http://localhost:5846`
- Postgres (pgvector): `localhost:5532` if enabled, internal only on Compose network (`db:5432`) by default

Database defaults (from `docker-compose.yml`):

- Database: `sharpclaw`
- User: `sharpclaw`
- Password: `sharpclaw`

Stop:

```powershell
docker compose down
```

Stop and remove DB volume too:

```powershell
docker compose down -v
```

## Build and Run (Podman)

If you have a Compose provider installed:

```powershell
podman compose up --build
```

If `podman compose` fails with a provider error, install one of:

- `podman-compose`
- `docker-compose`

Then run the same command again.

## Notes

The image is built via a multi-stage root `Dockerfile`:

1. Build `sharpclaw-web` with Node
2. Publish `SharpClaw.API` with .NET
3. Copy web `dist` into API `wwwroot`

The backend serves both API routes and frontend static files from the same container.
