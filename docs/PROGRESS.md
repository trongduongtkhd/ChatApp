# Tiến độ

Cập nhật sau mỗi phần. Phần "Ghi chú cho báo cáo" dùng để viết báo cáo sau này.

| # | Phần | Trạng thái |
|---|---|---|
| 1 | Docker Compose hạ tầng | ✅ Xong |
| 2 | identity-service | ✅ Xong |
| 3 | API Gateway | ✅ Xong |
| 4 | group-service | ✅ Xong |
| 5 | Kafka events | ✅ Xong |
| 6 | gRPC | ✅ Xong |
| 7 | chat-service | ✅ Xong |
| 8 | Scale + Backplane | ✅ Xong |
| 9 | notification-service | ✅ Xong |
| 10 | Angular | ⬜ |
| 11 | Bạn bè + nhắn tin riêng | ⬜ |
| 12 | Chịu lỗi | ⬜ |
| 13 | Sao lưu | ⬜ |

## Ghi chú cho báo cáo
(Sau mỗi phần ghi: khái niệm đã dùng, thuộc chương nào, file code minh họa, cách demo.)

### Phần 1 – Cấu trúc solution + Docker Compose hạ tầng
- **Đã làm:** `backend/ChatApp.sln` (định dạng .sln, vì .NET 10 mặc định tạo .slnx) gồm 7 project khung; mỗi service chỉ tham chiếu `ChatApp.Contracts` + `ChatApp.Common`. Port local đặt trong `Properties/launchSettings.json` (5000–5004).
- **Hạ tầng (`docker-compose.yml`):** postgres:17-alpine (5432), redis:7.4-alpine (6379), apache/kafka:4.1.0 KRaft (9092), kafbat/kafka-ui:v1.3.0 (8080). Network `chatapp-net`, 3 volume `postgres-data`, `redis-data`, `kafka-data`. Healthcheck: `pg_isready`, `redis-cli ping`, `kafka-topics.sh --list`.
- **Khái niệm → chương:**
  - Image/container/tiến trình, Docker Compose, healthcheck, `depends_on: service_healthy` → **Chương 3**.
  - DNS nội bộ Docker (gọi `kafka:29092`, `postgres` thay vì IP; IP đổi được, tên cố định) → **Chương 5**.
  - 1 server PostgreSQL – 4 database riêng (`infra/postgres/init.sql`) → database per service mức logic, **Chương 2**.
  - Volume giữ dữ liệu khi container bị xóa → mở đầu cho **Chương 7**.
  - Kafka KRaft (controller dùng Raft thay ZooKeeper), 3 listener INTERNAL/EXTERNAL/CONTROLLER (advertised listener = "địa chỉ Kafka tự giới thiệu cho client") → **Chương 4, 5**.
- **Cách demo:** `docker compose ps` (4 container healthy/up); `docker exec postgres psql -U chatapp -c "\l"` thấy 4 database `_db`; `docker exec redis redis-cli ping` → PONG; mở http://localhost:8080 thấy cluster `chatapp` online; `docker exec redis ping -c 1 kafka` → DNS phân giải ra IP 172.x.
- **Kết quả đã kiểm tra:** gửi message qua listener EXTERNAL (localhost:9092) đọc lại được qua INTERNAL (kafka:29092); `docker compose down` rồi `up` vẫn còn key Redis và 4 database.
- **Lưu ý cho báo cáo:** Kafka chỉ có 1 broker → replication factor = 1, broker chết là mất Kafka (điểm yếu sẽ bàn ở chương 7, 8). Gộp 4 DB chung 1 server là để tiết kiệm tài nguyên; production có thể tách server.

### Phần 2 – identity-service: đăng ký, đăng nhập, JWT
- **Đã làm:** `POST /api/auth/register`, `POST /api/auth/login`, `GET /api/users/me` (port 5001, Swagger tại `/swagger`). Bảng `identity_db.users` tạo bằng EF Core migration, tự áp khi service khởi động.
- **File chính:**
  - `ChatApp.Common/Auth/JwtOptions.cs`, `JwtAuthenticationExtensions.cs`, `ChatAppClaims.cs`: phần KIỂM TRA JWT dùng chung cho mọi service.
  - `IdentityService/Services/JwtTokenService.cs`: phần TẠO token (chỉ identity làm).
  - `IdentityService/Services/AuthService.cs`: BCrypt, kiểm tra trùng, bắt lỗi unique 23505, dummy hash chống dò username.
  - `IdentityService/Entities/User.cs` (`Guid.CreateVersion7()`), `Data/IdentityDbContext.cs` (unique index, snake_case).
- **Khái niệm → chương:**
  - Tên (UserName) vs định danh (Id không đổi) ; GUID v7 sinh phi tập trung, có thứ tự thời gian → **Chương 5**.
  - JWT = header.payload.signature, claim `sub`/`name`/`display_name`, chữ ký HMAC-SHA256 → **Chương 5**.
  - Stateless auth: service nào có secret cũng tự xác minh token, không gọi identity-service → hợp với scale ngang, **Chương 2, 5**. Nhược điểm: không thu hồi token trước `exp` → hạn ngắn 60 phút.
  - Unique index là chốt chặn khi 2 request đăng ký cùng tên đồng thời (check-then-insert không an toàn) → **Chương 6**.
  - `ClockSkew` 30 giây: các máy trong hệ phân tán lệch đồng hồ nhau → **Chương 6** (đồng bộ đồng hồ).
  - Secret thiếu → service dừng ngay khi khởi động (fail fast) → **Chương 8**.
- **Cách demo:** Swagger đăng ký → xem `password_hash` dạng `$2a$11$...` (60 ký tự) bằng `docker exec postgres psql -U chatapp -d identity_db -c "select * from users"`; đăng nhập → dán token vào jwt.io đọc được claim; `/api/users/me` không token → 401, có token → 200; sửa 1 ký tự payload → 401.
- **Kết quả đã kiểm tra:** 201/409/400 khi đăng ký; sai username và sai mật khẩu trả cùng thông báo 401; token có `exp - iat = 3600`; token bị sửa payload → 401 `invalid_token`; chạy service với secret khác → token cũ 401; secret ngắn → service không khởi động.
- **Lưu ý cho báo cáo:**
  - **Chưa phát Kafka `identity.user-registered`** (để sang Phần 5, có `TODO` trong `AuthService.RegisterAsync`). → User đăng ký TRƯỚC Phần 5 sẽ KHÔNG có trong `group_db.user_snapshots`: không tìm/thêm vào nhóm được. Khi làm Phần 5 cần đăng ký lại user test (hoặc xóa bảng `users`). Đây cũng là ví dụ thực tế cho eventual consistency: bản sao chỉ có dữ liệu từ lúc bắt đầu nghe sự kiện.
  - JWT ký đối xứng (HS256): mọi service giữ secret đều có thể TẠO token giả. Production nên dùng RS256 (identity giữ private key, service khác chỉ có public key).

### Phần 3 – API Gateway (YARP) + xác thực JWT
- **Đã làm:** `ChatApp.Gateway` (port 5000) dùng `Yarp.ReverseProxy` 2.3.0. Bảng định tuyến 7 route / 4 cluster trong `Gateway/appsettings.json` (mục `ReverseProxy`), đúng bảng DESIGN mục 5. Route `auth` có `AuthorizationPolicy: anonymous`, 6 route còn lại `default` (phải có JWT); thêm `FallbackPolicy` bắt đăng nhập làm lưới an toàn. Gateway gọi lại `AddChatAppJwtAuthentication()` của Common, tự đọc `Jwt:Secret` từ kho user-secrets chung (không cấu hình thêm).
- **Common:** `JwtAuthenticationExtensions` thêm `OnMessageReceived` đọc `?access_token=` CHỈ cho đường dẫn `/hubs/*` (WebSocket không gắn được header). Phần 7, 9 dùng lại.
- **File chính:** `Gateway/Program.cs`, `Gateway/appsettings.json`, `Common/Auth/JwtAuthenticationExtensions.cs`.
- **Khái niệm → chương:**
  - Reverse proxy, API Gateway = cửa vào duy nhất, ẩn cấu trúc bên trong, gom việc chung (xác thực) → **Chương 2**.
  - Route / Cluster / Destination; URI là địa chỉ logic, Gateway ánh xạ sang host:port vật lý → **Chương 5**.
  - Gateway chỉ dành cho client → hệ thống; service gọi nhau bằng gRPC/Kafka, không vòng qua Gateway → **Chương 2, 4**.
  - Defense in depth: Gateway chặn sớm, service vẫn tự kiểm tra JWT → **Chương 2, 5**.
  - 502 Bad Gateway khi service phía sau chết; Gateway là single point of failure → **Chương 8**.
- **Cách demo:** chạy identity (5001) + Gateway (5000). Login qua 5000 → 200; `/api/users/me` qua 5000 không token → 401, có token → 200. Tắt identity: không token → vẫn 401 (do Gateway chặn), có token → 502 (qua được Gateway nhưng không có service).
- **Kết quả đã kiểm tra:** login qua 5000 giống hệt 5001 (log YARP: `Proxying to http://localhost:5001/api/auth/login`); token rác → 401 `invalid_token`; đường dẫn không có route → 404; `/api/groups` không token → 401, có token → 502; `/hubs/chat/negotiate` không token → 401, `?access_token=` → 502; REST `/api/users/me?access_token=` → 401 (query token chỉ nhận cho `/hubs`).
- **Lưu ý cho báo cáo:**
  - Gateway chưa chạy trong Docker; khi viết Dockerfile, ghi đè địa chỉ cluster bằng biến môi trường `ReverseProxy__Clusters__<tên>__Destinations__d1__Address`.
  - Phần 8 đổi cluster `chat` sang địa chỉ Nginx.
  - Token trong query string có thể lộ vào access log của proxy → chỉ áp dụng cho `/hubs`, token hạn ngắn.

### Phần 4 – group-service: CRUD nhóm, thành viên, Optimistic Locking
- **Đã làm:** 8 endpoint REST (DESIGN mục 5) ở port 5002, gọi qua Gateway 5000. 3 bảng trong `group_db`: `groups`, `group_members` (PK kép, FK cascade tới groups), `user_snapshots`. Migration tự áp khi khởi động. Secret `ConnectionStrings:GroupDb` trong kho chung.
- **File chính:**
  - `GroupService/Entities/Group.cs`: `[Timestamp] uint Version` → cột hệ thống `xmin`.
  - `GroupService/Data/GroupDbContext.cs`: PK `(group_id, user_id)`, FK cascade (cùng DB), `Role` lưu dạng chữ, KHÔNG FK cho `OwnerId`/`UserId` (khác DB).
  - `GroupService/Services/GroupManagementService.cs`: toàn bộ nghiệp vụ + phân quyền; `UpdateAsync` đặt `OriginalValue` của Version rồi bắt `DbUpdateConcurrencyException` → 409; `AddMemberAsync` kiểm tra `user_snapshots`, bắt 23505.
  - `GroupService/Services/ServiceResult.cs` + `Controllers/ServiceResultExtensions.cs`: đổi lỗi nghiệp vụ sang 400/403/404/409.
  - `Controllers/GroupsController.cs`, `GroupMembersController.cs`, `UserSearchController.cs` (ràng buộc route `{groupId:guid}`).
  - `scripts/seed-user-snapshots.ps1` (tạm, **đã xóa ở Phần 5**), `scripts/test-optimistic-lock.ps1` (demo).
- **Khái niệm → chương:**
  - Database per service, không FK chéo DB → service tự kiểm tra tính hợp lệ của `UserId` qua bản sao → **Chương 2**.
  - `user_snapshots` = bản sao dữ liệu của service khác, eventual consistency (thành viên hiện `displayName = null` khi bản sao chưa có) → **Chương 2**.
  - Lost update; Pessimistic vs Optimistic Locking; `xmin` (mã giao dịch ghi dòng lần cuối, không phải bộ đếm riêng nên version nhảy 775 → 780); `UPDATE ... WHERE xmin = @v` → 0 dòng → 409 → **Chương 6**.
  - Transaction cục bộ: tạo nhóm + Owner trong 1 `SaveChangesAsync` → **Chương 6**.
  - PK kép chặn thêm trùng thành viên khi 2 request đồng thời (giống unique index ở Phần 2) → **Chương 6**.
  - Phân quyền theo dữ liệu (403) khác xác thực (401) → **Chương 5**.
  - URI có ràng buộc kiểu `{groupId:guid}` → **Chương 5**.
- **Cách demo:**
  1. Chạy identity, group, Gateway. Đăng ký user → group-service nhận `identity.user-registered` → thấy user trong `user_snapshots` (từ Phần 5; trước đó dùng script seed tạm).
  2. `.\scripts\test-optimistic-lock.ps1` → in "Request A: 200 / Request B: 409" (bên thắng ngẫu nhiên giữa các lần chạy), dữ liệu cuối chỉ là của bên thắng.
  3. `docker exec postgres psql -U chatapp -d group_db -c "select name, xmin from groups"` trước/sau khi sửa → xmin đổi.
- **Kết quả đã kiểm tra (qua Gateway):** tạo 201 (thiếu tên 400); người ngoài xem chi tiết 403; nhóm không tồn tại 404; `/api/groups/abc` 404; sửa đúng version 200 (version mới), version cũ 409, thiếu version 400, không phải Owner 403; xóa nhóm 204 và cascade members; thêm thành viên 201 / trùng 409 / GUID không có trong snapshot 404 / Member thêm người 403; Owner tự rời 400; thành viên tự rời 204; Member xóa người khác 403; tìm `q=an` ra 2 user, `q=%` ra rỗng; script lock chạy 3 lần đều 200 + 409.
- **Lưu ý cho báo cáo:**
  - **`scripts/seed-user-snapshots.ps1` là công cụ test TẠM**, cố tình đọc chéo `identity_db` → vi phạm database per service; Phần 5 thay bằng Kafka `identity.user-registered`. Minh họa: không có sự kiện thì bản sao chỉ đồng bộ được bằng tay.
  - Chưa phát Kafka: có 4 chỗ `TODO Phần 5` trong `GroupManagementService` (tạo nhóm, xóa nhóm, thêm, xóa thành viên).
  - Báo trước Phần 5: lưu DB và phát Kafka không chung một transaction được (dual write) → cần bàn khi làm Kafka.
  - Vì sao 2 PUT đồng thời không cùng thắng: PostgreSQL khóa dòng khi UPDATE; request sau chờ request trước commit, rồi kiểm tra lại `WHERE xmin = @v` → `xmin` đã đổi → 0 dòng. Optimistic Locking ở mức ứng dụng dựa trên khóa dòng rất ngắn của DB, không giữ khóa trong lúc người dùng đang sửa form.

### Phần 5 – Kafka: user-registered, member-added/removed (Transactional Outbox)
- **Đã làm:** identity phát `identity.user-registered`; group phát `group.member-added` (tạo nhóm → Owner, thêm thành viên) và `group.member-removed` (xóa thành viên, tự rời, xóa nhóm → 1 sự kiện cho MỖI thành viên). group nghe `identity.user-registered` → upsert `user_snapshots`. Phát qua **Transactional Outbox** (bảng `outbox_messages` trong identity_db, group_db). Topic tạo bởi container `kafka-init`; tắt tự tạo topic. Hết toàn bộ `TODO Phần 5`.
- **File chính:**
  - `Contracts/Events/`: `IntegrationEvent.cs` (eventId GUID v7, eventType, occurredAt), `UserRegistered.cs`, `MemberAdded.cs`, `MemberRemoved.cs`, `KafkaTopics.cs`.
  - `Common/Kafka/KafkaProducer.cs`: singleton, `Acks.All`, `EnableIdempotence`, `MessageTimeoutMs = 5000`.
  - `Common/Kafka/KafkaConsumerBase.cs`: BackgroundService, `Consume()` chạy bằng `Task.Run`, `EnableAutoCommit = false` + `Commit` sau khi xử lý, `AutoOffsetReset.Earliest`, lỗi tạm thời → thử lại mãi, JSON hỏng → bỏ qua (poison message), `Close()` khi tắt.
  - `Common/Outbox/`: `OutboxMessage.cs`, `OutboxModelBuilderExtensions.cs` (jsonb, partial index), `OutboxDbContextExtensions.cs` (`AddOutboxEvent`), `OutboxPublisher.cs` (polling 1 giây, lô 100, `FOR UPDATE SKIP LOCKED`, lỗi → dừng lô giữ thứ tự), `OutboxServiceCollectionExtensions.cs` (`AddChatAppOutbox<TDbContext>`).
  - `IdentityService/Services/AuthService.cs`, `GroupService/Services/GroupManagementService.cs`: `AddOutboxEvent` trước `SaveChangesAsync`.
  - `GroupService/Messaging/UserRegisteredConsumer.cs`: `INSERT ... ON CONFLICT DO UPDATE`.
  - Migration `AddOutbox` ở identity và group. `docker-compose.yml`: `kafka-init`, `KAFKA_AUTO_CREATE_TOPICS_ENABLE=false`.
  - `scripts/demo-kafka-outbox.ps1`: demo tắt Kafka → đăng ký → bật Kafka → user tự xuất hiện.
- **Khái niệm → chương:**
  - Producer/consumer, topic, tách rời theo thời gian (2 bên không cần cùng lúc chạy) → **Chương 4**.
  - Partition + key: key = groupId/userId → cùng partition → giữ thứ tự; chỉ đảm bảo trong 1 topic (added và removed là 2 topic) → **Chương 6**.
  - Consumer group + offset lưu trong `__consumer_offsets`; group mới đọc từ đầu (replay) → **Chương 4, 7**.
  - BackgroundService (OutboxPublisher, consumer) chạy song song request HTTP; Singleton phải tự tạo scope cho DbContext → **Chương 3**.
  - Dual write và Transactional Outbox: dữ liệu + sự kiện cùng 1 transaction cục bộ → **Chương 2, 6**.
  - `FOR UPDATE SKIP LOCKED`: loại trừ lẫn nhau giữa nhiều bản publisher không cần khóa phân tán → **Chương 6**.
  - At-least-once (outbox gửi lại, consumer commit sau xử lý) + idempotent consumer (upsert) → **Chương 6**.
  - Eventual consistency: độ trễ đo được ~1 giây từ lúc đăng ký đến khi có trong `user_snapshots` → **Chương 2**.
  - Phát hiện lỗi bằng timeout: producer timeout 5 giây; consumer bị giết đột ngột → Kafka chờ session timeout 45 giây mới chia lại partition; tắt đúng cách (`Close()`) → chia lại ngay → **Chương 8**.
  - Lỗi tạm thời (thử lại) vs lỗi vĩnh viễn (poison message, bỏ qua; production dùng dead-letter topic) → **Chương 8**.
- **Cách demo:**
  1. `docker compose up -d` → `docker compose ps -a`: `kafka-init` `Exited (0)`; `docker exec kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --describe` → `chat.message-sent` PartitionCount 3.
  2. Chạy identity + group → `.\scripts\demo-kafka-outbox.ps1`.
  3. Tắt group-service (Ctrl+C), đăng ký vài user → `docker exec kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --describe --group group-service` thấy `LAG` > 0 → bật lại → `LAG = 0`, user xuất hiện.
  4. Tạo nhóm / thêm / xóa thành viên / xóa nhóm → Kafka UI (http://localhost:8080) xem `group.member-*`: cột Key = groupId.
- **Kết quả đã kiểm tra:**
  - Producer: Kafka chạy → OK ~1 giây; Kafka tắt → `Message timed out` sau 5040 ms; message vẫn còn sau khi Kafka khởi động lại (volume).
  - 2 OutboxPublisher song song, 300 event → Kafka nhận đúng 300, mọi dòng `attempts = 1`.
  - Kafka tắt: đăng ký vẫn 201 sau ~240–550 ms; outbox `last_error = Local: Message timed out`; bật Kafka → tự gửi bù, user có trong `user_snapshots` ~1 giây sau khi Kafka healthy.
  - Đăng ký trùng tên / thêm trùng thành viên (409) → KHÔNG có dòng outbox (transaction hủy).
  - group-service chạy lần đầu → đọc từ offset 0, nhận cả user đăng ký trước khi nó tồn tại.
  - Gửi lại cùng event 3 lần (cùng eventId) → vẫn 1 dòng `user_snapshots`.
  - group-service tắt → LAG = 1; bật lại → chỉ đọc offset mới, không đọc lại từ đầu.
  - Message JSON hỏng → log "Bỏ qua", message sau vẫn xử lý.
  - Kịch bản nhóm: 3 `MemberAdded` (Owner + 2 Member) và 3 `MemberRemoved` (1 tự rời + 2 khi xóa nhóm), cùng key = groupId, cùng partition 0.
  - `kafka-init` chạy lại vẫn exit 0; gửi vào topic không tồn tại → lỗi, không tạo topic rác.
- **Lưu ý cho báo cáo:**
  - Bảng `outbox_messages` giữ cả dòng đã gửi (tiện demo/tra cứu) → lớn dần; hướng xử lý: job dọn định kỳ (Hangfire, Phần 13).
  - Chạy 2 bản service: mỗi bản giữ thứ tự trong lô của mình, giữa 2 lô không tuyệt đối. Phần 5 identity/group chạy 1 bản nên không ảnh hưởng.
  - `added` và `removed` là 2 topic → bên nghe (Phần 7, 9) không được giả định thứ tự giữa chúng.
  - Xóa nhóm: đọc danh sách thành viên rồi mới xóa; người được thêm đúng giữa 2 bước sẽ không nhận `member-removed` (khe hở rất nhỏ, chấp nhận).
  - `UserRegisteredConsumer` không cần `processed_events` vì upsert vốn idempotent; Phần 9 (`UnreadCount + 1`) thì BẮT BUỘC cần → điểm so sánh hay.
  - `jsonb` tự sắp lại thứ tự field JSON → không ảnh hưởng vì bên nghe đọc theo tên.
  - Log SQL của EF hạ xuống `Warning` ở identity/group (nếu không, câu polling outbox in ra mỗi giây).
  - Khi viết Dockerfile: `Kafka__BootstrapServers=kafka:29092`; service nên `depends_on: kafka-init: condition: service_completed_successfully`.
  - User đăng ký trước Phần 5 (`duong`, `lan`, `lan1`, `minh` do script seed) vẫn còn trong `user_snapshots`; không có sự kiện tương ứng trong Kafka.

### Phần 6 – gRPC: chat-service → group-service
- **Đã làm:** group-service là gRPC server (`CheckMembership`, `GetMemberIds`) ở port **5012 chỉ HTTP/2**, REST vẫn ở 5002 chỉ HTTP/1.1 (cấu hình `Kestrel:Endpoints` trong `appsettings.json`, bỏ `applicationUrl` ở launchSettings). chat-service là gRPC client, có lớp bọc đặt deadline 3 giây. Endpoint **tạm** `GET /debug/membership` ở chat-service (chỉ Development) để demo khi chưa có hub → **xóa ở Phần 7**.
- **File chính:**
  - `Contracts/Protos/group_membership.proto` (đúng DESIGN mục 5, `csharp_namespace = ChatApp.Contracts.Grpc`); `Contracts.csproj`: `Google.Protobuf`, `Grpc.Core.Api`, `Grpc.Tools`, `<Protobuf ... GrpcServices="Both" />` → code sinh ở `obj/Debug/net10.0/Protos/` (`GroupMembership.cs` = message, `GroupMembershipGrpc.cs` = `GroupMembershipBase` + `GroupMembershipClient`).
  - `GroupService/Grpc/GroupMembershipGrpcService.cs`: kế thừa `GroupMembershipBase`, GUID sai → `RpcException(InvalidArgument)`, dùng `context.CancellationToken`.
  - `GroupService/Program.cs`: `AddGrpc()`, `MapGrpcService<...>().RequireHost("*:5012")`.
  - `ChatService/Grpc/GroupMembershipClient.cs`: đổi Guid ↔ string, `deadline` từ `GrpcServices:DeadlineSeconds`.
  - `ChatService/Program.cs`: `AddGrpcClient` (`Grpc.Net.ClientFactory`), địa chỉ `GrpcServices:GroupService`; endpoint debug đổi status gRPC → HTTP (400/503/504).
  - `scripts/test-grpc.ps1`.
- **Khái niệm → chương:**
  - RPC: gọi hàm ở máy khác như hàm cục bộ, nhưng có lỗi mạng/timeout → không trong suốt hoàn toàn → **Chương 4**.
  - Protobuf/IDL: hợp đồng `.proto` sinh code cho 2 phía (stub), dữ liệu nhị phân, số hiệu field → **Chương 4**.
  - HTTP/2: multiplexing nhiều lời gọi trên 1 kết nối TCP; lần gọi đầu ~300 ms (mở kết nối), sau đó ~5 ms (dùng lại kết nối) → **Chương 4**.
  - REST (client ngoài) vs gRPC (service↔service cần trả lời ngay) vs Kafka (thông báo không chờ) → **Chương 2, 4**.
  - gRPC đọc thẳng dữ liệu gốc → nhất quán mạnh (thêm thành viên là thấy ngay), khác với bản sao qua Kafka (eventual) → **Chương 2**.
  - Đổi lại: phụ thuộc lúc chạy (temporal coupling) – group-service chết thì chat-service không kiểm tra được → Phần 7 thêm Redis cache, Phần 12 thêm Polly → **Chương 2, 8**.
  - Deadline + status code (`InvalidArgument`, `Unavailable`, `DeadlineExceeded`) = phát hiện lỗi bằng timeout → **Chương 8**.
  - Địa chỉ service qua cấu hình (`localhost:5012` local, `group-service:5012` qua Docker DNS) → **Chương 5**.
  - API nội bộ: không qua Gateway, không JWT, không publish port ra ngoài khi chạy Docker → **Chương 2, 5**.
- **Cách demo:**
  1. Chạy identity, group, Gateway, chat (5003) → log group-service: `Now listening on: http://localhost:5012` và `:5002`.
  2. `.\scripts\test-grpc.ps1` → Owner `True`, người ngoài `False`, vừa thêm → `True` ngay, xóa nhóm → `False` + `memberIds = 0`. *(Từ Phần 7: `/debug/membership` đã xóa; script gọi `dotnet run scripts/chat-test.cs -- grpc`, kiểm tra bằng `JoinGroup` qua hub: được vào / bị từ chối.)*
  3. `docker pause postgres` → `curl.exe -s "http://localhost:5003/debug/membership?groupId=<guid>&userId=<guid>"` → `DeadlineExceeded` sau ~3000 ms → `docker unpause postgres`. *(Từ Phần 7: endpoint đã xóa nên không demo được cách này nữa; `docker pause postgres` làm treo cả identity. Kịch bản gRPC quá hạn demo bằng mục 4.)*
  4. Tắt group-service → gọi lại → lỗi (xem lưu ý Windows bên dưới) → bật lại → tự gọi được, không cần khởi động lại chat-service. *(Từ Phần 7: `dotnet run scripts/chat-test.cs -- down`.)*
  5. (Tùy chọn) Postman → New → gRPC → `localhost:5012`, import file `.proto`, gọi trực tiếp; `group_id = "abc"` → `INVALID_ARGUMENT`.
- **Kết quả đã kiểm tra:** script đúng cả 4 trường hợp; DB treo → `DeadlineExceeded` 3033 ms; client trỏ nhầm sang 5002 → `HTTP_1_1_REQUIRED` (port REST từ chối HTTP/2); HTTP/1.1 vào 5012 → 400; group-service tắt, deadline 3 s → `DeadlineExceeded` ~3015 ms; deadline 10 s → `Unavailable` sau 4152 ms; bật lại group-service → gọi được ngay; `test-optimistic-lock.ps1` vẫn 200 + 409.
- **Lưu ý cho báo cáo:**
  - **Windows báo "port đóng" chậm**: kết nối tới port không có ai nghe mất ~2 giây/địa chỉ (Windows gửi lại SYN), `localhost` thử cả `::1` và `127.0.0.1` → ~4 giây > deadline 3 giây → ra `DeadlineExceeded` thay vì `Unavailable`. Linux/Docker từ chối ngay. Ví dụ cho việc phía gọi **không phân biệt được** "server chết" với "server chậm" (Chương 8).
  - gRPC không có xác thực (tin mạng nội bộ). Production nên dùng mTLS hoặc token giữa các service.
  - Proto sinh "Both" trong Contracts nên chat-service cũng có lớp Base (không dùng) – đổi lại chỉ một nơi sinh code, không service nào tham chiếu service khác.
  - Khi viết Dockerfile: `GrpcServices__GroupService=http://group-service:5012`; `Kestrel__Endpoints__Rest__Url=http://+:5002`, `Kestrel__Endpoints__Grpc__Url=http://+:5012` (localhost trong container không nhận kết nối từ container khác).

### Phần 7 – chat-service: SignalR hub, lưu tin, Redis INCR, idempotency, lịch sử
- **Đã làm (7 bước):** (1) `chat_db` (`messages` + `outbox_messages`), Redis, JWT; (2) hub `/hubs/chat` (`JoinGroup`, `LeaveGroup`, `SendMessage`) + Redis INCR, **xóa `/debug/membership`**; (3) idempotency 2 lớp + outbox `chat.message-sent`; (4) bộ đếm chịu lỗi (mất key / key cũ) + bắn 200 tin; (5) cache thành viên Redis + consumer `member-added/removed` xóa cache; (6) presence + `UserPresenceChanged`; (7) REST lịch sử phân trang keyset. Secret `ConnectionStrings:ChatDb` trong kho chung.
- **File chính** (`ChatApp.ChatService/`):
  - `Hubs/ChatHub.cs` (`Hub<IChatClient>`, `[Authorize]`, `HubException`, `Context.Items` giữ phòng đã vào), `Hubs/IChatClient.cs`.
  - `Services/MessageService.cs`: kiểm tra `messageId` trước INCR (lớp 1), bắt 23505 `pk_messages` (lớp 2), `IdTaken` khi Id của người khác; `AddOutboxEvent` cùng transaction; 23505 `ix_messages_group_id_sequence_number` → `ResyncAsync` rồi thử lại (tối đa 3); `GetHistoryAsync` (keyset).
  - `Services/SequenceGenerator.cs`: `EXISTS` → thiếu thì `ResyncAsync` (MAX trong DB + Lua "chỉ nâng, không hạ") → `INCR`.
  - `Services/GroupMemberCache.cs`: cache-aside `SISMEMBER` → `EXISTS` → gRPC `GetMemberIds` → `SADD`+`EXPIRE` trong MULTI/EXEC.
  - `Services/PresenceTracker.cs`: `SADD` khi kết nối; Lua `SREM`+`SCARD` khi ngắt; `GetOnlineAsync` pipelining.
  - `Messaging/MemberAddedConsumer.cs`, `MemberRemovedConsumer.cs` (kế thừa `KafkaConsumerBase`, consumer group `chat-service`).
  - `Controllers/MessagesController.cs`; `Entities/Message.cs`, `Data/ChatDbContext.cs` (unique `(group_id, sequence_number)`); `Contracts/Events/MessageSent.cs`.
  - `scripts/chat-test.cs` – 9 chế độ: `basic`, `grpc`, `dup`, `load`, `seqlost`, `cache`, `presence`, `history`, `down`.
- **Khái niệm → chương:**
  - WebSocket (kết nối mở lâu, server chủ động đẩy) vs REST (hỏi–đáp); SignalR negotiate, hub, client gọi method như RPC → **Chương 4**.
  - ConnectionId (định danh kết nối, đổi khi reconnect) vs UserId (từ JWT, không lấy từ tham số client); phòng = tập ConnectionId có tên → **Chương 5**.
  - Bộ đếm tập trung Redis INCR (đơn luồng, lệnh nguyên tử) thay cho đồng hồ máy (lệch nhau) hay bộ đếm riêng từng bản (trùng) → **Chương 6**.
  - Thứ tự CẤP SỐ ≠ thứ tự ĐẾN NƠI: 200 tin song song → 73 lần tin đến người nghe không theo seq → client phải sắp theo `sequenceNumber` → **Chương 6**.
  - Idempotency: client sinh `messageId`, được gửi lại thoải mái; check-then-act có khe hở → PK chặn cuối; cái giá: seq có lỗ (duy nhất + tăng dần, không liên tục) → **Chương 6**.
  - Không tin dữ liệu client: `messageId` của người khác → từ chối, không lộ nội dung → **Chương 5**.
  - Transactional Outbox ở chat-service: Kafka chết vẫn chat, tin trùng thua ở PK → không có sự kiện thừa → **Chương 2, 6**.
  - Nguồn sự thật (PostgreSQL) vs dữ liệu phái sinh (bộ đếm Redis, cache) – hỏng thì dựng lại từ nguồn → **Chương 6, 7**.
  - Lỗi phát hiện trước (`EXISTS`) vs phát hiện sau (unique index) → tự sửa + thử lại có giới hạn → **Chương 8**.
  - Lua script / MULTI-EXEC = thao tác nguyên tử tự định nghĩa → **Chương 6**.
  - Cache-aside + xóa cache bằng sự kiện + TTL: đổi nhất quán mạnh (gRPC mỗi lần) lấy tốc độ và khả dụng (nhất quán cuối cùng ~0,5–1 s) → **Chương 2, 4**.
  - Presence = "còn ≥ 1 kết nối", lưu chung ở Redis (Phần 8 nhiều bản); snapshot (`JoinGroup` trả về) + delta (`UserPresenceChanged`) → **Chương 4, 5**.
  - Phân trang keyset (`seq < beforeSeq`, dùng index) vs OFFSET (lệch khi có tin mới, phải đếm N dòng) → **Chương 4**.
  - Fail closed: không kiểm tra được quyền → từ chối; kết nối hub vẫn sống khi group-service chết (cô lập lỗi) → **Chương 8**.
- **Cách demo:** chạy identity, group, Gateway, chat (tắt bằng **Ctrl+C**), rồi `dotnet run scripts/chat-test.cs -- <chế độ>`:
  1. `basic` → 2 user chat, seq 1..4, người ngoài bị từ chối, không token 401.
  2. `dup` → gửi trùng tuần tự + 5 kết nối đồng thời → 1 tin; psql: 3 tin, 3 dòng outbox.
  3. `load` → 200 tin song song seq 1..200 (thêm `--count 1000 --connections 50`).
  4. `seqlost` → xóa key / lùi key / xóa key giữa lúc gửi → vẫn đúng; log `Đồng bộ lại bộ đếm`.
  5. `cache` → TTL 600, thêm/xóa thành viên → cache bị xóa sau ~0,5–1 s.
  6. `presence` → 2 kết nối của 1 user, đóng lần lượt, chỉ báo offline ở kết nối cuối.
  7. `history` → lật trang 71..120 → 21..70 → 1..20 → rỗng, có tin mới chen giữa vẫn đúng; `explain analyze` thấy `Index Scan Backward`.
  8. `down` (kịch bản lỗi) → tắt group-service: nhóm có cache gửi được (~20 ms), nhóm chưa cache bị từ chối (~3 s); bật lại → gửi được.
  9. Kafka chết: `docker compose stop kafka` → `basic` vẫn ĐÚNG; outbox `last_error = Local: Message timed out`; bật Kafka → gửi bù.
- **Kết quả đã kiểm tra:**
  - `basic`, `grpc`, `dup`, `load`, `seqlost`, `cache`, `presence`, `history` đều ĐÚNG; `test-optimistic-lock.ps1` vẫn 200 + 409.
  - 20 kết nối gửi cùng messageId → 1 tin, seq tiếp theo nhảy 3 → 9 (lớp 2 chạm, INCR bỏ phí).
  - `load` 200 tin: 778 ms trước khi có cache → 381 ms sau khi có cache (~524 tin/giây).
  - `seqlost` (3): DEL rơi giữa EXISTS và INCR của 18 request → nhận seq 1..18 → unique index chặn → resync → 400/400 tin lưu được, 0 lời gọi thất bại.
  - Kafka chết: gửi tin vẫn OK, 4 sự kiện kẹt outbox, bật Kafka → `chua_gui = 0`; 16 sự kiện / 5 nhóm nằm ở partition 1 và 2 (theo key).
  - group-service chết: nhóm có cache 23 ms; nhóm chưa cache `DeadlineExceeded` 3075 ms; REST lịch sử nhóm chưa cache → 503 sau 3081 ms.
  - Lịch sử: `Index Scan Backward using ix_messages_group_id_sequence_number`, 0,125 ms trên 1490 tin.
- **Lưu ý cho báo cáo:**
  - **Kết nối ma:** chat-service chết đột ngột → `OnDisconnectedAsync` không chạy → ConnectionId nằm lại trong `presence:{userId}` (đã tái hiện, còn cả sau khi khởi động lại) → user online mãi. Hướng xử lý (Phần 8/11): heartbeat + TTL theo từng bản.
  - **Nhiều tab mở nhóm khác nhau:** đóng tab cuối chỉ báo offline cho các phòng tab đó đã vào (`Context.Items` là của từng kết nối). Sửa được bằng cách lưu phòng của user ở Redis (key mới, chưa làm).
  - **Người bị xóa khỏi nhóm vẫn NHẬN tin** nếu đang ở trong phòng SignalR, tới khi rời phòng/kết nối lại (phòng chỉ kiểm tra quyền lúc `JoinGroup`); gửi thì bị chặn ngay khi cache bị xóa.
  - **Sự kiện cũ xóa cache mới:** tạo nhóm → nạp cache ngay → `member-added` của Owner đến muộn ~1 s xóa mất. An toàn (chỉ thêm 1 lần gRPC) nhưng group-service chết đúng lúc đó thì nhóm "tưởng có cache" bị từ chối.
  - **gRPC reconnect backoff:** sau vài lần lỗi, client chờ lâu dần mới thử lại; trong lúc chờ lời gọi bị từ chối ngay (`Unavailable` ~1 s thay vì 3 s); group-service sống lại có thể mất vài giây mới thông. Phần 12 (Polly).
  - **Giết chat-service đột ngột → Kafka chờ session timeout:** bản mới khởi động sau 2,1 s nhưng 29,3 s mới được giao partition → trong lúc đó không xử lý `member-added` → người mới thêm bị từ chối. Ctrl+C (`Close()`) thì không gặp.
  - Log `fail` có `23505` khi gửi đồng thời / bộ đếm hỏng là EF ghi lại INSERT bị chặn TRƯỚC khi code thử lại; không phải lỗi người dùng (không có `Failed to invoke hub method` tương ứng).
  - Chạy 1 bản nên `Clients.Group(...)` chỉ phát trong bản đó → Phần 8 thêm Redis Backplane.
  - Khi viết Dockerfile: `ConnectionStrings__Redis=redis:6379`, `ConnectionStrings__ChatDb=Host=postgres;...`, `Kafka__BootstrapServers=kafka:29092`, `GrpcServices__GroupService=http://group-service:5012`.

### Phần 8 – 2 bản chat-service + Nginx + Redis Backplane
- **Đã làm (5 bước):** (1) Dockerfile chat-service, chạy trong container thay cho `dotnet run`; (2) bản thứ 2 → tái hiện lỗi "không thấy tin nhau"; (3) Nginx load balancer (cổng 5080), Gateway trỏ cluster `chat` sang Nginx, client bỏ negotiate; (4) Redis Backplane có công tắc bật/tắt; (5) kịch bản tắt 1 bản / cả 2 bản. identity, group, Gateway vẫn chạy `dotnet run`.
- **File chính:**
  - `ChatService/Dockerfile`: multi-stage (sdk → aspnet), build context `backend/`, restore tách lớp để cache, `USER $APP_UID` (không chạy root), cài `libgssapi-krb5-2` (Npgsql cần, thiếu thì log báo lỗi). `backend/.dockerignore` bỏ `bin/`, `obj/`.
  - `docker-compose.yml`: khối chung `x-chat-service: &chat-service` (YAML anchor) → `chat-service-1` (5003), `chat-service-2` (5013) cùng image `chatapp/chat-service`; biến môi trường `ConnectionStrings__ChatDb` (host `postgres`), `ConnectionStrings__Redis=redis:6379`, `Kafka__BootstrapServers=kafka:29092`, `GrpcServices__GroupService=http://host.docker.internal:5012`, `Jwt__Secret=${JWT_SECRET}`, `SignalR__RedisBackplane=${CHAT_BACKPLANE:-true}`; service `nginx` (5080).
  - `infra/nginx/nginx.conf`: `upstream chat` round-robin, `zone` + `resolve` + `resolver 127.0.0.11`, `max_fails=1 fail_timeout=10s`, `proxy_next_upstream error timeout`, `proxy_connect_timeout 2s`, header `Upgrade`/`Connection`, `proxy_read_timeout 120s`, header `X-Chat-Upstream`, log dùng `$uri` (không ghi `access_token`).
  - `ChatService/Program.cs`: `AddSignalR().AddStackExchangeRedis(...)` khi `SignalR:RedisBackplane = true`, `ChannelPrefix = "chatapp-chat:"`, log trạng thái lúc khởi động. Code hub KHÔNG đổi.
  - `Gateway/appsettings.json`: cluster `chat` → `http://localhost:5080`.
  - `scripts/chat-test.cs`: kết nối mặc định `SkipNegotiation` + chỉ WebSocket (cờ `--negotiate` bật lại); chế độ mới `backplane`, `lb`, `failover` (`--action stop|kill|none`).
- **Khái niệm → chương:**
  - Image vs container (2 tiến trình cùng image), multi-stage build, chạy bằng user thường → **Chương 3**.
  - `localhost` trong container là chính container; gọi nhau bằng tên (Docker DNS), `host.docker.internal` = máy Windows; Nginx tự phân giải lại tên container (`resolve`), IP container đổi sau khi tạo lại → **Chương 5**.
  - Scale ngang đòi hỏi service stateless: thứ đã để ở Redis (`chat:seq`, presence, cache) tự dùng chung, không sửa dòng nào; thứ nằm trong RAM (phòng SignalR, `Context.Items`, connectionToken của negotiate) thì hỏng → **Chương 2, 6**.
  - Load balancer round-robin (20 request → 10/10; 10 kết nối → 5/5), WebSocket chia theo KẾT NỐI không theo tin → **Chương 8**.
  - Proxy WebSocket: `Upgrade` → `101 Switching Protocols`; Nginx mặc định không chuyển header này → **Chương 4**.
  - Negotiate + round-robin: negotiate ở bản 2, WebSocket ở bản 1 → 404 (10/10 lỗi). Sticky session bằng `ip_hash` không dùng được (mọi request đến từ IP của Gateway) → bỏ negotiate → **Chương 4, 5**.
  - Redis Pub/Sub làm backplane: mỗi bản `SUBSCRIBE` kênh `...:group:{groupId}` khi có kết nối đầu tiên vào phòng; `Clients.Group` → 1 lệnh `PUBLISH`; Pub/Sub không lưu tin (khác Kafka) → **Chương 4**.
  - Kênh `...:internal:ack:<container>`: bản này nhờ bản kia thêm kết nối vào phòng rồi chờ xác nhận → định danh từng bản, **Chương 5**.
  - Consumer group: 2 bản × 2 consumer = 4 member, mỗi topic 1 partition → chỉ 1 bản nhận, bản kia dự phòng; bản sống lại → chia lại (mỗi bản giữ 1 topic) → **Chương 4, 8**.
  - 2 OutboxPublisher cùng đọc `chat_db`: 2 sự kiện của 1 nhóm do 2 bản gửi, `attempts = 1` (không trùng) nhờ `SKIP LOCKED` → **Chương 6**.
  - Tự kết nối lại (`WithAutomaticReconnect`) + JoinGroup lại (ConnectionId mới) + gửi lại cùng `messageId` + lấy bù bằng REST lịch sử → **Chương 8**.
  - Health check bị động của Nginx + fail fast: lần đầu chờ timeout (504), đánh dấu hỏng rồi từ chối ngay (502) → **Chương 8**.
- **Cách demo** (chạy `docker compose up -d --build`, rồi identity, group, Gateway bằng `dotnet run`; chờ log chat-service có `Application started`):
  1. `dotnet run scripts/chat-test.cs -- backplane` → 7 OK.
  2. Tắt backplane: `$env:CHAT_BACKPLANE="false"; docker compose up -d chat-service-1 chat-service-2` → `backplane`: 4 OK (Redis dùng chung) + 3 SAI (tin, presence giữa 2 bản). Bật lại: `Remove-Item Env:CHAT_BACKPLANE; docker compose up -d chat-service-1 chat-service-2`.
  3. `lb` → REST 10/10, hub 5/5, có negotiate 0/10 thành công; `docker compose logs nginx --tail 30` thấy cặp `negotiate 200 → .7` / `GET /hubs/chat 404 → .5`.
  4. Xem backplane: `docker exec -it redis redis-cli MONITOR` rồi chạy `basic` → `SUBSCRIBE`/`PUBLISH ...:group:<id>`; hoặc `docker exec redis redis-cli PUBSUB CHANNELS "chatapp-chat*"`.
  5. `failover` (mặc định stop) và `failover --action kill`.
  6. Tắt cả 2: `docker compose stop chat-service-1 chat-service-2` → gọi REST chat qua Gateway: lần đầu 504, sau đó 502.
- **Kết quả đã kiểm tra:**
  - Container: `whoami` = `app`, image 513 MB; container gọi được gRPC sang group-service trên Windows qua `host.docker.internal:5012` (group-service nghe `localhost`, Docker Desktop vẫn chuyển được).
  - Không backplane: lan (bản 2) không nhận tin duong (bản 1) và ngược lại, nhưng cả 2 tin có trong DB, seq 1, 2 liên tiếp; qua Nginx `basic` chỉ nhận 1/4 và 3/4 tin, `dup`, `presence` SAI; `history`, `cache`, `grpc` vẫn ĐÚNG.
  - Có backplane: mọi chế độ qua Nginx ĐÚNG (`basic`, `dup`, `load`, `presence`, `history`, `cache`, `grpc`, `seqlost`, `lb`, `backplane`).
  - `failover --action stop`: duong mất kết nối ở giây 10,1 → nối lại sang bản kia ở 10,2 (≈ 0,1 s); 40/40 tin, 0 lần gửi lỗi, không trùng; lan thấy duong `offline → online`; consumer: bản tắt → bản còn lại nhận CẢ 2 partition ngay, bản bật lại → chia lại 1–1.
  - `failover --action kill`: vẫn nối lại ≈ 0,1 s, 40/40 tin; nhưng lan KHÔNG nhận offline, `presence:{duong}` còn 2 ConnectionId (1 là kết nối ma). Bản bị kill đang giữ partition `member-added` → bản còn sống KHÔNG được giao; 35 giây sau (khi bản bị kill khởi động lại và vào group) mới chia lại.
  - Tắt cả 2 bản: request đầu 504 sau 4,0 s (thử 2 bản × `proxy_connect_timeout 2s`), request ngay sau 502 sau 2 ms (`no live upstreams`).
- **Lưu ý cho báo cáo:**
  - Hai bản đều chết → Gateway trả **504 rồi 502** (không phải luôn 502): ví dụ phát hiện lỗi bằng timeout (chậm) rồi nhớ trạng thái hỏng để từ chối nhanh (`fail_timeout` 10 s) – ý tưởng giống circuit breaker ở Phần 12.
  - **Kill vs stop:** stop (SIGTERM) → ASP.NET đóng kết nối đúng cách → `OnDisconnectedAsync` chạy, consumer `Close()` → mọi thứ sạch. Kill (SIGKILL) → kết nối ma trong presence, offline không được báo, Kafka chờ session timeout mới chia lại partition (trong lúc đó `member-added` không ai xử lý → người mới thêm bị từ chối tới khi cache hết hạn/được xóa). Xử lý: Phần 12 (heartbeat + TTL cho presence).
  - Trong 2 lần chạy `failover`, lan tình cờ nằm ở bản KHÔNG bị tắt nên "lỡ 0 tin". Nếu cả 2 cùng ở bản bị tắt thì tin gửi lúc lan đang nối lại chỉ lấy được qua REST lịch sử – script có sẵn bước lấy bù đó.
  - Bỏ negotiate → chỉ dùng WebSocket, không còn đường lùi long polling khi mạng chặn WebSocket (chấp nhận được với trình duyệt hiện nay). Client không biết `ConnectionId` (do negotiate trả), server vẫn có.
  - Backplane: mỗi tin thêm 1 vòng Redis; Redis chết → không đẩy tin giữa các bản (tin vẫn lưu DB/Kafka); rất nhiều bản → Redis thành nút thắt.
  - `.env` cần `JWT_SECRET` = `Jwt:Secret` trong user-secrets (đã thêm khi làm Bước 1), nếu không container trả 401 với token do identity local cấp.
  - Sửa code chat-service → `docker compose up -d --build chat-service-1 chat-service-2`. Các chỉ dẫn "chạy chat-service bằng `dotnet run`" ở Phần 6, 7 không còn dùng (cổng 5003 thuộc container).
  - Chạy 3 `dotnet run` cùng lúc lần đầu dễ lỗi build `ChatApp.Contracts.dll ... being used by another process` → `dotnet build backend/ChatApp.sln` trước rồi `dotnet run --no-build`.
  - Cảnh báo DataProtection trong log container là vô hại (chat-service không dùng cookie).

### Phần 9 – notification-service: đếm tin chưa đọc, idempotent consumer, hub
- **Đã làm (7 bước):** (1) `notification_db` 3 bảng; (2a) consumer `member-added/removed` kiểu đơn giản → tái hiện lỗi đảo thứ tự; (2b) sửa bằng tombstone + `occurredAt`; (3) consumer `chat.message-sent` + Idempotent Consumer; (4) REST `GET /unread`, `POST /groups/{id}/read`; (5) hub `/hubs/notifications` + `UnreadCountChanged`; (6) demo sự kiện trùng (công tắc) + replay. Chạy `dotnet run` port 5004, 1 bản. Secret `ConnectionStrings:NotificationDb` trong kho chung.
- **File chính** (`ChatApp.NotificationService/`):
  - `Entities/` `UnreadCounter`, `GroupMemberSnapshot` (`IsMember`, `LastEventAt`, `LastEventId`), `ProcessedEvent`; `Data/NotificationDbContext.cs`; migration `Initial`, `MemberSnapshotTombstone` (sửa tay mặc định `is_member = true` cho dòng cũ).
  - `Services/MemberSnapshotService.cs`: một câu `INSERT ... ON CONFLICT DO UPDATE ... WHERE (last_event_at, last_event_id) < (excluded...)`; áp được → tạo/xóa dòng `unread_counters` cùng transaction.
  - `Services/UnreadCounterService.cs`: `ApplyMessageSentAsync` (1 transaction: `processed_events` → `UPDATE ... FROM group_member_snapshots ... RETURNING` +1 người nhận → người gửi "đọc tới seq"); `MarkReadAsync` (một câu UPDATE, `GREATEST`, về 0 khi VƯỢT mốc); công tắc `Notification:IdempotentConsumer`.
  - `Services/UnreadNotifier.cs`: `IHubContext` → `Clients.User(userId).UnreadCountChanged` sau commit, lỗi chỉ ghi log.
  - `Messaging/MemberAddedConsumer.cs`, `MemberRemovedConsumer.cs`, `MessageSentConsumer.cs` (kế thừa `KafkaConsumerBase`).
  - `Controllers/NotificationsController.cs`, `Dtos/`; `Hubs/NotificationHub.cs`, `INotificationClient.cs`, `SubUserIdProvider.cs`.
  - `Contracts/Events/IntegrationEvent.cs`: chú thích `OccurredAt` (so được giữa sự kiện của CÙNG bên phát).
  - `scripts/chat-test.cs`: lớp `Kafka` (tự đẩy sự kiện giả theo JSON của Contracts), `NotificationClient`; chế độ `reorder`, `snapshot`, `unread`, `dupevent`.
- **Khái niệm → chương:**
  - At-least-once: consumer commit sau xử lý, outbox gửi lại khi chưa ghi `processed_at` → sự kiện trùng là chuyện bình thường → **Chương 4, 6**.
  - Idempotent Consumer: `INSERT processed_events ON CONFLICT DO NOTHING` vừa kiểm tra vừa ghi sổ, CÙNG transaction với `+1` (tách 2 transaction → mất đếm hoặc đếm 2 lần) → **Chương 6**.
  - So sánh: upsert snapshot / xóa cache / bản sao thành viên tự idempotent → không cần sổ; `+1` thì BẮT BUỘC cần → **Chương 6**.
  - Kafka chỉ giữ thứ tự trong 1 partition của 1 topic; added/removed/message-sent là 3 topic, 3 consumer song song → đến sai thứ tự → **Chương 6**.
  - Idempotent ≠ không phụ thuộc thứ tự: bản 2a idempotent nhưng vẫn sai khi đảo thứ tự → **Chương 6**.
  - Tombstone + last-writer-wins theo `(occurredAt, eventId)`: xóa = đánh dấu, nhớ sự kiện cuối, bỏ sự kiện cũ đến muộn; bằng thời điểm thì phân xử bằng eventId → **Chương 6**.
  - Đồng hồ vật lý chỉ so được trong CÙNG một bên phát; nhiều bản lệch đồng hồ → cần đồng hồ logic (số phiên bản do group-service cấp, Lamport) → **Chương 6**.
  - Kafka replay dựng lại dữ liệu phái sinh (xóa bảng + reset offset) – chỉ đúng khi code đúng với mọi thứ tự và mọi lần lặp → **Chương 7**.
  - Mốc đã đọc `LastReadSequence` dùng seq logic (Phần 7), chỉ tăng (`GREATEST`); về 0 khi request VƯỢT mốc → chống request cũ đến muộn, request lặp → **Chương 6**.
  - Một câu SQL thay cho đọc-rồi-ghi (consumer +1 và user mark read cùng dòng → khóa dòng, không mất cập nhật) → **Chương 6**.
  - `Clients.User` (mọi kết nối của 1 user) vs `Clients.Group` (phòng); `IUserIdProvider` nối UserId = claim `sub` = UserIdentifier → **Chương 4, 5**.
  - Ghi DB trước, đẩy SignalR sau (best-effort); DB là nguồn sự thật, client lấy lại bằng REST → **Chương 4, 8**.
  - Quyền mark read theo bản sao (không gọi group-service) → group-service chết vẫn chạy (cô lập lỗi), đổi lại người vừa được thêm có thể 403 ~1 s. Ngược với chat-service (fail closed qua gRPC) → **Chương 2, 8**.
  - Tách rời theo thời gian: notification chết → chat vẫn chạy, Kafka giữ sự kiện, sống lại đọc tiếp từ offset → **Chương 2, 4**.
- **Cách demo** (chạy identity, group, Gateway, notification bằng `dotnet run`; `docker compose up -d`):
  1. `dotnet run scripts/chat-test.cs -- reorder` → 12 OK (đảo thứ tự, gửi lại, thêm lại, cùng occurredAt).
  2. `snapshot` → bản sao khớp group_db (thừa 0, thiếu 0 trừ dữ liệu trước Phần 5).
  3. `unread` → 2 tab cùng nhận 1, 2, 3; đọc ở tab 1 → tab 2 nhận 0; gửi thêm → 1; không token 401.
  4. `dupevent` → 1 (BẬT). Tắt: dừng notification, `$env:Notification__IdempotentConsumer="false"`, chạy notification trong CÙNG cửa sổ PowerShell, `dupevent` → 3 (SAI có chủ đích, log khởi động "Idempotent Consumer: TẮT"). Bật lại: `Remove-Item Env:Notification__IdempotentConsumer` rồi chạy lại.
  5. Replay: Ctrl+C notification → `docker exec kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --group notification-service --reset-offsets --to-earliest --topic chat.message-sent --execute` → bật lại → bộ đếm không đổi, log toàn "Bỏ qua sự kiện trùng". (Replay bản sao thành viên: thêm `truncate group_member_snapshots, unread_counters, processed_events` và reset cả 2 topic `group.member-*` rồi chạy `snapshot`.)
  6. Lỗi: tắt notification → `basic` vẫn ĐÚNG, LAG > 0 → bật lại → bộ đếm đúng. Tắt group-service → REST notification vẫn 200.
- **Kết quả đã kiểm tra:**
  - **Bản 2a tái hiện lỗi thật khi replay lần đầu:** 29/30 sự kiện removed chạy trước added tương ứng ("không có gì để xóa") → bản sao 137–139 dòng trong khi group_db 109–110 → 28–30 thành viên ma. `reorder` (2) → SAI.
  - **Bản 2b:** `reorder` 12/12 OK. Replay sạch (truncate + reset offset): 186 sự kiện, 38 bị bỏ vì cũ, bản sao 110 = group_db 111 trừ 1 thành viên của nhóm tạo lúc 07:30 03/10 (Phần 4) trong khi outbox đầu tiên 17:52 → không có sự kiện để replay; 0 thành viên ma; 34 tombstone.
  - **Trùng có sẵn trong Kafka:** lần đầu đọc `chat.message-sent`: 4.235 sự kiện, `processed_events` 4.183 = số dòng outbox chat_db, 52 lần "Bỏ qua sự kiện trùng" (2 nhóm test ngày 04/10, outbox ghi `processed_at` trễ 6 phút sau `occurred_at` → đã gửi lại). Tốc độ replay ~70 sự kiện/giây.
  - `basic` → duong `1/3`, lan `0/4` (unread/mốc).
  - REST 13 trường hợp: read bằng mốc → giữ 1; cũ hơn → giữ; vượt → 0; tab cũ đến muộn → mốc không lùi; 400 (thiếu / âm); 403 (ngoài nhóm, nhóm không tồn tại); 404 (`abc`); 401.
  - `unread`: độ trễ gửi tin → nhận UnreadCountChanged 274–403 ms.
  - `dupevent`: BẬT → unread 1, `processed_events` 1 dòng, 2 log bỏ qua; TẮT → unread 3, nhận `1, 2, 3`, 0 dòng sổ.
  - Replay `chat.message-sent` sau khi đã chạy công tắc TẮT: 4.257 sự kiện, 4.256 bỏ qua, 1 xử lý thật → 122 bộ đếm chỉ đổi 1 dòng (`3 → 4`) đúng nhóm demo lúc TẮT (sự kiện xử lý khi tắt không được ghi sổ).
  - Tắt group-service: `/api/groups` 502, notification GET 200 (19 ms), POST read 200.
  - Kill notification rồi bật lại: 39–44 s mới được giao partition (session timeout); chat vẫn chạy trong lúc đó; bắt kịp đúng.
- **Lưu ý cho báo cáo:**
  - **Tin đến trước member-added:** thành viên vừa được thêm có thể không được đếm những tin đầu (khác topic). Lúc bắt kịp sau sự cố, 2 consumer chạy song song – lần kiểm tra member-added được áp trước nên đúng, nhưng KHÔNG được bảo đảm.
  - **Tắt idempotency để lại hậu quả lâu dài:** sự kiện xử lý lúc tắt không có trong sổ → replay về sau đếm lại chúng (đã đo được: +1).
  - Server không biết seq lớn nhất thật (không lưu tin) → tin `lastReadSequence` của client; gửi `999999` vẫn 200 → nhóm đó không đếm tin < 999999 cho chính user đó (thiệt hại tự chịu).
  - Mark read về 0 khi vượt mốc: client gửi mốc thấp hơn tin mới nhất đã đếm thì các tin sau mốc bị coi là đã đọc (muốn chính xác phải lưu từng seq). Angular gửi seq lớn nhất đang hiển thị.
  - Dựa vào đồng hồ group-service (1 bản). Nhiều bản lệch đồng hồ → có thể áp sai; hướng sửa: version tăng dần cho từng (nhóm, user).
  - `processed_events` (4.201 dòng) và tombstone lớn dần → job dọn (Hangfire, Phần 13). Xóa sổ quá sớm thì sự kiện trùng đến muộn bị đếm lại → giữ lâu hơn thời gian lưu của Kafka.
  - Chạy 1 bản: `Clients.User` chỉ tới kết nối trên bản này; 2 bản cần Redis Backplane như Phần 8. Bản thân bộ đếm chịu được 2 bản (PK `processed_events`, câu UPDATE nguyên tử, 3 partition chia được).
  - SignalR không gửi bù thông báo lỡ → Angular (Phần 10) gọi `GET /unread` sau khi kết nối/kết nối lại.
  - Reset offset cần consumer group KHÔNG hoạt động: dừng bằng kill → Kafka giữ member cũ ~45 s (`group is Stable`), Ctrl+C thì reset được ngay.
  - Hub `/hubs/notifications` cũng dùng `skipNegotiation` + WebSocket như chat (Angular dùng chung cách kết nối).
  - Khi viết Dockerfile: `ConnectionStrings__NotificationDb` (host `postgres`), `Kafka__BootstrapServers=kafka:29092`, `Jwt__Secret`.

## Quyết định thiết kế đã thay đổi
(Ghi lại nếu có sửa so với DESIGN.md và lý do.)

- **Phần 1:** thêm `.env.example` vào thư mục gốc (đã ghi vào cây thư mục DESIGN.md) – để người clone biết cần điền biến nào mà không lộ mật khẩu thật.
- **Phần 2:** thêm `backend/Directory.Build.props` (đã ghi vào cây thư mục DESIGN.md) – đặt `UserSecretsId` chung `chatapp-dev` cho mọi project, để JWT secret chỉ khai báo một lần, không lệch giữa các service. Thêm `JWT_SECRET` vào `.env.example` cho lúc chạy Docker. Cách thiết lập ghi ở CLAUDE.md mục "Secret khi chạy local".
- **Phần 2:** login trả thêm `expiresAt` cạnh `accessToken` (để Angular biết khi nào token hết hạn).
- **Phần 5:** dùng **Transactional Outbox** thay vì gửi Kafka trực tiếp sau `SaveChangesAsync`. Lý do: lưu DB và gửi Kafka là 2 hệ thống, không chung transaction (dual write). Kafka chết đúng lúc → DB đã có user nhưng event mất vĩnh viễn → `user_snapshots` thiếu user mãi mãi. Với outbox, event nằm trong DB cùng transaction, Kafka sống lại thì tự gửi bù. Đã cập nhật DESIGN.md: mục 1 (nguyên tắc), cây thư mục (`ChatApp.Common/Outbox/`), mục 3 (bảng `outbox_messages` ở identity_db, group_db, chat_db), mục 4 (cách phát), mục 6 (luồng gửi tin bước 5, 7), mục 7 (chương 6). Phần dùng chung viết trong `ChatApp.Common/Outbox` để chat-service dùng lại ở Phần 7.
- **Phần 5:** tạo topic bằng container chạy một lần `kafka-init` trong docker-compose (đúng số partition DESIGN mục 4) thay vì để Kafka tự tạo (tự tạo chỉ có 1 partition). Xóa `scripts/seed-user-snapshots.ps1` vì đã có sự kiện `identity.user-registered`.
- **Phần 5:** tắt tự tạo topic (`KAFKA_AUTO_CREATE_TOPICS_ENABLE=false`) – gõ sai tên topic thì báo lỗi ngay thay vì lặng lẽ tạo topic rác. Thêm `Kafka:ConsumerGroupId` vào cấu hình service có consumer (giá trị = tên service theo DESIGN mục 4). Đã ghi vào DESIGN.md mục 4.
- **Phần 7:** `JoinGroup` trả về danh sách userId thành viên đang online (snapshot ban đầu; `UserPresenceChanged` chỉ báo thay đổi về sau, nên người vào phòng sau không biết ai đã online từ trước). `UserPresenceChanged` chỉ gửi tới các phòng user đã Join (người ngoài nhóm không biết ai online). Đã ghi DESIGN.md mục 5.
- **Phần 7:** cache `group:members:{groupId}` thêm TTL 10 phút – lưới an toàn khi lỡ mất sự kiện Kafka hoặc ghi đè cache cũ. Bộ đếm `chat:seq` ngoài "mất key → MAX" còn xử lý "key cũ hơn DB" (unique index báo trùng → nâng lên MAX → thử lại). `ConnectionStrings:Redis` đặt trong `appsettings.json` (không mật khẩu). Đã ghi DESIGN.md mục 3.
- **Phần 7:** client test SignalR viết bằng file C# chạy thẳng `scripts/chat-test.cs` (.NET 10 file-based app, `#:package`) thay vì PowerShell (PowerShell 5.1 không có client SignalR) – không tạo project mới. `scripts/test-grpc.ps1` gọi lại chế độ `grpc` của file này.
- **Phần 8:** sơ đồ đổi thành Angular → Gateway → **Nginx → 2 bản chat-service** (trước ghi Nginx đứng trước Gateway). Nginx chỉ cân bằng tải cho chat-service; Gateway vẫn là cửa vào duy nhất. Cổng Nginx 5080; chat-service chạy Docker, lộ 5003/5013 để demo nối thẳng từng bản. Đã ghi DESIGN.md mục 1, 2.
- **Phần 8:** client SignalR dùng `skipNegotiation: true` + chỉ WebSocket (áp dụng cả Angular Phần 10) thay vì sticky session – `ip_hash` vô dụng vì mọi request đến từ IP của Gateway. Sau reconnect tải lại lịch sử. Đã ghi DESIGN.md quy tắc frontend.
- **Phần 8:** thêm cấu hình `SignalR:RedisBackplane` (mặc định bật, Docker `CHAT_BACKPLANE`) chỉ để demo tắt backplane; kênh `chatapp-chat:*`. Thêm `backend/.dockerignore`. Đã ghi DESIGN.md mục 2b, 3.
- **Phần 9:** `group_member_snapshots` thêm `IsMember`, `LastEventAt`, `LastEventId` – **tombstone + chỉ áp sự kiện mới hơn** theo `(occurredAt, eventId)`. Lý do: member-added và member-removed là 2 topic, đến sai thứ tự thì cách INSERT/DELETE để lại thành viên ma (đo được 28–30 dòng khi replay). Đã cân nhắc gọi gRPC `GetMemberIds` để đồng bộ lại (luôn khớp nguồn, tự sửa khi mất sự kiện) nhưng chọn tombstone vì không thêm phụ thuộc lúc chạy notification → group-service. Đã ghi DESIGN.md mục 3.
- **Phần 9:** thêm công tắc `Notification:IdempotentConsumer` (mặc định `true`) chỉ để demo đếm trùng. Đã ghi DESIGN.md mục 3.
- **Phần 9:** quy tắc đánh dấu đã đọc: `LastReadSequence = GREATEST(cũ, mới)`, `UnreadCount = 0` chỉ khi mốc mới VƯỢT mốc cũ; người gửi tin coi như đọc tới tin của mình (cùng quy tắc).
