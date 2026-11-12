param([switch]$Gpu)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(-not $Gpu){Write-Output 'CPU setup/model/runtime are included in the backup. Use -Gpu to restore optional upstream acceleration.';return}
$deps=Get-Content (Join-Path $PSScriptRoot 'upstream-gpu-dependencies.json') -Raw|ConvertFrom-Json
$cache=Join-Path $root 'build\dependency-restore';New-Item -ItemType Directory -Path $cache -Force|Out-Null
foreach($dep in $deps){$file=Join-Path $cache ([IO.Path]::GetFileName(([Uri]$dep.url).AbsolutePath));Invoke-WebRequest -Uri $dep.url -OutFile $file;if((Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $dep.sha256){throw "Dependency hash mismatch: $($dep.name)"};if($dep.target -eq 'engine/cuda'){$unpack=Join-Path $cache 'cuda';Expand-Archive -LiteralPath $file -DestinationPath $unpack -Force;$dest=Join-Path $root 'engine\cuda';New-Item -ItemType Directory -Path $dest -Force|Out-Null;Get-ChildItem $unpack -Recurse -File|Where-Object {$_.Name -like '*.dll' -or $_.Name -eq 'whisper-cli.exe'}|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $dest -Force}}else{Copy-Item -LiteralPath $file -Destination (Join-Path $root $dep.target) -Force}}
