#Requires -Version 7.1

<#
.SYNOPSIS
Creates or reuses a Catalyst project and app for the plain-Dapr reproduction.
.DESCRIPTION
Install the current Diagrid CLI: https://docs.diagrid.io/getting-started/install-cli/
Run diagrid login and select the intended organization first.
This provisions cloud resources; it does not create API keys or store credentials.
.EXAMPLE
pwsh ./scripts/setup-catalyst.ps1
.EXAMPLE
pwsh ./scripts/setup-catalyst.ps1 -ProjectName your-project-name
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $ProjectName = 'parallel-rerun-repro',
    [string] $AppId = 'parallel-rerun-repro',
    [string] $Region
)

$ErrorActionPreference = 'Stop'
if ($null -eq (Get-Command diagrid -ErrorAction SilentlyContinue)) {
    throw 'Install Diagrid CLI from https://docs.diagrid.io/getting-started/install-cli/ and add it to PATH.'
}

$devHelp = & diagrid dev run --help | Out-String
if ($LASTEXITCODE -ne 0 -or $devHelp -notmatch '--skip-managed-pubsub' -or $devHelp -notmatch '--id\s') {
    throw 'Update Diagrid CLI before setup: this sample requires dev run --id and --skip-managed-pubsub. See https://docs.diagrid.io/getting-started/install-cli/'
}

function Get-Names([string[]] $Arguments) {
    $output = & diagrid @Arguments --output json
    if ($LASTEXITCODE -ne 0) { throw 'Cannot list Catalyst resources. Check diagrid login and your selected organization.' }
    $text = ($output -join "`n") -replace '\x1b\[[0-9;]*m', ''
    $start = [regex]::Match($text, '(?m)^\s*[\{\[]')
    if ($start.Success -eq $false) { throw 'Diagrid returned no JSON; update the CLI and retry.' }
    $response = $text.Substring($start.Index) | ConvertFrom-Json
    $items = if ($null -ne $response.items) { $response.items } else { $response }

    return @($items | ForEach-Object { $_.metadata.name })
}

function Invoke-Diagrid([string[]] $Arguments) {
    # Creation output can contain application tokens; do not print it.
    $output = & diagrid @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Diagrid $($Arguments[0]) $($Arguments[1]) failed." }
}

if ($PSCmdlet.ShouldProcess($ProjectName, 'Configure Catalyst project and app') -eq $false) { return }
if ($ProjectName -notin (Get-Names @('project', 'list'))) {
    $arguments = @('project', 'create', $ProjectName, '--enable-managed-workflow', '--wait')
    if ($Region) { $arguments += @('--region', $Region) }
    Invoke-Diagrid $arguments
}
if ($AppId -notin (Get-Names @('app', 'list', '--project', $ProjectName))) {
    Invoke-Diagrid @('app', 'create', $AppId, '--project', $ProjectName, '--wait')
}

Write-Host "Configured project $ProjectName and app $AppId."
Write-Host "Run Aspire with: dotnet run --project AppHost -- --UseCatalyst=true --CatalystProject=$ProjectName --CatalystAppId=$AppId"
