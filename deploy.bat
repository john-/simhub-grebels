@echo off
setlocal
pushd "%~dp0"

:: Usage: deploy.bat [SimHub dir] [--prebuilt | --build] [--allow-stale] [-h | --help | /?]
::   (default)      build if a Windows .NET SDK exists, otherwise use bin\Release as built elsewhere (e.g. WSL)
::   --prebuilt     never build; deploy bin\Release (the WSL build, then deploy from Windows workflow)
::   --build        require a Windows .NET SDK and build here
::   --allow-stale  deploy prebuilt binaries even if source files are newer than the DLL
::   -h, --help, /? show this help message
set "MODE=auto"
set "ALLOW_STALE=0"
set "SIMHUB_ARG="

for %%A in (%*) do (
    if /i "%%~A"=="--help" goto :help
    if /i "%%~A"=="-h" goto :help
    if "%%~A"=="/?" goto :help
    if /i "%%~A"=="--prebuilt" (
        set "MODE=prebuilt"
    ) else if /i "%%~A"=="--build" (
        set "MODE=build"
    ) else if /i "%%~A"=="--allow-stale" (
        set "ALLOW_STALE=1"
    ) else (
        if not defined SIMHUB_ARG (
            set "ARG_CANDIDATE=%%~A"
            call :check_arg
        )
    )
)
goto :after_args

:check_arg
if not "%ARG_CANDIDATE:~0,2%"=="--" set "SIMHUB_ARG=%ARG_CANDIDATE%"
goto :eof

:after_args

:: 1. SimHub directory from the command line
if defined SIMHUB_ARG (
    set "SIMHUB_DIR=%SIMHUB_ARG%"
    goto :validate
)

:: 2. Check if already defined in environment
if defined SIMHUB_DIR goto :validate

:: 3. Check common locations
for %%P in (
    "C:\Program Files (x86)\SimHub"
    "C:\Program Files\SimHub"
    "D:\SimHub"
    "D:\Program Files (x86)\SimHub"
) do (
    if exist "%%~P" (
        set "SIMHUB_DIR=%%~P"
        goto :validate
    )
)

:: 4. Prompt user if still not found
echo [WARN] SimHub not found in standard locations.
set /p "SIMHUB_DIR=Enter SimHub directory: "

:validate
:: Strip quotes if entered by user
set "SIMHUB_DIR=%SIMHUB_DIR:"=%"

if not exist "%SIMHUB_DIR%" (
    echo [ERROR] SimHub directory not found: "%SIMHUB_DIR%"
    goto :fail
)

:: Strip a trailing backslash so it cannot escape the closing quote in -p:SimHubPath="..."
if "%SIMHUB_DIR:~-1%"=="\" set "SIMHUB_DIR=%SIMHUB_DIR:~0,-1%"

echo [G-Rebels] Target SimHub directory: "%SIMHUB_DIR%"

:: Version label (display only; a prebuilt DLL carries the version it was built with)
set "PLUGIN_VER="
for /f "tokens=*" %%v in ('git describe --tags --always --dirty 2^>nul') do set "PLUGIN_VER=%%v"
if not defined PLUGIN_VER (
    for /f "tokens=3 delims=<>" %%v in ('findstr "<Version>" SimHub.GRebels.csproj 2^>nul') do set "PLUGIN_VER=v%%v"
)
if not defined PLUGIN_VER set "PLUGIN_VER=unknown"
echo [G-Rebels] Plugin Version: "%PLUGIN_VER%"

:: Check if SimHub is currently running
tasklist /fi "imagename eq SimHubWPF.exe" 2>nul | find /i "SimHubWPF.exe" >nul
if %ERRORLEVEL% equ 0 (
    echo [ERROR] SimHub is currently running!
    echo [ERROR] Running SimHub locks plugin files. Please close SimHub completely and run deploy.bat again.
    goto :fail
)

:: ---- Choose build or prebuilt ----
if /i "%MODE%"=="prebuilt" goto :prebuilt

:: A real SDK prints version lines; a runtime-only install prints nothing useful.
set "HAVE_SDK=0"
for /f "delims=" %%S in ('dotnet --list-sdks 2^>nul ^| findstr /r "^[0-9]"') do set "HAVE_SDK=1"
if "%HAVE_SDK%"=="1" goto :build

if /i "%MODE%"=="build" (
    echo [ERROR] --build requested but no .NET SDK was found on the Windows PATH.
    goto :fail
)
echo [G-Rebels] No Windows .NET SDK found. Using binaries built elsewhere, such as WSL.
goto :prebuilt

:build
echo [G-Rebels] Building Release configuration...
dotnet build -c Release -p:SimHubPath="%SIMHUB_DIR%" -p:InformationalVersion="%PLUGIN_VER%"
if errorlevel 1 (
    echo [ERROR] Build failed. Nothing was deployed.
    goto :fail
)
goto :backup

:prebuilt
if not exist "bin\Release\SimHub.GRebels.dll" (
    echo [ERROR] bin\Release\SimHub.GRebels.dll not found.
    echo [ERROR] Build it first, for example in WSL: dotnet build -c Release
    goto :fail
)
echo [G-Rebels] Deploying pre-built binaries from bin\Release.
if "%ALLOW_STALE%"=="1" goto :backup

:: Refuse to deploy a DLL older than the source it came from.
powershell -NoProfile -Command "$d=(Get-Item 'bin\Release\SimHub.GRebels.dll').LastWriteTimeUtc; $n=Get-ChildItem -Path . -Recurse -File -Include *.cs,*.csproj,*.json | Where-Object { $_.FullName -notmatch '\\(bin|obj|SimHub\.GRebels\.Tests|\.git)\\' -and $_.LastWriteTimeUtc -gt $d }; if ($n) { $n | ForEach-Object { Write-Host ('  newer than DLL: ' + $_.Name) }; exit 1 }"
if errorlevel 1 (
    echo [ERROR] Source files are newer than the pre-built DLL. Rebuild it, or pass --allow-stale.
    goto :fail
)

:backup
:: Back up whatever is currently installed, including a customised JSON config.
:: One backup per file, overwritten on each deploy: SimHub.GRebels.dll.bak and so on.
echo [G-Rebels] Backing up existing files...
for %%F in (SimHub.GRebels.dll SimHub.GRebels.pdb SimHub.GRebels.json) do (
    if exist "%SIMHUB_DIR%\%%F" (
        copy /y "%SIMHUB_DIR%\%%F" "%SIMHUB_DIR%\%%F.bak" >nul
        if errorlevel 1 (
            echo [ERROR] Could not back up %%F. Nothing was deployed.
            goto :fail
        )
    )
)

echo [G-Rebels] Deploying to "%SIMHUB_DIR%"...
copy /y "bin\Release\SimHub.GRebels.dll" "%SIMHUB_DIR%\" >nul
if errorlevel 1 (
    echo [ERROR] Failed to deploy SimHub.GRebels.dll. The file may be in use or permission denied.
    goto :fail
)

if exist "bin\Release\SimHub.GRebels.pdb" (
    copy /y "bin\Release\SimHub.GRebels.pdb" "%SIMHUB_DIR%\" >nul
    if errorlevel 1 (
        echo [ERROR] Failed to deploy SimHub.GRebels.pdb.
        goto :fail
    )
)

copy /y "bin\Release\SimHub.GRebels.json" "%SIMHUB_DIR%\" >nul
if errorlevel 1 (
    echo [ERROR] Failed to deploy SimHub.GRebels.json.
    goto :fail
)

echo [G-Rebels] Deployment complete. Previous files saved as *.bak in "%SIMHUB_DIR%".
popd
endlocal
exit /b 0

:help
echo Usage: deploy.bat [SimHub dir] [--prebuilt ^| --build] [--allow-stale]
echo.
echo Options:
echo   (default)      Build if Windows .NET SDK exists, otherwise deploy bin\Release
echo   --prebuilt     Never build; deploy prebuilt bin\Release (WSL build -^> Windows deploy)
echo   --build        Require a Windows .NET SDK and build Release configuration
echo   --allow-stale  Deploy prebuilt binaries even if source files are newer than DLL
echo   -h, --help, /? Display this help message
echo.
echo Examples:
echo   deploy.bat
echo   deploy.bat --prebuilt
echo   deploy.bat "C:\Program Files (x86)\SimHub" --prebuilt
echo   deploy.bat "D:\SimHub" --build
popd
endlocal
exit /b 0

:fail
popd
endlocal
exit /b 1
