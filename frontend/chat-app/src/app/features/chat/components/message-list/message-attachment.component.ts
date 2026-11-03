import { AfterViewInit, ChangeDetectionStrategy, ChangeDetectorRef, Component, ElementRef, Input, OnChanges, OnDestroy } from '@angular/core';
import { Subscription } from 'rxjs';
import { DomSanitizer, SafeUrl } from '@angular/platform-browser';
import { Attachment } from '../../../../core/models/message';
import { AttachmentApiService } from '../../../../core/services/attachment-api.service';

@Component({
  selector: 'app-message-attachment', changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="ca-attachment">
      <button *ngIf="attachment.kind === 'image'" type="button" class="ca-image-open" (click)="open($event)" [attr.aria-label]="'Xem ảnh ' + attachment.fileName">
        <img *ngIf="url" [src]="url" [alt]="attachment.fileName">
        <span *ngIf="!url">{{ loading ? 'Đang tải ảnh…' : error ? 'Không tải được ảnh · Bấm thử lại' : 'Xem ảnh' }}</span>
      </button>
      <strong class="ca-file-name">{{ attachment.fileName }}</strong>
      <div class="ca-file-actions"><span>{{ size }}</span><button type="button" class="ca-link" (click)="download()" [disabled]="downloading">{{ downloading ? 'Đang tải…' : 'Tải xuống' }}</button></div>
      <span *ngIf="error" role="status">{{ error }}</span>
    </div>
    <div *ngIf="expanded && url" class="ca-image-lightbox" role="dialog" aria-modal="true" [attr.aria-label]="attachment.fileName" (click)="close()" (keydown.escape)="close()" (keydown.tab)="$event.preventDefault()">
      <button class="ca-btn secondary" type="button" (click)="close(); $event.stopPropagation()">Đóng ảnh</button>
      <img [src]="url" [alt]="attachment.fileName" (click)="$event.stopPropagation()">
    </div>`
})
export class MessageAttachmentComponent implements AfterViewInit, OnChanges, OnDestroy {
  @Input() groupId = '';
  @Input() attachment!: Attachment;
  url: SafeUrl | null = null;
  private blobUrl: string | null = null;
  error: string | null = null;
  loading = false;
  downloading = false;
  expanded = false;
  private observer?: IntersectionObserver;
  private request?: Subscription;
  private downloadRequest?: Subscription;
  private viewReady = false;
  private returnFocus?: HTMLElement;
  constructor(private readonly api: AttachmentApiService, private readonly host: ElementRef<HTMLElement>, private readonly cd: ChangeDetectorRef, private readonly sanitizer: DomSanitizer) {}
  get size(): string { return this.attachment.size >= 1048576 ? (this.attachment.size / 1048576).toFixed(1) + ' MiB' : Math.ceil(this.attachment.size / 1024) + ' KiB'; }
  ngAfterViewInit(): void { this.viewReady = true; this.watch(); }
  ngOnChanges(): void {
    this.request?.unsubscribe(); this.downloadRequest?.unsubscribe(); this.observer?.disconnect();
    if (this.blobUrl) URL.revokeObjectURL(this.blobUrl);
    this.blobUrl = null;
    this.url = null; this.loading = false; this.downloading = false; this.error = null; this.expanded = false;
    if (this.viewReady) this.watch();
  }
  private watch(): void {
    if (this.attachment.kind !== 'image') return;
    if (typeof IntersectionObserver === 'undefined') { this.loadImage(); return; }
    this.observer = new IntersectionObserver(entries => {
      if (entries.some(e => e.isIntersecting)) { this.observer?.disconnect(); this.loadImage(); }
    }, { rootMargin: '200px' });
    this.observer.observe(this.host.nativeElement);
  }
  private loadImage(): void {
    if (this.loading || this.url) return;
    this.loading = true; this.error = null; this.cd.markForCheck();
    this.request = this.api.content(this.groupId, this.attachment.id).subscribe({
      next: data => {
        this.blobUrl = URL.createObjectURL(data); this.url = this.sanitizer.bypassSecurityTrustUrl(this.blobUrl);
        this.loading = false; this.cd.markForCheck();
      },
      error: () => { this.loading = false; this.error = 'Không tải được ảnh. Hãy thử lại.'; this.cd.markForCheck(); }
    });
  }
  open(event: Event): void {
    if (!this.url) { this.loadImage(); return; }
    this.returnFocus = event.currentTarget as HTMLElement; this.expanded = true;
    setTimeout(() => this.host.nativeElement.querySelector<HTMLButtonElement>('.ca-image-lightbox button')?.focus());
  }
  close(): void { this.expanded = false; this.returnFocus?.focus(); }
  download(): void {
    if (this.downloading) return;
    this.downloading = true; this.error = null;
    this.downloadRequest = this.api.content(this.groupId, this.attachment.id).subscribe({
      next: data => {
        const url = URL.createObjectURL(data), a = document.createElement('a');
        a.href = url; a.download = this.attachment.fileName; a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
        this.downloading = false; this.cd.markForCheck();
      },
      error: () => { this.downloading = false; this.error = 'Không tải được file. Hãy thử lại.'; this.cd.markForCheck(); }
    });
  }
  ngOnDestroy(): void {
    this.observer?.disconnect(); this.request?.unsubscribe(); this.downloadRequest?.unsubscribe();
    if (this.blobUrl) URL.revokeObjectURL(this.blobUrl);
  }
}
