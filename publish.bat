@echo off
setlocal

echo.
echo =================================================
echo   CodeReviewAgent - Publish Standalone EXE
echo =================================================
echo.

:: ?? Publish as self-contained single-file executable ??????????
echo Publishing for Windows x64...
echo.

pushd CodeReviewAgent
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o ..\publish\win-x64
set EXIT_CODE=%ERRORLEVEL%
popd

if %EXIT_CODE% neq 0 (
    echo.
    echo ERROR: Publish failed.
    pause
    exit /b 1
)

:: ?? Copy supporting files to publish folder ???????????????????
copy CodeReviewAgent\appsettings.template.json publish\win-x64\appsettings.template.json >nul 2>&1
copy CodeReviewAgent\rules.md publish\win-x64\rules.md >nul 2>&1

:: ?? Create appsettings.json if not present in publish folder ??
if not exist "publish\win-x64\appsettings.json" (
    copy CodeReviewAgent\appsettings.template.json publish\win-x64\appsettings.json >nul 2>&1
)

echo.
echo =================================================
echo   Published successfully!
echo =================================================
echo.
echo Output folder: %CD%\publish\win-x64\
echo.
echo Contents:
dir /b publish\win-x64\
echo.
echo -------------------------------------------------
echo   How to use the EXE:
echo -------------------------------------------------
echo.
echo   1. Edit publish\win-x64\appsettings.json
echo      and set your GitHubToken.
echo.
echo   2. Run:
echo      cd publish\win-x64
echo      CodeReviewAgent.exe ^
echo        --repoPath "C:\path\to\repo" ^
echo        --sourceBranch "feature-branch" ^
echo        --targetBranch "main" ^
echo        --rulesPath ".\rules.md"
echo.
echo   No .NET SDK required on the target machine.
echo -------------------------------------------------
echo.
pause
