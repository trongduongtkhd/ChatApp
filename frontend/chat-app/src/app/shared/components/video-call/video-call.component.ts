import { Component, Directive, ElementRef, Input, NgZone, OnDestroy, OnInit } from '@angular/core';
import { Subscription } from 'rxjs';
import { VideoCallService } from '../../../core/services/video-call.service';

@Directive({ selector: 'video[appCallStream]' })
export class CallStreamDirective {
  constructor(private readonly element: ElementRef<HTMLVideoElement>) {}
  @Input() set appCallStream(stream: MediaStream | null) {
    this.element.nativeElement.srcObject = stream;
    if (stream) void this.element.nativeElement.play().catch(() => undefined);
  }
}

@Component({
  selector: 'app-video-call', templateUrl: './video-call.component.html',
  styles: [`
    .call-backdrop { position: fixed; inset: 0; z-index: 1100; background: rgba(0,0,0,.65); display: flex; align-items: center; justify-content: center; padding: 16px; }
    .call-dialog { background: var(--surface); color: var(--ink); border: 1px solid var(--line); border-radius: 20px; padding: 22px; width: min(880px,100%); max-height: 95vh; overflow: auto; box-shadow: 0 20px 80px rgba(0,0,0,.3); }
    .call-heading { display: flex; justify-content: space-between; gap: 12px; align-items: center; margin-bottom: 14px; }
    .call-heading h2 { margin: 0 0 6px; }
    .call-media { position: relative; background: #101318; border-radius: 14px; height: min(52vh,440px); overflow: hidden; margin: 14px 0; }
    .remote { width: 100%; height: 100%; object-fit: contain; }
    .local { position: absolute; right: 12px; bottom: 12px; width: 26%; max-height: 32%; border: 1px solid #fff; border-radius: 10px; object-fit: cover; background: #20242b; transform: scaleX(-1); }
    .call-waiting { position: absolute; inset: 25% 10% auto; text-align: center; color: #fff; }
    .call-actions { display: flex; flex-wrap: wrap; justify-content: center; gap: 10px; margin-top: 18px; }
    .call-end { background: #bf2434; color: #fff; border: 0; }
    .call-message { color: var(--ink-muted); }
  `]
})
export class VideoCallComponent implements OnInit, OnDestroy {
  private readonly subscriptions = new Subscription();
  private timer?: ReturnType<typeof setInterval>;
  private previousFocus?: HTMLElement;
  duration = '';
  constructor(readonly calls: VideoCallService, private readonly host: ElementRef<HTMLElement>, private readonly zone: NgZone) {}
  ngOnInit(): void {
    let id = '';
    this.subscriptions.add(this.calls.view$.subscribe(view => {
      if (view && view.id !== id) {
        id = view.id; this.previousFocus = document.activeElement as HTMLElement;
        setTimeout(() => this.host.nativeElement.querySelector<HTMLButtonElement>('button')?.focus());
      }
      if (view?.phase === 'active' && !this.timer) {
        this.zone.runOutsideAngular(() => { this.timer = setInterval(() => this.zone.run(() => {
          const start = this.calls.view?.connectedAt || 0;
          const seconds = start ? Math.max(0, Math.floor((Date.now() - start) / 1000)) : 0;
          this.duration = Math.floor(seconds / 60).toString().padStart(2, '0') + ':' + (seconds % 60).toString().padStart(2, '0');
        }), 1000); });
      } else if (view?.phase !== 'active') { clearInterval(this.timer); this.timer = undefined; this.duration = ''; }
    }));
  }
  close(): void { this.calls.dismiss(); this.previousFocus?.focus(); }
  keyboard(event: KeyboardEvent): void {
    if (event.key === 'Escape' && this.calls.view?.phase === 'ended') this.close();
    if (event.key !== 'Tab') return;
    const buttons = Array.from(this.host.nativeElement.querySelectorAll<HTMLButtonElement>('button:not(:disabled)'));
    if (!buttons.length) return;
    const first = buttons[0], last = buttons[buttons.length - 1];
    if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
    else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
  }
  play(video: HTMLVideoElement): void { void video.play().catch(() => undefined); }
  ngOnDestroy(): void { this.subscriptions.unsubscribe(); clearInterval(this.timer); }
}
