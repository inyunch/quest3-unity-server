@echo off
REM ============================================================
REM Pull Quest Telemetry CSV Files from Unity Local Storage
REM ============================================================
REM
REM This script pulls ALL telemetry CSV files from your Quest device
REM to your PC for analysis.
REM
REM Location on Quest:
REM   /sdcard/Android/data/com.DefaultCompany.PassthroughCameraApiSamples/files/telemetry_*.csv
REM
REM Output directory:
REM   C:\Users\user\Unity-PassthroughCameraApiSamples\telemetry\
REM
REM Usage:
REM   1. Double-click this file
REM   2. CSV files will be copied to .\telemetry\
REM   3. Open CSV files with Excel or any CSV viewer
REM
REM ============================================================

echo ========================================
echo Pull Quest Telemetry CSV Files
echo ========================================
echo.

REM Create local telemetry directory if it doesn't exist
if not exist "%~dp0..\telemetry" (
    echo Creating telemetry directory...
    mkdir "%~dp0..\telemetry"
)

REM Check if Quest is connected
echo Checking if Quest is connected...
adb devices | findstr /C:"device" >nul
if errorlevel 1 (
    echo ERROR: No Quest device found!
    echo.
    echo Please:
    echo   1. Connect Quest via USB
    echo   2. Enable USB debugging in Quest settings
    echo   3. Accept "Allow USB debugging" prompt on Quest
    echo.
    pause
    exit /b 1
)

echo Quest device connected!
echo.

REM Define package name (update if your package name is different)
set PACKAGE_NAME=com.DefaultCompany.PassthroughCameraApiSamples

REM Pull all CSV files
echo Pulling telemetry CSV files from Quest...
echo Source: /sdcard/Android/data/%PACKAGE_NAME%/files/
echo Destination: %~dp0..\telemetry\
echo.

adb pull /sdcard/Android/data/%PACKAGE_NAME%/files/telemetry_*.csv "%~dp0..\telemetry\" 2>nul

if errorlevel 1 (
    echo WARNING: No telemetry files found on Quest.
    echo.
    echo Possible reasons:
    echo   1. Unity app hasn't been run yet
    echo   2. Package name is different (current: %PACKAGE_NAME%)
    echo   3. Local telemetry is disabled (m_enableLocalTelemetry = false)
    echo.
    echo To find the correct package name, run:
    echo   adb shell pm list packages ^| findstr Passthrough
    echo.
    pause
    exit /b 1
)

echo.
echo ========================================
echo SUCCESS!
echo ========================================
echo.
echo CSV files copied to:
echo   %~dp0..\telemetry\
echo.

REM List copied files
echo Copied files:
dir /B "%~dp0..\telemetry\telemetry_*.csv" 2>nul

echo.
echo ========================================
echo Next Steps
echo ========================================
echo.
echo 1. Open CSV files with Excel
echo 2. Analyze frame states (Displayed/Dropped/Failed)
echo 3. Check drop_reason and error_reason columns
echo 4. Compare with server-side Excel logs
echo.
echo Tip: You can also copy files directly using Windows File Explorer:
echo   This PC ^> Quest 3 ^> Internal shared storage ^> Android ^> data ^> %PACKAGE_NAME% ^> files
echo.

pause
