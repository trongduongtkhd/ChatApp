import { HttpErrorResponse } from '@angular/common/http';
import { Component, ElementRef, OnDestroy, OnInit } from '@angular/core';
import { FormControl } from '@angular/forms';
import { forkJoin, Observable, of, Subscription } from 'rxjs';
import { catchError, debounceTime, distinctUntilChanged, finalize, map, switchMap } from 'rxjs/operators';
import { Friend, FriendRequest } from '../../../../core/models/friend';
import { UserSnapshot } from '../../../../core/models/group';
import { AuthService } from '../../../../core/services/auth.service';
import { FriendApiService } from '../../../../core/services/friend-api.service';
import { GroupApiService } from '../../../../core/services/group-api.service';

type Tab = 'friends' | 'incoming' | 'outgoing';
// Quan hệ của mình với một người trong kết quả tìm kiếm (tính từ 3 danh sách đã tải, không gọi thêm API).
type Relation = 'self' | 'friend' | 'incoming' | 'outgoing' | 'none';

// Trang Danh bạ (Phần 11): tìm người để mời + 3 tab Bạn bè / Lời mời / Đã gửi.
// Dữ liệu bạn bè là của identity-service; ô tìm người dùng lại API tìm của group-service (bản sao user_snapshots).
// Sau MỖI thao tác thành công → nạp lại cả 3 danh sách: một nguồn sự thật (server), không tự sửa mảng ở client
// (vd mời đúng lúc người kia cũng mời mình → server trả 409 "Người này đã mời bạn", nạp lại sẽ thấy ở tab Lời mời).
@Component({
  selector: 'app-contacts-page',
  templateUrl: './contacts-page.component.html'
})
export class ContactsPageComponent implements OnInit, OnDestroy {
  readonly myId = this.auth.currentUser?.id ?? '';
  readonly tabs: { id: Tab; label: string }[] = [
    { id: 'friends', label: 'Bạn bè' },
    { id: 'incoming', label: 'Lời mời' },
    { id: 'outgoing', label: 'Đã gửi' }
  ];
  tab: Tab = 'friends';

  friends: Friend[] = [];
  incoming: FriendRequest[] = [];
  outgoing: FriendRequest[] = [];
  loading = true;
  loadError = false;

  readonly query = new FormControl('');
  results: UserSnapshot[] = [];
  searching = false;

  busyId: string | null = null;            // userId đang có thao tác chạy → khóa nút của dòng đó
  // waiting: đang chờ chat riêng xuất hiện; link: mã chat riêng đã sẵn sàng → hiện nút "Nhắn tin".
  notice: { kind: 'ok' | 'error'; text: string; waiting?: boolean; link?: string } | null = null;

  confirmRemove: Friend | null = null;     // hộp xác nhận Hủy kết bạn
  removing = false;
  removeError: string | null = null;

  private readonly subs = new Subscription();

  constructor(
    private readonly friendApi: FriendApiService,
    private readonly groupApi: GroupApiService,
    private readonly auth: AuthService,
    private readonly host: ElementRef<HTMLElement>
  ) {}

  ngOnInit(): void {
    this.reload();
    this.subs.add(this.query.valueChanges.pipe(
      map((q: string) => q.trim()),
      debounceTime(300),
      distinctUntilChanged(),
      switchMap(q => (q ? this.search(q) : of([] as UserSnapshot[])))
    ).subscribe(users => (this.results = users.filter(u => u.userId !== this.myId))));
  }

  ngOnDestroy(): void {
    this.subs.unsubscribe();
  }

  // Tải song song 3 danh sách (cùng một service). Lỗi → hộp lỗi + nút Thử lại, giữ dữ liệu cũ.
  reload(): void {
    this.loadError = false;
    this.subs.add(forkJoin([this.friendApi.list(), this.friendApi.incoming(), this.friendApi.outgoing()])
      .pipe(finalize(() => (this.loading = false)))
      .subscribe({
        next: ([friends, incoming, outgoing]) => {
          this.friends = friends;
          this.incoming = incoming;
          this.outgoing = outgoing;
        },
        error: () => (this.loadError = true)
      }));
  }

  selectTab(tab: Tab): void {
    this.tab = tab;
  }

  // Bàn phím cho tablist (mẫu ARIA, giống group-list): ← → chuyển tab và chuyển focus theo.
  onTabKey(e: KeyboardEvent, index: number): void {
    const step = e.key === 'ArrowRight' ? 1 : e.key === 'ArrowLeft' ? -1 : 0;
    if (!step) {
      return;
    }
    e.preventDefault();
    const next = (index + step + this.tabs.length) % this.tabs.length;
    this.selectTab(this.tabs[next].id);
    this.host.nativeElement.querySelectorAll<HTMLElement>('[role="tab"]')[next]?.focus();
  }

  relationOf(u: UserSnapshot): Relation {
    if (u.userId === this.myId) {
      return 'self';
    }
    if (this.friends.some(f => f.userId === u.userId)) {
      return 'friend';
    }
    if (this.incoming.some(r => r.userId === u.userId)) {
      return 'incoming';
    }
    return this.outgoing.some(r => r.userId === u.userId) ? 'outgoing' : 'none';
  }

  invite(u: UserSnapshot): void {
    this.run(u.userId, this.friendApi.invite(u.userId), `Đã gửi lời mời tới ${u.displayName}.`, () => (this.tab = 'outgoing'));
  }

  // Chấp nhận → identity trả 200 NGAY, nhưng chat riêng do group-service tạo SAU khi nhận sự kiện qua Kafka (~1 s).
  // Hỏi GET /api/groups mỗi giây (tối đa 5 lần) tới khi thấy directGroupId → mới hiện nút "Nhắn tin"
  // (bấm sớm hơn thì chat-window nhận 403 vì mình chưa là thành viên) – eventual consistency hiện ra trên giao diện.
  accept(r: { userId: string; displayName: string }): void {
    this.busyId = r.userId;
    this.notice = null;
    this.friendApi.accept(r.userId).pipe(finalize(() => (this.busyId = null))).subscribe({
      next: friend => {
        this.tab = 'friends';
        this.reload();
        const text = `Bạn và ${r.displayName} đã là bạn bè.`;
        this.notice = { kind: 'ok', text: `${text} Đang tạo cuộc trò chuyện riêng…`, waiting: true };
        this.subs.add(this.groupApi.waitForGroup(friend.directGroupId).subscribe(found => {
          this.notice = found
            ? { kind: 'ok', text: `${text} Cuộc trò chuyện riêng đã sẵn sàng.`, link: friend.directGroupId }
            : { kind: 'error', text: `${text} Cuộc trò chuyện riêng chưa sẵn sàng (hệ thống đang chậm), thử “Nhắn tin” lại sau ít giây.` };
        }));
      },
      error: (err: HttpErrorResponse) => {
        this.notice = { kind: 'error', text: this.messageOf(err) };
        this.reload();
      }
    });
  }

  decline(r: FriendRequest): void {
    this.run(r.userId, this.friendApi.decline(r.userId), `Đã từ chối lời mời của ${r.displayName}.`);
  }

  cancel(r: FriendRequest): void {
    this.run(r.userId, this.friendApi.cancel(r.userId), `Đã hủy lời mời gửi ${r.displayName}.`);
  }

  askRemove(f: Friend): void {
    this.confirmRemove = f;
    this.removeError = null;
  }

  doRemove(): void {
    const f = this.confirmRemove;
    if (!f) {
      return;
    }
    this.removing = true;
    this.removeError = null;
    this.friendApi.remove(f.userId).pipe(finalize(() => (this.removing = false))).subscribe({
      next: () => {
        this.confirmRemove = null;
        this.notice = { kind: 'ok', text: `Đã hủy kết bạn với ${f.displayName}.` };
        this.reload();
      },
      error: (err: HttpErrorResponse) => {
        // 404 = đã không còn là bạn (vd người kia vừa hủy trước) → đóng hộp, nạp lại cho khớp server.
        if (err.status === 404) {
          this.confirmRemove = null;
          this.notice = { kind: 'error', text: this.messageOf(err) };
          this.reload();
        } else {
          this.removeError = this.messageOf(err);
        }
      }
    });
  }

  trackByUser(_: number, x: { userId: string }): string {
    return x.userId;
  }

  // Chạy một thao tác trên một dòng: khóa nút dòng đó, xong thì báo + nạp lại 3 danh sách (cả khi lỗi:
  // lỗi 409/404 thường do trạng thái ở server đã khác cái đang hiện → nạp lại để thấy đúng).
  private run(userId: string, call: Observable<unknown>, okText: string, onOk?: () => void): void {
    this.busyId = userId;
    this.notice = null;
    call.pipe(finalize(() => (this.busyId = null))).subscribe({
      next: () => {
        this.notice = { kind: 'ok', text: okText };
        onOk?.();
        this.reload();
      },
      error: (err: HttpErrorResponse) => {
        this.notice = { kind: 'error', text: this.messageOf(err) };
        this.reload();
      }
    });
  }

  private messageOf(err: HttpErrorResponse): string {
    return err.error?.title || (err.status === 0 ? 'Không kết nối được máy chủ.' : 'Không thực hiện được. Thử lại sau.');
  }

  private search(q: string): Observable<UserSnapshot[]> {
    this.searching = true;
    return this.groupApi.searchUsers(q).pipe(
      catchError(() => of([] as UserSnapshot[])),
      finalize(() => (this.searching = false))
    );
  }
}
