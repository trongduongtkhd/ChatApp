import { CommonModule } from '@angular/common';
import { NgModule } from '@angular/core';
import { FormsModule, ReactiveFormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { AppRailComponent } from './components/app-rail/app-rail.component';
import { AvatarComponent } from './components/avatar/avatar.component';
import { ConfirmDialogComponent } from './components/confirm-dialog/confirm-dialog.component';
import { ConnectionBannerComponent } from './components/connection-banner/connection-banner.component';
import { UnreadBadgeComponent } from './components/unread-badge/unread-badge.component';
import { ListTimePipe } from './pipes/list-time.pipe';
import { VideoCallComponent, CallStreamDirective } from './components/video-call/video-call.component';

const COMPONENTS = [AvatarComponent, UnreadBadgeComponent, ConnectionBannerComponent, ConfirmDialogComponent, AppRailComponent, ListTimePipe, VideoCallComponent, CallStreamDirective];

// SharedModule: component/directive dùng lại ở nhiều feature (avatar, unread-badge, connection-banner...).
// Feature module nào cần thì import. KHÔNG chứa service singleton (service để ở core/).
// RouterModule (không forRoot/forChild): chỉ để dùng routerLink/routerLinkActive trong app-rail.
@NgModule({
  declarations: COMPONENTS,
  imports: [CommonModule, RouterModule],
  exports: [CommonModule, FormsModule, ReactiveFormsModule, ...COMPONENTS]
})
export class SharedModule {}
