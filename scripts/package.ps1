param([switch]$SkipInstallerTests)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $projectRoot
$product=Get-Content -LiteralPath (Join-Path $projectRoot 'package.json') -Raw | ConvertFrom-Json
$version=$product.version
if($version -notmatch '^\d+\.\d+\.\d+$'){throw 'Use a semantic product version.'}
function Assert-Command([string]$Step){if($LASTEXITCODE -ne 0){throw "$Step failed with code $LASTEXITCODE"}}
& pnpm assets; Assert-Command 'Asset export'
& pnpm lint; Assert-Command 'Lint'
& pnpm build; Assert-Command 'Frontend build'
& (Join-Path $PSScriptRoot 'build-native.ps1')
& (Join-Path $projectRoot 'native\publish\DiskManager.Native.exe') --self-test; Assert-Command 'Native regression'
& node (Join-Path $PSScriptRoot 'integration.mjs'); Assert-Command 'Native integration'
& node (Join-Path $PSScriptRoot 'licenses.mjs'); Assert-Command 'License collection'
$buildId=[Guid]::NewGuid().ToString('N')
$electronOutput=Join-Path $projectRoot ('.packaging\electron-'+$buildId)
& pnpm exec electron-builder --dir --win --x64 ('--config.directories.output='+$electronOutput); Assert-Command 'Application packaging'
$payload=Join-Path $electronOutput 'win-unpacked'
if(-not (Test-Path -LiteralPath (Join-Path $payload 'DiskManager.exe'))){throw 'Packaged executable is missing.'}
$compiler=$env:INNOSETUP_ISCC
if(-not $compiler){$compiler=(Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source)}
if(-not $compiler -or -not (Test-Path -LiteralPath $compiler)){throw 'Set INNOSETUP_ISCC to the Inno Setup 7.1.0 compiler or add ISCC.exe to PATH.'}
& (Join-Path $projectRoot 'installer\inno\test-assets.ps1')
$record=Join-Path $projectRoot ('.packaging\records\'+$version+'\'+$buildId)
New-Item -ItemType Directory -Force -Path $record | Out-Null
$candidate=Join-Path $record ('DiskManager-'+$version+'-windows-x64-setup.exe')
& (Join-Path $projectRoot 'installer\inno\build.ps1') -PayloadDirectory $payload -Version $version -OutputFile $candidate -CompilerPath $compiler -RecordDirectory (Join-Path $record 'installer')
if(-not $SkipInstallerTests){
  & (Join-Path $projectRoot 'installer\inno\test.ps1') -PayloadDirectory $payload -Version $version -CompilerPath $compiler
  & (Join-Path $projectRoot 'installer\inno\test-custom.ps1') -PayloadDirectory $payload -Version $version -CompilerPath $compiler
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$sourceFiles=@(& git -c core.quotepath=false ls-files --cached --others --exclude-standard)
Assert-Command 'Source inventory'
$archive=[IO.Compression.ZipFile]::Open((Join-Path $record 'source.zip'),[IO.Compression.ZipArchiveMode]::Create)
$manifest=@()
try{
  foreach($relative in $sourceFiles){
    $source=[IO.Path]::GetFullPath((Join-Path $projectRoot $relative))
    if(-not $source.StartsWith($projectRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Source path escapes workspace.'}
    if(-not (Test-Path -LiteralPath $source -PathType Leaf)){continue}
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$source,$relative.Replace('\','/'),[IO.Compression.CompressionLevel]::Optimal) | Out-Null
    $manifest+=[ordered]@{path=$relative;sha256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()}
  }
}finally{$archive.Dispose()}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $record 'source-manifest.json') -Encoding utf8NoBOM
$installers=Join-Path $projectRoot 'installers'
New-Item -ItemType Directory -Force -Path $installers | Out-Null
$output=Join-Path $installers ([IO.Path]::GetFileName($candidate))
if(Test-Path -LiteralPath $output){
  $previous=Join-Path $projectRoot ('.packaging\previous-candidate\'+[Guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Force -Path $previous | Out-Null
  Copy-Item -LiteralPath $output -Destination (Join-Path $previous ([IO.Path]::GetFileName($output)))
}
Copy-Item -LiteralPath $candidate -Destination $output -Force
[ordered]@{product='DiskManager';version=$version;createdAt=(Get-Date).ToUniversalTime().ToString('o');installer=$output;sha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant();record=$record;installerTests=(-not $SkipInstallerTests);signed=$false;firstInstallPerformed=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $projectRoot '.packaging\latest.json') -Encoding utf8NoBOM
Write-Output $output
