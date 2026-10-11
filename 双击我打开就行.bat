@echo off
cd /d "%~dp0"
title Captcha Detector Local Server
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0serve_detector.ps1"
if errorlevel 1 pause
