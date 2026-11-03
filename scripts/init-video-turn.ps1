$ErrorActionPreference = 'Stop'
$target = Join-Path (Split-Path -Parent $PSScriptRoot) 'infra/turn.local.env'
if (Test-Path -LiteralPath $target) { Write-Host 'Keeping existing local TURN secret.'; exit 0 }
$bytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
$secret = ([BitConverter]::ToString($bytes)).Replace('-', '').ToLowerInvariant()
$data = "VIDEO_TURN_URL=turn:127.0.0.1:3479?transport=tcp`nVIDEO_TURN_SHARED_SECRET=$secret`nVIDEO_ICE_TRANSPORT_POLICY=relay`n"
[IO.File]::WriteAllText($target, $data, (New-Object Text.UTF8Encoding($false)))
Write-Host 'Created local-only TURN settings. Secret not printed.'
