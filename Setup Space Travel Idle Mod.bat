@echo off
rem Space Travel Idle - Community Mod Setup launcher.
rem Two layouts: in the project, setup_mod.py sits in installer\. In a release
rem zip it sits right here beside this .bat. Find whichever one is present.
cd /d "%~dp0"
set "SETUP=%~dp0installer\setup_mod.py"
if not exist "%SETUP%" set "SETUP=%~dp0setup_mod.py"
if not exist "%SETUP%" (
    echo Could not find setup_mod.py next to this file or in installer\.
    echo Unzip the whole download to one folder and run this again.
    pause
    exit /b 1
)
where python >nul 2>nul
if %errorlevel%==0 (
    python "%SETUP%"
) else (
    where py >nul 2>nul
    if errorlevel 1 (
        echo Python was not found.
        echo Install Python 3.9 or newer from python.org and tick
        echo "Add python.exe to PATH" on the first screen, then run this again.
        pause
        exit /b 1
    )
    py "%SETUP%"
)
if errorlevel 1 pause
