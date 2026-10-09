import { Injectable } from '@angular/core';

const TOKEN_KEY = 'chatapp.accessToken';
const EXPIRES_KEY = 'chatapp.expiresAt';

// Lưu / đọc JWT trong localStorage của trình duyệt → F5 hay mở tab mới vẫn còn đăng nhập.
// Đánh đổi: localStorage đọc được bằng JavaScript, nên nếu trang bị chèn script lạ (XSS) thì token có thể bị lấy.
// Bù lại token chỉ sống 60 phút (stateless JWT không thu hồi được trước hạn – xem Phần 2).
@Injectable({ providedIn: 'root' })
export class TokenStorageService {
  save(token: string, expiresAt: string): void {
    localStorage.setItem(TOKEN_KEY, token);
    localStorage.setItem(EXPIRES_KEY, expiresAt);
  }

  // Trả null nếu chưa có hoặc ĐÃ HẾT HẠN (gửi token hết hạn lên server cũng chỉ nhận 401).
  getToken(): string | null {
    const token = localStorage.getItem(TOKEN_KEY);
    return token && this.getExpiresAt() > Date.now() ? token : null;
  }

  // Thời điểm hết hạn (mili giây kể từ 1/1/1970); 0 nếu không có.
  getExpiresAt(): number {
    const value = localStorage.getItem(EXPIRES_KEY);
    return value ? Date.parse(value) : 0;
  }

  clear(): void {
    localStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(EXPIRES_KEY);
  }
}
