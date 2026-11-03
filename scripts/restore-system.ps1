param([ValidatePattern('^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}$')][string]$BackupId, [switch]$Cleanup)
. "$PSScriptRoot/postgres-backup-common.ps1"
$systemRoot = Join-Path $script:PgBackupProjectRoot 'infra/backups/system'
$runtime = Join-Path $systemRoot 'recovery-runtime'
[IO.Directory]::CreateDirectory($runtime) | Out-Null
$composePath = Join-Path $runtime 'compose.json'
$restoreName = 'chatapp-pg-restore-system'
$project = 'chatapp-recovery'

function Invoke-RecoveryCompose {
    param([string[]]$ComposeArgs)
    return Invoke-PgDocker (@('compose','--env-file',(Join-Path $script:PgBackupProjectRoot '.env'),'-p',$project,'-f',$composePath) + $ComposeArgs)
}
function Remove-RecoveryStack {
    # Only resources with this exact project label, fixed generated prefix and
    # the known PostgreSQL restore label can be removed. Never use source down -v.
    $names = (Invoke-PgDocker @('ps','-a','--filter',"label=com.docker.compose.project=$project",'--format','{{.Names}}')) -split "`n" | Where-Object { $_ }
    foreach ($name in $names) {
        if ($name -notmatch '^chatapp-recovery-[a-z0-9-]+$') { throw 'Unexpected recovery container name.' }
        Invoke-PgDocker @('rm','--force',$name) | Out-Null
    }
    $volumes = (Invoke-PgDocker @('volume','ls','--filter',"label=com.docker.compose.project=$project",'--format','{{.Name}}')) -split "`n" | Where-Object { $_ }
    foreach ($volume in $volumes) {
        if ($volume -notmatch '^chatapp-recovery_[a-z0-9-]+$') { throw 'Unexpected recovery volume name.' }
        Invoke-PgDocker @('volume','rm',$volume) | Out-Null
    }
    $networks = (Invoke-PgDocker @('network','ls','--filter',"label=com.docker.compose.project=$project",'--format','{{.Name}}')) -split "`n" | Where-Object { $_ }
    foreach ($network in $networks) {
        if ($network -ne 'chatapp-recovery_network') { throw 'Unexpected recovery network.' }
        Invoke-PgDocker @('network','rm',$network) | Out-Null
    }
    Remove-PgRestore $restoreName
}
if ($Cleanup) { Remove-RecoveryStack; Write-Host 'Recovery resources removed. Source untouched.'; return }
if (!$BackupId) { throw 'BackupId is required.' }
if (Test-PgContainerExists 'chatapp-recovery-web') { throw 'A recovery stack already exists. Clean it from the source backup page first.' }
$directory = Get-PgBackupDirectory (Join-Path $systemRoot $BackupId)
$manifest = Get-Content -LiteralPath (Join-Path $directory 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.version -ne 1 -or $manifest.id -ne $BackupId) { throw 'Unsupported system backup.' }
foreach ($file in $manifest.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $directory $file.path))
    if (!$path.StartsWith($directory + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe manifest path.' }
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "Backup integrity failed: $($file.path)" }
}
$restored = & "$PSScriptRoot/restore-postgres.ps1" -BackupDirectory (Join-Path $directory 'postgres') -TargetName $manifest.pgTarget -RestoreName $restoreName
try {
    Invoke-PgDocker @('exec','--user','postgres',$restoreName,'pg_ctl','promote','-D','/var/lib/postgresql/data','-w') | Out-Null
    Invoke-PgDocker @('stop',$restoreName) | Out-Null
    Invoke-PgDocker @('rm',$restoreName) | Out-Null # Keep its OWN labelled volume for the isolated stack.
    $credentials = Join-Path $script:PgBackupProjectRoot 'infra/attachments.local.env'
    $init = (Join-Path $script:PgBackupProjectRoot 'infra/mongo').Replace('\','/')
    $nginx = (Join-Path $script:PgBackupProjectRoot 'infra/backup/recovery-nginx.conf').Replace('\','/')
    $web = if (Test-Path -LiteralPath (Join-Path $directory 'web/index.html')) { Join-Path $directory 'web' } else { Join-Path $script:PgBackupProjectRoot 'frontend/chat-app/dist/chat-app' }
    if (!(Test-Path -LiteralPath (Join-Path $web 'index.html'))) { throw 'Build Angular production before restoring the web.' }
    $mongoUri = 'mongodb://mongo-router:27017/?appName=ChatAppRecovery&serverSelectionTimeoutMS=3000'
    $baseEnv = @{
        Jwt__Secret='${JWT_SECRET}'; Backup__CoordinationEnabled='false'; Backup__RecoveryMode='true'; Backup__Enabled='false'; Backup__RestoreId=$BackupId
        ConnectionStrings__Redis='redis:6379,abortConnect=false'; Kafka__BootstrapServers='kafka:29092'
        GrpcServices__IdentityService='http://identity:5011'; GrpcServices__GroupService='http://group:5012'; GrpcServices__PresenceService='http://presence:5015'
        Mongo__ConnectionString=$mongoUri; Mongo__Database='chat_history'; Attachments__Endpoint='http://attachment-storage:8333'; Attachments__Bucket='chatapp-attachments'
    }
    function Service {
        param([string]$Image,[string[]]$Command,[hashtable]$Extra=@{})
        $env = @{}; foreach($key in $baseEnv.Keys){$env[$key]=$baseEnv[$key]}; foreach($key in $Extra.Keys){$env[$key]=$Extra[$key]}
        return @{ image=$Image; command=$Command; environment=$env; env_file=@($credentials); networks=@('network') }
    }
    $services = [ordered]@{}
    $services['postgres'] = @{ image=(Get-PgBackupMetadata (Join-Path $directory 'postgres')).imageId; command=@('postgres','-c','listen_addresses=*','-c','archive_mode=off'); volumes=@('pgdata:/var/lib/postgresql/data'); networks=@('network') }
    $services['redis'] = @{image='redis:7.4-alpine';command=@('redis-server','--appendonly','yes');volumes=@('redis-data:/data');networks=@('network')}
    foreach ($spec in @(@('mongo-config','chat-config','27019','configsvr','configdb'),@('mongo-shard-1','chat-shard-1','27018','shardsvr','db'),@('mongo-shard-2','chat-shard-2','27018','shardsvr','db'))) {
        $services[$spec[0]] = @{image='mongo:8.0.34'; command=@('mongod',"--$($spec[3])",'--replSet',$spec[1],'--port',$spec[2],'--bind_ip_all','--wiredTigerCacheSizeGB','0.25');volumes=@("$($spec[0])-data:/data/$($spec[4])");networks=@('network')}
    }
    $services['mongo-router'] = @{image='mongo:8.0.34';command=@('mongos','--configdb','chat-config/mongo-config:27019','--bind_ip_all','--port','27017');networks=@('network')}
    $services['mongo-init'] = @{image='mongo:8.0.34';entrypoint=@('mongosh','--nodb','--quiet','/init/init.js');volumes=@("${init}:/init:ro");networks=@('network')}
    $services['attachment-storage'] = @{image='chrislusf/seaweedfs:4.51';command=@('mini','-dir=/data','-ip=attachment-storage','-ip.bind=0.0.0.0','-admin.ui=false');env_file=@($credentials);volumes=@('attachment-data:/data');networks=@('network')}
    $clusterId = [Convert]::ToBase64String([Guid]::NewGuid().ToByteArray()).TrimEnd('=').Replace('+','-').Replace('/','_')
    $services['kafka'] = @{image='apache/kafka:4.1.0';environment=@{
        KAFKA_NODE_ID='1';KAFKA_PROCESS_ROLES='broker,controller';KAFKA_CONTROLLER_QUORUM_VOTERS='1@kafka:9093';KAFKA_CONTROLLER_LISTENER_NAMES='CONTROLLER'
        KAFKA_LISTENERS='INTERNAL://:29092,CONTROLLER://:9093';KAFKA_ADVERTISED_LISTENERS='INTERNAL://kafka:29092';KAFKA_LISTENER_SECURITY_PROTOCOL_MAP='INTERNAL:PLAINTEXT,CONTROLLER:PLAINTEXT';KAFKA_INTER_BROKER_LISTENER_NAME='INTERNAL'
        KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR='1';KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR='1';KAFKA_TRANSACTION_STATE_LOG_MIN_ISR='1';KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS='0';KAFKA_AUTO_CREATE_TOPICS_ENABLE='false';KAFKA_LOG_DIRS='/var/lib/kafka/data';CLUSTER_ID=$clusterId;KAFKA_HEAP_OPTS='-Xmx256m -Xms256m'
    };volumes=@('kafka-data:/var/lib/kafka/data');networks=@('network')}
    $services['identity'] = Service 'chatapp/identity-service' @('ChatApp.IdentityService.dll') @{
        ConnectionStrings__IdentityDb='Host=postgres;Database=identity_db;Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}'
        Kestrel__Endpoints__Rest__Url='http://0.0.0.0:5001';Kestrel__Endpoints__Grpc__Url='http://0.0.0.0:5011'
    }
    $services['group'] = Service 'chatapp/group-service' @('ChatApp.GroupService.dll') @{
        ConnectionStrings__GroupDb='Host=postgres;Database=group_db;Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}'
        Kestrel__Endpoints__Rest__Url='http://0.0.0.0:5002';Kestrel__Endpoints__Grpc__Url='http://0.0.0.0:5012'
    }
    $services['notification'] = Service 'chatapp/notification-service' @('ChatApp.NotificationService.dll') @{
        ConnectionStrings__NotificationDb='Host=postgres;Database=notification_db;Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}';ASPNETCORE_URLS='http://+:5004'
    }
    $services['chat'] = Service 'chatapp/chat-service' @() @{ ASPNETCORE_URLS='http://+:8080' }
    $services['presence'] = Service 'chatapp/presence-service' @() @{}
    $services['gateway'] = Service 'chatapp/gateway' @('ChatApp.Gateway.dll') @{
        ASPNETCORE_URLS='http://+:5000';ReverseProxy__Clusters__identity__Destinations__d1__Address='http://identity:5001'
        ReverseProxy__Clusters__group__Destinations__d1__Address='http://group:5002';ReverseProxy__Clusters__notification__Destinations__d1__Address='http://notification:5004';ReverseProxy__Clusters__chat__Destinations__d1__Address='http://chat:8080'
    }
    $services['mongo-import'] = Service 'chatapp/chat-service' @('--backup-data','import','--directory','/bundle/mongo') @{}
    $services['mongo-import']['volumes'] = @($directory.Replace('\','/') + ':/bundle:ro')
    $services['kafka-import'] = Service 'chatapp/gateway' @('ChatApp.Gateway.dll','--backup-kafka','import','--directory','/bundle/kafka') @{}
    $services['kafka-import']['volumes'] = @($directory.Replace('\','/') + ':/bundle:ro')
    foreach($name in @('identity','group','notification','gateway','presence','chat')) {
        $image = $manifest.images.$name
        if ($image -notmatch '^sha256:[0-9a-f]{64}$') { throw 'Missing pinned application image.' }
        $services[$name]['image'] = $image
    }
    $services['mongo-import']['image'] = $manifest.images.chat
    $services['kafka-import']['image'] = $manifest.images.gateway
    $services['web'] = @{image='nginx:1.29-alpine';ports=@('127.0.0.1:4201:80');volumes=@(($web.Replace('\','/') + ':/usr/share/nginx/html:ro'),"${nginx}:/etc/nginx/nginx.conf:ro");networks=@('network')}
    foreach($name in $services.Keys){$services[$name]['container_name']="chatapp-recovery-$name"}
    $volumes = @{pgdata=@{external=$true;name=$restored.Volume};'redis-data'=@{};'mongo-config-data'=@{};'mongo-shard-1-data'=@{};'mongo-shard-2-data'=@{};'attachment-data'=@{};'kafka-data'=@{}}
    Write-PgBackupJson -Path $composePath -Value @{services=$services;volumes=$volumes;networks=@{network=@{name='chatapp-recovery_network'}}}
    Invoke-RecoveryCompose @('up','-d','postgres','redis','mongo-config','mongo-shard-1','mongo-shard-2','mongo-router','attachment-storage','kafka') | Out-Null
    Invoke-RecoveryCompose @('run','--rm','--no-deps','mongo-init') | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        try { Invoke-PgDocker @('exec','chatapp-recovery-kafka','/opt/kafka/bin/kafka-topics.sh','--bootstrap-server','kafka:29092','--list') | Out-Null; break } catch { if([DateTime]::UtcNow -gt $deadline){throw}; Start-Sleep -Seconds 1 }
    } while($true)
    Invoke-RecoveryCompose @('run','--rm','--no-deps','mongo-import') | Out-Null
    Invoke-RecoveryCompose @('run','--rm','--no-deps','kafka-import') | Out-Null
    Invoke-RecoveryCompose @('up','-d','identity','group','notification','presence','chat','gateway','web') | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        try {
            Invoke-PgDocker @('exec','chatapp-recovery-redis','sh','-ec','wget -qO- http://identity:5001/; wget -qO- http://group:5002/; wget -qO- http://notification:5004/; wget -qO- http://chat:8080/') | Out-Null
            $response = Invoke-WebRequest -Uri 'http://localhost:4201/api/system/mode' -UseBasicParsing -TimeoutSec 5
            if (!(($response.Content | ConvertFrom-Json).recoveryMode)) { throw 'Recovery mode missing.' }
            break
        } catch { if([DateTime]::UtcNow -gt $deadline){throw}; Start-Sleep -Seconds 1 }
    } while($true)
    Write-Host 'PASS: isolated recovered web ready at http://localhost:4201 . Source unchanged.'
} catch {
    Remove-RecoveryStack
    throw
}
