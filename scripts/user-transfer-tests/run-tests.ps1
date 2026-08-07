<#
.SYNOPSIS
    End-to-end test for scripts/TransferUserData.sql against a real SQL Server.

.DESCRIPTION
    Builds the production schema from the EF migrations into a throwaway database,
    seeds two accounts plus an untouched bystander, runs the transfer script (dry
    run first, then committed), and asserts the outcome.

    Also exercises the guard paths -- same-user, missing user, schema drift,
    subscription collision and duplicate settings -- each of which must abort and
    roll back.

    Requires the sqlserver container from docker-compose.yml to be running:
        docker compose up -d sqlserver

.EXAMPLE
    ./run-tests.ps1

.EXAMPLE
    ./run-tests.ps1 -SkipMigrations   # reuse an already-built test database
#>
[CmdletBinding()]
param(
    [string] $Container  = '3d-print-log-api-sqlserver-1',
    [string] $SaPassword = 'YourStrong@Passw0rd',
    [string] $Database   = 'PrintLogTransferTest',
    [int]    $HostPort   = 1434,
    [switch] $SkipMigrations
)

$ErrorActionPreference = 'Stop'

$here       = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot   = Resolve-Path (Join-Path $here '..\..')
$transfer   = Join-Path $here '..\TransferUserData.sql'
$sqlcmd     = '/opt/mssql-tools18/bin/sqlcmd'
$work       = Join-Path ([System.IO.Path]::GetTempPath()) "printlog-transfer-tests"

if (-not (Test-Path $transfer)) { throw "Cannot find TransferUserData.sql at $transfer" }
New-Item -ItemType Directory -Force -Path $work | Out-Null

$script:failures = 0

function Write-Step([string] $Message) { Write-Host "`n=== $Message ===" -ForegroundColor Cyan }

function Invoke-Sql {
    param([string] $File, [string] $Query, [switch] $NoDatabase)

    $args = @('exec', $Container, $sqlcmd, '-S', 'localhost', '-U', 'sa', '-P', $SaPassword, '-C', '-W')
    if (-not $NoDatabase) { $args += @('-d', $Database) }

    if ($File) {
        $name = [System.IO.Path]::GetFileName($File)
        docker cp $File "${Container}:/tmp/$name" | Out-Null
        $args += @('-i', "/tmp/$name")
    }
    else {
        $args += @('-Q', $Query)
    }

    # stderr is folded into stdout by the tool wrapper; -W trims padding.
    & docker @args
}

# Materialise the transfer script with the parameter block rewritten. The
# defaults live in the script itself, so this only overrides what a run needs.
function New-TransferScript {
    param(
        [string] $Name,
        [long]   $SourceId,
        [long]   $TargetId,
        [int]    $DryRun = 1,
        [hashtable] $Toggles = @{}
    )

    $sql = Get-Content $transfer -Raw
    $sql = $sql -replace '(?m)^(DECLARE @SourceUserId\s+bigint = )0;', "`${1}$SourceId;"
    $sql = $sql -replace '(?m)^(DECLARE @TargetUserId\s+bigint = )0;', "`${1}$TargetId;"
    $sql = $sql -replace '(?m)^(DECLARE @DryRun\s+bit\s+= )1;',        "`${1}$DryRun;"

    foreach ($k in $Toggles.Keys) {
        $sql = $sql -replace "(?m)^(DECLARE @$k\s+bit\s*=\s*)[01];", "`${1}$($Toggles[$k]);"
    }

    if ($sql -match '(?m)^DECLARE @SourceUserId\s+bigint = 0;') {
        throw 'Failed to substitute @SourceUserId - the parameter block format changed.'
    }

    $path = Join-Path $work "$Name.sql"
    "USE [$Database];`nGO`n" + $sql | Set-Content $path -Encoding UTF8
    return $path
}

function Assert-Contains {
    param([string[]] $Output, [string] $Pattern, [string] $What)

    if ($Output -match $Pattern) {
        Write-Host "  PASS  $What" -ForegroundColor Green
    }
    else {
        Write-Host "  FAIL  $What (expected /$Pattern/)" -ForegroundColor Red
        $script:failures++
    }
}

# --- 1. schema ---------------------------------------------------------------
if (-not $SkipMigrations) {
    Write-Step "Building schema from EF migrations into [$Database]"
    $env:ConnectionString__PrintLogDb =
        "Server=localhost,$HostPort;Database=$Database;User Id=sa;Password=$SaPassword;TrustServerCertificate=True;Encrypt=False"
    Push-Location $repoRoot
    try {
        dotnet ef database update --project PrintLogApi | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "dotnet ef database update failed ($LASTEXITCODE)" }
    }
    finally { Pop-Location }
    Write-Host '  schema ready' -ForegroundColor Green
}

# --- 2. seed -----------------------------------------------------------------
Write-Step 'Seeding source, target and bystander accounts'
Invoke-Sql -File (Join-Path $here 'seed.sql') | Out-Null

$ids = Invoke-Sql -Query @"
SET NOCOUNT ON;
SELECT CAST((SELECT Id FROM dbo.Users WHERE OAuthUserId='auth0|old') AS varchar(20)) + ',' +
       CAST((SELECT Id FROM dbo.Users WHERE OAuthUserId='auth0|new') AS varchar(20)) + ',' +
       CAST((SELECT Id FROM dbo.Users WHERE OAuthUserId='auth0|by')  AS varchar(20));
"@
$line = ($ids | Where-Object { $_ -match '^\d+,\d+,\d+$' } | Select-Object -First 1)
if (-not $line) { throw "Could not read seeded user ids. sqlcmd said:`n$($ids -join "`n")" }
$src, $tgt, $oth = $line.Split(',')
Write-Host "  source=$src target=$tgt bystander=$oth" -ForegroundColor Green

# --- 3. dry run --------------------------------------------------------------
Write-Step 'Dry run (must roll back)'
$out = Invoke-Sql -File (New-TransferScript -Name 'dry' -SourceId $src -TargetId $tgt -DryRun 1)
Assert-Contains $out 'DRY RUN' 'dry run rolled back'

$still = Invoke-Sql -Query "SET NOCOUNT ON; SELECT CAST(COUNT(*) AS varchar(10)) FROM dbo.Prints WHERE CreatedById = $src;"
Assert-Contains $still '^1$' 'dry run left the source data untouched'

# --- 4. committed run --------------------------------------------------------
Write-Step 'Committed run'
$out = Invoke-Sql -File (New-TransferScript -Name 'commit' -SourceId $src -TargetId $tgt -DryRun 0)
Assert-Contains $out 'Transfer committed' 'transfer committed'

Write-Step 'Assertions'
$out = Invoke-Sql -File (Join-Path $here 'assert.sql')
$out | Where-Object { $_ -match 'PASS|FAIL|Verdict|CHECKS' } | ForEach-Object { Write-Host "  $_" }
Assert-Contains $out 'ALL CHECKS PASSED' 'all post-transfer assertions passed'

# --- 5. idempotency ----------------------------------------------------------
Write-Step 'Re-run must be a no-op'
$out = Invoke-Sql -File (New-TransferScript -Name 'commit2' -SourceId $src -TargetId $tgt -DryRun 0)
Assert-Contains $out 'Transfer committed' 're-run succeeds'
if ($out -match '(?m)^moved\s+0\s*$') {
    Write-Host '  PASS  re-run moved 0 rows' -ForegroundColor Green
}
else {
    Write-Host '  FAIL  re-run moved a non-zero number of rows' -ForegroundColor Red
    $script:failures++
}

# --- 6. guard paths ----------------------------------------------------------
Write-Step 'Guards must abort and roll back'

$out = Invoke-Sql -File (New-TransferScript -Name 'g_same' -SourceId $tgt -TargetId $tgt -DryRun 0)
Assert-Contains $out 'Msg 50002' 'same source and target rejected'

$out = Invoke-Sql -File (New-TransferScript -Name 'g_missing' -SourceId $tgt -TargetId 999999 -DryRun 0)
Assert-Contains $out 'Msg 50004' 'nonexistent target rejected'

Invoke-Sql -Query @"
INSERT INTO dbo.Subscriptions (CancelAtPeriodEnd,CreatedById,CreatedDate,[Plan],Status,StripeCustomerId,StripeSubscriptionId,UpdatedById,UpdatedDate,UserId)
SELECT 0,$oth,SYSUTCDATETIME(),1,1,'cus_by','sub_by',$oth,SYSUTCDATETIME(),$oth
WHERE NOT EXISTS (SELECT 1 FROM dbo.Subscriptions WHERE UserId=$oth);
INSERT INTO dbo.Subscriptions (CancelAtPeriodEnd,CreatedById,CreatedDate,[Plan],Status,StripeCustomerId,StripeSubscriptionId,UpdatedById,UpdatedDate,UserId)
SELECT 0,$tgt,SYSUTCDATETIME(),1,1,'cus_new','sub_new',$tgt,SYSUTCDATETIME(),$tgt
WHERE NOT EXISTS (SELECT 1 FROM dbo.Subscriptions WHERE UserId=$tgt);
"@ | Out-Null
$out = Invoke-Sql -File (New-TransferScript -Name 'g_sub' -SourceId $oth -TargetId $tgt -DryRun 0 -Toggles @{ TransferSubscription = 1 })
Assert-Contains $out 'Msg 50007' 'subscription collision rejected'

Invoke-Sql -Query @"
INSERT INTO dbo.UserSettings (CreatedById,CreatedDate,UpdatedById,UpdatedDate,UserId,UserSettingTypeId,Value)
VALUES ($tgt,SYSUTCDATETIME(),$tgt,SYSUTCDATETIME(),$tgt,5,'DUPE');
"@ | Out-Null
$out = Invoke-Sql -File (New-TransferScript -Name 'g_dupe' -SourceId $oth -TargetId $tgt -DryRun 0)
Assert-Contains $out 'Msg 50009' 'pre-existing duplicate settings rejected'
Invoke-Sql -Query "DELETE FROM dbo.UserSettings WHERE Value='DUPE';" | Out-Null

Invoke-Sql -Query @"
CREATE TABLE dbo.ZzDriftProbe (Id int IDENTITY PRIMARY KEY,
    OwnerUserId bigint NOT NULL CONSTRAINT FK_ZzDriftProbe_Users FOREIGN KEY REFERENCES dbo.Users(Id));
"@ | Out-Null
$out = Invoke-Sql -File (New-TransferScript -Name 'g_drift' -SourceId $oth -TargetId $tgt -DryRun 0)
Assert-Contains $out 'Msg 50006'                  'unhandled FK to Users rejected'
Assert-Contains $out 'ZzDriftProbe.OwnerUserId'   'drift guard names the offending column'
Invoke-Sql -Query 'DROP TABLE dbo.ZzDriftProbe;' | Out-Null

# --- verdict -----------------------------------------------------------------
Write-Host ''
if ($script:failures -eq 0) {
    Write-Host '=== ALL TESTS PASSED ===' -ForegroundColor Green
    exit 0
}
Write-Host "*** $($script:failures) TEST(S) FAILED ***" -ForegroundColor Red
exit 1
