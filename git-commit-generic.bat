@echo off
:: ============================================================
:: git-commit-generic.bat
:: Stage, commit, and push this repository.
:: Double-click to run.
::
:: 2026-08-16: was still staging "SpotTheDifference Files/" from
:: another project. That pathspec matches nothing here, so nothing
:: was ever staged, the commit failed, and push ran anyway - and it
:: still printed "Done", which made a silent no-op look like success.
:: Now stages everything (matching the convention in CLAUDE.md) and
:: refuses to continue when a step fails.
:: ============================================================

cd /d "%~dp0"

:: ============================================================
:: STAGE
:: ============================================================
git add -A
if errorlevel 1 (
    echo.
    echo ERROR: git add failed. Nothing committed.
    pause
    exit /b 1
)

:: Bail out if there is nothing staged, rather than "committing" nothing
git diff --cached --quiet
if not errorlevel 1 (
    echo.
    echo Nothing to commit - working tree clean.
    pause
    exit /b 0
)

:: Show exactly what is about to be committed
echo.
echo === Files staged for commit ===
git status --short
echo.
git diff --cached --stat
echo.

set /p MSG="Commit message: "
if "%MSG%"=="" set MSG=Update files

git commit -m "%MSG%"
if errorlevel 1 (
    echo.
    echo ERROR: commit failed. Nothing pushed.
    pause
    exit /b 1
)

:: 2026-08-29: pull before push. Without this a change made on another
:: machine or on GitHub turns into a rejected push and a confusing error
:: at the very end of the run.
echo.
echo Pulling remote changes before push...
git pull --rebase
if errorlevel 1 (
    echo.
    echo ERROR: pull/rebase failed - you probably have a conflict.
    echo Your commit exists locally and is NOT on GitHub.
    echo Resolve the conflict, then run "git rebase --continue" and
    echo "git push".
    pause
    exit /b 1
)

git push
if errorlevel 1 (
    echo.
    echo ERROR: push failed. The commit exists locally but is NOT on GitHub.
    echo Fix the problem and run "git push" again.
    pause
    exit /b 1
)

echo.
echo Done - committed and pushed.
pause
