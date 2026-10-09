import { NgModule } from '@angular/core';
import { SharedModule } from '../../shared/shared.module';
import { SettingsPageComponent } from './pages/settings-page/settings-page.component';
import { SettingsRoutingModule } from './settings-routing.module';

// Feature module tải lười (lazy) cho /settings.
@NgModule({
  declarations: [SettingsPageComponent],
  imports: [SharedModule, SettingsRoutingModule]
})
export class SettingsModule {}
