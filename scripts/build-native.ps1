$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sdk=if($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))){Join-Path $env:DOTNET_ROOT 'dotnet.exe'}else{'dotnet'}
$version=(Get-Content -LiteralPath (Join-Path $projectRoot 'package.json') -Raw | ConvertFrom-Json).version
$publishTarget=[IO.Path]::GetFullPath((Join-Path $projectRoot 'native\publish'))
$staging=[IO.Path]::GetFullPath((Join-Path $projectRoot ('native\publish-stage-'+[Guid]::NewGuid().ToString('N'))))
function Assert-PublishIdle {
  foreach($running in @(Get-Process -Name 'DiskManager.Native' -ErrorAction SilentlyContinue)) {
    if($running.Path -and $running.Path.StartsWith($publishTarget+'\',[StringComparison]::OrdinalIgnoreCase)) { throw '请先正常退出开发版 DiskManager 及其扫描任务，再构建原生后台。' }
  }
}
Assert-PublishIdle
& $sdk publish (Join-Path $projectRoot 'native\DiskManager.Native.csproj') -c Release -p:Version=$version -o $staging
if($LASTEXITCODE -ne 0){throw 'Native build failed.'}
if((Get-Item -LiteralPath (Join-Path $staging 'DiskManager.Native.exe')).Length -lt 5000000){throw 'Expected a self-contained native bundle.'}
Assert-PublishIdle
if(Test-Path -LiteralPath $publishTarget){
  $resolved=(Resolve-Path -LiteralPath $publishTarget).Path
  if($resolved -ne $publishTarget -or -not $resolved.StartsWith($projectRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or ((Get-Item -LiteralPath $publishTarget).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Native output path is outside the owned workspace.'}
  Remove-Item -LiteralPath $publishTarget -Recurse -Force
}
if(-not $staging.StartsWith($projectRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Native staging path is outside the workspace.'}
Move-Item -LiteralPath $staging -Destination $publishTarget
