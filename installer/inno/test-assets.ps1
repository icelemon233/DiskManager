$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing
$sceneProduct=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'product.json') -Raw | ConvertFrom-Json
$sceneRoot=Join-Path $PSScriptRoot 'assets'
$sceneRecord=Get-Content -LiteralPath (Join-Path $sceneRoot 'manifest.json') -Raw | ConvertFrom-Json
if($sceneRecord.product -ne $sceneProduct.Name -or $sceneRecord.kind -ne $sceneProduct.Kind){throw 'Installer scenes belong to a different application.'}
foreach($sceneFile in $sceneRecord.files){
  $scenePath=Join-Path $sceneRoot $sceneFile.path
  if((Get-FileHash -LiteralPath $scenePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sceneFile.sha256){throw 'Installer scene differs from its approved source manifest.'}
  $sceneImage=[Drawing.Image]::FromFile($scenePath)
  try{if($sceneImage.Width -ne $sceneFile.width -or $sceneImage.Height -ne $sceneFile.height -or $sceneImage.Width -lt 1280 -or $sceneImage.Height -lt 768){throw 'Installer scene has unexpected dimensions.'}}finally{$sceneImage.Dispose()}
}
$sceneLuminance=@{}
foreach($sceneTheme in @('day','night')){
  $sceneBitmap=[Drawing.Bitmap]::new((Join-Path $sceneRoot ('scene-'+$sceneTheme+'.jpg')))
  try{
    $sceneTotal=0.0
    for($sceneI=1;$sceneI -le 10;$sceneI++){
      $scenePixel=$sceneBitmap.GetPixel([int]($sceneBitmap.Width*0.12),[int]($sceneBitmap.Height*$sceneI/12))
      $sceneTotal+=0.2126*$scenePixel.R+0.7152*$scenePixel.G+0.0722*$scenePixel.B
    }
    $sceneLuminance[$sceneTheme]=$sceneTotal/10
  }finally{$sceneBitmap.Dispose()}
}
if($sceneLuminance.day - $sceneLuminance.night -lt 80){throw 'Day and night text areas do not provide distinct light and dark surfaces.'}
Write-Output ('PASS '+$sceneProduct.Name+' installer scene identity, source/export hashes, dimensions and day/night surfaces')
