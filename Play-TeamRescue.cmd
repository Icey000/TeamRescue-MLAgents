@echo off
rem One-click launcher: starts the local Ollama server (if needed), then the Team Rescue demo.
setlocal
if not defined OLLAMA_MODELS if exist "D:\.ollama\models" set "OLLAMA_MODELS=D:\.ollama\models"
set "OLLAMA_HOST=127.0.0.1:11434"
set "OLLAMA_EXE=D:\Tools\ollama\ollama.exe"
if not exist "%OLLAMA_EXE%" set "OLLAMA_EXE=ollama"
powershell -NoProfile -Command "try { Invoke-RestMethod http://127.0.0.1:11434/api/tags -TimeoutSec 2 | Out-Null; exit 0 } catch { exit 1 }"
if errorlevel 1 (
  echo Starting local language model server...
  start "Ollama" /min "%OLLAMA_EXE%" serve
  timeout /t 4 /nobreak >nul
)
start "" "%~dp0Build\Demo\TeamRescueDemo.exe"
endlocal
