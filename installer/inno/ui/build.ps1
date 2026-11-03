param([Parameter(Mandatory)][string]$OutputDirectory,[string]$ZigPath=$env:CINNA_ZIG)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$uiRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if(-not $ZigPath){
  $uiCache=Join-Path $uiRoot '.deps\zig-x86_64-windows-0.15.2'
  $ZigPath=Join-Path $uiCache 'zig.exe'
  if(-not (Test-Path -LiteralPath $ZigPath)){
    $uiArchive=Join-Path $uiRoot '.deps\zig-x86_64-windows-0.15.2.zip'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $uiArchive) | Out-Null
    if(-not (Test-Path -LiteralPath $uiArchive)){Invoke-WebRequest -Uri 'https://ziglang.org/download/0.15.2/zig-x86_64-windows-0.15.2.zip' -OutFile $uiArchive}
    if((Get-FileHash -LiteralPath $uiArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne '3a0ed1e8799a2f8ce2a6e6290a9ff22e6906f8227865911fb7ddedc3cc14cb0c'){throw 'Native UI compiler checksum mismatch.'}
    Expand-Archive -LiteralPath $uiArchive -DestinationPath (Split-Path -Parent $uiCache) -Force
  }
}
if((& $ZigPath version | Out-String).Trim() -ne '0.15.2'){throw 'The native installer UI is pinned to Zig 0.15.2.'}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$uiDll=Join-Path $OutputDirectory 'InstallerUi.dll'
& $ZigPath c++ -target x86-windows-gnu -shared -O2 -std=c++17 (Join-Path $PSScriptRoot 'InstallerUi.cpp') (Join-Path $PSScriptRoot 'InstallerUi.def') -o $uiDll -lgdiplus -lgdi32 -lcomctl32 -ldwmapi -luxtheme -lole32 -lshell32 -ladvapi32 -loleacc -luuid
if($LASTEXITCODE -ne 0){throw 'Native installer UI compilation failed.'}
$uiBytes=[IO.File]::ReadAllBytes($uiDll)
if($uiBytes.Length -lt 512 -or $uiBytes[0] -ne 0x4d -or $uiBytes[1] -ne 0x5a){throw 'Native UI output is not a Windows PE image.'}
$uiPe=[BitConverter]::ToInt32($uiBytes,0x3c)
if([BitConverter]::ToUInt16($uiBytes,$uiPe+4) -ne 0x14c -or ([BitConverter]::ToUInt16($uiBytes,$uiPe+22) -band 0x2000) -eq 0){throw 'Native UI must be an x86 DLL for the pinned Inno host.'}
Write-Output $uiDll
