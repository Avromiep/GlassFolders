@echo off
REM Build the NativeAOT GFOpen launcher and copy it to dist\GFOpen.exe.
REM NativeAOT's link step needs the MSVC toolchain, so we run inside a VS dev environment.
REM Requires the VS "Desktop development with C++" build tools installed on this machine.
setlocal
set "REPO=%~dp0.."

REM Put vswhere on PATH (the AOT targets shell out to it) and initialise the x64 dev env.
set "PATH=C:\Program Files (x86)\Microsoft Visual Studio\Installer;%PATH%"
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 ( echo [build-gfopen] vcvars64 failed - is "Desktop development with C++" installed? & exit /b 1 )

"C:\Program Files\dotnet\dotnet.exe" publish "%REPO%\src\GFOpen\GFOpen.csproj" -c Release -r win-x64 -o "%REPO%\src\GFOpen\bin\aot" -v m
if errorlevel 1 ( echo [build-gfopen] publish failed & exit /b 1 )
if not exist "%REPO%\src\GFOpen\bin\aot\GFOpen.exe" ( echo [build-gfopen] native exe not produced & exit /b 1 )

copy /Y "%REPO%\src\GFOpen\bin\aot\GFOpen.exe" "%REPO%\dist\GFOpen.exe"
if errorlevel 1 ( echo [build-gfopen] copy to dist failed & exit /b 1 )
echo [build-gfopen] OK -^> dist\GFOpen.exe
endlocal
