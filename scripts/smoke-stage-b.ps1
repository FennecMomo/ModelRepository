param(
    [Parameter(Mandatory = $true)][string]$Server,
    [string]$WorkDirectory
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$cli = Join-Path $repoRoot 'src/ModuleRepository.Cli/bin/Release/net10.0/module-repo.exe'
if (-not (Test-Path -LiteralPath $cli)) { throw '请先 dotnet build ModelRepository.sln -c Release。' }
if (-not $WorkDirectory) { $WorkDirectory = Join-Path $repoRoot ('artifacts/stage-b-smoke-' + [guid]::NewGuid().ToString('N')) }
$project = Join-Path $WorkDirectory 'project'
New-Item -ItemType Directory -Path $project -Force | Out-Null
$moduleId = 'smoke-' + [guid]::NewGuid().ToString('N')
function Api([string]$Method, [string]$Path, $Body) {
    $parameters = @{ Method = $Method; Uri = $Server.TrimEnd('/') + '/api/' + $Path }
    if ($null -ne $Body) { $parameters.Body = ConvertTo-Json $Body -Depth 20; $parameters.ContentType = 'application/json; charset=utf-8' }
    Invoke-RestMethod @parameters
}
function InvokeClient([string[]]$Arguments) {
    $output = & $cli @Arguments
    if ($LASTEXITCODE -ne 0) { throw "CLI 失败，退出码 $LASTEXITCODE" }
    ($output -join "`n") | ConvertFrom-Json
}
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Snippet([string]$Path, [string]$Content) {
    $detail = Api GET "modules/$moduleId" $null
    Api POST "modules/$moduleId/snippet" @{ path=$Path; content=$Content; expectedDraftRevision=$detail.draftRevision } | Out-Null
}
function Publish([string]$Version) {
    $detail = Api GET "modules/$moduleId" $null
    Api POST "modules/$moduleId/publish" @{ version=$Version; changeNotes='阶段 B 正常链路冒烟'; expectedDraftRevision=$detail.draftRevision } | Out-Null
}
$category = Api POST categories @{ name=$moduleId; parentId=$null }
Api POST modules @{ id=$moduleId; metadata=@{ name='本地接入冒烟'; summary='独立测试数据'; language='C#'; environment='.NET'; categoryId=$category.id; tags=@('smoke'); documentation='安装说明'; license='Internal' } } | Out-Null
Snippet 'A.cs' '// version 1'
Snippet 'B.cs' '// removed in version 2'
Publish '1.0.0'
$download = InvokeClient @('download','--server',$Server,'--module',$moduleId,'--output',(Join-Path $WorkDirectory 'module.zip'))
Assert ((Test-Path -LiteralPath $download.output) -and (Test-Path -LiteralPath $download.manifestPath)) '下载包或清单缺失'
$plan = InvokeClient @('plan','--server',$Server,'--module',$moduleId,'--project',$project,'--target','Assets/Reusable')
Assert ($plan.status -eq 'planned' -and -not (Test-Path -LiteralPath (Join-Path $project '.module-repository'))) '预览计划写入了项目或未成功'
$install = InvokeClient @('install','--server',$Server,'--module',$moduleId,'--project',$project,'--target','Assets/Reusable')
Assert ($install.success -and $install.version -eq '1.0.0') '首次安装失败'
$target = Join-Path $project 'Assets/Reusable'
[IO.File]::WriteAllText((Join-Path $target 'A.cs'), '// local modification')
[IO.File]::WriteAllText((Join-Path $target 'ProjectAdapter.cs'), '// keep project file')
Snippet 'A.cs' '// version 2'
$detail = Api GET "modules/$moduleId" $null
Api DELETE "modules/$moduleId/files" @{ path='B.cs'; expectedDraftRevision=$detail.draftRevision } | Out-Null
Snippet 'C.cs' '// added in version 2'
Publish '1.1.0'
$check = InvokeClient @('check-updates','--project',$project)
Assert ($check.updates[0].status -eq 'updateAvailable') '未发现新发布'
$update = InvokeClient @('update','--project',$project,'--installation',$install.installationId)
Assert ($update.success -and $update.version -eq '1.1.0') '覆盖更新失败'
Assert ([IO.File]::ReadAllText((Join-Path $target 'A.cs')) -eq '// version 2') '受管本地修改没有覆盖'
Assert (-not (Test-Path -LiteralPath (Join-Path $target 'B.cs'))) '旧受管文件没有删除'
Assert ([IO.File]::ReadAllText((Join-Path $target 'C.cs')) -eq '// added in version 2') '新文件缺失'
Assert ([IO.File]::ReadAllText((Join-Path $target 'ProjectAdapter.cs')) -eq '// keep project file') '项目额外文件被改变'
$list = InvokeClient @('list','--project',$project)
Assert ($list.state.installations.Count -eq 1 -and $list.state.installations[0].version -eq '1.1.0' -and $list.state.pendingOperations.Count -eq 0) '安装记录不正确'
$latest = InvokeClient @('check-updates','--project',$project)
Assert ($latest.updates[0].status -eq 'upToDate') '当前版本状态不正确'
[pscustomobject]@{ success=$true; scope='下载、计划、安装、检查更新、覆盖/新增/删除、额外文件保留、安装记录'; moduleId=$moduleId; installationId=$install.installationId; record=(Join-Path $project '.module-repository/modules.json'); workDirectory=$WorkDirectory } | ConvertTo-Json
