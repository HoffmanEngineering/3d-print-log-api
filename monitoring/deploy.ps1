<#
.SYNOPSIS
    Deploys (or updates in place) the 3D Print Log monitoring workbook.

.DESCRIPTION
    Resolves the two Application Insights resource ids by name and runs the Bicep
    template in main.bicep. Requires an authenticated Azure CLI (`az login`) with access
    to the resource group. Safe to re-run: the workbook has a stable name derived from the
    resource group, so every run updates the same resource.

.EXAMPLE
    ./deploy.ps1
    ./deploy.ps1 -ResourceGroup 3d-print-log-dev -ApiInsightsName api-application-insights -UiInsightsName ui-dev-app-insights
#>
[CmdletBinding()]
param(
    [string] $ResourceGroup = '3d-print-log-production',
    [string] $ApiInsightsName = '3d-print-log-api-insights',
    [string] $UiInsightsName = '3d-print-log-ui-insights',
    [string] $DisplayName = '3D Print Log',
    [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Get-InsightsId([string] $name) {
    $id = az resource show --resource-group $ResourceGroup --name $name `
        --resource-type Microsoft.Insights/components --query id --output tsv
    if (-not $id) { throw "Application Insights resource '$name' not found in '$ResourceGroup'." }
    return $id
}

$apiId = Get-InsightsId $ApiInsightsName
$uiId = Get-InsightsId $UiInsightsName

$mode = if ($WhatIf) { 'what-if' } else { 'create' }
Write-Host "Deploying workbook '$DisplayName' to $ResourceGroup ($mode)..."

az deployment group $mode `
    --resource-group $ResourceGroup `
    --name printlog-monitoring `
    --template-file main.bicep `
    --parameters apiInsightsId=$apiId uiInsightsId=$uiId displayName=$DisplayName `
    --query 'properties.outputs.portalUrl.value' --output tsv

if ($LASTEXITCODE -ne 0) { throw "Deployment failed." }
