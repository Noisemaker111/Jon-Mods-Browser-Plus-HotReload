@echo off
python -B "%~dp0server.py"
if errorlevel 1 pause
