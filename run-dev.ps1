# Spusti cerstvy dev build LSP.
# Reseni problemu: bezici instance drzi zamceny exe/DLL -> build tise selze
# nebo se preskoci -> spusti se stary vystup. Tento skript:
#   1. zabije bezici LSP procesy,
#   2. prebuilduji LSP.App (vcetne LSP.Server pres project reference),
#   3. pri selhani buildu skonci NAHLAS (nespusti nic stareho),
#   4. spusti cerstvy exe a vypise build stamp.

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$running = Get-Process -Name "LSP" -ErrorAction SilentlyContinue
if ($running) {
    # Graceful stop: zrus bezici enrichment pres API (Stop-Process -Force uprostred jobu
    # nechava knihovnu napul obohacenou - merge faze nikdy nedobehne).
    $portFile = Join-Path $env:LOCALAPPDATA "LSP\server.json"
    if (Test-Path $portFile) {
        try {
            $port = (Get-Content $portFile | ConvertFrom-Json).Port
            $base = "http://127.0.0.1:$port"
            $status = Invoke-RestMethod "$base/api/enrich/status" -TimeoutSec 2
            if ($status.state -eq "running") {
                Write-Host "Bezi enrichment - rusim a cekam na dobehnuti..." -ForegroundColor Yellow
                Invoke-RestMethod "$base/api/enrich/cancel" -Method Post -TimeoutSec 2 | Out-Null
                foreach ($i in 1..20) {
                    Start-Sleep -Milliseconds 500
                    $status = Invoke-RestMethod "$base/api/enrich/status" -TimeoutSec 2
                    if ($status.state -ne "running") { break }
                }
            }
        } catch { <# server neodpovida - pokracuj na kill #> }
    }

    Write-Host "Ukoncuji bezici LSP (PID $($running.Id -join ', '))..." -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

Write-Host "Builduji LSP.App..." -ForegroundColor Cyan
dotnet build src/LSP.App/LSP.App.csproj --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "BUILD SELHAL -- aplikace NEBYLA spustena (zadny stary build se nepousti)." -ForegroundColor Red
    exit 1
}

$exe = Join-Path $PSScriptRoot "src/LSP.App/bin/Debug/net10.0/LSP.exe"
$dll = Join-Path $PSScriptRoot "src/LSP.App/bin/Debug/net10.0/LSP.Server.dll"
$stamp = (Get-Item $dll).LastWriteTime
Write-Host "Spoustim LSP (build z $($stamp.ToString('dd.MM.yyyy HH:mm:ss')))..." -ForegroundColor Green
Start-Process $exe
