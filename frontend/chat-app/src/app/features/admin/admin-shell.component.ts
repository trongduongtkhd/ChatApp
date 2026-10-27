import { Component } from '@angular/core';

@Component({ selector: 'app-admin-shell', styles: [`
  .admin-layout { display: flex; height: 100vh; background: var(--surface); color: var(--ink); }
  .admin-main { flex: 1; min-width: 0; overflow: auto; padding: 28px 32px; }
  .admin-heading { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 12px; }
  h1 { margin: 4px 0 20px; font-size: 26px; }
  .admin-tabs { display: flex; gap: 8px; border-bottom: 1px solid var(--line); margin: 0 0 24px; padding-bottom: 12px; }
  .admin-tabs a { text-decoration: none; color: var(--ink-muted); padding: 8px 16px; border-radius: 8px; }
  .admin-tabs .selected { background: var(--brand-soft); color: var(--brand); font-weight: 600; }
  @media (max-width: 700px) { .admin-main { padding: 18px 12px; } }
`], template: `
  <div class="admin-layout"><app-rail></app-rail><main class="admin-main">
    <header class="admin-heading"><div><p class="ca-meta">CHATAPP · QUẢN TRỊ HỆ THỐNG</p><h1>Quản lý ứng dụng</h1></div><a class="ca-btn secondary" routerLink="/chat">Về trò chuyện</a></header>
    <nav class="admin-tabs" aria-label="Quản trị"><a routerLink="users" routerLinkActive="selected">Người dùng</a><a routerLink="groups" routerLinkActive="selected">Nhóm</a></nav>
    <router-outlet></router-outlet>
  </main></div>` })
export class AdminShellComponent {}
