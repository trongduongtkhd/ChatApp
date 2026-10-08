# Thiết kế giao diện ChatApp (Phần 10)

Giao diện theo phong cách Messenger. Thiết kế gốc nằm trong `docs/ui/`:

| File | Vai trò |
|---|---|
| `docs/ui/tokens.json` | Design token: màu (sáng/tối), chữ, khoảng cách, bo góc. **Nguồn sự thật về màu và kích thước.** |
| `docs/ui/app.css` | CSS của bản thiết kế (biến CSS + lớp `.ca-*`). Dùng làm điểm xuất phát cho `src/styles.scss`. |
| `docs/ui/components.css` | CSS các thành phần mẫu trong design system. |
| `docs/ui/screens/*.dc.html` | Bản thiết kế từng màn hình. Chỉ **đọc phần HTML trong `<x-dc>`** để lấy bố cục, chữ, lớp CSS. Bỏ qua `<script src="./support.js">`, `<helmet>`, `<sc-if>` (là cú pháp của công cụ thiết kế; `<sc-if>` tương ứng `*ngIf`). |

## Quy tắc chung

- **Angular 12**: NgModule, `*ngIf`/`*ngFor`, RxJS. Không standalone, không signals, không `@if`.
- Font **Be Vietnam Pro** (Google Fonts, 400/600/700) khai báo trong `index.html`.
- Biến CSS lấy từ `app.css` đặt ở `:root` trong `src/styles.scss`; theme tối là lớp `.dark` trên `<body>`.
- Màu trạng thái có nghĩa cố định: `--online` chỉ cho chấm online, `--unread` chỉ cho số chưa đọc và tin gửi lỗi, `--warning` chỉ cho "Đang kết nối lại".
- Icon: SVG nét (kiểu Lucide), không emoji. Nút chỉ có icon phải có `aria-label`.
- Không thêm thư viện UI (Material, PrimeNG…): CSS tự viết theo `app.css`.

## Màn hình → component (theo cấu trúc frontend trong DESIGN.md)

| Bản thiết kế | Component Angular | Ghi chú |
|---|---|---|
| `Login.dc.html` | `features/auth/pages/login` | Bố cục chia đôi; lỗi 401 hiện hộp đỏ "Tên đăng nhập hoặc mật khẩu không đúng." |
| `Register.dc.html` | `features/auth/pages/register` | Lỗi 409 hiện dưới ô Tên đăng nhập |
| `Main.dc.html` | `features/chat/pages/chat-layout` + `group-list` + `chat-window` + `message-list` + `message-input` | Bố cục chính |
| `Members.dc.html` | `features/chat/components/member-panel` | Mở từ nút Thành viên trên đầu khung chat |
| `CreateGroup.dc.html` | `features/chat/components/create-group-dialog` | |
| `EditConflict.dc.html` | `features/chat/components/edit-group-dialog` | Gửi kèm `version`; nhận 409 thì hiện hộp cảnh báo + nút "Tải lại bản mới" |
| `Empty.dc.html` | trạng thái trống / 403 / 503 trong `chat-layout` và `chat-window` | |

Thành phần dùng chung đặt trong `shared/components`: `avatar` (có chấm online), `unread-badge`, `connection-banner`.

## Hành vi gắn với backend (bắt buộc)

### ConnectionBanner (`Main.dc.html`, tweak "connection")
- `onreconnecting` → dải vàng "Đang kết nối lại… Tin bạn gửi sẽ được gửi khi có kết nối."
- `onreconnected` → dải xanh "Đã kết nối lại" trong 2 giây rồi ẩn; đồng thời: gọi lại `JoinGroup` cho nhóm đang mở, tải lại lịch sử (tin sau `sequenceNumber` lớn nhất đang có), gọi `GET /api/notifications/unread`.
- `onclose` (hết lượt thử) → giữ dải vàng, thêm nút "Thử lại".

### Trạng thái tin nhắn của mình (`Main.dc.html`)
- Bấm Gửi: sinh `messageId` (GUID v7) **trước**, thêm ngay bong bóng mờ "Đang gửi…".
- Server trả `MessageDto` → thay bằng tin thật (có `sequenceNumber`), chỉ tin cuối hiện "Đã gửi ✓".
- Lỗi/timeout → chữ đỏ "Không gửi được · Gửi lại"; Gửi lại dùng **cùng `messageId`**.
- `ReceiveMessage` trùng `id` với bong bóng đang chờ → gộp, không hiện 2 lần.

### Danh sách tin
- Luôn sắp xếp theo `sequenceNumber` tăng dần; không giả định số liên tục.
- Cuộn lên đầu → dải "Đang tải tin cũ…", gọi lịch sử với `beforeSeq` = seq nhỏ nhất đang có, `limit=50`.
- Tin liên tiếp của cùng người: gộp cụm, tên người gửi chỉ ở tin đầu cụm (tin người khác), avatar nhỏ ở tin cuối cụm.

### Danh sách nhóm và chấm đỏ
- Mở app: `GET /api/groups` + `GET /api/notifications/unread`.
- `UnreadCountChanged(groupId, count)` từ `/hubs/notifications` cập nhật huy hiệu ngay; 0 thì ẩn; trên 99 hiện "99+".
- Mở nhóm: `POST /api/notifications/groups/{groupId}/read` với `sequenceNumber` lớn nhất đang hiện.

### Presence
- `JoinGroup` trả danh sách userId online → chấm xanh trên avatar thành viên; `UserPresenceChanged` cập nhật về sau.
- Đầu khung chat: "4 thành viên · Lan, Minh đang hoạt động".

### Lỗi
| Mã | Hiển thị |
|---|---|
| 401 | Về trang đăng nhập |
| 403 khi mở nhóm | Màn "Bạn không còn là thành viên nhóm này" (`Empty.dc.html`, tweak forbidden) |
| 503 | Màn "Hệ thống đang gián đoạn" + nút Thử lại (`Empty.dc.html`, tweak unavailable) |
| 409 sửa nhóm | Hộp cảnh báo trong dialog (`EditConflict.dc.html`) |
| `HubException` | Hiện nguyên thông báo của server dưới ô nhập, màu `--ink-muted` |
