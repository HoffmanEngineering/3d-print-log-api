# Contributing to 3D Print Log API

Thank you for your interest in contributing!

Most feature work spans both the API and the frontend — the UI repo is at [HoffmanEngineering/3d-print-log-ui](https://github.com/HoffmanEngineering/3d-print-log-ui).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)

## Local Setup

1. **Clone the repo**
   ```bash
   git clone https://github.com/HoffmanEngineering/3d-print-log-api.git
   cd 3d-print-log-api
   ```

2. **Start the local database and blob storage**

   ```bash
   docker compose up -d
   ```

   This starts SQL Server (port 1433) and Azurite blob storage (port 10000) with persistent volumes.

3. **Configure local settings**

   ```bash
   cp PrintLogApi/appsettings.Development.example.json PrintLogApi/appsettings.Development.json
   ```

   The default values work out of the box with Docker Compose. You do **not** need an Auth0 account for local development.

4. **Run database migrations**

   ```bash
   cd PrintLogApi
   dotnet ef database update
   ```

5. **Start the API**
   ```bash
   dotnet run
   ```
   The API will be available at `https://localhost:5001`. Swagger UI is at `https://localhost:5001/swagger`.

## Dev Auth Bypass

In `Development` mode the API accepts an `X-Dev-User-Id` header instead of a Bearer token. Sending `X-Dev-User-Id: 1` authenticates you as dev user 1 (created automatically on first use). Use different IDs to simulate multiple users.

No Auth0 account is required for local development. If you need to test the real Auth0 login flow, set `ASPNETCORE_ENVIRONMENT=Staging` and fill in the Auth0 values in `appsettings.Development.json`.

## Running Tests

```bash
dotnet test
```

The test suite is integration tests backed by an in-memory SQLite database — no Docker or external services required. To run a specific project:

```bash
dotnet test PrintLogApi.IntegrationTests
```

## Troubleshooting

### Port 1433 already in use (SQL Server conflict)

If you have a local SQL Server instance installed, it may already be listening on port 1433 and conflict with the Docker container. Symptoms: `dotnet ef database update` or `dotnet run` fail with a login error even though the container is running.

Fix: create a `docker-compose.override.yml` in the repo root to remap the container to a different host port, and update the connection string to match.

**docker-compose.override.yml**
```yaml
services:
  sqlserver:
    ports:
      - '1434:1433'
```

**PrintLogApi/appsettings.Development.json** — change the port in the connection string:
```json
"PrintLogDb": "Server=localhost,1434;Database=PrintLogDb;..."
```

Then restart the containers (`docker compose down && docker compose up -d`) and re-run migrations.

### Port 5001 already in use (API conflict)

If `dotnet run` fails with `Failed to bind to address https://127.0.0.1:5001: address already in use`, a previous API instance is likely still running. Find and stop it:

```bash
# Windows
netstat -ano | findstr ":5001"
# Then kill the listed PID:
taskkill /PID <pid> /F

# macOS/Linux
lsof -ti :5001 | xargs kill
```

## Submitting a PR

- Fork the repo and create a branch from `main`
- Make your changes with tests
- Open a pull request — CI runs automatically
- If your change requires infrastructure updates (new environment variables, Azure configuration, storage changes, etc.), call this out in the PR description so it can be coordinated before the code ships

## Stopping the Local Environment

```bash
docker compose down
```

To wipe all local data and start fresh:

```bash
docker compose down -v
```
