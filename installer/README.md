# Building the Alfred setup

The generated Windows x64 offline installer is saved under ../releases/. Release binaries are ignored by Git and should be uploaded as GitHub Release assets together with their .sha256 and manifest files. The source repository contains the reproducible installer scripts. No user notes, mic recordings, data, temp, build scratch, development backups or unknown-provenance fine-tuned model are included.

1. Run git lfs pull.
2. Install the official Inno Setup compiler (6.7.4+ recommended); this checkout uses F:/Alfred/build/tools/InnoSetup/ISCC.exe.
3. Ensure the Microsoft-signed VC++ x64 prerequisite exists at build/tools/vc_redist.x64.exe; package.ps1 downloads/verifies it if absent.
4. Run PowerShell: .\installer\package.ps1 -Version 2.0.1 (use -Compiler with another ISCC.exe path).

The package script builds the app, curates an allowlisted payload, validates the official small model hash and Microsoft signature, runs native checks on the payload, generates a file manifest, compiles the Setup EXE, then writes its SHA256 file. For packaging an already checked app use -SkipBuild.

Standard edition runs on CPU without NVIDIA. The advanced -WithCuda option is available for a separately reviewed CUDA edition; it includes the GPU runtime and Turbo model, substantially increases size and needs NVIDIA redistribution/license review before publication. Standard edition is the tested distributable.

Setup is per-user, supports folder selection, Start Menu/optional desktop shortcuts and uninstall. It installs VC++ only when needed (Windows may request administrator permission for that Microsoft system component). .NET 4.7.2+ is checked before installation. Same AppId/path supports upgrades. Existing settings/notes are preserved on upgrade and uninstall. No scheduled autostart and no silent cloud subscription.

The binary is unsigned until a real signing certificate is configured. Do not claim testing on every Windows PC; verify on a clean Windows VM before public production distribution.
