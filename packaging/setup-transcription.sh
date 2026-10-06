#!/bin/sh
# Installs the speech engine SoundOff transcribes with, using the uv beside this script. Safe to run again to repair it.
# Pass --gpu for the CUDA build on Linux with an NVIDIA card.
here="$(cd "$(dirname "$0")" && pwd)"
export PATH="$here/tools:$PATH" UV_PYTHON_PREFERENCE=only-managed PYTHONDONTWRITEBYTECODE=1
echo "Setting up SoundOff transcription."
echo "This downloads the speech engine (about 2 GB on disk) and can take a while."
echo
if uv run --no-project --python 3.11 "$here/scripts/setup_runtime.py" "$@"; then
  echo
  echo "Done. Go back to SoundOff to continue."
else
  echo
  echo "Setup did not finish. Check your internet connection and run it again."
fi
echo
printf "Press Enter to close this window. "
read -r _
