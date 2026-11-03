$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing
$sceneRoot=Join-Path $PSScriptRoot 'assets'
$sceneProduct=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'product.json') -Raw | ConvertFrom-Json
$sceneFiles=@()
foreach($sceneTheme in @('day','night')){
  $sceneSource=Join-Path $sceneRoot ('source-'+$sceneTheme+'.png')
  $sceneOutput=Join-Path $sceneRoot ('scene-'+$sceneTheme+'.jpg')
  $sceneImage=[Drawing.Image]::FromFile($sceneSource)
  try{
    $sceneCodec=[Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg'
    $sceneParams=[Drawing.Imaging.EncoderParameters]::new(1)
    try{
      $sceneParams.Param[0]=[Drawing.Imaging.EncoderParameter]::new([Drawing.Imaging.Encoder]::Quality,[long]90)
      $sceneImage.Save($sceneOutput,$sceneCodec,$sceneParams)
    }finally{$sceneParams.Dispose()}
    foreach($sceneFile in @($sceneSource,$sceneOutput)){
      $sceneFiles+=[ordered]@{path=[IO.Path]::GetFileName($sceneFile);width=$sceneImage.Width;height=$sceneImage.Height;sha256=(Get-FileHash -LiteralPath $sceneFile -Algorithm SHA256).Hash.ToLowerInvariant()}
    }
  }finally{$sceneImage.Dispose()}
}
[ordered]@{product=$sceneProduct.Name;kind=$sceneProduct.Kind;edition='2026-10';origin='Cinna installer scenes, generated from the approved character and composition references';files=$sceneFiles} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $sceneRoot 'manifest.json') -Encoding utf8NoBOM
