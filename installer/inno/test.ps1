param(
  [Parameter(Mandatory)][string]$PayloadDirectory,
  [Parameter(Mandatory)][string]$Version,
  [string]$CompilerPath = $env:INNOSETUP_ISCC
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$qaProject = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$qaProduct = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'product.json') -Raw | ConvertFrom-Json
$qaParent = switch($qaProduct.Kind){'fan'{'work'} 'landrop'{'.local'} 'network'{'.runtime'} 'disk'{'.runtime'}}
$qaId = [Guid]::NewGuid().ToString('N')
$qaRoot = Join-Path $qaProject ($qaParent+'\inno-qa-'+$qaId)
$qaSetup = Join-Path $qaRoot ($qaProduct.Name+'-setup-qa.exe')
& (Join-Path $PSScriptRoot 'build.ps1') -PayloadDirectory $PayloadDirectory -Version $Version -OutputFile $qaSetup -CompilerPath $CompilerPath -QaRoot $qaRoot | Write-Host
$qaRecord = Get-Content -LiteralPath (Join-Path $qaRoot 'records\installer.build.json') -Raw | ConvertFrom-Json
if($qaRecord.qaRoot -ne $qaRoot){throw 'Never run installer integration against a production build.'}
$qaKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\'+$qaRecord.setupId+'_is1'
$qaLegacyKey = 'Software\Cinna\InstallerQA\'+$qaId+'\Legacy'
$qaApplication = Join-Path $qaRoot '程序 安装 🧊'
$qaData = Join-Path $qaRoot 'data'
New-Item -ItemType Directory -Path $qaData,$qaApplication -Force | Out-Null
$qaUserFile = Join-Path $qaApplication '用户保留文件.txt'
$qaDataFile = Join-Path $qaData '保留资料.txt'
[IO.File]::WriteAllText($qaUserFile,'keep installation-directory user files')
[IO.File]::WriteAllText($qaDataFile,'keep user settings and received files')
$qaChecks = [Collections.Generic.List[string]]::new()
function Invoke-QaInstaller([string]$File,[string[]]$Arguments,[bool]$Success=$true){
  $qaProcess = Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -PassThru
  if(-not $qaProcess.WaitForExit(90000)){throw "QA installer timed out. Inspect $qaRoot before retrying."}
  if($Success -and $qaProcess.ExitCode -ne 0){
    foreach($qaFailureLog in @(Get-ChildItem -LiteralPath $qaRoot -File -Filter '*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 1)){Get-Content -LiteralPath $qaFailureLog.FullName -Tail 65 | Write-Host}
    throw "Installer exited with $($qaProcess.ExitCode); inspect logs under $qaRoot"
  }
  if(-not $Success -and $qaProcess.ExitCode -eq 0){throw 'Installer should have refused the unsafe operation.'}
  if($Success -and [IO.Path]::GetFileName($File) -like 'unins*.exe'){
    # Inno's original uninstaller can exit while its temporary copy completes cleanup.
    for($qaWait=0;$qaWait -lt 100;$qaWait++){
      $qaOpenMutex=$null
      if(-not [Threading.Mutex]::TryOpenExisting(('Local\Cinna.InstallerQA.'+$qaId+'.Installing'),[ref]$qaOpenMutex)){break}
      $qaOpenMutex.Dispose(); Start-Sleep -Milliseconds 100
    }
  }
  return $qaProcess.ExitCode
}
function Assert-Qa([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
function Assert-QaPreserved {
  Assert-Qa ([IO.File]::ReadAllText($qaUserFile) -eq 'keep installation-directory user files') 'Installation-directory user file was changed.'
  Assert-Qa ([IO.File]::ReadAllText($qaDataFile) -eq 'keep user settings and received files') 'User data was changed.'
}
function Read-QaRegistration {
  $qaRegistration = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($qaKey)
  if(-not $qaRegistration){return $null}
  try{return @{Directory=$qaRegistration.GetValue('InstallLocation');Publisher=$qaRegistration.GetValue('Publisher');Version=$qaRegistration.GetValue('DisplayVersion')}}finally{$qaRegistration.Dispose()}
}
$qaArgs = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER',('/DIR="'+$qaApplication+'"'))
Invoke-QaInstaller $qaSetup ($qaArgs+@('/TASKS=startmenuicon',('/LOG="'+(Join-Path $qaRoot 'fresh.log')+'"'))) | Out-Null
$qaRegistration = Read-QaRegistration
Assert-Qa ($qaRegistration -and $qaRegistration.Publisher -eq 'Cinna' -and $qaRegistration.Version -eq $Version) 'Uninstall registration is incorrect.'
Assert-Qa ([IO.Path]::TrimEndingDirectorySeparator($qaRegistration.Directory) -eq $qaApplication) 'Custom installation directory was not used.'
$qaDesktop = Join-Path $qaRoot ('shortcuts\desktop\'+$qaProduct.Name+'.lnk')
$qaStart = Join-Path $qaRoot ('shortcuts\start\'+$qaProduct.Name+'\'+$qaProduct.Name+'.lnk')
Assert-Qa (-not (Test-Path -LiteralPath $qaDesktop)) 'Desktop shortcut was created despite being unchecked.'
Assert-Qa (Test-Path -LiteralPath $qaStart) 'Start menu shortcut was not created.'
Assert-QaPreserved
$qaChecks.Add('fresh current-user install, Chinese/space path, selected shortcut options, registry')
Write-Output ('PASS '+$qaProduct.Name+': fresh install')

# Exercise the production silent-upgrade adapter against isolated registration.
# A repaired Start menu entry must survive the next unattended upgrade.
. (Join-Path $qaProject 'scripts\cinna-windows.ps1')
$qaShortcutRecord=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($qaKey)
try{
  $qaSnapshot=@{scope='current-user';directory=$qaApplication;shortcuts=@{desktop=$qaShortcutRecord.GetValue('CinnaDesktopShortcut',$null);startMenu=$qaShortcutRecord.GetValue('CinnaStartMenuShortcut',$null)}}
}finally{$qaShortcutRecord.Dispose()}
Invoke-CinnaInstaller $qaSetup $qaSnapshot (Join-Path $qaRoot 'automatic-shortcut-preservation.log')
Assert-Qa (-not (Test-Path -LiteralPath $qaDesktop)) 'Automatic upgrade enabled an unchecked desktop shortcut.'
Assert-Qa (Test-Path -LiteralPath $qaStart) 'Automatic upgrade lost the existing Start menu shortcut.'
Assert-QaPreserved
$qaChecks.Add('automatic silent upgrade preserves both saved shortcut choices')
Write-Output ('PASS '+$qaProduct.Name+': automatic shortcut preservation')

$qaLock = [Threading.Mutex]::new($false,('Local\Cinna.InstallerQA.'+$qaId+'.Installing'))
try{Invoke-QaInstaller $qaSetup ($qaArgs+@('/TASKS=desktopicon',('/LOG="'+(Join-Path $qaRoot 'busy.log')+'"'))) $false | Out-Null}finally{$qaLock.Dispose()}
Assert-QaPreserved
$qaChecks.Add('concurrent maintenance lock refuses writes')
Invoke-QaInstaller $qaSetup ($qaArgs+@('/TASKS=desktopicon',('/LOG="'+(Join-Path $qaRoot 'upgrade.log')+'"'))) | Out-Null
Assert-Qa (Test-Path -LiteralPath $qaDesktop) 'Desktop preference was not applied on upgrade.'
Assert-Qa (-not (Test-Path -LiteralPath $qaStart)) 'Start menu preference was not applied on upgrade.'
$qaShell = New-Object -ComObject Shell.Application
$qaFolder = $qaShell.NameSpace((Split-Path -Parent $qaDesktop))
$qaItem = $qaFolder.ParseName([IO.Path]::GetFileName($qaDesktop))
$qaLink = $qaItem.GetLink
Assert-Qa ($qaLink.Path -eq (Join-Path $qaApplication $qaProduct.Executable)) 'Shortcut points outside this installation.'
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($qaLink) | Out-Null
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($qaItem) | Out-Null
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($qaFolder) | Out-Null
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($qaShell) | Out-Null
Assert-QaPreserved
$qaChecks.Add('in-place upgrade and shortcut preferences')
Write-Output ('PASS '+$qaProduct.Name+': upgrade and shortcut preferences')

$qaUninstaller = Join-Path $qaApplication 'unins000.exe'
Invoke-QaInstaller $qaUninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $qaRoot 'uninstall.log')+'"')) | Out-Null
Assert-Qa (-not (Read-QaRegistration)) 'Uninstall registry record remains.'
Assert-Qa (-not (Test-Path -LiteralPath (Join-Path $qaApplication $qaProduct.Executable))) 'Uninstall left the application executable.'
Assert-Qa (-not (Test-Path -LiteralPath $qaDesktop)) 'Uninstall left the owned desktop shortcut.'
Assert-QaPreserved
$qaChecks.Add('uninstall removes owned files and registration while preserving user files and data')
Write-Output ('PASS '+$qaProduct.Name+': uninstall preserves user files')

# Simulate only the legacy registration and known uninstaller file. No daily installation is used.
if($qaProduct.LegacyFiles.Count -gt 0){
$qaLegacy = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($qaLegacyKey)
try{
  # Legacy installations can omit InstallLocation. Resolve the quoted Unicode
  # uninstaller path while ignoring its arguments, without executing it.
  $qaLegacy.SetValue('UninstallString','"'+(Join-Path $qaApplication $qaProduct.LegacyFiles[0])+'" /S')
  $qaLegacy.SetValue('DisplayName',$qaProduct.Name)
}finally{$qaLegacy.Dispose()}
$qaLegacyUninstaller = Join-Path $qaApplication $qaProduct.LegacyFiles[0]
[IO.File]::WriteAllText($qaLegacyUninstaller,'isolated legacy installer fixture')
if($qaProduct.Kind -eq 'landrop'){
  $qaOldLink = Join-Path $qaRoot ('shortcuts\start\'+$qaProduct.Name+'\卸载.lnk')
  New-Item -ItemType Directory -Path (Split-Path -Parent $qaOldLink) -Force | Out-Null
  $qaOldAsciiLink = Join-Path (Split-Path -Parent $qaOldLink) 'legacy-uninstall.lnk'
  $qaOldShell = New-Object -ComObject WScript.Shell
  $qaOldShortcut = $qaOldShell.CreateShortcut($qaOldAsciiLink)
  $qaOldShortcut.TargetPath = Join-Path $env:WINDIR 'System32\notepad.exe'
  $qaOldShortcut.Save()
  [Runtime.InteropServices.Marshal]::FinalReleaseComObject($qaOldShortcut) | Out-Null
  [Runtime.InteropServices.Marshal]::FinalReleaseComObject($qaOldShell) | Out-Null
  [IO.File]::Move($qaOldAsciiLink,$qaOldLink)
  # WScript's target property uses the system code page. Shell's link object
  # preserves the full Unicode target when creating this legacy fixture.
  $qaOldShell = New-Object -ComObject Shell.Application
  $qaOldFolder = $qaOldShell.NameSpace((Split-Path -Parent $qaOldLink))
  $qaOldItem = $qaOldFolder.ParseName([IO.Path]::GetFileName($qaOldLink))
  $qaOldShortcut = $qaOldItem.GetLink
  $qaOldShortcut.Path = $qaLegacyUninstaller
  $qaOldShortcut.Save()
  foreach($qaOldObject in @($qaOldShortcut,$qaOldItem,$qaOldFolder,$qaOldShell)){[Runtime.InteropServices.Marshal]::FinalReleaseComObject($qaOldObject) | Out-Null}
}
Invoke-QaInstaller $qaSetup ($qaArgs+@('/TASKS=desktopicon,startmenuicon',('/LOG="'+(Join-Path $qaRoot 'legacy-migration.log')+'"'))) | Out-Null
Assert-Qa ([Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($qaLegacyKey) -eq $null) 'Legacy registration was not retired.'
Assert-Qa (-not (Test-Path -LiteralPath $qaLegacyUninstaller)) 'Legacy uninstaller remains.'
if($qaProduct.Kind -eq 'landrop'){Assert-Qa (-not (Test-Path -LiteralPath $qaOldLink)) 'Legacy uninstall shortcut remains.'}
Assert-Qa ($null -ne (Read-QaRegistration)) 'Inno registration missing after migration.'
Assert-QaPreserved
$qaChecks.Add('legacy registration migration without duplicate uninstall records')
}else{
  Invoke-QaInstaller $qaSetup ($qaArgs+@('/TASKS=desktopicon,startmenuicon',('/LOG="'+(Join-Path $qaRoot 'reinstall.log')+'"'))) | Out-Null
  Assert-Qa ($null -ne (Read-QaRegistration)) 'Inno registration missing after reinstall.'
  Assert-QaPreserved
  $qaChecks.Add('fresh product reinstall without legacy metadata')
}

$qaInvalidArgs = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER',('/DIR="'+$qaProject+'"'),('/LOG="'+(Join-Path $qaRoot 'invalid-path.log')+'"'))
Invoke-QaInstaller $qaSetup $qaInvalidArgs $false | Out-Null
Assert-QaPreserved
$qaChecks.Add('invalid directory refuses installation')
Invoke-QaInstaller (Join-Path $qaApplication 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $qaRoot 'final-uninstall.log')+'"')) | Out-Null
Assert-QaPreserved

# A same-named user shortcut to a different executable must remain untouched.
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $qaDesktop) | Out-Null
$qaForeignShell=New-Object -ComObject WScript.Shell
$qaForeignLink=$qaForeignShell.CreateShortcut($qaDesktop)
$qaForeignLink.TargetPath=Join-Path $env:WINDIR 'System32\notepad.exe'
$qaForeignLink.Save()
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($qaForeignLink) | Out-Null
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($qaForeignShell) | Out-Null
$qaForeignHash=(Get-FileHash -LiteralPath $qaDesktop -Algorithm SHA256).Hash
Invoke-QaInstaller $qaSetup ($qaArgs+@('/TASKS=desktopicon',('/LOG="'+(Join-Path $qaRoot 'foreign-shortcut-refused.log')+'"'))) $false | Out-Null
Assert-Qa ((Get-FileHash -LiteralPath $qaDesktop -Algorithm SHA256).Hash -eq $qaForeignHash) 'Installer overwrote a foreign shortcut.'
Invoke-QaInstaller $qaSetup ($qaArgs+@('/TASKS=',('/LOG="'+(Join-Path $qaRoot 'foreign-shortcut-preserved.log')+'"'))) | Out-Null
Assert-Qa ((Get-FileHash -LiteralPath $qaDesktop -Algorithm SHA256).Hash -eq $qaForeignHash) 'Unchecked shortcut choice removed a foreign shortcut.'
Invoke-QaInstaller (Join-Path $qaApplication 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') | Out-Null
Assert-Qa ((Get-FileHash -LiteralPath $qaDesktop -Algorithm SHA256).Hash -eq $qaForeignHash) 'Uninstall removed a foreign shortcut.'
Remove-Item -LiteralPath $qaDesktop
Assert-QaPreserved
$qaChecks.Add('same-named foreign shortcut is refused for overwrite and preserved when unchecked and uninstalled')
$qaResult = [ordered]@{product=$qaProduct.Name;engine='Inno Setup 7.1.0';passed=@($qaChecks);qaRoot=$qaRoot;allUsers='requires interactive UAC verification';missingRuntime='not installed or changed by QA'}
$qaResult | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $qaRoot 'result.json') -Encoding utf8NoBOM
Write-Output ('PASS '+$qaProduct.Name+': legacy migration and path guards; results '+$qaRoot)
