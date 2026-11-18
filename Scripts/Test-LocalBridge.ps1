$ErrorActionPreference = "Stop"
try {
    $result = Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:47831/health" -TimeoutSec 3
    if (-not $result.ok) { throw "El puente respondió, pero no indicó ok=true." }
    Write-Host "Puente local activo: $($result.service) v$($result.version)" -ForegroundColor Green
} catch {
    Write-Error "No se pudo contactar con SunoSync en 127.0.0.1:47831. Abre la aplicación y vuelve a intentarlo. $($_.Exception.Message)"
    exit 1
}
