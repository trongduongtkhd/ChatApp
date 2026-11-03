import { Component } from '@angular/core';
import { ThemeService } from './core/services/theme.service';
import { ChatHubService } from './core/services/chat-hub.service';
import { RecoveryModeService } from './core/services/recovery-mode.service';

@Component({
  selector: 'app-root',
  template: '<div *ngIf="(recovery.mode$ | async)?.recoveryMode" class="ca-recovery-banner" role="status">BẢN PHỤC HỒI RIÊNG · Dữ liệu tại đây tách biệt với web nguồn</div><router-outlet></router-outlet><app-video-call></app-video-call>'
})
export class AppComponent {
  constructor(theme: ThemeService, chat: ChatHubService, readonly recovery: RecoveryModeService) {
    theme.init();
  }
}
