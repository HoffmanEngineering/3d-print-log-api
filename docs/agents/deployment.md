# Deployment

GitHub Actions, and the triggers differ in a way that matters:

- `ci.yml` — every push to `main` and every PR targeting `main`. Build and test only.
- `deploy.yml` — **only on pushing a `v*` tag**. Merging to `main` does not release; tag a commit
  to deploy it.

`deploy.yml` builds, tests, publishes, and produces two artifacts — a `migration-script` for review
and an `efbundle` to apply. The deploy job is gated on the `production` GitHub Environment, so it
waits for a required reviewer before running the bundle against the production database and
deploying to App Service. Review the `migration-script` artifact before approving. A release with
migrations needs the rebuild list in `docs/agents/database.md` ("Before asking for production
approval") first.

The bundle runs with `Command Timeout=900` appended to the connection string, because SqlClient's
30s default is what failed v1.12.0 (an index build on `Prints`). That is a per-statement ceiling,
and for a migration that `ALTER`s a hot table it is also how long the live app can block on that
table's schema lock — so a statement that needs more than that belongs in its own migration, not a
higher number.

To generate a migration script manually (e.g. for emergency patching):

```bash
dotnet ef migrations script <LastAppliedMigrationId> --project PrintLogApi --output migrations.sql --idempotent
```

### The app runs from the deployed zip, never from extracted files (#309)

Production is App Service **Linux** (`DOTNETCORE|10.0`) on plan **B1**: one instance and no
deployment slots, so there is no staging slot to swap. `azure/webapps-deploy@v3` with the publish
profile deploys through Kudu's OneDeploy (`/api/publish?type=zip`). By default OneDeploy *extracts*
the zip over `/home/site/wwwroot` while the old process is still running and loading assemblies from
it. Every deploy from Oct 8 to Nov 7, 2026 produced a burst of `System.BadImageFormatException`
("Bad IL range", "Format of the executable (.exe) or library (.dll) is invalid"); on Oct 17 and 20
that was 139 and 72 HTTP 500s across ordinary endpoints.

The fix is the app setting **`WEBSITE_RUN_FROM_PACKAGE=1`**. With it, OneDeploy writes the zip to
`/home/data/SitePackages`, points `packagename.txt` at it and restarts, and the new process mounts
the zip read-only as `wwwroot`. Nothing ever changes a file under a running process, and a process
only ever sees one complete package. ([Run your app from a ZIP package](https://learn.microsoft.com/azure/app-service/deploy-run-package);
Kudu on Linux checks the setting in `PushDeploymentController`.) It needs no new credential, and the
workflow does not set it: it is App Service configuration, which only the owner changes.

- **`wwwroot` is read-only.** The app writes nothing there today (data protection keys live under
  `/home/ASP.NET`, uploads go to Blob Storage). Anything new that writes next to the binaries fails
  in production only.
- **A deploy still restarts the app**, so expect a few seconds of 502/503 on B1. Stopping the app
  around the deploy was considered and rejected: it needs an Azure service principal this repo does
  not have, and it turns those seconds into the whole upload.
- **Rollback** is a redeploy of an older tag. Old packages stay in `SitePackages` (Kudu prunes
  them), but switching `packagename.txt` by hand is not a supported path.
- If the setting is ever removed, deploys silently go back to extracting over the live process.
  Nothing in the workflow can see app settings with a publish profile, so check it with
  `az webapp config appsettings list` when the deploy-time exceptions come back.

After the deploy step, **"Wait for the API to serve"** polls `https://api.3dprintlog.com/health/ready`
every 10 seconds for up to 5 minutes and passes on three 200s in a row. `publish-release` and
`publish-mcp-registry` need the deploy job, so a release is announced only once the new build is
serving and can reach SQL Server. The check cannot tell the old process from the new one (no
version is exposed); three passes in a row after the restart is the approximation.
