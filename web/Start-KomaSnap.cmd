@echo off
setlocal
cd /d "%~dp0"
where node.exe >nul 2>&1
if errorlevel 1 (
  echo Node.js is required to launch KomaSnap Web.
  pause
  exit /b 1
)
node serve.mjs --open %*
