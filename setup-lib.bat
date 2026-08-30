@echo off
:: ============================================================
:: setup-lib.bat
:: Copies the four reference DLLs this project builds against into
:: lib\. They are copyrighted game / third-party files and are NOT
:: redistributed in this repo - lib\ is gitignored, and nothing this
:: script copies will ever be committed.
::
:: Three come from your RimWorld install, one from the Harmony
:: Workshop mod (id 2009463077).
::
:: Added 2026-08-29, when the repos were split one-per-mod and every
:: repo needed to be able to rebuild lib\ from scratch.
:: ============================================================

cd /d "%~dp0"

set "RWDIR=E:\SteamLibrary\steamapps\common\RimWorld"
set "WSDIR=E:\SteamLibrary\steamapps\workshop\content\294100"

echo Default RimWorld install: %RWDIR%
set /p "INPUT=Press Enter to accept, or type a different path: "
if not "%INPUT%"=="" set "RWDIR=%INPUT%"

set "MANAGED=%RWDIR%\RimWorldWin64_Data\Managed"
if not exist "%MANAGED%" (
    echo.
    echo ERROR: %MANAGED% not found.
    echo That does not look like a RimWorld install.
    pause
    exit /b 1
)

if not exist "lib" mkdir "lib"

for %%F in (Assembly-CSharp.dll UnityEngine.dll UnityEngine.CoreModule.dll) do (
    copy /y "%MANAGED%\%%F" "lib\%%F" >nul
    if errorlevel 1 (
        echo ERROR: could not copy %%F
        pause
        exit /b 1
    )
    echo   copied %%F
)

:: Harmony ships per-version folders; Current is the one to prefer
set "HARMONY=%WSDIR%\2009463077\Current\Assemblies\0Harmony.dll"
if not exist "%HARMONY%" set "HARMONY=%WSDIR%\2009463077\1.5\Assemblies\0Harmony.dll"

if exist "%HARMONY%" (
    copy /y "%HARMONY%" "lib\0Harmony.dll" >nul
    echo   copied 0Harmony.dll
) else (
    echo.
    echo WARNING: 0Harmony.dll not found under %WSDIR%\2009463077
    echo Subscribe to the Harmony mod on the Workshop, or copy the DLL
    echo into lib\ by hand. The build will fail without it.
)

echo.
echo Done. lib\ is gitignored - none of this will be committed.
pause
