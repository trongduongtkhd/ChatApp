# Demo gRPC chat-service → group-service (Phần 6, Chương 4).
# Đi qua endpoint TẠM /debug/membership của chat-service (5003); endpoint này gọi gRPC CheckMembership + GetMemberIds
# tới group-service (5012). Nhóm/thành viên được tạo qua REST của Gateway (5000) như người dùng thật.
#
# Cần chạy trước: docker compose up -d, identity-service, group-service, Gateway, chat-service.
# Chạy: .\scripts\test-grpc.ps1 [-UserName duong] [-Password 123456] [-OtherUserName lan]

param(
    [string]$UserName = "duong",
    [string]$Password = "123456",
    [string]$OtherUserName = "lan",
    [string]$GatewayUrl = "http://localhost:5000",
    [string]$ChatUrl = "http://localhost:5003"
)

$ErrorActionPreference = "Stop"
$utf8 = @{ ContentType = "application/json; charset=utf-8" }

function Check-Membership($groupId, $userId, $label) {
    $r = Invoke-RestMethod "$ChatUrl/debug/membership?groupId=$groupId&userId=$userId"
    Write-Host ("  {0,-34} isMember = {1,-5}  memberIds = {2}  ({3} ms)" -f $label, $r.isMember, $r.memberIds.Count, $r.elapsedMs)
    return $r.isMember
}

# 1. Đăng nhập, lấy userId của mình (claim sub) qua /api/users/me.
$login = Invoke-RestMethod "$GatewayUrl/api/auth/login" -Method Post @utf8 -Body (@{ userName = $UserName; password = $Password } | ConvertTo-Json)
$auth = @{ Authorization = "Bearer $($login.accessToken)" }
$me = Invoke-RestMethod "$GatewayUrl/api/users/me" -Headers $auth

# 2. Tìm user thứ hai trong user_snapshots của group-service (không có thì dùng GUID ngẫu nhiên).
$other = (Invoke-RestMethod "$GatewayUrl/api/groups/users/search?q=$OtherUserName" -Headers $auth) |
    Where-Object { $_.userName -eq $OtherUserName } | Select-Object -First 1
$otherId = if ($other) { $other.userId } else { [guid]::NewGuid().ToString() }

# 3. Tạo nhóm thử: mình là Owner.
$group = Invoke-RestMethod "$GatewayUrl/api/groups" -Method Post -Headers $auth @utf8 -Body (@{ name = "Demo gRPC" } | ConvertTo-Json)
Write-Host "Nhóm thử $($group.id); Owner = $UserName, người thứ hai = $OtherUserName ($otherId)`n"

$results = @()
$results += (Check-Membership $group.id $me.id "Owner ($UserName)") -eq $true
$results += (Check-Membership $group.id $otherId "$OtherUserName, chưa được thêm") -eq $false

# 4. Thêm thành viên rồi hỏi lại ngay: gRPC đọc thẳng group_db nên thấy liền (không chờ như Kafka).
if ($other) {
    Invoke-RestMethod "$GatewayUrl/api/groups/$($group.id)/members" -Method Post -Headers $auth @utf8 -Body (@{ userId = $otherId } | ConvertTo-Json) | Out-Null
    $results += (Check-Membership $group.id $otherId "$OtherUserName, vừa được thêm") -eq $true
} else {
    Write-Host "  (Không tìm thấy '$OtherUserName' trong user_snapshots → bỏ qua bước thêm thành viên)" -ForegroundColor Yellow
}

# 5. Xóa nhóm: nhóm không còn → không ai là thành viên, danh sách rỗng (không phải lỗi).
Invoke-RestMethod "$GatewayUrl/api/groups/$($group.id)" -Method Delete -Headers $auth | Out-Null
$results += (Check-Membership $group.id $me.id "Owner, sau khi xóa nhóm") -eq $false

if ($results -notcontains $false) {
    Write-Host "`nKẾT QUẢ: ĐÚNG – chat-service hỏi group-service qua gRPC, trả lời khớp dữ liệu group_db." -ForegroundColor Green
} else {
    Write-Host "`nKẾT QUẢ: KHÔNG như mong đợi." -ForegroundColor Red
}

Write-Host "`nDemo lỗi: tắt group-service rồi chạy:"
Write-Host "  curl.exe -s `"$ChatUrl/debug/membership?groupId=$($group.id)&userId=$($me.id)`""
Write-Host "  → mong đợi grpcStatus = Unavailable (HTTP 503)"
