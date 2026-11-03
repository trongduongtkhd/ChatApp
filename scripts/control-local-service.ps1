param([ValidateSet('Group','Identity','Notification')][string]$Service='Group',[ValidateSet('Stop','Start')][string]$Action)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$ports=@{Group=5002;Identity=5001;Notification=5004}
$directory=Join-Path $taskRoot "backend/src/Services/$Service/ChatApp.${Service}Service"
$executable=[IO.Path]::GetFullPath((Join-Path $directory "bin/Debug/net10.0/ChatApp.${Service}Service.exe"))
foreach($ownerId in @(Get-NetTCPConnection -State Listen -LocalPort $ports[$Service] -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique)) {
    $owned=Get-CimInstance Win32_Process -Filter "ProcessId=$ownerId"
    if (![string]::Equals($owned.ExecutablePath,$executable,[StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to stop an unrelated process.' }
    if ($Action -eq 'Stop') { Stop-Process -Id $ownerId -Force }
    else { Write-Host 'Already running'; exit 0 }
}
if($Action -eq 'Start') {
    $runtime=Join-Path $taskRoot 'infra/backups/runtime'
    $env:ASPNETCORE_ENVIRONMENT='Development'
    $env:ASPNETCORE_URLS="http://localhost:$($ports[$Service])"
    Start-Process -FilePath $executable -WorkingDirectory $directory -WindowStyle Hidden -RedirectStandardOutput "$runtime/${Service}Service.log" -RedirectStandardError "$runtime/${Service}Service.err.log" | Out-Null
}
