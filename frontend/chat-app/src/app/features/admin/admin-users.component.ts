import { Component, OnDestroy } from '@angular/core';
import { Subscription } from 'rxjs';
import { finalize } from 'rxjs/operators';
import { AdminApiService, AdminUser, AuditEntry, Page, describeApiError } from '../../core/services/admin-api.service';
import { AuthService } from '../../core/services/auth.service';

@Component({ selector: 'app-admin-users', templateUrl: './admin-users.component.html', styleUrls: ['./admin.scss'] })
export class AdminUsersComponent implements OnDestroy {
  q = ''; locked = ''; page = 1; data: Page<AdminUser> | null = null;
  loading = false; error = ''; detail: { user: AdminUser; audit: AuditEntry[] } | null = null;
  target: AdminUser | null = null; reason = ''; busy = false; dialogError = '';
  readonly myId = this.auth.currentUser?.id;
  private listSub?: Subscription;
  private readonly subscriptions = new Subscription();
  constructor(private readonly api: AdminApiService, private readonly auth: AuthService) { this.load(); }
  ngOnDestroy(): void { this.listSub?.unsubscribe(); this.subscriptions.unsubscribe(); }
  load(reset = false): void {
    if (reset) this.page = 1;
    this.listSub?.unsubscribe(); this.loading = true; this.error = '';
    this.listSub = this.api.users(this.q.trim(), this.locked, this.page).pipe(finalize(() => this.loading = false)).subscribe({
      next: data => this.data = data, error: err => this.error = describeApiError(err)
    });
  }
  select(user: AdminUser): void {
    this.subscriptions.add(this.api.user(user.id).subscribe({ next: detail => this.detail = detail, error: err => this.error = describeApiError(err) }));
  }
  ask(user: AdminUser): void { this.target = user; this.reason = ''; this.dialogError = ''; }
  confirm(): void {
    const user = this.target; if (!user || this.busy || (!user.isLocked && !this.reason.trim())) return;
    this.busy = true;
    this.subscriptions.add(this.api.lock(user, !user.isLocked, this.reason.trim()).pipe(finalize(() => this.busy = false)).subscribe({
      next: saved => { this.target = null; this.load(); if (this.detail?.user.id === saved.id) this.select(saved); },
      error: err => { this.dialogError = describeApiError(err); if (err.status === 409) { this.target = null; this.load(); this.error = describeApiError(err); } }
    }));
  }
}
