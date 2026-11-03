param([Parameter(Mandatory)][string]$PayloadDirectory,[Parameter(Mandatory)][string]$Version,[string]$CompilerPath=$env:INNOSETUP_ISCC)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$uiProject=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$uiProduct=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'product.json') -Raw | ConvertFrom-Json
$uiParent=switch($uiProduct.Kind){'fan'{'work'} 'landrop'{'.local'} 'network'{'.runtime'} 'disk'{'.runtime'}}
$uiRoot=Join-Path $uiProject ($uiParent+'\inno-qa-'+[Guid]::NewGuid().ToString('N'))
$uiSetup=Join-Path $uiRoot 'custom-ui-qa.exe'
& (Join-Path $PSScriptRoot 'build.ps1') -PayloadDirectory $PayloadDirectory -Version $Version -OutputFile $uiSetup -CompilerPath $CompilerPath -QaRoot $uiRoot -QaTheme dark | Write-Host
& (Join-Path $PSScriptRoot 'ui\test.ps1') | Write-Host
$uiRecord=Get-Content -LiteralPath (Join-Path $uiRoot 'records\installer.build.json') -Raw | ConvertFrom-Json
if($uiRecord.qaRoot -ne $uiRoot){throw 'Custom UI QA requires isolated registration and paths.'}
$uiApp=Join-Path $uiRoot '程序 安装'
$uiData=Join-Path $uiRoot 'data'
New-Item -ItemType Directory -Force -Path $uiApp,$uiData | Out-Null
$uiUserFile=Join-Path $uiApp '用户资料.txt';$uiDataFile=Join-Path $uiData '设置.txt'
[IO.File]::WriteAllText($uiUserFile,'keep custom UI installation files');[IO.File]::WriteAllText($uiDataFile,'keep custom UI application data')
$uiArgs=@('/CURRENTUSER','/NORESTART','/CINNAAUTO','/CINNAQAEXIT',('/DIR="'+$uiApp+'"'),'/TASKS=desktopicon,startmenuicon')
function Invoke-UiQa([string]$Label,[string[]]$Extra=@(),[bool]$ExpectedSuccess=$true){
  $uiLog=Join-Path $uiRoot ($Label+'.log')
  $uiProcess=Start-Process -FilePath $uiSetup -ArgumentList ($uiArgs+$Extra+@('/LOG="'+$uiLog+'"')) -WindowStyle Hidden -PassThru
  if(-not $uiProcess.WaitForExit(120000)){throw "Native UI QA timed out: $uiLog"}
  if($ExpectedSuccess -and $uiProcess.ExitCode -ne 0){throw "Native UI QA failed: $uiLog"}
  if(-not $ExpectedSuccess -and $uiProcess.ExitCode -eq 0){throw "Native UI QA should have cancelled: $uiLog"}
  if(-not ([IO.File]::ReadAllText($uiLog).Contains('Custom native installer UI initialized.'))){throw 'The real native presentation did not initialize.'}
  if([IO.File]::ReadAllText($uiUserFile) -ne 'keep custom UI installation files' -or [IO.File]::ReadAllText($uiDataFile) -ne 'keep custom UI application data'){throw 'Custom UI QA changed user data.'}
}
function Read-UiHashes {
  $uiHashes=@{}
  foreach($uiFile in (Get-ChildItem -LiteralPath $uiApp -File -Recurse)){
    $uiHashes[[IO.Path]::GetRelativePath($uiApp,$uiFile.FullName)]=(Get-FileHash -LiteralPath $uiFile.FullName -Algorithm SHA256).Hash
  }
  return $uiHashes
}
Invoke-UiQa 'fresh-handoff' @('/CINNAQAHANDOFF')
if(-not (Test-Path -LiteralPath (Join-Path $uiRoot ('shortcuts\desktop\'+$uiProduct.Name+'.lnk'))) -or -not (Test-Path -LiteralPath (Join-Path $uiRoot ('shortcuts\start\'+$uiProduct.Name+'\'+$uiProduct.Name+'.lnk')))){throw 'Native handoff lost selected shortcut options.'}
$uiPreviousExecutable=Join-Path $uiApp $uiProduct.Executable
$uiPreviousStream=[IO.File]::Open($uiPreviousExecutable,[IO.FileMode]::Append,[IO.FileAccess]::Write)
try{$uiPreviousBytes=[Text.Encoding]::ASCII.GetBytes('CINNA-QA-PREVIOUS-VERSION');$uiPreviousStream.Write($uiPreviousBytes,0,$uiPreviousBytes.Length)}finally{$uiPreviousStream.Dispose()}
$uiBefore=Read-UiHashes
Invoke-UiQa 'cancel-upgrade' @('/CINNAQACANCEL=40') $false
if(-not ([IO.File]::ReadAllText((Join-Path $uiRoot 'cancel-upgrade.log')).Contains('Foreground cancellation request sent to backend'))){throw 'Upgrade cancellation did not cross the foreground/backend transport.'}
$uiAfter=Read-UiHashes
foreach($uiPath in $uiBefore.Keys){if($uiAfter[$uiPath] -ne $uiBefore[$uiPath]){throw "Cancelled upgrade did not restore: $uiPath"}}
$uiKey=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\'+$uiRecord.setupId+'_is1')
if(-not $uiKey){throw 'Cancelled upgrade lost the previous uninstall record.'};$uiKey.Dispose()
Invoke-UiQa 'upgrade'
$uiUninstall=Join-Path $uiApp 'unins000.exe'
$uiUninstaller=Start-Process -FilePath $uiUninstall -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -PassThru
if(-not $uiUninstaller.WaitForExit(90000) -or $uiUninstaller.ExitCode -ne 0){throw 'QA uninstall failed.'}
for($uiWait=0;$uiWait -lt 100;$uiWait++){
  $uiMutex=$null
  if(-not [Threading.Mutex]::TryOpenExisting(('Local\Cinna.InstallerQA.'+([IO.Path]::GetFileName($uiRoot).Substring(8))+'.Installing'),[ref]$uiMutex)){break}
  $uiMutex.Dispose();Start-Sleep -Milliseconds 100
}
Invoke-UiQa 'cancel-fresh' @('/CINNAQACANCEL=40') $false
if(-not ([IO.File]::ReadAllText((Join-Path $uiRoot 'cancel-fresh.log')).Contains('Foreground cancellation request sent to backend'))){throw 'Fresh cancellation did not cross the foreground/backend transport.'}
if(Test-Path -LiteralPath (Join-Path $uiApp $uiProduct.Executable)){throw 'Cancelled fresh install left program files.'}
if(Get-ChildItem -LiteralPath $uiRoot -Directory -Filter ('.'+$uiProduct.Name+'.rollback-*')){throw 'Completed recovery left an upgrade journal.'}
[ordered]@{product=$uiProduct.Name;qaRoot=$uiRoot;checks=@('real native UI initializes and drives Inno','fresh and in-place upgrade','cancelled upgrade restores every original file hash and uninstall record','cancelled fresh install removes program changes','user files and application data preserved')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $uiRoot 'custom-ui-results.json') -Encoding utf8NoBOM
Write-Output ('PASS '+$uiProduct.Name+' custom native UI, upgrade cancellation recovery and fresh cancellation: '+$uiRoot)
