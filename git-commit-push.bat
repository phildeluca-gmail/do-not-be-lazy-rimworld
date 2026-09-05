@echo off
:: ============================================================
:: git-commit-push.bat
:: Stage, commit, and push this repository.
:: Double-click to run.
::
:: Repo: https://github.com/phildeluca-gmail/do-not-be-lazy-rimworld
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

:: NEVER commit the reference DLLs. CLAUDE.md section 1: they are
:: copyrighted and are not ours to redistribute. If one is staged the
:: ignore rule is broken and this must stop rather than push it.
::
:: Backported from the push-*.bat scripts 2026-09-05. Not theoretical -
:: the hand-run version of this check caught Assemblies\*.dll staged
:: during the Standard Cargo split-out the same day, because
:: .gitignore's */Assemblies/*.dll needs a directory above Assemblies.
:: NOT anchored with $ on purpose. git writes LF-only line endings
:: and findstr's $ expects a CR before the LF, so the anchored form
:: silently matches NOTHING - verified against a real staged .dll on
:: 2026-09-05, which is how this was caught before it shipped. A path
:: merely containing ".dll" would be a false positive; that stops a
:: commit with a clear message, which is the safe direction to fail.
git diff --cached --name-only | findstr /i /r "\.dll \.pdb" >nul
:: The listing below uses a git pathspec rather than a pipe: a piped
:: command inside a parenthesised cmd block does not survive the ^
:: escape, and git receives the bar as an argument. And no :: comment
:: may appear INSIDE the block at all - cmd tries to run it as a
:: drive change and prints "The system cannot find the drive
:: specified". Both were caught by running this script for real on
:: 2026-09-05 rather than reading it.
if not errorlevel 1 (
    echo.
    echo ERROR: a .dll or .pdb is staged. That must never be committed.
    echo Staged binaries:
    git diff --cached --name-only -- "*.dll" "*.pdb"
    echo.
    echo Fix the .gitignore in this repo before running this again.
    git reset >nul
    pause
    exit /b 1
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
