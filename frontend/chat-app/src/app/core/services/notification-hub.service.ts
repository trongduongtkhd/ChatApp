import { Injectable, NgZone } from '@angular/core';
import { HubConnection } from '@microsoft/signalr';
import { Observable, Subject } from 'rxjs';
import { distinctUntilChanged, map } from 'rxjs/operators';
import { environment } from '../../../environments/environment';
import { UnreadChange } from '../models/unread-counter';
import { buildHubConnection } from '../utils/hub-connection';
import { AuthService } from './auth.service';
import { TokenStorageService } from './token-storage.service';

// Kết nối SignalR /hubs/notifications (notification-service, Phần 9). MỘT kết nối cho cả app.
// Khác chat hub: không có phòng (server gửi Clients.User – tới mọi kết nối của chính mình), không có dải trạng thái,
// mất kết nối thì cứ lặng lẽ thử lại – số chưa đọc chỉ là phụ, lấy lại được bằng REST.
@Injectable({ providedIn: 'root' })
export class NotificationHubService {
  private connection: HubConnection | null = null;
  private retryTimer?: ReturnType<typeof setTimeout>;

  private readonly changedSubject = new Subject<UnreadChange>();
  private readonly connectedSubject = new Subject<void>();

  readonly unreadChanged$: Observable<UnreadChange> = this.changedSubject.asObservable();
  // Mỗi lần kết nối được (lần đầu VÀ sau reconnect). SignalR không gửi bù UnreadCountChanged đã lỡ
  // → ai nghe sự kiện này phải gọi GET /api/notifications/unread để lấy lại số đúng.
  readonly connected$: Observable<void> = this.connectedSubject.asObservable();

  constructor(
    private readonly auth: AuthService,
    private readonly storage: TokenStorageService,
    private readonly zone: NgZone
  ) {
    auth.currentUser$.pipe(map(u => u?.id ?? null), distinctUntilChanged()).subscribe(userId => {
      if (userId) {
        this.start();
      } else {
        this.stop();
      }
    });
  }

  private start(): void {
    if (this.connection) {
      return;
    }
    const connection = buildHubConnection(`${environment.hubUrl}/notifications`, () => this.storage.getToken(), () => this.sessionExpired());
    const mine = (fn: () => void) => () => this.zone.run(() => {
      if (this.connection === connection) {
        fn();
      }
    });
    connection.on('UnreadCountChanged', (groupId: string, unreadCount: number) =>
      this.zone.run(() => this.changedSubject.next({ groupId, unreadCount })));
    connection.onreconnected(mine(() => this.connectedSubject.next()));
    // Hết lượt tự kết nối lại → tiếp tục thử thưa (không có nút "Thử lại" cho hub này).
    connection.onclose(mine(() => this.connect(connection, 3)));

    this.connection = connection;
    this.connect(connection, 0);
  }

  private connect(connection: HubConnection, attempt: number): void {
    connection.start()
      .then(() => this.zone.run(() => {
        if (this.connection === connection) {
          this.connectedSubject.next();
        }
      }))
      .catch(err => this.zone.run(() => {
        if (this.connection !== connection) {
          return; // đã đăng xuất
        }
        if (!this.storage.getToken()) {
          this.sessionExpired();
          return;
        }
        console.warn('[notification-hub] Kết nối thất bại, sẽ thử lại', err);
        this.retryTimer = setTimeout(() => this.connect(connection, attempt + 1), Math.min(30000, 2000 * 2 ** attempt));
      }));
  }

  private sessionExpired(): void {
    if (this.connection) {
      this.zone.run(() => this.auth.logout());
    }
  }

  private stop(): void {
    clearTimeout(this.retryTimer);
    const connection = this.connection;
    this.connection = null;
    connection?.stop();
  }
}
