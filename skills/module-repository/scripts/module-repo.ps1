param([Parameter(ValueFromRemainingArguments = $true)][string[]]$CommandArguments)
$ErrorActionPreference = 'Stop'
$settingsFile = Join-Path (Split-Path $PSScriptRoot -Parent) 'local-settings.json'
if (-not (Test-Path -LiteralPath $settingsFile)) { throw '请先执行仓库 scripts/Install-AgentIntegration.ps1。' }
$settings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
if (-not $env:MODULEREPO_SERVER) { $env:MODULEREPO_SERVER = $settings.server }
& $settings.cli @CommandArguments
exit $LASTEXITCODE
