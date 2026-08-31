@echo off
setlocal EnableExtensions EnableDelayedExpansion

cd /d "%~dp0"

set "DOTNET=C:\Program Files\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"

set "MSBuildSDKsPath="

set "VERSION_FILE=version.txt"
set "VERSION="
if exist "%VERSION_FILE%" (
    set /p VERSION=<"%VERSION_FILE%"
)
if "%VERSION%"=="" if exist "..\version.txt" (
    set /p VERSION=<"..\version.txt"
)
if "%VERSION%"=="" set "VERSION=local"

set "ROOT=%CD%"
set "PUBLISH_DIR=%ROOT%\artifacts\publish\ForgeFlow_Lite_%VERSION%"
set "STAGING_DIR=%ROOT%\artifacts\installer\staging"
set "INSTALLER_PUBLISH_DIR=%ROOT%\artifacts\installer\publish\%VERSION%"
set "SETUP_DIR=%ROOT%\artifacts\setup"
set "PAYLOAD_ZIP=%STAGING_DIR%\payload.zip"
set "FINAL_SETUP=%SETUP_DIR%\ForgeFlow_Lite_Setup_%VERSION%.exe"
set "SETUP_EXE=%INSTALLER_PUBLISH_DIR%\ForgeFlow_Lite_Setup_%VERSION%.exe"

echo.
echo ForgeFlow Lite build
echo Version: %VERSION%
echo.

if not exist "ForgeFlow.csproj" (
    echo ERROR: ForgeFlow.csproj was not found.
    pause
    exit /b 1
)

if not exist "Installer\ForgeFlowSetup\ForgeFlowSetup.csproj" (
    echo ERROR: Installer\ForgeFlowSetup\ForgeFlowSetup.csproj was not found.
    pause
    exit /b 1
)

echo Cleaning output folders...
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if exist "%STAGING_DIR%" rmdir /s /q "%STAGING_DIR%"
if exist "%INSTALLER_PUBLISH_DIR%" rmdir /s /q "%INSTALLER_PUBLISH_DIR%"
mkdir "%PUBLISH_DIR%" >nul 2>nul
mkdir "%STAGING_DIR%" >nul 2>nul
mkdir "%INSTALLER_PUBLISH_DIR%" >nul 2>nul
mkdir "%SETUP_DIR%" >nul 2>nul

echo.
echo Building app...
"%DOTNET%" clean ForgeFlow.csproj -c Release -p:NuGetAudit=false -v:minimal
if errorlevel 1 goto fail

"%DOTNET%" build ForgeFlow.csproj -c Release -p:NuGetAudit=false -v:minimal
if errorlevel 1 goto fail

echo.
echo Publishing app payload...
"%DOTNET%" publish ForgeFlow.csproj -c Release -r win-x64 --self-contained true -p:NuGetAudit=false -o "%PUBLISH_DIR%" -v:minimal
if errorlevel 1 goto fail

echo.
echo Creating payload.zip...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%PUBLISH_DIR%\*' -DestinationPath '%PAYLOAD_ZIP%' -CompressionLevel Optimal -Force"
if errorlevel 1 goto fail

if not exist "%PAYLOAD_ZIP%" (
    echo ERROR: payload.zip was not created.
    goto fail
)

echo.
echo Building setup EXE...
"%DOTNET%" publish Installer\ForgeFlowSetup\ForgeFlowSetup.csproj -c Release -r win-x64 --self-contained true -p:NuGetAudit=false -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o "%INSTALLER_PUBLISH_DIR%" -v:minimal
if errorlevel 1 goto fail

if not exist "%SETUP_EXE%" (
    echo ERROR: Setup EXE was not created:
    echo %SETUP_EXE%
    goto fail
)

copy /y "%SETUP_EXE%" "%FINAL_SETUP%" >nul
if errorlevel 1 goto fail

echo.
echo Done.
echo Setup:
echo %FINAL_SETUP%
echo.
certutil -hashfile "%FINAL_SETUP%" SHA256
if errorlevel 1 (
    echo WARNING: Setup was built, but SHA256 could not be displayed.
)
pause
exit /b 0

:fail
echo.
echo Build failed.
pause
exit /b 1
