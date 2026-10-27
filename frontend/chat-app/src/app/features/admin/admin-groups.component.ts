import { Component, OnDestroy } from '@angular/core';
import { Subscription } from 'rxjs';
import { finalize } from 'rxjs/operators';
import { AdminApiService, AdminGroup, AdminGroupDetail, Page, describeApiError } from '../../core/services/admin-api.service';

@Component({ selector: 'app-admin-groups', templateUrl: './admin-groups.component.html', styleUrls: ['./admin.scss'] })
export class AdminGroupsComponent implements OnDestroy {
  q = ''; page = 1; memberPage = 1; data: Page<AdminGroup> | null = null; detail: AdminGroupDetail | null = null;
  loading = false; error = '';
  private listSub?: Subscription;
  private detailSub?: Subscription;
  constructor(private readonly api: AdminApiService) { this.load(); }
  ngOnDestroy(): void { this.listSub?.unsubscribe(); this.detailSub?.unsubscribe(); }
  load(reset = false): void {
    if (reset) this.page = 1;
    this.listSub?.unsubscribe(); this.loading = true; this.error = '';
    this.listSub = this.api.groups(this.q.trim(), this.page).pipe(finalize(() => this.loading = false)).subscribe({
      next: data => this.data = data, error: err => this.error = describeApiError(err)
    });
  }
  select(id: string, page = 1): void {
    this.detailSub?.unsubscribe(); this.memberPage = page; this.error = '';
    this.detailSub = this.api.group(id, page).subscribe({ next: detail => this.detail = detail, error: err => this.error = describeApiError(err) });
  }
  roleLabel(role: string): string { return role === 'Owner' ? 'Trưởng nhóm' : role === 'Admin' ? 'Quản trị viên' : 'Thành viên'; }
}
