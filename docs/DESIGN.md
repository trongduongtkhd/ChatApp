# Thiết kế hệ thống ChatApp

## 1. Tổng quan kiến trúc
- Backend .NET 10; frontend Angular 12; hạ tầng (PostgreSQL, Redis, Kafka, Nginx) chạy bằng Docker Compose.
- Microservices + Event-driven (Kafka). Mỗi service một database PostgreSQL riêng.
- Angular chỉ gọi qua **API Gateway (YARP)**. Gateway là cửa vào giữa client và hệ thống, KHÔNG dùng để các service gọi nhau.
- Service gọi nhau: **gRPC** (cần trả lời ngay) hoặc **Kafka** (thông báo, không chờ).
- Dữ liệu giữa các service nhất quán theo kiểu **eventual consistency**: service sở hữu dữ liệu phát sự kiện, service khác tự cập nhật bản sao.

```
Angular ──REST/SignalR──► Nginx ──► API Gateway (YARP)
                                     ├─► identity-service
                                     ├─► group-service
                                     ├─► chat-service (x2 bản)
                                     └─► notification-service
chat-service ──gRPC──► group-service
identity/group/chat ──Kafka──► group/chat/notification
SignalR Redis Backplane đồng bộ giữa các bản chat-service
```

## 2. Danh sách service

| Service | Project | Tên Docker | Database | Port local |
|---|---|---|---|---|
| API Gateway | ChatApp.Gateway | api-gateway | – | 5000 |
| Identity | ChatApp.IdentityService | identity-service | identity_db | 5001 |
| Group | ChatApp.GroupService | group-service | group_db | 5002 (REST), 5012 (gRPC, HTTP/2) |
| Chat | ChatApp.ChatService | chat-service | chat_db | 5003 |
| Notification | ChatApp.NotificationService | notification-service | notification_db | 5004 |

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
│   └── src/
│       ├── BuildingBlocks/
│       │   ├── ChatApp.Contracts/      # hợp đồng dùng chung
│       │   │   ├── Events/             # UserRegistered, MemberAdded, MemberRemoved, MessageSent
│       │   │   └── Protos/group_membership.proto
│       │   └── ChatApp.Common/         # code hạ tầng dùng chung
│       │       ├── Kafka/              # KafkaProducer, KafkaConsumerBase (BackgroundService)
│       │       ├── Auth/               # extension cấu hình JWT
│       │       └── Logging/            # extension cấu hình Serilog
│       ├── Gateway/ChatApp.Gateway/
│       └── Services/
│           ├── Identity/ChatApp.IdentityService/
│           ├── Group/ChatApp.GroupService/
│           ├── Chat/ChatApp.ChatService/
│           └── Notification/ChatApp.NotificationService/
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
├── proxy.conf.json                 # chuyển /api và /hubs (ws: true) tới Gateway :5000, tránh CORS
└── src/
    ├── environments/               # apiUrl, hubUrl
    └── app/
        ├── app.module.ts
        ├── app-routing.module.ts   # khai báo lazy loading
        ├── core/                   # import 1 lần ở AppModule, chứa singleton
        │   ├── core.module.ts
        │   ├── models/             # user, group, message, unread-counter
        │   ├── services/
        │   │   ├── auth.service.ts             # login, register, logout, user hiện tại
        │   │   ├── token-storage.service.ts    # lưu/đọc JWT
        │   │   ├── group-api.service.ts        # REST /api/groups
        │   │   ├── message-api.service.ts      # REST lịch sử tin nhắn
        │   │   ├── notification-api.service.ts # REST unread, mark read
        │   │   ├── chat-hub.service.ts         # SignalR /hubs/chat
        │   │   └── notification-hub.service.ts # SignalR /hubs/notifications
        │   ├── interceptors/
        │   │   ├── jwt.interceptor.ts          # gắn Authorization: Bearer
        │   │   └── error.interceptor.ts        # 401 → logout, 409 → báo xung đột
        │   └── guards/
        │       ├── auth.guard.ts               # CanLoad + CanActivate: phải đăng nhập
        │       └── guest.guard.ts              # đã đăng nhập thì không vào /auth
        ├── shared/                 # dùng lại ở nhiều feature, KHÔNG chứa service singleton
        │   ├── shared.module.ts
        │   └── components/         # loading-spinner, confirm-dialog, avatar
        └── features/
            ├── auth/               # lazy: /auth
            │   ├── auth.module.ts
            │   ├── auth-routing.module.ts
            │   └── pages/ login/, register/
            └── chat/               # lazy: /chat
                ├── chat.module.ts
                ├── chat-routing.module.ts
                ├── pages/chat-layout/          # khung: sidebar trái + <router-outlet>
                └── components/
                    ├── group-list/             # danh sách nhóm + chấm đỏ unread + online
                    ├── chat-window/            # route con /chat/:groupId
                    ├── message-list/           # hiển thị, sắp theo sequenceNumber, cuộn tải thêm
                    ├── message-input/          # ô nhập, sinh messageId
                    ├── member-panel/           # thành viên, thêm/xóa
                    ├── create-group-dialog/
                    └── edit-group-dialog/      # gửi kèm version, xử lý 409
```

Routes:
| Đường dẫn | Module/Component | Guard |
|---|---|---|
| `''` | redirect `/chat` | – |
| `/auth/login`, `/auth/register` | AuthModule (lazy) | GuestGuard |
| `/chat` | ChatModule (lazy) → ChatLayout | AuthGuard (canLoad, canActivate) |
| `/chat/:groupId` | ChatWindow (route con) | – |

Quy tắc frontend:
- Hub service nằm trong `core/` (`providedIn: 'root'`) để cả app chỉ có MỘT kết nối mỗi hub. Không provide lại trong module lazy.
- Kết nối hub khi đăng nhập, `stop()` khi đăng xuất; truyền JWT bằng `accessTokenFactory`; bật `withAutomaticReconnect()`.
- Sau khi reconnect, ConnectionId mới nên phải gọi lại `JoinGroup` cho nhóm đang mở (`onreconnected`).
- Component không gọi HttpClient trực tiếp, chỉ gọi qua service trong `core/services`.

## 3. Database

### identity_db
**users**: Id (uuid v7, PK), UserName (varchar 50, unique), Email (varchar 100, unique), PasswordHash (text, BCrypt), DisplayName (varchar 100), CreatedAt (timestamptz).
Tự viết bảng + BCrypt, không dùng ASP.NET Core Identity.

### group_db
**groups**: Id (uuid PK), Name (varchar 100), Description (varchar 500, null), OwnerId (uuid, không FK), CreatedAt, UpdatedAt, Version (`[Timestamp] uint` → cột hệ thống xmin, dùng Optimistic Locking).
**group_members**: GroupId (FK groups), UserId, Role (`Owner`/`Member`), JoinedAt. PK (GroupId, UserId).
**user_snapshots**: UserId (PK), DisplayName, UserName. Bản sao từ sự kiện `identity.user-registered`.

### chat_db
**messages**: Id (uuid, DO CLIENT SINH → chống trùng), GroupId, SenderId, SenderName, Content (text), SequenceNumber (bigint, từ Redis INCR, dùng để sắp xếp), CreatedAt (chỉ hiển thị, không dùng sắp xếp).
Unique index (GroupId, SequenceNumber).

Redis của chat-service:
- `chat:seq:{groupId}` (string số): bộ đếm số thứ tự tin nhắn. Mất key → khởi tạo lại từ MAX(SequenceNumber) trong DB.
- `presence:{userId}` (set): các ConnectionId SignalR đang mở. Rỗng = offline.
- `group:members:{groupId}` (set): cache thành viên; xóa khi có member-added/removed.

### notification_db
**unread_counters**: UserId, GroupId (PK kép), UnreadCount (int), LastReadSequence (bigint), UpdatedAt.
**group_member_snapshots**: GroupId, UserId (PK kép). Bản sao từ sự kiện group.member-*.
**processed_events**: EventId (PK), ProcessedAt. Idempotent Consumer (Kafka at-least-once).

## 4. Kafka

Mọi sự kiện có 3 field chung: `eventId` (GUID của sự kiện), `eventType`, `occurredAt`.

| Topic | Bên phát | Bên nghe | Key | Partition |
|---|---|---|---|---|
| identity.user-registered | identity | group | userId | 1 |
| group.member-added | group | chat, notification | groupId | 1 |
| group.member-removed | group | chat, notification | groupId | 1 |
| chat.message-sent | chat | notification | groupId | 3 |

Consumer group đặt trùng tên service (group-service, chat-service, notification-service).

Payload:
- `identity.user-registered`: userId, userName, displayName
- `group.member-added`: groupId, userId, role
- `group.member-removed`: groupId, userId
- `chat.message-sent`: messageId, groupId, senderId, senderName, sequenceNumber, contentPreview (50 ký tự đầu)

## 5. API

### Định tuyến Gateway
| Tiền tố | Đến |
|---|---|
| /api/auth/*, /api/users/* | identity-service |
| /api/groups/* | group-service |
| /api/chat/*, /hubs/chat | chat-service |
| /api/notifications/*, /hubs/notifications | notification-service |

### identity-service
- POST /api/auth/register (công khai): userName, email, password, displayName → phát identity.user-registered
- POST /api/auth/login (công khai): userName, password → accessToken (JWT)
- GET /api/users/me (đã đăng nhập)
JWT claims: `sub` (UserId), `name` (UserName), `display_name`.

### group-service
- POST /api/groups – tạo nhóm, người tạo là Owner (phát member-added cho Owner)
- GET /api/groups – nhóm mình tham gia
- GET /api/groups/{groupId} – chi tiết + thành viên (chỉ thành viên)
- PUT /api/groups/{groupId} – Owner sửa tên/mô tả, body kèm `version`; version cũ → 409 Conflict
- DELETE /api/groups/{groupId} – Owner
- GET /api/groups/users/search?q= – tìm trong user_snapshots
- POST /api/groups/{groupId}/members – Owner thêm, body: userId → phát member-added
- DELETE /api/groups/{groupId}/members/{userId} – Owner hoặc chính người đó → phát member-removed

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
- GET /api/chat/groups/{groupId}/messages?beforeSeq=&limit=50 – lịch sử, phân trang bằng sequenceNumber

SignalR Hub `/hubs/chat` (JWT truyền qua query `access_token`):
| Hướng | Method | Tham số |
|---|---|---|
| Client → Server | JoinGroup | groupId |
| Client → Server | LeaveGroup | groupId |
| Client → Server | SendMessage | messageId, groupId, content |
| Server → Client | ReceiveMessage | message (có sequenceNumber) |
| Server → Client | UserPresenceChanged | userId, isOnline |

### notification-service
- GET /api/notifications/unread
- POST /api/notifications/groups/{groupId}/read – body: lastReadSequence

SignalR Hub `/hubs/notifications`:
| Hướng | Method | Tham số |
|---|---|---|
| Server → Client | UnreadCountChanged | groupId, unreadCount |

## 6. Luồng gửi tin nhắn
1. Angular sinh messageId, gọi SendMessage qua SignalR (qua Nginx tới 1 bản chat-service).
2. Kiểm tra thành viên: Redis cache → nếu trống gọi gRPC (Polly bảo vệ).
3. messageId đã tồn tại → bỏ qua (idempotency).
4. Redis INCR chat:seq:{groupId} → sequenceNumber.
5. Lưu PostgreSQL.
6. Gửi ReceiveMessage tới phòng SignalR của nhóm (Redis Backplane để mọi bản đều phát).
7. Phát chat.message-sent (key groupId).
8. notification-service: kiểm tra processed_events → tăng UnreadCount (trừ người gửi) → UnreadCountChanged.

## 7. Công nghệ theo chương
| Chương | Công nghệ |
|---|---|
| 1. Mở đầu | draw.io, Mermaid/PlantUML |
| 2. Kiến trúc | Microservices, Event-driven, YARP, Database per service, eventual consistency |
| 3. Tiến trình & luồng | async/await, BackgroundService (Kafka consumer), Hangfire, Docker Compose |
| 4. Trao đổi thông tin | REST, gRPC, Kafka, Redis cache, SignalR + Redis Backplane |
| 5. Định danh | GUID v7, JWT, SignalR ConnectionId, Docker DNS, URI |
| 6. Đồng bộ hóa | Redis INCR (số thứ tự), Kafka partition key, idempotency, Optimistic Locking (xmin) |
| 7. Sao lưu | PostgreSQL streaming replication, pg_dump + Hangfire, Redis AOF, Kafka retention/replay |
| 8. Chịu lỗi | Polly, Nginx load balancing, Health Checks, Serilog + Seq, SignalR auto reconnect, consumer group |
