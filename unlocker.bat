@echo off
chcp 65001 > nul
setlocal EnableExtensions EnableDelayedExpansion
set "AUTO="
if /i "%~1"=="/y" set "AUTO=1"

:: Elevation
net session >nul 2>&1
if errorlevel 1 (
    echo Требуются права администратора, перезапуск...
    set "UL_SELF=%~f0"
    set "UL_ARGS=%*"
    powershell -NoProfile -Command "$a=$env:UL_ARGS; if($a){Start-Process -FilePath $env:UL_SELF -ArgumentList $a -Verb RunAs}else{Start-Process -FilePath $env:UL_SELF -Verb RunAs}"
    if errorlevel 1 (
        echo Не удалось получить права администратора.
        if not defined AUTO pause
        exit /b 1
    )
    exit /b 0
)

set "ROOT=%~dp0"
if not defined AUTO (
    set "ANS="
    set /p "ANS=Остановить zapret и все его компоненты? (Y/N): "
    if /i not "!ANS!"=="Y" (
        echo Отменено.
        pause
        exit /b 1
    )
)

echo.
echo [1/5] Закрытие ZapretGUI...
taskkill /F /IM ZapretGUI.exe >nul 2>&1

echo [2/5] Очистка средствами ZapretGUI...
if exist "%ROOT%ZapretGUI.exe" (
    start "" /wait "%ROOT%ZapretGUI.exe" --cleanup
    echo Код возврата ZapretGUI --cleanup: !errorlevel!
) else (
    echo ZapretGUI.exe не найден, пропуск.
)

echo [3/5] Остановка winws и служб...
taskkill /F /IM winws.exe >nul 2>&1
call :remove_service zapret
call :remove_service "AmneziaWGTunnel$zapret-tg"
call :remove_service WinDivert
call :remove_service WinDivert14

echo [4/5] Удаление задачи автозапуска...
schtasks /Delete /TN "ZapretGUI" /F >nul 2>&1

echo [5/5] Проверка блокировок файлов...
timeout /t 2 /nobreak >nul
set "UL_ROOT=%ROOT%"
set "LOCKED="
for /f "usebackq delims=" %%L in (`powershell -NoProfile -Command "$r=$env:UL_ROOT; foreach($f in 'bin\WinDivert64.sys','bin\winws.exe','ZapretGUI.exe'){$p=Join-Path $r $f; if(Test-Path -LiteralPath $p){try{$s=[IO.File]::Open($p,'Open','ReadWrite','None');$s.Close()}catch{$f}}}"`) do (
    set "LOCKED=1"
    echo   Занят: %%L
)

echo.
if defined LOCKED (
    echo Некоторые файлы всё ещё заняты. Перезагрузите компьютер и удалите папку после перезагрузки.
) else (
    echo Готово: папку можно удалить
)
if not defined AUTO pause
if defined LOCKED (exit /b 1) else (exit /b 0)

:remove_service
sc query %1 >nul 2>&1
if errorlevel 1 exit /b 0
echo   Служба %~1: остановка и удаление
sc stop %1 >nul 2>&1
sc delete %1 >nul 2>&1
exit /b 0
