import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { ChatWindowComponent } from './components/chat-window/chat-window.component';
import { ChatLayoutComponent } from './pages/chat-layout/chat-layout.component';

// /chat → khung chính (thanh bên + router-outlet); /chat/:groupId → ChatWindow hiện trong outlet đó.
// Đổi nhóm chỉ thay phần bên phải, thanh bên (và dữ liệu của nó) giữ nguyên.
const routes: Routes = [
  {
    path: '',
    component: ChatLayoutComponent,
    children: [{ path: ':groupId', component: ChatWindowComponent }]
  }
];

@NgModule({
  imports: [RouterModule.forChild(routes)],
  exports: [RouterModule]
})
export class ChatRoutingModule {}
