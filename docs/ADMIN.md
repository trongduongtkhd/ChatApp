# Công việc 3 – Quản trị tài khoản và nhóm

Phương án được duyệt ngày 27/10/2026, gồm seed admin, trang quản trị, Owner/Admin/Member, chuyển trưởng nhóm, phê duyệt và liên kết mời. Không thêm service nghiệp vụ hoặc database mới. Identity giữ tài khoản, Group giữ quản lý nhóm; Chat giữ tin trong MongoDB như công việc 2.

## Tài khoản admin và khởi động

Trong workspace hiện tại đã seed tài khoản `admin`. Mật khẩu hiện tại được lưu vào user-secrets `AdminSeed:Password` và bản hướng dẫn đăng nhập riêng tại `infra/admin-seed-credentials.local.txt` (gitignore). Không in mật khẩu vào log, không commit file này.

- Trang đăng nhập: http://localhost:4200/auth/login
- Trang người dùng: http://localhost:4200/admin/users
- Trang nhóm: http://localhost:4200/admin/groups
- Đăng nhập SystemAdmin tự chuyển tới trang quản trị; rail có nút Quản trị hệ thống.

Khi cài trên máy khác, đặt `AdminSeed:Password` trong user-secrets dùng chung hoặc biến môi trường `AdminSeed__Password` trước lần khởi động Identity đầu tiên. `AdminSeed:UserName` mặc định `admin`, `AdminSeed:Email` mặc định `admin@chatapp.local`. Mật khẩu seed ít nhất 8 ký tự theo yêu cầu cập nhật của người dùng. Không đặt mật khẩu trong appsettings.

Seed dùng transaction + advisory lock PostgreSQL: chỉ tạo khi chưa có, giữ mật khẩu/trạng thái của admin đã tồn tại. Nếu tên seed trùng tài khoản thường, dừng và yêu cầu đổi cấu hình, không tự nâng quyền. UserRegistered vào Outbox cùng transaction để tên admin đồng bộ sang Group.

Identity và Group tự áp migration bổ sung khi startup. Tài khoản cũ có role User, active và AuthVersion 0; nhóm cũ tắt phê duyệt và giữ nguyên Owner/Member. Hai migration mới: AccountAdministration và GroupGovernance. Không chạy reset database hoặc import lại lịch sử Mongo.

Build solution khi đã dừng service local để tránh Windows khóa DLL, rồi chạy từng service local như trước:

```powershell
dotnet build backend/ChatApp.sln
docker compose up -d --build chat-service-1 chat-service-2 nginx
dotnet run --no-build --project backend/src/Services/Identity/ChatApp.IdentityService
dotnet run --no-build --project backend/src/Services/Group/ChatApp.GroupService
dotnet run --no-build --project backend/src/Services/Notification/ChatApp.NotificationService
dotnet run --no-build --project backend/src/Gateway/ChatApp.Gateway
```

Các lệnh dotnet run chạy ở terminal riêng. Hạ tầng PostgreSQL/Redis/Kafka/Mongo/Presence phải sẵn sàng trước. Angular: `cd frontend/chat-app`, `npm start`. Angular 12 có thể cần restart dev server khi thêm module mới.

## Hai phạm vi quyền

SystemRole trong Identity là User hoặc SystemAdmin. Role nhóm trong Group là Owner/Admin/Member và chỉ áp dụng trong nhóm đó. SystemAdmin xem metadata nhóm thường và vai trò qua endpoint riêng; không tự trở thành thành viên hay được đọc lịch sử. Chat riêng bị chặn các nghiệp vụ role/approval/invite/transfer trước khi xét OwnerId.

| Thao tác nhóm thường | Owner | Admin nhóm | Member |
|---|---|---|---|
| Chat, đọc lịch sử | Có | Có | Có |
| Thêm người | Trực tiếp | Trực tiếp | Trực tiếp nếu tắt phê duyệt; bật thì Pending |
| Duyệt/từ chối | Có | Có | Không |
| Xóa người | Admin/Member | Chỉ Member | Chỉ tự rời |
| Bổ nhiệm/miễn nhiệm Admin | Có | Không | Không |
| Chuyển Owner | Có | Không | Không |
| Sửa tên/mô tả, cấu hình approval, xóa nhóm | Có | Không | Không |
| Tạo/đổi liên kết mời | Có | Có | Không |

Owner muốn rời phải chuyển quyền trước; Owner cũ trở thành Member. Người nhận quyền phải là thành viên đang hoạt động. Owner bị khóa vẫn giữ vai trò; mở khóa có thể tiếp tục quản lý, không tự bổ nhiệm người khác.
Owner vẫn có thể miễn nhiệm Admin đang bị khóa; bổ nhiệm Admin mới và chuyển Owner yêu cầu tài khoản đích hoạt động.

## Khóa, phiên bản xác thực và kết nối

JWT mang `system_role` và `auth_version`; token legacy thiếu auth_version được hiểu là 0. Middleware sau JWT đọc nguồn trạng thái hiện tại: Identity đọc DB nội bộ, Group/Chat/Notification dùng AccountAccess gRPC. Role hệ thống được lấy lại từ nguồn trước authorization, không tin quyền từ giao diện. Endpoint gọi thẳng service cũng bị kiểm tra.

Khóa/mở khóa tăng AuthVersion trong cùng transaction với audit và Outbox account-state-changed. Token cũ bị từ chối 401, kể cả sau mở khóa; người dùng phải đăng nhập mới. Sai thông tin đăng nhập và tài khoản khóa đều không cấp token. Admin không tự khóa mình và không được khóa hết admin hoạt động.

Hub filter kiểm account ở lúc kết nối và mỗi lời gọi. AccountConnectionMonitor chạy ở mọi Chat replica và Notification, kiểm theo lô tối đa 500 user/RPC rồi thu hồi connection lỗi quyền. Chu kỳ đợi 5s cộng thời gian RPC/xử lý, không phải cam kết hard deadline 5s. Chat gọi EndAsync để dọn Presence trước khi abort. Khi Presence không đáp ứng, lease còn lại tự hết hạn theo công việc 1.

Account gRPC không xác nhận được thì REST trả 503; monitor đóng connection theo fail closed. Không đổi IsLocked vì lỗi mạng. Identity phục hồi, token còn đúng phiên bản vẫn được dùng lại. Sự kiện Kafka account-state-changed phục vụ luồng sự kiện/quan sát; hiệu lực khóa không phụ thuộc Kafka. Publisher vẫn at-least-once.

Chat đọc quyền gửi/vào phòng/history từ Group gRPC hiện tại thay cache Redis cũ. MembershipRoomMonitor kiểm lại phòng khoảng 5s, gỡ người mất quyền ngay cả khi không heartbeat/Kafka ngừng; nhận realtime có độ trễ thu hồi, không đồng bộ nguyên tử với transaction Group. Group không xác nhận được quyền thì tạm từ chối gửi/đọc. Presence lỗi đơn thuần vẫn không chặn lưu/gửi tin.

## Phê duyệt và liên kết

Group lưu RequiresApproval, InviteTokenHash; bảng group_join_requests lưu Id/GroupId/UserId/RequestedBy/Status/CreatedAt/ReviewedBy/ReviewedAt. Status Pending/Approved/Rejected/Cancelled. Unique index một Pending cho một cặp group/user. Người Pending chưa có group_members, không được xem chi tiết thành viên, vào phòng hoặc đọc tin.

Owner/Admin thêm người là duyệt trực tiếp. Member thêm hoặc người tự xin qua link: RequiresApproval bật thì tạo Pending, tắt thì thêm Member. Tắt approval vẫn giữ yêu cầu cũ để quản lý duyệt, không tự nhận hàng loạt. Người xin tự hủy; có thể gửi yêu cầu mới sau hủy/từ chối. Thêm trực tiếp người đang Pending đồng thời đóng request Approved.

Invite token là 24 byte ngẫu nhiên (192 bit), biểu diễn 48 ký tự hex. DB chỉ giữ SHA-256 của token. URL `/groups/join/{token}`; đăng nhập để xem metadata tối thiểu và xin vào/tham gia. Preview không trả danh sách thành viên. Tạo liên kết mới thu hồi liên kết cũ, giữ thành viên và request đang chờ. Token chỉ trả một lần sau tạo; UI có ô chọn/copy, không đọc ngược token từ hash. Đăng nhập từ link có returnUrl giới hạn đúng đường join nội bộ.

Panel nhóm hiện role, menu từng người, chế độ approval, link và tối đa 200 yêu cầu đang chờ mỗi lượt. Khi xử lý xong, lượt tải tiếp lấy các yêu cầu tiếp theo. Panel đang mở refresh khoảng 8s; trang join đang Pending cũng refresh khoảng 8s. Backend luôn kiểm quyền hiện tại dù UI chưa refresh.

## Transaction và thứ tự

Mọi thay đổi quản lý nhóm dùng transaction, `SELECT *, xmin ... FOR UPDATE` dòng groups trước khi đọc role/request rồi ghi. Phải chọn xmin tường minh vì SELECT * không bao gồm system columns. Unique index chỉ cho tối đa một role Owner/group và một Pending/group/user. Chuyển Owner hạ role cũ trước để không chạm unique index tức thời, rồi đổi target/OwnerId trong cùng transaction. Version xmin kiểm thao tác UI cũ, trả 409.

Chỉ thay đổi tư cách thành viên mới phát member-added/member-removed cùng transaction. Đổi role không phát giả member-added, không reset unread. Hai người cùng duyệt cùng kết quả là idempotent; duyệt/từ chối tranh nhau chỉ một hướng thắng, bên kia 409. Group audit giữ các thao tác quản lý; Identity account_audits giữ khóa/mở khóa.

## API chính

| API | Chủ sở hữu |
|---|---|
| GET /api/admin/users?q=&locked=&page=&pageSize= | Identity, SystemAdmin |
| GET /api/admin/users/{id} | Identity, thông tin + 30 audit gần nhất |
| PUT /api/admin/users/{id}/lock | Identity, isLocked/version/reason |
| GET /api/admin/groups?q=&page=&pageSize= | Group, SystemAdmin, nhóm thường |
| GET /api/admin/groups/{id}?page=&pageSize= | Group, metadata + thành viên có phân trang |
| PUT /api/groups/{id}/members/{userId}/role | Group, role/version |
| PUT /api/groups/{id}/owner | Group, userId/version |
| PUT /api/groups/{id}/approval | Group, requiresApproval/version |
| GET /api/groups/{id}/join-requests | Group, Owner/Admin |
| PUT /api/groups/{id}/join-requests/{requestId} | Group, approve |
| DELETE /api/groups/{id}/join-requests/mine | Group, yêu cầu của bản thân |
| POST /api/groups/{id}/invite | Group, version; trả token mới |
| GET /api/groups/invites/{token} | Group, preview sau đăng nhập |
| POST /api/groups/invites/{token}/join | Group, 201 Member hoặc 202 Pending |

POST members hiện có cũng trả 201 Member hoặc 202 Pending tùy quyền/cấu hình. Admin users/groups mặc định pageSize 20, tối đa 100; tìm kiếm thực hiện ở DB, escape ký tự wildcard. Audit và DTO không chứa PasswordHash.

## Kiểm thử và demo lỗi

Nghiệm thu 27/10/2026: **232 kiểm tra đạt**: 80 API/nghiệp vụ/thu hồi, 30 UI quản trị và nhóm, 12 dependency/recovery, 110 hồi quy công việc 1+2. Backend solution build 0 lỗi; Angular 12 production build thành công trên Node 14.21.3, không có cảnh báo budget mới. Ảnh sáng/tối đã kiểm tra, UI không có runtime exception. Cảnh báo NuGet MessagePack/SignalR.Common đã có từ trước được ghi trong PROGRESS.md.

Kiểm tra source legacy Mongo vẫn khớp SHA-256 của 4.629 tin cũ. 200 tin đồng thời trong lượt hồi quy ~4,57s, seq liên tục; đây là số đo local. Thu hồi phòng được kiểm cả khi user không heartbeat và Kafka tắt. Chủ nhóm vẫn có thể miễn nhiệm Admin đang khóa, nhưng không bổ nhiệm lại cho tới khi account hoạt động.

Khi services đang chạy đúng mã mới, dùng một lệnh (fault tests không chạy song song):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-admin-system.ps1
```

`-SkipBuild` nếu đã build, `-SkipRegression` nếu chỉ muốn công việc 3. Các lệnh riêng:

```powershell
node scripts/admin-test.cjs
node scripts/admin-ui-test.cjs
node scripts/admin-dependency-test.cjs
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-chat-system.ps1 -SkipBuild
```

Test tạo fixture mới và giữ dữ liệu người dùng. admin-test tạm stop/start Kafka, khôi phục tài khoản fixture trong finally. dependency-test chỉ stop/start đúng executable Identity/Group trong workspace, dùng terminal cùng tài khoản chạy services. UI Edge headless profile riêng/proxy 4389, debug ports 9399/9400/9401; không dùng phiên duyệt của người dùng. Mật khẩu/token không được in ra log.

Hai wrapper `test-admin-system.ps1` và `test-chat-system.ps1` tự dọn fixture của chính lượt chạy trong finally (lọc theo thời điểm bắt đầu), chờ Outbox/consumer xử lý hết và backup trước khi xóa. Chạy file test riêng bằng node không có bước dọn tự động của wrapper. Công cụ xem trước: `node scripts/cleanup-test-data.cjs`; dọn dữ liệu mẫu đã xác định: thêm `--apply --wait`. Không chạy lại test chỉ để xác minh cleanup vì sẽ tạo fixture mới.

Kịch bản demo: hai trình duyệt → Owner bật phê duyệt, tạo link → người kia xin vào → Pending không đọc chat → Admin duyệt → người kia chat được. Sau đó SystemAdmin khóa người đang chat → server đóng kết nối, UI về đăng nhập → mở khóa → đăng nhập mới, lịch sử cũ còn nguyên.

Demo lỗi: tắt Kafka rồi duyệt/khóa/gỡ thành viên → SQL vẫn commit, kiểm quyền gRPC vẫn có hiệu lực, Outbox chờ gửi bù. Dừng Identity → API liên quan 503 và connection bị thu hồi, không gán nhầm tài khoản bị khóa. Dừng Group → Chat từ chối thao tác cần kiểm membership, khởi động lại rồi retry cùng messageId. Không xóa volume.

## Code minh họa theo chương

| File | Khái niệm | Chương |
|---|---|---|
| Common/Auth/AccountAccess.cs, account_access.proto | gRPC và kiểm quyền từ service sở hữu | 2, 4, 5 |
| Common/Auth/AccountConnections.cs | Hub filter, registry, worker, thu hồi nhiều phiên | 3, 4, 8 |
| Identity/Services/AdminSeeder.cs | Seed idempotent, advisory lock, Outbox | 5, 6 |
| Identity/Controllers/AdminUsersController.cs | RBAC, khóa/version/audit, chống ghi đè | 5, 6 |
| Group/Services/GroupGovernance.cs | Row lock, transaction, state machine, token/hash | 5, 6 |
| Chat/Services/MembershipRoomMonitor.cs | Kiểm quyền server định kỳ, sửa sai khi mất tín hiệu | 3, 8 |
| frontend/features/admin và group-join | Lazy module, guard, REST, polling, xác nhận thao tác | 2, 4, 8 |

Backend paths tương đối với `backend/src/BuildingBlocks/ChatApp.Common` hoặc `backend/src/Services/{Service}/ChatApp.{Service}Service` theo tên cột File.
