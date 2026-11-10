# Chương 7 – Sao lưu và phục hồi toàn ChatApp

Sau khi bật chương 8, dùng `scripts/start-resilience-demo.ps1` để giữ mode Kubernetes/giám sát. Entry point `start-backup-demo.ps1` cũng giữ mode đã chọn bằng cách chuyển sang script này khi cần. Backup nguồn Kubernetes vẫn phục hồi ra web Compose riêng; xem `RESILIENCE.md`.

Nếu web vẫn chat được nhưng tạo backup báo **“Service chưa sẵn sàng cho backup: chat-…”**, kiểm tra chế độ chạy trước khi thử lại. Sau khi khởi động lại Docker, `docker compose up -d` có thể bật hai Chat Compose và Nginx Compose trong khi `mode.json` vẫn chọn Kubernetes; Pod cũ còn giữ IP hạ tầng trong hostAliases nên lỗi Redis. Web đi qua container đang sống, còn backup tìm heartbeat của Pod đã chết. Không xóa Redis key hoặc bỏ kiểm tra participant để né lỗi: điều đó có thể bỏ sót writer khi chốt mốc.

Tại thư mục gốc, dùng một trong hai lệnh đồng bộ toàn bộ môi trường, đợi `READY` rồi mới tạo backup:

```powershell
# Giữ Kubernetes/HPA, làm mới địa chỉ hạ tầng của Pod:
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-resilience-demo.ps1 -SkipBuild
# Hoặc chủ động chọn Compose cho buổi demo chỉ sao lưu:
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-resilience-demo.ps1 -SkipBuild -Mode Compose
```

Chỉ chạy một lệnh, không chạy song song. Không khởi động thêm hai container Chat Compose sau khi đã chọn Kubernetes. Tạo nhóm mới không làm đổi chế độ triển khai; lỗi participant phải được xử lý ở môi trường chạy. Các bản backup đã thất bại vẫn giữ trạng thái lỗi; sau khi sửa hãy tạo bản mới.

Đọc mục 1 để hiểu quy trình, mục 2 để demo trên web, rồi [BACKUP-CODE.md](BACKUP-CODE.md) để đọc từng phần code. Bài PostgreSQL A → bản nền → B → xóa B → lấy lại B nằm trong [BACKUP-POSTGRES.md](BACKUP-POSTGRES.md).

## 1. Một bản sao lưu giữ những gì?

| Thành phần | Dữ liệu | Cách giữ |
|---|---|---|
| PostgreSQL 17 | Tài khoản, bạn bè, nhóm, thành viên, unread, SQL Outbox, bảng chống trùng | Bản nền online `pg_basebackup` và WAL đến named restore point |
| MongoDB 8, hai shard | Tin nhắn, khóa chống trùng, counter, Mongo Outbox, metadata file | Đọc sáu collection với cùng `readConcern: snapshot, atClusterTime: T` |
| SeaweedFS | Bytes ảnh/file đã gắn vào tin tại mốc | Lấy object qua S3 API, đối chiếu kích thước và SHA-256 với Mongo |
| Kafka | Sự kiện còn cần xử lý và bookmark của từng consumer group | Xuất khoảng đã chốt từng partition, nhập broker mới và ánh xạ offset |
| Redis | Cache, presence, cuộc gọi, cửa phối hợp | AOF bảo vệ restart; RDB giữ để đối chiếu. Recovery dùng Redis mới để tránh trạng thái online/cuộc gọi giả |
| Web/app | Giao diện và phiên bản chương trình | Chép Angular build vào bundle, ghi Docker app image IDs để dùng lại |

Quy trình:

```text
1. PostgreSQL tạo bản nền                → Người dùng tiếp tục đọc/ghi
2. Đóng cửa nhận ghi trên 5 instance    → Đợi writer/publisher/consumer đang chạy xong
3. Ghi mốc PostgreSQL + Mongo T + Kafka offsets
4. Mở cửa ghi                          → Người dùng tiếp tục ghi
5. Chép WAL + đọc Mongo ở T + chép object bất biến + xuất Kafka range
6. Hash, kiểm tra rồi công bố hoàn tất
```

**Checkpoint** là mốc phối hợp ở bước 3. PostgreSQL LSN và Mongo T là hai loại tọa độ riêng; chúng được ghi khi các tác vụ thay đổi dữ liệu nghiệp vụ đã dừng. Không dùng việc “cùng giờ trên đồng hồ” để bảo đảm nhất quán.

**Barrier/cửa ghi** chỉ giữ lúc chốt mốc, không giữ suốt lúc chép backup. HTTP đọc vẫn hoạt động, ghi mới đợi ngắn. Intent Redis có TTL 8 giây; coordinator có deadline 7 giây. Không đủ ACK hoặc service đổi phiên thì backup thất bại. Đây là online backup có khoảng tăng độ trễ ghi ngắn, không hứa mọi request luôn nhanh như bình thường.

**ACK** xác nhận instance đã đóng cửa và writer trước đó đã hoàn tất. ACK chứa generation của tiến trình và token lần chốt để không dùng lại ACK cũ. API ghi, hub gửi tin, SQL/Mongo publisher, Kafka handler **và commit offset** đều tham gia.

**Snapshot Mongo** cho phép chép sau khi mở cửa nhưng vẫn đọc trạng thái ở T; tin mới sau T không lọt vào backup. Mỗi shard giữ cửa sổ lịch sử tối thiểu 3.600 giây; job giới hạn 30 phút. Lỗi snapshot quá cũ làm backup thất bại. Xem [MongoDB snapshot read concern](https://www.mongodb.com/docs/manual/reference/read-concern-snapshot/).

**Outbox/Kafka**: sự kiện chưa publish nằm trong bản DB; sự kiện đã publish nhưng chưa xử lý nằm trong Kafka range. Bảng chống trùng cũng được khôi phục. Broker mới có offset riêng, nên phải ánh xạ bookmark trước khi chạy consumer. Không reset offset Kafka nguồn.

Broker recovery trả được metadata/topic chưa có nghĩa bộ điều phối consumer group đã sẵn sàng. Trường hợp mọi consumer đã bắt kịp trước mốc sao lưu, các file Kafka có thể rỗng nhưng vẫn cần đặt offset phục hồi về 0. `restore-system.ps1` chờ coordinator của các group bằng lệnh describe chỉ đọc trước khi gọi importer; bước này hỗ trợ cả image importer cũ đã được pin trong bundle. Importer mới còn thử lại có giới hạn đối với lỗi coordinator tạm thời ở cấp group/partition và đọc lại offset để đối chiếu trước khi chạy ứng dụng. Không sửa manifest hoặc thay image ứng dụng của bản sao lưu để né lỗi.

**File bất biến**: attachment `Attached` không bị worker hiện tại xóa hoặc ghi đè. Vì vậy chép bytes sau khi mở cửa vẫn lấy đúng file của snapshot. Upload dở/chưa gắn tin không thuộc bản phục hồi. Nếu bổ sung sửa/xóa file vĩnh viễn, phải thêm versioning hoặc thời gian giữ object trước khi xóa.

## 2. Demo thực tế trên web

Docker Desktop đang chạy; dùng Node **14**, .NET **10**, `.env`, shared user-secrets và `infra/attachments.local.env` đã cấu hình của dự án. Không chia sẻ các secret này.

Mở PowerShell tại thư mục gốc:

```powershell
cd D:\Projects\ChatApp
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-backup-demo.ps1
```

Script build backend, Angular production và app images, bật hạ tầng/standby, chạy Identity/Group/Notification/Gateway local. Chat vẫn chạy hai container. Script chỉ dừng service local sau khi kiểm đúng executable dự án; cổng thuộc ứng dụng khác thì báo lỗi. Có tám bước tiến độ, bao gồm log build Docker. Giữ PowerShell mở và đợi dòng **`READY: http://localhost:4200/auth/login`** rồi mới mở web. Dòng `Build succeeded` chỉ có nghĩa một bước build đã xong, chưa phải cả ứng dụng đã chạy. Không chạy thêm một lần trong khi lần trước vẫn đang chạy; file lock sẽ từ chối lần trùng. Log bước/lỗi ở `infra/backups/runtime/startup.log`, log service cùng thư mục.

Nếu đã build đúng phiên bản, chỉ cần khởi động lại:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-backup-demo.ps1 -SkipBuild
```

`-SkipBuild` không cập nhật binary/image. Nếu 4200 trống, script phục vụ Angular production tại đó. Nếu bạn đã chạy `npm start`, vẫn dùng route tương tự; khởi động lại dev server khi thêm module mới.

Nếu script báo `FAILED at ...`, sửa lỗi tại bước đó trước khi thử lại. Backend chết trước khi sẵn sàng hoặc HTTP không trả lời sẽ được báo lỗi thay vì in địa chỉ web thành công. Ví dụ Redis không khởi động vì AOF hỏng sẽ chặn bước hạ tầng, nên web chưa được bật. Cần giữ bản sao file gốc trước khi dùng công cụ `redis-check-aof` sửa nhật ký; không xóa volume Redis hoặc các database để chữa lỗi mở web.

Kịch bản demo:

1. Dùng tài khoản thường tạo **một nhóm thử riêng** ở web nguồn 4200; gửi `A - trước backup`, gửi một ảnh/file. Nhớ tài khoản này.
2. Dùng cửa sổ ẩn danh hoặc profile trình duyệt khác đăng nhập **SystemAdmin**. Vào **Quản trị → Sao lưu & phục hồi**, route `/admin/backups`.
3. Bấm **Tạo bản sao lưu**. Tiếp tục gửi tin từ cửa sổ thường. Đợi quản trị báo **Hoàn tất**, hiển thị thời gian chốt mốc.
4. Sau khi backup hoàn tất, gửi `B - sau backup`, rồi xóa **nhóm thử** trên nguồn. Không dùng nhóm thật đang sử dụng để demo.
5. Trong quản trị bấm **Kiểm tra** của bản vừa tạo, kết quả phải khớp checksum.
6. Bấm **Phục hồi riêng → Bắt đầu phục hồi**. Đợi sẵn sàng rồi **Mở web phục hồi**.
7. **http://localhost:4201** có dải **BẢN PHỤC HỒI RIÊNG**. Đăng nhập tài khoản bước 1: thấy nhóm, tin A, ảnh/file và tải được file. Không thấy tin B.
8. Web nguồn 4200 vẫn đã xóa nhóm. Gửi tin ở 4201 chỉ thay đổi bản riêng, không thay đổi nguồn.
9. Xem xong bấm **Dọn môi trường phục hồi** tại web nguồn. Chỉ tài nguyên recovery có nhãn riêng bị xóa; file backup và volume nguồn vẫn giữ.

Chỉ dựng một recovery cùng lúc. Muốn chọn backup khác phải dọn bản hiện tại trước. Recovery tắt tác vụ/lịch backup, tránh chạy lại các job cũ nằm trong bản PostgreSQL.

### Lịch và giữ bản cũ

Chọn Mỗi giờ / Mỗi ngày 02:00 giờ Việt Nam / Tạm tắt; số bản giữ lại 1–30; bấm **Lưu lịch**. Hangfire lưu job trong `backup_db`; Gateway và Docker phải hoạt động để chạy job. Máy tắt lúc 02:00 không thể backup đúng 02:00. [Cơ chế recurring jobs](https://docs.hangfire.io/en/latest/background-methods/performing-recurrent-tasks.html).

Sau bản hoàn tất, dọn bundle hoàn tất cũ vượt N, nhưng ghim bundle đang dùng để recovery. Tổng có thể vượt N vì bản ghim. Việc dọn có thể kết thúc sau khi dòng backup chuyển Hoàn tất. Bản lỗi được giữ để chẩn đoán, không coi là bản phục hồi thành công.

WAL trung gian mặc định giữ 168 giờ (7 ngày), chọn 24–720 giờ. Chỉ dọn archive spool, không dọn `pg_wal` hoặc WAL trong bundle. Bundle hoàn tất tự chứa WAL cần cho mốc của nó. Muốn thêm mốc cho bản nền thủ công rất cũ phải còn chuỗi WAL liên tục; không bảo đảm PITR đến mọi giờ tùy ý.

## 3. Replication và Redis

```powershell
docker exec postgres psql -U chatapp -d postgres -c "select application_name, state, sync_state from pg_stat_replication;"
docker exec postgres-standby psql -U chatapp -d postgres -c "select pg_is_in_recovery();"
docker exec postgres-standby psql -U chatapp -d postgres -c "show transaction_read_only;"
docker exec redis redis-cli CONFIG GET appendonly
docker exec redis redis-cli CONFIG GET appendfsync
```

Mong đợi: một kết nối `streaming`; standby `t` và `on`; Redis `yes`, `everysec`.

Standby liên tục nhận thay đổi qua streaming replication bất đồng bộ; xóa nhầm cũng truyền sang nó. Backup giữ mốc cũ để quay lại trước khi xóa. Chưa tự promote/failover hoặc đổi route nguồn. Slot giới hạn 256 MB; standby dừng quá lâu có thể cần dựng lại. Cùng máy nên không chống hỏng cả phần cứng máy.

AOF lưu thay đổi Redis để đọc lại khi restart cùng volume. `everysec` có cửa sổ mất dữ liệu khi sự cố, không phải mất 0 thay đổi. [Redis persistence](https://redis.io/docs/latest/management/persistence/).

## 4. Kiểm thử và demo lỗi

Chạy **tuần tự**, khi không có job backup/restore khác:

```powershell
node .\scripts\backup-system-test.cjs --cleanup-recovery
node .\scripts\backup-web-test.cjs
node .\scripts\backup-infra-test.cjs
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-postgres-backup.ps1
dotnet run .\scripts\kafka-backup-test.cs
```

- System test: tạo fixture, vừa chat vừa backup, xóa nhóm thử, phục hồi web, đối chiếu chính xác message IDs và bytes file. Cleaner dọn fixture nguồn theo thời điểm bắt đầu và kiểm fingerprint dữ liệu giữ lại.
- Web test: Edge đăng nhập form, bấm tạo/verify/restore, kiểm lịch/retention/pin, cố ý hỏng checksum hoặc thiếu object **trong bundle thử** rồi hoàn nguyên, tải file từ chat recovery.
- Infra test: **chủ động restart Redis và pause/unpause Chat replica 2**. Chứng minh AOF giữ key, TTL nhả cửa, thiếu participant không được báo backup thành công. Chạy lúc demo riêng.
- PostgreSQL: SQL ghi thật khi base backup hoạt động, A/B/PITR, lỗi manifest/base/WAL/target, fingerprint bốn DB thật.
- Kafka: hai broker thử riêng, bookmark nhiều group, dữ liệu sau mốc bị loại, đúng thứ tự/nội dung/offset, từ chối target đã có dữ liệu.

`--cleanup-recovery` dọn recovery đang có trước bài test mới, không dọn nguồn. Fault test dùng `finally` hoàn nguyên tài nguyên thử. Không xóa volume nguồn, không reset offset nguồn. Report/ảnh/log ở bundle hoặc `infra/backups/runtime/`. `demo-context.local.json` chứa thông tin **tài khoản fixture** để kiểm lại recovery; không đưa lên Git.

## 5. Bundle và giới hạn hiện tại

```text
infra/backups/system/<id>/
  manifest.json, state.json, worker.log
  postgres/base/, wal/, metadata.json
  mongo/*.jsonl, catalog.json, objects/*.bin
  kafka/*.jsonl, checkpoint.json
  web/
  redis-runtime.rdb
```

Manifest ghi các file/kích thước/SHA-256 và mốc, app image IDs. Checksum phát hiện hỏng/thiếu so với manifest; không phải chữ ký chống người sửa được cả manifest lẫn file, và không thay việc thử restore. PostgreSQL cần base và WAL liên tục: [tài liệu PITR](https://www.postgresql.org/docs/17/continuous-archiving.html).

Recovery dùng network/volume/container riêng, chỉ publish `127.0.0.1:4201`. PostgreSQL replay đúng mốc và pause trước, sau đó **chỉ bản PostgreSQL riêng** được promote để web hoạt động. Không promote primary/standby nguồn.

Backup đang lưu local, chưa đẩy ra máy khác. App images được tham chiếu bằng ID local, không prune image cần cho backup đang giữ. Cần giữ code/config/secret tương ứng an toàn và chuyển bundle + images ra máy khác để chống mất cả máy. Compose/config restore vẫn do repository cung cấp.

Không deploy/migration/chuyển shard/schema, cleanup hoặc ghi DB/Kafka trực tiếp trong lúc backup toàn app: các thao tác ngoài ứng dụng không tham gia barrier. Thêm persistent writer/topic/consumer/collection phải cập nhật protocol/exporter; exporter hiện từ chối collection không biết.

Không tự ghi đè nguồn hoặc tự chuyển người dùng sang recovery. Polly, health checks/log tập trung và failover tự động của chương 8 là công việc khác.
