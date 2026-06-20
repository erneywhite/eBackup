<#
.SYNOPSIS
  Сборка установщика eBackup: self-contained publish + Inno Setup.
.DESCRIPTION
  1) dotnet publish приложения (self-contained, win-x64) → ..\publish
  2) ISCC по eBackup.iss → installer\dist\eBackup-setup-<версия>-x64.exe
  Версия берётся из <Version> в eBackup.App.csproj.
.EXAMPLE
  pwsh installer\build-installer.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo "src\eBackup.App\eBackup.App.csproj"
$publish = Join-Path $repo "publish"

# Версия из csproj.
[xml]$csproj = Get-Content $proj
$version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) -as [string]
if (-not $version) { $version = "1.4.0" }
Write-Host "eBackup $version — сборка установщика" -ForegroundColor Cyan

# Найти ISCC.
$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    foreach ($p in @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $p) { $iscc = $p; break }
    }
}
if (-not $iscc) { throw "Inno Setup (ISCC.exe) не найден. Установи: winget install JRSoftware.InnoSetup" }

# 1) Publish GUI.
Write-Host "→ dotnet publish GUI (self-contained)…" -ForegroundColor Yellow
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish $proj -c $Configuration -r win-x64 --self-contained true -o $publish | Out-Null
if (-not (Test-Path (Join-Path $publish "eBackup.App.pri"))) {
    throw "В publish нет eBackup.App.pri — XAML не загрузится. Проверь target CopyAppPriToPublish."
}

# 1b) Publish службы (self-contained) → publish\service\ (ставится в {app}\service\).
Write-Host "→ dotnet publish службы (self-contained)…" -ForegroundColor Yellow
$svcProj = Join-Path $repo "src\eBackup.Service\eBackup.Service.csproj"
$svcOut = Join-Path $publish "service"
dotnet publish $svcProj -c $Configuration -r win-x64 --self-contained true -o $svcOut | Out-Null
if (-not (Test-Path (Join-Path $svcOut "eBackup.Service.exe"))) {
    throw "В publish\service нет eBackup.Service.exe."
}

# 2) ISCC.
Write-Host "→ ISCC…" -ForegroundColor Yellow
$iss = Join-Path $PSScriptRoot "eBackup.iss"
& $iscc "/DAppVersion=$version" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC завершился с кодом $LASTEXITCODE" }

$out = Join-Path $PSScriptRoot "dist\eBackup-setup-$version-x64.exe"
Write-Host "✓ Готово: $out" -ForegroundColor Green
