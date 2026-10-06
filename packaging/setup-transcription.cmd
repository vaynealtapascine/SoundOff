@echo off
rem Installs the speech engine SoundOff transcribes with, using the uv beside this script. Safe to run again to repair it.
rem Pass --gpu for the CUDA build on a computer with an NVIDIA card.
setlocal
set "PATH=%~dp0tools;%PATH%"
set "UV_PYTHON_PREFERENCE=only-managed"
set "PYTHONDONTWRITEBYTECODE=1"
title Set up SoundOff transcription
echo Setting up SoundOff transcription.
echo This downloads the speech engine (about 2 GB on disk) and can take a while.
echo.
uv run --no-project --python 3.11 "%~dp0scripts\setup_runtime.py" %*
if errorlevel 1 (
  echo.
  echo Setup did not finish. Check your internet connection and run it again.
) else (
  echo.
  echo Done. Go back to SoundOff to continue.
)
echo.
pause
