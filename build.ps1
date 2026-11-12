$ErrorActionPreference = 'Stop'
$alfredRoot = $PSScriptRoot
$alfredTemp = Join-Path $alfredRoot 'temp'
$alfredStaged = Join-Path $alfredRoot 'Alfred-stage.exe'
$alfredExe = Join-Path $alfredRoot 'Alfred.exe'
New-Item -ItemType Directory -Path $alfredTemp, (Join-Path $alfredRoot 'build') -Force | Out-Null
$env:TEMP = $alfredTemp
$env:TMP = $alfredTemp
$alfredCompiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$alfredSpeech = 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll'
$alfredWpf = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF'
$alfredSources = Get-ChildItem -LiteralPath (Join-Path $alfredRoot 'source') -Filter '*.cs' | Select-Object -ExpandProperty FullName
& $alfredCompiler /nologo /target:winexe /platform:x64 /optimize+ /debug- /utf8output "/out:$alfredStaged" "/win32icon:$alfredRoot\installer\Alfred.ico" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "/r:$alfredSpeech" "/r:$alfredWpf\UIAutomationClient.dll" "/r:$alfredWpf\UIAutomationTypes.dll" "/r:$alfredWpf\WindowsBase.dll" $alfredSources
if ($LASTEXITCODE -ne 0) { throw 'Alfred compilation failed.' }
$alfredTest = Start-Process -FilePath $alfredStaged -ArgumentList '--self-test' -WorkingDirectory $alfredRoot -WindowStyle Hidden -Wait -PassThru
Get-Content -LiteralPath (Join-Path $alfredRoot 'build\tests.txt')
if ($alfredTest.ExitCode -ne 0) { throw 'Alfred command routing checks failed.' }
$alfredNativeCheck = Start-Process -FilePath $alfredStaged -ArgumentList '--check-speech-engine' -WorkingDirectory $alfredRoot -WindowStyle Hidden -Wait -PassThru
Get-Content -LiteralPath (Join-Path $alfredRoot 'build\native-check.txt')
if ($alfredNativeCheck.ExitCode -ne 0) { throw 'Alfred native speech engine validation failed; current application was preserved.' }
$running = Get-Process -Name 'Alfred' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $alfredExe }
if ($running) {
    Start-Process -FilePath $alfredExe -ArgumentList '--quit' -WorkingDirectory $alfredRoot -WindowStyle Hidden -Wait
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}
Move-Item -LiteralPath $alfredStaged -Destination $alfredExe -Force
Start-Process -FilePath $alfredExe -WorkingDirectory $alfredRoot -WindowStyle Hidden
Get-Item -LiteralPath $alfredExe | Select-Object FullName, Length
