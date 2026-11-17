<#
.SYNOPSIS
    Exports every Auth0 user's id, email, verified flag and signup date for the email backfill.

.DESCRIPTION
    Starts an Auth0 users-export job, waits for it, and downloads the gzipped NDJSON result.
    Feed the file to: dotnet run --project PrintLogApi.Tools -- email-backfill --file <OutFile> ...
    See docs/email-runbook.md, "Backfilling email addresses".

    The token needs the read:users scope on the Auth0 Management API. The output contains every
    user's email address: keep it out of the repository and delete it after the import.

.EXAMPLE
    ./scripts/auth0-export-users.ps1 -Domain 3dprintlog.us.auth0.com -Token $token -OutFile ~/auth0-users.json.gz
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Domain,
    [Parameter(Mandatory)] [string] $Token,
    [string] $ConnectionId,
    [Parameter(Mandatory)] [string] $OutFile
)

$ErrorActionPreference = 'Stop'
$headers = @{ Authorization = "Bearer $Token" }
$base = "https://$Domain/api/v2"

$body = @{
    format = 'json'
    fields = @(
        @{ name = 'user_id' },
        @{ name = 'email' },
        @{ name = 'email_verified' },
        @{ name = 'created_at' }
    )
}
if ($ConnectionId) {
    $body.connection_id = $ConnectionId
}

$job = Invoke-RestMethod -Method Post -Uri "$base/jobs/users-exports" -Headers $headers `
    -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 4)
Write-Host "Started export job $($job.id)"

do {
    Start-Sleep -Seconds 5
    $job = Invoke-RestMethod -Method Get -Uri "$base/jobs/$($job.id)" -Headers $headers
    Write-Host "  status: $($job.status)"
    if ($job.status -eq 'failed') {
        throw "Auth0 export job $($job.id) failed."
    }
} while ($job.status -ne 'completed')

# The location is a pre-signed URL; it must not receive the Management API token.
Invoke-WebRequest -Uri $job.location -OutFile $OutFile
Write-Host "Saved $OutFile"
