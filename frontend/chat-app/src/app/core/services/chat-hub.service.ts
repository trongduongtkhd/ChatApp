import { Injectable, NgZone } from '@angular/core';
import { HubConnection, HubConnectionState } from '@microsoft/signalr';
import { BehaviorSubject, Observable, Subject } from 'rxjs';
import { distinctUntilChanged, filter, map, take } from 'rxjs/operators';
import { environment } from '../../../environments/environment';
import { Message } from '../models/message';
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

// Ai đang online trong nhóm đang mở (snapshot từ JoinGroup + cập nhật bằng UserPresenceChanged).
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
      this.invokeSafe('LeaveGroup', this.currentGroup);
    }
    this.currentGroup = groupId;
    this.presenceSubject.next({ groupId, online: new Set() });
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
    connection.on('ReceiveMessage', (m: Message) => this.zone.run(() => this.messageSubject.next(m)));
    connection.on('UserPresenceChanged', (userId: string, isOnline: boolean) =>
      this.zone.run(() => this.applyPresence(userId, isOnline)));
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
      .then(() => this.zone.run(() => this.onConnected()))
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
    if (this.currentGroup) {
      this.invokeJoin(this.currentGroup);
    }
    if (isReconnect) {
      this.reconnectedSubject.next();
    }
  }

  // Mọi thay đổi trạng thái đi qua đây để tính luôn dải trạng thái (banner).
  private setState(state: HubState): void {
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
    clearTimeout(this.startTimer);
    clearTimeout(this.bannerTimer);
    const connection = this.connection;
    this.connection = null;
    this.currentGroup = null;
    this.everConnected = false;
    this.presenceSubject.next(null);
    this.stateSubject.next('disconnected');
    this.bannerSubject.next('none'); // đăng xuất là chủ động, không phải sự cố → không hiện dải
    connection?.stop();
  }

  // JoinGroup trả về danh sách userId thành viên đang online (snapshot ban đầu, Phần 7).
  private invokeJoin(groupId: string): void {
    this.connection!.invoke<string[]>('JoinGroup', groupId)
      .then(ids => this.zone.run(() => {
        if (this.currentGroup === groupId) {
          this.presenceSubject.next({ groupId, online: new Set(ids) });
        }
      }))
      // Không phải thành viên → HubException. Màn 403 đã do REST lịch sử báo, ở đây chỉ ghi log.
      .catch(err => console.warn('[chat-hub] JoinGroup thất bại', err));
  }

  // Sau JoinGroup chỉ nhận THAY ĐỔI (delta). Presence là của user (còn ≥ 1 kết nối), không riêng nhóm nào.
  private applyPresence(userId: string, isOnline: boolean): void {
    const current = this.presenceSubject.value;
    if (!current) {
      return;
    }
    const online = new Set(current.online); // Set mới → component OnPush nhận ra thay đổi
    if (isOnline) {
      online.add(userId);
    } else {
      online.delete(userId);
    }
    this.presenceSubject.next({ groupId: current.groupId, online });
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
