param(
  [Parameter(Mandatory)][string]$PayloadDirectory,
  [Parameter(Mandatory)][string]$Version,
  [Parameter(Mandatory)][string]$OutputFile,
  [string]$CompilerPath = $env:INNOSETUP_ISCC,
  [string]$WebViewBootstrapper,
  [string]$RecordDirectory,
  [string]$QaRoot,
  [ValidateSet('system','light','dark')][string]$QaTheme='system'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$innoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$innoProduct = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'product.json') -Raw | ConvertFrom-Json
if(-not $QaRoot -and $QaTheme -ne 'system'){throw 'Forced appearance is available only in an isolated QA build.'}
$innoToolchain = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'toolchain.json') -Raw | ConvertFrom-Json
$innoPayload = [IO.Path]::GetFullPath($PayloadDirectory)
$innoOutput = [IO.Path]::GetFullPath($OutputFile)
if(-not $RecordDirectory){$RecordDirectory=if($QaRoot){Join-Path $QaRoot 'records'}else{Join-Path $innoRoot ('.packaging\staging\installer-'+[Guid]::NewGuid().ToString('N'))}}
$innoRecords=[IO.Path]::GetFullPath($RecordDirectory)
$innoRecordParent=if($QaRoot){[IO.Path]::GetFullPath($QaRoot)}else{Join-Path $innoRoot '.packaging'}
if(-not $innoRecords.StartsWith($innoRecordParent+'\',[StringComparison]::OrdinalIgnoreCase) -and $innoRecords -ne $innoRecordParent){throw 'Installer records must remain in the internal packaging/QA directory.'}
New-Item -ItemType Directory -Force -Path $innoRecords | Out-Null
if($Version -notmatch '^\d+\.\d+\.\d+$'){throw 'Use the application version from its existing version source.'}
if(-not (Test-Path -LiteralPath (Join-Path $innoPayload $innoProduct.Executable))){throw 'The packaged application payload is missing.'}
$innoExeVersion = (Get-Item -LiteralPath (Join-Path $innoPayload $innoProduct.Executable)).VersionInfo.ProductVersion
if($innoExeVersion -and ([version]$innoExeVersion).ToString(3) -ne ([version]$Version).ToString(3)){throw "Payload version $innoExeVersion differs from $Version"}
if(-not $CompilerPath){
  $innoCache = Join-Path $innoRoot '.deps\inno-7.1.0-portable'
  $CompilerPath = Join-Path $innoCache 'ISCC.exe'
  if(-not (Test-Path -LiteralPath $CompilerPath)){
    $innoArchive = Join-Path $innoRoot '.deps\innosetup-7.1.0-x64.exe'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $innoArchive) | Out-Null
    if(-not (Test-Path -LiteralPath $innoArchive)){Invoke-WebRequest -Uri $innoToolchain.url -OutFile $innoArchive}
    if((Get-FileHash -LiteralPath $innoArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $innoToolchain.sha256){throw 'Inno Setup toolchain checksum mismatch.'}
    $innoSignature = Get-AuthenticodeSignature -LiteralPath $innoArchive
    if($innoSignature.Status -ne 'Valid' -or $innoSignature.SignerCertificate.Subject -notmatch [regex]::Escape($innoToolchain.publisher)){throw 'Inno Setup publisher verification failed.'}
    # Official portable mode disables registration, uninstallation records, icons and associations.
    $innoExtract = Start-Process -FilePath $innoArchive -ArgumentList @('/PORTABLE=1','/CURRENTUSER','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/TASKS=',('/DIR="'+$innoCache+'"')) -WindowStyle Hidden -Wait -PassThru
    if($innoExtract.ExitCode -ne 0){throw 'Inno Setup portable extraction failed.'}
  }
}
if(-not (Test-Path -LiteralPath $CompilerPath)){throw 'Inno Setup ISCC.exe was not found.'}
$innoCompilerVersion = (& $CompilerPath --version | Out-String).Trim()
if($LASTEXITCODE -ne 0 -or $innoCompilerVersion -ne '7.1.0'){throw 'This installer is pinned to Inno Setup 7.1.0.'}
$innoWork = Join-Path $innoRoot ('.installer-build\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $innoWork | Out-Null
& (Join-Path $PSScriptRoot 'ui\build.ps1') -OutputDirectory $innoWork | Write-Host
$innoSceneRoot=Join-Path $PSScriptRoot 'assets'
& (Join-Path $PSScriptRoot 'test-assets.ps1') | Write-Host
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $innoOutput) | Out-Null
if($QaRoot){
  $QaRoot = [IO.Path]::GetFullPath($QaRoot)
  if(-not $QaRoot.StartsWith($innoRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or $QaRoot -notmatch '[\\/](?:work|\.local|\.runtime)[\\/]inno-qa-[a-f0-9]{32}$'){throw 'QA builds require a unique in-project work/.local/.runtime inno-qa-<GUID> directory.'}
  $innoQaId = [IO.Path]::GetFileName($QaRoot).Substring(8)
  New-Item -ItemType Directory -Force -Path $QaRoot | Out-Null
}
$innoFiles = @(Get-ChildItem -LiteralPath $innoPayload -File -Recurse | Sort-Object FullName)
foreach($innoFile in $innoFiles){
  $innoRelative = [IO.Path]::GetRelativePath($innoPayload,$innoFile.FullName)
  if($innoRelative.StartsWith('..') -or $innoRelative -match '(^|[\\/])(\.git|\.runtime|\.local|node_modules|runtime\.json|settings\.json|config\.json|.*\.pdb)$'){throw "Unexpected application payload file: $innoRelative"}
  $innoAncestor = $innoFile
  while($innoAncestor -and $innoAncestor.FullName.Length -ge $innoPayload.Length){
    if(($innoAncestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Installer payload must not contain links.'}
    $innoAncestor = if($innoAncestor -is [IO.FileInfo]){$innoAncestor.Directory}else{$innoAncestor.Parent}
  }
}
$innoManifest = Join-Path $innoWork 'payload-files.txt'
$innoLicenseFiles=@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'licenses') -File | Sort-Object Name)
$innoOwnedPaths=@($innoFiles | ForEach-Object {[IO.Path]::GetRelativePath($innoPayload,$_.FullName)}) + @($innoLicenseFiles | ForEach-Object {'licenses\installer-ui\'+$_.Name})
[IO.File]::WriteAllLines($innoManifest,$innoOwnedPaths,[Text.UTF8Encoding]::new($true))
if($innoProduct.Kind -notin @('network','disk')){
  if(-not $WebViewBootstrapper){$WebViewBootstrapper = Join-Path $innoRoot $innoProduct.Bootstrapper}
  if(-not (Test-Path -LiteralPath $WebViewBootstrapper)){
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $WebViewBootstrapper) | Out-Null
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $WebViewBootstrapper
  }
  $innoWebHash = (Get-FileHash -LiteralPath $WebViewBootstrapper -Algorithm SHA256).Hash.ToLowerInvariant()
  if($innoWebHash -ne 'f414de01c4680f0bb0694d1ea7803c6968f3a81bf6bad01b89bfc65dce14c2a2'){throw 'WebView2 bootstrapper checksum mismatch.'}
  $innoWebSignature = Get-AuthenticodeSignature -LiteralPath $WebViewBootstrapper
  if($innoWebSignature.Status -ne 'Valid' -or $innoWebSignature.SignerCertificate.Subject -notmatch 'Microsoft Corporation'){throw 'WebView2 publisher verification failed.'}
}else{$innoWebHash=''; $WebViewBootstrapper=''}

# Export the confirmed application image; no new or substitute character artwork.
Add-Type -AssemblyName System.Drawing
$innoImage = [Drawing.Image]::FromFile((Join-Path $innoRoot $innoProduct.BrandImage))
try{
  foreach($innoTheme in @('light','dark')){
    $innoBitmap = [Drawing.Bitmap]::new(492,942)
    $innoGraphics = [Drawing.Graphics]::FromImage($innoBitmap)
    try{
      $innoColor = if($innoTheme -eq 'light'){'#FFFCF6'}else{'#24272B'}
      $innoGraphics.Clear([Drawing.ColorTranslator]::FromHtml($innoColor))
      $innoGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
      $innoClip = [Drawing.Drawing2D.GraphicsPath]::new()
      $innoRectangle = [Drawing.Rectangle]::new(24,174,444,444)
      $innoRadius = 96
      $innoClip.AddArc(24,174,$innoRadius*2,$innoRadius*2,180,90)
      $innoClip.AddArc(468-$innoRadius*2,174,$innoRadius*2,$innoRadius*2,270,90)
      $innoClip.AddArc(468-$innoRadius*2,618-$innoRadius*2,$innoRadius*2,$innoRadius*2,0,90)
      $innoClip.AddArc(24,618-$innoRadius*2,$innoRadius*2,$innoRadius*2,90,90)
      $innoClip.CloseFigure(); $innoGraphics.SetClip($innoClip)
      $innoGraphics.DrawImage($innoImage,$innoRectangle)
      $innoBitmap.Save((Join-Path $innoWork ('sidebar-'+$innoTheme+'.png')),[Drawing.Imaging.ImageFormat]::Png)
      $innoClip.Dispose()
    }finally{$innoGraphics.Dispose();$innoBitmap.Dispose()}
  }
  $innoHeader = [Drawing.Bitmap]::new(192,192)
  $innoGraphics = [Drawing.Graphics]::FromImage($innoHeader)
  try{
    $innoGraphics.Clear([Drawing.Color]::Transparent)
    $innoGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $innoGraphics.DrawImage($innoImage,[Drawing.Rectangle]::new(0,0,192,192))
    $innoHeader.Save((Join-Path $innoWork 'header.png'),[Drawing.Imaging.ImageFormat]::Png)
  }finally{$innoGraphics.Dispose();$innoHeader.Dispose()}
}finally{$innoImage.Dispose()}

$innoValues = [ordered]@{
  ProductName=$innoProduct.Name; ProductKind=$innoProduct.Kind; AppVersion=$Version
  SetupId=$innoProduct.Id; Executable=$innoProduct.Executable; UserFolder=$innoProduct.UserFolder
  AppUserModelId=$innoProduct.AppUserModelId; MinimumWindows=$innoProduct.MinimumWindows
  IconFile=(Join-Path $innoRoot $innoProduct.Icon); ShortcutIcon=$innoProduct.ShortcutIcon.Replace('${version}',$Version).Replace('/', '\')
  ArtworkDirectory=$innoWork; PayloadDirectory=$innoPayload; PayloadManifest=$innoManifest
  OutputDirectory=(Split-Path -Parent $innoOutput); OutputName=[IO.Path]::GetFileNameWithoutExtension($innoOutput)
  WebViewBootstrapper=$WebViewBootstrapper; WebViewHash=$innoWebHash
  WelcomeText=$innoProduct.WelcomeText; ClosePrompt=$innoProduct.ClosePrompt
  UiDescription=$innoProduct.Description
  UiRequirements=$(switch($innoProduct.Kind){'fan'{'需要 .NET Framework 4.8；缺少 WebView2 时会联网安装。'} 'landrop'{'缺少 WebView2 时会联网安装；无需另装 Python。'} 'network'{'需要 Windows 11 x64；运行库已包含在安装包中。'} 'disk'{'需要 Windows 11 x64；运行库已包含在安装包中。'}})
  DataDirectory=$innoProduct.DataDirectory; LegacyKey=$innoProduct.LegacyKey
  SetupMutex=('Local\Cinna.'+$innoProduct.Name+'.Setup,Global\Cinna.'+$innoProduct.Name+'.Setup'); InstallingMutex=$innoProduct.InstallingMutex
  RunningMutex=$innoProduct.RunningMutex; LaunchArguments=''; MaintenanceArguments=''; LaunchFlags=''
  DesktopLink=('{autodesktop}\'+$innoProduct.Name); StartMenuDirectory=('{autoprograms}\'+$innoProduct.Name)
  WizardAppearance='dynamic'
  UiDirectory=$innoWork
  SceneDirectory=(Join-Path $PSScriptRoot 'assets')
  UiTheme='0'
}
if($QaRoot){
  if($QaTheme -ne 'system'){$innoValues.WizardAppearance=$QaTheme;$innoValues.UiTheme=if($QaTheme -eq 'dark'){'2'}else{'1'}}
  $innoValues.SetupId = 'CinnaQA.'+$innoQaId
  $innoValues.QaRoot=$QaRoot
  $innoValues.QaWindowTitle=$innoProduct.Name+' Installer QA '+$innoQaId
  $innoValues.LegacyKey='Software\Cinna\InstallerQA\'+$innoQaId+'\Legacy'
  $innoValues.SetupMutex='Local\Cinna.InstallerQA.'+$innoQaId+'.Setup'
  $innoValues.InstallingMutex='Local\Cinna.InstallerQA.'+$innoQaId+'.Installing'
  $innoValues.RunningMutex='Local\Cinna.InstallerQA.'+$innoQaId+'.Running'
  $innoValues.DataDirectory=$QaRoot+'\data'
  $innoValues.DesktopLink=$QaRoot+'\shortcuts\desktop\'+$innoProduct.Name
  $innoValues.StartMenuDirectory=$QaRoot+'\shortcuts\start\'+$innoProduct.Name
  $innoValues.LaunchFlags='unchecked'
  if($innoProduct.Kind -eq 'landrop'){
    $innoValues.LaunchArguments='--state-dir "'+$QaRoot+'\data"'
    $innoValues.MaintenanceArguments=$innoValues.LaunchArguments
  }
  if($innoProduct.Kind -eq 'fan'){$innoValues.LaunchArguments='--ui-qa'}
}
$innoConfig = Join-Path $innoWork 'config.iss'
$innoValues.LaunchCode = $innoValues.LaunchArguments.Replace("'", "''")
$innoValues.LaunchArguments = $innoValues.LaunchArguments.Replace('"','""')
$innoDefines = foreach($innoEntry in $innoValues.GetEnumerator()){
  '#define '+$innoEntry.Key+' "'+([string]$innoEntry.Value).Replace('"','""')+'"'
}
[IO.File]::WriteAllLines($innoConfig,$innoDefines,[Text.UTF8Encoding]::new($true))
$innoCleanup = @($innoProduct.LegacyFiles | ForEach-Object {'    DeleteFile(ExpandConstant(''{app}\'+$_.Replace('/','\').Replace('''','''''')+'''));'})
[IO.File]::WriteAllLines((Join-Path $innoWork 'legacy-cleanup.iss'),$innoCleanup,[Text.UTF8Encoding]::new($true))
& $CompilerPath /Qp ('/DBuildConfig='+$innoConfig) ('/I'+$innoWork) (Join-Path $PSScriptRoot 'setup.iss')
if($LASTEXITCODE -ne 0){throw 'Inno Setup compilation failed.'}
$innoDigest = (Get-FileHash -LiteralPath $innoOutput -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $innoRecords 'installer.sha256'),$innoDigest+'  '+[IO.Path]::GetFileName($innoOutput)+[Environment]::NewLine,[Text.Encoding]::ASCII)
$innoRecord = [ordered]@{
  product=$innoProduct.Name;version=$Version;engine='Inno Setup';engineVersion=$innoToolchain.version
  setupId=$innoValues.SetupId;qaRoot=$QaRoot;sha256=$innoDigest;outputFile=$innoOutput
  sharedScriptSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'setup.iss') -Algorithm SHA256).Hash.ToLowerInvariant()
  interface='Cinna native scene installer';uiCompiler='Zig 0.15.2'
  uiDllSha256=(Get-FileHash -LiteralPath (Join-Path $innoWork 'InstallerUi.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
  sources=@(Get-ChildItem -LiteralPath $PSScriptRoot -File -Recurse | Where-Object Extension -in '.iss','.cpp','.h','.hpp','.def','.ps1','.json' | Sort-Object FullName | ForEach-Object {[ordered]@{path=[IO.Path]::GetRelativePath($PSScriptRoot,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
  files=@($innoFiles | ForEach-Object {[ordered]@{path=[IO.Path]::GetRelativePath($innoPayload,$_.FullName);bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
  installerFiles=@($innoLicenseFiles | ForEach-Object {[ordered]@{path=('licenses/installer-ui/'+$_.Name);bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
}
$innoRecord | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $innoRecords 'installer.build.json') -Encoding utf8NoBOM
Write-Output $innoOutput
