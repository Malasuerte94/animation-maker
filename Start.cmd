@echo off
setlocal
if exist "%~dp0artifacts\portable\AnimationMaker\AnimationMaker.exe" (
  start "Animation Maker" "%~dp0artifacts\portable\AnimationMaker\AnimationMaker.exe"
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\run.ps1"
)
