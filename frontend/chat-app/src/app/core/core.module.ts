import { HTTP_INTERCEPTORS, HttpClientModule } from '@angular/common/http';
import { NgModule, Optional, SkipSelf } from '@angular/core';
import { ErrorInterceptor } from './interceptors/error.interceptor';
import { JwtInterceptor } from './interceptors/jwt.interceptor';

// CoreModule: chỉ import MỘT lần ở AppModule. Chứa những thứ cả app dùng chung đúng một bản:
// HttpClient, interceptor, các service singleton (auth, chat-hub, notification-hub...).
// Service trong core/ khai báo providedIn: 'root' → Angular tạo một bản duy nhất cho cả app,
// nên cả app chỉ có MỘT kết nối mỗi hub SignalR (DESIGN mục 2b, quy tắc frontend).
@NgModule({
  imports: [HttpClientModule],
  providers: [
    // multi: true → nhiều interceptor xếp thành chuỗi theo thứ tự khai báo:
    // request đi Jwt → Error → mạng; response đi ngược lại (Error thấy lỗi 401 trước khi tới nơi gọi).
    { provide: HTTP_INTERCEPTORS, useClass: JwtInterceptor, multi: true },
    { provide: HTTP_INTERCEPTORS, useClass: ErrorInterceptor, multi: true }
  ]
})
export class CoreModule {
  // Chặn import lại ở module lazy: nếu lỡ import, Angular sẽ tạo thêm một bản service → 2 kết nối hub.
  // @SkipSelf: tìm ở injector CHA; @Optional: không có thì nhận null (lần import đầu tiên).
  constructor(@Optional() @SkipSelf() parentModule: CoreModule | null) {
    if (parentModule) {
      throw new Error('CoreModule đã được import ở AppModule. Không import lại ở module khác.');
    }
  }
}
