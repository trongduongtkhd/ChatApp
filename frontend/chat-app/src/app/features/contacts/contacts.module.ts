import { NgModule } from '@angular/core';
import { SharedModule } from '../../shared/shared.module';
import { ContactsPageComponent } from './pages/contacts-page/contacts-page.component';
import { ContactsRoutingModule } from './contacts-routing.module';

// Feature module tải lười (lazy) cho /contacts (Phần 11).
@NgModule({
  declarations: [ContactsPageComponent],
  imports: [SharedModule, ContactsRoutingModule]
})
export class ContactsModule {}
