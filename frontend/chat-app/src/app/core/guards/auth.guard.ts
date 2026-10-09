import { Injectable } from '@angular/core';
import { CanActivate, CanLoad, Router, UrlTree } from '@angular/router';
import { AuthService } from '../services/auth.service';

// Chỉ người đã đăng nhập (còn token chưa hết hạn) mới vào /chat.
// CanLoad: chặn TRƯỚC khi tải file JS của ChatModule (chưa đăng nhập thì không tải code khung chat).
// CanActivate: chặn mỗi lần vào route, kể cả khi module đã tải rồi (vd đăng xuất xong bấm Back).
// Guard chỉ là lớp tiện cho giao diện; bảo vệ THẬT là server kiểm tra JWT (Gateway + từng service).
@Injectable({ providedIn: 'root' })
export class AuthGuard implements CanLoad, CanActivate {
  constructor(private readonly auth: AuthService, private readonly router: Router) {}

  canLoad(): boolean | UrlTree {
    return this.check();
  }

  canActivate(): boolean | UrlTree {
    return this.check();
  }

  private check(): boolean | UrlTree {
    // Trả UrlTree = "đừng vào đây, chuyển sang trang này".
    return this.auth.isLoggedIn() ? true : this.router.parseUrl('/auth/login');
  }
}
