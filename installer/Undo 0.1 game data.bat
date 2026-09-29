@echo off
rem Space Travel Idle Community Patch 0.1.1: take out the game data 0.1 wrote.
rem
rem 0.1 changed the game's data file on disk and kept the untouched one next
rem to it as resources.assets.backup-original. 0.1.1 makes its changes in
rem memory instead, and refuses to start them on top of 0.1's. This puts the
rem untouched file back. It lands in the game folder with the rest of the
rem zip, so it finds the game by its own location.
setlocal
set "DATA=%~dp0SpaceTravelIdle_Data"
set "ASSETS=%DATA%\resources.assets"
set "BACKUP=%DATA%\resources.assets.backup-original"

if not exist "%ASSETS%" goto notgame
if not exist "%BACKUP%" goto nothing
tasklist /fi "imagename eq SpaceTravelIdle.exe" 2>nul | find /i "SpaceTravelIdle.exe" >nul
if not errorlevel 1 goto running

copy /y "%BACKUP%" "%ASSETS%" >nul
if errorlevel 1 goto failed
fc /b "%BACKUP%" "%ASSETS%" >nul
if errorlevel 1 goto failed
del "%BACKUP%"
echo Done. The game data is back to the community version, and 0.1.1 can
echo make its changes. Start the game from Steam as normal.
goto end

:notgame
echo This file has to be in the game folder, next to SpaceTravelIdle.exe.
echo Unzip the whole Community Patch into the game folder and run it there.
goto end

:nothing
echo Nothing to undo. Your game data has no 0.1 changes in it.
goto end

:running
echo The game is running. Quit it from inside the game, then run this again.
goto end

:failed
echo Could not put the file back. Nothing is lost: the untouched copy is
echo still here:
echo   %BACKUP%
echo Try again with the game closed. If it still fails, tell Fuzzied.
goto end

:end
echo.
pause
