@echo off
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set RES=
if exist warp.conf set RES=/resource:warp.conf,ZapretGUI.warp.conf
if not defined RES echo NOTE: gui\warp.conf not embedded, WARP account will be registered on first use
if not defined OUT set OUT=..\ZapretGUI.exe
"%CSC%" /nologo /target:winexe /optimize /out:%OUT% /win32manifest:app.manifest %RES% /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Numerics.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ZapretGUI.cs
if errorlevel 1 (echo Build FAILED) else echo Build OK: %OUT%
