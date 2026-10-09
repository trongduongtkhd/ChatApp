import { HttpTransportType, HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';

// Cấu hình CHUNG cho mọi kết nối SignalR của app (/hubs/chat, /hubs/notifications) – DESIGN 2b.
export function buildHubConnection(url: string, getToken: () => string | null, onTokenExpired: () => void): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(url, {
      // Bỏ bước negotiate, đi thẳng WebSocket (Phần 8): sau Nginx round-robin, negotiate và WebSocket là 2 request,
      // có thể rơi vào 2 bản khác nhau → 404. Một request duy nhất thì không cần sticky session.
      skipNegotiation: true,
      transport: HttpTransportType.WebSockets,
      // WebSocket không gắn được header Authorization → token đi qua ?access_token= (server chỉ nhận cách này
      // cho /hubs/*, Phần 3). SignalR gọi hàm này trước MỖI lần start và MỖI lượt reconnect → luôn lấy token
      // mới nhất; token đã hết hạn → chắc chắn bị 401 → không thử nữa, báo ra ngoài để đăng xuất.
      accessTokenFactory: () => {
        const token = getToken(); // null khi đã quá expiresAt
        if (!token) {
          onTokenExpired();
          throw new Error('Token đã hết hạn');
        }
        return token;
      }
    })
    // Mất kết nối → tự thử lại sau 0, 2, 10, 30 giây (mặc định), rồi bỏ cuộc (onclose).
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();
}
