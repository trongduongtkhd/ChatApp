# Sơ đồ ChatApp cho buổi thuyết trình

Mở `ChatApp-Tong-the-He-thong-va-Ha-tang.drawio` bằng diagrams.net hoặc Draw.io Desktop. File có hai trang, khổ ngang 16:9 (2240 × 1260); chữ, khối và đường nối là đối tượng chỉnh sửa được. Trong diagrams.net: **File → Open From → Device**; chọn trang ở thanh phía dưới. Có thể xuất SVG/PDF/PNG từ **File → Export as**. Hai ảnh `ChatApp-01.png` và `ChatApp-02.png` dùng để chèn trực tiếp vào slide.

## Trang 1: Tổng thể hệ thống

Lời trình bày gợi ý khoảng hai phút:

“ChatApp dùng kiến trúc microservices kết hợp hướng sự kiện. Người dùng thao tác trên Angular 12. REST API và kết nối SignalR đi qua API Gateway YARP; Gateway kiểm JWT rồi định tuyến đến dịch vụ phù hợp. Nhánh Chat đi tiếp qua Nginx đến các bản Chat Service.

Identity quản lý tài khoản, bạn bè và trạng thái xác thực. Group quản lý nhóm, thành viên, vai trò và phê duyệt. Chat xử lý tin nhắn thời gian thực, lịch sử, đính kèm và signaling cho gọi video. Notification xử lý số tin chưa đọc. Presence quản lý trạng thái online theo heartbeat và lease.

Các dịch vụ trao đổi đồng bộ bằng gRPC khi cần kiểm tra quyền hoặc lấy trạng thái hiện tại; chúng gửi sự kiện bất đồng bộ bằng Kafka. Identity, Group và Chat ghi Outbox cùng giao dịch nghiệp vụ, sau đó worker gửi Kafka. Vì có thể gửi lại sự kiện, consumer phải chống xử lý trùng.

Identity, Group và Notification có database PostgreSQL riêng về mặt logic. Tin nhắn và Outbox của Chat hiện lưu trong MongoDB hai shard; Redis dùng cho Backplane, Presence và trạng thái cuộc gọi. Ảnh và file nằm ở SeaweedFS local tương thích S3. Media WebRTC đi giữa trình duyệt hoặc qua TURN; Chat chỉ chuyển signaling.

Hệ thống có giám sát, cơ chế chịu lỗi và sao lưu phối hợp nhiều kho. Trang tiếp theo thể hiện các thành phần này thực sự chạy ở đâu.”

## Trang 2: Hạ tầng triển khai

“Trong bản triển khai local, Angular, Gateway, Identity, Group và Notification chạy thành tiến trình trên Windows. Hạ tầng dữ liệu, Presence và Nginx chạy bằng Docker. Chat chạy trên Kubernetes kind một node, tối thiểu hai và tối đa bốn Pod theo HPA. Đường vào là Gateway → Nginx → NodePort → Kubernetes Service → Chat Pod.

Redis Backplane giúp các kết nối trên những Pod khác nhau nhận được tin. Metrics Server cung cấp CPU cho HPA; Prometheus thu thập số liệu để xem ở Grafana, còn Seq nhận log. Operations agent làm cầu nối giám sát từng Pod qua API Kubernetes.

Gateway chứa bộ điều phối sao lưu và Hangfire, lưu công việc trong backup_db. Bundle giữ PostgreSQL base/WAL, Mongo snapshot, dữ liệu Kafka/offsets và file đính kèm đã kiểm SHA-256. Phục hồi dựng môi trường riêng ở cổng 4201 để đối chiếu mà không ghi đè nguồn.

Đây là triển khai đồ án trên một máy: HPA và phục hồi Pod chứng minh khả năng mở rộng, tự phục hồi tầng ứng dụng. Nó chưa bảo vệ khi mất cả máy, và PostgreSQL standby chưa tự động failover.”

## Gắn với tám chương của thầy

| Chương | Điểm có thể chỉ trực tiếp trên sơ đồ |
|---|---|
| 1. Mở đầu | Toàn bộ trang 1: mục đích, tác nhân và tổng quan ChatApp |
| 2. Kiến trúc | Microservices, Gateway, database per service, Kafka và bản sao eventual consistency |
| 3. Tiến trình và luồng | Tiến trình Windows, container/Pod; BackgroundService cho Outbox/consumer; Hangfire chạy nền |
| 4. Trao đổi thông tin | REST, SignalR/WebSocket, gRPC, Kafka và Redis Pub/Sub |
| 5. Định danh | JWT/AuthVersion, GUID v7, UUID v5 cho chat riêng; ConnectionId; Docker DNS/URI |
| 6. Đồng bộ hóa | SQL xmin/row lock, transaction Mongo/counter, unique key/idempotency, lease/fencing, Redis TIME/Lua |
| 7. Sao lưu | PostgreSQL base + WAL/PITR, standby; checkpoint Mongo/Kafka/object, AOF và recovery riêng |
| 8. Chịu lỗi | Polly, Nginx, probes, reconnect/lấy bù lịch sử, Seq/Prometheus/Grafana, Kubernetes/HPA |

## Các điểm cần giải thích chính xác

- Nginx chỉ phục vụ nhánh Chat, đứng sau Gateway. Trong mode Kubernetes, Kubernetes Service phân phối tới Pod; trong mode Compose, Nginx cân bằng hai container Chat. Hai mode không đồng thời phục vụ nguồn.
- `mode.json` trong runtime đang ghi `kubernetes`. Sơ đồ dựa trên cấu hình và tài liệu nghiệm thu trong repository; việc vẽ sơ đồ không khởi động lại hệ thống hoặc đo lại trạng thái chạy.
- Ba DB dịch vụ và `backup_db` dùng chung một PostgreSQL instance; đây là tách dữ liệu logic, không phải bốn máy DB riêng. `chat_db` là dữ liệu PostgreSQL cũ để đối chiếu; Chat runtime dùng MongoDB.
- Hash sharding theo groupId phân phối các cuộc trò chuyện qua hai shard; không chia một cuộc trò chuyện nóng ra nhiều shard. Mỗi replica set Mongo hiện có một node, không có bản dự phòng thật.
- Redis không còn cấp sequenceNumber cho Chat runtime. Counter nằm trong Mongo cùng transaction với message và Outbox. Redis Pub/Sub/SignalR không phải kho lịch sử tin nhắn.
- Kafka có một broker và replication factor 1. Không diễn giải at-least-once thành exactly-once; Notification dùng `processed_events` để chống cộng trùng.
- Các đường mũi tên trên trang tổng quan gom nhóm luồng chính; ghi chú RPC nguồn quyền bổ sung phụ thuộc của Gateway (backup), Group và Notification vào Identity. Không phải mọi request Gateway đều gọi Identity gRPC.
- SeaweedFS là kho local tương thích S3, không phải AWS S3. Binary đi qua REST; Kafka/SignalR không mang bytes của file đính kèm.
- Media WebRTC không đi qua Kafka/DB. STUN/TURN là tùy chọn; HTTPS tunnel cho web/signaling không tự giải quyết mọi tình huống NAT.
- Standby, volume và backup có vai trò khác nhau. Standby bất đồng bộ chưa tự failover; backup cùng máy chưa chống mất toàn máy. Recovery có network/volume riêng và tắt lịch backup.

## Căn cứ đã đối chiếu trong repository

| Nguồn | Nội dung dùng để vẽ |
|---|---|
| `CLAUDE.md`, `docs/DESIGN.md`, `docs/PLAN.md`, `docs/PROGRESS.md` | Bối cảnh, kiến trúc hiện tại và các thay đổi so với phiên bản cũ |
| `backend/src/Gateway/ChatApp.Gateway/appsettings.json`, `Program.cs` | Route YARP, nhánh Chat qua :5080, Hangfire và điều phối backup |
| `backend/src/Services/**/Program.cs` | Dịch vụ gRPC, Kafka consumers, Mongo Outbox, Redis Backplane và endpoints |
| `docker-compose.yml`, các Compose include/overlay | Vị trí container, cổng, network, PostgreSQL, Redis, Kafka, Presence, Mongo, SeaweedFS và standby |
| `scripts/setup-chat-k8s.cjs`, `infra/k8s/hpa.yml` | kind, Deployment, NodePort :30080, probes, Secret, requests và HPA 2–4/60% |
| `infra/nginx/nginx.k8s.conf` | Nginx → chatapp-control-plane:30080 |
| `infra/observability/prometheus.yml`, `scripts/operations-agent.cjs` | Scrape app/từng Pod, agent :18080 và Kubernetes resource metrics |
| `docs/RESILIENCE.md` | Cách chạy hai mode, nghiệm thu và giới hạn local |
| PDF bảng chấm điểm đính kèm (3 trang) | Tám nhóm tiêu chí và yêu cầu sơ đồ kiến trúc; các công cụ trong PDF là gợi ý, không phải thành phần mặc định của ChatApp |

Ảnh mẫu được dùng để tham khảo cách chia vùng: người dùng/frontend → gateway → các dịch vụ và kho dữ liệu; giám sát, chịu lỗi, sao lưu ở bên dưới. Các logo/tên công nghệ mờ trong ảnh không được dùng làm căn cứ thêm thành phần vào dự án.

Kiểm tra đã thực hiện cho bộ sơ đồ: XML hợp lệ, hai trang, ID không trùng, tham chiếu source/target/parent đầy đủ; kiểm tra kích thước chữ bằng Arial và xem ảnh render cả hai trang. Không thay đổi mã ứng dụng hoặc cài package.
