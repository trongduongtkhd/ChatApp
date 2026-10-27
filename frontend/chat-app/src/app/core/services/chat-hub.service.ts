import { Injectable, NgZone } from '@angular/core';
import { HubConnection, HubConnectionState } from '@microsoft/signalr';
import { BehaviorSubject, Observable, Subject } from 'rxjs';
import { distinctUntilChanged, filter, map, take } from 'rxjs/operators';
import { environment } from '../../../environments/environment';
import { Message } from '../models/message';
import { PresenceSnapshot, TypingNotice } from '../models/presence';
import { buildHubConnection } from '../utils/hub-connection';
import { AuthService } from './auth.service';
import { TokenStorageService } from './token-storage.service';

export type HubState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

// none: bình thường | reconnecting: dải vàng | closed: dải vàng + nút "Thử lại" | reconnected: dải xanh 2 giây
export type ConnectionBanner = 'none' | 'reconnecting' | 'closed' | 'reconnected';

// Chờ server trả lời SendMessage tối đa 10 giây. Hết giờ KHÔNG có nghĩa là server chưa lưu
// (có thể lưu rồi mà câu trả lời bị trễ/mất) → "Gửi lại" với CÙNG messageId, server trả lại tin cũ.
const SEND_TIMEOUT_MS = 10000;

// Lỗi gửi tin. fromServer = server từ chối có chủ đích (HubException: không phải thành viên, quá dài...)
// → hiện nguyên thông báo dưới ô nhập. Ngược lại là lỗi mạng / hết giờ.
export class SendError extends Error {
  constructor(message: string, readonly fromServer: boolean) {
    super(message);
  }
}

// Adapter cho khung chat: trạng thái toàn cục, giới hạn hiển thị bằng thành viên của phòng.
export interface GroupPresence {
  groupId: string;
  online: ReadonlySet<string>;
}

// Kết nối SignalR /hubs/chat. providedIn: 'root' → cả app chỉ có MỘT kết nối (DESIGN 2b).
// Tự kết nối khi đăng nhập, tự ngắt khi đăng xuất (nghe AuthService.currentUser$).
// Component không đụng HubConnection: chỉ nhận Observable và gọi joinGroup/leaveGroup.
@Injectable({ providedIn: 'root' })
export class ChatHubService {
  private connection: HubConnection | null = null;
  private startTimer?: ReturnType<typeof setTimeout>;
  private currentGroup: string | null = null;

  private everConnected = false;  // đã từng kết nối thành công (để biết lần sau là "kết nối LẠI")
  private bannerTimer?: ReturnType<typeof setTimeout>;

  private readonly stateSubject = new BehaviorSubject<HubState>('disconnected');
  private readonly bannerSubject = new BehaviorSubject<ConnectionBanner>('none');
  private readonly messageSubject = new Subject<Message>();
  private readonly presenceSubject = new BehaviorSubject<GroupPresence | null>(null);
  private readonly reconnectedSubject = new Subject<void>();
  private readonly activitySubject = new BehaviorSubject<PresenceSnapshot | null>(null);
  private readonly typingSubject = new BehaviorSubject<TypingNotice[]>([]);
  private readonly revokedSubject = new Subject<string>();
  private heartbeatTimer?: ReturnType<typeof setTimeout>;
  private staleTimer?: ReturnType<typeof setTimeout>;
  private typingTimer?: ReturnType<typeof setInterval>;
  private readonly typers = new Map<string, { notice: TypingNotice; until: number }>();
  private heartbeatSeconds = 10;
  private readonly pulses = new WeakSet<HubConnection>();
  private snapshotVersion = 0;
  private lastTypingAt = 0;
  readonly activity$ = this.activitySubject.asObservable();
  readonly typing$ = this.typingSubject.asObservable();
  readonly revoked$ = this.revokedSubject.asObservable();

  get activity(): PresenceSnapshot | null { return this.activitySubject.value; }

  setTyping(groupId: string, active: boolean): void {
    if (groupId !== this.currentGroup || this.connection?.state !== HubConnectionState.Connected) return;
    if (active && Date.now() - this.lastTypingAt < 2000) return;
    this.lastTypingAt = active ? Date.now() : 0;
    this.invokeSafe('SetTyping', groupId, active);
  }

  refreshActivity(): void {
    const connection = this.connection;
    if (connection?.state === HubConnectionState.Connected) this.pulse(connection);
  }

  readonly state$: Observable<HubState> = this.stateSubject.pipe(distinctUntilChanged());
  // Dải trạng thái trên giao diện. Giữ ở service (không ở component) để đổi nhóm không làm mất trạng thái.
  readonly banner$: Observable<ConnectionBanner> = this.bannerSubject.pipe(distinctUntilChanged());
  readonly messages$: Observable<Message> = this.messageSubject.asObservable();
  readonly presence$: Observable<GroupPresence | null> = this.presenceSubject.asObservable();
  // Kết nối lại thành công (ConnectionId MỚI). SignalR KHÔNG gửi bù tin lỡ trong lúc mất kết nối →
  // ai nghe sự kiện này phải tự lấy bù qua REST (chat-window: lịch sử, chat-layout: tin cuối).
  readonly reconnected$: Observable<void> = this.reconnectedSubject.asObservable();

  constructor(
    private readonly auth: AuthService,
    private readonly storage: TokenStorageService,
    private readonly zone: NgZone
  ) {
    document.addEventListener('visibilitychange', () => {
      if (!document.hidden) this.refreshActivity();
    });
    window.addEventListener('online', () => this.refreshActivity());
    auth.currentUser$.pipe(map(u => u?.id ?? null), distinctUntilChanged()).subscribe(userId => {
      if (userId) {
        this.start();
      } else {
        this.stop();
      }
    });
  }

  // Vào phòng của nhóm đang mở (rời phòng cũ nếu có). Chưa kết nối xong thì ghi nhớ,
  // kết nối xong sẽ tự JoinGroup (xem onConnected).
  joinGroup(groupId: string): void {
    if (this.currentGroup && this.currentGroup !== groupId) {
      this.setTyping(this.currentGroup, false);
      this.invokeSafe('LeaveGroup', this.currentGroup);
    }
    this.currentGroup = groupId;
    this.clearTyping();
    this.publishGroupPresence();
    if (this.connection?.state === HubConnectionState.Connected) {
      this.invokeJoin(groupId);
    }
  }

  // Gửi tin. Đang kết nối / kết nối lại → tin CHỜ (hàng đợi), có kết nối thì tự gửi.
  // Rớt kết nối giữa lúc đang gửi → chờ nối lại rồi gửi lại cùng messageId (server idempotent, Phần 7).
  // Kết quả trả về trong NgZone để component cập nhật giao diện được ngay.
  sendMessage(messageId: string, groupId: string, content: string): Promise<Message> {
    return new Promise<Message>((resolve, reject) => {
      this.sendLoop(messageId, groupId, content).then(
        m => this.zone.run(() => resolve(m)),
        e => this.zone.run(() => reject(e))
      );
    });
  }

  // Nút "Thử lại" khi đã hết lượt tự kết nối lại (onclose) hoặc lần kết nối đầu đang chờ thử lại.
  retryNow(): void {
    const connection = this.connection;
    if (connection && connection.state === HubConnectionState.Disconnected) {
      clearTimeout(this.startTimer);
      this.connect(connection, 0);
    }
  }

  leaveGroup(groupId: string): void {
    if (this.currentGroup !== groupId) {
      return;
    }
    this.setTyping(groupId, false);
    this.clearTyping();
    this.currentGroup = null;
    this.presenceSubject.next(null);
    this.invokeSafe('LeaveGroup', groupId);
  }

  private start(): void {
    if (this.connection) {
      return;
    }
    // skipNegotiation + WebSocket, token qua accessTokenFactory (hết hạn → sessionExpired), withAutomaticReconnect.
    const connection = buildHubConnection(`${environment.hubUrl}/chat`, () => this.storage.getToken(), () => this.sessionExpired());

    // Callback của SignalR: đưa về NgZone để Angular biết mà vẽ lại giao diện.
    connection.on('ReceiveMessage', (m: Message) => this.zone.run(() => {
      if (this.connection === connection) this.messageSubject.next(m);
    }));
    connection.on('PresenceSnapshot', (snapshot: PresenceSnapshot) => this.zone.run(() => {
      if (this.connection !== connection || snapshot.version <= this.snapshotVersion) return;
      this.snapshotVersion = snapshot.version;
      this.heartbeatSeconds = snapshot.heartbeatSeconds;
      this.activitySubject.next(snapshot);
      this.publishGroupPresence();
      clearTimeout(this.staleTimer);
      this.zone.runOutsideAngular(() => {
        this.staleTimer = setTimeout(() => this.zone.run(() => this.markUnknown()), snapshot.leaseSeconds * 1000);
      });
    }));
    connection.on('TypingChanged', (groupId: string, userId: string, displayName: string, isTyping: boolean) => this.zone.run(() => {
      if (this.connection !== connection || groupId !== this.currentGroup || userId === this.auth.currentUser?.id) return;
      if (isTyping) this.typers.set(userId, { notice: { groupId, userId, displayName, isTyping }, until: Date.now() + 5000 });
      else this.typers.delete(userId);
      this.emitTyping();
    }));
    connection.on('GroupAccessRevoked', (groupId: string) => this.zone.run(() => {
      if (this.connection !== connection) return;
      if (this.currentGroup === groupId) this.leaveGroup(groupId);
      this.revokedSubject.next(groupId);
    }));
    // Bỏ qua callback của kết nối cũ (vd onclose do chính stop() khi đăng xuất gây ra).
    const mine = (fn: () => void) => () => this.zone.run(() => {
      if (this.connection === connection) {
        fn();
      }
    });
    connection.onreconnecting(mine(() => {
      this.setState('reconnecting');
      this.probeSession();
    }));
    // Reconnect = kết nối MỚI, ConnectionId MỚI → server không còn nhớ phòng cũ → phải JoinGroup lại.
    connection.onreconnected(mine(() => this.onConnected()));
    // Hết lượt thử (0, 2, 10, 30 giây) → đóng hẳn. Không tự thử nữa: chờ người dùng bấm "Thử lại".
    connection.onclose(mine(() => {
      this.setState('disconnected');
      this.probeSession();
    }));

    this.connection = connection;
    this.connect(connection, 0);
  }

  // Lần kết nối ĐẦU TIÊN thất bại thì withAutomaticReconnect không lo (nó chỉ lo khi đang chạy bị đứt)
  // → tự thử lại, giãn dần tới 30 giây, tới khi được hoặc đã đăng xuất.
  private connect(connection: HubConnection, attempt: number): void {
    this.setState('connecting');
    connection.start()
      .then(() => this.zone.run(() => {
        if (this.connection === connection) this.onConnected();
      }))
      .catch(err => this.zone.run(() => {
        if (this.connection !== connection) {
          return; // đã đăng xuất trong lúc đang kết nối (kể cả do accessTokenFactory báo hết hạn)
        }
        if (!this.storage.getToken()) {
          this.sessionExpired(); // hết phiên: thử lại vô ích
          return;
        }
        console.warn('[chat-hub] Kết nối thất bại, sẽ thử lại', err);
        this.setState('disconnected');
        this.probeSession();
        const delay = Math.min(30000, 2000 * 2 ** attempt);
        this.startTimer = setTimeout(() => this.connect(connection, attempt + 1), delay);
      }));
  }

  // Hết phiên đăng nhập → đăng xuất, về trang đăng nhập. logout() phát currentUser = null → stop() ở trên
  // hủy kết nối + hẹn giờ thử lại. Gọi nhiều lần vô hại (lần sau connection đã null).
  private sessionExpired(): void {
    if (this.connection) {
      this.zone.run(() => this.auth.logout());
    }
  }

  // Kết nối WebSocket hỏng nhưng KHÔNG biết vì sao: trình duyệt không cho đọc mã HTTP (401 hay 502) của
  // bước bắt tay WebSocket. → Hỏi lại bằng một request REST nhẹ có gắn token:
  // - 401 → ErrorInterceptor tự logout() (token bị sửa, server đổi secret...): lỗi VĨNH VIỄN, thử lại vô ích;
  // - 200 / 502 / 503 / mất mạng → không làm gì: lỗi TẠM THỜI của chat-service, để cơ chế thử lại lo.
  private probeSession(): void {
    this.auth.me().subscribe({ error: () => undefined });
  }

  private onConnected(): void {
    const isReconnect = this.everConnected;
    this.setState('connected');
    this.snapshotVersion = 0;
    this.markUnknown();
    this.pulse(this.connection!);
    clearInterval(this.typingTimer);
    this.zone.runOutsideAngular(() => {
      this.typingTimer = setInterval(() => this.zone.run(() => this.emitTyping()), 1000);
    });
    if (this.currentGroup) {
      this.invokeJoin(this.currentGroup);
    }
    if (isReconnect) {
      this.reconnectedSubject.next();
    }
  }

  // Mọi thay đổi trạng thái đi qua đây để tính luôn dải trạng thái (banner).
  private setState(state: HubState): void {
    if (state !== 'connected') {
      clearTimeout(this.heartbeatTimer);
      clearInterval(this.typingTimer);
      this.markUnknown();
      this.clearTyping();
    }
    this.stateSubject.next(state);
    const banner = this.bannerSubject.value;
    clearTimeout(this.bannerTimer);
    switch (state) {
      case 'connected':
        this.everConnected = true;
        if (banner === 'reconnecting' || banner === 'closed') {
          this.bannerSubject.next('reconnected');
          this.bannerTimer = setTimeout(() => this.zone.run(() => this.bannerSubject.next('none')), 2000);
        } else {
          this.bannerSubject.next('none');
        }
        break;
      case 'reconnecting':
        this.bannerSubject.next('reconnecting');
        break;
      case 'connecting':
        // Bấm "Thử lại" → đổi sang dải "Đang kết nối lại…" trong lúc thử.
        if (banner === 'closed') {
          this.bannerSubject.next('reconnecting');
        }
        break;
      case 'disconnected':
        this.bannerSubject.next('closed');
        break;
    }
  }

  private stop(): void {
    clearTimeout(this.heartbeatTimer);
    clearTimeout(this.staleTimer);
    clearInterval(this.typingTimer);
    this.clearTyping();
    this.activitySubject.next(null);
    clearTimeout(this.startTimer);
    clearTimeout(this.bannerTimer);
    const connection = this.connection;
    this.connection = null;
    this.currentGroup = null;
    this.everConnected = false;
    this.presenceSubject.next(null);
    this.stateSubject.next('disconnected');
    this.bannerSubject.next('none'); // đăng xuất là chủ động, không phải sự cố → không hiện dải
    // Kết nối giữ JWT đã xác thực nên vẫn thu hồi được dù storage vừa bị xóa khi logout.
    if (connection?.state === HubConnectionState.Connected) {
      withTimeout(connection.invoke('EndSession'), 2500).catch(() => undefined).then(() => connection.stop());
    } else {
      connection?.stop();
    }
  }

  // Vào phòng chỉ đăng ký nhận tin; trạng thái online lấy qua PresenceSnapshot.
  private invokeJoin(groupId: string): void {
    this.connection!.invoke('JoinGroup', groupId)
      .then(() => this.zone.run(() => {
        if (this.currentGroup === groupId) {
          this.publishGroupPresence();
        }
      }))
      // Không phải thành viên → HubException. Màn 403 đã do REST lịch sử báo, ở đây chỉ ghi log.
      .catch(err => console.warn('[chat-hub] JoinGroup thất bại', err));
  }

  private publishGroupPresence(): void {
    const snapshot = this.activitySubject.value;
    this.presenceSubject.next(this.currentGroup ? {
      groupId: this.currentGroup,
      online: new Set(snapshot?.known ? snapshot.onlineUserIds : [])
    } : null);
  }

  private markUnknown(): void {
    const previous = this.activitySubject.value;
    this.activitySubject.next(previous ? { ...previous, known: false, onlineUserIds: [], onlineCountsByGroup: {} } : null);
    this.publishGroupPresence();
  }

  private pulse(connection: HubConnection): void {
    if (this.connection !== connection || connection.state !== HubConnectionState.Connected || this.pulses.has(connection)) return;
    clearTimeout(this.heartbeatTimer);
    this.pulses.add(connection);
    withTimeout(connection.invoke('Heartbeat'), 10000)
      .catch(() => this.zone.run(() => {
        if (this.connection === connection) this.markUnknown();
      }))
      .finally(() => {
        this.pulses.delete(connection);
        if (this.connection === connection && connection.state === HubConnectionState.Connected) {
          this.zone.runOutsideAngular(() => {
            this.heartbeatTimer = setTimeout(() => this.pulse(connection), this.heartbeatSeconds * 1000);
          });
        }
      });
  }

  private clearTyping(): void {
    this.typers.clear();
    this.typingSubject.next([]);
  }

  private emitTyping(): void {
    for (const [id, entry] of this.typers) {
      if (entry.until <= Date.now()) this.typers.delete(id);
    }
    this.typingSubject.next(Array.from(this.typers.values()).map(t => t.notice));
  }

  private async sendLoop(messageId: string, groupId: string, content: string): Promise<Message> {
    for (;;) {
      const connection = await this.waitConnected();
      try {
        return await withTimeout(connection.invoke<Message>('SendMessage', messageId, groupId, content), SEND_TIMEOUT_MS);
      } catch (err) {
        if (err instanceof SendError) {
          throw err; // hết giờ
        }
        // Kết nối đứt khi đang chờ trả lời: SignalR hủy lời gọi và chuyển sang Reconnecting → vòng lại, chờ, gửi lại.
        if (connection.state === HubConnectionState.Reconnecting) {
          continue;
        }
        throw toSendError(err);
      }
    }
  }

  // Đợi tới khi Connected (trả về kết nối) hoặc kết nối hỏng hẳn (disconnected → lỗi, không chờ vô hạn).
  private async waitConnected(): Promise<HubConnection> {
    const state = await this.stateSubject.pipe(filter(s => s === 'connected' || s === 'disconnected'), take(1)).toPromise();
    if (state === 'disconnected' || !this.connection) {
      throw new SendError('Mất kết nối tới máy chủ.', false);
    }
    return this.connection;
  }

  private invokeSafe(method: string, ...args: unknown[]): void {
    if (this.connection?.state === HubConnectionState.Connected) {
      this.connection.invoke(method, ...args).catch(err => console.warn(`[chat-hub] ${method} thất bại`, err));
    }
  }
}

function withTimeout<T>(promise: Promise<T>, ms: number): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const timer = setTimeout(() => reject(new SendError('Máy chủ không trả lời.', false)), ms);
    promise.then(
      v => { clearTimeout(timer); resolve(v); },
      e => { clearTimeout(timer); reject(e); }
    );
  });
}

// Server ném HubException → client nhận Error với message
// "An unexpected error occurred invoking 'SendMessage' on the server. HubException: <thông báo>".
// Chỉ HubException mới lộ thông báo ra client (lỗi khác bị server giấu, chỉ còn câu chung chung).
function toSendError(err: unknown): SendError {
  const text = err instanceof Error ? err.message : String(err);
  const match = /HubException: (.*)$/.exec(text);
  return match ? new SendError(match[1], true) : new SendError('Không gửi được.', false);
}
