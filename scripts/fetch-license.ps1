param(
  [Parameter(Mandatory)][uri]$Uri,
  [Parameter(Mandatory)][string]$Destination,
  [ValidateSet('text','github-license')][string]$Mode='text'
)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$licenseRoot=Join-Path $projectRoot 'licenses\app'
$target=[IO.Path]::GetFullPath($Destination)
if($Uri.Scheme -ne 'https' -or $Uri.Host -notin @('api.github.com','raw.githubusercontent.com')){throw 'Unsupported license source.'}
if(-not $target.StartsWith($licenseRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'License output is outside the project license directory.'}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
for($attempt=0;$attempt -lt 3;$attempt++){
  try{
    if($Mode -eq 'github-license'){
      $license=Invoke-RestMethod -Uri $Uri -TimeoutSec 30
      [IO.File]::WriteAllBytes($target,[Convert]::FromBase64String($license.content))
      [ordered]@{source=$license.git_url;sha=$license.sha;license=$license.license.spdx_id} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path (Split-Path -Parent $target) 'source.json') -Encoding utf8NoBOM
    }else{
      Invoke-WebRequest -Uri $Uri -OutFile $target -TimeoutSec 30
    }
    break
  }catch{
    if($attempt -eq 2){throw}
    Start-Sleep -Seconds 1
  }
}
