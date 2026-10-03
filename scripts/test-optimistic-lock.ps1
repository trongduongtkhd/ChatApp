# Demo Optimistic Locking (Phần 4, Chương 6).
# Hai người cùng đọc nhóm ở version V, rồi CÙNG LÚC gửi PUT kèm version V.
# Mong đợi: đúng 1 request 200 (thắng), 1 request 409 Conflict (thua) – không ai ghi đè mất dữ liệu của người kia.
#
# Cần chạy trước: docker compose up -d, identity-service, group-service, Gateway.
# Chạy: .\scripts\test-optimistic-lock.ps1 [-UserName duong] [-Password 123456]

param(
    [string]$UserName = "duong",
    [string]$Password = "123456",
    [string]$BaseUrl = "http://localhost:5000"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

$client = New-Object System.Net.Http.HttpClient
$client.BaseAddress = [Uri]$BaseUrl

function Send($method, $path, $body) {
    $req = New-Object System.Net.Http.HttpRequestMessage ([System.Net.Http.HttpMethod]::new($method)), $path
    if ($null -ne $body) {
        $req.Content = New-Object System.Net.Http.StringContent (($body | ConvertTo-Json)), ([Text.Encoding]::UTF8), "application/json"
    }
    return $client.SendAsync($req)
}

function Read-Json($task) {
    $resp = $task.Result
    $text = $resp.Content.ReadAsStringAsync().Result
    [pscustomobject]@{ Status = [int]$resp.StatusCode; Body = if ($text) { $text | ConvertFrom-Json } else { $null } }
}

# 1. Đăng nhập, gắn JWT cho mọi request sau.
$login = Read-Json (Send "POST" "/api/auth/login" @{ userName = $UserName; password = $Password })
if ($login.Status -ne 200) { throw "Đăng nhập thất bại ($($login.Status))" }
$client.DefaultRequestHeaders.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue "Bearer", $login.Body.accessToken

# 2. Tạo nhóm để thử, ghi lại version.
$group = (Read-Json (Send "POST" "/api/groups" @{ name = "Demo optimistic lock"; description = "ban dau" })).Body
$v = $group.version
Write-Host "Tạo nhóm $($group.id), version = $v"

# 3. Bắn 2 PUT SONG SONG với CÙNG version (không chờ request 1 xong mới gửi request 2).
Write-Host "`nGửi đồng thời 2 PUT cùng version $v ..."
$t1 = Send "PUT" "/api/groups/$($group.id)" @{ name = "Ten cua NGUOI A"; description = "A sua"; version = $v }
$t2 = Send "PUT" "/api/groups/$($group.id)" @{ name = "Ten cua NGUOI B"; description = "B sua"; version = $v }
[System.Threading.Tasks.Task]::WaitAll(@($t1, $t2))

$r1 = Read-Json $t1; $r2 = Read-Json $t2
Write-Host ("  Request A: {0} {1}" -f $r1.Status, $(if ($r1.Status -eq 200) { "-> version moi $($r1.Body.version)" } else { $r1.Body.title }))
Write-Host ("  Request B: {0} {1}" -f $r2.Status, $(if ($r2.Status -eq 200) { "-> version moi $($r2.Body.version)" } else { $r2.Body.title }))

# 4. Xem dữ liệu cuối cùng: chỉ thay đổi của bên thắng được lưu.
$detail = (Read-Json (Send "GET" "/api/groups/$($group.id)" $null)).Body
Write-Host "`nDữ liệu trong DB: name = '$($detail.group.name)', version = $($detail.group.version)"

# 5. Gửi lại version cũ lần nữa → chắc chắn 409.
$r3 = Read-Json (Send "PUT" "/api/groups/$($group.id)" @{ name = "Thu lai"; description = "x"; version = $v })
Write-Host "PUT lại với version cũ $v -> $($r3.Status)"

$codes = @($r1.Status, $r2.Status) | Sort-Object
if (($codes -join ",") -eq "200,409" -and $r3.Status -eq 409) {
    Write-Host "`nKẾT QUẢ: ĐÚNG – 1 request thắng (200), 1 request bị từ chối (409)." -ForegroundColor Green
} else {
    Write-Host "`nKẾT QUẢ: KHÔNG như mong đợi ($($codes -join ', '), lần 3: $($r3.Status))." -ForegroundColor Red
}

# 6. Dọn nhóm thử.
(Send "DELETE" "/api/groups/$($group.id)" $null).Result | Out-Null
