import { HttpErrorResponse } from '@angular/common/http';
import { Component, EventEmitter, Input, OnDestroy, Output } from '@angular/core';
import { FormControl } from '@angular/forms';
import { Observable, of, Subscription } from 'rxjs';
import { catchError, debounceTime, distinctUntilChanged, finalize, map, switchMap } from 'rxjs/operators';
import { GroupDetail, Member, UserSnapshot } from '../../../../core/models/group';
import { GroupApiService } from '../../../../core/services/group-api.service';

type Confirm = { kind: 'remove'; member: Member } | { kind: 'leave' } | { kind: 'delete' };

// Panel thành viên (Members.dc.html): thông tin nhóm, danh sách thành viên + online, thêm/xóa (Owner),
// rời nhóm (Member), xóa nhóm (Owner). Tự gọi GroupApiService; xong thì báo chat-window nạp lại (changed)
// hoặc rời màn hình nhóm (gone).
@Component({
  selector: 'app-member-panel',
  templateUrl: './member-panel.component.html'
})
export class MemberPanelComponent implements OnDestroy {
  @Input() detail!: GroupDetail;
  @Input() myId = '';
  @Input() onlineIds: ReadonlySet<string> = new Set();
  @Output() readonly edit = new EventEmitter<void>();
  @Output() readonly changed = new EventEmitter<void>();
  @Output() readonly gone = new EventEmitter<void>();

  adding = false;
  readonly query = new FormControl('');
  results: UserSnapshot[] = [];
  searching = false;
  addError: string | null = null;
  addingId: string | null = null;

  confirm: Confirm | null = null;
  busy = false;
  confirmError: string | null = null;

  private readonly sub: Subscription;

  constructor(private readonly api: GroupApiService) {
    this.sub = this.query.valueChanges.pipe(
      map((q: string) => q.trim()),
      debounceTime(300),
      distinctUntilChanged(),
      switchMap(q => (q ? this.search(q) : of([])))
    ).subscribe(users => (this.results = users));
  }

  ngOnDestroy(): void {
    this.sub.unsubscribe();
  }

  get isOwner(): boolean {
    return this.detail.group.myRole === 'Owner';
  }

  // Trưởng nhóm lên đầu, rồi theo tên.
  get members(): Member[] {
    return [...this.detail.members].sort((a, b) =>
      (a.role === 'Owner' ? 0 : 1) - (b.role === 'Owner' ? 0 : 1) || this.nameOf(a).localeCompare(this.nameOf(b), 'vi'));
  }

  // displayName null = bản sao user_snapshots của group-service chưa có người này (eventual consistency).
  nameOf(m: Member): string {
    return m.displayName || m.userName || 'Đang đồng bộ…';
  }

  statusOf(m: Member): string {
    const online = this.onlineIds.has(m.userId) ? 'Đang hoạt động' : '';
    return m.userId === this.myId ? (online ? 'Bạn · ' + online : 'Bạn') : online;
  }

  isMember(u: UserSnapshot): boolean {
    return this.detail.members.some(m => m.userId === u.userId);
  }

  toggleAdd(): void {
    this.adding = !this.adding;
    this.addError = null;
    this.query.setValue('');
  }

  add(u: UserSnapshot): void {
    this.addError = null;
    this.addingId = u.userId;
    this.api.addMember(this.detail.group.id, u.userId).pipe(finalize(() => (this.addingId = null))).subscribe({
      next: () => this.changed.emit(),
      error: (err: HttpErrorResponse) => {
        this.addError = err.status === 409 ? `${u.displayName} đã ở trong nhóm.`
          : err.status === 404 ? `${u.displayName} chưa được đồng bộ sang group-service, thử lại sau ít giây.`
          : err.status === 403 ? 'Chỉ trưởng nhóm mới thêm được thành viên.'
          : 'Không thêm được. Thử lại sau.';
      }
    });
  }

  ask(c: Confirm): void {
    this.confirm = c;
    this.confirmError = null;
  }

  get confirmTitle(): string {
    return !this.confirm ? '' : this.confirm.kind === 'remove' ? 'Xóa khỏi nhóm?' : this.confirm.kind === 'leave' ? 'Rời nhóm?' : 'Xóa nhóm?';
  }

  get confirmMessage(): string {
    if (!this.confirm) {
      return '';
    }
    switch (this.confirm.kind) {
      case 'remove': return `${this.nameOf(this.confirm.member)} sẽ không xem và gửi tin trong nhóm “${this.detail.group.name}” nữa.`;
      case 'leave': return `Bạn sẽ không nhận tin nhắn của nhóm “${this.detail.group.name}” nữa.`;
      case 'delete': return `Nhóm “${this.detail.group.name}” sẽ bị xóa với mọi thành viên. Không hoàn tác được.`;
    }
  }

  doConfirm(): void {
    const c = this.confirm;
    if (!c) {
      return;
    }
    const groupId = this.detail.group.id;
    const call: Observable<unknown> = c.kind === 'delete' ? this.api.delete(groupId)
      : this.api.removeMember(groupId, c.kind === 'remove' ? c.member.userId : this.myId);
    this.busy = true;
    this.confirmError = null;
    call.pipe(finalize(() => (this.busy = false))).subscribe({
      next: () => {
        this.confirm = null;
        if (c.kind === 'remove') {
          this.changed.emit();
        } else {
          this.gone.emit(); // rời / xóa nhóm → không còn xem được nhóm này
        }
      },
      error: (err: HttpErrorResponse) => {
        this.confirmError = err.status === 403 ? 'Bạn không có quyền làm việc này.'
          : err.status === 404 ? 'Nhóm hoặc thành viên không còn tồn tại.'
          : err.error?.title || 'Không thực hiện được. Thử lại sau.';
      }
    });
  }

  trackMember(_: number, m: Member): string {
    return m.userId;
  }

  private search(q: string): Observable<UserSnapshot[]> {
    this.searching = true;
    return this.api.searchUsers(q).pipe(
      catchError(() => of([] as UserSnapshot[])),
      finalize(() => (this.searching = false))
    );
  }
}
