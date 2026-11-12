$ErrorActionPreference = 'Stop'
$alfredRoot = 'F:\Alfred'
$alfredTemp = Join-Path $alfredRoot 'temp'
$state = Get-Content (Join-Path $alfredTemp 'test-restore-state.json') -Raw | ConvertFrom-Json
$note = Join-Path $alfredRoot 'data\notes\notes.txt'
$noteDirectory = Split-Path $note
$timer = Join-Path $alfredRoot 'data\timers.txt'
$testNote = [IO.File]::ReadAllText($note).Contains('ALFRED TEMP TEST NOTE')
$timersActive = (Get-Content (Join-Path $alfredRoot 'data\status.json') -Raw | ConvertFrom-Json).timers -eq 0

if ($state.NoteFileExisted) {
    $backup = Join-Path $alfredTemp 'notes-before-test.bin'
    [IO.File]::WriteAllBytes($note, [IO.File]::ReadAllBytes($backup))
    $noteRestored = [Convert]::ToBase64String([IO.File]::ReadAllBytes($note)) -eq [Convert]::ToBase64String([IO.File]::ReadAllBytes($backup))
} else {
    Remove-Item -LiteralPath $note -ErrorAction SilentlyContinue
    $noteRestored = -not (Test-Path -LiteralPath $note)
}
if (-not $state.NoteDirectoryExisted -and (Test-Path -LiteralPath $noteDirectory) -and -not (Get-ChildItem -LiteralPath $noteDirectory -Force)) {
    Remove-Item -LiteralPath $noteDirectory -Force
}

if ($state.TimerFileExisted) {
    $backup = Join-Path $alfredTemp 'timers-before-test.bin'
    [IO.File]::WriteAllBytes($timer, [IO.File]::ReadAllBytes($backup))
    $timerRestored = [Convert]::ToBase64String([IO.File]::ReadAllBytes($timer)) -eq [Convert]::ToBase64String([IO.File]::ReadAllBytes($backup))
} else {
    Remove-Item -LiteralPath $timer -ErrorAction SilentlyContinue
    $timerRestored = -not (Test-Path -LiteralPath $timer)
}

$report = [pscustomobject]@{
    NoteSaveAction = $testNote
    NoteStateRestored = $noteRestored
    TimerStartedAndCanceled = $timersActive
    TimerStateRestored = $timerRestored
}
$report | ConvertTo-Json | Set-Content (Join-Path $alfredRoot 'build\live-note-timer-check.json')
$report | Format-List

Remove-Item -LiteralPath (Join-Path $alfredTemp 'notes-before-test.bin') -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $alfredTemp 'timers-before-test.bin') -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $alfredTemp 'test-restore-state.json') -ErrorAction SilentlyContinue
if (-not $noteRestored -or -not $timersActive -or -not $timerRestored) { throw 'Note/timer verification failed.' }
