$ErrorActionPreference = 'Stop'
$alfredRoot = $PSScriptRoot
$alfredTemp = Join-Path $alfredRoot 'temp'
New-Item -ItemType Directory -Path $alfredTemp, (Join-Path $alfredRoot 'build') -Force | Out-Null
$env:TEMP = $alfredTemp
$env:TMP = $alfredTemp
$alfredCompiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$alfredSpeech = 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll'
$alfredSources = Get-ChildItem -LiteralPath (Join-Path $alfredRoot 'source') -Filter '*.cs' | Select-Object -ExpandProperty FullName
& $alfredCompiler /nologo /target:winexe /platform:x64 /optimize+ /debug- /utf8output "/out:$alfredRoot\Alfred.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "/r:$alfredSpeech" $alfredSources
if ($LASTEXITCODE -ne 0) { throw 'Alfred compilation failed.' }
$alfredTest = Start-Process -FilePath (Join-Path $alfredRoot 'Alfred.exe') -ArgumentList '--self-test' -WorkingDirectory $alfredRoot -WindowStyle Hidden -Wait -PassThru
Get-Content -LiteralPath (Join-Path $alfredRoot 'build\tests.txt')
if ($alfredTest.ExitCode -ne 0) { throw 'Alfred command routing checks failed.' }
Get-Item -LiteralPath (Join-Path $alfredRoot 'Alfred.exe') | Select-Object FullName, Length
