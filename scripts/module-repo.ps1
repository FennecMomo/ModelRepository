param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CommandArguments
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src\ModuleRepository.Cli\ModuleRepository.Cli.csproj'
& dotnet run --project $projectPath --configuration Release --verbosity quiet -- @CommandArguments
exit $LASTEXITCODE
