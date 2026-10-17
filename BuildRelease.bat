@echo off
rem Builds the player download: release\RealismRenderer-<version>.zip, holding the RealismRenderer folder with
rem RealismRenderer.exe (self-contained, no .NET install needed), glfw3.dll (Silk.NET only loads it from next to the
rem exe) and mods\ with the bridge mod and Fabric API. On start the exe installs those into the launcher's Fabric
rem 1.21.5 profiles (Realism/Live/ModInstaller.cs), so players only need Fabric Loader and this zip.
setlocal
set "ROOT=%~dp0"
set "OUT=%ROOT%release"
set "APP=%OUT%\RealismRenderer"
for /f "tokens=2 delims==" %%v in ('findstr /b "mod_version=" "%ROOT%MinecraftMod\gradle.properties"') do set "VERSION=%%v"
for /f "tokens=2 delims==" %%v in ('findstr /b "fabric_version=" "%ROOT%MinecraftMod\gradle.properties"') do set "FABRIC_API=%%v"
if exist "%OUT%" rmdir /s /q "%OUT%"
mkdir "%APP%\mods"

echo Building RealismRenderer.exe...
dotnet publish "%ROOT%Realism\Realism.csproj" -c Release -r win-x64 --self-contained true -o "%APP%" ^
  -p:AssemblyName=RealismRenderer -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false ^
  -p:DebugType=none -v quiet
if errorlevel 1 goto failed

echo Building the mod jar...
pushd "%ROOT%MinecraftMod"
call "%ROOT%MinecraftMod\gradlew.bat" build -q
set "ERR=%errorlevel%"
popd
if not "%ERR%"=="0" goto failed
for %%f in ("%ROOT%MinecraftMod\build\libs\*.jar") do (
  echo %%~nf | findstr /i /c:"-sources" >nul || copy /y "%%f" "%APP%\mods" >nul
)

echo Downloading Fabric API %FABRIC_API%...
curl -sSfL -o "%APP%\mods\fabric-api-%FABRIC_API%.jar" ^
  "https://maven.fabricmc.net/net/fabricmc/fabric-api/fabric-api/%FABRIC_API%/fabric-api-%FABRIC_API%.jar"
if errorlevel 1 goto failed

(
  echo Realism Renderer %VERSION%
  echo ======================
  echo.
  echo See your Minecraft world the way it would look in real life, live, while you play.
  echo.
  echo 1. Install Fabric Loader for Minecraft 1.21.5 ^(once^): https://fabricmc.net/use/installer/
  echo 2. Double-click RealismRenderer.exe. The first time, it offers to install its mod and Fabric API into your
  echo    Fabric 1.21.5 profiles: press Enter to accept.
  echo 3. Start Minecraft from your launcher with the Fabric 1.21.5 profile, signed in as usual, and open a world.
  echo    The Realism window follows your game. You can start Minecraft and Realism Renderer in either order.
  echo.
  echo In the Realism window: click to play, Esc releases the mouse, F11 fullscreen, F8 hides/shows Minecraft's window,
  echo F6 free camera, F7 realistic animals, F2 screenshot. Settings: Mods ^> Realism Renderer ^(needs Mod Menu^).
  echo.
  echo Keep everything in this folder together. Needs Windows 10 or 11 and a GPU with OpenGL 3.3 ^(a dedicated GPU
  echo is recommended^). Windows may warn about an unknown app the first time: click "More info", then "Run anyway".
  echo.
  echo Fabric API ^(mods\fabric-api-*.jar^) is by FabricMC, under the Apache License 2.0.
) > "%APP%\HOW TO PLAY.txt"

echo Zipping...
rem Windows' own tar (it writes zips); Git's GNU tar, first on the PATH in Git Bash, can't.
"%SystemRoot%\System32\tar.exe" -a -c -f "%OUT%\RealismRenderer-%VERSION%.zip" -C "%OUT%" RealismRenderer
if errorlevel 1 goto failed

echo.
echo Done: %OUT%\RealismRenderer-%VERSION%.zip
dir /b /s "%APP%"
exit /b 0

:failed
echo Build failed.
exit /b 1
