# Thiết kế hệ thống ChatApp

## 1. Tổng quan kiến trúc
- Backend .NET 10; frontend Angular 12; hạ tầng (PostgreSQL, MongoDB, Redis, Kafka, Nginx) chạy bằng Docker Compose.
- Microservices + Event-driven (Kafka). Identity/Group/Notification có database PostgreSQL riêng; Chat dùng MongoDB sharded cluster; Presence dùng Redis. PostgreSQL chat_db cũ được giữ để đối chiếu.
- Angular chỉ gọi qua **API Gateway (YARP)**. Gateway là cửa vào giữa client và hệ thống, KHÔNG dùng để các service gọi nhau.
- Service gọi nhau: **gRPC** (cần trả lời ngay) hoặc **Kafka** (thông báo, không chờ).
- Dữ liệu giữa các service nhất quán theo kiểu **eventual consistency**: service sở hữu dữ liệu phát sự kiện, service khác tự cập nhật bản sao.
- Phát sự kiện bằng **Transactional Outbox**: event cùng transaction với dữ liệu nghiệp vụ (bảng PostgreSQL của Identity/Group; collection Mongo của Chat), BackgroundService gửi Kafka sau commit. Chat cấp sequenceNumber trong cùng transaction Mongo; publisher theo conversation có lease/fencing.

```
Angular ──REST/SignalR──► API Gateway (YARP)
                           ├─► identity-service
                           ├─► group-service
                           ├─► Nginx (load balancer) ──► chat-service-1 / chat-service-2
                           └─► notification-service
chat-service ──gRPC──► group-service
chat-service ──gRPC──► identity-service (bạn bè) / presence-service (lease)
identity/group/chat ──Kafka──► group/chat/notification
SignalR Redis Backplane đồng bộ giữa các bản chat-service
```

## 2. Danh sách service

| Service | Project | Tên Docker | Database | Port local |
|---|---|---|---|---|
| API Gateway | ChatApp.Gateway | api-gateway | – | 5000 |
| Identity | ChatApp.IdentityService | identity-service | identity_db | 5001 (REST), 5011 (gRPC nội bộ) |
| Group | ChatApp.GroupService | group-service | group_db | 5002 (REST), 5012 (gRPC, HTTP/2) |
| Chat | ChatApp.ChatService | chat-service-1, chat-service-2 | chat_history (MongoDB, 2 shards) | 5003, 5013 (Docker, cổng 8080 trong container) |
| Notification | ChatApp.NotificationService | notification-service | notification_db | 5004 |
| Presence | ChatApp.PresenceService | presence-service | Redis (không thêm PostgreSQL) | 5005 health (localhost); 5015 gRPC chỉ trong Docker |
| Nginx (load balancer chat) | – | nginx | – | 5080 |

Từ Phần 8 chat-service chạy bằng Docker (2 bản, cùng image `chatapp/chat-service`), Gateway gọi chat qua Nginx `localhost:5080`. Cổng 5003/5013 chỉ để demo nối thẳng vào từng bản.

## 2b. Cấu trúc thư mục

```
ChatApp/
├── CLAUDE.md
├── docs/
├── docker-compose.yml
├── .env                          # mật khẩu DB, JWT secret (không commit)
├── .env.example                  # mẫu .env có đủ tên biến, giá trị giả (commit)
├── infra/
│   ├── postgres/init.sql         # tạo 4 database
│   ├── nginx/nginx.conf          # load balancing chat-service
│   └── backups/                  # file backup pg_dump
├── scripts/                      # script test (bắn 200 tin, gửi trùng...)
├── backend/
│   ├── ChatApp.sln
│   ├── Directory.Build.props     # UserSecretsId chung "chatapp-dev" cho mọi project (JWT secret không lệch)
│   ├── .dockerignore             # build context của Dockerfile là backend/: bỏ bin/, obj/ của Windows
│   └── src/
│       ├── BuildingBlocks/
│       │   ├── ChatApp.Contracts/      # hợp đồng dùng chung
│       │   │   ├── DirectChat.cs       # mã nhóm chat riêng tất định theo cặp userId (UUID v5) – identity và group cùng tính
│       │   │   ├── Events/             # UserRegistered, FriendshipChanged, MemberAdded, MemberRemoved, MessageSent
│       │   │   └── Protos/             # group_membership.proto, presence.proto
│       │   └── ChatApp.Common/         # code hạ tầng dùng chung
│       │       ├── Kafka/              # KafkaProducer, KafkaConsumerBase (BackgroundService)
│       │       ├── Outbox/             # OutboxMessage, cấu hình bảng outbox_messages, OutboxPublisher (BackgroundService)
│       │       ├── Auth/               # extension cấu hình JWT
│       │       └── Logging/            # extension cấu hình Serilog
│       ├── Gateway/ChatApp.Gateway/
│       └── Services/
│           ├── Identity/ChatApp.IdentityService/
│           ├── Group/ChatApp.GroupService/
│           ├── Chat/ChatApp.ChatService/
│           ├── Notification/ChatApp.NotificationService/
│           └── Presence/ChatApp.PresenceService/
└── frontend/chat-app/            # Angular 12
```

Bên trong mỗi service (một project Web API, chia theo thư mục):
```
ChatApp.<Tên>Service/
├── Controllers/       # REST API
├── Hubs/              # SignalR hub (chat, notification)
├── Grpc/              # gRPC server (group) hoặc client (chat)
├── Entities/          # class ánh xạ bảng
├── Data/              # DbContext, Migrations/
├── Dtos/              # dữ liệu vào/ra API
├── Services/          # logic nghiệp vụ
├── Messaging/         # Kafka: publisher + consumer của service
├── Program.cs
├── appsettings.json
└── Dockerfile
```
Chỉ tạo thư mục mà service thực sự cần.

### Frontend (Angular 12, NgModule + lazy loading)
```
frontend/chat-app/
├── proxy.conf.js                   # chuyển /api và /hubs (ws: true) tới Gateway :5000, tránh CORS; .js để bắt lỗi socket WebSocket (không làm sập ng serve)
└── src/
    ├── environments/               # apiUrl, hubUrl
    └── app/
        ├── app.module.ts
        ├── app-routing.module.ts   # khai báo lazy loading
        ├── core/                   # import 1 lần ở AppModule, chứa singleton
        │   ├── core.module.ts
        │   ├── models/             # user, group, message, unread-counter, friend
        │   ├── services/
        │   │   ├── auth.service.ts             # login, register, logout, user hiện tại
        │   │   ├── token-storage.service.ts    # lưu/đọc JWT
        │   │   ├── group-api.service.ts        # REST /api/groups
        │   │   ├── friend-api.service.ts       # REST /api/friends (Phần 11)
        │   │   ├── message-api.service.ts      # REST lịch sử tin nhắn
        │   │   ├── notification-api.service.ts # REST unread, mark read
        │   │   ├── chat-hub.service.ts         # SignalR /hubs/chat
        │   │   ├── notification-hub.service.ts # SignalR /hubs/notifications
        │   │   └── theme.service.ts            # theme Sáng/Tối/Hệ thống (body.dark), lưu localStorage, mặc định Hệ thống
        │   ├── interceptors/
        │   │   ├── jwt.interceptor.ts          # gắn Authorization: Bearer
        │   │   └── error.interceptor.ts        # 401 → logout; lỗi khác (403, 409...) ném về nơi gọi tự hiển thị
        │   ├── guards/
        │   │   ├── auth.guard.ts               # CanLoad + CanActivate: phải đăng nhập
        │   │   └── guest.guard.ts              # đã đăng nhập thì không vào /auth
        │   └── utils/
        │       ├── guid.ts                     # uuidv7() – messageId sinh ở client
        │       └── hub-connection.ts           # cấu hình chung của 2 hub (skipNegotiation, token, reconnect)
        ├── shared/                 # dùng lại ở nhiều feature, KHÔNG chứa service singleton
        │   ├── shared.module.ts
        │   ├── components/         # avatar, unread-badge, connection-banner, confirm-dialog, app-rail (rail điều hướng 72px: Chat, Danh bạ, Cài đặt)
        │   └── pipes/              # list-time (giờ góc phải danh sách chat: "14:27", "Hôm qua", "T4", "05/09")
        └── features/
            ├── auth/               # lazy: /auth
            │   ├── auth.module.ts
            │   ├── auth-routing.module.ts
            │   └── pages/ login/, register/
            ├── chat/               # lazy: /chat
            │   ├── chat.module.ts
            │   ├── chat-routing.module.ts
            │   ├── pages/chat-layout/          # khung: rail + danh sách chat + <router-outlet>
            │   └── components/
            │       ├── group-list/             # danh sách chat: tìm kiếm, tab Tất cả/Chưa đọc/Nhóm, chấm đỏ unread + online
            │       ├── chat-window/            # route con /chat/:groupId
            │       ├── message-list/           # hiển thị, sắp theo sequenceNumber, cuộn tải thêm, mốc ngày, giờ cuối cụm
            │       ├── message-input/          # ô nhập, sinh messageId
            │       ├── member-panel/           # thành viên, thêm/xóa
            │       ├── create-group-dialog/
            │       └── edit-group-dialog/      # gửi kèm version, xử lý 409
            ├── contacts/           # lazy: /contacts (Phần 11)
            │   ├── contacts.module.ts
            │   ├── contacts-routing.module.ts
            │   └── pages/contacts-page/        # tab Bạn bè / Lời mời / Đã gửi + ô tìm người để mời; Nhắn tin, Hủy kết bạn (confirm-dialog)
            └── settings/           # lazy: /settings (Bước 9b)
                ├── settings.module.ts
                ├── settings-routing.module.ts
                └── pages/settings-page/        # thẻ Hồ sơ (chỉ xem, Đăng xuất) + thẻ Giao diện (Sáng/Tối/Hệ thống)
```

Routes:
| Đường dẫn | Module/Component | Guard |
|---|---|---|
| `''` | redirect `/chat` | – |
| `/auth/login`, `/auth/register` | AuthModule (lazy) | GuestGuard |
| `/chat` | ChatModule (lazy) → ChatLayout | AuthGuard (canLoad, canActivate) |
| `/chat/:groupId` | ChatWindow (route con) | – |
| `/contacts` | ContactsModule (lazy) → ContactsPage | AuthGuard (canLoad, canActivate) |
| `/settings` | SettingsModule (lazy) → SettingsPage | AuthGuard (canLoad, canActivate) |

Quy tắc frontend:
- Hub service nằm trong `core/` (`providedIn: 'root'`) để cả app chỉ có MỘT kết nối mỗi hub. Không provide lại trong module lazy.
- Kết nối hub khi đăng nhập, `stop()` khi đăng xuất; truyền JWT bằng `accessTokenFactory`; bật `withAutomaticReconnect()`.
- Kết nối hub dùng `skipNegotiation: true` + `transport: HttpTransportType.WebSockets` (Phần 8): chat-service có 2 bản sau Nginx round-robin, nếu để negotiate thì request negotiate và request WebSocket có thể rơi vào 2 bản khác nhau → 404. Bỏ negotiate → chỉ một request, không cần sticky session.
- Sau khi reconnect, tải lại lịch sử (REST) để lấy bù tin bị lỡ lúc mất kết nối (SignalR không gửi bù).
- Sau khi reconnect, ConnectionId mới nên phải gọi lại `JoinGroup` cho nhóm đang mở (`onreconnected`).
- Token hết hạn hoặc bị từ chối khi kết nối / kết nối lại hub → đăng xuất, về `/auth/login`, KHÔNG thử lại. Trình duyệt không đọc được mã 401 của bước bắt tay WebSocket nên phát hiện 2 lớp: `accessTokenFactory` kiểm `expiresAt` trước mỗi lượt kết nối; kết nối hỏng thì hỏi lại `GET /api/users/me` (401 → `ErrorInterceptor` đăng xuất; 200/5xx → lỗi tạm thời, tiếp tục thử lại như thường).
- Component không gọi HttpClient trực tiếp, chỉ gọi qua service trong `core/services`.
- Tin cuối ở danh sách nhóm: chat-service không có API "tin cuối" → gọi lịch sử `limit=1` cho từng nhóm (song song, lỗi từng nhóm không làm hỏng danh sách); nhóm đang mở cập nhật từ `ReceiveMessage`; nhóm khác có `UnreadCountChanged` tăng → gọi lại `limit=1` cho nhóm đó.
- Số chưa đọc: `GET /unread` khi mở app và sau mỗi lần (re)connect hub; `UnreadCountChanged` cập nhật ngay; `UnreadCountChanged` của nhóm chưa có trong danh sách (vừa được thêm vào nhóm) → nạp lại danh sách. Nhóm đang mở tự `POST /read` với seq lớn nhất đang hiện, gom lô 500 ms.
- Sửa / xóa nhóm, xóa thành viên thành công → `GroupApiService.changed$` → thanh bên nạp lại (route con và route cha không gọi thẳng nhau).
- Bố cục bên trong (Bước 9b, chi tiết ở `docs/UI.md`): rail `app-rail` (component dùng chung, đặt trong `chat-layout` và `settings-page`, không đổi cấu trúc route); đăng xuất nằm ở thẻ Hồ sơ trang `/settings`. Tab danh sách chat lọc phía client (Chưa đọc = `unread > 0`; Nhóm = `!isDirect`, Phần 11).
- Chat riêng (Phần 11): dòng danh sách và tiêu đề khung chat hiện tên/avatar của `peer`; ẩn sửa nhóm, thêm/xóa thành viên, xóa/rời nhóm. Chấp nhận lời mời xong → nạp lại `GET /api/groups` mỗi 1 s, tối đa ~5 lần, tới khi có `directGroupId` (trong lúc chờ: "Đang tạo cuộc trò chuyện…"). Người gửi lời mời thấy chat riêng khi về `/chat` / mở Danh bạ, hoặc khi có tin đầu tiên (`UnreadCountChanged` nhóm lạ → nạp lại). Đang mở chat riêng mà bị hủy kết bạn → màn 403 ghi "Hai bạn không còn là bạn bè" (phát hiện khi gửi tin bị server từ chối → hỏi lại chi tiết nhóm → 403; `GroupApiService` nhớ trong bộ nhớ các mã chat riêng đã gặp để phân biệt với nhóm thường – F5 thì mất, hiện câu chung).
- Theme: `ThemeService` lưu `localStorage['chatapp.theme']` = `light` | `dark` | `system` (mặc định `system`, chỉ khi đó mới nghe `prefers-color-scheme`). Là tiện ích riêng của trình duyệt, không lưu server.

## 3. Database

### identity_db
**users**: Id (uuid v7, PK), UserName (varchar 50, unique), Email (varchar 100, unique), PasswordHash (text, BCrypt), DisplayName (varchar 100), CreatedAt (timestamptz).
Từ công việc 3: SystemRole (User/SystemAdmin), IsLocked, AuthVersion (bigint), LockedAt/LockedBy/LockReason, Version (xmin). account_audits lưu ActorId/TargetId/Action/Reason/CreatedAt. Xem mục 10 và docs/ADMIN.md.
Tự viết bảng + BCrypt, không dùng ASP.NET Core Identity.

**friendships** (Phần 11): UserLowId, UserHighId (PK kép), RequesterId, Status (varchar 20, lưu chữ: `Pending`/`Accepted`/`Declined`/`Cancelled`/`Removed`), Revision (int, mặc định 0), CreatedAt, UpdatedAt, RespondedAt (null), Version (`[Timestamp] uint` → xmin, Optimistic Locking).
- Revision (Bước 4b): số phiên bản của quan hệ, tăng 1 ở MỖI lần đổi trạng thái (tạo lời mời đầu = 1), trong cùng `SaveChangesAsync` có xmin → 2 lần đổi tranh nhau không thể ra cùng một số. Gửi kèm trong `identity.friendship-changed` để bên nghe bỏ sự kiện cũ. Dùng số do identity cấp thay cho `occurredAt` vì đồng hồ giữa các bản identity có thể lệch.
- MỘT dòng cho mỗi cặp: cặp userId được sắp `low < high` (so chuỗi GUID chữ thường kiểu ordinal = đúng thứ tự byte của `uuid` PostgreSQL) → A mời B và B mời A rơi vào cùng một dòng, PK chặn 2 lời mời chéo nhau.
- Ràng buộc: `CHECK (user_low_id < user_high_id)` (không cặp ngược, không tự kết bạn), `CHECK (requester_id IN (user_low_id, user_high_id))`. FK 3 cột → `users` (cùng database nên được phép). Index `user_high_id` (cột low đã có PK).
- Máy trạng thái: `Pending → Accepted | Declined | Cancelled`; `Accepted → Removed`; `Declined | Cancelled | Removed → Pending` (mời lại, dùng lại dòng, `RequesterId` = người mời lần này). Mọi chuyển trạng thái đọc dòng rồi `SaveChangesAsync` kèm xmin → 2 thao tác tranh nhau thì bên sau 409.

### group_db
**groups**: Id (uuid PK), Name (varchar 100), Description (varchar 500, null), OwnerId (uuid, không FK), IsDirect (bool, mặc định false – Phần 11), FriendshipRevision (int, null – chỉ dùng cho nhóm `IsDirect`: revision của sự kiện bạn bè đã áp gần nhất, Bước 4b), CreatedAt, UpdatedAt, Version (`[Timestamp] uint` → cột hệ thống xmin, dùng Optimistic Locking).
- Nhóm chat riêng (`IsDirect = true`, Phần 11): `Id = DirectChat.GroupIdFor(a, b)` (UUID v5 của `"direct:{low}:{high}"`, cùng cặp luôn ra cùng mã), `Name = ""` (giao diện hiện tên người kia), `OwnerId = user_low_id` (chỉ để lấp cột bắt buộc, KHÔNG mang quyền Owner), 2 thành viên đều Role `Member`. Hủy kết bạn: giữ nhóm và tin nhắn, chỉ gỡ 2 thành viên; kết bạn lại → thêm lại 2 thành viên, lịch sử cũ hiện lại vì mã nhóm không đổi.
**group_members**: GroupId (FK groups), UserId, Role (`Owner`/`Admin`/`Member`), JoinedAt. PK (GroupId, UserId). Unique index một Owner/group.
Từ công việc 3: groups thêm RequiresApproval và InviteTokenHash; group_join_requests lưu yêu cầu Pending/Approved/Rejected/Cancelled, group_audits ghi thao tác quản lý. Mục 10 mô tả quy tắc nhóm thường; chat riêng vẫn hai Member.
**user_snapshots**: UserId (PK), DisplayName, UserName. Bản sao từ sự kiện `identity.user-registered`.

### chat_db (PostgreSQL legacy, chỉ đối chiếu/rollback)
Từ công việc 2, API không đăng ký ChatDbContext và không ghi vào database này. Mô tả dưới đây là schema trước chuyển đổi. Runtime Mongo xem mục 9.
**messages**: Id (uuid, DO CLIENT SINH → chống trùng), GroupId, SenderId, SenderName, Content (text), SequenceNumber (bigint, từ Redis INCR, dùng để sắp xếp), CreatedAt (chỉ hiển thị, không dùng sắp xếp).
Unique index (GroupId, SequenceNumber).

Redis của chat-service:
- `chat:seq:{groupId}` là key legacy, không còn được runtime đọc/ghi từ công việc 2. Bộ đếm hiện nằm trong Mongo conversation_state và cùng transaction với tin.
- Presence do Presence Service quản lý, thay set `presence:{userId}` cũ (key cũ không còn được đọc): `presence:v2:sessions:{userId}` là sorted set ConnectionId → thời điểm hết hạn; `presence:v2:due` lập lịch quét; `presence:v2:online` chống thông báo chuyển trạng thái trùng. Xem mục 8.
- `group:members:{groupId}` là cache legacy của Phần 7; từ công việc 3 không dùng làm nguồn quyền gửi/vào phòng/history. Consumer vẫn xóa key cũ và phát tín hiệu đồng bộ phòng.
- Địa chỉ Redis: `ConnectionStrings:Redis` trong `appsettings.json` (Redis không đặt mật khẩu nên không phải secret).
- Kênh Pub/Sub `chatapp-chat:*` (Phần 8): SignalR Redis Backplane, để `Clients.Group(...)` ở một bản tới được kết nối ở mọi bản. Bật/tắt bằng `SignalR:RedisBackplane` (mặc định `true`; Docker: biến `CHAT_BACKPLANE`, chỉ tắt khi demo).

### notification_db
**unread_counters**: UserId, GroupId (PK kép), UnreadCount (int), LastReadSequence (bigint), UpdatedAt.
**group_member_snapshots**: GroupId, UserId (PK kép), IsMember (bool), LastEventAt (timestamptz), LastEventId (uuid). Bản sao từ sự kiện group.member-*.
- member-added và member-removed là 2 topic → có thể đến sai thứ tự. Quy tắc áp: **tombstone + chỉ áp sự kiện mới hơn**. Xóa = `IsMember = false` (không xóa dòng); sự kiện chỉ ghi đè khi `(occurredAt, eventId)` lớn hơn `(LastEventAt, LastEventId)` đang lưu (một câu `INSERT ... ON CONFLICT DO UPDATE ... WHERE`). So `occurredAt` được vì mọi sự kiện thành viên đều do group-service tạo.
- Chỉ dòng `IsMember = true` mới là thành viên (đếm unread, kiểm tra quyền mark read).

**processed_events**: EventId (PK), ProcessedAt. Idempotent Consumer (Kafka at-least-once).
Công tắc `Notification:IdempotentConsumer` (mặc định `true`) chỉ để demo: tắt thì không ghi/kiểm tra `processed_events` → sự kiện trùng bị đếm 2 lần.

### outbox_messages (có trong identity_db, group_db, chat_db: mỗi service phát sự kiện giữ outbox riêng trong DB của mình)
**outbox_messages**: Id (uuid PK, = `eventId` của sự kiện), Topic (varchar 200), Key (varchar 200), EventType (varchar 100), Payload (jsonb), OccurredAt (timestamptz), ProcessedAt (timestamptz, null = chưa gửi), Attempts (int), LastError (text, null).
Index một phần trên (OccurredAt, Id) `WHERE processed_at IS NULL` để tìm nhanh các dòng chưa gửi.
Cấu hình bảng viết một lần trong `ChatApp.Common/Outbox`, mỗi DbContext gọi `modelBuilder.AddOutboxMessages()`.

## 4. Kafka

**Phát sự kiện (Transactional Outbox):**
1. Service nghiệp vụ thêm dòng `outbox_messages` vào DbContext, rồi gọi MỘT `SaveChangesAsync` → dữ liệu và event cùng commit hoặc cùng hủy.
2. `OutboxPublisher<TDbContext>` (BackgroundService) cứ ~1 giây đọc tối đa 100 dòng chưa gửi, theo thứ tự (OccurredAt, Id), bằng `SELECT ... FOR UPDATE SKIP LOCKED` (để khi chạy 2 bản service, mỗi dòng chỉ một bản xử lý).
3. Gửi Kafka thành công → đặt `ProcessedAt`. Gửi lỗi → tăng `Attempts`, ghi `LastError`, dừng lô này (giữ thứ tự), thử lại ở vòng sau.
4. Gửi xong nhưng sập trước khi ghi `ProcessedAt` → lần sau gửi lại → event có thể TRÙNG (at-least-once). Bên nghe phải idempotent.

Mọi sự kiện có 3 field chung: `eventId` (GUID của sự kiện), `eventType`, `occurredAt`.

| Topic | Bên phát | Bên nghe | Key | Partition |
|---|---|---|---|---|
| identity.user-registered | identity | group | userId | 1 |
| identity.account-state-changed | identity | sự kiện quan sát; thu hồi quyền dùng nguồn gRPC/monitor độc lập | userId | 1 |
| identity.friendship-changed | identity | group | `{low}:{high}` | 1 |
| group.member-added | group | chat, notification | groupId | 1 |
| group.member-removed | group | chat, notification | groupId | 1 |
| chat.message-sent | chat | notification | groupId | 3 |

Consumer group đặt trùng tên service (group-service, chat-service, notification-service), cấu hình ở `Kafka:ConsumerGroupId`.

Topic được tạo bởi container chạy một lần `kafka-init` trong docker-compose (`--if-not-exists`, đúng số partition ở bảng trên). Kafka tắt tự tạo topic (`KAFKA_AUTO_CREATE_TOPICS_ENABLE=false`).

Payload:
- `identity.user-registered`: userId, userName, displayName
- `identity.friendship-changed`: userLowId, userHighId, actorId (người bấm), change (`Accepted` | `Removed`), revision (`friendships.revision` sau lần đổi này); `eventType` = giá trị `change`. Chấp nhận và hủy kết bạn chung MỘT topic, key theo cặp → sự kiện của cùng một cặp luôn được xử lý đúng thứ tự (2 topic riêng thì Kafka không bảo đảm thứ tự giữa chúng). Cần field `change` riêng vì `eventType` của lớp gốc chỉ là getter, không đọc lại được khi giải mã JSON.
- `group.member-added`: groupId, userId, role
- `group.member-removed`: groupId, userId
- `chat.message-sent`: messageId, groupId, senderId, senderName, sequenceNumber, contentPreview (50 ký tự đầu)

## 5. API

### Định tuyến Gateway
| Tiền tố | Đến |
|---|---|
| /api/auth/*, /api/users/*, /api/friends/* | identity-service |
| /api/admin/users/* | identity-service (SystemAdmin) |
| /api/admin/groups/* | group-service (SystemAdmin) |
| /api/groups/* | group-service |
| /api/chat/*, /hubs/chat | chat-service |
| /api/notifications/*, /hubs/notifications | notification-service |

### identity-service
- POST /api/auth/register (công khai): userName, email, password, displayName → phát identity.user-registered
- POST /api/auth/login (công khai): userName, password → accessToken (JWT)
- GET /api/users/me (đã đăng nhập)
JWT claims: `sub` (UserId), `name` (UserName), `display_name`, `system_role`, `auth_version`. Role hệ thống và trạng thái được kiểm lại từ Identity khi xử lý request (mục 10).

Bạn bè (Phần 11, đã đăng nhập; lời mời gọi theo userId của người kia vì cặp userId là định danh của quan hệ):
- GET /api/friends – bạn bè: userId, userName, displayName, since, directGroupId (identity tự tính bằng `DirectChat.GroupIdFor`)
- GET /api/friends/requests/incoming, GET /api/friends/requests/outgoing – lời mời đến / đã gửi (đang Pending)
- POST /api/friends/requests – body: userId → 201; mời chính mình 400; user không tồn tại 404; đã là bạn / đã mời 409; người kia đã mời mình 409 ("Người này đã mời bạn"). Mời lại sau Declined/Cancelled/Removed được.
- POST /api/friends/requests/{userId}/accept → 200, phát friendship-changed `Accepted`; không có lời mời 404; mình là người gửi 403; đã Accepted → 200, không phát thêm; tranh chấp 409
- POST /api/friends/requests/{userId}/decline → 204
- DELETE /api/friends/requests/{userId} – hủy lời mời mình đã gửi → 204
- DELETE /api/friends/{userId} – hủy kết bạn → 204, phát friendship-changed `Removed`; không phải bạn 404; tranh chấp 409 (xmin)

### group-service
- POST /api/groups – tạo nhóm, người tạo là Owner (phát member-added cho Owner)
- GET /api/groups – nhóm mình tham gia
- GET /api/groups/{groupId} – chi tiết + thành viên (chỉ thành viên)
- PUT /api/groups/{groupId} – Owner sửa tên/mô tả, body kèm `version`; version cũ → 409 Conflict
- DELETE /api/groups/{groupId} – Owner
- GET /api/groups/users/search?q= – tìm trong user_snapshots
- POST /api/groups/{groupId}/members – Owner/Admin thêm trực tiếp, Member thêm tùy approval; body userId, 201 Member hoặc 202 Pending. Chỉ membership thật mới phát member-added.
- DELETE /api/groups/{groupId}/members/{userId} – Owner, Admin xóa Member hoặc chính người đó; Owner cần chuyển quyền trước khi rời → phát member-removed
- `GroupDto` có `isDirect` và `peer` (userId, userName, displayName của người kia, từ `user_snapshots`; chỉ có khi `isDirect`).
- Nhóm riêng: PUT, DELETE nhóm, thêm / xóa thành viên → 400 "Không áp dụng cho cuộc trò chuyện riêng". Phép chặn này chạy TRƯỚC mọi kiểm tra `OwnerId` (người `user_low_id` không phải chủ nhóm riêng).

Consumer `identity.friendship-changed` (idempotent, 1 transaction):
- Kiểm tra phiên bản trước (Bước 4b): nhóm chưa có → tạo nhóm với `friendship_revision = event.revision`; nhóm đã có → một câu `UPDATE groups SET friendship_revision = event.revision WHERE id = … AND (friendship_revision IS NULL OR friendship_revision < event.revision)`. 0 dòng (`event.revision <= friendship_revision`) → bỏ qua, ghi log "bỏ qua sự kiện cũ", không đụng thành viên, không phát gì. Sự kiện `revision = 0` (phát trước Bước 4b, chưa có trường này) áp như cũ, không so phiên bản.
- `Accepted`: `INSERT group_members ... ON CONFLICT DO NOTHING` cho 2 người; chỉ phát member-added cho dòng thật sự chèn được.
- `Removed`: xóa 2 dòng group_members nếu còn; chỉ phát member-removed cho dòng thật sự xóa được. chat-service, notification-service xử lý như gỡ thành viên bình thường (không sửa code).

gRPC (nội bộ, port 5012):
```protobuf
syntax = "proto3";
service GroupMembership {
  rpc CheckMembership (CheckMembershipRequest) returns (CheckMembershipReply);
  rpc GetMemberIds (GetMemberIdsRequest) returns (GetMemberIdsReply);
}
message CheckMembershipRequest { string group_id = 1; string user_id = 2; }
message CheckMembershipReply { bool is_member = 1; }
message GetMemberIdsRequest { string group_id = 1; }
message GetMemberIdsReply { repeated string user_ids = 1; }
```

### chat-service
- GET /api/chat/groups/{groupId}/messages?beforeSeq=&limit=50&weekStart= – Mongo history, keyset theo sequenceNumber, limit 1..100, response cũ → mới. weekStart tùy chọn là thứ Hai yyyy-MM-dd theo lịch Việt Nam; chỉ thành viên (403), Mongo gián đoạn 503.

SignalR Hub `/hubs/chat` (JWT truyền qua query `access_token`):
| Hướng | Method | Tham số | Trả về |
|---|---|---|---|
| Client → Server | JoinGroup | groupId | – (chỉ đăng ký nhận tin) |
| Client → Server | LeaveGroup | groupId | – |
| Client → Server | SendMessage | messageId, groupId, content | message đã lưu (gửi trùng messageId → tin cũ) |
| Server → Client | ReceiveMessage | message (có sequenceNumber) | |
| Client → Server | Heartbeat | – | gia hạn lease và đẩy snapshot theo quyền |
| Client → Server | EndSession | – | thu hồi phiên hiện tại rồi đóng kết nối |
| Client → Server | SetTyping | groupId, isTyping | kiểm tra thành viên từ nguồn gRPC |
| Server → Client | PresenceSnapshot | known, version, heartbeatSeconds, leaseSeconds, onlineUserIds, friendIds, onlineCountsByGroup | |
| Server → Client | TypingChanged | groupId, userId, displayName, isTyping | |
| Server → Client | GroupAccessRevoked | groupId | |

PresenceSnapshot được gửi tới từng connection, độc lập với phòng đang mở. `version` chỉ tăng trong vòng đời connection, client đặt lại khi reconnect. `known=false` nghĩa là chưa xác định, không phải tất cả offline.

### notification-service
- GET /api/notifications/unread
- POST /api/notifications/groups/{groupId}/read – body: lastReadSequence

SignalR Hub `/hubs/notifications`:
| Hướng | Method | Tham số |
|---|---|---|
| Server → Client | UnreadCountChanged | groupId, unreadCount |

## 6. Luồng gửi tin nhắn
1. Angular sinh messageId, gọi SendMessage qua SignalR (qua Nginx tới 1 bản chat-service).
2. Kiểm tra tài khoản hiện tại qua Identity và thành viên hiện tại qua Group gRPC (công việc 3); không xác nhận được quyền thì tạm từ chối.
3. messageId đã tồn tại → bỏ qua (idempotency).
4. Trong transaction Mongo: giữ message_keys, tăng conversation_state.lastSeq → sequenceNumber.
5. Ghi messages + outbox_messages (chat.message-sent, key groupId), commit cùng khóa và bộ đếm. Shard key messages/outbox là groupId hashed; keys/state là _id hashed.
6. Gửi ReceiveMessage tới phòng SignalR của nhóm (Redis Backplane để mọi bản đều phát).
7. MongoOutboxPublisher đẩy chat.message-sent theo thứ tự từng conversation, lease/version/generation chống worker cũ cập nhật sau takeover. Chờ ACK trước khi đánh dấu; vẫn at-least-once.
8. notification-service: kiểm tra processed_events → tăng UnreadCount (trừ người gửi) → UnreadCountChanged.

## 7. Công nghệ theo chương
| Chương | Công nghệ |
|---|---|
| 1. Mở đầu | draw.io, Mermaid/PlantUML |
| 2. Kiến trúc | Microservices, Event-driven, YARP, Database per service, eventual consistency |
| 3. Tiến trình & luồng | async/await, BackgroundService (Kafka consumer), Hangfire, Docker Compose |
| 4. Trao đổi thông tin | REST, gRPC, Kafka, Redis cache, SignalR + Redis Backplane |
| 5. Định danh | GUID v7, JWT, SignalR ConnectionId, Docker DNS, URI, ID tất định UUID v5 (nhóm chat riêng) |
| 6. Đồng bộ hóa | Mongo transaction/counter/unique index và lease fencing cho Chat; Redis TIME/Lua cho Presence; Kafka partition key, idempotency, Optimistic Locking (xmin), SQL Outbox (`FOR UPDATE SKIP LOCKED`) ở service khác |
| 7. Sao lưu | PostgreSQL streaming replication, pg_dump + Hangfire, Redis AOF, Kafka retention/replay |
| 8. Chịu lỗi | Polly, Nginx load balancing, Health Checks, Serilog + Seq, SignalR auto reconnect, consumer group |

## 8. Bổ sung đã duyệt: Presence Service và đang gõ (27/10/2026)

- Presence Service riêng, gRPC nội bộ 5015; Docker chỉ publish health `127.0.0.1:5005/health`. Không thêm DB SQL. Identity thêm gRPC 5011 `FriendPresence.GetFriends`; Group thêm `GetPresenceAudience` (một truy vấn cho mọi nhóm user đang tham gia). Đây là RPC nội bộ theo cùng quy ước mạng tin cậy của Group gRPC, không có route public qua Gateway.
- ChatHub xác thực JWT, lấy userId từ claims và connectionId từ Context; client không được chọn danh tính để gia hạn. Angular khởi tạo hub ở AppComponent để vào thẳng Danh bạ/Cài đặt vẫn online. Đóng hub khi JWT hết hạn.
- Heartbeat khoảng 10s; lease mỗi ConnectionId 60s; sweeper 2s, mỗi lượt tối đa 200 user đến hạn. Cấu hình `Presence:HeartbeatSeconds/LeaseSeconds/SweepSeconds` ở Presence Service; Compose cho ghi đè hai giá trị đầu qua `PRESENCE_HEARTBEAT_SECONDS/PRESENCE_LEASE_SECONDS`. Các mốc tính từ heartbeat cuối server nhận, có độ trễ quét/truyền; không phải timeout không thao tác bàn phím.
- Lua dùng Redis TIME, xóa phiên hết hạn + cập nhật phiên + chuyển trạng thái + PUBLISH nguyên tử. `GetOnline` kiểm tra deadline thật nên không phụ thuộc sweeper đã chạy hay chưa. Key session có TTL dọn rác (gấp đôi lease); index due vẫn cho phép xử lý chuyển offline sau khi key hết TTL. Một user online khi còn ít nhất một lease hợp lệ trên bất kỳ replica/thiết bị nào.
- Mất transport/đóng tab giữ lease đến hết hạn để reconnect không nhấp nháy offline. Logout gọi EndSession cho đúng connection hiện tại; thiết bị khác còn phiên vẫn online. Khi trình duyệt bị treo/ngủ quá lâu, lease có thể hết hạn; về tab/kết nối lại gửi heartbeat ngay.
- Kênh `chatapp:presence:changed` và `chatapp:membership:changed` chỉ báo cần đồng bộ. Chat replica lấy snapshot mới từ nguồn, gửi chỉ đến connection cục bộ qua IHubContext; không broadcast toàn phòng từ mọi replica. Semaphore từng connection giữ thứ tự, phiên bản snapshot tăng theo connection. Pub/Sub mất tín hiệu được sửa ở heartbeat kế tiếp; không dùng Redis keyspace notifications làm nguồn duy nhất.
- Mỗi heartbeat đọc quyền hiện tại từ Identity/Group theo lô. Snapshot chỉ gồm bạn bè Accepted, thành viên các nhóm user được xem và chính user. `onlineCountsByGroup` chỉ đếm thành viên KHÁC người xem. Client không tự đưa danh sách userId bất kỳ để tra trạng thái.
- Consumer thành viên xóa cache rồi publish tín hiệu cho mọi Chat replica. Mỗi replica đọc lại trạng thái Group hiện tại, gỡ connection khỏi phòng đã mất quyền và gửi GroupAccessRevoked; không áp mù sự kiện member-removed có thể cũ. Heartbeat là đường sửa sai nếu lỡ Pub/Sub. Quyền vẫn có độ trễ eventual consistency; chưa có bảo đảm thu hồi đồng bộ ngay tại thời điểm transaction Group commit.
- Khi Presence/nguồn audience lỗi: snapshot `known=false`, không kết luận offline; JoinGroup và SendMessage không chờ Presence. Khi client mất kết nối hoặc snapshot quá lease không được làm mới, xóa chấm xanh/hiện chưa xác định. Presence chưa có dự phòng Redis độc lập; không bảo đảm độ sẵn sàng production.
- Angular: mục Bạn bè đang hoạt động ở thanh bên (cuộn ngang, đủ mọi bạn bè online), chấm xanh Danh bạ/chat riêng, tab Danh bạ Đang hoạt động; mọi nhóm có số thành viên khác online và tab lọc Hoạt động. Nhóm sáng khi >=1 người khác online ở ứng dụng, không có nghĩa đang mở/đã đọc nhóm đó.
- Đang gõ: SetTyping qua SignalR, phải đang ở phòng và kiểm tra thành viên từ gRPC; không truyền nội dung bản nháp. Client throttle 2s, server hạn chế tín hiệu đang gõ 1,5s; ngừng nhập 2,5s/blur/gửi/đổi phòng phát dừng, phía nhận tự hết hạn 5s. Không lưu DB/Kafka.
- Sau công việc 2 (mục 9), tin và Outbox của Chat nằm trong MongoDB; Kafka và REST lấy bù giữ nguyên vai trò. Kafka luôn nhận sự kiện dù người nhận online/offline; Presence không quyết định tin có được lưu/phát sự kiện.
- Phạm vi đồ án: đọc snapshot theo lô và coalescing tín hiệu, giới hạn RPC tra trạng thái 20.000 user/lượt; chưa tối ưu phân trang audience/hệ thống presence quy mô lớn. Quản trị người dùng thuộc công việc 3.

## 9. Công việc 2 – MongoDB, hash sharding, lịch sử tuần (đã duyệt 27/10/2026)

- Docker Compose include `docker-compose.mongo.yml`: MongoDB 8.0.34 gồm config replica set một node, hai shard replica set mỗi bộ một node, mongos và mongo-init idempotent. Replica set một node hỗ trợ transaction nhưng không có node dự phòng. Không publish cổng Mongo ra host; thao tác quản trị/test qua `docker exec mongo-router mongosh`.
- `chat_history.messages`: `_id` là messageId GUID chuẩn dạng chuỗi; groupId/senderId cùng dạng; senderName/content; sequenceNumber Int64; createdAt BSON Date UTC; createdAtTicks Int64 giữ chính xác timestamp gốc; weekStartUtc BSON Date là thứ Hai 00:00 +07 quy ra UTC.
- Shard key messages/outbox `{groupId:'hashed'}`: Mongo tự băm, tất cả tin cùng conversation có cùng giá trị khóa. Phân phối nhiều conversation qua hai shard, không cam kết chia đôi số message hay chia một nhóm nóng qua nhiều shard.
- Index messages `{groupId:1,sequenceNumber:-1}` unique (index thường riêng, không unique hashed); `{groupId:1,weekStartUtc:1,sequenceNumber:-1}` phục vụ tuần. Query tuần equality group/week + range beforeSeq + sort seq dùng index và target một shard.
- `message_keys` shard `{_id:'hashed'}`: _id=messageId, groupId, senderId. Proxy khóa toàn cục giúp chặn cùng ID ở hai conversation/shard khác nhau; insert trong cùng transaction với message. _id của messages chia theo group không tự cung cấp uniqueness toàn cluster.
- `conversation_state` shard `{_id:'hashed'}`: _id=groupId; lastSeq, hasPending, leaseUntil/leaseOwner/leaseVersion/leaseGeneration. Cấp lastSeq trong transaction; writer cùng conversation được serialize bằng write conflict/retry. Mất state → MAX từ messages; state lùi → unique index chặn, nâng floor bằng $max rồi thử lại. Redis tiếp tục dùng cho Presence/cache/backplane.
- `outbox_messages` shard groupId hashed: _id=eventId; messageId/groupId/sequenceNumber/topic/payload/occurredAt/processedAt/attempts/lastError. MessageSent contract giữ nguyên, payload EventId không đổi khi retry. Worker nhận lease theo conversation bằng giờ Mongo $$NOW; ACK Mongo chỉ cập nhật trong transaction có kiểm tra owner/version/generation và lease còn sống. Không advance khi gửi/ACK lỗi; callback retry không gọi Kafka. hasPending clear trong transaction chạm cùng state với writer để tránh bỏ sót tin mới. Kafka vẫn at-least-once; consumer dedup EventId.
- API lịch sử thêm `weekStart=yyyy-MM-dd` tùy chọn; phải là thứ Hai theo lịch Việt Nam. Default trả tin mới nhất như cũ; limit 1..100, beforeSeq keyset, response DTO giữ nguyên. Mongo gián đoạn trả 503; gửi không được xác nhận thì client retry cùng messageId.
- Angular 12: chọn ngày trong tuần → chuẩn hóa thứ Hai, Xem tuần, Về tin mới nhất. Tuần cũ là chế độ đọc, ẩn composer; không chèn tin ngoài tuần, không tự markRead tin mới, badge unread vẫn hiện ở cuộc trò chuyện đang mở. Reconnect giữ filter và nạp lại tuần. Trở về hiện tại tải trang mới nhất và markRead bình thường.
- Migration chỉ khi `--migrate-chat`: Npgsql đọc source trong repeatable-read/read-only; batch transaction Mongo cho keys/messages/outbox; ID/content/seq/timestamp và EventId/processed state giữ nguyên. Không tạo lại event cho lịch sử không có Outbox. Đối chiếu từng tin và SHA-256 trước marker storage-ready; API từ chối startup nếu marker chưa hoàn tất. --verify chỉ đối chiếu legacy subset, không xóa tin mới phát sinh trên Mongo.
- Script `scripts/migrate-chat-history.ps1 -Apply`: build image trước, dừng hai Chat, pg_dump backup, apply/verify rồi mở Mongo Chat. Không xóa PostgreSQL hay volume. Sau khi Mongo có tin mới, rollback SQL phải chuyển ngược phần phát sinh, không chỉ đổi connection string.

## 10. Công việc 3 – Admin, vai trò nhóm và phê duyệt (đã duyệt 27/10/2026)

- Identity sở hữu SystemRole (User/SystemAdmin), IsLocked, AuthVersion, LockedAt/By/Reason, audit. Seed một admin theo cấu hình AdminSeed, mật khẩu từ secret; idempotent, không reset mật khẩu hay nâng quyền tài khoản trùng tên có sẵn.
- Group sở hữu Owner/Admin/Member theo từng nhóm thường. Owner bổ nhiệm/miễn nhiệm Admin, chuyển Owner (Owner cũ thành Member), cấu hình phê duyệt, sửa/xóa nhóm. Owner/Admin duyệt và thêm người, Admin chỉ xóa Member; Admin/Member tự rời. Chat riêng không dùng các nghiệp vụ này.
- Member thêm người: phê duyệt tắt thì thêm ngay, bật thì Pending. Owner/Admin thêm trực tiếp. Người ngoài dùng link token ngẫu nhiên, cần đăng nhập; chỉ hiện giới thiệu tối thiểu. Lưu hash token, có thể rotate; Pending chưa là thành viên. Pending cũ vẫn chờ khi tắt phê duyệt.
- Transaction khóa dòng group trước đọc quyền và ghi thay đổi thành viên/role/request; một pending/group/target và tối đa một Owner bằng unique index. Chuyển Owner cập nhật OwnerId và cả hai role nguyên tử. Đổi role không phát member-added. Membership thật thay đổi mới phát Outbox cũ.
- AuthVersion gắn JWT, legacy thiếu phiên bản = 0; khóa/mở khóa tăng phiên bản, token cũ không sống lại. API và hub kiểm trạng thái từ Identity (Identity đọc DB nội bộ, các service khác gRPC); quyền admin không tin frontend. Server theo dõi connection và kiểm tra theo lô khoảng 5s, thu hồi Chat/Notification trên mọi replica, dọn Presence. RPC xác thực lỗi thì fail closed/503, không diễn giải là tài khoản bị khóa.
- Quyền vào phòng/gửi/đọc lịch sử của Chat đọc Group gRPC hiện tại, thay cache quyền cũ. Group/Identity không xác nhận được quyền thì tạm từ chối; đây là thay đổi có chủ đích về availability so với Phần 7. Presence lỗi vẫn không chặn chat.
- MembershipRoomMonitor phía server kiểm phòng khoảng 5s cộng thời gian xử lý, không gia hạn lease; thu hồi phòng kể cả khi client ngừng heartbeat hoặc Kafka/PubSub gián đoạn.
- Admin Angular /admin/users và /admin/groups, phân trang/tìm kiếm/chi tiết/khóa-mở khóa; xem metadata/role nhóm thường, không cấp quyền chat. Backend admin routes riêng do đúng service xử lý. Không đọc chéo DB. Angular 12 NgModule/RxJS, không thêm package npm.
- Migration bổ sung, không reset dữ liệu. Kiểm thử API, race/role/revoke/two replicas/Kafka outage, UI và hồi quy 11b/11c. Chi tiết nghiệm thu sẽ ghi docs/ADMIN.md.
- Tài liệu vận hành/khái niệm/demo/kiểm thử: `docs/MONGODB.md`. Kiểm thử tổng hợp công việc 1+2: `scripts/test-chat-system.ps1`.
