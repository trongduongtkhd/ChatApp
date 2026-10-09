import { HttpEvent, HttpHandler, HttpInterceptor, HttpRequest } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { TokenStorageService } from '../services/token-storage.service';

// Interceptor: đứng giữa MỌI request HttpClient và mạng. Component/service chỉ gọi http.get(...),
// không ai phải tự gắn token → không quên, không lặp code.
@Injectable()
export class JwtInterceptor implements HttpInterceptor {
  constructor(private readonly storage: TokenStorageService) {}

  intercept(req: HttpRequest<unknown>, next: HttpHandler): Observable<HttpEvent<unknown>> {
    const token = this.storage.getToken();
    // Chỉ gắn cho API của mình (/api/...): không gửi token sang trang khác (vd Google Fonts).
    if (token && req.url.startsWith(environment.apiUrl)) {
      // HttpRequest bất biến → clone kèm header mới thay vì sửa trực tiếp.
      req = req.clone({ setHeaders: { Authorization: `Bearer ${token}` } });
    }
    return next.handle(req);
  }
}
