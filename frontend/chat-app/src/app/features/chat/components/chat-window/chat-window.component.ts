import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnDestroy } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { EMPTY, forkJoin, Observable, of, Subject, Subscription, throwError } from 'rxjs';
import { auditTime, catchError, expand, map, reduce, startWith, switchMap, tap } from 'rxjs/operators';
import { GroupDetail, groupTitle } from '../../../../core/models/group';
import { Message, PendingMessage } from '../../../../core/models/message';
import { AuthService } from '../../../../core/services/auth.service';
import { ChatHubService, SendError } from '../../../../core/services/chat-hub.service';
import { uuidv7 } from '../../../../core/utils/guid';
import { GroupApiService } from '../../../../core/services/group-api.service';
import { MessageApiService } from '../../../../core/services/message-api.service';
import { NotificationApiService } from '../../../../core/services/notification-api.service';

const PAGE_SIZE = 50;
const MAX_CATCHUP_PAGES = 10; // lấy bù tối đa 500 tin sau một lần mất kết nối

type ViewState = 'loading' | 'ready' | 'forbidden' | 'unavailable';

// Route con /chat/:groupId: đầu khung + danh sách tin + ô nhập.
// Lịch sử qua REST (keyset beforeSeq) + tin realtime qua SignalR + gửi tin có trạng thái Đang gửi / Đã gửi / Gửi lại.
@Component({
  selector: 'app-chat-window',
  templateUrl: './chat-window.component.html',
  styles: [`
    :host { display: flex; flex: 1; min-height: 0; min-width: 0; }
    .cw-col { flex: 999 1 420px; min-width: 0; display: flex; flex-direction: column; }
    app-member-panel { flex: 1 1 300px; max-width: 340px; min-width: 0; display: flex; }
    .ca-iconbtn.on { background: var(--brand-soft); color: var(--brand); }
  `]
})
export class ChatWindowComponent implements OnDestroy {
  readonly myId = this.auth.currentUser?.id ?? '';

  state: ViewState = 'loading';
  detail: GroupDetail | null = null;
  messages: Message[] = [];
  loadingOlder = false;
  olderError = false;
  reachedStart = false;

  onlineIds: ReadonlySet<string> = new Set();
  sendError: string | null = null;
  readonly banner$ = this.hub.banner$;
  showMembers = false;  // panel thành viên (giữ nguyên khi đổi nhóm, như Messenger)
  editing = false;      // hộp thoại sửa nhóm

  // Tin đang chờ theo TỪNG nhóm: đổi sang nhóm khác rồi quay lại vẫn thấy tin chờ / tin lỗi của nhóm đó.
  private readonly pendingByGroup = new Map<string, PendingMessage[]>();
  private readonly read$ = new Subject<void>();
  private readonly lastMarked = new Map<string, number>(); // seq đã gửi "đã đọc" gần nhất, theo nhóm
  private groupId = '';
  private readonly retry$ = new Subject<void>();
  private readonly sub = new Subscription();
  private olderSub?: Subscription;
  private catchUpSub?: Subscription;

  constructor(
    route: ActivatedRoute,
    private readonly router: Router,
    private readonly auth: AuthService,
    private readonly groupApi: GroupApiService,
    private readonly messageApi: MessageApiService,
    private readonly hub: ChatHubService,
    private readonly notificationApi: NotificationApiService
  ) {
    // Đổi nhóm (A → B): Angular DÙNG LẠI component, chỉ đổi :groupId → nghe paramMap.
    // switchMap hủy request của nhóm cũ nếu nó về muộn. Bấm "Thử lại" (retry$) = nạp lại nhóm hiện tại.
    this.sub.add(route.paramMap.pipe(
      map(p => p.get('groupId')!),
      switchMap(id => this.retry$.pipe(startWith(undefined), map(() => id))),
      switchMap(id => this.open(id))
    ).subscribe());

    // Tin realtime của nhóm đang mở. Gộp theo id + sắp theo seq: tin có thể đến SAI thứ tự (Phần 7: 200 tin
    // song song → 73 lần đảo), và có thể trùng với tin vừa lấy qua REST.
    // Tin của chính mình cũng về qua đây (server phát cho cả phòng), có khi TRƯỚC cả kết quả của SendMessage
    // → confirm() gộp theo id, bỏ bong bóng chờ, không hiện 2 lần.
    this.sub.add(hub.messages$.subscribe(m => this.confirm(m)));

    this.sub.add(hub.presence$.subscribe(p => {
      this.onlineIds = p && p.groupId === this.groupId ? p.online : new Set();
    }));

    // Đánh dấu đã đọc, GOM LÔ: auditTime(500) – trong 500 ms có 20 tin đến thì chỉ gửi 1 request (seq lớn nhất).
    this.sub.add(this.read$.pipe(auditTime(500)).subscribe(() => this.markRead()));

    // Kết nối lại: service đã tự JoinGroup lại (ConnectionId mới) và tự gửi các tin đang chờ;
    // ở đây lấy bù tin lỡ trong lúc mất kết nối (SignalR không gửi bù).
    this.sub.add(hub.reconnected$.subscribe(() => {
      if (this.state === 'ready') {
        this.catchUp();
      } else if (this.state === 'unavailable') {
        this.retry(); // lúc nãy mở nhóm thất bại vì hệ thống gián đoạn → có kết nối rồi thì tự mở lại
      }
    }));
  }

  ngOnDestroy(): void {
    this.sub.unsubscribe();
    this.olderSub?.unsubscribe();
    this.catchUpSub?.unsubscribe();
    this.hub.leaveGroup(this.groupId);
  }

  get memberCount(): number {
    return this.detail?.members.length ?? 0;
  }

  // Chat riêng 2 người (Phần 11). Lúc bị hủy kết bạn thì không còn chi tiết (server 403) → hỏi tập mã chat riêng
  // đã gặp trong phiên để màn 403 nói đúng "Hai bạn không còn là bạn bè".
  get isDirect(): boolean {
    return this.detail?.group.isDirect ?? this.groupApi.isKnownDirect(this.groupId);
  }

  get title(): string {
    return this.detail ? groupTitle(this.detail.group) : this.isDirect ? 'Trò chuyện riêng' : 'Nhóm';
  }

  // "Lan, Minh đang hoạt động": thành viên KHÁC mình đang online (chỉ biết tên khi có chi tiết nhóm).
  get activeNames(): string[] {
    return (this.detail?.members ?? [])
      .filter(m => m.userId !== this.myId && this.onlineIds.has(m.userId))
      .map(m => m.displayName || m.userName || 'Người dùng');
  }

  get pending(): PendingMessage[] {
    return this.pendingByGroup.get(this.groupId) ?? [];
  }

  // Bấm Gửi: sinh messageId TRƯỚC, hiện ngay bong bóng mờ (optimistic UI), rồi mới gửi.
  // Id có trước nên dù gửi lại bao nhiêu lần, server vẫn nhận ra là CÙNG một tin.
  send(content: string): void {
    const p: PendingMessage = { id: uuidv7(), groupId: this.groupId, content, status: 'sending' };
    this.setPending(p.groupId, [...this.pending, p]);
    this.deliver(p);
  }

  // "Gửi lại": cùng id, cùng nội dung. Lần trước thật ra đã lưu (chỉ mất câu trả lời) → server trả lại tin cũ.
  resend(id: string): void {
    const p = this.pending.find(x => x.id === id);
    if (p) {
      this.updatePending(p.groupId, id, 'sending');
      this.deliver(p);
    }
  }

  retry(): void {
    this.retry$.next();
  }

  retryConnection(): void {
    this.hub.retryNow();
  }

  // Thêm / xóa thành viên → lấy lại chi tiết (số thành viên, danh sách).
  // 403/404: mình vừa bị xóa khỏi nhóm hoặc nhóm vừa bị xóa (do người khác) → màn "không còn là thành viên".
  reloadDetail(): void {
    const groupId = this.groupId;
    this.groupApi.detail(groupId).subscribe({
      next: d => {
        if (groupId === this.groupId) {
          this.detail = d;
        }
      },
      error: (err: HttpErrorResponse) => {
        if (groupId === this.groupId && isForbidden(err)) {
          this.state = 'forbidden';
          this.groupApi.notifyChanged(); // thanh bên nạp lại → nhóm / chat riêng biến khỏi danh sách
        }
      }
    });
  }

  onGroupSaved(): void {
    this.editing = false;
    this.reloadDetail(); // tên/mô tả mới + version mới (lần sửa sau dùng version này)
  }

  // Tự rời nhóm hoặc xóa nhóm thành công → không còn xem được nhóm này. Thanh bên tự nạp lại nhờ GroupApiService.changed$.
  onGroupGone(): void {
    this.showMembers = false;
    this.router.navigate(['/chat']);
  }

  backToList(): void {
    this.router.navigate(['/chat']);
  }

  // Cuộn lên đầu → lấy trang cũ hơn: seq < seq nhỏ nhất đang có.
  loadOlder(): void {
    if (this.loadingOlder || this.reachedStart || !this.messages.length) {
      return;
    }
    const groupId = this.groupId;
    this.loadingOlder = true;
    this.olderError = false;
    this.olderSub = this.messageApi.history(groupId, this.messages[0].sequenceNumber, PAGE_SIZE).subscribe({
      next: page => {
        this.loadingOlder = false;
        this.messages = mergeBySeq(this.messages, page);
        // Ít hơn 1 trang (kể cả rỗng) = đã tới tin đầu tiên, khỏi gọi thêm một lần chỉ để nhận [].
        this.reachedStart = page.length < PAGE_SIZE;
      },
      error: (err: HttpErrorResponse) => {
        this.loadingOlder = false;
        if (err.status === 403) {
          this.state = 'forbidden'; // bị xóa khỏi nhóm trong lúc đang xem
        } else {
          this.olderError = true;
        }
      }
    });
  }

  // Lấy bù sau khi kết nối lại. API chỉ có beforeSeq (đi lùi về quá khứ), không có "afterSeq"
  // → lấy trang MỚI NHẤT, rồi lùi tiếp tới khi chạm seq lớn nhất đang có (known), tối đa MAX_CATCHUP_PAGES trang.
  // Gộp bằng mergeBySeq → tin đã có (hoặc vừa tới qua SignalR) không bị lặp.
  private catchUp(): void {
    const groupId = this.groupId;
    const known = this.messages.length ? this.messages[this.messages.length - 1].sequenceNumber : null;
    const before = this.messages.length;
    this.catchUpSub?.unsubscribe();
    this.catchUpSub = this.messageApi.history(groupId, undefined, PAGE_SIZE).pipe(
      // expand: mỗi trang nhận về lại quyết định có gọi trang cũ hơn không (đệ quy, tuần tự).
      expand((page, i) => known !== null && page.length === PAGE_SIZE
        && page[0].sequenceNumber > known + 1 && i + 1 < MAX_CATCHUP_PAGES
        ? this.messageApi.history(groupId, page[0].sequenceNumber, PAGE_SIZE)
        : EMPTY),
      reduce((all, page) => [...page, ...all], [] as Message[])
    ).subscribe({
      next: fetched => {
        if (known === null) {
          this.messages = mergeBySeq(this.messages, fetched);
          this.reachedStart = fetched.length < PAGE_SIZE;
        } else if (fetched.length === 0 || fetched[0].sequenceNumber <= known + 1) {
          this.messages = mergeBySeq(this.messages, fetched); // nối liền với đoạn đang có
        } else {
          // Lỡ quá nhiều (> MAX_CATCHUP_PAGES trang): giữa đoạn cũ và đoạn mới có khoảng trống không biết
          // → bỏ đoạn cũ, giữ đoạn mới nhất liền mạch; cuộn lên sẽ tải tiếp bằng beforeSeq như bình thường.
          this.messages = mergeBySeq([], fetched);
          this.reachedStart = false;
        }
        console.info(`[chat] Kết nối lại → lấy bù qua REST: ${fetched.length} tin, thêm mới ${Math.max(0, this.messages.length - before)}`);
        this.read$.next();
      },
      error: err => console.warn('[chat] Lấy bù tin thất bại', err)
    });
  }

  // POST /read với sequenceNumber LỚN NHẤT đang hiện (UI.md). Bỏ qua khi không có gì mới hơn lần trước.
  // Tin cuối là của mình → server đã tự coi mình đọc tới đó (Phần 9) → khỏi gửi.
  private markRead(): void {
    const groupId = this.groupId;
    const last = this.messages[this.messages.length - 1];
    if (this.state !== 'ready' || !last || last.sequenceNumber <= (this.lastMarked.get(groupId) ?? 0)) {
      return;
    }
    this.lastMarked.set(groupId, last.sequenceNumber);
    if (last.senderId === this.myId) {
      return;
    }
    this.notificationApi.markRead(groupId, last.sequenceNumber).subscribe({
      // 403: vừa được thêm vào nhóm, bản sao của notification-service chưa kịp có (~1 s) → lần sau thử lại.
      error: () => this.lastMarked.delete(groupId)
    });
  }

  private deliver(p: PendingMessage): void {
    this.sendError = null;
    this.hub.sendMessage(p.id, p.groupId, p.content).then(
      m => this.confirm(m),
      (err: SendError) => {
        this.updatePending(p.groupId, p.id, 'failed');
        if (p.groupId === this.groupId) {
          // HubException → nguyên văn lời server; lỗi mạng/hết giờ → chỉ hiện "Không gửi được · Gửi lại".
          this.sendError = err.fromServer ? err.message : null;
          // Server từ chối có chủ đích (vd "Bạn không phải thành viên nhóm này": bị xóa khỏi nhóm, bị hủy kết bạn)
          // → hỏi lại chi tiết; 403 thì chuyển sang màn "không còn là thành viên / không còn là bạn bè".
          if (err.fromServer) {
            this.reloadDetail();
          }
        }
      }
    );
  }

  // Tin đã có sequenceNumber (từ kết quả SendMessage HOẶC từ ReceiveMessage – cái nào đến trước cũng được).
  private confirm(m: Message): void {
    const list = this.pendingByGroup.get(m.groupId);
    if (list?.some(p => p.id === m.id)) {
      this.setPending(m.groupId, list.filter(p => p.id !== m.id));
    }
    if (m.groupId === this.groupId) {
      this.messages = mergeBySeq(this.messages, [m]);
      this.read$.next(); // đang mở nhóm này → tin mới coi như đã đọc (chấm đỏ không hiện cho nhóm đang xem)
    }
  }

  private setPending(groupId: string, list: PendingMessage[]): void {
    this.pendingByGroup.set(groupId, list);
  }

  private updatePending(groupId: string, id: string, status: PendingMessage['status']): void {
    const list = this.pendingByGroup.get(groupId) ?? [];
    this.setPending(groupId, list.map(p => (p.id === id ? { ...p, status } : p)));
  }

  // Mở một nhóm: lấy SONG SONG chi tiết (group-service) và trang tin mới nhất (chat-service).
  private open(groupId: string): Observable<unknown> {
    this.olderSub?.unsubscribe();
    this.catchUpSub?.unsubscribe();
    this.groupId = groupId;
    this.state = 'loading';
    this.detail = null;
    this.messages = [];
    this.loadingOlder = false;
    this.olderError = false;
    this.reachedStart = false;
    this.sendError = null;
    this.editing = false;

    // Vào phòng TRƯỚC khi lấy lịch sử: tin gửi trong lúc chờ REST vẫn tới qua SignalR rồi được gộp,
    // không lọt vào khe hở "REST đã chụp xong nhưng chưa vào phòng".
    this.hub.joinGroup(groupId);

    return forkJoin({
      // Chi tiết nhóm chỉ để hiện tên + số thành viên. group-service gián đoạn (5xx) mà chat-service vẫn
      // trả lịch sử thì vẫn cho đọc tin (không bắt cả màn hình chết theo một service phụ).
      detail: this.groupApi.detail(groupId).pipe(catchError((err: HttpErrorResponse) =>
        isForbidden(err) ? throwError(err) : of(null))),
      history: this.messageApi.history(groupId, undefined, PAGE_SIZE)
    }).pipe(
      tap(({ detail, history }) => {
        this.detail = detail;
        this.messages = mergeBySeq(this.messages, history); // giữ tin realtime đã đến trong lúc chờ
        this.reachedStart = history.length < PAGE_SIZE;
        this.state = 'ready';
        this.read$.next(); // mở nhóm → đánh dấu đã đọc tới tin mới nhất
      }),
      catchError((err: HttpErrorResponse) => {
        // 403: không phải thành viên (bị xóa / gõ URL nhóm người khác). 404: nhóm không tồn tại hoặc đã xóa.
        // Còn lại (0, 502, 503, 504): hệ thống gián đoạn, vd chat-service không hỏi được group-service qua gRPC.
        this.state = isForbidden(err) ? 'forbidden' : 'unavailable';
        return EMPTY;
      })
    );
  }
}

function isForbidden(err: HttpErrorResponse): boolean {
  return err.status === 403 || err.status === 404;
}

// Gộp theo id (tin trùng chỉ giữ 1) và sắp theo sequenceNumber tăng dần.
// KHÔNG giả định seq liên tục: seq có thể có lỗ (Phần 7: INCR bị bỏ phí khi gửi trùng / thử lại).
export function mergeBySeq(current: Message[], incoming: Message[]): Message[] {
  const byId = new Map<string, Message>();
  for (const m of current) {
    byId.set(m.id, m);
  }
  for (const m of incoming) {
    byId.set(m.id, m);
  }
  return Array.from(byId.values()).sort((a, b) => a.sequenceNumber - b.sequenceNumber);
}
