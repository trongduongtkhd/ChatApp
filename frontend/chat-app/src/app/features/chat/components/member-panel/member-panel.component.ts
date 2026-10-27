import { HttpErrorResponse } from '@angular/common/http';
import { Component, EventEmitter, Input, OnChanges, OnDestroy, Output } from '@angular/core';
import { FormControl } from '@angular/forms';
import { Observable, of, Subscription, timer } from 'rxjs';
import { catchError, debounceTime, distinctUntilChanged, finalize, map, switchMap } from 'rxjs/operators';
import { GroupDetail, JoinRequest, Member, UserSnapshot } from '../../../../core/models/group';
import { describeApiError } from '../../../../core/services/admin-api.service';
import { GroupApiService } from '../../../../core/services/group-api.service';

type Confirm = { kind: 'remove' | 'promote' | 'demote' | 'transfer'; member: Member }
  | { kind: 'leave' | 'delete' | 'invite' | 'approval' };

// Panel thành viên (Members.dc.html): thông tin nhóm, danh sách thành viên + online, thêm/xóa (Owner),
// rời nhóm (Member), xóa nhóm (Owner). Tự gọi GroupApiService; xong thì báo chat-window nạp lại (changed)
// hoặc rời màn hình nhóm (gone).
@Component({
  selector: 'app-member-panel',
  templateUrl: './member-panel.component.html',
  styles: [`.mp-governance { padding: 12px; border-bottom: 1px solid var(--line); overflow: auto; max-height: 42vh; flex: none; } .mp-governance button { margin: 4px 4px 4px 0; } .mp-invite { width: 100%; box-sizing: border-box; font: inherit; padding: 8px; border: 1px solid var(--line); color: var(--ink); background: var(--surface); border-radius: 8px; } .mp-actions { width: 34px; flex: none; color: var(--ink); background: var(--surface-hover); border: 0; border-radius: 6px; padding: 6px 0; } .mp-request { border-top: 1px solid var(--line); padding: 8px 0; }`]
})
export class MemberPanelComponent implements OnDestroy, OnChanges {
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
  requests: JoinRequest[] = [];
  requestsError = '';
  notice = '';
  inviteUrl = '';
  private seenGroup = '';

  private readonly sub: Subscription;

  constructor(private readonly api: GroupApiService) {
    this.sub = this.query.valueChanges.pipe(
      map((q: string) => q.trim()),
      debounceTime(300),
      distinctUntilChanged(),
      switchMap(q => (q ? this.search(q) : of([])))
    ).subscribe(users => (this.results = users));
    this.sub.add(timer(8000, 8000).subscribe(() => { if (!this.busy && !this.confirm) this.changed.emit(); }));
  }

  ngOnChanges(): void {
    if (!this.detail) return;
    if (this.seenGroup !== this.detail.group.id) {
      this.seenGroup = this.detail.group.id; this.inviteUrl = ''; this.notice = ''; this.requests = [];
    }
    this.loadRequests();
  }
  get isManager(): boolean { return this.isOwner || this.detail.group.myRole === 'Admin'; }
  roleLabel(m: Member): string { return m.role === 'Owner' ? 'Trưởng nhóm' : m.role === 'Admin' ? 'Quản trị viên' : 'Thành viên'; }
  canRemove(m: Member): boolean { return m.userId !== this.myId && m.role !== 'Owner' && (this.isOwner || (this.isManager && m.role === 'Member')); }
  memberAction(m: Member, event: Event): void {
    const select = event.target as HTMLSelectElement;
    const kind = select.value as 'remove' | 'promote' | 'demote' | 'transfer'; select.value = '';
    if (kind) this.ask({ kind, member: m });
  }
  loadRequests(): void {
    if (!this.isManager) { this.requests = []; return; }
    const id = this.detail.group.id;
    this.sub.add(this.api.requests(id).subscribe({
      next: rows => { if (id === this.detail.group.id) { this.requests = rows; this.requestsError = ''; } },
      error: err => { if (id === this.detail.group.id) this.requestsError = describeApiError(err); }
    }));
  }
  decide(request: JoinRequest, approve: boolean): void {
    if (this.busy) return; this.busy = true; this.requestsError = '';
    this.sub.add(this.api.decide(this.detail.group.id, request.id, approve).pipe(finalize(() => this.busy = false)).subscribe({
      next: () => { this.notice = approve ? 'Đã duyệt thành viên.' : 'Đã từ chối yêu cầu.'; this.changed.emit(); this.loadRequests(); },
      error: err => { this.requestsError = describeApiError(err); this.changed.emit(); this.loadRequests(); }
    }));
  }
  copyInvite(): void {
    if (navigator.clipboard) navigator.clipboard.writeText(this.inviteUrl).then(() => this.notice = 'Đã sao chép liên kết.').catch(() => this.notice = 'Hãy chọn và sao chép liên kết bên dưới.');
    else this.notice = 'Hãy chọn và sao chép liên kết bên dưới.';
  }
  selectInvite(event: Event): void { (event.target as HTMLInputElement).select(); }

  ngOnDestroy(): void {
    this.sub.unsubscribe();
  }

  get isOwner(): boolean {
    return this.detail.group.myRole === 'Owner';
  }

  // Trưởng nhóm lên đầu, rồi theo tên.
  get members(): Member[] {
    return [...this.detail.members].sort((a, b) =>
      (a.role === 'Owner' ? 0 : a.role === 'Admin' ? 1 : 2) - (b.role === 'Owner' ? 0 : b.role === 'Admin' ? 1 : 2) || this.nameOf(a).localeCompare(this.nameOf(b), 'vi'));
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
      next: result => { this.notice = 'status' in result ? 'Đã gửi yêu cầu, chờ trưởng nhóm hoặc quản trị viên duyệt.' : 'Đã thêm thành viên.'; this.changed.emit(); },
      error: (err: HttpErrorResponse) => {
        this.addError = err.status === 409 ? `${u.displayName} đã ở trong nhóm.`
          : err.status === 404 ? `${u.displayName} chưa được đồng bộ sang group-service, thử lại sau ít giây.`
          : describeApiError(err);
      }
    });
  }

  ask(c: Confirm): void {
    this.confirm = c;
    this.confirmError = null;
  }

  get confirmTitle(): string {
    return !this.confirm ? '' : ({ remove: 'Xóa khỏi nhóm?', leave: 'Rời nhóm?', delete: 'Xóa nhóm?',
      promote: 'Bổ nhiệm quản trị viên?', demote: 'Miễn nhiệm quản trị viên?', transfer: 'Chuyển trưởng nhóm?',
      invite: 'Tạo liên kết mời mới?', approval: this.detail.group.requiresApproval ? 'Tắt phê duyệt?' : 'Bật phê duyệt?' })[this.confirm.kind];
  }

  get confirmText(): string { return this.confirm?.kind === 'delete' || this.confirm?.kind === 'remove' ? 'Xóa' : this.confirm?.kind === 'leave' ? 'Rời nhóm' : 'Xác nhận'; }

  get confirmMessage(): string {
    if (!this.confirm) {
      return '';
    }
    switch (this.confirm.kind) {
      case 'remove': return `${this.nameOf(this.confirm.member)} sẽ không xem và gửi tin trong nhóm “${this.detail.group.name}” nữa.`;
      case 'leave': return `Bạn sẽ không nhận tin nhắn của nhóm “${this.detail.group.name}” nữa.`;
      case 'delete': return `Nhóm “${this.detail.group.name}” sẽ bị xóa với mọi thành viên. Không hoàn tác được.`;
      case 'promote': return `${this.nameOf(this.confirm.member)} có thể duyệt yêu cầu và quản lý thành viên thường.`;
      case 'demote': return `${this.nameOf(this.confirm.member)} trở lại vai trò thành viên thường.`;
      case 'transfer': return `${this.nameOf(this.confirm.member)} trở thành trưởng nhóm. Bạn trở thành thành viên thường.`;
      case 'invite': return 'Liên kết mời cũ sẽ hết hiệu lực. Các thành viên và yêu cầu đang chờ được giữ nguyên.';
      case 'approval': return this.detail.group.requiresApproval ? 'Người mới có thể tham gia trực tiếp. Các yêu cầu cũ vẫn cần được xét duyệt.' : 'Yêu cầu tham gia và người do thành viên thường thêm sẽ chờ xét duyệt.';
    }
  }

  doConfirm(): void {
    const c = this.confirm;
    if (!c) {
      return;
    }
    const groupId = this.detail.group.id;
    let call: Observable<unknown>;
    switch (c.kind) {
      case 'delete': call = this.api.delete(groupId); break;
      case 'leave': call = this.api.removeMember(groupId, this.myId); break;
      case 'remove': call = this.api.removeMember(groupId, c.member.userId); break;
      case 'promote': case 'demote': call = this.api.setRole(groupId, c.member.userId, c.kind === 'promote' ? 'Admin' : 'Member', this.detail.group.version); break;
      case 'transfer': call = this.api.transferOwner(groupId, c.member.userId, this.detail.group.version); break;
      case 'approval': call = this.api.setApproval(groupId, !this.detail.group.requiresApproval, this.detail.group.version); break;
      case 'invite': call = this.api.rotateInvite(groupId, this.detail.group.version); break;
    }
    this.busy = true;
    this.confirmError = null;
    call.pipe(finalize(() => (this.busy = false))).subscribe({
      next: result => {
        this.confirm = null;
        if (c.kind === 'invite') this.inviteUrl = window.location.origin + '/groups/join/' + (result as { token: string }).token;
        if (c.kind === 'leave' || c.kind === 'delete') {
          this.gone.emit(); // rời / xóa nhóm → không còn xem được nhóm này
        } else { this.notice = 'Đã cập nhật nhóm.'; this.changed.emit(); }
      },
      error: (err: HttpErrorResponse) => {
        this.confirmError = err.status === 403 ? 'Bạn không có quyền làm việc này.'
          : err.status === 404 ? 'Nhóm hoặc thành viên không còn tồn tại.'
          : err.error?.title || 'Không thực hiện được. Thử lại sau.';
        if (err.status === 403 || err.status === 409) this.changed.emit();
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
