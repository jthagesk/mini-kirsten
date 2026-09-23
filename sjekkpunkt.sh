#!/usr/bin/env bash
set -euo pipefail

if [[ "${1:-}" != "lab1" ]]; then
  echo "Bruk: ./sjekkpunkt.sh lab1" >&2
  exit 1
fi

rot="$(cd "$(dirname "$0")" && pwd)"
# python3 first, then python. Skip commands that exist but do not run, such as
# the Microsoft Store shortcut on Windows.
for py in python3 python; do
  if command -v "$py" >/dev/null 2>&1 && "$py" --version >/dev/null 2>&1; then
    exec "$py" "$rot/setup-lab1.py" --checkpoint
  fi
done
echo "Fant verken python3 eller python. Installer Python 3." >&2
exit 1
