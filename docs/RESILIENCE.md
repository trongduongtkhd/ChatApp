# Chương 8 — Tính chịu lỗi, giám sát và Kubernetes

Phạm vi đã duyệt ngày 01/11/2026: health/probes, Serilog/Seq, Polly, Prometheus/Grafana, Kubernetes cho Chat, tương thích backup, HPA và kiểm thử có dọn fixture. Kubernetes local dùng kind một node; đây là phục hồi/scale tầng ứng dụng, chưa phải HA khi mất cả máy hoặc tự chuyển PostgreSQL primary.

## Chạy hệ thống

PowerShell tại thư mục gốc, Docker Desktop đang chạy, .NET 10 và Node 14:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-resilience-demo.ps1
```

Script build backend/Angular/images, bật Seq/Prometheus/Grafana, khởi động app, tạo cluster kind riêng và chuyển Chat sang Kubernetes. Kind tải từ nguồn chính thức và kiểm SHA-256. Kubeconfig/secret hiệu lực nằm trong runtime gitignore; không thay kubeconfig cá nhân. Hạ tầng dữ liệu và các service khác vẫn theo Compose/local. Chờ READY; không chạy hai script startup cùng lúc.

Đã build đúng mã thì dùng `-SkipBuild`. Muốn quay về hai Chat container Compose:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-resilience-demo.ps1 -SkipBuild -Mode Compose
```

Script quiesce Chat runtime cũ trước khi mở mode mới. Không xóa database hoặc volume nguồn. Thay IP container hạ tầng cần chạy setup lại để cập nhật hostAliases của Chat Pod.

| Địa chỉ | Chức năng |
|---|---|
| http://localhost:4200 | ChatApp |
| http://localhost:3000/d/chatapp-ch8 | Dashboard Grafana |
| http://localhost:9090 | Prometheus, truy vấn và cảnh báo |
| http://localhost:5341 | Seq, lọc log tập trung |

Giám sát local chỉ publish loopback; Grafana cho xem ẩn danh và Seq demo không bật đăng nhập. Khi triển khai server cần cấu hình truy cập/HTTPS phù hợp. Các trang giám sát là công cụ vận hành, không phải route nghiệp vụ của Angular.

## Bản đồ code và khái niệm

| File | Khái niệm và nhiệm vụ |
|---|---|
| Common/Operations/OperationsExtensions.cs | Liveness/readiness; `/metrics`; log service/instance/TraceId; hub counters, histogram và connection gauge |
| Common/Operations/GrpcResilience.cs | Pipeline singleton theo dependency: circuit → total timeout → retry read-only RPC |
| Common/Operations/KafkaLagSampler.cs | Đọc watermark và committed offsets; không join/reset consumer group |
| Common/Auth/AccountAccess.cs | Identity RPC có resilience, giữ fail closed và lỗi tạm thời 503 |
| Chat/Grpc/GroupMembershipClient.cs | Group read-only RPC qua Polly; không coi isMember=false là transport fault |
| infra/observability/ | Prometheus scrape/rules, Grafana datasource/dashboard được provision từ file |
| scripts/setup-chat-k8s.cjs | kind, image, Secret, Deployment, NodePort, probes và Metrics Server |
| scripts/operations-agent.cjs | Bridge loopback tới API Kubernetes; tự khám phá từng Pod cho Prometheus, resource/restart/HPA metrics |
| infra/k8s/hpa.yml | HPA autoscaling/v2: 2..4 Pod, CPU 60% requests, ổn định scale-down 90s |
| infra/nginx/nginx.k8s.conf | Gateway → Nginx → NodePort/Service → nhiều Chat Pod; không dùng service port-forward ghim vào một Pod |
| Common/Backup/BackupWriteGate.cs | Registry/epoch và đọc intent nguyên tử; Pod mới chờ cửa trước startup writes |
| Gateway/Backup/ChatBackupRuntime.cs | Adapter Compose/Kubernetes cho tập writer và exec/copy checkpoint/export |
| Gateway/Backup/BackupOrchestrator.cs | Kiểm đủ ACK/generation/epoch và từ chối writer ngoài runtime trước khi công bố checkpoint |

Các đường Common/... nằm dưới backend/src/BuildingBlocks/ChatApp.Common; Chat/... dưới backend/src/Services/Chat/ChatApp.ChatService; Gateway/... dưới backend/src/Gateway/ChatApp.Gateway.

Polly mặc định: tổng 3s, retry một lần có delay/jitter cho Unavailable/DeadlineExceeded; circuit ratio 50%, ít nhất 6 logical calls trong 15s, nghỉ 10s. Không retry thao tác ghi một cách đại trà. Circuit state là riêng mỗi process/dependency; không phải một circuit toàn cluster. Client hủy request không được tính là lỗi dependency.

Health `/health/live` chỉ kiểm khả năng phản hồi của process. `/health/ready` kiểm storage cần thiết; Kafka/Presence gián đoạn được theo dõi riêng. Mongo cần thiết cho Chat; SQL cần thiết cho Identity/Group/Notification. File maintenance được cấu hình cho Chat K8s là `/tmp/chatapp-not-ready`, dùng để ngừng nhận kết nối mới có kiểm soát. Readiness không tự đóng WebSocket đã mở; client reconnect/history catch-up và quyền nguồn vẫn cần thiết.

Metrics giữ label hữu hạn, không dùng user/group/message ID hoặc nội dung tin làm label. Độ trễ hub là thời gian xử lý phía server, khác latency từ nút Gửi tới trình duyệt nhận ACK. HTTP 101 chỉ đo bước mở WebSocket, không đại diện toàn bộ thời gian chat. Các lần gửi được đo riêng trong hub filter. Seq không ghi body/query token ở request logging.

CPU request 200m/Pod sau đo tải nền, mục tiêu HPA 60% nghĩa là khoảng 120m CPU trung bình so với request, không phải 60% toàn máy. HPA không thêm RAM/CPU vật lý; đủ 4 Pod vẫn có thể quá tải. Nhiều kết nối không tự chứng minh CPU cao; cần đo tải nghiệp vụ thực. Pod mới nhận kết nối mới; các WebSocket cũ không tự di chuyển.

## Demo và kiểm thử

```powershell
$kc = 'D:\Projects\ChatApp\infra\backups\runtime\chapter8\kubeconfig'
kubectl --kubeconfig $kc -n chatapp get pods
kubectl --kubeconfig $kc -n chatapp get hpa
kubectl --kubeconfig $kc -n chatapp top pods
node .\scripts\resilience-test.cjs
node .\scripts\monitoring-ui-test.cjs
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-resilience.ps1 -SkipStartup
```

`k8s-probe-test` xác minh liveness bằng SIGSTOP đúng process Chat từ PID namespace cha của kind (PID 1 bên trong Pod bỏ qua một số tín hiệu). Nó kiểm container ID/PID, chờ restart thật và Ready; finally chỉ gửi CONT nếu container gốc còn chạy với đúng PID.

`resilience-test` tạo fixture có username/email/display-name và thời gian riêng, kiểm health/monitoring, readiness maintenance, Group outage/circuit/recovery, Pod deletion/client reconnect/idempotency, CPU HPA scale-up/down và Pod mới nhận traffic. Finally mở lại Group/xóa maintenance/đóng hubs rồi dùng cleaner hiện có để backup scoped fixture, dọn SQL/Mongo/Redis/object và kiểm retained-data fingerprints. Không chạy nhiều suite thay đổi dữ liệu đồng thời. Test có thể kéo dài vài phút do HPA stabilization và fault recovery.

Demo tải HPA riêng: terminal theo dõi `kubectl --kubeconfig $kc -n chatapp get hpa -w`; terminal thứ hai chạy `node scripts/hpa-load.cjs --seconds=90 --workers=32`. Tải là history API có xác thực trên nhóm fixture riêng. Ctrl+C dừng tải rồi cleanup. Theo dõi scale-down sau khi tải kết thúc; số Pod không giảm tức thì.

Demo circuit thủ công: PowerShell thứ hai chạy `control-local-service.ps1 -Service Group -Action Stop`, thử gửi tin hoặc mở history; xem circuit và lỗi trong dashboard/Seq. Bật lại bằng `-Action Start`, chờ half-open/closed. Group down thì không được bỏ qua quyền thành viên. Luôn bật lại Group sau demo.

Demo readiness: chọn đúng Pod thử, tạo file maintenance bằng `kubectl exec <pod> -- touch /tmp/chatapp-not-ready`; quan sát NotReady và EndpointSlice, process vẫn sống. Xóa file bằng `rm /tmp/chatapp-not-ready`. Demo tự phục hồi: xóa một Chat Pod; quan sát Pod thay thế và client reconnect. Không hứa giữ nguyên cuộc gọi WebRTC khi tiến trình signaling bị mất; kiểm kết thúc/lỗi và giải phóng trạng thái theo cơ chế hiện có.

Hồi quy backup trong mode Kubernetes dùng `node scripts/backup-system-test.cjs --cleanup-recovery`. Mốc bắt buộc chứa đủ writer đang hoạt động; thay tập/generation/epoch trong cut làm job thất bại thay vì complete sai. Sau khi export, thay Pod export có thể làm tác vụ thất bại; chạy lại job mới khi ổn định. HPA không bị tắt âm thầm để che lỗi phối hợp. Recovery vẫn là web Compose riêng 4201, source không bị ghi đè.

Logs/report/screenshot và công cụ tải xuống ở `infra/backups/runtime/chapter8/`. App fixture nguồn phải được dọn cuối test. Kafka là log append-only dùng chung, các event thử còn theo retention hiện có; không xóa topic/reset offsets để dọn fixture. Bundle/recovery tạo riêng cho test được dọn sau khi nghiệm thu và giữ report tổng hợp.

## Kết quả nghiệm thu

Nghiệm thu local ngày 01/11/2026:

| Nhóm kiểm thử | Kết quả |
|---|---|
| Circuit, health/readiness, reconnect, idempotency, backplane, HPA | 30/30; 14.131 lượt đọc tải thành công, 0 lỗi; scale-up và scale-down thực |
| Liveness với process bị treo | 2/2; container restart từ 0 lên 1, trở lại Ready |
| Backup/restore từ Chat Kubernetes | 24/24; 80 writes, 144 reads; checkpoint khoảng 2,30 giây; đối chiếu lịch sử và byte file phục hồi |
| Grafana, Seq và Angular qua Edge headless | 5/5; không lỗi JavaScript chưa xử lý |
| Hồi quy chat/Presence/Mongo/bạn bè/UI | 110/110 |
| Hồi quy admin/role/dependency/UI | 122/122 |
| Ảnh/file API và UI | 60/60 |
| Gọi video API và UI, media giả lập | 52/52 |

Tổng 405 kiểm tra trong các nhóm trên đều qua. Sau đó chạy lại riêng 12 kiểm tra dependency và smoke script tải HPA (296 lượt đọc, 0 lỗi) cũng qua. Report cuối nằm trong `infra/backups/runtime/chapter8/`: `resilience-test-report.json`, `probe-test-report.json`, `backup-k8s-test-report.json`, `monitoring-ui-test-report.json`, `regression-summary.json`. Log hồi quy giữ chi tiết số kiểm tra và dấu vân tay dữ liệu trước/sau cleanup.

.NET solution build 0 lỗi, còn hai cảnh báo NU1902 của MessagePack có sẵn. Angular build bằng Node 14/Angular 12 thành công; không thêm npm package. Promtool kiểm sáu luật cảnh báo thành công; script PowerShell/Node qua kiểm cú pháp. Đây là kiểm thử theo các kịch bản trên máy local, chưa phải kiểm thử ngâm dài ngày hoặc HA nhiều node.

Đã xóa dữ liệu fixture nguồn và kiểm fingerprints của dữ liệu giữ lại; gỡ recovery/bundle riêng của test và profile trình duyệt thử. Bản sao an toàn của cleaner và log giám sát vẫn giữ theo quy trình/retention. Kết thúc ở mode Kubernetes, hai Chat Pod Ready và HPA 2..4 hoạt động.

Các điểm code để học lại chương 8: `OperationsExtensions.cs:45` kiểm readiness, `OperationsExtensions.cs:78` đăng ký health/log/metrics, `GrpcResilience.cs:35` cấu hình circuit, `infra/k8s/hpa.yml:8` giới hạn replica và `:14` target CPU. Phần sao lưu dùng `BackupWriteGate.cs:40` đăng ký writer/đọc intent nguyên tử và `BackupOrchestrator.cs:119` từ chối checkpoint khi tập writer thay đổi. Các đường rút gọn được giải thích trong bảng bản đồ code ở trên.
