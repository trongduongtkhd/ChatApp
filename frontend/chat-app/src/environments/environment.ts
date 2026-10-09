// Đường dẫn TƯƠNG ĐỐI: trình duyệt gọi chính máy chủ dev (localhost:4200),
// proxy.conf.json chuyển /api và /hubs sang API Gateway (localhost:5000).
// Nhờ vậy trình duyệt thấy mọi thứ cùng một origin → không vướng CORS.
export const environment = {
  production: false,
  apiUrl: '/api',
  hubUrl: '/hubs'
};
