@echo off
setlocal enabledelayedexpansion

echo ========================================
echo Quick Telemetry Check (Current Logcat)
echo ========================================
echo.

echo [1/6] Checking for SEGMENTATION START marker...
adb logcat -d -s Unity:E 2>nul | findstr /C:"SEGMENTATION INFERENCE RUN MANAGER START" 2>nul
if errorlevel 1 (
    echo NOT FOUND - Scene may not have started
) else (
    echo FOUND - Scene is running
)
echo.

echo [2/6] Sample Created FrameTrace messages:
adb logcat -d -s Unity:W 2>nul | findstr /C:"Created FrameTrace" 2>nul | findstr /N "^" 2>nul | findstr /R "^[1-5]:" 2>nul
if errorlevel 1 echo No frames created yet
echo.

echo [3/6] Sample MarkCompleted messages:
adb logcat -d -s Unity:W 2>nul | findstr /C:"MarkCompleted frame" 2>nul | findstr /N "^" 2>nul | findstr /R "^[1-5]:" 2>nul
if errorlevel 1 echo No frames completed yet
echo.

echo [4/6] Sample m_lastCompletedTrace set messages:
adb logcat -d -s Unity:W 2>nul | findstr /C:"Set m_lastCompletedTrace" 2>nul | findstr /N "^" 2>nul | findstr /R "^[1-5]:" 2>nul
if errorlevel 1 echo No frames saved to m_lastCompletedTrace
echo.

echo [5/6] Sample Sending delayed headers messages:
adb logcat -d -s Unity:W 2>nul | findstr /C:"Sending delayed headers" 2>nul | findstr /N "^" 2>nul | findstr /R "^[1-5]:" 2>nul
if errorlevel 1 echo No delayed headers sent
echo.

echo [6/6] NULL m_lastCompletedTrace warnings:
adb logcat -d -s Unity:W 2>nul | findstr /C:"m_lastCompletedTrace is NULL" 2>nul
if errorlevel 1 echo None (good - means headers are being sent)
echo.

echo ========================================
echo SUMMARY:
echo ========================================

REM Count occurrences
set CREATED=0
set COMPLETED=0
set DISPLAYED=0
set SENT=0
set NULLS=0

for /f %%i in ('adb logcat -d -s Unity:W 2^>nul ^| findstr /C:"Created FrameTrace" 2^>nul ^| find /C "Created" 2^>nul') do set CREATED=%%i
for /f %%i in ('adb logcat -d -s Unity:W 2^>nul ^| findstr /C:"MarkCompleted frame" 2^>nul ^| find /C "MarkCompleted" 2^>nul') do set COMPLETED=%%i
for /f %%i in ('adb logcat -d -s Unity:W 2^>nul ^| findstr /C:"Set m_lastCompletedTrace" 2^>nul ^| find /C "Set" 2^>nul') do set DISPLAYED=%%i
for /f %%i in ('adb logcat -d -s Unity:W 2^>nul ^| findstr /C:"Sending delayed headers" 2^>nul ^| find /C "Sending" 2^>nul') do set SENT=%%i
for /f %%i in ('adb logcat -d -s Unity:W 2^>nul ^| findstr /C:"m_lastCompletedTrace is NULL" 2^>nul ^| find /C "NULL" 2^>nul') do set NULLS=%%i

echo Frames Created:           %CREATED%
echo Frames Completed:         %COMPLETED%
echo Frames Displayed (saved): %DISPLAYED%
echo Delayed Headers Sent:     %SENT%
echo NULL Warnings:            %NULLS%
echo.

REM Diagnosis
if %CREATED% EQU 0 (
    echo STATUS: No frames detected
    echo NEXT STEP: Launch Segmentation scene on Quest 3 and send frames
) else (
    echo STATUS: Segmentation is running (%CREATED% frames)

    if %SENT% GTR 0 (
        echo TELEMETRY: Working! Headers are being sent
        echo EXPECTED: Excel should have valid timestamps
    ) else (
        if %NULLS% GTR 0 (
            echo TELEMETRY: BROKEN - m_lastCompletedTrace is NULL
            echo DIAGNOSIS: TryDisplayNewestFrame not executing or frames not saved
        ) else (
            echo TELEMETRY: Unknown issue - check detailed logs
        )
    )

    if %CREATED% NEQ %COMPLETED% (
        echo WARNING: Created (%CREATED%) != Completed (%COMPLETED%)
        echo Some frames may still be pending
    )

    if %COMPLETED% NEQ %DISPLAYED% (
        echo WARNING: Completed (%COMPLETED%) != Displayed (%DISPLAYED%)
        echo TryDisplayNewestFrame may not be running
    )
)
echo.
echo ========================================
echo.
echo For detailed real-time monitoring, run:
echo   python monitor_segmentation_telemetry.py
echo.
echo To save full log:
echo   adb logcat -d -s Unity:W ^> segmentation_log.txt
echo.
pause
