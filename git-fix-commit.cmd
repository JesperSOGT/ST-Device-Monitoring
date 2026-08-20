@echo off
REM ===================================================================
REM  Rewrites the message of the most recent commit, removing any
REM  Co-Authored-By or trailer lines, and pushes the corrected commit
REM  (and the tag pointing at it) to GitHub.
REM
REM  Run it from the repository folder. It only touches the newest
REM  commit - the files in it are not changed.
REM ===================================================================
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "BRANCH=master"
set "OLDMSG=%TEMP%\st-oldmsg.txt"
set "NEWMSG=%TEMP%\st-newmsg.txt"

git rev-parse --is-inside-work-tree >nul 2>&1 || (echo Not a git repository. & pause & exit /b 1)

REM A crashed or interrupted git command leaves an empty .git\index.lock behind, and every
REM later command then refuses to run. Clear it, but only when nothing wrote to it.
if exist ".git\index.lock" (
  for %%f in (".git\index.lock") do set "LOCKSIZE=%%~zf"
  if "!LOCKSIZE!"=="0" (
    echo Removing a leftover .git\index.lock from an interrupted git command.
    del /f ".git\index.lock" >nul 2>&1
  ) else (
    echo .git\index.lock is not empty - a git command may really be running.
    echo Close any open git window or editor, then run this file again.
    pause
    exit /b 1
  )
)

echo.
echo === Current commit ===
git log -1 --format="%%H  %%s"
echo.

git log -1 --format=%%B > "%OLDMSG%"
findstr /v /i /c:"Co-Authored-By:" /c:"Claude-Session:" /c:"Generated with" "%OLDMSG%" > "%NEWMSG%"

fc /w "%OLDMSG%" "%NEWMSG%" >nul 2>&1
if not errorlevel 1 (
  echo The commit message has nothing to remove - stopping.
  del "%OLDMSG%" "%NEWMSG%" >nul 2>&1
  pause
  exit /b 0
)

echo === Lines that will be removed ===
findstr /i /c:"Co-Authored-By:" /c:"Claude-Session:" /c:"Generated with" "%OLDMSG%"
echo.

echo === Uncommitted changes ===
git status --short
echo.
echo Only the newest commit is rewritten. Its files stay exactly as they are.
echo Both the branch and any tag on it are then force pushed to GitHub.
echo.
set /p CONFIRM=Type YES to continue:
if /i not "%CONFIRM%"=="YES" (echo Cancelled. & del "%OLDMSG%" "%NEWMSG%" >nul 2>&1 & exit /b 0)

echo.
echo === 1/4 Rewriting the message ===
git commit --amend -F "%NEWMSG%"
if errorlevel 1 (echo AMEND FAILED & del "%OLDMSG%" "%NEWMSG%" >nul 2>&1 & pause & exit /b 1)
del "%OLDMSG%" "%NEWMSG%" >nul 2>&1

echo.
echo === 2/4 Moving tags that pointed at the old commit ===
for /f "delims=" %%t in ('git tag --points-at HEAD@{1} 2^>nul') do (
  echo    %%t
  git tag -f %%t
  set "MOVEDTAGS=!MOVEDTAGS! %%t"
)
if "!MOVEDTAGS!"=="" echo    none

echo.
echo === 3/4 Pushing the branch ===
git push -f origin %BRANCH%
if errorlevel 1 (echo PUSH FAILED & pause & exit /b 1)

echo.
echo === 4/4 Pushing the tags ===
for %%t in (!MOVEDTAGS!) do git push -f origin %%t

echo.
echo === Done ===
git log -1 --format="%%H%%n%%B"
echo.
echo Note: the old commit stays reachable on GitHub by its SHA until GitHub
echo cleans it up. Deleting and recreating the repository is the only way to
echo be certain it is gone.
echo.
pause
