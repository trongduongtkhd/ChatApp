# Gửi ảnh và file — kho local tương thích S3

Phương án đã duyệt 28/10/2026. SeaweedFS 4.51 / AWS S3 SDK 4.0.104.7; không truy cập AWS, không cần tài khoản AWS/thẻ và không có thời hạn Free Plan. Dùng CPU/RAM/ổ đĩa của máy chạy Docker.

## Chạy và cấu hình

Từ thư mục gốc:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/init-attachment-storage.ps1
docker compose up -d --build attachment-storage chat-service-1 chat-service-2
docker compose restart nginx
```

Script chỉ sinh credentials nếu `infra/attachments.local.env` chưa tồn tại, không in khóa; file có gitignore. Phải chạy một lần trước mọi lệnh Compose trên bản checkout mới. Không đổi/xóa file credentials nếu đang dùng kho. Không cần npm package mới.

Gateway có kiểm tra Content-Length cho upload quá 25 MiB để trả 413 trước khi proxy ghi body. Nếu Gateway đang chạy bản cũ, Ctrl+C ở terminal của Gateway trước khi build, sau đó:

```powershell
dotnet build backend/src/Gateway/ChatApp.Gateway
dotnet run --no-build --project backend/src/Gateway/ChatApp.Gateway
```

Identity/Group/Notification chạy như trước. Hai Chat Service chạy Docker. Angular:

```powershell
cd frontend/chat-app
npm start
```

Nếu Angular dev server không nhận component mới, Ctrl+C rồi npm start lại. Không khởi chạy thêm instance khi cổng 4200 đang được dùng.

## Sử dụng

- Trong chat nhóm hoặc chat riêng: bấm Gửi ảnh/Đính kèm file, chọn file, chờ Sẵn sàng gửi, nhập chú thích nếu muốn rồi Gửi.
- Ảnh JPEG/PNG/WebP <=10 MiB; file PDF/DOCX/XLSX/PPTX/TXT UTF-8/ZIP <=25 MiB; <=5 file và tổng <=50 MiB/tin.
- Ảnh hiển thị trong lịch sử, bấm để xem lớn, Escape/Đóng ảnh để thoát. Tài liệu tải về bằng nút Tải xuống. Không preview Word/Excel/PDF trong iframe.
- Angular 12 cần SafeUrl cho ảnh Blob: chỉ đánh dấu URL do `URL.createObjectURL` của chính ứng dụng tạo, không đánh dấu tin cậy URL tùy ý từ người dùng/server. URL được thu hồi khi bỏ draft/destroy/logout.
- Đổi conversation giữ bản nháp file theo đúng conversation trong phiên ứng dụng. F5 không giữ đối tượng File chưa gửi; upload bỏ dở được dọn sau 24h. Logout dọn request/URL bản nháp phía client.
- Lịch sử tuần có thông tin đính kèm, vẫn chỉ đọc. Người offline lấy lại tin qua REST khi quay lại; Notification/Kafka vẫn đếm chưa đọc như tin chữ.

## Khái niệm, code và chương

| Code | Khái niệm | Chương |
|---|---|---|
| docker-compose.attachments.yml | Object storage, S3 API, Docker DNS/volume | 2, 3, 4, 5 |
| Services/AttachmentStorage.cs | Backend truy cập kho qua SDK, interface tách hạ tầng | 2, 4 |
| Controllers/AttachmentsController.cs | REST binary, JWT, quyền nguồn gRPC, giới hạn I/O/temp | 4, 5, 8 |
| Services/AttachmentService.cs + MessageService.cs | Upload ID/SHA256, trạng thái, transaction cùng message/Outbox | 5, 6 |
| Services/AttachmentCleanupWorker.cs | BackgroundService, claim trước delete, cạnh tranh giữa hai replica | 3, 6, 8 |
| attachment-api.service.ts + message-attachment.component.ts | Upload progress, Blob URL/lazy ảnh, draft theo conversation | 4, 8 |

Mongo lưu metadata/lịch sử; SeaweedFS lưu bytes. SendMessageWithAttachments gửi mã đính kèm qua SignalR, không gửi file binary qua hub/Kafka. Mongo và kho không chung transaction: Uploading→Ready trước, Ready→Attached cùng giao dịch tin; upload bỏ dở được worker dọn. Không làm I/O kho trong transaction retry Mongo.

## Quyền và lưu trữ

Bucket/S3 và các cổng quản trị không publish ra host. Browser chỉ gọi API qua Gateway. Download bắt buộc tài khoản hợp lệ + thành viên hiện tại + attachment Attached. Pending/stranger/locked không được truy cập; SystemAdmin không tự có quyền đọc. Đã tải về thiết bị thì không thể thu hồi.

Object key do server sinh, không dùng đường dẫn người dùng. Server kiểm signature ảnh/PDF, cấu trúc ZIP/Office và TXT UTF-8; không chạy hoặc giải nén file. Kiểm định dạng không tương đương quét virus.

Volume giữ file khi container được tạo lại, nhưng một node/một ổ đĩa chưa phải HA hay backup. Không dùng `docker compose down -v` hoặc xóa volume nếu cần giữ file. Gửi thành công được giữ, 24h chỉ áp dụng upload chưa gửi. Dữ liệu local không phải dữ liệu đã lưu trên AWS. Chuyển sang AWS cần cấu hình, chuyển file và kiểm thử riêng.

## Kiểm thử và demo lỗi

Cần backend đang chạy và production Angular đã build bằng Node 14:

```powershell
cd frontend/chat-app
npm run build -- --configuration production
cd ../..
node scripts/attachment-test.cjs --ui
```

Script dùng user/nhóm riêng, finally backup/dọn đúng fixture cả SQL/Mongo/Redis và object SeaweedFS; không để lại hàng loạt tài khoản mẫu. Browser Edge headless profile riêng, proxy 4388, debug 9410/9411. Ảnh kiểm tra sáng/tối ở TEMP. Không chạy đồng thời các suite fixture vì cleanup theo thời điểm bắt đầu.

Các ca khóa tài khoản sử dụng admin seed hiện có qua file local `infra/admin-seed-credentials.local.txt` (xem `docs/ADMIN.md`), không in thông tin đăng nhập. Chỉ chạy lại UI bằng `node scripts/attachment-test.cjs --ui-only` khi cần kiểm tra bố cục; lệnh này cũng tạo/dọn fixture riêng.

Kiểm tra API: metadata/bytes, idempotency, cross-replica, quyền/loại/dung lượng, cạnh tranh một file, rollback tổng dung lượng, hết hạn/hủy, lịch sử tuần/offline, chat riêng/hủy bạn/gỡ thành viên, khóa tài khoản. Test chủ động dừng rồi force-recreate đúng `attachment-storage` để kiểm tra lỗi và volume; finally phục hồi nếu lỗi.

Demo thủ công: gửi ảnh/file thành công, `docker compose stop attachment-storage`, tải file → thông báo tạm gián đoạn; gửi chữ → vẫn thành công. `docker compose start attachment-storage`, bấm tải lại → nội dung cũ còn nguyên. Không xóa volume.
