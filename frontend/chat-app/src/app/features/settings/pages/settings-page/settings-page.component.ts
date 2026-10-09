import { Component } from '@angular/core';
import { AuthService } from '../../../../core/services/auth.service';
import { ThemePreference, ThemeService } from '../../../../core/services/theme.service';

// Trang Cài đặt (Bước 9b): thẻ Hồ sơ (chỉ xem – lấy từ claim JWT, không có API sửa hồ sơ) + thẻ Giao diện.
@Component({
  selector: 'app-settings-page',
  templateUrl: './settings-page.component.html'
})
export class SettingsPageComponent {
  readonly user = this.auth.currentUser;
  readonly preference$ = this.theme.preference$;

  readonly options: { value: ThemePreference; label: string; hint: string }[] = [
    { value: 'light', label: 'Sáng', hint: 'Luôn dùng nền sáng' },
    { value: 'dark', label: 'Tối', hint: 'Luôn dùng nền tối' },
    { value: 'system', label: 'Hệ thống', hint: 'Theo cài đặt sáng/tối của máy' }
  ];

  constructor(private readonly auth: AuthService, private readonly theme: ThemeService) {}

  choose(value: ThemePreference): void {
    this.theme.setPreference(value);
  }

  logout(): void {
    this.auth.logout();
  }
}
