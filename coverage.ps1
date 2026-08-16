#!/usr/bin/env pwsh
# Generate code coverage report locally

param(
    [switch]$Open  # Open report in browser after generation
)

$ErrorActionPreference = "Stop"

Write-Host "Running tests with coverage..." -ForegroundColor Cyan

$resultsDir = Join-Path $PSScriptRoot "TestResults"

# Clean previous results
if (Test-Path $resultsDir) {
    Remove-Item -Recurse -Force $resultsDir
}

# Run tests with coverage collection.
#
# Under Microsoft.Testing.Platform (#70) this is not `--collect "XPlat Code Coverage"` any more.
# That was a VSTest datacollector name, and coverlet.collector, which implemented it, is no longer
# referenced. Coverage is now an extension of the test executable itself
# (Microsoft.Testing.Extensions.CodeCoverage), so the options go AFTER `--`, which is what forwards
# them to that executable rather than to the `dotnet test` CLI.
#
# Cobertura is requested explicitly. The extension's native format is .coverage (binary), which
# reportgenerator below cannot read; the default would leave this script failing at the "no
# coverage file found" check rather than producing a wrong report, but asking for the format the
# next step needs is clearer than relying on that.
#
# $resultsDir is absolute on purpose. `--results-directory` is resolved by the test executable, so
# a relative path lands under the test PROJECT directory, not the repo root this script runs from.
dotnet test -- `
    --coverage `
    --coverage-output-format cobertura `
    --coverage-output coverage.cobertura.xml `
    --results-directory $resultsDir

# Recursive rather than a direct path: multi-targeting or a second test project would each write
# their own file below $resultsDir.
$coverageFile = Get-ChildItem -Path $resultsDir -Filter "coverage.cobertura.xml" -Recurse | Select-Object -First 1

if (-not $coverageFile) {
    Write-Host "No coverage file found!" -ForegroundColor Red
    exit 1
}

Write-Host "Generating HTML report..." -ForegroundColor Cyan

# Restore tools if needed
dotnet tool restore

# Generate HTML report (excluding Migrations folder)
$reportDir = Join-Path $resultsDir "CoverageReport"

dotnet reportgenerator `
    -reports:$($coverageFile.FullName) `
    -targetdir:$reportDir `
    -reporttypes:Html `
    -filefilters:-**/Migrations/*

$indexPath = Join-Path $reportDir "index.html"

Write-Host "Coverage report generated at: $indexPath" -ForegroundColor Green

if ($Open) {
    Start-Process $indexPath
}
