import { ChangeDetectionStrategy, Component } from '@angular/core';
import { AuthService } from '../../../core/services/auth.service';

// Rail điều hướng bên trái (Bước 9b), dùng chung cho /chat, /contacts (Phần 11) và /settings.
// Mục đang mở: routerLinkActive → lớp .active (nền sáng hơn + vạch trái) và aria-current="page".
@Component({
  selector: 'app-rail',
  template: `
    <nav class="ca-rail" aria-label="Điều hướng chính">
      <a class="ca-rail-logo" routerLink="/chat" aria-label="ChatApp – về Đoạn chat" title="ChatApp">
        <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"></path></svg>
      </a>

      <a class="ca-rail-item" routerLink="/chat" routerLinkActive="active" #chat="routerLinkActive"
         [attr.aria-current]="chat.isActive ? 'page' : null" aria-label="Đoạn chat" title="Đoạn chat">
        <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M7.9 20A9 9 0 1 0 4 16.1L2 22z"></path></svg>
      </a>

      <a class="ca-rail-item" routerLink="/contacts" routerLinkActive="active" #contacts="routerLinkActive"
         [attr.aria-current]="contacts.isActive ? 'page' : null" aria-label="Danh bạ" title="Danh bạ">
        <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2"></path><circle cx="9" cy="7" r="4"></circle><path d="M22 21v-2a4 4 0 0 0-3-3.9"></path><path d="M16 3.1a4 4 0 0 1 0 7.8"></path></svg>
      </a>

      <a class="ca-rail-item" routerLink="/settings" routerLinkActive="active" #settings="routerLinkActive"
         [attr.aria-current]="settings.isActive ? 'page' : null" aria-label="Cài đặt" title="Cài đặt">
        <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M12.2 2h-.4a2 2 0 0 0-2 2v.2a2 2 0 0 1-1 1.7l-.4.3a2 2 0 0 1-2 0l-.2-.1a2 2 0 0 0-2.7.7l-.2.4a2 2 0 0 0 .7 2.7l.2.1a2 2 0 0 1 1 1.7v.6a2 2 0 0 1-1 1.7l-.2.1a2 2 0 0 0-.7 2.7l.2.4a2 2 0 0 0 2.7.7l.2-.1a2 2 0 0 1 2 0l.4.3a2 2 0 0 1 1 1.7v.2a2 2 0 0 0 2 2h.4a2 2 0 0 0 2-2v-.2a2 2 0 0 1 1-1.7l.4-.3a2 2 0 0 1 2 0l.2.1a2 2 0 0 0 2.7-.7l.2-.4a2 2 0 0 0-.7-2.7l-.2-.1a2 2 0 0 1-1-1.7v-.6a2 2 0 0 1 1-1.7l.2-.1a2 2 0 0 0 .7-2.7l-.2-.4a2 2 0 0 0-2.7-.7l-.2.1a2 2 0 0 1-2 0l-.4-.3a2 2 0 0 1-1-1.7V4a2 2 0 0 0-2-2z"></path><circle cx="12" cy="12" r="3"></circle></svg>
      </a>

      <!-- Avatar của mình ở đáy → trang Cài đặt (hồ sơ) -->
      <a class="ca-rail-me" routerLink="/settings" [attr.aria-label]="'Hồ sơ: ' + displayName" [title]="displayName">
        <app-avatar [name]="displayName" size="md"></app-avatar>
      </a>
    </nav>
  `,
  styles: [':host { display: flex; flex: none; }'],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class AppRailComponent {
  readonly displayName = this.auth.currentUser?.displayName ?? '';

  constructor(private readonly auth: AuthService) {}
}
