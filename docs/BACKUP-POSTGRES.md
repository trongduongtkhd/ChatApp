# Chương 7 – Bài thực hành riêng: PostgreSQL online backup và phục hồi theo mốc

Đây là bài thực hành PostgreSQL riêng (bước 13.1). Hướng dẫn sao lưu toàn ứng dụng và demo web hiện tại nằm trong [BACKUP.md](BACKUP.md).

## 1. Kết quả cần hiểu

```text
Database nguồn:   có A → tạo bản nền → thêm B → đặt mốc → xóa B
                                             │
Database phục hồi: bản nền + áp dụng WAL đến mốc này → có A và B
```

B xuất hiện SAU bản nền. Vì vậy, việc phục hồi được B chứng minh đã sử dụng WAL, không chỉ đọc bản nền. Việc B vẫn có ở bản phục hồi chứng minh đã dừng trước thao tác xóa.

PostgreSQL nguồn vẫn nhận đọc/ghi trong lúc backup. Bài test chỉ thay đổi database mới `backup_demo_<id>`, tính fingerprint dữ liệu ứng dụng trước/sau và dọn database demo khi kết thúc.

Nguồn và nơi phục hồi là hai container/volume khác nhau. Container phục hồi không có mạng/cổng, chỉ cho đọc tại mốc; không có service của ChatApp kết nối vào đó.

## 2. Những file thay đổi có nhiệm vụ gì?

| File | Nhiệm vụ | Khái niệm/chương |
|---|---|---|
| `docker-compose.yml` | Thêm volume archive, chuẩn bị quyền ghi, bật archive ở PostgreSQL | Volume/tiến trình (3), sao lưu (7) |
| `infra/postgres/archive-wal.sh` | PostgreSQL gọi mỗi khi cần cất một đoạn WAL; tách theo system ID, ghi nguyên tử, không đè nội dung khác | WAL, durability, atomic publication (6, 7) |
| `scripts/postgres-backup-common.ps1` | Gọi Docker đúng cách trên PowerShell 5.1, kiểm tra nguồn/bundle, dọn đúng tài nguyên có nhãn do restore tạo | Validation, cô lập môi trường (7, 8) |
| `scripts/backup-postgres.ps1` | Chạy `pg_basebackup`, verify, xuất dữ liệu và metadata, lấy WAL đã archive | Base backup, online backup (7) |
| `scripts/new-postgres-restore-point.ps1` | Đặt tên mốc, đóng WAL segment, đợi archive xong và đưa WAL vào bộ backup | Restore point, LSN, WAL archiving (7) |
| `scripts/restore-postgres.ps1` | Verify bản nền/WAL và phục hồi đến đúng mốc trên volume mới | PITR, replay, kiểm tra toàn vẹn (7) |
| `scripts/test-postgres-backup.ps1` | Demo A/B/xóa B, ghi đồng thời, thử lỗi và đối chiếu dữ liệu thật | Kịch bản demo và bằng chứng kiểm thử (7) |
| `.gitignore` | Bỏ qua toàn bộ `infra/backups/postgres/` | Backup chứa dữ liệu thật, không đưa vào Git |
| `.gitattributes` | Giữ file shell ở dạng xuống dòng LF khi checkout trên Windows | Tương thích script Linux trong Docker |
| `docs/DESIGN.md`, `PLAN.md`, `PROGRESS.md` | Ghi thiết kế đã duyệt, phạm vi và kết quả nghiệm thu | Tài liệu báo cáo |

Backend/Angular không đổi trong bước này; không có package npm/NuGet mới.

## 3. Các khái niệm mới

### Bản nền – base backup

Là bản dữ liệu dùng làm điểm xuất phát. Ở đây dùng `pg_basebackup` để lấy bản vật lý của toàn bộ PostgreSQL instance, gồm các database của service. Công cụ phối hợp với PostgreSQL khi server vẫn chạy; không phải tự copy volume đang ghi.

### WAL – Write-Ahead Log

Nhật ký nội bộ PostgreSQL ghi các thay đổi dữ liệu. Muốn phục hồi theo mốc, cần bản nền phù hợp và chuỗi WAL không bị đứt từ thời điểm cần thiết của bản nền đến mốc phục hồi. Chỉ có nhật ký mà không có bản nền thích hợp cũng không đủ.

PostgreSQL có thể tái sử dụng WAL trong `pg_wal`. Vì vậy bật `archive_mode` để giữ bản riêng trước khi WAL cũ bị tái sử dụng. `archive_timeout=60s` giúp đóng segment theo thời gian khi có hoạt động; nó không phải lời hứa chỉ mất tối đa 60 giây trong mọi sự cố. Demo dùng `pg_switch_wal` và chờ archive để xác nhận file đã được giữ thật.

### LSN – địa chỉ trong WAL

Ví dụ `0/8000370`. Hãy hiểu là một địa chỉ trong nhật ký, không phải giờ trên đồng hồ và không phải ID tin nhắn. Script kiểm địa chỉ replay đã dừng đúng với LSN ghi khi tạo mốc.

### Named restore point – mốc phục hồi có tên

`pg_create_restore_point('before_delete_...')` ghi một dấu mốc vào WAL. Nó KHÔNG tạo thêm bản backup đầy đủ. Khi phục hồi, PostgreSQL đọc WAL đến dấu mốc này rồi dừng. Mốc được đặt SAU khi bản nền hoàn tất; không thể dùng một bản nền mới để quay ngược tùy ý về trước thời điểm nó có thể phục hồi.

### PITR và replay

PITR là phục hồi theo thời điểm/mốc. Replay là áp dụng lại những thay đổi đã ghi trong WAL. PostgreSQL thực hiện các thao tác đó; script PowerShell không tự viết lại mọi câu INSERT/UPDATE/DELETE của người dùng.

### Manifest và checksum

`base/backup_manifest` là danh sách file/checksum do PostgreSQL tạo; `pg_verifybackup` kiểm bản nền theo danh sách này. `metadata.json` là hồ sơ do script tạo: nguồn nào, image ID nào, system ID nào, mốc nào, các WAL nào và SHA-256 của chúng. Chỉ copy WAL đã được công bố hoàn tất; kiểm SHA-256 sau khi truyền, không copy file `.tmp` mà archiver đang ghi. Checksum giúp phát hiện file thiếu/hỏng, không thay thế việc thử phục hồi.

### Pause tại mốc

Script dùng `recovery_target_action=pause`. Database phục hồi nhận truy vấn đọc, nhưng không cho ghi và không tiếp tục chạy qua thao tác xóa. Đây là bản dùng để kiểm chứng; không tự chuyển ứng dụng sang nó.

### Khác với `pg_dump`

`pg_dump` vẫn hữu ích cho xuất/khôi phục logic từng database. Nhưng file dump không ghép với WAL để thực hiện cơ chế PITR này. Bước 13.1 dùng bản vật lý `pg_basebackup` + WAL. Đây là thay đổi so với gợi ý ban đầu sau khi người dùng chọn backup trực tuyến và phục hồi theo mốc.

## 4. Khởi động và demo tự động – nên làm trước

Mở PowerShell ở thư mục gốc dự án:

```powershell
cd D:\Projects\ChatApp
docker compose up -d postgres
docker compose ps postgres
.\scripts\test-postgres-backup.ps1 -KeepRestore
```

Lần đầu áp dụng cấu hình archive cần tạo lại/khởi động PostgreSQL. Nếu nguồn đang chạy, thao tác áp dụng cấu hình lần đầu có một khoảng gián đoạn. Những lần chạy `backup-postgres.ps1` sau đó không dừng/restart PostgreSQL. Test kiểm `StartedAt` của container không đổi và ghi SQL thực sự trong lúc `pg_stat_progress_basebackup` đang có phiên backup.

Nếu PowerShell chặn thực thi script, chỉ cho phép trong tiến trình chạy demo này:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-postgres-backup.ps1 -KeepRestore
```

Script giới hạn tốc độ bản nền để quan sát ghi đồng thời; thời gian chạy phụ thuộc dung lượng dữ liệu và tốc độ Docker/ổ đĩa. Đây là kiểm thử PostgreSQL, không khởi động Angular hay các service khác. Muốn fingerprint dữ liệu thật khớp, chạy suite khi không có người/service khác thay đổi dữ liệu nghiệp vụ. Writer đồng thời của suite nằm trong database demo riêng.

Kết quả mong đợi:

- Có dòng `SQL writes succeeded during base backup (...)` với ít nhất hai dòng ghi thành công trong lúc lệnh base backup thực sự chạy.
- Có dòng `Primary did not restart during backup`.
- Nguồn còn A sau khi xóa B; bản phục hồi có A+B.
- Thiếu WAL/file hỏng/target sai/restore trùng tên bị từ chối.
- Fingerprint các bảng public trong `identity_db`, `group_db`, `chat_db`, `notification_db` giữ nguyên; database demo nguồn được dọn.
- Cuối cùng có `PASS: ... checks` và đường dẫn bộ backup; số kiểm tra cuối được ghi trong `test-report.json`.

Suite đã ghi test report; để xem kết quả bằng SQL:

Lần nghiệm thu 31/10/2026: **21/21 kiểm tra**, 51 dòng ghi đồng thời khi bản nền đang chạy, source không restart; đối chiếu bổ sung dữ liệu bốn DB trên bản PITR với nguồn khớp. Kết quả chi tiết ở `docs/PROGRESS.md` và `test-report.json` của bundle.

```powershell
$reportFile = Get-ChildItem .\infra\backups\postgres\*\test-report.json |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$report = Get-Content -LiteralPath $reportFile.FullName -Raw | ConvertFrom-Json
docker exec --user postgres $report.restoreContainer psql -U chatapp -d $report.fixtureDatabase -c "TABLE demo_messages;"
```

Nếu POSTGRES_USER của bạn khác `chatapp`, thay `-U chatapp` bằng user đang dùng. Hai dòng mong đợi:

```text
1 | A - before base backup
2 | B - committed after base backup
```

Xem log để thấy PostgreSQL dừng tại mốc:

```powershell
docker logs --tail 25 $report.restoreContainer
```

Dọn container/volume phục hồi riêng sau khi xem:

```powershell
. .\scripts\postgres-backup-common.ps1
Remove-PgRestore -Name $report.restoreContainer
```

Hàm chỉ dọn tên có prefix `chatapp-pg-restore` và nhãn sở hữu đúng; không dọn `postgres` hoặc `postgres-data`. Nếu không truyền `-KeepRestore`, suite tự dọn cả bản phục hồi. File backup được giữ để bạn xem/học; nó có thể chứa dữ liệu thật, nên không commit hoặc chia sẻ tùy ý.

## 5. Demo từng bước thủ công

Khởi động PostgreSQL như mục 4. Chạy lần lượt trong CÙNG cửa sổ PowerShell để giữ các biến:

```powershell
# Tao database demo rieng, ten ngau nhien de khong dung du lieu khac.
$dbUser = (docker exec postgres printenv POSTGRES_USER).Trim()
$suffix = [Guid]::NewGuid().ToString('N').Substring(0,12)
$demoDb = "backup_demo_$suffix"
$target = "before_delete_$suffix"
$restoreName = "chatapp-pg-restore-$suffix"
docker exec --user postgres postgres psql -U $dbUser -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE $demoDb;"
docker exec --user postgres postgres psql -U $dbUser -d $demoDb -v ON_ERROR_STOP=1 -c "CREATE TABLE demo_messages(id int PRIMARY KEY, content text); INSERT INTO demo_messages VALUES(1,'A - before base backup');"

# Ban nen chi duoc tao TRUOC khi them B.
$backup = .\scripts\backup-postgres.ps1
docker exec --user postgres postgres psql -U $dbUser -d $demoDb -v ON_ERROR_STOP=1 -c "INSERT INTO demo_messages VALUES(2,'B - after base backup');"

# Dat moc, cho WAL archive xong va copy WAL vao bo backup.
.\scripts\new-postgres-restore-point.ps1 -BackupDirectory $backup.BackupDirectory -Name $target

# Tao su co CHI trong database demo. Nguon con A.
docker exec --user postgres postgres psql -U $dbUser -d $demoDb -v ON_ERROR_STOP=1 -c "DELETE FROM demo_messages WHERE id=2; TABLE demo_messages;"

# Phuc hoi vao container/volume KHAC. Ban nay co A+B.
$restored = .\scripts\restore-postgres.ps1 -BackupDirectory $backup.BackupDirectory -TargetName $target -RestoreName $restoreName
docker exec --user postgres $restored.Container psql -U $dbUser -d $demoDb -c "TABLE demo_messages;"
```

Để quan sát khả năng ghi khi backup, có thể mở PowerShell thứ hai ghi thêm vào database demo trong khi cửa sổ thứ nhất chạy `backup-postgres.ps1 -MaxRateKb 4096`. Bài test tự động ở mục 4 kiểm chính xác điều này, không cần tự canh thời gian.

Sau demo thủ công, dọn đúng database và bản phục hồi vừa tạo:

```powershell
if ($demoDb -notmatch '^backup_demo_[a-f0-9]{12}$') { throw 'Sai ten database demo' }
docker exec --user postgres postgres psql -U $dbUser -d postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE $demoDb WITH (FORCE);"
. .\scripts\postgres-backup-common.ps1
Remove-PgRestore -Name $restoreName
```

## 6. Kịch bản demo lỗi và cách quan sát

| Sự cố | Cách thực hiện | Kết quả mong đợi |
|---|---|---|
| Xóa nhầm B sau mốc | Bài test tự động hoặc mục 5 | Nguồn chỉ có A, bản phục hồi có A+B |
| File bản nền hỏng | Suite đổi `PG_VERSION` trong CHÍNH bundle test, rồi trả bytes gốc | `pg_verifybackup` từ chối, tài nguyên restore thất bại được dọn |
| Manifest hỏng | Suite làm hỏng rồi phục hồi bytes gốc | Từ chối trước khi tạo container restore |
| Thiếu WAL | Suite tạm giấu file trong CHÍNH bundle test, rồi trả lại | Báo thiếu WAL, không báo thành công giả |
| WAL hỏng | Suite thay nội dung một WAL trong CHÍNH bundle test, rồi trả lại bytes gốc | Từ chối trước khi khởi động recovery |
| Tên mốc không có trong bundle | `restore-postgres.ps1 -TargetName unknown_point ...` | Từ chối |
| Tên container/volume phục hồi đã tồn tại | Chạy restore lần hai với cùng tên | Từ chối, không ghi đè |

Không dùng `docker compose down -v`, không xóa volume nguồn và không xóa dữ liệu thật để demo.

## 7. Đọc bộ backup

```text
infra/backups/postgres/<backup-id>/
├── base/                  # ban nen vat ly + backup_manifest
├── wal/                   # WAL da sao chep vao bundle
├── metadata.json          # nguon, hash, moc/LSN, WAL cua tung moc
├── recovery-*.conf         # cau hinh cho ban phuc hoi rieng
└── test-report.json        # chi co neu chay suite
```

Trong lúc backup chưa xong chỉ có `metadata.partial.json`; restore từ chối bundle chưa có metadata hoàn tất. Đặt thêm restore point sẽ đưa thêm WAL vào bundle và bổ sung danh sách mốc. Bản nền và WAL đã copy vẫn đọc được khi PostgreSQL nguồn không truy cập được; restore không kết nối vào nguồn.

Metadata đánh dấu bản nền hoàn tất không đồng nghĩa có thể phục hồi đến mọi thời điểm tương lai. Cần có WAL đầy đủ và mốc đã chốt; script hiện chỉ cho chọn những mốc được ghi trong bundle.

## 8. Giới hạn và phần tiếp theo

- Chứng minh PostgreSQL online backup/PITR; chưa chứng minh ChatApp toàn bộ phục hồi đồng nhất. Mốc PostgreSQL không tự là mốc chung với MongoDB/Kafka/ảnh.
- Chưa lập lịch, standby/failover, Redis AOF hoặc tự động chuyển ứng dụng sang bản phục hồi.
- WAL archive còn tăng theo hoạt động ghi và các bản nền cũng chiếm đĩa. Bước này không tự xóa WAL/bản nền cũ để tránh cắt đứt chuỗi phục hồi; cần chính sách lưu giữ ở bước sau.
- Backup local cùng máy không bảo vệ khi hỏng cả máy/ổ đĩa. Mã kiểm tra toàn vẹn phát hiện lỗi file; không thay thế bản sao ngoài máy hay bảo vệ truy cập vào backup.
- Test dùng PostgreSQL thực, PowerShell 5.1 và Docker. Không cần thay Node 14/Angular 12.
- Chạy từng thao tác backup/chốt mốc/restore lần lượt trên cùng bundle; chưa hỗ trợ nhiều người đồng thời sửa metadata của một bundle.

Tham khảo chính thức: [pg_basebackup](https://www.postgresql.org/docs/17/app-pgbasebackup.html), [WAL và PITR](https://www.postgresql.org/docs/17/continuous-archiving.html), [pg_verifybackup](https://www.postgresql.org/docs/17/app-pgverifybackup.html).
