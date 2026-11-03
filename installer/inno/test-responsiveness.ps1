param([Parameter(Mandatory)][string]$PayloadDirectory,[Parameter(Mandatory)][string]$Version,[string]$CompilerPath=$env:INNOSETUP_ISCC,[int]$MaxHoverPaintMs=0)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$probeProject=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$probeProduct=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'product.json') -Raw | ConvertFrom-Json
$probeParent=switch($probeProduct.Kind){'fan'{'work'} 'landrop'{'.local'} 'network'{'.runtime'} 'disk'{'.runtime'}}
$probeRoot=Join-Path $probeProject ($probeParent+'\inno-qa-'+[Guid]::NewGuid().ToString('N'))
$probeSetup=Join-Path $probeRoot ($probeProduct.Name+'-responsiveness-qa.exe')
& (Join-Path $PSScriptRoot 'build.ps1') -PayloadDirectory $PayloadDirectory -Version $Version -OutputFile $probeSetup -CompilerPath $CompilerPath -QaRoot $probeRoot | Write-Host
$probeRecord=Get-Content -LiteralPath (Join-Path $probeRoot 'records\installer.build.json') -Raw | ConvertFrom-Json
if($probeRecord.qaRoot -ne $probeRoot){throw 'Responsiveness testing requires a compile-time isolated QA build.'}
try{
  # An intentionally visible test window measures real message and draw work.
  $probeProcess=Start-Process -FilePath $probeSetup -ArgumentList @('/CURRENTUSER','/CINNAAUTO','/CINNAQAEXIT','/CINNAQAPROBE',('/LOG="'+(Join-Path $probeRoot 'probe.log')+'"')) -PassThru
  if(-not $probeProcess.WaitForExit(120000)){throw "Responsiveness test timed out: $probeRoot"}
  if($probeProcess.ExitCode -ne 0){throw "Responsiveness installation failed: $probeRoot"}
  $probeStats=Get-Content -LiteralPath (Join-Path $probeRoot 'responsiveness.json') -Raw | ConvertFrom-Json
  if($probeStats.dispatched -lt 20 -or $probeStats.painted -lt 20){throw 'Insufficient actual message/draw samples.'}
  $probeStats | ConvertTo-Json -Compress | Write-Output
  if($MaxHoverPaintMs -gt 0 -and $probeStats.hoverPaintMaxMs -gt $MaxHoverPaintMs){throw "Hover response exceeded the local test budget of $MaxHoverPaintMs ms."}
  Write-Output ('PASS '+$probeProduct.Name+' installation responsiveness: '+$probeRoot)
}finally{
  $probeKey=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\'+$probeRecord.setupId+'_is1')
  if($probeKey){
    try{$probeLocation=([string]$probeKey.GetValue('InstallLocation')).TrimEnd('\')}finally{$probeKey.Dispose()}
    if(-not ([IO.Path]::GetFullPath($probeLocation)).StartsWith($probeRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected QA registration location; cleanup stopped.'}
    $probeUninstaller=Join-Path $probeLocation 'unins000.exe'
    $probeCleanup=Start-Process -FilePath $probeUninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -PassThru
    if(-not $probeCleanup.WaitForExit(90000) -or $probeCleanup.ExitCode -ne 0){throw 'Responsiveness QA cleanup failed.'}
  }
}
