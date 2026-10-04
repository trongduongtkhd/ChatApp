# Demo Phần 5: Transactional Outbox + Kafka giữ sự kiện khi Kafka tạm chết.
# Kịch bản: tắt Kafka → đăng ký user (vẫn thành công) → sự kiện nằm chờ trong outbox_messages
#           → bật Kafka lại → outbox tự gửi bù → group-service nhận → user xuất hiện trong user_snapshots.
#
# Cần chạy trước (2 terminal):
#   dotnet run --project backend/src/Services/Identity/ChatApp.IdentityService
#   dotnet run --project backend/src/Services/Group/ChatApp.GroupService
# Chạy (tại thư mục gốc):
#   .\scripts\demo-kafka-outbox.ps1

# Không đặt $ErrorActionPreference = "Stop": PowerShell 5.1 coi dòng stderr của docker ("Container kafka Stopping")
# là lỗi khi output bị chuyển hướng. Chỉ bắt lỗi ở lời gọi HTTP (-ErrorAction Stop).
$identity = "http://localhost:5001"

function Step($text) { Write-Host "`n=== $text" -ForegroundColor Cyan }
function Sql($db, $query) { docker exec postgres psql -U chatapp -d $db -c $query }
function SqlValue($db, $query) { (docker exec postgres psql -U chatapp -d $db -tAc $query) }

foreach ($port in 5001, 5002) {
    try { Invoke-RestMethod "http://localhost:$port/" -TimeoutSec 2 -ErrorAction Stop | Out-Null }
    catch { Write-Host "Service ở port $port chưa chạy. Xem hướng dẫn đầu file." -ForegroundColor Red; exit 1 }
}

$userName = "demo_" + (Get-Date -Format "HHmmss")

Step "1. Tắt Kafka"
docker compose stop kafka 2>&1 | Out-Null
Write-Host "Kafka: $(docker inspect -f '{{.State.Status}}' kafka)"

Step "2. Đăng ký $userName khi Kafka đang tắt"
$body = @{ userName = $userName; email = "$userName@test.com"; password = "Passw0rd!"; displayName = "Demo Outbox" } | ConvertTo-Json
$sw = [Diagnostics.Stopwatch]::StartNew()
try { $user = Invoke-RestMethod "$identity/api/auth/register" -Method Post -ContentType "application/json" -Body $body -ErrorAction Stop }
catch { Write-Host "Đăng ký thất bại: $($_.Exception.Message)" -ForegroundColor Red; docker compose start kafka 2>&1 | Out-Null; exit 1 }
Write-Host "Đăng ký THÀNH CÔNG sau $($sw.ElapsedMilliseconds) ms, userId = $($user.id)" -ForegroundColor Green

Step "3. Chờ 12 giây: OutboxPublisher thử gửi, thất bại, thử lại..."
Start-Sleep 12
Sql "identity_db" "select event_type, attempts, processed_at, last_error from outbox_messages where key = '$($user.id)'"
Write-Host "user_snapshots (group_db) có $userName chưa: $(SqlValue 'group_db' "select count(*) from user_snapshots where user_id = '$($user.id)'") dòng"

Step "4. Bật Kafka lại"
docker compose start kafka 2>&1 | Out-Null
$sw.Restart()
do { Start-Sleep 2 } until ((docker inspect -f "{{.State.Health.Status}}" kafka) -eq "healthy")
Write-Host "Kafka healthy sau $([int]$sw.Elapsed.TotalSeconds) giây"

Step "5. Chờ outbox gửi bù và group-service xử lý"
$sw.Restart()
do {
    Start-Sleep 1
    $found = SqlValue "group_db" "select count(*) from user_snapshots where user_id = '$($user.id)'"
} until ($found -eq "1" -or $sw.Elapsed.TotalSeconds -gt 90)

if ($found -eq "1") {
    Write-Host "$userName ĐÃ XUẤT HIỆN trong user_snapshots sau $([int]$sw.Elapsed.TotalSeconds) giây kể từ khi Kafka sẵn sàng" -ForegroundColor Green
} else {
    Write-Host "Quá 90 giây chưa thấy $userName. Xem log của identity-service và group-service." -ForegroundColor Red
}
Sql "identity_db" "select event_type, attempts, processed_at, last_error from outbox_messages where key = '$($user.id)'"
Sql "group_db" "select * from user_snapshots where user_id = '$($user.id)'"
