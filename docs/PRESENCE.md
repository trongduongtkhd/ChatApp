# Công việc 1 – Presence, online/offline và đang gõ

Thiết kế được duyệt ngày 27/10/2026. Giữ SignalR + Kafka; không chuyển sang Socket.IO. Code frontend tương thích Angular 12/Node 14.

Sau công việc 2, Chat dùng MongoDB. Máy chưa chuyển dữ liệu cần làm theo `docs/MONGODB.md` và chạy migration trước khi mở Chat Service; các quy tắc Presence dưới đây giữ nguyên.

Sau công việc 3, account/membership được kiểm tra từ nguồn gRPC, có monitor server thu hồi account và phòng độc lập heartbeat/Kafka; xem `docs/ADMIN.md`. Các test cache/down của Phần 7 là thiết kế cũ, dùng `scripts/admin-dependency-test.cjs` cho hành vi hiện tại.

## Chạy hệ thống

Tại thư mục gốc, dùng cấu hình secrets hiện có (không đổi JWT/DB). Nếu service local đang chạy, dừng trước khi build để tránh khóa DLL Windows.

```powershell
dotnet build backend/ChatApp.sln
docker compose up -d --build presence-service chat-service-1 chat-service-2 nginx
```

Chạy mỗi lệnh dưới đây trong một terminal riêng:

```powershell
dotnet run --no-build --project backend/src/Services/Identity/ChatApp.IdentityService
dotnet run --no-build --project backend/src/Services/Group/ChatApp.GroupService
dotnet run --no-build --project backend/src/Services/Notification/ChatApp.NotificationService
dotnet run --no-build --project backend/src/Gateway/ChatApp.Gateway
```

```powershell
cd frontend/chat-app
npm start
```

Health Presence: `http://localhost:5005/health`. gRPC Presence chỉ nằm trong mạng Docker. Identity phải chạy bản mới có gRPC 5011, Group có gRPC 5012. Không cần migration DB cho công việc này.

## Quy tắc hiển thị

- Heartbeat khoảng 10s, hết hạn sau 60s từ lần server nhận cuối; thêm độ trễ quét 2s và truyền thông báo. Đây là khả năng còn liên lạc, không phải thời gian không gõ phím.
- Nhiều tab/thiết bị: chỉ offline khi không còn lease hợp lệ. Ngắt kết nối tạm thời chờ hết hạn; logout thu hồi đúng phiên hiện tại ngay nếu server nhận được yêu cầu. Nếu yêu cầu logout thất bại, lease tự hết hạn.
- Bạn bè: sidebar và Danh bạ có mọi bạn bè online. Danh bạ có tab Đang hoạt động. Vào thẳng Danh bạ vẫn đăng ký presence.
- Nhóm: có ít nhất một thành viên KHÁC người xem online thì sáng; hiện số người khác online, dù họ đang mở cuộc trò chuyện khác. Một mình bạn online không làm mọi nhóm sáng.
- Chưa xác định: đang mất kết nối, Presence hoặc nguồn quyền không phản hồi; không đánh đồng với offline.
- Redis có thể còn phần tử đã hết hạn thêm một chu kỳ quét; truy vấn online đã loại phần tử ấy ngay theo deadline, không cần chờ xóa vật lý.
- Đang gõ: chỉ trong phòng đang mở, không lưu/gửi nội dung nháp. Throttle 2s; ngừng gõ 2,5s/blur/gửi/đổi phòng báo dừng, bên nhận tự xóa sau 5s nếu mất tín hiệu dừng.

## Giải thích khái niệm và code

| Code | Khái niệm | Chương |
|---|---|---|
| `backend/src/Services/Presence/ChatApp.PresenceService/Services/PresenceStore.cs` | Lease theo ConnectionId; Redis TIME và Lua nguyên tử; cùng user nhiều phiên | 5, 6, 8 |
| `Services/PresenceSweeper.cs` trong Presence | BackgroundService dọn phiên đến hạn, server khác chết vẫn phát hiện offline | 3, 8 |
| `backend/src/Services/Chat/ChatApp.ChatService/Services/PresenceCoordinator.cs` | gRPC lấy quyền theo lô, snapshot có phiên bản, Semaphore theo connection, thu hồi phòng | 2, 4, 6 |
| `Services/PresenceRelay.cs` trong Chat | Pub/Sub báo thay đổi cho mọi replica, heartbeat sửa tín hiệu bị lỡ | 4, 8 |
| `backend/src/Services/Chat/ChatApp.ChatService/Hubs/ChatHub.cs` | SignalR heartbeat/typing, danh tính từ JWT; lưu tin không phụ thuộc Presence | 4, 5 |
| `frontend/chat-app/src/app/core/services/chat-hub.service.ts` | Timer heartbeat, reconnect, snapshot hết hạn, typing tự hết hạn | 4, 8 |

Presence là dữ liệu tạm thời trong Redis. Sau công việc 2, tin nhắn và Outbox của Chat là dữ liệu bền vững trong MongoDB; Outbox đưa sự kiện vào Kafka cho cả người nhận online và offline. Người nhận quay lại lấy bù lịch sử qua REST, không đọc Kafka trực tiếp.

## Kiểm thử tự động

Các script tự tạo tài khoản/nhóm mới trong dữ liệu đồ án. Không dùng tài khoản cá nhân; không xóa volume.

```powershell
dotnet run scripts/chat-test.cs -- presence
```

Kỳ vọng: nhiều tab, global presence không cần mở phòng, số online loại chính mình, người lạ không có trong snapshot, typing/tin qua hai replica, lease hết hạn thật 60s, thu hồi phòng/quan hệ bạn bè, chat riêng vẫn hoạt động. Chạy hơn một phút.

```powershell
dotnet run scripts/chat-test.cs -- presencekill
```

Kịch bản lỗi: **SIGKILL chat-service-2 thật**, A còn ở replica 1; B tự offline sau lease, Redis dọn phiên ma; A gửi tin lúc B offline, Outbox vẫn phát Kafka. Script luôn start lại replica 2 trong finally; B nối lại và đọc đúng tin đã lỡ bằng REST.

Kiểm thử giao diện production (cần Edge, `node_modules` hiện có):

```powershell
cd frontend/chat-app
npm run build -- --configuration production
cd ../..
node scripts/presence-ui-test.cjs
```

Script dùng Edge headless với profile tạm riêng và proxy test localhost:4387, không đụng phiên trình duyệt người dùng. Kiểm tra Danh bạ vào thẳng, bạn bè/nhóm online, bộ lọc, typing, gửi tin, chat riêng. **Tạm stop Presence Service** để kiểm tra chưa xác định và vẫn chat được, rồi start lại trong finally. Cổng debug test 9397/9398 phải đang trống. Có thể đặt `PRESENCE_SCREENSHOT` là đường dẫn PNG để lưu ảnh.

Kết quả nghiệm thu 27/10/2026: backend build thành công; Angular production build thành công trên Node 14.21.3. Presence 17/17, SIGKILL/Kafka/REST 7/7, hồi quy bạn bè/chat riêng 24/24, UI Edge 15/15 (có kiểm tra danh sách nhóm/bạn bè tự cập nhật khi phía khác thay đổi). Tổng 63 kiểm tra. Cảnh báo NuGet hiện có được ghi trong PROGRESS.md; chưa nâng phiên bản thư viện trong công việc này.

## Demo thủ công

1. Hai trình duyệt A/B đã là bạn, cùng một nhóm. A mở Chat, B vào thẳng Danh bạ → A thấy B online và nhóm có một người khác online.
2. B mở thêm tab. Đăng xuất một phiên qua giao diện → phiên còn lại vẫn duy trì online nếu còn hoạt động.
3. Hai bên mở cùng chat riêng/nhóm. B nhập → A thấy đang gõ; ngừng nhập → tự mất. Gửi → tin tới bình thường.
4. B tắt mạng/đóng tab → không offline tức khắc; khoảng 60s từ heartbeat cuối mới offline. A vẫn gửi được, B quay lại thấy lịch sử.
5. `docker compose stop presence-service` → chưa xác định; gửi tin vẫn được. `docker compose start presence-service` → trạng thái tự phục hồi.

Thu hồi phòng có độ trễ tín hiệu/chu kỳ kiểm server, không nguyên tử cùng transaction Group. Công việc 3 bổ sung MembershipRoomMonitor độc lập heartbeat/Kafka, kiểm nguồn quyền cho gửi/vào phòng/history. Bộ đếm unread và Kafka dùng luồng hiện có. MongoDB và admin đã triển khai; presence quy mô production chưa triển khai.
