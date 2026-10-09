// Proxy của dev server (ng serve): trình duyệt gọi localhost:4200/api, /hubs → chuyển sang API Gateway :5000.
// Cùng origin với trang → không vướng CORS. Chỉ dùng lúc phát triển; production do web server/Gateway đảm nhận.
//
// Dùng file .js (không phải .json) để gắn được HÀM xử lý lỗi cho proxy WebSocket:
// webpack-dev-server 3.11 (Angular 12) dùng http-proxy 1.18.1. Khi Gateway trả lời request WebSocket bằng
// mã KHÁC 101 (502 vì chat-service tắt, 401 vì token sai), http-proxy chép câu trả lời vào socket của trình duyệt
// mà không nghe sự kiện 'error' trên socket đó. Trình duyệt đã đóng socket (bỏ cuộc, thử lại) → ghi lỗi
// ECONNABORTED/EPIPE → "Unhandled 'error' event" → CẢ dev server sập (đã tái hiện khi tắt 2 bản chat-service).
const target = 'http://localhost:5000';

module.exports = {
  '/api': {
    target,
    secure: false,
    changeOrigin: true,
    logLevel: 'warn'
  },
  '/hubs': {
    target,
    secure: false,
    changeOrigin: true,
    ws: true,
    logLevel: 'warn',
    // Chạy ngay trước khi gửi request WebSocket lên Gateway, nhận đúng socket của trình duyệt.
    onProxyReqWs: (proxyReq, req, socket) => {
      socket.on('error', err => console.warn(`[proxy ws] ${req.url.split('?')[0]}: ${err.code || err.message}`));
    }
  }
};
