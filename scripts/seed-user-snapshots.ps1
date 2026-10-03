# CÔNG CỤ TEST TẠM THỜI (Phần 4) – Phần 5 sẽ thay bằng sự kiện Kafka identity.user-registered.
#
# Copy user từ identity_db.users sang group_db.user_snapshots để thêm được thành viên vào nhóm.
# Script này CỐ TÌNH đọc chéo database – việc mà service KHÔNG được làm (database per service).
# Nó minh họa: khi chưa có sự kiện, bản sao dữ liệu chỉ đồng bộ được bằng tay.
#
# Chạy (PowerShell, tại thư mục gốc, cần container postgres đang chạy):
#   .\scripts\seed-user-snapshots.ps1

$ErrorActionPreference = "Stop"

# Lấy POSTGRES_USER từ .env (mặc định chatapp).
$pgUser = "chatapp"
if (Test-Path .env) {
    $line = Get-Content .env | Where-Object { $_ -match '^\s*POSTGRES_USER=(.+)$' } | Select-Object -First 1
    if ($line) { $pgUser = ($line -split '=', 2)[1].Trim() }
}

# Chạy hoàn toàn bên trong container: xuất từ identity_db → nạp vào bảng tạm của group_db → upsert.
$sh = @"
set -eo pipefail
psql -U $pgUser -d identity_db -Atc "copy (select id, user_name, display_name from users) to stdout" |
psql -U $pgUser -d group_db -v ON_ERROR_STOP=1 \
  -c "create temp table incoming (user_id uuid, user_name varchar(50), display_name varchar(100))" \
  -c "copy incoming from stdin" \
  -c "insert into user_snapshots (user_id, user_name, display_name) select user_id, user_name, display_name from incoming on conflict (user_id) do update set user_name = excluded.user_name, display_name = excluded.display_name"
"@

# Ghi lệnh ra file tạm (UTF-8 KHÔNG BOM, xuống dòng kiểu Linux) rồi docker cp vào container.
# Không truyền qua tham số hay stdin vì PowerShell 5.1 làm hỏng dấu ngoặc kép hoặc chèn BOM.
$tmp = Join-Path $env:TEMP "seed-user-snapshots.sh"
[IO.File]::WriteAllText($tmp, $sh.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding $false))
docker cp $tmp postgres:/tmp/seed-user-snapshots.sh | Out-Null
docker exec postgres sh /tmp/seed-user-snapshots.sh
if ($LASTEXITCODE -ne 0) { throw "Seed thất bại" }

docker exec postgres psql -U $pgUser -d group_db -c "select user_id, user_name, display_name from user_snapshots order by user_name"
