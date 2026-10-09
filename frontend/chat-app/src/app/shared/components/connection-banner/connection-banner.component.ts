import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';

// Dải trạng thái kết nối (Main.dc.html, tweak "connection"; UI.md "ConnectionBanner").
// Chỉ hiển thị: trạng thái do ChatHubService tính, nút "Thử lại" báo ra ngoài qua (retry).
// --warning chỉ dùng cho "Đang kết nối lại" (UI.md: màu trạng thái có nghĩa cố định).
@Component({
  selector: 'app-connection-banner',
  template: `
    <ng-container [ngSwitch]="status">
      <div *ngSwitchCase="'reconnecting'" class="ca-banner reconnecting" role="status">
        <span class="ca-spin"></span>Đang kết nối lại… Tin bạn gửi sẽ được gửi khi có kết nối.
      </div>
      <div *ngSwitchCase="'closed'" class="ca-banner reconnecting" role="alert">
        Mất kết nối tới máy chủ.
        <button class="ca-link" type="button" style="color: inherit; margin-left: auto" (click)="retry.emit()">Thử lại</button>
      </div>
      <div *ngSwitchCase="'reconnected'" class="ca-banner connected" role="status">
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M20 6 9 17l-5-5"></path></svg>Đã kết nối lại
      </div>
    </ng-container>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ConnectionBannerComponent {
  @Input() status: 'none' | 'reconnecting' | 'closed' | 'reconnected' | null = 'none';
  @Output() readonly retry = new EventEmitter<void>();
}
