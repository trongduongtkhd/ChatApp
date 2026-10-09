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
| 11 | Bạn bè và nhắn tin riêng (chi tiết bên dưới) | Quan hệ 2 chiều lưu 1 dòng + máy trạng thái (Pending → Accepted/Declined/Cancelled, Accepted → Removed, mời lại), ID tất định theo cặp userId (UUID v5, chống tạo trùng), thứ tự sự kiện theo key trong 1 topic, idempotent consumer, eventual consistency | 2, 4, 5, 6 | A mời B → B chấp nhận → nhóm 2 người tự xuất hiện ở cả 2 bên; gửi lại cùng sự kiện / chấp nhận 2 lần → vẫn 1 nhóm; hủy kết bạn → mất chat riêng, kết bạn lại → thấy lịch sử cũ |
| 12 | Chịu lỗi: Polly, Health Checks, Serilog + Seq | Retry, circuit breaker, timeout, health check, log tập trung | 8 | Tắt group-service → circuit breaker mở, log trong Seq |
| 13 | Sao lưu: PostgreSQL replication, backup Hangfire, Redis AOF | Replication vs backup, restore, Kafka replay | 3, 7 | Tắt primary đọc replica; xóa nhầm rồi restore |

## Phần 11 – Bạn bè và nhắn tin riêng
Thiết kế chi tiết: DESIGN.md mục 3 (`friendships`, `groups.is_direct`), mục 4 (`identity.friendship-changed`), mục 5 (API bạn bè, quy tắc nhóm riêng).
- identity-service: bảng `friendships` (1 dòng/cặp, PK `(user_low_id, user_high_id)`) + API gửi / chấp nhận / từ chối / hủy lời mời, hủy kết bạn; danh sách bạn, lời mời đến, lời mời đã gửi.
- Chấp nhận / hủy kết bạn → phát Kafka `identity.friendship-changed` (`change` = `Accepted` | `Removed`, key `{low}:{high}`, qua Transactional Outbox) – 1 topic để sự kiện của cùng một cặp đúng thứ tự.
- group-service nghe sự kiện → `Accepted`: tạo nhóm 2 người `is_direct = true` (mã `DirectChat.GroupIdFor`, `ON CONFLICT DO NOTHING`) + thêm 2 thành viên; `Removed`: gỡ 2 thành viên, giữ nhóm. Chỉ phát member-added/removed cho dòng thật sự chèn/xóa được.
- Frontend: trang Danh bạ (Bạn bè / Lời mời / Đã gửi), "Hủy kết bạn" có hộp xác nhận; tab "Nhóm" lọc theo `isDirect`; chat riêng bị hủy kết bạn → 403 "Hai bạn không còn là bạn bè".
- chat-service và notification-service không sửa.

Các bước:
| Bước | Nội dung |
|---|---|
| 1 | Contracts (`DirectChat.GroupIdFor`, `FriendshipChanged`, topic) + bảng `friendships` (migration) |
| 2 | identity: API bạn bè (8 endpoint) + Gateway route `/api/friends` |
| 3 | Outbox `friendship-changed` khi chấp nhận / hủy kết bạn + `kafka-init` |
| 4 | group-service: `is_direct`, consumer Accepted/Removed idempotent, chặn sửa nhóm riêng, `GroupDto.isDirect/peer` |
| 4b | Số phiên bản: `friendships.revision` (+1 mỗi lần đổi trạng thái) → `FriendshipChanged.Revision` → `groups.friendship_revision`; consumer bỏ sự kiện có revision ≤ đã áp |
| 5 | `chat-test.cs` chế độ kiểm tra bạn bè + kịch bản lỗi |
| 6 | Frontend: model, `FriendApiService`, trang `/contacts`, rail |
| 7 | Frontend: tab Nhóm, hiển thị chat riêng, Nhắn tin + chờ nhóm, 403 riêng |
| 8 | Tài liệu |

Ghi chú Bước 4: với nhóm `is_direct`, phép chặn 400 chạy TRƯỚC mọi kiểm tra `OwnerId`. Rà mọi chỗ dùng `OwnerId` để quyết định quyền (`UpdateAsync`, `DeleteAsync`, `AddMemberAsync`, `RemoveMemberAsync` – kể cả nhánh "Owner không thể rời nhóm", `ToDto`/`myRole`, frontend ẩn nút theo owner) để người `user_low_id` KHÔNG được coi là chủ nhóm riêng.

Kiểm tra cần có (Bước 5, 7):
- A mời B → B chấp nhận → chat riêng có ở cả 2 bên, chat được; chấp nhận 2 lần / đẩy trùng sự kiện → vẫn 1 nhóm, 2 thành viên, không có member-added thừa.
- Mời chéo cùng lúc → 1 dòng; chấp nhận và hủy lời mời cùng lúc → 1 cái 409.
- Hủy kết bạn → cả 2 bên mất chat riêng, gửi tin bị từ chối; kết bạn lại → thấy lại lịch sử cũ (cùng mã nhóm).
- A và B cùng ở một nhóm thường: hủy kết bạn xong vẫn gửi / nhận tin trong nhóm đó bình thường (chỉ gỡ khỏi nhóm có mã `DirectChat.GroupIdFor`).
- Kịch bản lỗi: tắt group-service, chấp nhận → hủy → chấp nhận lại, bật group-service → xử lý đúng thứ tự, kết quả cuối còn 2 thành viên.
- (Bước 4b) Đẩy lại một `Removed` cũ sau khi đã kết bạn lại → vẫn còn 2 thành viên.
- (Bước 4b) Gửi tin để chat riêng có số chưa đọc, rồi đẩy lại cả chuỗi sự kiện cũ → số chưa đọc không về 0, không phát thêm member-removed / member-added.
- Báo cáo: so sánh 3 cách chống trùng / sai thứ tự (`processed_events` Phần 9, mã tất định + `ON CONFLICT` Bước 4, số phiên bản Bước 4b) và lý do không dùng giờ hệ thống.
