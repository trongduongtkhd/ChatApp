# Shared functions for PostgreSQL-only online backup and isolated recovery.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:PgBackupProjectRoot = Split-Path -Parent $PSScriptRoot

function Invoke-PgDocker {
    param([Parameter(Mandatory=$true)][string[]]$DockerArgs)
    # Bypass Windows PowerShell 5's native argument quoting (embedded quotes in
    # SQL, Go templates and sh scripts must arrive unchanged). No shell evaluation.
    $quoted = foreach ($argument in $DockerArgs) {
        $escaped = [regex]::Replace($argument, '(\\*)"', '$1$1\"')
        $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
        '"' + $escaped + '"'
    }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = (Get-Command docker.exe -CommandType Application | Select-Object -First 1).Source
    $start.Arguments = $quoted -join ' '
    $start.WorkingDirectory = $script:PgBackupProjectRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        $process.Start() | Out-Null
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Docker operation failed (exit $($process.ExitCode)): $stderr $stdout" }
        return $stdout.Trim()
    } finally { $process.Dispose() }
}

function Get-PgBackupRoot {
    $root = Join-Path $script:PgBackupProjectRoot 'infra/backups/postgres'
    [IO.Directory]::CreateDirectory($root) | Out-Null
    return [IO.Path]::GetFullPath($root)
}

function Get-PgBackupDirectory {
    param([Parameter(Mandatory=$true)][string]$Path)
    if (![IO.Path]::IsPathRooted($Path)) { $Path = Join-Path $script:PgBackupProjectRoot $Path }
    $resolved = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $roots = @((Get-PgBackupRoot), (Join-Path $script:PgBackupProjectRoot 'infra/backups/system'))
    if (!@($roots | Where-Object { $resolved.StartsWith($_ + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) }).Count) {
        throw 'Backup directory must be under infra/backups/postgres or infra/backups/system.'
    }
    if ($resolved.Contains(',')) { throw 'Comma in backup path is not supported by Docker --mount.' }
    return $resolved
}

function Write-PgBackupJson {
    param([string]$Path, [object]$Value)
    $temporaryPath = $Path + '.tmp'
    [IO.File]::WriteAllText($temporaryPath, ($Value | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
}

function Get-PgSource {
    param([string]$Container = 'postgres')
    if ($Container -notmatch '^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,100}$') { throw 'Invalid source container name.' }
    $state = Invoke-PgDocker @('inspect', '--format', '{{.State.Running}}', $Container)
    if ($state -ne 'true') { throw "Source container $Container must be running." }
    $dbUser = Invoke-PgDocker @('exec', $Container, 'printenv', 'POSTGRES_USER')
    if ($dbUser -notmatch '^[a-zA-Z_][a-zA-Z0-9_]{0,62}$') { throw 'Unsupported PostgreSQL user name.' }
    $systemId = Invoke-PgSql -Container $Container -DbUser $dbUser -Sql 'SELECT system_identifier FROM pg_control_system();'
    if ($systemId -notmatch '^\d+$') { throw 'Invalid PostgreSQL system identifier.' }
    $major = [int](Invoke-PgSql -Container $Container -DbUser $dbUser -Sql 'SHOW server_version_num;')
    if ([math]::Floor($major / 10000) -ne 17) { throw 'This step supports PostgreSQL 17 only.' }
    if ((Invoke-PgSql -Container $Container -DbUser $dbUser -Sql 'SELECT pg_is_in_recovery();') -ne 'f') {
        throw 'This step requires the primary PostgreSQL server.'
    }
    return [pscustomobject]@{
        Container = $Container; DbUser = $dbUser; SystemId = $systemId
        Timeline = [int](Invoke-PgSql -Container $Container -DbUser $dbUser -Sql 'SELECT timeline_id FROM pg_control_checkpoint();')
        ImageId = (Invoke-PgDocker @('inspect', '--format', '{{.Image}}', $Container))
        StartedAt = (Invoke-PgDocker @('inspect', '--format', '{{.State.StartedAt}}', $Container))
    }
}

function Invoke-PgSql {
    param([string]$Container, [string]$DbUser, [string]$Database = 'postgres', [string]$Sql)
    return Invoke-PgDocker @('exec', '--user', 'postgres', $Container, 'psql', '-X', '-A', '-t',
        '--username', $DbUser, '--dbname', $Database, '--set', 'ON_ERROR_STOP=1', '--command', $Sql)
}

function Get-PgBackupMetadata {
    param([string]$Directory)
    $metadataPath = Join-Path $Directory 'metadata.json'
    if (!(Test-Path -LiteralPath $metadataPath -PathType Leaf)) { throw 'No completed backup metadata.json.' }
    $metadata = Get-Content -LiteralPath $metadataPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($metadata.schemaVersion -ne 1 -or $metadata.status -ne 'complete' -or $metadata.scope -ne 'postgres-only') {
        throw 'Unsupported or incomplete PostgreSQL backup.'
    }
    if ($metadata.systemIdentifier -notmatch '^\d+$' -or $metadata.imageId -notmatch '^sha256:[0-9a-f]{64}$' -or
        $metadata.dbUser -notmatch '^[a-zA-Z_][a-zA-Z0-9_]{0,62}$') { throw 'Invalid backup metadata.' }
    $manifest = Join-Path $Directory 'base/backup_manifest'
    if (!(Test-Path -LiteralPath $manifest -PathType Leaf)) { throw 'Missing backup_manifest.' }
    if ((Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash -ne $metadata.baseManifestSha256) {
        throw 'Backup manifest hash mismatch. Recovery refused.'
    }
    return $metadata
}

function Test-PgContainerExists {
    param([string]$Name)
    $allNames = Invoke-PgDocker @('container', 'ls', '-a', '--format', '{{.Names}}')
    return @($allNames -split "`n") -contains $Name
}

function Test-PgVolumeExists {
    param([string]$Name)
    $allNames = Invoke-PgDocker @('volume', 'ls', '--format', '{{.Name}}')
    return @($allNames -split "`n") -contains $Name
}

function Remove-PgRestore {
    param([Parameter(Mandatory=$true)][string]$Name)
    if ($Name -notmatch '^chatapp-pg-restore(?:-[a-z0-9][a-z0-9-]{0,31})?$') { throw 'Unsafe restore name.' }
    foreach ($containerName in @($Name, "$Name-prepare")) {
        if (Test-PgContainerExists $containerName) {
            $owner = Invoke-PgDocker @('inspect', '--format', '{{index .Config.Labels "chatapp.backup.restore"}}', $containerName)
            if ($owner -ne $Name) { throw "Container $containerName is not owned by this restore." }
            Invoke-PgDocker @('rm', '--force', $containerName) | Out-Null
        }
    }
    $volumeName = "$Name-data"
    if (Test-PgVolumeExists $volumeName) {
        $owner = Invoke-PgDocker @('volume', 'inspect', '--format', '{{index .Labels "chatapp.backup.restore"}}', $volumeName)
        if ($owner -ne $Name) { throw 'Refusing to delete an unrelated volume.' }
        Invoke-PgDocker @('volume', 'rm', $volumeName) | Out-Null
    }
}

function Remove-PgBackupStage {
    param([string]$Container, [string]$Stage)
    if ($Stage -notmatch '^/tmp/chatapp-pg-backup-[a-f0-9-]+$') { throw 'Unsafe backup staging path.' }
    Invoke-PgDocker @('exec', '--user', 'postgres', $Container, 'rm', '-rf', '--', $Stage) | Out-Null
}

function Wait-PgArchivedWal {
    param([object]$Source, [string]$WalFile, [int]$TimeoutSeconds = 60)
    if ($WalFile -notmatch '^[0-9A-F]{24}$') { throw 'Invalid WAL filename.' }
    $path = "/var/lib/postgresql/wal-archive/$($Source.SystemId)/$WalFile"
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $found = Invoke-PgDocker @('exec', $Source.Container, 'sh', '-c', 'if test -f "$1"; then echo yes; else echo no; fi', '--', $path)
        if ($found -eq 'yes') { return }
        Start-Sleep -Milliseconds 300
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "WAL archive timeout for $WalFile. Check pg_stat_archiver and archive volume permissions."
}

function Save-PgArchivedWal {
    param([object]$Source, [string]$BackupDirectory, [ValidatePattern('^[0-9A-F]{24}$')][string]$ThroughWal)
    $walDirectory = Join-Path $BackupDirectory 'wal'
    [IO.Directory]::CreateDirectory($walDirectory) | Out-Null
    $archivePath = "/var/lib/postgresql/wal-archive/$($Source.SystemId)"
    $baseManifest = Get-Content -LiteralPath (Join-Path $BackupDirectory 'base/backup_manifest') -Raw -Encoding UTF8 | ConvertFrom-Json
    $ranges = @($baseManifest.'WAL-Ranges')
    $startLsn = $ranges[0].'Start-LSN'
    if ($startLsn -notmatch '^[0-9A-F]+/[0-9A-F]+$') { throw 'Invalid base WAL range.' }
    $firstWal = Invoke-PgSql -Container $Source.Container -DbUser $Source.DbUser -Sql "SELECT pg_walfile_name('$startLsn'::pg_lsn);"
    $names = Invoke-PgDocker @('exec', '--user', 'postgres', $Source.Container, 'ls', '-1', $archivePath)
    # Published archive files are immutable. Never traverse/copy the whole live
    # directory: an archiver's *.tmp can disappear while docker cp is walking it.
    foreach ($name in @($names -split "`n" | Where-Object { $_ -match '^([0-9A-F]{24}(\.[0-9A-F]{8}\.backup)?|[0-9A-F]{8}\.history)$' })) {
        if ($name -notmatch '\.history$') {
            $segment = $name.Substring(0,24)
            if ([string]::CompareOrdinal($segment,$firstWal) -lt 0 -or ($ThroughWal -and [string]::CompareOrdinal($segment,$ThroughWal) -gt 0)) { continue }
        }
        $remotePath = "$archivePath/$name"
        $hashLine = Invoke-PgDocker @('exec', '--user', 'postgres', $Source.Container, 'sha256sum', $remotePath)
        $expectedHash = ($hashLine -split '\s+')[0]
        $destination = Join-Path $walDirectory $name
        if ((Test-Path -LiteralPath $destination -PathType Leaf) -and (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $expectedHash) { continue }
        $temporaryPath = "$destination.$([Guid]::NewGuid().ToString('N')).partial"
        try {
            Invoke-PgDocker @('cp', "$($Source.Container):$remotePath", $temporaryPath) | Out-Null
            if ((Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA256).Hash -ne $expectedHash) { throw "WAL transfer checksum mismatch: $name" }
            Move-Item -LiteralPath $temporaryPath -Destination $destination -Force
        } finally { if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath } }
    }
    return $walDirectory
}
