# Fabrique l'installateur : artifacts\DiscordVoiceTranslator-Setup-<version>.exe
# Usage : powershell -ExecutionPolicy Bypass -File build\release.ps1 [-Version 1.2.0]
# Prérequis : SDK .NET 8, Inno Setup 6 (winget install JRSoftware.InnoSetup).
param([string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'artifacts\publish'
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }

# .NET inclus (self-contained) : rien à installer chez l'utilisateur.
dotnet publish (Join-Path $root 'src\DiscordVoiceTranslator.App') -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -o $publish -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# Whisper.net copie ses runtimes pour toutes les plateformes : on ne garde que Windows x64.
# CUDA aussi est retiré (environ 150 Mo) : il exige le CUDA Toolkit, absent chez la plupart des
# gens, et Vulkan (inclus, NVIDIA comme AMD) a été mesuré aussi rapide.
$unused = 'runtimes\cuda', 'runtimes\vulkan\linux-x64', 'runtimes\linux-x64', 'runtimes\linux-arm64',
          'runtimes\osx-arm64', 'runtimes\osx-x64', 'runtimes\win-arm64', 'runtimes\win-x86'
foreach ($dir in $unused) {
    $path = Join-Path $publish $dir
    if (Test-Path $path) { Remove-Item -Recurse -Force $path }
}

# Runtime Visual C++ (requis par whisper.cpp et ONNX Runtime) copié à côté de l'appli, comme
# Microsoft l'autorise : pas besoin d'installer le redistribuable, ni de droits administrateur.
$vcRuntime = 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll',
             'msvcp140_atomic_wait.dll', 'msvcp140_codecvt_ids.dll', 'concrt140.dll', 'vcomp140.dll'
foreach ($dll in $vcRuntime) {
    Copy-Item (Join-Path $env:SystemRoot "System32\$dll") $publish
}

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found (winget install JRSoftware.InnoSetup)' }
& $iscc /Q "/DAppVersion=$Version" (Join-Path $root 'installer\DiscordVoiceTranslator.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }

$setup = Join-Path $root "artifacts\DiscordVoiceTranslator-Setup-$Version.exe"
'{0} ({1:N0} MB)' -f $setup, ((Get-Item $setup).Length / 1MB)
