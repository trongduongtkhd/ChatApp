import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

// Số tin chưa đọc (UI.md): 0 thì ẩn, trên 99 hiện "99+". Màu --unread chỉ dùng cho việc này và tin gửi lỗi.
@Component({
  selector: 'app-unread-badge',
  template: `<span *ngIf="count > 0" class="ca-badge" [attr.aria-label]="count + ' tin chưa đọc'">{{ count > 99 ? '99+' : count }}</span>`,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class UnreadBadgeComponent {
  @Input() count = 0;
}
