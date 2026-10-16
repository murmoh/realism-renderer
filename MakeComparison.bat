@echo off
rem Comparison video with a sliding divider: Minecraft on the left, Realism Renderer on the right.
rem Drag ONE side-by-side recording (both windows next to each other; black borders are cropped off) onto this file,
rem or TWO recordings (Minecraft first, then Realism). The result is saved next to the first video as *_comparison.mp4.
if "%~1"=="" (
  echo Drag your recording onto MakeComparison.bat to make the comparison video.
  pause
  exit /b 1
)
set "DLL=%~dp0Realism\bin\Release\net10.0\Realism.dll"
if not exist "%DLL%" (
  echo Building Realism first...
  dotnet build "%~dp0Realism\Realism.csproj" -c Release -v quiet
)
set "SWAP="
if "%~2"=="" (
  choice /c LR /n /m "In your recording, is Minecraft on the Left or the Right? [L/R] "
  if errorlevel 2 set "SWAP=--swap"
)
dotnet "%DLL%" --compare %* %SWAP%
pause
