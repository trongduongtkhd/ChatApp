import { Component } from '@angular/core';
import { ThemeService } from './core/services/theme.service';
import { ChatHubService } from './core/services/chat-hub.service';

@Component({
  selector: 'app-root',
  template: '<router-outlet></router-outlet>'
})
export class AppComponent {
  constructor(theme: ThemeService, chat: ChatHubService) {
    theme.init();
  }
}
