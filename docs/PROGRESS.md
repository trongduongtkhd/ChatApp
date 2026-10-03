# Tiến độ

Cập nhật sau mỗi phần. Phần "Ghi chú cho báo cáo" dùng để viết báo cáo sau này.

| # | Phần | Trạng thái |
|---|---|---|
| 1 | Docker Compose hạ tầng | ✅ Xong |
| 2 | identity-service | ✅ Xong |
| 3 | API Gateway | ✅ Xong |
| 4 | group-service | ⬜ |
| 5 | Kafka events | ⬜ |
| 6 | gRPC | ⬜ |
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

## Quyết định thiết kế đã thay đổi
(Ghi lại nếu có sửa so với DESIGN.md và lý do.)

- **Phần 1:** thêm `.env.example` vào thư mục gốc (đã ghi vào cây thư mục DESIGN.md) – để người clone biết cần điền biến nào mà không lộ mật khẩu thật.
- **Phần 2:** thêm `backend/Directory.Build.props` (đã ghi vào cây thư mục DESIGN.md) – đặt `UserSecretsId` chung `chatapp-dev` cho mọi project, để JWT secret chỉ khai báo một lần, không lệch giữa các service. Thêm `JWT_SECRET` vào `.env.example` cho lúc chạy Docker. Cách thiết lập ghi ở CLAUDE.md mục "Secret khi chạy local".
- **Phần 2:** login trả thêm `expiresAt` cạnh `accessToken` (để Angular biết khi nào token hết hạn).
