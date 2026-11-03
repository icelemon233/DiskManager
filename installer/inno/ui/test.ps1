param([string]$ZigPath=$env:CINNA_ZIG)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$testProject=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if(-not $ZigPath){$ZigPath=Join-Path $testProject '.deps\zig-x86_64-windows-0.15.2\zig.exe'}
if(-not (Test-Path -LiteralPath $ZigPath)){throw 'Run the installer builder first or set CINNA_ZIG to the pinned compiler.'}
$testOutput=Join-Path $testProject ('.installer-build\presentation-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testOutput | Out-Null
$testExe=Join-Path $testOutput 'presentation-tests.exe'
& $ZigPath c++ -target x86-windows-gnu -O2 -std=c++17 -municode (Join-Path $PSScriptRoot 'PresentationTests.cpp') -o $testExe -lgdiplus -lgdi32 -lcomctl32 -ldwmapi -luxtheme -lole32 -lshell32 -ladvapi32 -loleacc -luuid
if($LASTEXITCODE -ne 0){throw 'Presentation regression compilation failed.'}
$testProduct=Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\product.json') -Raw | ConvertFrom-Json
& $testExe (Join-Path $PSScriptRoot '..\assets') (Join-Path $testProject $testProduct.BrandImage)
if($LASTEXITCODE -ne 0){throw 'Native presentation regression failed.'}
