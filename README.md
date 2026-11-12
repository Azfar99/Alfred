# Alfred

Alfred is a lightweight Windows voice assistant. The application is a native WinForms/.NET Framework program; wake detection uses sherpa-onnx Zipformer int8 and Silero VAD. Multilingual transcription runs on demand through CUDA whisper.cpp with a verified large-v3-turbo Q5 model; a CPU fallback is retained. Windows System.Speech provides the installed TTS voice.

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
## Windows installer

A standard offline setup is built with installer/package.ps1 and saved under releases/. It supports Windows10 1809+/11 x64 with .NET4.7.2+. The setup includes the CPU speech model and voice runtime, plus the Microsoft-signed VC++ prerequisite. NVIDIA/Python/API keys are not required. This CPU edition has different speed/accuracy from the CUDA Turbo development build.

Download the *-Setup.exe from GitHub Releases after publication; the GitHub source ZIP is for development. Choose a writable install folder. Start Menu shortcut, optional desktop shortcut and uninstall are included. Notes/settings stay in the chosen folder and survive updates/uninstall. No boot autostart.

See installer/README.md for repeatable packaging. Generated binaries/checksums should be GitHub Release assets and are ignored by Git. This build is unsigned; clean-PC QA remains outstanding.