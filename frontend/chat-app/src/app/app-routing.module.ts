import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { AuthGuard } from './core/guards/auth.guard';
import { GuestGuard } from './core/guards/guest.guard';
import { AdminGuard } from './core/guards/admin.guard';

// LAZY LOADING: mỗi feature là một file JS riêng (chunk), chỉ tải khi người dùng vào đường dẫn đó.
// Mở /auth/login thì trình duyệt chưa phải tải code khung chat, và ngược lại.
const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'chat' },
  { path: 'admin', canLoad: [AdminGuard], canActivate: [AdminGuard],
    loadChildren: () => import('./features/admin/admin.module').then(m => m.AdminModule) },
  { path: 'groups', loadChildren: () => import('./features/group-join/group-join.module').then(m => m.GroupJoinModule) },
  {
    path: 'auth',
    canActivate: [GuestGuard],
    loadChildren: () => import('./features/auth/auth.module').then(m => m.AuthModule)
  },
  {
    path: 'chat',
    canLoad: [AuthGuard],
    canActivate: [AuthGuard],
    loadChildren: () => import('./features/chat/chat.module').then(m => m.ChatModule)
  },
  {
    path: 'contacts',
    canLoad: [AuthGuard],
    canActivate: [AuthGuard],
    loadChildren: () => import('./features/contacts/contacts.module').then(m => m.ContactsModule)
  },
  {
    path: 'settings',
    canLoad: [AuthGuard],
    canActivate: [AuthGuard],
    loadChildren: () => import('./features/settings/settings.module').then(m => m.SettingsModule)
  },
  { path: '**', redirectTo: 'chat' }
];

@NgModule({
  imports: [RouterModule.forRoot(routes)],
  exports: [RouterModule]
})
export class AppRoutingModule {}
