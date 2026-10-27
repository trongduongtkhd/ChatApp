# Công việc 2 – MongoDB hai shard và lịch sử theo tuần

Thiết kế được duyệt ngày 27/10/2026. Chat riêng và chat nhóm dùng chung cơ chế. Identity, Group và Notification tiếp tục dùng PostgreSQL; Presence/cache/SignalR Backplane dùng Redis. Chat đọc/ghi `chat_history` trên MongoDB; `chat_db` PostgreSQL được giữ làm nguồn đối chiếu.

## Kiến trúc và dữ liệu

```text
Angular → Gateway → Nginx → Chat Service 1 / 2
                             │
                             ├─ gRPC → Group / Identity / Presence
                             ├─ transaction → mongos → shard 1 / shard 2
                             └─ SignalR → người nhận đang kết nối
Mongo Outbox Publisher → Kafka → Notification → unread
Người nhận kết nối lại → REST → lịch sử Mongo
```

- Server MongoDB 8.0.34; driver C# 3.13.0. `docker-compose.yml` include `docker-compose.mongo.yml` (Compose hiện tại hỗ trợ include).
- Bốn tiến trình thường trực: config server, hai shard, mongos. Mỗi replica set có một node để chạy đồ án, hỗ trợ transaction nhưng **không có node dự phòng**. Mongo không mở cổng host; quản trị qua Docker.
- Script `infra/mongo/init.js` khởi tạo replica sets, thêm shards, tạo collection/index. Chạy lại không drop dữ liệu.

| Collection | Shard key | Nhiệm vụ |
|---|---|---|
| messages | groupId hashed | Nội dung tin và lịch sử |
| message_keys | _id hashed (messageId) | Giữ mã tin duy nhất, kiểm tra người gửi/conversation |
| conversation_state | _id hashed (groupId) | lastSeq, hasPending, lease owner/version/generation/deadline |
| outbox_messages | groupId hashed | EventId, payload, sequenceNumber, processedAt, attempts, lastError |

Mongo tự tính hash. Mọi tin trong cùng conversation được định tuyến theo cùng giá trị groupId. Có nhiều conversation mới phân phối được qua nhiều shard; không cam kết cân bằng tuyệt đối hoặc chia một nhóm cực nóng qua nhiều shard.

`messages` lưu GUID dạng chuỗi chuẩn, sequenceNumber Int64, thời điểm UTC BSON Date, `createdAtTicks` giữ độ chính xác PostgreSQL/.NET và `weekStartUtc` suy ra từ thời điểm gửi theo lịch Việt Nam. DTO SignalR/REST giữ nguyên các trường công khai.

## Transaction và Outbox

Một lần gửi giữ `message_keys` → tăng counter → ghi message → ghi Outbox, rồi commit toàn bộ. Kafka và SignalR không được gọi bên trong callback transaction (callback có thể được retry).

- Hai replica cùng ID: một lần commit; lần kia trả lại đúng tin cũ nếu cùng sender/group, hoặc từ chối nếu khác. Không lộ nội dung qua ID đoán được.
- Bộ đếm ở Mongo trong cùng transaction, thay Redis INCR của bản cũ. Cùng conversation được serialize bằng write conflict; conversation khác vẫn song song. Mất state → MAX messages; state tụt → unique index chặn, $max nâng floor rồi retry.
- Publisher lấy lease theo conversation bằng giờ server Mongo `$$NOW`, gửi tuần tự từng sự kiện chờ. Chờ Kafka ACK rồi mới đánh dấu trong transaction kiểm tra owner/version/generation và deadline. Worker cũ không được advance sau takeover; generation chống tái sử dụng phiên lease khi state bị xóa/tạo lại.
- Không giữ transaction Mongo khi chờ Kafka. Sập sau Kafka ACK nhưng trước đánh dấu có thể gửi lại cùng EventId: **at-least-once**; Notification dùng processed_events để loại trùng.
- Clear hasPending cũng chạm state trong transaction, nên writer mới chen vào sẽ gây conflict/retry, không bỏ sót sự kiện mới.
- Đẩy SignalR là best effort sau commit; dữ liệu bền vững nằm trong Mongo. Reconnect/mở lại lấy bù REST. Kafka luôn nhận sự kiện dù người nhận online/offline.

Khi thử tắt Kafka, kiểm thử phát hiện lỗi commit offset cũ có thể làm Notification Service dừng. `KafkaConsumerBase` được sửa để tạo lại consumer sau lỗi Kafka không fatal; đọc lại từ offset đã ACK, dựa vào handler idempotent. Lỗi fatal vẫn được báo lên host.

## Index và API tuần

```javascript
{ groupId: "hashed" }                              // định tuyến shard
{ groupId: 1, sequenceNumber: -1 }                  // unique, lịch sử bình thường
{ groupId: 1, weekStartUtc: 1, sequenceNumber: -1 }  // tuần + keyset + sort
```

Index unique là index thường riêng, không đặt unique lên hashed index. message_keys giải quyết tính duy nhất messageId trên toàn cụm khi messages phân phối theo groupId.

```text
GET /api/chat/groups/{groupId}/messages?limit=50&beforeSeq=...
GET /api/chat/groups/{groupId}/messages?weekStart=2026-10-26&limit=50&beforeSeq=...
```

- weekStart là thứ Hai, `yyyy-MM-dd`, theo giờ Việt Nam; tuần `[thứ Hai 00:00 +07, thứ Hai tuần sau 00:00 +07)`.
- Không truyền tuần → hành vi cũ. limit 1..100, beforeSeq keyset; response cũ → mới theo sequenceNumber.
- Tham số sai → 400; không có quyền → 403; kho Mongo gián đoạn → 503 có timeout. Gửi chưa xác nhận thì retry cùng messageId.
- Giao diện Angular 12: chọn ngày bất kỳ trong tuần, Xem tuần, Về tin mới nhất. Tuần cũ ẩn composer, không chèn tin ngoài tuần, không tự đánh dấu đọc tin mới. Reconnect giữ tuần. Bản nháp giữ theo conversation khi đổi tuần/reconnect. Trở về hiện tại tải tin mới và markRead bình thường.
- Khi reconnect, REST có thể tạm 502 trong lúc replica khác còn restart dù WebSocket đã nối lại. Luồng mở lại lịch sử retry tối đa bốn lần sau 1/2/4/8 giây, chỉ cho lỗi mạng/5xx; đổi nhóm/tuần hủy timer, 4xx không retry. Hết lượt vẫn hiện nút Thử lại.

## Khởi động và chuyển dữ liệu

Các service local cần dừng trước khi build solution trên Windows để tránh khóa DLL. Chạy hạ tầng và build:

```powershell
dotnet build backend/ChatApp.sln
docker compose up -d mongo-init
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/migrate-chat-history.ps1
```

Lệnh cuối chỉ dry-run source, không đổi data hoặc marker. Để chuyển lần đầu:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/migrate-chat-history.ps1 -Apply
```

Script build image trước, dừng đúng hai Chat, pg_dump backup vào infra/backups, apply + verify rồi mở lại hai Chat và Nginx. Identity/Group/Notification/Gateway vẫn dùng cấu hình secrets hiện có. Không in connection string/mật khẩu, không xóa PostgreSQL hoặc volume.

Migration đọc PostgreSQL trong transaction repeatable-read/read-only. Chuyển theo lô; cùng khóa nhưng nội dung khác thì dừng, không ghi đè. Giữ MessageId/GroupId/sender/name/content/sequence/timestamp, EventId và trạng thái Outbox. Không tạo lại sự kiện cho tin cũ không có Outbox. API kiểm tra marker storage-ready trước startup.

Đối chiếu lại source cũ (kể cả khi Mongo đã có tin mới):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/migrate-chat-history.ps1 -Verify
```

`--verify` kiểm tra toàn bộ source legacy, không đòi tổng số Mongo bằng SQL vì tin mới được ghi vào Mongo. SHA-256 tính theo đúng thứ tự group/sequence, đối chiếu từng DTO và key, kiểm tra payload/trạng thái Outbox. Counter dùng $max nên không bị hạ khi chạy lại migration.

Lần chuyển thực tế: 4.629 tin, 4.624 Outbox, 89 conversation; SHA-256 source/target:

```text
14B661E7886CF89E5938008C2F5207175F651A97E0BABAB33C5CCA53BE89FD14
```

Backup đầu: `infra/backups/chat-before-mongo-20261027-154548.dump` (được gitignore). Sau khi có tin mới trên Mongo, rollback SQL phải chuyển lại phần tin/Outbox mới; không được chỉ đổi connection string và coi SQL cũ là đầy đủ.

Đã chạy lại Apply + Verify sau kiểm thử, source/target hash vẫn khớp; số document trước/sau giữ nguyên: 8.304 messages, 8.304 keys, 5.280 Outbox, 112 state (gồm fixture). Tin mới trên Mongo không bị xóa và dữ liệu cũ không bị nhân đôi. Lượt này có thêm backup `infra/backups/chat-before-mongo-20261027-162003.dump`.

Chạy Identity, Group, Notification và Gateway trong các terminal riêng:

```powershell
dotnet run --no-build --project backend/src/Services/Identity/ChatApp.IdentityService
dotnet run --no-build --project backend/src/Services/Group/ChatApp.GroupService
dotnet run --no-build --project backend/src/Services/Notification/ChatApp.NotificationService
dotnet run --no-build --project backend/src/Gateway/ChatApp.Gateway
```

```powershell
docker compose up -d chat-service-1 chat-service-2 nginx presence-service
cd frontend/chat-app
npm start
```

## Code minh họa và chương đồ án

| File | Nội dung | Chương |
|---|---|---|
| Data/MongoChatStore.cs | Client/session, DTO và lưu timestamp | 2, 5 |
| Services/MessageService.cs | Transaction, chống trùng xuyên shard, lịch sử keyset | 4, 6 |
| Services/SequenceGenerator.cs | Counter cùng transaction, phục hồi floor | 6, 8 |
| Messaging/MongoOutboxPublisher.cs | Worker, lease/fencing, ACK và retry | 3, 4, 6, 8 |
| Data/Migration/ChatHistoryMigration.cs | Import idempotent và đối chiếu | 6, 7 |
| Services/ChatWeek.cs | UTC, ranh giới tuần Việt Nam | 5 |
| infra/mongo/init.js | Replica sets, mongos, shard/index | 2, 3, 5 |
| frontend/.../chat-window | Bộ lọc, reconnect, unread và draft | 4, 8 |

Các đường dẫn backend tương đối với `backend/src/Services/Chat/ChatApp.ChatService/`.

## Kiểm thử và demo lỗi

Một lệnh cho cả công việc 1 và 2 (các service local đang chạy):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-chat-system.ps1
```

Script tạo tài khoản/nhóm fixture mới, không xóa dữ liệu người dùng. Các pha lỗi chạy tuần tự, tự start lại container trong finally. Có thể `-SkipBuild` khi đã build đúng mã hiện tại.

Nghiệm thu ngày 27/10/2026: **110/110 kiểm tra đạt** (Mongo 25; outage 11; counter 2; friends/chat riêng 24; Presence 17; SIGKILL 7; UI 24). Backend solution và Angular 12 production build thành công; UI không có lỗi JavaScript runtime. Cảnh báo thư viện cũ MessagePack/SignalR.Common được ghi ở PROGRESS.md, chưa nâng ngoài phạm vi này.

```powershell
node scripts/mongo-chat-test.cjs
node scripts/mongo-chat-test.cjs --faults
dotnet run scripts/chat-test.cs -- seqlost
dotnet run scripts/chat-test.cs -- friends
dotnet run scripts/chat-test.cs -- presence
dotnet run scripts/chat-test.cs -- presencekill
node scripts/presence-ui-test.cjs
```

Mongo test kiểm tra timestamp tới ticks, ranh giới tuần, hai shard, collision messageId xuyên conversation/user, rollback giữa insert, 200 tin đồng thời, counter mất/lùi, Outbox/Kafka có đủ sự kiện và theo sequence. Fixture tạo trực tiếp vào Mongo chỉ là lịch sử giả trong nhóm riêng của test, không phát Kafka cho tin seed.

Fault test tạm stop Kafka/router/một shard rồi phục hồi: Kafka tắt vẫn lưu và đẩy SignalR, Outbox giữ chờ rồi tự gửi; Mongo không đáp ứng thì không báo gửi thành công, history 503; retry cùng ID chỉ có một tin. Khi shard tắt, conversation ở shard khác vẫn đọc được. SIGKILL Chat kiểm tra hết lease Presence, Kafka và REST lấy bù.

UI Edge dùng production Angular, profile tạm riêng và proxy 4387, debug ports 9397/9398. Kiểm tra tuần cũ không nhận nhầm tin mới/không làm mất unread, reconnect giữ tuần và draft, trở về hiện tại mới markRead, cùng các chức năng Presence. Test chủ động restart hai Chat và stop/start Presence.

Sau restart, UI test đợi handshake WebSocket mới của cả hai tab rồi mới kiểm tra snapshot, tránh nhầm DOM còn giữ trạng thái trước sự cố. Typing mô phỏng nhiều phím trong khi kết nối sẵn sàng, phù hợp tín hiệu best effort; proxy và profile test được dọn khi kết thúc.

Demo thủ công: hai user chat → A chọn tuần cũ → B gửi tin mới → A vẫn ở tuần cũ, số chưa đọc tăng → A về tin mới nhất thấy tin và unread được xóa. Có thể stop Kafka, gửi tin, start lại để xem Outbox phục hồi. Không xóa volume để tạo lỗi.

Số liệu local ban đầu: 200 tin cùng conversation qua 8 kết nối/hai replica khoảng 6,1s. Explain tuần trong 1.005 fixture: `group_week_sequence`, 3 documents examined, 3 returned, một shard. Đây là số đo môi trường đồ án, không phải cam kết throughput production.

Tài liệu tham khảo: [Hashed sharding](https://www.mongodb.com/docs/manual/core/hashed-sharding/), [proxy unique keys](https://www.mongodb.com/docs/manual/tutorial/unique-constraints-on-arbitrary-fields/), [C# transactions](https://www.mongodb.com/docs/drivers/csharp/current/crud/transactions/).
