#!/usr/bin/env pwsh
# Legger inn Lab 1-sjekkpunktet. Samme som ./sjekkpunkt.sh lab1.
#
#   ./sjekkpunkt.ps1 lab1
param(
    [Parameter(Position = 0)]
    [string]$Lab
)

$ErrorActionPreference = 'Stop'

if ($Lab -ne 'lab1') {
    [Console]::Error.WriteLine('Bruk: ./sjekkpunkt.ps1 lab1')
    exit 1
}

$script = Join-Path $PSScriptRoot 'setup-lab1.py'
foreach ($candidate in @('python', 'python3', 'py')) {
    if (-not (Get-Command $candidate -ErrorAction SilentlyContinue)) {
        continue
    }
    # Windows kan ha en python-snarvei til Microsoft Store som ikke virker.
    & $candidate --version *> $null
    if ($LASTEXITCODE -ne 0) {
        continue
    }
    & $candidate $script --checkpoint
    exit $LASTEXITCODE
}

[Console]::Error.WriteLine('Fant verken python, python3 eller py. Installer Python 3.')
exit 1
