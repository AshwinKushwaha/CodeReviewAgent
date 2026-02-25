@echo off
setlocal enabledelayedexpansion

echo.
echo =================================================
echo   CodeReviewAgent - AI Code Review
echo =================================================
echo.

:: ?? Check if appsettings.json exists ??????????????????????????
if not exist "CodeReviewAgent\appsettings.json" (
    echo ERROR: appsettings.json not found.
    echo Run setup.ps1 first or copy appsettings.template.json to appsettings.json
    echo and add your GitHub token.
    echo.
    pause
    exit /b 1
)

:: ?? Collect parameters ????????????????????????????????????????
set /p REPO_PATH="Enter local repository path: "
if "!REPO_PATH!"=="" (
    echo ERROR: Repository path is required.
    pause
    exit /b 1
)

set /p SOURCE_BRANCH="Enter source branch (merge from): "
if "!SOURCE_BRANCH!"=="" (
    echo ERROR: Source branch is required.
    pause
    exit /b 1
)

set /p TARGET_BRANCH="Enter target branch (merge into): "
if "!TARGET_BRANCH!"=="" (
    echo ERROR: Target branch is required.
    pause
    exit /b 1
)

set /p RULES_PATH="Enter rules file path (press Enter for default ./rules.md): "
if "!RULES_PATH!"=="" set RULES_PATH=.\rules.md

:: ?? Verify rules file exists ??????????????????????????????????
if not exist "CodeReviewAgent\!RULES_PATH!" (
    if not exist "!RULES_PATH!" (
        echo ERROR: Rules file not found at: !RULES_PATH!
        pause
        exit /b 1
    )
)

echo.
echo -------------------------------------------------
echo   Repo   : !REPO_PATH!
echo   Source : !SOURCE_BRANCH!
echo   Target : !TARGET_BRANCH!
echo   Rules  : !RULES_PATH!
echo -------------------------------------------------
echo.

:: ?? Run the review ????????????????????????????????????????????
pushd CodeReviewAgent
dotnet run -- --repoPath "!REPO_PATH!" --sourceBranch "!SOURCE_BRANCH!" --targetBranch "!TARGET_BRANCH!" --rulesPath "!RULES_PATH!"
set EXIT_CODE=!ERRORLEVEL!
popd

echo.
if !EXIT_CODE! equ 0 (
    echo Review complete: No violations found.
) else if !EXIT_CODE! equ 1 (
    echo Review complete: Violations found. Check the report in CodeReviewAgent\ReviewReports\
) else (
    echo Review failed with exit code !EXIT_CODE!.
)

echo.
pause
