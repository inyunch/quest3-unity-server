@echo off
echo ========================================
echo Killing all Python processes...
echo ========================================
echo.

REM Kill all python.exe processes
taskkill /F /IM python.exe 2>nul
if %errorlevel% equ 0 (
    echo [OK] Killed python.exe processes
) else (
    echo [INFO] No python.exe processes found
)

echo.

REM Kill all pythonw.exe processes
taskkill /F /IM pythonw.exe 2>nul
if %errorlevel% equ 0 (
    echo [OK] Killed pythonw.exe processes
) else (
    echo [INFO] No pythonw.exe processes found
)

echo.
echo ========================================
echo Done! All Python processes terminated.
echo ========================================
echo.
pause
