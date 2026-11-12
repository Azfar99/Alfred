param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '2.0.1',
    [switch]$WithCuda,
    [switch]$SkipBuild,
    [string]$Compiler = ''
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$env:TEMP=Join-Path $root 'temp';$env:TMP=$env:TEMP
if (-not $Compiler) {$Compiler=Join-Path $root 'build\tools\InnoSetup\ISCC.exe'}
if (-not (Test-Path -LiteralPath $Compiler)) {throw 'Inno Setup 6.7.4+ compiler missing. See installer\README.md.'}
if (-not $SkipBuild) {& (Join-Path $root 'build.ps1')}
$small=Join-Path $root 'models\ggml-small-q5_1.bin'
if (-not (Test-Path -LiteralPath $small)) {throw 'Run git lfs pull to fetch the speech models.'}
if ((Get-FileHash -LiteralPath $small -Algorithm SHA1).Hash -ne '6FE57DDCFDD1C6B07CDCC73AAF620810CE5FC771') {throw 'Official multilingual small model checksum mismatch.'}
$vc=Join-Path $root 'build\tools\vc_redist.x64.exe'
if (-not (Test-Path -LiteralPath $vc)) {Invoke-WebRequest -Uri 'https://aka.ms/vs/17/release/vc_redist.x64.exe' -OutFile $vc}
$signature=Get-AuthenticodeSignature -LiteralPath $vc
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {throw 'Microsoft runtime signature validation failed.'}
$edition=if($WithCuda){'Windows-x64-CUDA'}else{'Windows-x64'}
$payload=Join-Path $root ('build\installer-payload-'+[guid]::NewGuid().ToString('N'))
$app=Join-Path $payload 'app'
$out=Join-Path $root 'releases'
$dirs=@($app,(Join-Path $app 'engine\Release'),(Join-Path $app 'engine\sherpa'),(Join-Path $app 'models\wake'),(Join-Path $app 'licenses'),(Join-Path $payload 'prerequisites'),$out)
New-Item -ItemType Directory -Path $dirs -Force|Out-Null
foreach($name in @('Alfred.exe','COMMANDS-BN.txt','TECHNOLOGY.md')) {Copy-Item -LiteralPath (Join-Path $root $name) -Destination $app}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'INSTALL-README.txt') -Destination (Join-Path $app 'README.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') -Destination $app
foreach($name in @('whisper-cli.exe','whisper.dll','ggml.dll','ggml-base.dll')) {Copy-Item -LiteralPath (Join-Path $root ('engine\Release\'+$name)) -Destination (Join-Path $app 'engine\Release')}
Get-ChildItem -LiteralPath (Join-Path $root 'engine\Release') -Filter 'ggml-cpu-*.dll' -File | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $app 'engine\Release')}
foreach($name in @('sherpa-onnx-c-api.dll','onnxruntime.dll')){Copy-Item -LiteralPath (Join-Path $root ('engine\sherpa\'+$name)) -Destination (Join-Path $app 'engine\sherpa')}
Copy-Item -LiteralPath $small -Destination (Join-Path $app 'models')
Copy-Item -LiteralPath (Join-Path $root 'models\silero_vad.onnx') -Destination (Join-Path $app 'models')
Get-ChildItem -LiteralPath (Join-Path $root 'models\wake') -File|Where-Object {$_.Name -like '*.int8.onnx' -or $_.Name -in @('tokens.txt','keywords.txt')}|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $app 'models\wake')}
Get-ChildItem -LiteralPath (Join-Path $root 'third-party\licenses') -File|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $app 'licenses')}
Copy-Item -LiteralPath $vc -Destination (Join-Path $payload 'prerequisites')
if($WithCuda){
    $turbo=Join-Path $root 'models\ggml-large-v3-turbo-q5_0.bin'
    if((Get-FileHash -LiteralPath $turbo -Algorithm SHA256).Hash -ne '394221709CD5AD1F40C46E6031CA61BCE88931E6E088C188294C6D5A55FFA7E2'){throw 'Turbo model checksum mismatch.'}
    New-Item -ItemType Directory -Path (Join-Path $app 'engine\cuda') -Force|Out-Null
    Get-ChildItem -LiteralPath (Join-Path $root 'engine\cuda') -File|Where-Object {$_.Name -like '*.dll' -or $_.Name -eq 'whisper-cli.exe'}|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $app 'engine\cuda')}
    Copy-Item -LiteralPath $turbo -Destination (Join-Path $app 'models')
}
# Native smoke check uses the exact curated payload, not the development checkout.
$check=Start-Process -FilePath (Join-Path $app 'Alfred.exe') -ArgumentList '--check-speech-engine' -WorkingDirectory $app -WindowStyle Hidden -Wait -PassThru
if($check.ExitCode -ne 0){throw 'Curated payload failed native speech validation.'}
# Diagnostic processes create scratch directories; [Files] must exclude them.
$manifest=@(Get-ChildItem -LiteralPath $app -Recurse -File|Where-Object {$_.FullName.Substring($app.Length+1) -notmatch '^(data|temp|build)\\'}|ForEach-Object {
    [ordered]@{path=$_.FullName.Substring($app.Length+1).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
if($manifest.Count -lt 20){throw 'Release manifest unexpectedly empty/incomplete.'}
[ordered]@{version=$Version;edition=$edition;files=$manifest}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $app 'release-manifest.json') -Encoding utf8
& $Compiler ("/DAppVersion=$Version") ("/DPayloadDir=$payload") ("/DOutputPath=$out") ("/DEdition=$edition") (Join-Path $PSScriptRoot 'Alfred.iss')
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed.'}
$setup=Join-Path $out "Alfred-$Version-$edition-Setup.exe"
$hash=(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($setup+'.sha256',$hash+'  '+[IO.Path]::GetFileName($setup)+[Environment]::NewLine)
Copy-Item -LiteralPath (Join-Path $app 'release-manifest.json') -Destination (Join-Path $out "Alfred-$Version-$edition-manifest.json") -Force
[ordered]@{setup=$setup;bytes=(Get-Item -LiteralPath $setup).Length;sha256=$hash;payload=$app}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $out 'latest-build.json') -Encoding utf8
Get-Item -LiteralPath $setup|Select-Object FullName,Length
