import { NgModule } from '@angular/core';
import { RouterModule } from '@angular/router';
import { SharedModule } from '../../shared/shared.module';
import { AdminShellComponent } from './admin-shell.component';
import { AdminUsersComponent } from './admin-users.component';
import { AdminGroupsComponent } from './admin-groups.component';
import { AdminBackupsComponent } from './admin-backups.component';

@NgModule({
  declarations: [AdminShellComponent, AdminUsersComponent, AdminGroupsComponent, AdminBackupsComponent],
  imports: [SharedModule, RouterModule.forChild([{ path: '', component: AdminShellComponent, children: [
    { path: '', pathMatch: 'full', redirectTo: 'users' },
    { path: 'users', component: AdminUsersComponent }, { path: 'groups', component: AdminGroupsComponent }, { path: 'backups', component: AdminBackupsComponent }
  ] }])]
})
export class AdminModule {}
