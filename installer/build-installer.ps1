$ErrorActionPreference = "Stop"

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
Set-Location $projectRoot

$version = if ($env:LSP_INSTALLER_VERSION) { $env:LSP_INSTALLER_VERSION } else { "0.1.1" }
$publishDir = Join-Path $projectRoot "artifacts\publish\LSP"
$installerDir = Join-Path $projectRoot "artifacts\installer"
$ffmpegDir = if ($env:LSP_FFMPEG_DIRECTORY) { $env:LSP_FFMPEG_DIRECTORY } else { Join-Path $projectRoot "tools\ffmpeg\win-x64" }
foreach ($tool in @("ffmpeg.exe", "ffprobe.exe")) {
    if (-not (Test-Path -LiteralPath (Join-Path $ffmpegDir $tool) -PathType Leaf)) {
        throw "Missing $tool in $ffmpegDir. Set LSP_FFMPEG_DIRECTORY to the directory containing the tested FFmpeg tools."
    }
}

function Resolve-InnoSetupCompiler {
    $fromPath = Get-Command iscc -ErrorAction SilentlyContinue
    if ($fromPath) {
        return $fromPath.Source
    }

    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 7\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    return $null
}

Write-Host "Building web frontend..." -ForegroundColor Cyan
Push-Location "src\web"
try {
    npm run build
    if ($LASTEXITCODE -ne 0) {
        throw "Frontend build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

Write-Host "Publishing desktop app..." -ForegroundColor Cyan
dotnet publish "src\LSP.App\LSP.App.csproj" `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDir `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "Dotnet publish failed with exit code $LASTEXITCODE."
}

# The installed application must use the same tools as the tested build, not an unrelated PATH version.
$bundledFfmpegDir = Join-Path $publishDir "ffmpeg\win-x64"
New-Item -ItemType Directory -Force -Path $bundledFfmpegDir | Out-Null
Get-ChildItem -LiteralPath $ffmpegDir -File | Copy-Item -Destination $bundledFfmpegDir -Force

$iscc = Resolve-InnoSetupCompiler
if (-not $iscc) {
    Write-Host ""
    Write-Host "Publish is ready: $publishDir" -ForegroundColor Green
    Write-Host "Installer was not built because ISCC.exe was not found." -ForegroundColor Yellow
    Write-Host "Add Inno Setup to PATH, or edit Resolve-InnoSetupCompiler in this script." -ForegroundColor Yellow
    exit 1
}

New-Item -ItemType Directory -Force -Path $installerDir | Out-Null

Write-Host "Building installer with $iscc..." -ForegroundColor Cyan
$env:LSP_INSTALLER_VERSION = $version
& $iscc "installer\LSP.iss"
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup Compiler failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "Installer ready: $(Join-Path $installerDir "LSP-Setup-$version.exe")" -ForegroundColor Green
