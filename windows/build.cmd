@echo off
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set NSIS=%~dp0tools\nsis-3.10\makensis.exe
if not exist out mkdir out

echo [1/4] IconGen
"%CSC%" /nologo /codepage:65001 /target:exe /out:out\IconGen.exe /r:System.Drawing.dll src\IconGen.cs || exit /b 1

echo [2/4] app.ico
out\IconGen.exe out\app.ico out\icon256.png || exit /b 1

echo [3/4] OmniVPNConnect.exe
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ /warn:4 ^
  /out:out\OmniVPNConnect.exe /win32icon:out\app.ico /win32manifest:src\app.manifest ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Security.dll ^
  src\Program.cs src\Ras.cs src\VpnProfile.cs src\AppSettings.cs src\ProfileFile.cs src\IkeProbe.cs src\NetCheck.cs src\Rdp.cs src\Updater.cs src\Theme.cs src\Widgets.cs src\Pages.cs src\MainForm.cs || exit /b 1

echo [4/4] installer
"%NSIS%" /V2 /INPUTCHARSET UTF8 src\installer.nsi || exit /b 1

echo BUILD OK
