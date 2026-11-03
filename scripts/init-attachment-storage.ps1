$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'infra/attachments.local.env'
if (-not (Test-Path -LiteralPath $target)) {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $secret = ([BitConverter]::ToString($bytes)).Replace('-', '').ToLowerInvariant()
    $data = "AWS_ACCESS_KEY_ID=chatapp-local`nAWS_SECRET_ACCESS_KEY=$secret`nS3_BUCKET=chatapp-attachments`n"
    [IO.File]::WriteAllText($target, $data, (New-Object Text.UTF8Encoding($false)))
    Write-Host 'Created local storage credentials (gitignored; keys are not printed).'
} else { Write-Host 'Keeping existing local storage credentials.' }
