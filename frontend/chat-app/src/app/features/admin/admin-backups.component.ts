import { Component, OnDestroy, OnInit } from '@angular/core';
import { Subscription, timer, EMPTY, Observable } from 'rxjs';
import { catchError, switchMap } from 'rxjs/operators';
import { BackupApiService, BackupItem, BackupOverview, BackupSettings } from '../../core/services/backup-api.service';
import { describeApiError } from '../../core/services/admin-api.service';

@Component({ selector: 'app-admin-backups', template: `
  <section class="backup-page">
    <div class="backup-intro"><div><h2>Sao lưu &amp; phục hồi</h2>
      <p>Giữ tài khoản, nhóm, tin nhắn và ảnh/file trong một bộ phục hồi.</p></div>
      <button class="ca-btn primary" data-testid="create-backup" [disabled]="busy || !overview?.enabled" (click)="create()">Tạo bản sao lưu</button>
    </div>
    <p class="notice" *ngIf="!overview?.enabled && overview">Bạn đang ở bản phục hồi. Quản lý sao lưu tại web nguồn.</p>
    <p class="feedback error" role="alert" *ngIf="error">{{error}}</p>
    <p class="feedback success" role="status" *ngIf="message">{{message}}</p>
    <section class="backup-card" *ngIf="overview?.enabled">
      <h3>Lịch tự động</h3><div class="schedule-row">
        <label>Thực hiện <select [(ngModel)]="settings.period" aria-label="Lịch sao lưu"><option value="disabled">Tạm tắt</option><option value="hourly">Mỗi giờ</option><option value="daily">Mỗi ngày lúc 02:00 (giờ Việt Nam)</option></select></label>
        <label>Giữ gần nhất <input type="number" min="1" max="30" [(ngModel)]="settings.retentionCount" aria-label="Số bản giữ lại"> bản</label>
        <label>WAL trung gian <input type="number" min="24" max="720" [(ngModel)]="settings.walArchiveHours" aria-label="Số giờ giữ WAL"> giờ</label>
        <button class="ca-btn secondary" [disabled]="busy" (click)="saveSchedule()">Lưu lịch</button>
      </div><p class="muted">Lịch chạy khi Gateway và Docker hoạt động. Bản đang được dùng để phục hồi được giữ lại.</p>
    </section>
    <section class="backup-card" *ngIf="overview && overview.recovery.status !== 'none'" data-testid="recovery-status">
      <h3>Bản phục hồi riêng</h3><p>{{overview.recovery.message}}</p>
      <p class="muted">Dữ liệu nguồn tiếp tục được giữ nguyên.</p>
      <a *ngIf="overview.recovery.status === 'ready'" class="ca-btn primary" href="http://localhost:4201" target="_blank" rel="noopener" data-testid="open-recovery">Mở web phục hồi</a>
      <button class="ca-btn secondary" [disabled]="busy" (click)="cleanup()">Dọn môi trường phục hồi</button>
    </section>
    <section class="backup-card"><h3>Các bản sao lưu</h3>
      <p *ngIf="!overview">Đang tải…</p><p *ngIf="overview && !overview.items.length" class="muted">Chưa có bản sao lưu nào.</p>
      <div class="backup-table" *ngIf="overview && overview.items.length"><table>
        <thead><tr><th>Bản sao lưu</th><th>Trạng thái</th><th>Thông tin</th><th>Thao tác</th></tr></thead>
        <tbody><tr *ngFor="let item of overview.items" [attr.data-backup-id]="item.id">
          <td><strong>{{item.id}}</strong><small>{{item.updatedAt | date:'dd/MM/yyyy HH:mm:ss'}}</small></td>
          <td><span class="status" [class.complete]="item.status === 'complete'" [class.failed]="item.status === 'failed'">{{statusLabel(item.status)}}</span></td>
          <td>{{item.message}}<small *ngIf="item.checkpointMs != null">Chốt mốc: {{item.checkpointMs | number:'1.0-0'}} ms</small></td>
          <td><div class="actions"><button class="ca-btn secondary" [disabled]="busy || item.status !== 'complete'" (click)="verify(item)">Kiểm tra</button>
            <button class="ca-btn secondary" [disabled]="busy || item.status !== 'complete' || !overview.enabled" (click)="restore(item)">Phục hồi riêng</button></div></td>
        </tr></tbody>
      </table></div>
    </section>
    <app-confirm-dialog *ngIf="pendingRestore" title="Tạo bản phục hồi riêng?" message="Hệ thống sẽ dựng một web riêng từ bản sao lưu này. Dữ liệu nguồn không bị ghi đè." confirmText="Bắt đầu phục hồi" [danger]="false" (confirm)="confirmRestore()" (cancelled)="pendingRestore = null"></app-confirm-dialog>
  </section>`, styles: [`
    h2 { margin:0 0 8px; } h3 { margin:0 0 14px; } p { line-height:1.6; }
    .backup-intro { display:flex; align-items:center; justify-content:space-between; gap:20px; margin-bottom:20px; }
    .backup-intro p,.muted { color:var(--ink-muted); } .backup-card { border:1px solid var(--line); border-radius:14px; padding:22px; margin:18px 0; }
    .schedule-row,.actions { display:flex; align-items:center; flex-wrap:wrap; gap:12px; } label { display:flex; gap:8px; align-items:center; }
    select,input { color:var(--ink); background:var(--surface); border:1px solid var(--line); border-radius:7px; padding:9px; } input { width:65px; }
    .backup-table { overflow-x:auto; } table { width:100%; border-collapse:collapse; text-align:left; font-size:13px; } th,td { padding:14px 10px; border-bottom:1px solid var(--line); vertical-align:top; }
    th { color:var(--ink-muted); } td small { display:block; margin-top:6px; color:var(--ink-muted); } .status { border-radius:6px; padding:5px 8px; background:var(--brand-soft); }
    .complete,.success { color:#16814b; } .failed,.error { color:#cf3434; } .feedback,.notice { padding:12px; border:1px solid var(--line); border-radius:8px; }
    .backup-card>a { display:inline-flex; margin-right:10px; text-decoration:none; } @media(max-width:750px) { .backup-intro { align-items:flex-start; flex-direction:column; } }
  `] })
export class AdminBackupsComponent implements OnInit, OnDestroy {
  overview: BackupOverview | null = null;
  settings: BackupSettings = {period:'disabled', retentionCount:5, walArchiveHours:168};
  pendingRestore: BackupItem | null = null;
  error = ''; message = ''; working = false;
  private initialized = false;
  private readonly subscriptions = new Subscription();
  constructor(private readonly api: BackupApiService) {}
  get busy(): boolean { return this.working || !!this.overview?.items.some(i => i.status === 'queued' || i.status === 'running') || this.overview?.recovery.status === 'running' || this.overview?.recovery.status === 'queued'; }
  ngOnInit(): void {
    this.subscriptions.add(timer(0, 2000).pipe(switchMap(() => this.api.list().pipe(catchError(e => {this.error = describeApiError(e); return EMPTY;}))))
      .subscribe(data => { this.overview = data; if (!this.initialized) {this.settings = {...data.settings}; this.initialized = true;} }));
  }
  ngOnDestroy(): void { this.subscriptions.unsubscribe(); }
  statusLabel(status: string): string { return ({queued:'Đang chờ', running:'Đang thực hiện', complete:'Hoàn tất', failed:'Thất bại'} as {[key:string]:string})[status] || status; }
  create(): void { this.perform(this.api.create(), 'Đã gửi yêu cầu sao lưu. Bạn có thể tiếp tục trò chuyện.'); }
  verify(item: BackupItem): void { this.perform(this.api.verify(item.id), 'Các file của bản sao lưu khớp checksum.'); }
  restore(item: BackupItem): void { this.pendingRestore = item; }
  confirmRestore(): void { if (!this.pendingRestore) {return;} const id = this.pendingRestore.id; this.pendingRestore = null; this.perform(this.api.restore(id), 'Đang chuẩn bị bản phục hồi riêng…'); }
  cleanup(): void { this.perform(this.api.cleanup(), 'Đã dọn môi trường phục hồi.'); }
  saveSchedule(): void { this.perform(this.api.schedule(this.settings), 'Đã lưu lịch sao lưu.'); }
  private perform(action: Observable<unknown>, success: string): void {
    this.error = ''; this.message = ''; this.working = true;
    this.subscriptions.add(action.subscribe(() => {this.working = false; this.message = success;}, e => {this.working = false; this.error = describeApiError(e);}));
  }
}
