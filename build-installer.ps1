# Builds publish\PowerTray.exe, then installer\Output\PowerTraySetup-<version>.exe with Inno Setup 6.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
if ($LASTEXITCODE) { exit $LASTEXITCODE }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Get it from https://jrsoftware.org/isinfo.php' }

& $iscc /Qp installer\PowerTray.iss
exit $LASTEXITCODE
