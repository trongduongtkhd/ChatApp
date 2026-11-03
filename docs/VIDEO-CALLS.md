# Gọi video 1–1 — WebRTC và SignalR

## Phạm vi và luồng hoạt động

Hai tài khoản đang là bạn bè, có quyền trong cuộc trò chuyện riêng và đang kết nối ứng dụng có thể gọi nhau. Có nhận/từ chối/hủy/kết thúc, báo bận, tắt/bật microphone và camera. Cuộc gọi đến hiện trên toàn ứng dụng, kể cả khi người nhận đang ở Danh bạ. Chỉ một tab của người nhận được nhận cuộc gọi. Chưa có gọi nhóm, ghi âm/ghi hình hoặc lịch sử cuộc gọi.

1. Người gọi bấm biểu tượng camera ở đầu chat riêng, cho phép camera và microphone.
2. ChatHub kiểm JWT, trạng thái tài khoản, quan hệ bạn bè ở Identity và quyền thành viên từ Group gRPC. Redis giữ chỗ cả hai người bằng một thao tác Lua nguyên tử, kể cả khi hai kết nối ở hai Chat replica khác nhau.
3. SignalR gửi lời mời đến các tab của người nhận. Camera người nhận chỉ mở sau khi họ bấm Nhận cuộc gọi và cấp quyền. Redis chọn đúng một connection nhận cuộc gọi.
4. SignalR chuyển SDP offer/answer và ICE candidate giữa hai connection được chọn. Đây là thông tin thương lượng đường truyền, không phải dữ liệu hình ảnh/âm thanh.
5. WebRTC truyền âm thanh/hình ảnh giữa trình duyệt; nếu có TURN được cấu hình và cần relay thì media đi qua TURN. Kafka vẫn xử lý sự kiện tin nhắn như trước, không vận chuyển media của cuộc gọi.
6. Kết thúc hoặc mất quyền/kết nối: ứng dụng đóng RTCPeerConnection, dừng mọi track camera/microphone và giải phóng trạng thái bận.

Media, SDP và ICE không được lưu vào MongoDB, SeaweedFS hoặc Kafka. Redis chỉ chứa trạng thái cuộc gọi tạm thời, các mã người dùng/connection, version và deadline. WebRTC mã hóa đường truyền media; không triển khai chức năng ghi hình.

## Chạy và thử bằng tay

Giữ các dịch vụ Identity, Group, Gateway và hạ tầng đang chạy theo hướng dẫn dự án. Sau khi sửa backend Chat:

```powershell
cd D:\Projects\ChatApp
docker compose up -d --build chat-service-1 chat-service-2
cd frontend\chat-app
npm start
```

Nếu Angular đã chạy ở 4200, dùng phiên đó; tải lại trang sau khi build xong. Không chạy thêm một `npm start` trên cùng cổng.

- Mở `http://localhost:4200` bằng hai hồ sơ trình duyệt riêng hoặc một cửa sổ thường và một cửa sổ ẩn danh, đăng nhập hai tài khoản đã kết bạn. Hai tab chung hồ sơ dùng chung localStorage nên không phù hợp để thử hai tài khoản.
- Mở chat riêng, bấm camera ở góc trên phải. Bên kia nhận lời mời, bấm Nhận cuộc gọi và cấp quyền camera/microphone. Mong đợi thấy hình hai bên, đồng hồ cuộc gọi và các nút điều khiển.
- Dùng tai nghe để tránh vọng âm nếu hai máy ở gần nhau. Nếu trình duyệt chặn autoplay, bấm Phát âm thanh/video.
- Camera cần HTTPS hoặc localhost. Mở bằng `http://<IP-LAN>:4200` có thể bị trình duyệt chặn camera.

## Cloudflare Tunnel và chi phí

Tính năng này không tích hợp tài khoản thanh toán hoặc dịch vụ media trả phí. WebRTC và SignalR chạy trên hạ tầng hiện có; Coturn tùy chọn chạy Docker local. Vẫn sử dụng điện, CPU, RAM và đường truyền của máy bạn. Không tự đăng ký AWS, Cloudflare Realtime/TURN hay thuê VPS.

Khi đã cài `cloudflared`, có thể dùng Quick Tunnel cho bản demo:

```powershell
cloudflared tunnel --url http://localhost:4200 --http-host-header localhost
```

Gửi link HTTPS `trycloudflare.com` được in ra cho bạn của bạn. Proxy Angular hiện có chuyển `/api` và `/hubs` về Gateway, bao gồm WebSocket. Máy chủ phải tiếp tục chạy Docker, các backend local, Angular và cloudflared. Quick Tunnel có URL tạm thời và giới hạn dịch vụ; chưa kiểm thử giữa hai mạng Internet trong lần triển khai này. Tham khảo [Quick Tunnels](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/do-more-with-tunnels/trycloudflare/) và [HTTP Host Header](https://developers.cloudflare.com/tunnel/reference/origin-parameters/#httphostheader).

**Tunnel web không thay thế TURN.** Hai người không cần chung mạng, nhưng NAT/firewall có thể ngăn đường truyền trực tiếp. STUN giúp tìm địa chỉ kết nối; STUN không relay media. Muốn gọi ổn định ở những mạng đó cần TURN có địa chỉ mà cả hai thiết bị truy cập được. TURN localhost dưới đây chỉ phục vụ thử trên chính máy này; không giúp người bạn ở mạng khác kết nối. Public TURN cần cấu hình IP/NAT/cổng thực tế; không tự động được đưa ra Internet qua lệnh Quick Tunnel trên.

## Cấu hình STUN/TURN

- `VideoCalls:StunUrls`: mặc định `stun:stun.l.google.com:19302`, có thể thay bằng máy chủ phù hợp.
- `VideoCalls:TurnUrls`: mặc định rỗng. Compose nhận phần tử đầu qua `VIDEO_TURN_URL`, ví dụ `turn:<host-truy-cap-duoc>:3478?transport=udp`.
- `VIDEO_TURN_SHARED_SECRET`: khóa ngẫu nhiên dùng chung giữa backend và Coturn `use-auth-secret`. Giữ ở môi trường local/user-secrets; không commit hoặc đưa vào Angular.
- `VIDEO_ICE_TRANSPORT_POLICY`: `all` mặc định, `relay` để buộc thử TURN. Relay không có TURN bị từ chối.

Backend tạo credential TURN tạm thời theo HMAC-SHA1, thời hạn một giờ, cấp qua hub đã xác thực. Trình duyệt chỉ nhận credential tạm; không nhận shared secret. Cấu hình được làm mới trước mỗi lần gọi/nhận. Chưa triển khai gia hạn credential và ICE restart trong cuộc gọi rất dài; có thể cần kết thúc rồi gọi lại.

### Coturn Docker tùy chọn, chỉ trên cùng máy

`docker-compose.turn.yml` không được include mặc định. Script tạo khóa local có gitignore, không in khóa ra màn hình:

```powershell
cd D:\Projects\ChatApp
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/init-video-turn.ps1
docker compose --env-file .env --env-file infra/turn.local.env -f docker-compose.yml -f docker-compose.turn.yml --profile turn up -d video-turn chat-service-1 chat-service-2
$env:VIDEO_TEST_RELAY = '1'
try { node scripts/video-call-test.cjs --ui-only } finally { Remove-Item Env:VIDEO_TEST_RELAY -ErrorAction SilentlyContinue }
```

Chỉ bind `127.0.0.1`: TURN 3479 TCP/UDP và relay UDP 49160–49200. Script mới dùng `turn:127.0.0.1:3479?transport=tcp` cho kết nối client tới TURN; nếu đã có file cấu hình local cũ dùng UDP thì sửa URL trong file đó trước khi chạy lại Compose. Đây là cấu hình test local, cho phép loopback peer, không dùng nguyên cấu hình này làm public TURN. Test relay chỉ dùng TURN URL từ backend và bỏ STUN công cộng để kiểm tra riêng đường relay.

Trả về cấu hình mặc định sau thử nghiệm:

```powershell
docker compose up -d --force-recreate chat-service-1 chat-service-2
docker compose --env-file .env --env-file infra/turn.local.env -f docker-compose.yml -f docker-compose.turn.yml --profile turn stop video-turn
docker compose --env-file .env --env-file infra/turn.local.env -f docker-compose.yml -f docker-compose.turn.yml --profile turn rm -f video-turn
```

## Đồng bộ hóa và chịu lỗi

- `Ringing` tối đa 30s; thiết lập kết nối tối đa 45s sau khi nhận. Không trả lời thì giải phóng bận.
- Mỗi phía gửi pulse 5s trong cuộc gọi, lease 30s; sweeper chạy 2s. Mốc dùng Redis TIME để hai replica không phụ thuộc đồng hồ riêng. Heartbeat ứng dụng vẫn theo Presence hiện có; lease gọi video riêng không thay đổi quy tắc online 60s.
- Connection gọi video đăng ký lease 30s, được làm mới từ heartbeat ứng dụng. Người nhận phải có kết nối SignalR còn lease; trạng thái online Presence không bảo đảm có thể nhận cuộc gọi ở đúng thời điểm mạng mất.
- Mỗi user tối đa một cuộc gọi; Redis Lua giữ busy cả hai user và chọn tab nhận nguyên tử. Version giúp client bỏ snapshot cũ. Tín hiệu chỉ đến hai connection đã chọn; người ngoài/tab khác không được điều khiển cuộc gọi đang nhận.
- Monitor kiểm tra quyền và AuthVersion khoảng 6s, ngoài kiểm tra trên thao tác hub. Thu hồi bạn bè, thành viên, khóa tài khoản hoặc đổi phiên dẫn đến kết thúc trong ứng dụng. Lỗi nguồn quyền xử lý fail closed. Server không thể ép một client WebRTC bị sửa trái phép tự ngừng media sau khi đã thương lượng trực tiếp.
- Mất SignalR kết thúc cuộc gọi, không tự khôi phục media khi reconnect; người dùng gọi lại. ICE disconnected cho phép chờ phục hồi 10s, failed kết thúc. Camera xin quyền trả về sau lúc hủy vẫn được dừng track.
- Redis hiện một node; chưa có HA. Monitor đọc tối đa 200 call đang có deadline mỗi vòng kiểm quyền: phù hợp đồ án, chưa tuyên bố đáp ứng quy mô production.

## Code, khái niệm và chương báo cáo

| Code | Vai trò | Chương |
|---|---|---|
| `Hubs/ChatHub.cs`, `Services/VideoCallCoordinator.cs` trong ChatService | Signaling SignalR; RPC Identity/Group kiểm quyền, định tuyến theo ConnectionId | 2, 4, 5 |
| `Services/VideoCallStore.cs` | Lua nguyên tử, Redis TIME, lease, busy và version trên hai replica | 6 |
| `Services/VideoCallMonitor.cs` | BackgroundService kiểm deadline và thu hồi quyền | 3, 8 |
| `frontend/chat-app/src/app/core/services/video-call.service.ts` | getUserMedia, SDP offer/answer, ICE, RTCPeerConnection, dọn media | 4, 8 |
| `frontend/chat-app/src/app/shared/components/video-call/` | Hộp cuộc gọi toàn ứng dụng, video và điều khiển thiết bị | 4 |
| `docker-compose.turn.yml` | TURN relay tự chạy, tách khỏi HTTP Tunnel | 2, 4 |

## Kiểm thử và demo lỗi

Build Chat backend và Angular production trước. Script dùng Node 14, SignalR/ws đã có trong Angular; Edge headless với **camera/microphone giả**, profile tạm riêng, không dùng thiết bị hoặc phiên trình duyệt cá nhân:

```powershell
node scripts/video-call-test.cjs --ui
```

API kiểm quyền, hai replica, bận, gọi chéo, hai tab cùng nhận, chỉ định tuyến tới tab được chọn, không trả lời 30s, ngắt phiên và hủy bạn. UI kiểm frame video, `getStats()` có inbound RTP audio/video tăng, điều khiển thiết bị, từ chối quyền camera, hủy khi đang xin quyền, reload và hủy bạn. Mặc định test UI bỏ ICE server để kiểm đường host local; tùy chọn relay kiểm đường Coturn riêng. Script finally dọn đúng fixture có backup và đối chiếu dữ liệu thật; không chạy đồng thời với suite khác có cleanup.

Để thử UI với nguyên danh sách ICE server backend đang cấu hình (không bỏ STUN):

```powershell
$env:VIDEO_TEST_CONFIGURED_ICE = '1'
try { node scripts/video-call-test.cjs --ui-only } finally { Remove-Item Env:VIDEO_TEST_CONFIGURED_ICE -ErrorAction SilentlyContinue }
```

Kết quả ngày 28/10/2026: 32 API; 20 UI đường host local; chạy lại 20 UI với STUN mặc định; 21 UI buộc relay Coturn TCP đều qua. Hồi quy 43 API/17 UI ảnh-file và 110 kiểm tra chat/presence/Mongo cũng qua. Thiết bị trong test là giả và hai trình duyệt cùng máy; chưa kiểm thử hai người ở hai mạng Internet. Runtime đã trả về STUN/all, container Coturn thử nghiệm đã được gỡ. Không còn user/nhóm fixture của các lượt thử; fingerprint dữ liệu giữ lại khớp trước/sau cleanup.

Demo lỗi bằng tay:

1. Gọi nhưng không nhận: khoảng 30s hiện Không có người trả lời, sau đó gọi lại được.
2. Chặn quyền camera bên nhận rồi nhận cuộc gọi: hiện hướng dẫn cấp quyền, phía gọi được giải phóng, camera hai bên ngừng.
3. Đang gọi, tải lại trang một bên: phía kia thấy mất kết nối và dừng camera; gọi lại sau khi hub kết nối lại.
4. Đang gọi, hủy kết bạn: ứng dụng kết thúc và dừng media sau kiểm tra quyền. Khóa tài khoản được xử lý theo AuthVersion/account monitor hiện có.
5. Hai máy khác mạng: nếu đổ chuông/nhận được nhưng không có hình và báo cần TURN, kiểm tra đường media/NAT và cấu hình TURN public; không kết luận SignalR hoặc Kafka không hoạt động.
