param(
    [ValidateRange(1, 65535)][int]$Port = 5188,
    [string]$DataDirectory,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src\ModuleRepository.Web\ModuleRepository.Web.csproj'
if ([string]::IsNullOrWhiteSpace($DataDirectory)) {
    $DataDirectory = Join-Path $repositoryRoot '.local\data'
}
$resolvedDataDirectory = [System.IO.Path]::GetFullPath($DataDirectory)
$runArguments = @('run', '--project', $projectPath, '--no-launch-profile', '-c', 'Release')
if ($NoBuild) { $runArguments += '--no-build' }
$runArguments += @('--', '--urls', "http://localhost:$Port", '--Repository:DataDirectory', $resolvedDataDirectory)

Write-Host "模块仓库：http://localhost:$Port"
Write-Host "数据目录：$resolvedDataDirectory"
Write-Host '首次访问时，在界面中创建管理员。按 Ctrl+C 停止。'
& dotnet @runArguments
exit $LASTEXITCODE
