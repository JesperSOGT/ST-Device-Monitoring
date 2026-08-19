@echo off
REM ===================================================================
REM  Replaces the history on GitHub with the single clean commit that
REM  is on the local master branch, so old screenshots and transfer
REM  zips are no longer part of the repository.
REM
REM  Run it from the repository folder. Nothing is changed until you
REM  type YES.
REM ===================================================================
setlocal enabledelayedexpansion
cd /d "%~dp0"

set TAG=v1.28.1
set BRANCH=master

echo.
echo === Repository ===
git rev-parse --is-inside-work-tree >nul 2>&1 || (echo Not a git repository. & exit /b 1)
for /f "delims=" %%i in ('git remote get-url origin') do set ORIGIN=%%i
echo Remote : %ORIGIN%
echo Branch : %BRANCH%
for /f %%i in ('git rev-list --count %BRANCH%') do echo Commits: %%i
echo.

echo === Uncommitted changes ===
git status --short
echo.

echo === Checking the new history for junk ===
git log %BRANCH% --name-only --pretty=format: > "%TEMP%\histfiles.txt"
findstr /i "zip _to_delete devices.json oui.csv" "%TEMP%\histfiles.txt" >nul
if errorlevel 1 (
  echo OK - no zip files, _to_delete, devices.json or oui.csv in the history.
) else (
  echo WARNING - the history still contains:
  findstr /i "zip _to_delete devices.json oui.csv" "%TEMP%\histfiles.txt" | sort
  echo.
  echo Fix that before publishing. Press Ctrl+C to stop.
)
del "%TEMP%\histfiles.txt" >nul 2>&1
echo.

echo This will FORCE PUSH %BRANCH% and re-create the tag %TAG% on GitHub.
echo Everything currently on the remote branch is overwritten.
echo.
set /p CONFIRM=Type YES to continue: 
if /i not "%CONFIRM%"=="YES" (echo Cancelled. & exit /b 0)

echo.
echo === 1/5 Committing any pending changes ===
git add -A
git diff --cached --quiet || git commit -m "Cleanup"

echo.
echo === 2/5 Removing the old release (needs the gh CLI - skipped if missing) ===
where gh >nul 2>&1 && (gh release delete %TAG% -y --cleanup-tag) || echo gh CLI not found - delete the old release by hand on GitHub if the tag push fails.

echo.
echo === 3/5 Deleting the old tag on GitHub ===
git push origin :refs/tags/%TAG%

echo.
echo === 4/5 Force pushing the clean history ===
git push -f origin %BRANCH%
if errorlevel 1 (echo PUSH FAILED & exit /b 1)

echo.
echo === 5/5 Pushing the tag again (starts the release workflow) ===
git tag -f %TAG%
git push origin %TAG%

echo.
echo === Done ===
echo Check https://github.com/JesperSOGT/ST-Device-Monitoring/actions for the build,
echo and Releases for the new files.
echo.
echo IMPORTANT before making the repository public:
echo   A force push only moves the branch. The old commits stay reachable on
echo   GitHub by their SHA until GitHub garbage collects them, which can take a
echo   long time. To be certain nothing old is public, delete the repository
echo   (Settings - Danger Zone - Delete this repository), create an empty one
echo   with the same name, and run:
echo.
echo       git push -u origin %BRANCH%
echo       git push origin %TAG%
echo.
pause
