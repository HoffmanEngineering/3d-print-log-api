# Contributing to 3D Print Log API

Thank you for your interest in contributing!

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download)
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

## Submitting a PR

- Fork the repo and create a branch from `master`
- Make your changes with tests
- Open a pull request — CI runs automatically

## Stopping the Local Environment

```bash
docker compose down
```

To wipe all local data and start fresh:

```bash
docker compose down -v
```
