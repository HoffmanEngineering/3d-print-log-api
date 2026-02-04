#!/usr/bin/env pwsh
# Generate code coverage report locally

param(
    [switch]$Open  # Open report in browser after generation
)

$ErrorActionPreference = "Stop"

Write-Host "Running tests with coverage..." -ForegroundColor Cyan

# Clean previous results
if (Test-Path "TestResults") {
    Remove-Item -Recurse -Force "TestResults"
}

# Run tests with coverage collection
dotnet test --collect:"XPlat Code Coverage" --results-directory TestResults

# Find the coverage file
$coverageFile = Get-ChildItem -Path "TestResults" -Filter "coverage.cobertura.xml" -Recurse | Select-Object -First 1

if (-not $coverageFile) {
    Write-Host "No coverage file found!" -ForegroundColor Red
    exit 1
}

Write-Host "Generating HTML report..." -ForegroundColor Cyan

# Restore tools if needed
dotnet tool restore

# Generate HTML report
dotnet reportgenerator `
    -reports:$($coverageFile.FullName) `
    -targetdir:TestResults/CoverageReport `
    -reporttypes:Html

Write-Host "Coverage report generated at: TestResults/CoverageReport/index.html" -ForegroundColor Green

if ($Open) {
    Start-Process "TestResults/CoverageReport/index.html"
}
