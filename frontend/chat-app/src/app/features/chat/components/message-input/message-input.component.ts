import { ChangeDetectionStrategy, Component, ElementRef, EventEmitter, Input, Output, ViewChild } from '@angular/core';

// Ô nhập tin (Main.dc.html). Chỉ lo nhập + bấm Gửi; sinh messageId và gửi do chat-window làm.
// KHÔNG khóa ô nhập khi đang kết nối lại: tin vẫn nhận, nằm chờ "Đang gửi…" tới khi có kết nối.
@Component({
  selector: 'app-message-input',
  templateUrl: './message-input.component.html',
  styles: [`
    .composer-error { padding: 0 16px 10px; font-size: 13px; line-height: 16px; color: var(--ink-muted); }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class MessageInputComponent {
  // Thông báo từ server (HubException), hiện nguyên văn dưới ô nhập, màu --ink-muted (UI.md).
  @Input() error: string | null = null;
  @Output() readonly send = new EventEmitter<string>();

  @ViewChild('box', { static: true }) box!: ElementRef<HTMLInputElement>;

  text = '';

  submit(): void {
    const content = this.text.trim();
    if (!content) {
      return;
    }
    this.send.emit(content);
    this.text = '';
    this.box.nativeElement.focus();
  }

  focus(): void {
    this.box.nativeElement.focus();
  }
}
