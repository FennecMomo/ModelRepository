param(
    [string]$Server = 'http://localhost:5188',
    [string]$SkillRoot,
    [switch]$SkipMcpRegistration
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $SkillRoot) {
    $codexDirectory = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
    $SkillRoot = Join-Path $codexDirectory 'skills'
}
$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$output = Join-Path $repo "artifacts/agent-$version"
foreach ($project in @('Mcp','Cli')) {
    & dotnet publish (Join-Path $repo "src/ModuleRepository.$project/ModuleRepository.$project.csproj") -c Release -o (Join-Path $output $project) --nologo
    if ($LASTEXITCODE -ne 0) { throw "发布 $project 失败。" }
}
$destination = Join-Path $SkillRoot 'module-repository'
New-Item -ItemType Directory -Path (Join-Path $destination 'scripts') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'skills/module-repository/SKILL.md') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $repo 'skills/module-repository/scripts/module-repo.ps1') -Destination (Join-Path $destination 'scripts') -Force
$cli = Join-Path $output 'Cli/module-repo.exe'
$mcp = Join-Path $output 'Mcp/module-repo-mcp.exe'
@{ server=$Server; cli=$cli; mcp=$mcp; version=$version } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'local-settings.json') -Encoding utf8
if (-not $SkipMcpRegistration) {
    & codex mcp add module-repository --env "MODULEREPO_SERVER=$Server" -- $mcp
    if ($LASTEXITCODE -ne 0) { throw 'MCP 注册失败，Skill 和发布文件已保留。' }
}
[pscustomobject]@{version=$version;server=$Server;skill=$destination;cli=$cli;mcp=$mcp;registered=(-not $SkipMcpRegistration)} | ConvertTo-Json
