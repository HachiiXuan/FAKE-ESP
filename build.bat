@echo off
rem Rebuild FAKE ESP. Fully offline: the project uses zero NuGet packages.
rem Usually you do NOT need this - FakeEsp.exe is already built and in the project root.
rem
rem ASCII-ONLY CONTENT ON PURPOSE: cmd parses .bat bytes in the console codepage,
rem so non-ASCII characters here (even inside rem comments) can break parsing.
setlocal
cd /d "%~dp0"

set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
set DOTNET_CLI_HOME=%~dp0_dev\.dotnet-home

echo Building FAKE ESP ...
dotnet build "src\FakeEspBridge.csproj" -c Release -v q --nologo
if errorlevel 1 goto failed

rem Keep the runnable copy in the project root so you only ever double-click one file.
copy /y "src\bin\Release\net9.0-windows\FakeEsp.exe"                "FakeEsp.exe"                >nul
copy /y "src\bin\Release\net9.0-windows\FakeEsp.dll"                "FakeEsp.dll"                >nul
copy /y "src\bin\Release\net9.0-windows\FakeEsp.deps.json"          "FakeEsp.deps.json"          >nul
copy /y "src\bin\Release\net9.0-windows\FakeEsp.runtimeconfig.json" "FakeEsp.runtimeconfig.json" >nul

rem Rebuilding invalidates the previous Authenticode signature, so re-sign here.
rem The signing script lives in _dev\ (not uploaded to git); if it is missing we
rem simply skip this step, so people without a certificate can still build.
rem Prefer PowerShell 7 when present: the signing cmdlets live in
rem Microsoft.PowerShell.Security, which does not always load under 5.1.
set SIGNPS=powershell
where pwsh >nul 2>&1 && set SIGNPS=pwsh

if exist "_dev\tools\sign-exe.ps1" (
  echo.
  echo Signing FakeEsp.exe ...
  "%SIGNPS%" -NoProfile -ExecutionPolicy Bypass -File "_dev\tools\sign-exe.ps1" -Exe "FakeEsp.exe"
  if errorlevel 1 echo   (signing failed - the exe still runs, it is just unsigned)
)

echo.
echo BUILD OK -^> FakeEsp.exe (in this folder)
exit /b 0

:failed
echo.
echo BUILD FAILED
pause
exit /b 1
