# Kế hoạch làm từng phần

## Phần 11e — Ảnh/file local (đã duyệt 28/10/2026)
- SeaweedFS Docker + volume/credentials local, không AWS.
- REST upload/status/cancel/content, quyền Identity/Group, validation/giới hạn.
- Mongo attachment state, claim cùng giao dịch message/Outbox, cleanup cạnh tranh an toàn.
- Angular chọn nhiều file/preview/progress/retry, hiển thị/tải, draft theo conversation, tuần và chat riêng.
- Build .NET/Angular 12 Node 14, API/race/outage/UI/persistence và hồi quy; dọn fixture cả SQL/Mongo/Redis/object.
- Hướng dẫn: `docs/ATTACHMENTS.md`; test: `node scripts/attachment-test.cjs --ui`.

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
| 12 | Chịu lỗi: Polly, Health Checks, Serilog/Seq, Prometheus/Grafana, Kubernetes/HPA | Retry, circuit breaker, timeout, health check, log tập trung | 8 | Tắt group-service → circuit breaker mở, log trong Seq |
| 13 | Sao lưu toàn app: PG base/WAL/PITR, standby, checkpoint Mongo/Kafka/object, Redis AOF, Hangfire/lưu giữ, web recovery | Replication vs backup, snapshot, barrier, offsets, manifest/checksum, restore | 3, 6, 7 | Vừa chat vừa backup; xóa nhóm thử; web riêng lấy lại nhóm/tin/file đúng mốc |
| 11b | Công việc 1 đã duyệt: Presence Service, lease 60s/heartbeat 10s, trạng thái mọi bạn bè/nhóm, đang gõ, thu hồi phòng | gRPC theo lô, lease, Redis TIME/Lua, snapshot, Pub/Sub, SignalR, eventual consistency | 2, 3, 4, 5, 6, 8 | `chat-test.cs -- presence`, `-- presencekill`, `node scripts/presence-ui-test.cjs` |
| 11c | Công việc 2 đã duyệt: MongoDB hai shard, transaction/Outbox, chuyển dữ liệu và lịch sử tuần | Hash sharding, transaction/counter, idempotency, lease/fencing, index/keyset, migration/backup | 2, 3, 4, 5, 6, 7, 8 | `scripts/test-chat-system.ps1`: 110 kiểm tra tích hợp và UI |
| 11d | Công việc 3 đã duyệt: SystemAdmin, khóa tài khoản, role nhóm, phê duyệt và link mời | RBAC, gRPC nguồn quyền, version/revocation, transaction/row lock, state machine, random token/hash | 2, 3, 4, 5, 6, 8 | `scripts/test-admin-system.ps1`, chi tiết `docs/ADMIN.md` |

Phần 11b bổ sung theo phê duyệt ngày 27/10/2026, tách khỏi Phần 12 (Polly/log tập trung) và Phần 13. Chi tiết DESIGN mục 8. Thực hiện: Contracts + Presence → gRPC audience → Chat heartbeat/relay/thu hồi → Angular toàn bộ bạn bè/nhóm + đang gõ → build và kiểm thử tích hợp/lỗi → tài liệu. MongoDB và admin chưa thuộc phạm vi này.

## Phần 13 – Hoàn thành sao lưu toàn ứng dụng (yêu cầu mở rộng 31/10/2026)

- Thực hiện toàn bộ chức năng chương 7 theo yêu cầu mới, không mở rộng sang Phần 12/chương 8.
- PostgreSQL online base + named point/WAL, cửa ghi ngắn theo ACK/token/generation để chốt mốc phối hợp, đọc vẫn tiếp tục.
- Mongo snapshot cùng atClusterTime trên hai shard; catalog/index và bytes Attached đối chiếu hash; Kafka pending ranges và mapping group offsets, giữ Outbox/idempotency.
- Redis AOF/restart, RDB đối chiếu; recovery không nạp presence/call/lease cũ. PostgreSQL streaming standby riêng, chưa tự failover.
- Hangfire/PostgreSQL storage, lịch hourly/daily/disabled, N bundles + pin recovery và archive spool retention.
- Trang admin Angular 12 tạo/kiểm tra/phục hồi riêng/lịch/cleanup. Clone chạy web 4201, nguồn 4200, cấu hình/network/volumes riêng, tắt job trong recovery.
- Build .NET/Angular Node14; test PG PITR/lỗi/fingerprint, Kafka nhiều bookmarks, system online/restore exact IDs+bytes, Edge UI/download/checksum/retention và infrastructure faults; dọn fixture.
- Tài liệu: `BACKUP.md` demo, `BACKUP-CODE.md` đọc code từng phần, `BACKUP-POSTGRES.md` bài riêng.

## Phần 13.1 – PostgreSQL online backup và PITR (bước đầu đã duyệt 31/10/2026)

- Chỉ làm bước PostgreSQL của chương 7; chưa đánh dấu toàn bộ Phần 13 hoàn thành.
- `pg_basebackup` + WAL lưu trữ liên tục thay cho chỉ dùng `pg_dump` khi cần phục hồi theo mốc.
- Compose cấu hình archive volume riêng, namespace theo system identifier; tạo archive nguyên tử, không ghi đè file khác nội dung.
- Script PowerShell 5.1: backup và verify, tạo named restore point/đóng WAL/copy vào bundle, restore đến đúng LSN.
- Phục hồi trên container và volume mới, network none, không publish cổng; PostgreSQL dừng replay tại mốc và chỉ cho đọc.
- Demo trong database `backup_demo_<id>`: A → base backup → B → mốc → xóa B → phục hồi A+B; ghi liên tục khi `pg_stat_progress_basebackup` đang hoạt động.
- Test dữ liệu hỏng/thiếu WAL/target sai/không ghi đè restore cũ; fingerprint database ứng dụng trước/sau và dọn fixture.
- Hướng dẫn, giải thích theo file và demo: `docs/BACKUP.md`.
- Các phần ngoài bước 13.1 đã được đưa vào phạm vi Phần 13 mở rộng phía trên. Automatic failover vẫn ngoài phần sao lưu.

## Phần 11c – Công việc 2 đã duyệt 27/10/2026
- MongoDB hai shards thực, config replica set + mongos, index hash groupId và index lịch sử/tuần.
- Transaction Mongo ghi key chống trùng toàn cục + counter + message + Outbox; Redis giữ Presence/cache/backplane.
- Publisher Mongo lease/fencing theo conversation, ACK Kafka, at-least-once.
- Chuyển legacy PostgreSQL có backup, batch idempotent, đối chiếu SHA-256 và marker trước startup.
- Angular lịch sử tuần, bảo toàn unread/reconnect/chat riêng; Angular 12/Node 14.
- Build và chạy tổng hợp 11b+11c: migration verify, Mongo persistence/faults, counter recovery, friends, presence/SIGKILL và UI.
- MongoDB Driver 3.13.0, server 8.0.34; không thêm service nghiệp vụ hay npm package. Admin thuộc công việc 3.

## Phần 11d – Công việc 3 đã duyệt 27/10/2026
- Account state/role/version, seed admin, audit + API phân trang/khóa-mở khóa.
- gRPC account access, middleware và thu hồi kết nối server; quyền Chat từ nguồn Group.
- Owner/Admin/Member, chuyển Owner, requests, approval setting, invite link hash/rotation, transaction và Outbox.
- Trang admin + panel nhóm + join link trên Angular 12.
- Build, API/race/outage/UI, hồi quy 11b/11c; cập nhật tài liệu và cách demo.

## Phần 11f – Gọi video 1–1 đã duyệt 28/10/2026

- ChatHub signaling và coordinator kiểm account/bạn bè/thành viên nguồn; Redis state machine nguyên tử cho hai replica, monitor deadline/thu hồi quyền.
- Angular 12 WebRTC, lời mời toàn ứng dụng, nhiều tab chỉ một nơi nhận; camera/mic, trạng thái và dọn media khi kết thúc/lỗi.
- STUN/TURN cấu hình, credential TURN tạm thời; Coturn Docker local tùy chọn. Không thuê dịch vụ hoặc đăng ký tài khoản trả phí.
- Build và test signaling/race/quyền, media thật giữa hai trình duyệt dùng thiết bị giả, hồi quy chat/file/presence; hướng dẫn HTTPS Quick Tunnel và giới hạn NAT.
- Tài liệu và demo lỗi: `docs/VIDEO-CALLS.md`.

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


## Phần 12 — Chương 8 đã duyệt toàn bộ 01/11/2026

1. Health liveness/readiness, probes theo service.
2. Serilog + Seq structured log, service/instance/trace.
3. Polly cho RPC đọc account/membership Group/Identity, retry giới hạn và circuit state.
4. Prometheus/Grafana: app metrics, resource metrics, dashboard, cảnh báo.
5. Kind Kubernetes Chat Deployment/Service; chuyển mode, backup writer động và export adapter.
6. Metrics Server và HPA 2..4; tải nghiệp vụ thật, Pod mới nhận traffic, scale-down; requests 200m, target 60%.
7. Fault/regression/backup restore/UI, cleanup fixture và fingerprint; tài liệu RESILIENCE.md, script test-resilience.ps1.
