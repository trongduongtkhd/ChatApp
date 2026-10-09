import { NgModule } from '@angular/core';
import { SharedModule } from '../../shared/shared.module';
import { ChatRoutingModule } from './chat-routing.module';
import { ChatWindowComponent } from './components/chat-window/chat-window.component';
import { CreateGroupDialogComponent } from './components/create-group-dialog/create-group-dialog.component';
import { EditGroupDialogComponent } from './components/edit-group-dialog/edit-group-dialog.component';
import { GroupListComponent } from './components/group-list/group-list.component';
import { MemberPanelComponent } from './components/member-panel/member-panel.component';
import { MessageInputComponent } from './components/message-input/message-input.component';
import { MessageListComponent } from './components/message-list/message-list.component';
import { ChatLayoutComponent } from './pages/chat-layout/chat-layout.component';

// Feature module tải lười (lazy) cho /chat.
@NgModule({
  declarations: [ChatLayoutComponent, GroupListComponent, ChatWindowComponent, MessageListComponent, MessageInputComponent,
    MemberPanelComponent, CreateGroupDialogComponent, EditGroupDialogComponent],
  imports: [SharedModule, ChatRoutingModule]
})
export class ChatModule {}
