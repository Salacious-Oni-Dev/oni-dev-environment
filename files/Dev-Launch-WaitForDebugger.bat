@echo off
rem Same as Dev-Launch.bat, but the runtime freezes at startup until a debugger attaches
rem to 127.0.0.1:56000. Use this to breakpoint early startup (Global.Awake, mod loading).
setlocal
cd /d "%~dp0"
if not exist "DevData" mkdir "DevData"
start "" "OxygenNotIncluded.exe" -logFile "%~dp0DevData\Player.log" --doorstop-mono-debug-suspend true %*
