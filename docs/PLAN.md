# Kế hoạch làm từng phần

Mỗi phần: giải thích trước → code từng bước → giải thích khái niệm gắn với code → hướng dẫn test → kịch bản lỗi.

| # | Nội dung | Khái niệm cần giải thích | Chương | Cách test / demo chính |
|---|---|---|---|---|
| 1 | Cấu trúc solution + docker-compose: PostgreSQL, Redis, Kafka (KRaft), Kafka UI | Container, tiến trình, mạng Docker, DNS nội bộ | 3, 5 | `docker compose ps`, mở Kafka UI, kết nối DB |
| 2 | identity-service: register, login, JWT | JWT, claim, stateless auth, GUID v7, BCrypt | 5 | Swagger/Postman đăng ký, đăng nhập, giải mã token ở jwt.io |
| 3 | API Gateway YARP + xác thực JWT | API Gateway, định tuyến, cửa vào duy nhất | 2 | Gọi qua port 5000; không token → 401 |
| 4 | group-service: CRUD nhóm, thành viên, Optimistic Locking | Database per service, concurrency token, 409 | 2, 6 | 2 request sửa cùng version → 1 cái 409 |
| 5 | Kafka: user-registered, member-added/removed | Producer, consumer, topic, partition, key, consumer group, BackgroundService, eventual consistency | 3, 4 | Đăng ký → thấy dòng mới trong user_snapshots; xem message ở Kafka UI |
| 6 | gRPC chat-service → group-service | gRPC, Protobuf, HTTP/2, so sánh REST | 4 | Gọi CheckMembership đúng/sai thành viên |
| 7 | chat-service: SignalR hub, lưu tin, Redis INCR, idempotency, lịch sử | WebSocket, hub, phòng, số thứ tự tập trung, idempotency | 4, 5, 6 | Script bắn 200 tin song song → seq liên tục, không trùng; gửi trùng messageId |
| 8 | 2 bản chat-service + Nginx + Redis Backplane | Load balancing, backplane | 4, 8 | Tắt backplane: user ở 2 bản không thấy tin nhau; bật lại thì thấy |
| 9 | notification-service: unread, idempotent consumer, hub | At-least-once, idempotent consumer | 4, 6 | Gửi lại cùng event → không đếm 2 lần |
| 10 | Angular: login, danh sách nhóm, khung chat, unread | SignalR client, withAutomaticReconnect, guard, interceptor JWT | 4, 8 | Chat 2 trình duyệt; tắt 1 bản chat-service → tự kết nối lại |
| 11 | Chịu lỗi: Polly, Health Checks, Serilog + Seq | Retry, circuit breaker, timeout, health check, log tập trung | 8 | Tắt group-service → circuit breaker mở, log trong Seq |
| 12 | Sao lưu: PostgreSQL replication, backup Hangfire, Redis AOF | Replication vs backup, restore, Kafka replay | 3, 7 | Tắt primary đọc replica; xóa nhầm rồi restore |
