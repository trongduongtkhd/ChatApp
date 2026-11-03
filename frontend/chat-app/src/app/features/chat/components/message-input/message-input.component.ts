import { ChangeDetectionStrategy, ChangeDetectorRef, Component, ElementRef, EventEmitter, Input, Output, ViewChild, OnDestroy } from '@angular/core';
import { Subscription } from 'rxjs';
import { AttachmentApiService, AttachmentDraft } from '../../../../core/services/attachment-api.service';
import { Attachment } from '../../../../core/models/message';

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
export class MessageInputComponent implements OnDestroy {
  @Input() groupId = '';
  fileError: string | null = null;
  private readonly subscription: Subscription;
  constructor(readonly files: AttachmentApiService, cd: ChangeDetectorRef) { this.subscription = files.changed$.subscribe(() => cd.markForCheck()); }
  get drafts(): AttachmentDraft[] { return this.files.list(this.groupId); }
  get canSend(): boolean { return (!!this.text.trim() || this.drafts.length > 0) && this.drafts.every(d => d.status === 'ready'); }
  picked(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (input.files) this.fileError = this.files.add(this.groupId, input.files);
    input.value = '';
  }
  // Thông báo từ server (HubException), hiện nguyên văn dưới ô nhập, màu --ink-muted (UI.md).
  @Input() error: string | null = null;
  @Output() readonly send = new EventEmitter<{ content: string; attachments: Attachment[] }>();
  @Output() readonly typing = new EventEmitter<boolean>();
  @Output() readonly textChange = new EventEmitter<string>();
  private idleTimer?: ReturnType<typeof setTimeout>;

  onInput(): void {
    this.textChange.emit(this.text);
    clearTimeout(this.idleTimer);
    this.typing.emit(!!this.text.trim());
    this.idleTimer = setTimeout(() => this.stopTyping(), 2500);
  }

  stopTyping(): void {
    clearTimeout(this.idleTimer);
    this.typing.emit(false);
  }

  ngOnDestroy(): void { this.stopTyping(); this.subscription.unsubscribe(); }

  @ViewChild('box', { static: true }) box!: ElementRef<HTMLInputElement>;

  @Input() text = '';

  submit(): void {
    const content = this.text.trim();
    if (!this.canSend) {
      return;
    }
    this.send.emit({ content, attachments: this.files.takeReady(this.groupId) });
    this.stopTyping();
    this.text = '';
    this.textChange.emit('');
    this.box.nativeElement.focus();
  }

  focus(): void {
    this.box.nativeElement.focus();
  }
}
