# CodePulse Installer

CodePulse ships as a self-contained Windows publish, so target machines do not need the .NET runtime installed separately.

## Portable package

Build a portable zip:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-portable-zip.ps1
```

Output:

```text
artifacts\package\CodePulse-portable.zip
```

## Setup.exe installer

Install Inno Setup 6 first:

```powershell
winget install JRSoftware.InnoSetup
```

Then build the installer:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1
```

Output:

```text
artifacts\installer\CodePulse-Setup-1.0.0.exe
```

The installer creates Start Menu shortcuts and can optionally create a Desktop shortcut.
