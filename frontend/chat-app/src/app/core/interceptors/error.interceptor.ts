import { HttpErrorResponse, HttpEvent, HttpHandler, HttpInterceptor, HttpRequest } from '@angular/common/http';
import { Injectable, Injector } from '@angular/core';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { environment } from '../../../environments/environment';
import { AuthService } from '../services/auth.service';

// Xử lý lỗi CHUNG cho mọi request:
// 401 = token thiếu / sai / hết hạn → đăng xuất, về trang đăng nhập.
// Các lỗi khác (403, 409, 503...) vẫn ném tiếp cho nơi gọi tự hiển thị theo ngữ cảnh
// (409 sửa nhóm → hộp cảnh báo trong dialog, 403 mở nhóm → màn "không còn là thành viên"...).
@Injectable()
export class ErrorInterceptor implements HttpInterceptor {
  // Sai mật khẩu cũng là 401 nhưng trang đăng nhập tự hiện hộp đỏ, không phải "phiên hết hạn".
  private readonly ignore401 = [`${environment.apiUrl}/auth/login`, `${environment.apiUrl}/auth/register`];

  // Không nhận AuthService qua constructor: AuthService cần HttpClient, HttpClient cần interceptor này
  // → vòng tròn phụ thuộc (NG0200). Lấy AuthService từ Injector lúc thật sự cần (khi có lỗi 401).
  constructor(private readonly injector: Injector) {}

  intercept(req: HttpRequest<unknown>, next: HttpHandler): Observable<HttpEvent<unknown>> {
    return next.handle(req).pipe(
      catchError((err: HttpErrorResponse) => {
        if (err.status === 401 && !this.ignore401.includes(req.url)) {
          this.injector.get(AuthService).logout();
        }
        return throwError(err);
      })
    );
  }
}
