import { Component, NgModule, OnDestroy } from '@angular/core';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { Subscription, timer } from 'rxjs';
import { exhaustMap, finalize } from 'rxjs/operators';
import { InvitePreview } from '../../core/models/group';
import { AuthService } from '../../core/services/auth.service';
import { GroupApiService } from '../../core/services/group-api.service';
import { describeApiError } from '../../core/services/admin-api.service';
import { SharedModule } from '../../shared/shared.module';

@Component({ selector: 'app-group-join', template: `
  <main class="join-page"><section class="ca-card">
    <p class="ca-meta">CHATAPP · LỜI MỜI THAM GIA NHÓM</p><h1>{{ preview?.name || 'Tham gia nhóm' }}</h1>
    <ng-container *ngIf="!loggedIn"><p>Đăng nhập để xem lời mời và tham gia nhóm.</p><a class="ca-btn primary" routerLink="/auth/login" [queryParams]="{returnUrl: returnUrl}">Đăng nhập</a></ng-container>
    <p *ngIf="error" role="alert">{{ error }}</p><p *ngIf="notice" role="status">{{ notice }}</p>
    <ng-container *ngIf="preview"><p>{{ preview.description || 'Bạn được mời tham gia cuộc trò chuyện này.' }}</p>
      <a *ngIf="preview.isMember" class="ca-btn primary" [routerLink]="['/chat', preview.groupId]">Mở cuộc trò chuyện</a>
      <ng-container *ngIf="!preview.isMember"><p class="ca-meta">{{ preview.requiresApproval ? 'Trưởng nhóm hoặc quản trị viên sẽ xét duyệt yêu cầu của bạn.' : 'Bạn có thể tham gia sau khi xác nhận.' }}</p>
        <p *ngIf="preview.requestStatus === 'Pending'" role="status">Yêu cầu của bạn đang chờ phê duyệt.</p>
        <button *ngIf="preview.requestStatus === 'Pending'" class="ca-btn secondary" type="button" (click)="cancel()" [disabled]="busy">Hủy yêu cầu của tôi</button>
        <button *ngIf="preview.requestStatus !== 'Pending'" class="ca-btn primary" type="button" (click)="join()" [disabled]="busy">{{ busy ? 'Đang xử lý…' : preview.requiresApproval ? 'Yêu cầu tham gia' : 'Tham gia nhóm' }}</button>
      </ng-container>
    </ng-container>
    <button *ngIf="loggedIn" class="ca-btn secondary" type="button" (click)="load()" [disabled]="busy">Tải lại lời mời</button><a routerLink="/chat">Về ChatApp</a>
  </section></main>`, styles: [`.join-page { min-height: 100vh; padding: 32px 16px; box-sizing: border-box; background: var(--surface-alt); display: grid; place-items: center; } .ca-card { width: min(480px, 100%); box-sizing: border-box; } h1 { margin: 0; }`] })
export class GroupJoinComponent implements OnDestroy {
  preview: InvitePreview | null = null; error = ''; notice = ''; busy = false;
  readonly token = this.route.snapshot.paramMap.get('token') || '';
  readonly returnUrl = '/groups/join/' + this.token;
  readonly loggedIn = this.auth.isLoggedIn();
  private readonly sub = new Subscription();
  constructor(private readonly route: ActivatedRoute, private readonly auth: AuthService, private readonly api: GroupApiService) {
    if (this.loggedIn) {
      this.load();
      this.sub.add(timer(8000,8000).subscribe(() => { if (this.preview?.requestStatus === 'Pending' && !this.busy) this.load(); }));
    }
  }
  load(): void {
    if (!this.loggedIn) return;
    this.error = '';
    this.sub.add(this.api.previewInvite(this.token).subscribe({ next: p => this.preview = p, error: err => { this.preview = null; this.error = describeApiError(err); } }));
  }
  join(): void {
    if (this.busy) return; this.busy = true; this.error = ''; this.notice = '';
    this.sub.add(this.api.joinInvite(this.token).pipe(finalize(() => this.busy = false)).subscribe({
      next: () => this.load(), error: err => { this.error = describeApiError(err); if (err.status === 409) this.load(); }
    }));
  }
  cancel(): void {
    if (!this.preview || this.busy) return; this.busy = true; this.error = '';
    this.sub.add(this.api.cancelRequest(this.preview.groupId).pipe(finalize(() => this.busy = false)).subscribe({
      next: () => { this.notice = 'Đã hủy yêu cầu.'; this.load(); }, error: err => this.error = describeApiError(err)
    }));
  }
  ngOnDestroy(): void { this.sub.unsubscribe(); }
}

@NgModule({ declarations: [GroupJoinComponent], imports: [SharedModule, RouterModule.forChild([{ path: 'join/:token', component: GroupJoinComponent }])] })
export class GroupJoinModule {}
