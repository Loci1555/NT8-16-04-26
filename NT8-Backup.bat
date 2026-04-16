@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

:: ============================================================
::  NT8 Profile Backup Script
:: ============================================================

set "NT8_PROFILE=D:\Documents\NinjaTrader 8"
set "BACKUP_DIR=D:\Мій диск\Трейдинг\NT8\NT8 Profile Backup"
set "TEMP_COPY=%TEMP%\NT8_Backup_Temp"

if not exist "%NT8_PROFILE%" (
    echo [ERROR] NT8 folder not found: %NT8_PROFILE%
    pause
    exit /b 1
)

if not exist "%BACKUP_DIR%" (
    mkdir "%BACKUP_DIR%"
    echo [INFO] Created folder: %BACKUP_DIR%
)

:: --- Date via PowerShell ---
for /f %%I in ('powershell -NoProfile -Command "Get-Date -Format yyyy-MM-dd"') do set "DATESTAMP=%%I"
set "ZIP_NAME=NT8-Profile_%DATESTAMP%.zip"
set "ZIP_PATH=%BACKUP_DIR%\%ZIP_NAME%"

if exist "%ZIP_PATH%" (
    echo [INFO] Backup for %DATESTAMP% already exists: %ZIP_NAME%
    echo Overwrite? [Y/N]
    set /p "CONFIRM="
    if /i "!CONFIRM!" neq "Y" (
        echo Cancelled.
        pause
        exit /b 0
    )
    del "%ZIP_PATH%"
)

if exist "%TEMP_COPY%" rmdir /s /q "%TEMP_COPY%"

echo.
echo [1/3] Copying profile (excluding db, log, trace)...
robocopy "%NT8_PROFILE%" "%TEMP_COPY%" /e /xd db log trace /xf *.log /njh /njs /ndl /nc /ns >nul

if errorlevel 8 (
    echo [ERROR] Robocopy failed.
    pause
    exit /b 1
)

echo [2/3] Archiving %ZIP_NAME%...
powershell -NoProfile -Command "Compress-Archive -Path '%TEMP_COPY%\*' -DestinationPath '%ZIP_PATH%' -Force"

if errorlevel 1 (
    echo [ERROR] Failed to create archive.
    rmdir /s /q "%TEMP_COPY%"
    pause
    exit /b 1
)

echo [3/3] Cleaning up...
rmdir /s /q "%TEMP_COPY%"

for %%A in ("%ZIP_PATH%") do set "FSIZE=%%~zA"
set /a "SIZE_MB=!FSIZE! / 1048576"

echo.
echo ============================================================
echo   Backup created!
echo   File: %ZIP_NAME%
echo   Size: ~!SIZE_MB! MB
echo   Path: %BACKUP_DIR%
echo ============================================================

echo Cleaning backups older than 90 days...
forfiles /p "%BACKUP_DIR%" /m "NT8-Profile_*.zip" /d -90 /c "cmd /c del @path && echo   Deleted: @file" 2>nul

echo.
echo Done!
pause
