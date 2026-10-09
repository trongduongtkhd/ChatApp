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
- Biến CSS lấy từ `app.css` đặt ở `:root` trong `src/styles.scss`; theme tối là lớp `.dark` trên `<body>` (bật/tắt bởi `ThemeService` theo lựa chọn Sáng/Tối/Hệ thống, Bước 9b).
- Màu trạng thái có nghĩa cố định: `--online` chỉ cho chấm online, `--unread` chỉ cho số chưa đọc và tin gửi lỗi, `--warning` chỉ cho "Đang kết nối lại".
- Icon: SVG nét (kiểu Lucide), không emoji. Nút chỉ có icon phải có `aria-label`.
- Không thêm thư viện UI (Material, PrimeNG…): CSS tự viết theo `app.css`.

## Màn hình → component (theo cấu trúc frontend trong DESIGN.md)

| Bản thiết kế | Component Angular | Ghi chú |
|---|---|---|
| `Login.dc.html` | `features/auth/pages/login` | Bố cục chia đôi; lỗi 401 hiện hộp đỏ "Tên đăng nhập hoặc mật khẩu không đúng." |
| `Register.dc.html` | `features/auth/pages/register` | Lỗi 409 hiện dưới ô Tên đăng nhập |
| `Main.dc.html` | `features/chat/pages/chat-layout` + `group-list` + `chat-window` + `message-list` + `message-input` | Màu, chữ, bong bóng. **Bố cục: xem "Bố cục bên trong (Bước 9b)"** |
| `Members.dc.html` | `features/chat/components/member-panel` | Mở từ nút Thành viên trên đầu khung chat. **Bố cục: xem Bước 9b** |
| `CreateGroup.dc.html` | `features/chat/components/create-group-dialog` | |
| `EditConflict.dc.html` | `features/chat/components/edit-group-dialog` | Gửi kèm `version`; nhận 409 thì hiện hộp cảnh báo + nút "Tải lại bản mới" |
| `Empty.dc.html` | trạng thái trống / 403 / 503 trong `chat-layout` và `chat-window` | Nội dung các trạng thái giữ nguyên |
| (không có bản thiết kế) | `shared/components/app-rail`, `features/settings/pages/settings-page` | Bước 9b |
| (không có bản thiết kế) | `features/contacts/pages/contacts-page` | Phần 11 – trang Danh bạ, cùng khuôn với Cài đặt |

Thành phần dùng chung đặt trong `shared/components`: `avatar` (có chấm online), `unread-badge`, `connection-banner`, `confirm-dialog`, `app-rail`.

## Bố cục bên trong (Bước 9b)

Từ Bước 9b, **bố cục bên trong** (sau khi đăng nhập) không còn theo `Main.dc.html` / `Members.dc.html` / `Empty.dc.html` nữa. Các file đó vẫn là nguồn cho màu, chữ, bong bóng và nội dung các trạng thái. Trang đăng nhập/đăng ký, hộp tạo nhóm và hộp xung đột 409 giữ thiết kế gốc. Màu thương hiệu `#0866FF`, font Be Vietnam Pro không đổi.

```
┌──────┬──────────────────┬──────────────────────────────┬──────────────┐
│ rail │ Tin nhắn     [✎] │ [av] Tên nhóm           [👥] │ panel thành  │
│ 72px │ [ Tìm kiếm     ] │ N thành viên · … đang h.động │ viên (tùy    │
│ tối  │ Tất cả|Chưa đọc|N│ ───────── ( Hôm nay ) ────── │ chọn)        │
│ logo │ [av] Tên   14:27 │ bong bóng…                   │              │
│ chat │      Tin…    (3) │ 14:27                        │              │
│ cài  │ …                │                22:24 · Đã gửi✓│              │
│ đặt  │                  │ [ Aa                   ] [➤] │              │
│ (me) │                  │                              │              │
└──────┴──────────────────┴──────────────────────────────┴──────────────┘
```

- **Rail** (`app-rail`, 72px, nền tối ở CẢ hai theme, token `--rail-bg`, `--rail-ink`, `--rail-ink-muted`, `--rail-active`, `--rail-hover`): logo → `/chat`, Đoạn chat (`/chat`), Danh bạ (`/contacts`, Phần 11), Cài đặt (`/settings`), avatar của mình ở đáy → `/settings`. Mục đang mở: nền sáng hơn + vạch trái 4px màu `--brand`, `aria-current="page"`. Không có chuông thông báo.
- **Danh sách chat** (`chat-layout` + `group-list`): tiêu đề "Tin nhắn" + nút Tạo nhóm; ô tìm kiếm (bỏ dấu); tab `role="tablist"` **Tất cả / Chưa đọc / Nhóm**, lọc phía client: Chưa đọc = `unread > 0` (kèm số nhóm chưa đọc; nhóm vừa mở ở tab này được giữ lại tới khi đổi tab), Nhóm = bỏ chat riêng (`!isDirect`, Phần 11). Mỗi dòng: avatar · tên · giờ ở góc phải (pipe `listTime`: hôm nay `HH:mm`, "Hôm qua", `T2`…`CN`, cũ hơn `dd/MM`) / "Bạn: …" tin cuối · huy hiệu. Chưa đọc: tin cuối và giờ in đậm, giờ màu `--brand`. Phím ← → chuyển tab.
- **Khung chat**: đầu khung (avatar nhóm, tên, "N thành viên · … đang hoạt động", nút Thành viên). Mốc ngày chỉ khi sang ngày: viên thuốc ở giữa, kẻ ngang hai bên (`.ca-day-sep` / `.ca-day-pill`): "Hôm nay", "Hôm qua", "Thứ 2"…"Chủ nhật", `dd/MM/yyyy`. Giờ nhỏ `HH:mm` dưới bong bóng **cuối mỗi cụm** (`.ca-time`); cụm tách khi đổi người gửi, sang ngày, hoặc cách nhau ≥ 15 phút. Tin cuối cùng của mình gộp: `22:24 · Đã gửi ✓`.
- **Panel thành viên** (gọn): đầu panel một hàng cao bằng đầu khung chat (avatar, tên, mô tả 1 dòng, nút bút chì "Sửa thông tin nhóm" cho Owner); "THÀNH VIÊN · N" + nút Thêm/Xong; dòng thành viên ~52px (chip "Trưởng nhóm", nút X `aria-label="Xóa {tên} khỏi nhóm"`); ghi chú đồng bộ ~1 giây; Xóa nhóm / Rời nhóm ở đáy.
- **Trang Cài đặt** (`/settings`, lazy, AuthGuard): thẻ **Hồ sơ** (avatar chữ, tên hiển thị, @username – chỉ xem; nút Đăng xuất) và thẻ **Giao diện** (radio thật Sáng / Tối / Hệ thống, `role="radiogroup"`).
- **Trang Danh bạ** (`/contacts`, lazy, AuthGuard – Phần 11): rail có thêm mục Danh bạ giữa Đoạn chat và Cài đặt. Cùng khuôn với Cài đặt (`.ca-page`, cột 640px, `.ca-card`). Thẻ **Thêm bạn**: ô tìm (API tìm của group-service, bỏ chính mình), mỗi kết quả hiện đúng trạng thái: nút Kết bạn / "Đã gửi lời mời" / nút Chấp nhận / chip "Bạn bè". Thẻ thứ hai: tab `role="tablist"` **Bạn bè / Lời mời / Đã gửi** (phím ← →; chỉ tab Lời mời có huy hiệu số đỏ). Dòng dùng lại `.ca-item.mp-row`, nút `.mp-add`: Bạn bè → "Nhắn tin" (`/chat/{directGroupId}`) + nút "Hủy kết bạn" (`confirm-dialog`, nói rõ lịch sử được giữ); Lời mời → Chấp nhận / Từ chối (kèm giờ mời theo `listTime`); Đã gửi → Hủy lời mời. Kết quả mỗi thao tác hiện ở hộp thông báo đầu trang (xanh = thành công, đỏ = `title` lỗi của server, vd 409 "Người này đã mời bạn, hãy chấp nhận lời mời"); sau mỗi thao tác nạp lại cả 3 danh sách từ server.
- **Chat riêng** (Phần 11, `isDirect`): dòng danh sách và đầu khung hiện tên + avatar chữ (kiểu người) của `peer`, đầu khung ghi "Trò chuyện riêng" / "Đang hoạt động" thay cho "N thành viên"; không có nút Thành viên (không sửa nhóm / thêm / xóa / rời). Tab Nhóm bỏ chat riêng; tìm kiếm theo tên người kia. Chấp nhận ở Danh bạ → thông báo "Đang tạo cuộc trò chuyện riêng…" (vòng quay) → "…đã sẵn sàng" + nút Nhắn tin. Bị hủy kết bạn khi đang mở → màn khóa "Hai bạn không còn là bạn bè" + nút Về danh sách / Mở Danh bạ.
- **Theme**: `ThemeService` lưu lựa chọn ở `localStorage['chatapp.theme']` (đọc/ghi bọc try/catch), mặc định "Hệ thống" (theo `prefers-color-scheme`, đổi ngay khi máy đổi); Sáng/Tối bỏ qua cài đặt máy.
- Không làm: gọi điện, video, ghim tin, thả cảm xúc, gửi file, công tắc thông báo, ẩn online, xác nhận đã đọc, sửa hồ sơ.

## Hành vi gắn với backend (bắt buộc)

### ConnectionBanner (`Main.dc.html`, tweak "connection")
- `onreconnecting` → dải vàng "Đang kết nối lại… Tin bạn gửi sẽ được gửi khi có kết nối."
- `onreconnected` → dải xanh "Đã kết nối lại" trong 2 giây rồi ẩn; đồng thời: gọi lại `JoinGroup` cho nhóm đang mở, tải lại lịch sử (tin sau `sequenceNumber` lớn nhất đang có), gọi `GET /api/notifications/unread`.
- `onclose` (hết lượt thử) → giữ dải vàng, thêm nút "Thử lại".

### Trạng thái tin nhắn của mình (`Main.dc.html`)
- Bấm Gửi: sinh `messageId` (GUID v7) **trước**, thêm ngay bong bóng mờ "Đang gửi…".
- Server trả `MessageDto` → thay bằng tin thật (có `sequenceNumber`), chỉ tin cuối hiện "Đã gửi ✓" (từ 9b gộp với giờ: `22:24 · Đã gửi ✓`).
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
