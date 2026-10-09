import { HttpClient } from '@angular/common/http';
import { Injectable, NgZone } from '@angular/core';
import { Router } from '@angular/router';
import { BehaviorSubject, Observable } from 'rxjs';
import { map, switchMap, tap } from 'rxjs/operators';
import { environment } from '../../../environments/environment';
import { CurrentUser, LoginRequest, LoginResponse, RegisterRequest } from '../models/user';
import { TokenStorageService } from './token-storage.service';

// Đăng nhập / đăng ký / đăng xuất và "ai đang đăng nhập".
// Không hỏi server "tôi là ai": thông tin người dùng nằm sẵn trong payload của JWT (stateless, Phần 2).
@Injectable({ providedIn: 'root' })
export class AuthService {
  // BehaviorSubject: luôn giữ giá trị hiện tại; ai subscribe sau cũng nhận ngay giá trị mới nhất.
  private readonly userSubject = new BehaviorSubject<CurrentUser | null>(null);
  readonly currentUser$ = this.userSubject.asObservable();

  private expiryTimer?: ReturnType<typeof setTimeout>;

  constructor(
    private readonly http: HttpClient,
    private readonly storage: TokenStorageService,
    private readonly router: Router,
    private readonly zone: NgZone
  ) {
    // F5 / mở tab mới: còn token hợp lệ trong localStorage thì khôi phục phiên đăng nhập.
    const token = this.storage.getToken();
    if (token) {
      this.startSession(token);
    } else {
      this.storage.clear();
    }
  }

  get currentUser(): CurrentUser | null {
    return this.userSubject.value;
  }

  isLoggedIn(): boolean {
    return this.storage.getToken() !== null;
  }

  login(request: LoginRequest): Observable<CurrentUser> {
    return this.http.post<LoginResponse>(`${environment.apiUrl}/auth/login`, request).pipe(
      tap(res => this.storage.save(res.accessToken, res.expiresAt)),
      map(res => this.startSession(res.accessToken))
    );
  }

  // Đăng ký xong (201) thì đăng nhập luôn bằng chính tên + mật khẩu vừa nhập.
  register(request: RegisterRequest): Observable<CurrentUser> {
    return this.http.post(`${environment.apiUrl}/auth/register`, request).pipe(
      switchMap(() => this.login({ userName: request.userName, password: request.password }))
    );
  }

  // GET /api/users/me – hỏi identity-service thông tin đầy đủ (có email). Token do JwtInterceptor tự gắn.
  me(): Observable<{ id: string; userName: string; email: string; displayName: string }> {
    return this.http.get<{ id: string; userName: string; email: string; displayName: string }>(`${environment.apiUrl}/users/me`);
  }

  logout(): void {
    clearTimeout(this.expiryTimer);
    this.storage.clear();
    this.userSubject.next(null);
    this.router.navigate(['/auth/login']);
  }

  private startSession(token: string): CurrentUser {
    const user = this.decodeUser(token);
    this.userSubject.next(user);

    // Token hết hạn (60 phút) → tự đăng xuất đúng lúc, không đợi tới request kế tiếp bị 401.
    // Chạy hẹn giờ NGOÀI Angular zone: một setTimeout 60 phút trong zone làm Angular coi app "chưa ổn định".
    clearTimeout(this.expiryTimer);
    const msLeft = this.storage.getExpiresAt() - Date.now();
    this.zone.runOutsideAngular(() => {
      this.expiryTimer = setTimeout(() => this.zone.run(() => this.logout()), msLeft);
    });
    return user;
  }

  // JWT = header.payload.signature, mỗi phần mã hóa base64url. Payload chỉ MÃ HÓA, không bí mật:
  // client đọc được nhưng không sửa được (sửa thì chữ ký sai → server trả 401).
  private decodeUser(token: string): CurrentUser {
    const base64 = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    // atob trả từng byte; tên tiếng Việt là UTF-8 nhiều byte → phải giải mã UTF-8, nếu không "Dương" thành "DÆ°Æ¡ng".
    const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
    const claims = JSON.parse(new TextDecoder().decode(bytes));
    return {
      id: claims.sub,
      userName: claims.name,
      displayName: claims.display_name || claims.name
    };
  }
}
