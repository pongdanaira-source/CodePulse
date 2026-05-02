@echo off
setlocal EnableExtensions

title CodePulse Installer Build

set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..") do set "REPO_ROOT=%%~fI"
set "INSTALLER_DIR=%REPO_ROOT%\artifacts\installer"
set "INSTALLER_OUT="
set "PUSHD_OK=0"

echo.
echo CodePulse installer build
echo Repository: %REPO_ROOT%
echo.

pushd "%REPO_ROOT%" || goto fail
set "PUSHD_OK=1"

echo [1/4] Shutdown dotnet build servers
dotnet build-server shutdown
if errorlevel 1 (
    echo Warning: dotnet build-server shutdown returned an error. Continuing build.
)
echo.

echo [2/4] Test, publish, and compile installer
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%build-installer.ps1" %*
if errorlevel 1 goto fail
echo.

echo [3/4] Locate installer output
for /f "usebackq delims=" %%F in (`powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -LiteralPath '%INSTALLER_DIR%' -Filter 'CodePulse-Setup-*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName"`) do set "INSTALLER_OUT=%%F"

if not defined INSTALLER_OUT (
    echo Installer output was not found in: %INSTALLER_DIR%
    goto fail
)

if not exist "%INSTALLER_OUT%" (
    echo Installer path was detected but the file does not exist:
    echo %INSTALLER_OUT%
    goto fail
)
echo.

echo [4/4] Installer ready
for %%F in ("%INSTALLER_OUT%") do (
    echo File: %%~fF
    echo Size: %%~zF bytes
)
echo.

popd
echo Done.
if /i not "%CODEPULSE_NO_PAUSE%"=="1" pause
exit /b 0

:fail
echo.
echo Build failed. Check the messages above.
if "%PUSHD_OK%"=="1" (
    popd
)
if /i not "%CODEPULSE_NO_PAUSE%"=="1" pause
exit /b 1
