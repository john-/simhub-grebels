@echo off
setlocal

:: 1. Check command-line argument: deploy.bat "D:\SimHub"
if not "%~1"=="" (
    set "SIMHUB_DIR=%~1"
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
    exit /b 1
)

echo [G-Rebels] Target SimHub directory: "%SIMHUB_DIR%"

echo [G-Rebels] Building Release configuration...
dotnet build -c Release -p:SimHubPath="%SIMHUB_DIR%" >nul 2>&1
if %ERRORLEVEL% neq 0 (
    if exist "bin\Release\SimHub.GRebels.dll" (
        echo [WARN] Windows .NET SDK not detected.
        echo [INFO] Deploying existing pre-compiled binaries from bin\Release...
    ) else (
        echo [ERROR] Build failed and no pre-compiled binaries found in bin\Release.
        exit /b %ERRORLEVEL%
    )
)

echo [G-Rebels] Deploying to "%SIMHUB_DIR%"...
copy /y "bin\Release\SimHub.GRebels.dll" "%SIMHUB_DIR%\"
copy /y "bin\Release\SimHub.GRebels.pdb" "%SIMHUB_DIR%\"
copy /y "bin\Release\SimHub.GRebels.json" "%SIMHUB_DIR%\"

echo [G-Rebels] Deployment complete!
endlocal
