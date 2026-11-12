# Alfred

Alfred is a lightweight Windows voice assistant. The application is a native WinForms/.NET Framework program; speech recognition uses Windows `System.Speech`, with Bengali transcription handled on demand by the bundled `whisper.cpp` runtime and local GGML models.

## Repository contents

- `Alfred.exe` — current application build.
- `Start-Alfred.vbs` — launcher.
- `build.ps1` and `source/` — build script and C# source.
- `engine/Release/` — native `whisper.cpp` runtime binaries used by Alfred.
- `models/` — the four local quantized Whisper models. Git LFS stores these binary files.
- `COMMANDS-BN.txt` — Bengali command guide.
- `PROGRESS.md` — project notes, validation history, and publishing preference.

## Requirements

- Windows x64.
- .NET Framework 4.x with the C# compiler and `System.Speech` assemblies used by `build.ps1`.
- Git LFS to clone or pull the model files.

Install Git LFS before cloning, then fetch LFS objects if needed:

```powershell
git lfs install
git lfs pull
```

## Build

From this folder in PowerShell:

```powershell
.\build.ps1
```

The script compiles `source/*.cs`, runs the app's built-in command checks, then replaces `Alfred.exe` and starts it. The script writes generated output under `build/` and temporary files under `temp/`.

## Local data and ignored files

`data/` stores local settings, notes, and other user state. `temp/` contains transient audio/transcription files. `build/` contains generated builds and test/evaluation artifacts, including large CUDA scratch downloads. These directories are intentionally ignored so personal recordings/notes and generated scratch are not uploaded.

## Model and dependency licensing

The model binaries and `engine/Release/` contain third-party artifacts. The local project folder did not include provenance/license records for every binary, especially the Bengali-tuned model. Check the upstream model card and notices before redistributing those artifacts. The Alfred source has no project-level `LICENSE` file, so repository visibility alone does not grant reuse rights.