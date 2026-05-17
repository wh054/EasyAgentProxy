@echo off
setlocal EnableExtensions

set "ROOT=%~dp0"
set "ROOT_ARG=%ROOT:~0,-1%"
set "PROJECT=%ROOT%AppProxyHelper\AppProxyHelper.csproj"
set "APP_NAME=EasyProxy"
set "CONFIGURATION=Release"
set "RID=win-x64"
if "%NUGET_SOURCE%"=="" set "NUGET_SOURCE=https://api.nuget.org/v3/index.json"

if not "%~1"=="" set "RID=%~1"
if not "%~2"=="" set "CONFIGURATION=%~2"

set "DIST=%ROOT%dist\%APP_NAME%-%RID%"
set "DOTNET_EXE=dotnet"
if exist "%ROOT%.dotnet\dotnet.exe" set "DOTNET_EXE=%ROOT%.dotnet\dotnet.exe"

pushd "%ROOT%" >nul
if errorlevel 1 goto fail

echo.
echo %APP_NAME% single-file publish
echo Project : %PROJECT%
echo Runtime : %RID%
echo Config  : %CONFIGURATION%
echo Output  : %DIST%
echo NuGet   : %NUGET_SOURCE%
echo.

"%DOTNET_EXE%" --version >nul 2>nul
if errorlevel 1 (
    echo ERROR: .NET SDK was not found.
    echo Install .NET 7 SDK, or place dotnet.exe at .dotnet\dotnet.exe.
    goto fail
)

if not exist "%DIST%" mkdir "%DIST%"
if errorlevel 1 goto fail

echo Restoring...
"%DOTNET_EXE%" restore "%PROJECT%" --runtime "%RID%" --source "%NUGET_SOURCE%"
if errorlevel 1 goto fail

echo Publishing...
"%DOTNET_EXE%" publish "%PROJECT%" ^
    --configuration "%CONFIGURATION%" ^
    --runtime "%RID%" ^
    --self-contained true ^
    --no-restore ^
    --output "%DIST%" ^
    -p:PublishSingleFile=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=None ^
    -p:DebugSymbols=false
if errorlevel 1 goto fail

if exist "%ROOT%app-proxy.example.json" (
    copy /y "%ROOT%app-proxy.example.json" "%DIST%\app-proxy.example.json" >nul
)

echo Preparing WinDivert...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\prepare-windivert.ps1" ^
    -Root "%ROOT_ARG%" ^
    -RuntimeIdentifier "%RID%" ^
    -OutputDirectory "%DIST%"
if errorlevel 1 goto fail

if not exist "%DIST%\WinDivert.dll" (
    echo ERROR: WinDivert.dll was not copied to the output root.
    goto fail
)

if /i "%RID%"=="win-x64" (
    if not exist "%DIST%\WinDivert64.sys" (
        echo ERROR: WinDivert64.sys was not copied to the output root.
        goto fail
    )
)

if /i "%RID%"=="win-x86" (
    if not exist "%DIST%\WinDivert32.sys" (
        echo ERROR: WinDivert32.sys was not copied to the output root.
        goto fail
    )
)

if not exist "%DIST%\%APP_NAME%.exe" (
    echo ERROR: publish finished but %APP_NAME%.exe was not found.
    goto fail
)

echo.
echo Done.
echo EXE: %DIST%\%APP_NAME%.exe
goto end

:fail
echo.
echo Build failed.
popd >nul 2>nul
if /i not "%NO_PAUSE%"=="1" pause
exit /b 1

:end
popd >nul 2>nul
if /i not "%NO_PAUSE%"=="1" pause
exit /b 0
