@echo off
REM ===================================================================
REM  Publishes a GitHub release with the exe files, using the GitHub
REM  CLI. Installs the CLI and signs in the first time it is needed.
REM
REM    gh-release.cmd           use the exe files already in publish\
REM    gh-release.cmd build     run publish.cmd first, then release
REM
REM  The version number is read from the csproj, so this file never
REM  has to be edited.
REM ===================================================================
setlocal enabledelayedexpansion
cd /d "%~dp0"

set REPO=JesperSOGT/ST-Device-Monitoring
set PROJ=ST Device Monitoring\ST Device Monitoring.csproj
set BRANCH=master
set NOTES=%TEMP%\st-release-notes.md

echo.
echo === 1/8 GitHub CLI ===
where gh >nul 2>&1
if errorlevel 1 (
  echo Not installed - installing it with winget, please accept the prompt.
  winget install --id GitHub.cli -e --source winget --accept-package-agreements --accept-source-agreements
  set "PATH=%PATH%;%ProgramFiles%\GitHub CLI"
)
where gh >nul 2>&1
if errorlevel 1 (
  echo.
  echo gh was installed but is not on the PATH of this window.
  echo Close this window, open a new one and run gh-release.cmd again.
  pause
  exit /b 1
)
gh --version

echo.
echo === 2/8 Sign in ===
gh auth status >nul 2>&1
if errorlevel 1 (
  echo Not signed in. A browser window opens - paste the code it shows.
  gh auth login --hostname github.com --git-protocol https --web
)
gh auth status
if errorlevel 1 (echo Sign in failed. & pause & exit /b 1)

echo.
echo === 3/8 Version ===
REM PROJ is passed through the environment so no quoting is needed inside PowerShell.
for /f "delims=" %%v in ('powershell -NoProfile -Command "([xml](Get-Content -Raw -LiteralPath $env:PROJ)).Project.PropertyGroup.Version"') do set "VER=%%v"
if "%VER%"=="" (echo Could not read the version from the csproj. & pause & exit /b 1)
set TAG=v%VER%
echo Version : %VER%
echo Tag     : %TAG%
echo Repo    : %REPO%

echo.
echo === 4/8 Build ===
if /i "%1"=="build" goto :build
if not exist "publish\selfcontained\ST Device Monitoring.exe" goto :build
echo Using the exe files already in publish\ - run "gh-release.cmd build" to rebuild.
goto :stage

:build
call publish.cmd
if errorlevel 1 (echo BUILD FAILED & pause & exit /b 1)

:stage
echo.
echo === 5/8 Naming the files ===
if not exist artifacts mkdir artifacts
set SELF=artifacts\ST-Device-Monitoring-%TAG%-win-x64.exe
set FDEP=artifacts\ST-Device-Monitoring-%TAG%-win-x64-netdesktop8.exe
copy /y "publish\selfcontained\ST Device Monitoring.exe" "%SELF%" >nul
if errorlevel 1 (echo Self-contained exe is missing - run "gh-release.cmd build". & pause & exit /b 1)
set ASSETS="%SELF%#Standalone - nothing to install"
if exist "publish\framework-dependent\ST Device Monitoring.exe" (
  copy /y "publish\framework-dependent\ST Device Monitoring.exe" "%FDEP%" >nul
  set ASSETS=!ASSETS! "%FDEP%#Small - needs the .NET 8 Desktop Runtime"
)
dir /b artifacts

echo.
echo === 6/8 Commit, tag and push ===
REM commit-message.txt is used as the commit text when it exists, so the description written
REM together with the code ends up in the history. It is git-ignored and removed after the commit.
git add -A
git diff --cached --quiet
if errorlevel 1 (
  if exist "commit-message.txt" (
    echo Using commit-message.txt as the commit text.
    git commit -F "commit-message.txt"
    if not errorlevel 1 del "commit-message.txt"
  ) else (
    git commit -m "Release %TAG%"
  )
) else (
  echo Nothing to commit - the working tree is clean.
)
git push origin %BRANCH%
if errorlevel 1 (echo PUSH FAILED - fix it and run again. & pause & exit /b 1)
git tag -f %TAG%
git push -f origin %TAG%

echo.
echo === 7/8 Release ===
> "%NOTES%" echo ST Device Monitoring %TAG%
>>"%NOTES%" echo.
>>"%NOTES%" echo ### Download
>>"%NOTES%" echo.
>>"%NOTES%" echo * ST-Device-Monitoring-%TAG%-win-x64.exe - runs on its own, nothing to install.
>>"%NOTES%" echo * ST-Device-Monitoring-%TAG%-win-x64-netdesktop8.exe - smaller, needs the .NET 8 Desktop Runtime.
>>"%NOTES%" echo.
>>"%NOTES%" echo Windows SmartScreen may warn about an unknown publisher. Choose "More info" and then "Run anyway".
>>"%NOTES%" echo.
>>"%NOTES%" echo Private use only - see LICENSE.

gh release view %TAG% --repo %REPO% >nul 2>&1
if not errorlevel 1 (
  echo An existing release was found for %TAG% - replacing it. The tag is kept.
  gh release delete %TAG% --repo %REPO% -y
)
gh release create %TAG% --repo %REPO% --title "%TAG%" --notes-file "%NOTES%" --latest %ASSETS%
if errorlevel 1 (echo RELEASE FAILED & pause & exit /b 1)
del "%NOTES%" >nul 2>&1

echo.
echo === 8/8 Repository visibility ===
set VIS=
for /f "delims=" %%v in ('gh repo view %REPO% --json visibility -q .visibility') do set VIS=%%v
echo Currently: !VIS!
if /i "!VIS!"=="private" (
  echo.
  echo A private repository means the download links only work for people you
  echo have invited. Making it public lets anyone download the exe.
  set /p GOPUB=Make the repository public now? Type YES:
  if /i "!GOPUB!"=="YES" (
    gh repo edit %REPO% --visibility public --accept-visibility-change-consequences
    echo Repository is now public.
  ) else (
    echo Left private.
  )
)

echo.
echo === Done ===
gh release view %TAG% --repo %REPO%
echo.
echo Direct link: https://github.com/%REPO%/releases/tag/%TAG%
echo.
pause
