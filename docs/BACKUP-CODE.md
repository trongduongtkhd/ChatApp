# Đọc hiểu code sao lưu theo từng phần

Đọc một phần rồi tự trả lời câu hỏi cuối phần, không cần đọc mọi script một lúc. Demo/lệnh chạy: [BACKUP.md](BACKUP.md).

## 1. Nút trên web → API → job

| File | Thay đổi/nhiệm vụ |
|---|---|
| `frontend/chat-app/src/app/features/admin/admin-backups.component.ts` | Trang mới: tạo/verify/restore, lịch, retention và polling trạng thái mỗi 2 giây |
| `frontend/chat-app/src/app/core/services/backup-api.service.ts` | HTTP client và kiểu dữ liệu của trang |
| `frontend/chat-app/src/app/features/admin/admin.module.ts` | Khai báo bằng NgModule Angular 12 và route `/admin/backups` |
| `frontend/chat-app/src/app/features/admin/admin-shell.component.ts` | Tab Sao lưu & phục hồi; chiều cao dành chỗ cho banner recovery |
| `frontend/chat-app/src/app/core/services/recovery-mode.service.ts` | Đọc cờ recovery, đặt class layout phù hợp |
| `frontend/chat-app/src/app/app.component.ts` | Dải nhận diện bản phục hồi |
| `frontend/chat-app/src/styles.scss`, `features/auth/auth-layout.scss` | Chừa khoảng cho dải nhận diện để không che ô nhập tin |
| `backend/src/Gateway/ChatApp.Gateway/Backup/BackupEndpoints.cs` | API mới, hỏi quyền hiện tại qua Identity gRPC, xếp job Hangfire; không chỉ tin role trong token cũ |

**Background job** là công việc dài chạy tại server; browser chỉ gửi yêu cầu rồi hỏi trạng thái. Hangfire dùng `backup_db`, một worker xử lý tuần tự; không mở dashboard công khai. Thuộc chương 3/4/5/7.

Tự kiểm: đóng tab sau khi bấm Tạo thì việc sao lưu có nằm trong browser không?

## 2. Bộ chỉ huy

`backend/src/Gateway/ChatApp.Gateway/Backup/BackupOrchestrator.cs`, đọc theo thứ tự:

1. `PrepareAsync()` tạo ID/thư mục và queued.
2. `CreateAsync()` lưu image IDs/web, tạo bản nền, chốt mốc, xuất Mongo/object/Kafka.
3. `VerifyAsync()` kiểm toàn bộ file trước khi công bố/restore.
4. `PrepareRecoveryAsync()` chặn ghi đè recovery hiện có; `RestoreAsync()` dựng bản riêng.
5. `SaveSettingsAsync()`/`ApplySchedule()` đặt lịch; `PruneAsync()` giữ N bản và bản ghim; `PruneArchiveAsync()` dọn archive trung gian.

**Manifest** là bảng kê của bộ phục hồi. **Checksum SHA-256** kiểm nội dung file. **Retention** là giữ bản cũ theo chính sách. **Pin** giữ bản recovery đang dùng dù vượt N. File lock `.worker.lock` chống tác vụ quản trị chạy chồng. Thuộc chương 6/7.

Tự kiểm: có metadata ảnh trong Mongo nhưng mất bytes trong SeaweedFS thì bản sao lưu đã đủ chưa?

## 3. Cửa ghi phối hợp

`backend/src/BuildingBlocks/ChatApp.Common/Backup/BackupWriteGate.cs`:

- `EnterAsync()` tăng số writer được nhận; `Dispose()` giảm. Lease này chỉ theo dõi thao tác đang chạy, không phải bản backup.
- `PulseAsync()` heartbeat, đọc intent Redis, đóng cửa mới, đợi writer cũ hoàn tất rồi ACK.
- `BackupWriteMiddleware` bao API ghi; GET/HEAD/OPTIONS đi tiếp. gRPC hiện tại chỉ đọc nên không lấy lease lồng nhau.
- `BackupHubFilter` bao hai phương thức gửi tin; từng lần gọi SignalR không đi qua REST middleware nên cần filter riêng.

| File sửa | Lý do |
|---|---|
| `ChatApp.Common/Kafka/KafkaConsumerBase.cs` | Giữ lease cho handler **và commit offset** |
| `ChatApp.Common/Outbox/OutboxPublisher.cs` | Bao transaction, publish và ghi ProcessedAt |
| `ChatApp.ChatService/Messaging/MongoOutboxPublisher.cs` | Bao claim/renew/publish/ACK/trả lease theo conversation |
| `ChatApp.ChatService/Services/AttachmentCleanupWorker.cs` | Worker đổi metadata/xóa object cũng phải tham gia |
| `Program.cs` của Identity, Group, Notification, Chat | Đăng ký gate, khởi tạo trước migration/index, middleware; Chat thêm hub filter |
| `appsettings.json` của Identity/Group/Notification, env hai Chat trong Compose | Bật phối hợp, Redis và instance IDs cố định của năm participant |

Các đường dẫn trong bảng tính từ `backend/src/BuildingBlocks/` hoặc `backend/src/Services/<service>/` tương ứng.

**TTL** là hạn tự hết hiệu lực. **Generation** định danh một lần chạy tiến trình. **ACK** chứa generation + token lần chốt. **Barrier** là điểm mọi bên cùng đợi. Đây không phải transaction rollback cả ba kho: tại mốc vẫn có sự kiện đang đi đường, được giữ bằng Outbox/Kafka. Thuộc chương 3/6/7.

Tự kiểm: chặn API gửi tin mà để consumer tiếp tục ghi DB thì có chốt được mốc phối hợp không?

## 4. PostgreSQL và replication

| File mới | Nhiệm vụ |
|---|---|
| `infra/postgres/archive-wal.sh` | Công bố WAL nguyên tử, tách system ID, không ghi đè nội dung khác |
| `scripts/postgres-backup-common.ps1` | Gọi Docker đúng quoting PowerShell 5.1; kiểm nguồn/bundle/nhãn restore |
| `scripts/backup-postgres.ps1` | Bản nền online + verify + metadata, destination trong bundle toàn app |
| `scripts/new-postgres-restore-point.ps1` | Tạo mốc mới cho bài PostgreSQL độc lập |
| `scripts/seal-system-postgres.ps1` | Đóng gói WAL cho mốc **đã chốt**, không tạo mốc muộn hơn sau mở cửa |
| `scripts/restore-postgres.ps1` | Volume/container mới, replay đúng named point/LSN rồi pause chỉ đọc |
| `scripts/prune-postgres-archive.ps1` | Dọn archive spool quá tuổi, không đụng pg_wal/bundle/volume nguồn |
| `infra/postgres/start-standby.sh`, `scripts/setup-postgres-replica.ps1` | Role/slot replication, standby data riêng |

**Base backup** là điểm xuất phát. **WAL** là nhật ký thay đổi PostgreSQL. **LSN** là địa chỉ trong nhật ký. **PITR** áp dụng WAL tới mốc. **Standby** liên tục nhận WAL mới, nên khác backup giữ mốc cũ. Thuộc chương 7, liên hệ chương 3/6/8. Bài chi tiết: [BACKUP-POSTGRES.md](BACKUP-POSTGRES.md).

Tự kiểm: B tạo sau bản nền thì chỉ chép bản nền có lấy lại B được không?

## 5. MongoDB và bytes ảnh/file

`backend/src/Services/Chat/ChatApp.ChatService/Data/Backup/ChatBackupCommand.cs` chạy CLI quản trị trong image Chat hiện có:

- `checkpoint`: majority insert, lấy operationTime.
- `export`: `SnapshotAsync()` đọc cùng T trên sáu collection; cursor/getMore, canonical Extended JSON giữ kiểu BSON.
- Chỉ attachment Attached được xuất; lấy bytes qua S3 và kiểm size/hash theo Mongo, tạo catalog.
- `import`: chỉ trong RecoveryMode, target rỗng, kiểm checksum/count, nhập object/document/index; làm hết hạn lease của publisher cũ.

`docker-compose.mongo.yml` giữ lịch sử snapshot 3.600 giây trên cả hai shard. Không đóng lần lượt shard để chép volume. T là tọa độ logic Mongo, không phải giờ Windows. Thuộc chương 4/6/7.

Tự kiểm: tin C ghi sau khi mở cửa có lọt vào exporter vẫn yêu cầu T cũ không?

## 6. Kafka và dấu trang

`backend/src/BuildingBlocks/ChatApp.Common/Backup/KafkaBackup.cs`:

- `Capture()` chốt high watermark và committed offset của từng group/partition. Start là ranh giới sớm nhất group còn cần; End là giới hạn không bao gồm chính offset đó.
- `ExportAsync()` chỉ giữ `[Start, End)`, gồm old offset/key/value/timestamp. Không lấy tin sau mốc.
- `ImportAsync()` tạo topic đúng partition trên broker riêng, yêu cầu rỗng, nhập tin rồi đặt group offsets trước khi app chạy.
- Topic audit chưa có consumer giữ phần còn trong retention. Offset broker nguồn không đổi.

Ví dụ offset cũ 100–104 thành 0–4 trên broker mới. Group cũ chờ offset 103 thì bookmark mới là 3, không phải 103. Bảng chống trùng phục hồi theo DB giúp xử lý lại sự kiện an toàn. Thuộc chương 4/6/7.

Tự kiểm: hai consumer group đọc cùng partition có bắt buộc cùng bookmark không?

## 7. Các file khởi động/cấu hình còn lại

| File tạo/sửa | Vai trò |
|---|---|
| `scripts/restore-system.ps1` | Verify → PG PITR riêng → promote bản riêng → import Mongo/object/Kafka → chạy app → kiểm 4201; cleanup theo nhãn riêng |
| `infra/backup/recovery-nginx.conf` | Angular backup và API/SignalR proxy tới recovery Gateway |
| `backend/Service.Dockerfile` | Build app images cho recovery, không tạo service nghiệp vụ mới |
| `docker-compose.backup.yml` | Profile build images và PostgreSQL standby |
| `docker-compose.yml` | Include backup compose, PostgreSQL archive/slot, Redis AOF, Chat instance IDs |
| `backend/src/Gateway/ChatApp.Gateway/Program.cs`, `appsettings.json` | Hangfire, authorization, backup endpoints, Kafka import CLI, recovery mode |
| `backend/src/BuildingBlocks/ChatApp.Common/ChatApp.Common.csproj` | Redis client cho gate chung |
| `backend/src/Gateway/ChatApp.Gateway/ChatApp.Gateway.csproj` | Hangfire/storage; pin Npgsql/Newtonsoft tương thích |
| `scripts/start-backup-demo.ps1` | Build/khởi động local, file lock chống chạy trùng, tám bước tiến độ và Docker log trực tiếp, chờ HTTP/backend/web/proxy trước khi báo READY; log vào runtime |
| `scripts/serve-backup-web.cjs` | Angular production trên 4200, proxy API/WebSocket về nguồn |
| `.gitignore` | Bỏ dữ liệu/runtime; cho phép source Backup/ vốn khớp mẫu ignore Visual Studio |
| `.gitattributes` | Shell script LF để chạy trong Linux container |
| `docs/DESIGN.md`, `PLAN.md`, `PROGRESS.md`, ba tài liệu BACKUP | Thiết kế, tiến độ, đọc code, demo và bài PostgreSQL riêng |

**RecoveryMode** tắt job trong bản phục hồi. Cô lập dữ liệu còn nhờ network/container/volume riêng và cấu hình mọi service về bản riêng; không chỉ nhờ banner.

Các thay đổi attachment/video khác đang có trong Git thuộc công việc trước đó. Riêng AttachmentCleanupWorker được thêm gate; không coi mọi file chưa commit là file mới của phần sao lưu.

## 8. Các file kiểm thử

| File | Chứng minh |
|---|---|
| `scripts/test-postgres-backup.ps1` | Ghi khi base backup thật đang chạy, B sau base được phục hồi qua WAL, đúng LSN, các lỗi và fingerprint 4 DB |
| `scripts/kafka-backup-test.cs` | Hai broker thử, nhiều bookmark, pending ranges, loại tin sau mốc, target có dữ liệu bị từ chối |
| `scripts/backup-system-test.cjs` | Chat/file qua API thật, backup online, xóa fixture, phục hồi web, so exact message IDs/bytes và dọn nguồn |
| `scripts/backup-recovery-test.cjs` | Kiểm lại bundle demo hiện có; --observe chỉ kiểm recovery đã chạy |
| `scripts/backup-web-test.cjs` | Edge/form/nút/lịch/retention/pin/checksum/restore/chat/download |
| `scripts/backup-infra-test.cjs` | Standby streaming/read-only, AOF restart, TTL nhả cửa, thiếu participant fail closed |

Các fault test chỉ sửa bundle thử hoặc tài nguyên thử được xác định, rồi hoàn nguyên trong finally. Không tắt cơ chế kiểm tra để demo qua.
