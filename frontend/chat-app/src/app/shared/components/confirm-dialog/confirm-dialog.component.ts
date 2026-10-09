import { ChangeDetectionStrategy, Component, EventEmitter, HostListener, Input, Output } from '@angular/core';

// Hộp thoại xác nhận cho thao tác không hoàn tác được (xóa nhóm, rời nhóm, xóa thành viên).
// Chỉ hiển thị: bên gọi tự làm việc khi nhận (confirm), truyền busy/error vào.
@Component({
  selector: 'app-confirm-dialog',
  template: `
    <div class="ca-scrim" (click)="cancel()">
      <div class="ca-dialog" role="alertdialog" aria-modal="true" aria-labelledby="cf-title" aria-describedby="cf-msg"
           style="max-width: 420px" (click)="$event.stopPropagation()">
        <div class="ca-dialog-head"><h2 class="ca-h2" id="cf-title">{{ title }}</h2></div>
        <div class="ca-dialog-body">
          <p id="cf-msg" style="margin: 0">{{ message }}</p>
          <div *ngIf="error" class="ca-alert error" role="alert"><span>{{ error }}</span></div>
        </div>
        <div class="ca-dialog-foot">
          <button class="ca-btn secondary" type="button" (click)="cancel()" [disabled]="busy">Hủy</button>
          <button class="ca-btn" [class.danger]="danger" [class.primary]="!danger" type="button" (click)="confirm.emit()" [disabled]="busy">
            <span *ngIf="busy" class="ca-spin"></span>{{ confirmText }}
          </button>
        </div>
      </div>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ConfirmDialogComponent {
  @Input() title = '';
  @Input() message = '';
  @Input() confirmText = 'Đồng ý';
  @Input() danger = true;
  @Input() busy = false;
  @Input() error: string | null = null;
  @Output() readonly confirm = new EventEmitter<void>();
  @Output() readonly cancelled = new EventEmitter<void>();

  @HostListener('document:keydown.escape')
  cancel(): void {
    if (!this.busy) {
      this.cancelled.emit();
    }
  }
}
