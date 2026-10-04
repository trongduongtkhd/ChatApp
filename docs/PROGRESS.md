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
| 7 | chat-service | ⬜ |
| 8 | Scale + Backplane | ⬜ |
| 9 | notification-service | ⬜ |
| 10 | Angular | ⬜ |
| 11 | Chịu lỗi | ⬜ |
| 12 | Sao lưu | ⬜ |

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
  - Bảng `outbox_messages` giữ cả dòng đã gửi (tiện demo/tra cứu) → lớn dần; hướng xử lý: job dọn định kỳ (Hangfire, Phần 12).
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
  - Đổi lại: phụ thuộc lúc chạy (temporal coupling) – group-service chết thì chat-service không kiểm tra được → Phần 7 thêm Redis cache, Phần 11 thêm Polly → **Chương 2, 8**.
  - Deadline + status code (`InvalidArgument`, `Unavailable`, `DeadlineExceeded`) = phát hiện lỗi bằng timeout → **Chương 8**.
  - Địa chỉ service qua cấu hình (`localhost:5012` local, `group-service:5012` qua Docker DNS) → **Chương 5**.
  - API nội bộ: không qua Gateway, không JWT, không publish port ra ngoài khi chạy Docker → **Chương 2, 5**.
- **Cách demo:**
  1. Chạy identity, group, Gateway, chat (5003) → log group-service: `Now listening on: http://localhost:5012` và `:5002`.
  2. `.\scripts\test-grpc.ps1` → Owner `True`, người ngoài `False`, vừa thêm → `True` ngay, xóa nhóm → `False` + `memberIds = 0`.
  3. `docker pause postgres` → `curl.exe -s "http://localhost:5003/debug/membership?groupId=<guid>&userId=<guid>"` → `DeadlineExceeded` sau ~3000 ms → `docker unpause postgres`.
  4. Tắt group-service → gọi lại → lỗi (xem lưu ý Windows bên dưới) → bật lại → tự gọi được, không cần khởi động lại chat-service.
  5. (Tùy chọn) Postman → New → gRPC → `localhost:5012`, import file `.proto`, gọi trực tiếp; `group_id = "abc"` → `INVALID_ARGUMENT`.
- **Kết quả đã kiểm tra:** script đúng cả 4 trường hợp; DB treo → `DeadlineExceeded` 3033 ms; client trỏ nhầm sang 5002 → `HTTP_1_1_REQUIRED` (port REST từ chối HTTP/2); HTTP/1.1 vào 5012 → 400; group-service tắt, deadline 3 s → `DeadlineExceeded` ~3015 ms; deadline 10 s → `Unavailable` sau 4152 ms; bật lại group-service → gọi được ngay; `test-optimistic-lock.ps1` vẫn 200 + 409.
- **Lưu ý cho báo cáo:**
  - **Windows báo "port đóng" chậm**: kết nối tới port không có ai nghe mất ~2 giây/địa chỉ (Windows gửi lại SYN), `localhost` thử cả `::1` và `127.0.0.1` → ~4 giây > deadline 3 giây → ra `DeadlineExceeded` thay vì `Unavailable`. Linux/Docker từ chối ngay. Ví dụ cho việc phía gọi **không phân biệt được** "server chết" với "server chậm" (Chương 8).
  - gRPC không có xác thực (tin mạng nội bộ). Production nên dùng mTLS hoặc token giữa các service.
  - Proto sinh "Both" trong Contracts nên chat-service cũng có lớp Base (không dùng) – đổi lại chỉ một nơi sinh code, không service nào tham chiếu service khác.
  - Khi viết Dockerfile: `GrpcServices__GroupService=http://group-service:5012`; `Kestrel__Endpoints__Rest__Url=http://+:5002`, `Kestrel__Endpoints__Grpc__Url=http://+:5012` (localhost trong container không nhận kết nối từ container khác).

## Quyết định thiết kế đã thay đổi
(Ghi lại nếu có sửa so với DESIGN.md và lý do.)

- **Phần 1:** thêm `.env.example` vào thư mục gốc (đã ghi vào cây thư mục DESIGN.md) – để người clone biết cần điền biến nào mà không lộ mật khẩu thật.
- **Phần 2:** thêm `backend/Directory.Build.props` (đã ghi vào cây thư mục DESIGN.md) – đặt `UserSecretsId` chung `chatapp-dev` cho mọi project, để JWT secret chỉ khai báo một lần, không lệch giữa các service. Thêm `JWT_SECRET` vào `.env.example` cho lúc chạy Docker. Cách thiết lập ghi ở CLAUDE.md mục "Secret khi chạy local".
- **Phần 2:** login trả thêm `expiresAt` cạnh `accessToken` (để Angular biết khi nào token hết hạn).
- **Phần 5:** dùng **Transactional Outbox** thay vì gửi Kafka trực tiếp sau `SaveChangesAsync`. Lý do: lưu DB và gửi Kafka là 2 hệ thống, không chung transaction (dual write). Kafka chết đúng lúc → DB đã có user nhưng event mất vĩnh viễn → `user_snapshots` thiếu user mãi mãi. Với outbox, event nằm trong DB cùng transaction, Kafka sống lại thì tự gửi bù. Đã cập nhật DESIGN.md: mục 1 (nguyên tắc), cây thư mục (`ChatApp.Common/Outbox/`), mục 3 (bảng `outbox_messages` ở identity_db, group_db, chat_db), mục 4 (cách phát), mục 6 (luồng gửi tin bước 5, 7), mục 7 (chương 6). Phần dùng chung viết trong `ChatApp.Common/Outbox` để chat-service dùng lại ở Phần 7.
- **Phần 5:** tạo topic bằng container chạy một lần `kafka-init` trong docker-compose (đúng số partition DESIGN mục 4) thay vì để Kafka tự tạo (tự tạo chỉ có 1 partition). Xóa `scripts/seed-user-snapshots.ps1` vì đã có sự kiện `identity.user-registered`.
- **Phần 5:** tắt tự tạo topic (`KAFKA_AUTO_CREATE_TOPICS_ENABLE=false`) – gõ sai tên topic thì báo lỗi ngay thay vì lặng lẽ tạo topic rác. Thêm `Kafka:ConsumerGroupId` vào cấu hình service có consumer (giá trị = tên service theo DESIGN mục 4). Đã ghi vào DESIGN.md mục 4.
