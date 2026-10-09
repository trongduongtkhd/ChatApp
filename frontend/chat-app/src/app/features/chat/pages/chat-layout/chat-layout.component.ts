import { Component, NgZone, OnDestroy, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { forkJoin, interval, Observable, of, Subscription } from 'rxjs';
import { catchError, map, switchMap } from 'rxjs/operators';
import { Group } from '../../../../core/models/group';
import { Message } from '../../../../core/models/message';
import { UnreadChange } from '../../../../core/models/unread-counter';
import { AuthService } from '../../../../core/services/auth.service';
import { ChatHubService } from '../../../../core/services/chat-hub.service';
import { GroupApiService } from '../../../../core/services/group-api.service';
import { MessageApiService } from '../../../../core/services/message-api.service';
import { NotificationApiService } from '../../../../core/services/notification-api.service';
import { NotificationHubService } from '../../../../core/services/notification-hub.service';
import { GroupListItem } from '../../components/group-list/group-list.component';

// Khung chính /chat: thanh bên (danh sách nhóm) + <router-outlet> cho route con /chat/:groupId.
// Giữ dữ liệu danh sách nhóm (tin cuối, số chưa đọc); group-list chỉ hiển thị.
@Component({
  selector: 'app-chat-layout',
  templateUrl: './chat-layout.component.html'
})
export class ChatLayoutComponent implements OnInit, OnDestroy {
  readonly myId = this.auth.currentUser?.id ?? '';

  items: GroupListItem[] = [];
  loading = true;
  loadError = false;
  showCreate = false;
  hasChat = false;          // đang có route con /chat/:groupId hay không
  now = Date.now();         // đổi mỗi phút → "2 phút" tự thành "3 phút"

  // Nhóm đang mở có thành viên KHÁC mình online → chấm xanh trên avatar nhóm đó ở danh sách.
  // Chỉ biết presence của nhóm đã JoinGroup (nhóm đang mở), các nhóm khác không có dữ liệu.
  onlineGroupId: string | null = null;
  readonly banner$ = this.hub.banner$;

  // Số chưa đọc theo nhóm. Để riêng (không chỉ nằm trong items) vì có thể về TRƯỚC danh sách nhóm.
  private unread = new Map<string, number>();
  private openGroupId: string | null = null; // nhóm đang mở (đang ở trong phòng SignalR)
  private readonly subs = new Subscription();

  constructor(
    private readonly auth: AuthService,
    private readonly groupApi: GroupApiService,
    private readonly messageApi: MessageApiService,
    private readonly notificationApi: NotificationApiService,
    // Inject ở đây để 2 hub được tạo (và tự kết nối) ngay khi vào /chat, trước khi mở nhóm nào.
    private readonly hub: ChatHubService,
    private readonly notificationHub: NotificationHubService,
    private readonly router: Router,
    private readonly zone: NgZone
  ) {}

  ngOnInit(): void {
    // Mở app: GET /api/groups + GET /api/notifications/unread (UI.md).
    this.load();
    this.refreshUnread();

    // Tin realtime (chỉ của phòng đang vào) → cập nhật "tin cuối" và đưa nhóm lên đầu danh sách.
    this.subs.add(this.hub.messages$.subscribe(m => this.updateLast(m)));
    this.subs.add(this.hub.presence$.subscribe(p => {
      this.openGroupId = p?.groupId ?? null;
      this.onlineGroupId = p && Array.from(p.online).some(id => id !== this.myId) ? p.groupId : null;
    }));
    // Chat hub kết nối lại: có thể đã lỡ tin ở bất kỳ nhóm nào → tải lại tin cuối + số chưa đọc.
    this.subs.add(this.hub.reconnected$.subscribe(() => {
      this.refreshLastMessages();
      this.refreshUnread();
    }));

    // Notification hub: đẩy số mới ngay; mỗi lần (re)connect thì lấy lại toàn bộ bằng REST
    // (SignalR không gửi bù UnreadCountChanged lỡ trong lúc mất kết nối).
    this.subs.add(this.notificationHub.unreadChanged$.subscribe(c => this.onUnreadChanged(c)));
    this.subs.add(this.notificationHub.connected$.subscribe(() => this.refreshUnread()));

    // Sửa tên / rời / xóa nhóm (ở panel thành viên) → nạp lại ngầm danh sách nhóm.
    this.subs.add(this.groupApi.changed$.subscribe(() => this.load(true)));

    // Đồng hồ chạy NGOÀI zone (không bắt Angular kiểm tra cả trang mỗi lần tick), chỉ vào zone để cập nhật.
    this.zone.runOutsideAngular(() => {
      this.subs.add(interval(60000).subscribe(() => this.zone.run(() => (this.now = Date.now()))));
    });
  }

  ngOnDestroy(): void {
    this.subs.unsubscribe();
  }

  get isEmpty(): boolean {
    return !this.loading && !this.loadError && this.items.length === 0;
  }

  // silent: nạp lại ngầm (không hiện "Đang tải…", lỗi thì giữ danh sách cũ) – dùng khi có nhóm mới.
  load(silent = false): void {
    if (!silent) {
      this.loading = true;
      this.loadError = false;
    }
    this.subs.add(this.groupApi.list().pipe(
      switchMap(groups => this.withLastMessages(groups))
    ).subscribe({
      next: items => {
        this.items = this.withUnread(sortByActivity(items));
        this.loading = false;
        this.loadError = false;
      },
      error: () => {
        if (!silent) {
          this.loadError = true;
          this.loading = false;
        }
      }
    }));
  }

  onCreated(group: Group): void {
    this.showCreate = false;
    if (!this.items.some(i => i.group.id === group.id)) {
      this.items = [{ group, last: null, unread: 0 }, ...this.items];
    }
    this.router.navigate(['/chat', group.id]);
  }

  retryConnection(): void {
    this.hub.retryNow();
  }

  private refreshUnread(): void {
    this.subs.add(this.notificationApi.unread().subscribe({
      next: list => {
        this.unread = new Map(list.map(c => [c.groupId, c.unreadCount]));
        this.items = this.withUnread(this.items);
      },
      error: err => console.warn('[unread] Không lấy được số chưa đọc', err)
    }));
  }

  private onUnreadChanged(c: UnreadChange): void {
    const before = this.unread.get(c.groupId) ?? 0;
    this.unread.set(c.groupId, c.unreadCount);
    if (!this.items.some(i => i.group.id === c.groupId)) {
      // Nhóm lạ: vừa được người khác thêm vào nhóm → nạp lại danh sách để nhóm mới hiện ra.
      if (!this.loading) {
        this.load(true);
      }
      return;
    }
    this.items = this.withUnread(this.items);
    // Có tin mới ở nhóm KHÔNG mở (không nhận ReceiveMessage của nhóm đó) → hỏi tin cuối bằng REST.
    if (c.unreadCount > before && c.groupId !== this.openGroupId) {
      this.subs.add(this.messageApi.history(c.groupId, undefined, 1).subscribe({
        next: msgs => msgs[0] && this.updateLast(msgs[0]),
        error: () => undefined
      }));
    }
  }

  // Gắn số chưa đọc vào từng dòng; dòng không đổi giữ nguyên object (OnPush không vẽ lại thừa).
  private withUnread(items: GroupListItem[]): GroupListItem[] {
    return items.map(i => {
      const unread = this.unread.get(i.group.id) ?? 0;
      return unread === i.unread ? i : { ...i, unread };
    });
  }

  private refreshLastMessages(): void {
    if (!this.items.length) {
      return;
    }
    this.subs.add(this.withLastMessages(this.items.map(i => i.group)).subscribe(fresh => {
      const lastById = new Map(fresh.map(f => [f.group.id, f.last]));
      // Lỗi khi lấy (last = null) thì giữ tin cuối cũ.
      this.items = sortByActivity(this.items.map(i => ({ ...i, last: lastById.get(i.group.id) || i.last })));
    }));
  }

  private updateLast(m: Message): void {
    const item = this.items.find(i => i.group.id === m.groupId);
    // Chỉ thay khi tin mới hơn tin cuối đang hiện (tin có thể đến sai thứ tự → so theo seq).
    if (!item || (item.last && item.last.sequenceNumber >= m.sequenceNumber)) {
      return;
    }
    this.items = sortByActivity(this.items.map(i => (i === item ? { ...i, last: m } : i)));
  }

  // Tin cuối của từng nhóm: chat-service không có API "tin cuối" → gọi lịch sử limit=1 cho TỪNG nhóm,
  // SONG SONG (forkJoin chờ tất cả). Nhóm nào lỗi (vd chat-service gián đoạn) thì chỉ nhóm đó không có
  // tin cuối, danh sách vẫn hiện (catchError riêng từng lời gọi).
  private withLastMessages(groups: Group[]): Observable<GroupListItem[]> {
    if (groups.length === 0) {
      return of([]); // forkJoin([]) hoàn tất mà KHÔNG phát giá trị nào → phải xử lý riêng
    }
    return forkJoin(groups.map(group => this.messageApi.history(group.id, undefined, 1).pipe(
      map((msgs): Message | null => msgs[0] ?? null),
      catchError(() => of(null)),
      map(last => ({ group, last, unread: 0 }))
    )));
  }
}

// Nhóm có tin mới nhất lên đầu; nhóm chưa có tin thì tính theo lúc tạo nhóm.
function sortByActivity(items: GroupListItem[]): GroupListItem[] {
  const key = (i: GroupListItem) => Date.parse(i.last?.createdAt ?? i.group.createdAt);
  return [...items].sort((a, b) => key(b) - key(a));
}
