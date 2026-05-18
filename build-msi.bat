@echo off
setlocal EnableExtensions

set "ROOT=%~dp0"
set "ROOT_ARG=%ROOT:~0,-1%"
set "APP_NAME=EasyProxy"
set "RID=win-x64"
set "CONFIGURATION=Release"
set "VERSION_FILE=%ROOT%installer\version.txt"
set "BUMP_KIND="
if "%NUGET_SOURCE%"=="" set "NUGET_SOURCE=https://api.nuget.org/v3/index.json"

if "%~1"=="" (
    call :choose_bump_kind
    if errorlevel 1 goto fail
    set "ARG_RID=%~2"
    set "ARG_CONFIGURATION=%~3"
) else if /i "%~1"=="patch" (
    set "BUMP_KIND=patch"
    set "ARG_RID=%~2"
    set "ARG_CONFIGURATION=%~3"
) else if /i "%~1"=="minor" (
    set "BUMP_KIND=minor"
    set "ARG_RID=%~2"
    set "ARG_CONFIGURATION=%~3"
) else if /i "%~1"=="major" (
    set "BUMP_KIND=major"
    set "ARG_RID=%~2"
    set "ARG_CONFIGURATION=%~3"
) else if "%~1"=="-h" (
    call :print_usage
    exit /b 0
) else if "%~1"=="--help" (
    call :print_usage
    exit /b 0
) else if "%~1"=="/?" (
    call :print_usage
    exit /b 0
) else (
    echo ERROR: Invalid change type: %~1
    echo.
    call :print_usage
    exit /b 1
)

if not "%ARG_RID%"=="" set "RID=%ARG_RID%"
if not "%ARG_CONFIGURATION%"=="" set "CONFIGURATION=%ARG_CONFIGURATION%"

if /i not "%RID%"=="win-x64" (
    echo ERROR: MSI packaging currently supports win-x64 only.
    goto fail
)

set "PUBLISH_DIR=%ROOT%dist\%APP_NAME%-%RID%"
set "MSI_DIR=%ROOT%dist\installer"
set "DOTNET_EXE=dotnet"
if exist "%ROOT%.dotnet\dotnet.exe" set "DOTNET_EXE=%ROOT%.dotnet\dotnet.exe"

pushd "%ROOT%" >nul
if errorlevel 1 goto fail

call :load_version
if errorlevel 1 goto fail

set "MSI_PATH=%MSI_DIR%\%APP_NAME%-%PRODUCT_VERSION%-%RID%.msi"

echo.
echo %APP_NAME% MSI build
echo Version : %PRODUCT_VERSION%
echo Next    : %NEXT_PRODUCT_VERSION%
echo Bump    : %BUMP_KIND%
echo Runtime : %RID%
echo Config  : %CONFIGURATION%
echo Output  : %MSI_PATH%
echo.

set "NO_PAUSE=1"
call "%ROOT%build-exe.bat" "%RID%" "%CONFIGURATION%"
if errorlevel 1 goto fail

if not exist "%PUBLISH_DIR%\%APP_NAME%.exe" (
    echo ERROR: Missing %PUBLISH_DIR%\%APP_NAME%.exe
    goto fail
)

if not exist "%PUBLISH_DIR%\app-proxy.example.json" (
    echo ERROR: Missing %PUBLISH_DIR%\app-proxy.example.json
    goto fail
)

if not exist "%MSI_DIR%" mkdir "%MSI_DIR%"
if errorlevel 1 goto fail

echo Restoring WiX local tool...
"%DOTNET_EXE%" tool restore --add-source "%NUGET_SOURCE%"
if errorlevel 1 goto fail

echo Restoring WiX UI extension...
"%DOTNET_EXE%" wix extension add WixToolset.UI.wixext/6.0.2
if errorlevel 1 goto fail

echo Building MSI...
"%DOTNET_EXE%" wix build "%ROOT%installer\AppProxyHelper.wxs" ^
    -arch x64 ^
    -ext WixToolset.UI.wixext ^
    -culture zh-CN ^
    -d "PublishDir=%PUBLISH_DIR%" ^
    -d "ProductVersion=%PRODUCT_VERSION%" ^
    -o "%MSI_PATH%"
if errorlevel 1 goto fail

if not exist "%MSI_PATH%" (
    echo ERROR: MSI build finished but output was not found.
    goto fail
)

> "%VERSION_FILE%" echo %NEXT_PRODUCT_VERSION%
if errorlevel 1 goto fail

echo.
echo Done.
echo MSI: %MSI_PATH%
echo Next version: %NEXT_PRODUCT_VERSION%
goto end

:fail
echo.
echo MSI build failed.
popd >nul 2>nul
if /i not "%NO_PAUSE%"=="1" pause
exit /b 1

:end
popd >nul
if /i not "%NO_PAUSE%"=="1" pause
exit /b 0

:choose_bump_kind
echo.
echo Select this package's change type:
echo   1. patch - fixes, packaging-only changes, or small compatible updates
echo   2. minor - new compatible functionality
echo   3. major - breaking or migration-requiring changes
echo.
choice /C 123 /N /M "Choose 1, 2, or 3: "
if errorlevel 3 (
    set "BUMP_KIND=major"
    exit /b 0
)
if errorlevel 2 (
    set "BUMP_KIND=minor"
    exit /b 0
)
if errorlevel 1 (
    set "BUMP_KIND=patch"
    exit /b 0
)
exit /b 1

:print_usage
echo Usage:
echo   build-msi.bat
echo   build-msi.bat patch [win-x64] [Release]
echo   build-msi.bat minor [win-x64] [Release]
echo   build-msi.bat major [win-x64] [Release]
echo.
echo Version selection:
echo   patch  fixes, packaging-only changes, or small compatible updates
echo   minor  new compatible functionality
echo   major  breaking or migration-requiring changes
echo.
echo The script never accepts a manual version number. It reads installer\version.txt
echo and advances it only after a successful MSI build.
exit /b 0

:load_version
set "PRODUCT_VERSION="
if exist "%VERSION_FILE%" set /p PRODUCT_VERSION=<"%VERSION_FILE%"
if "%PRODUCT_VERSION%"=="" set "PRODUCT_VERSION=1.0.0"

set "VERSION_MAJOR="
set "VERSION_MINOR="
set "VERSION_PATCH="
set "VERSION_EXTRA="
for /f "tokens=1-4 delims=." %%A in ("%PRODUCT_VERSION%") do (
    set "VERSION_MAJOR=%%A"
    set "VERSION_MINOR=%%B"
    set "VERSION_PATCH=%%C"
    set "VERSION_EXTRA=%%D"
)

set "VERSION_INVALID="
if "%VERSION_MAJOR%"=="" set "VERSION_INVALID=1"
if "%VERSION_MINOR%"=="" set "VERSION_INVALID=1"
if "%VERSION_PATCH%"=="" set "VERSION_INVALID=1"
if not "%VERSION_EXTRA%"=="" set "VERSION_INVALID=1"
for /f "delims=0123456789" %%A in ("%VERSION_MAJOR%") do set "VERSION_INVALID=1"
for /f "delims=0123456789" %%A in ("%VERSION_MINOR%") do set "VERSION_INVALID=1"
for /f "delims=0123456789" %%A in ("%VERSION_PATCH%") do set "VERSION_INVALID=1"
if not "%VERSION_INVALID%"=="" (
    echo ERROR: Invalid MSI version in %VERSION_FILE%: %PRODUCT_VERSION%
    echo Expected a numeric version like 1.0.0.
    exit /b 1
)

set /a NEXT_PATCH=%VERSION_PATCH% + 1 >nul
set "NEXT_PRODUCT_VERSION=%VERSION_MAJOR%.%VERSION_MINOR%.%NEXT_PATCH%"
if /i "%BUMP_KIND%"=="patch" exit /b 0

if /i "%BUMP_KIND%"=="minor" goto version_minor
if /i "%BUMP_KIND%"=="major" goto version_major

echo ERROR: Invalid version bump kind: %BUMP_KIND%
echo Expected patch, minor, or major.
exit /b 1

:version_minor
set /a PRODUCT_MINOR=%VERSION_MINOR% + 1 >nul
set "PRODUCT_VERSION=%VERSION_MAJOR%.%PRODUCT_MINOR%.0"
set "NEXT_PRODUCT_VERSION=%VERSION_MAJOR%.%PRODUCT_MINOR%.1"
exit /b 0

:version_major
set /a PRODUCT_MAJOR=%VERSION_MAJOR% + 1 >nul
set "PRODUCT_VERSION=%PRODUCT_MAJOR%.0.0"
set "NEXT_PRODUCT_VERSION=%PRODUCT_MAJOR%.0.1"
exit /b 0
