@echo off
rem Starts the local language model (if needed) and the co-op game in study mode.
setlocal
set /p PID=Participant ID (e.g. P01):
if "%PID%"=="" goto :eof
set "OLLAMA_MODELS=D:\.ollama\models"
set "OLLAMA_HOST=127.0.0.1:11434"
powershell -NoProfile -Command "try { Invoke-RestMethod http://127.0.0.1:11434/api/tags -TimeoutSec 2 | Out-Null; exit 0 } catch { exit 1 }"
if errorlevel 1 (
  start "Ollama" /min "D:\Tools\ollama\ollama.exe" serve
  timeout /t 4 /nobreak >nul
)
start "" "%~dp0..\Build\Coop\TeamRescueCoop.exe" -study %PID%
echo Game started for %PID%. Log: %~dp0..\Build\Coop\StudyData\%PID%.csv
endlocal
