# Shared Windows delivery adapter. Source this file; it does not start an application.
Set-StrictMode -Version Latest
if(-not (Get-Variable -Name CinnaMaintenanceOwner -Scope Script -ErrorAction SilentlyContinue)){$script:CinnaMaintenanceOwner=[Guid]::NewGuid().ToString()}

function Write-CinnaJson([string]$Path,$Value) {
  $temporary=$Path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
  [IO.File]::WriteAllText($temporary,($Value|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
  [IO.File]::Move($temporary,$Path,$true)
}
function Test-CinnaWithin([string]$Parent,[string]$Child) {
  $base=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Parent))
  return [IO.Path]::GetFullPath($Child).StartsWith($base+'\',[StringComparison]::OrdinalIgnoreCase)
}
function Assert-CinnaPlainPath([string]$Path) {
  $cursor=[IO.Path]::GetFullPath($Path)
  while($cursor.Length -gt 3){
    if(Test-Path -LiteralPath $cursor){if((Get-Item -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint){throw ('Linked paths are not accepted: '+$cursor)}}
    $cursor=[IO.Path]::GetDirectoryName($cursor)
  }
}
function Get-CinnaInstallation([string]$Root) {
  $product=Get-Content -LiteralPath (Join-Path $Root 'installer\inno\product.json') -Raw | ConvertFrom-Json
  $found=@()
  foreach($hive in @([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryHive]::LocalMachine)){
    $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey($hive,[Microsoft.Win32.RegistryView]::Registry64)
    try{
      foreach($key in @(('Software\Microsoft\Windows\CurrentVersion\Uninstall\'+$product.Id+'_is1'),$product.LegacyKey)){
        $record=$base.OpenSubKey($key)
        if(-not $record){continue}
        try{
          $directory=[string]$record.GetValue('InstallLocation')
          if(-not $directory){
            $uninstall=[string]$record.GetValue('UninstallString')
            if($uninstall -match '^"([^"]+\.exe)"'){$directory=[IO.Path]::GetDirectoryName($Matches[1])}
            elseif($uninstall -match '^(.*?\.exe)(?:\s|$)'){$directory=[IO.Path]::GetDirectoryName($Matches[1])}
          }
          if(-not $directory){continue}
          $directory=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($directory))
          Assert-CinnaPlainPath $directory
          $executable=Join-Path $directory $product.Executable
          if(-not (Test-Path -LiteralPath $executable -PathType Leaf)){continue}
          $info=(Get-Item -LiteralPath $executable).VersionInfo
          if($info.ProductName -ne $product.Name -or $info.CompanyName -ne 'Cinna'){throw 'Registered executable identity differs from this Cinna product.'}
          $version=([version]($info.FileVersion.Trim().Split('+')[0])).ToString(3)
          $shortcuts=@{desktop=$record.GetValue('CinnaDesktopShortcut',$null);startMenu=$record.GetValue('CinnaStartMenuShortcut',$null)}
          $found+= [ordered]@{product=$product.Name;directory=$directory;executable=$executable;version=$version;registeredVersion=[string]$record.GetValue('DisplayVersion');scope=if($hive -eq [Microsoft.Win32.RegistryHive]::CurrentUser){'current-user'}else{'all-users'};key=$key;shortcuts=$shortcuts}
          break
        } finally {$record.Dispose()}
      }
    } finally {$base.Dispose()}
  }
  if($found.Count -eq 0){return $null}
  return $found[0]
}
function Get-CinnaProcessImage([uint32]$ProcessId) {
  # CIM can omit the path of an elevated worker belonging to this same user.
  # Query identity with limited read access; never request termination or write access.
  if(-not ('CinnaDelivery.ProcessImage' -as [type])){
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
namespace CinnaDelivery {
  public static class ProcessImage {
    [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    public static string Read(uint id) {
      var handle=OpenProcess(0x1000,false,id);
      if(handle==IntPtr.Zero)return null;
      try {
        uint size=32768;var name=new StringBuilder((int)size);
        return QueryFullProcessImageName(handle,0,name,ref size)?name.ToString():null;
      } finally { CloseHandle(handle); }
    }
  }
}
'@
  }
  return [CinnaDelivery.ProcessImage]::Read($ProcessId)
}
function Get-CinnaProcesses($Installation,[switch]$IncludeQa) {
  $names=@([IO.Path]::GetFileName($Installation.executable),($Installation.product+'.Worker.exe'),($Installation.product+'.Native.exe'))
  $filter=($names|ForEach-Object {if($_ -notmatch '^[A-Za-z0-9.]+\.exe$'){throw 'Invalid executable name'};"Name = '$_'"}) -join ' OR '
  $items=@(Get-CimInstance Win32_Process -Filter $filter)
  $result=@()
  foreach($item in $items){
    if(-not $item.ExecutablePath){$item.CimInstanceProperties['ExecutablePath'].Value=Get-CinnaProcessImage $item.ProcessId}
    if(-not $item.ExecutablePath -and -not (Get-Process -Id $item.ProcessId -ErrorAction SilentlyContinue)){continue}
    if(-not $item.ExecutablePath){if($item.SessionId -eq (Get-Process -Id $PID).SessionId){throw 'A potentially related process cannot be identified; preserve the running application.'};continue}
    if(-not (Test-CinnaWithin $Installation.directory $item.ExecutablePath)){continue}
    if(-not $IncludeQa -and $item.CommandLine -match '(?:^|\s)--(?:ui-qa|test-data|smoke|capture)(?:\s|=|$)'){continue}
    $result+=$item
  }
  return $result
}
function Get-CinnaDataDirectory([string]$Root) {
  $product=Get-Content -LiteralPath (Join-Path $Root 'installer\inno\product.json') -Raw | ConvertFrom-Json
  return $product.DataDirectory.Replace('{localappdata}',[Environment]::GetFolderPath('LocalApplicationData')).Replace('{userappdata}',[Environment]::GetFolderPath('ApplicationData'))
}
function Read-CinnaMaintenance($Installation,[string]$DataDirectory,[string]$Action='inspect') {
  $processes=@(Get-CinnaProcesses $Installation)
  if($processes.Count -eq 0){return @{windowState='closed';busy=$false;unsaved=$false;healthy=$true;pid=0}}
  Assert-CinnaPlainPath $DataDirectory
  if(-not (Test-Path -LiteralPath $DataDirectory -PathType Container)){throw 'The running application has no identifiable maintenance profile.'}
  $requestFile=Join-Path $DataDirectory 'maintenance-request.json'
  $statusFile=Join-Path $DataDirectory 'maintenance.json'
  $candidates=@($processes|Where-Object {$_.ExecutablePath -eq $Installation.executable -and $_.CommandLine -notmatch '--type[= ]'})
  foreach($process in $candidates){
    $nonce=[Guid]::NewGuid().ToString()
    if(Test-Path -LiteralPath $requestFile){
      try{$previous=Get-Content -LiteralPath $requestFile -Raw|ConvertFrom-Json -AsHashtable}catch{throw 'Unrecognized maintenance request was preserved.'}
      if($previous.schema -ne 1 -or $previous.product -ne $Installation.product){throw 'Unrecognized maintenance request was preserved.'}
    }
    Write-CinnaJson $requestFile @{schema=1;product=$Installation.product;owner=$script:CinnaMaintenanceOwner;pid=[int]$process.ProcessId;nonce=$nonce;action=$Action;updatedAt=[DateTimeOffset]::UtcNow.ToString('o')}
    for($attempt=0;$attempt -lt 35;$attempt++){
      Start-Sleep -Milliseconds 100
      if(Test-Path -LiteralPath $statusFile){
        try{
          $status=Get-Content -LiteralPath $statusFile -Raw | ConvertFrom-Json -AsHashtable
          if($status.requestNonce -eq $nonce -and $status.pid -eq $process.ProcessId -and $status.product -eq $Installation.product -and $status.version -eq $Installation.version -and [IO.Path]::GetFullPath($status.executable) -eq $Installation.executable -and [Math]::Abs(([DateTimeOffset]::UtcNow-([DateTimeOffset]$status.updatedAt)).TotalSeconds) -lt 15){return $status}
        }catch{ }
      }
    }
  }
  throw 'The running version did not provide a fresh, matching maintenance status. No process was terminated.'
}
function Wait-CinnaIdleAndExit($Installation,[string]$DataDirectory) {
  $initial=Read-CinnaMaintenance $Installation $DataDirectory
  if($initial.windowState -eq 'closed'){return $initial}
  if($initial.unsaved){throw 'Unapplied edits are present. Save or close the draft before retrying the upgrade.'}
  while($true){
    $status=Read-CinnaMaintenance $Installation $DataDirectory
    if($status.windowState -eq 'closed'){return $initial}
    if($status.unsaved){throw 'Unapplied edits appeared; the running application was preserved.'}
    if(-not $status.healthy){throw 'Application health/status is unavailable; the running application was preserved.'}
    if(-not $status.busy){break}
    Write-Host ('Waiting for '+$Installation.product+' tasks to finish; no task is cancelled.')
    Start-Sleep -Seconds 2
  }
  $null=Read-CinnaMaintenance $Installation $DataDirectory 'exit'
  for($attempt=0;$attempt -lt 60;$attempt++){
    if(@(Get-CinnaProcesses $Installation).Count -eq 0){return $initial}
    Start-Sleep -Seconds 1
    if(($attempt % 10) -eq 9){
      $status=Read-CinnaMaintenance $Installation $DataDirectory
      if($status.unsaved){throw 'Unapplied work blocked shutdown; the application was preserved.'}
      if(-not $status.busy -and $status.healthy){throw 'The application declined normal exit; no repeated exit or forced termination was attempted.'}
    }
  }
  throw 'The application or an owned child did not finish its normal exit. No files were replaced.'
}
function Clear-CinnaMaintenanceRequest([string]$DataDirectory) {
  if(-not $DataDirectory){return}
  $file=Join-Path $DataDirectory 'maintenance-request.json'
  if(-not (Test-Path -LiteralPath $file)){return}
  try{
    $value=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json -AsHashtable
    if($value.schema -eq 1 -and $value.owner -eq $script:CinnaMaintenanceOwner){Remove-Item -LiteralPath $file}
  }catch{Write-Warning 'An unknown maintenance request was preserved.'}
}
function Test-CinnaInstalledPayload([string]$Directory,$Manifest) {
  foreach($entry in $Manifest.files){
    $file=[IO.Path]::GetFullPath((Join-Path $Directory $entry.path))
    if(-not (Test-CinnaWithin $Directory $file)){throw 'Invalid package payload path'}
    Assert-CinnaPlainPath $file
    if(-not (Test-Path -LiteralPath $file -PathType Leaf)){return $false}
    if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256){return $false}
  }
  return $true
}
function Invoke-CinnaInstaller([string]$Installer,$Installation,[string]$LogFile) {
  $scope=if($Installation.scope -eq 'current-user'){'/CURRENTUSER'}else{'/ALLUSERS'}
  $arguments=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CLOSEAPP',$scope,('/DIR="'+$Installation.directory+'"'),('/LOG="'+$LogFile+'"'))
  # Silent setup must receive the user's choices explicitly. Its task page is
  # skipped, and relying on previous-task defaults can remove repaired entries.
  if($Installation.Contains('shortcuts')){
    $choices=$Installation.shortcuts
    if($null -ne $choices.desktop -and $null -ne $choices.startMenu -and $choices.desktop -in @(0,1) -and $choices.startMenu -in @(0,1)){
      $desktopTask=if($choices.desktop -eq 1){'desktopicon'}else{'!desktopicon'}
      $startTask=if($choices.startMenu -eq 1){'startmenuicon'}else{'!startmenuicon'}
      $arguments+=('/TASKS="'+$desktopTask+','+$startTask+'"')
    }
  }
  $process=Start-Process -FilePath $Installer -ArgumentList $arguments -WindowStyle Hidden -PassThru
  while(-not $process.WaitForExit(1000)){ }
  if($process.ExitCode -ne 0){throw ('Installer exited with code '+$process.ExitCode+'; see '+$LogFile)}
}
function Restore-CinnaState($Installation,[string]$State,[string]$DataDirectory) {
  if($State -eq 'closed'){return}
  $restoreArguments=if($State -eq 'headless'){@('--headless')}else{@('--restore-window='+$State)}
  Start-Process -FilePath $Installation.executable -ArgumentList $restoreArguments -WorkingDirectory $Installation.directory -WindowStyle Hidden | Out-Null
  for($i=0;$i -lt 30;$i++){
    Start-Sleep -Seconds 1
    try{$status=Read-CinnaMaintenance $Installation $DataDirectory;if($status.healthy -and $status.windowState -eq $State){return}}catch{ }
  }
  throw 'The updated application did not restore a healthy prior window/tray state.'
}
