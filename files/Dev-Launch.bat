@echo off
rem Launch the dev copy of Oxygen Not Included.
rem   - all persistent data lives in .\DevData (patched Util.GetKleiRootPath)
rem   - Player.log is redirected into .\DevData too
rem   - Doorstop opens the Mono debugger on 127.0.0.1:56000 (see doorstop_config.ini)
setlocal
cd /d "%~dp0"
if not exist "DevData" mkdir "DevData"
start "" "OxygenNotIncluded.exe" -logFile "%~dp0DevData\Player.log" %*
