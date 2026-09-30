# Builds a self-contained Surfio (no .NET install needed on the target PC)
# into .\publish, then the installer into .\dist if Inno Setup 6 is installed.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Remove-Item publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish Surfio.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$iscc = @(
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($iscc) {
  & $iscc installer\Surfio.iss
  if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
  Write-Host "Installer: $(Resolve-Path dist)"
} else {
  Write-Host 'Inno Setup 6 not found - skipped the installer. The portable app is in .\publish'
}
