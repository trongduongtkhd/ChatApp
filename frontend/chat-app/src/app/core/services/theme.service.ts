import { DOCUMENT } from '@angular/common';
import { Inject, Injectable } from '@angular/core';
import { BehaviorSubject, Observable } from 'rxjs';

// light / dark: người dùng tự chọn; system: theo cài đặt hệ điều hành (prefers-color-scheme).
export type ThemePreference = 'light' | 'dark' | 'system';

const STORAGE_KEY = 'chatapp.theme';

// Theme sáng/tối. Chỉ gắn/gỡ lớp .dark trên <body>; mọi màu đổi theo nhờ biến CSS trong styles.scss.
// Lựa chọn lưu ở localStorage của trình duyệt này (tiện ích cá nhân, không cần đồng bộ lên server).
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly query = window.matchMedia('(prefers-color-scheme: dark)');
  private readonly preferenceSubject = new BehaviorSubject<ThemePreference>(readPreference());
  readonly preference$: Observable<ThemePreference> = this.preferenceSubject.asObservable();

  constructor(@Inject(DOCUMENT) private readonly document: Document) {}

  init(): void {
    this.apply();
    // Đang ở "Hệ thống" mà người dùng đổi Windows sang tối/sáng → đổi theo ngay, không cần tải lại trang.
    // (Chọn Sáng/Tối thì bỏ qua sự kiện này: lựa chọn của người dùng thắng cài đặt hệ điều hành.)
    this.query.addEventListener('change', () => this.apply());
  }

  get preference(): ThemePreference {
    return this.preferenceSubject.value;
  }

  setPreference(preference: ThemePreference): void {
    try {
      localStorage.setItem(STORAGE_KEY, preference);
    } catch {
      // Trình duyệt chặn localStorage (chế độ riêng tư…) → vẫn đổi theme cho phiên này.
    }
    this.preferenceSubject.next(preference);
    this.apply();
  }

  private apply(): void {
    const p = this.preferenceSubject.value;
    const dark = p === 'dark' || (p === 'system' && this.query.matches);
    this.document.body.classList.toggle('dark', dark);
  }
}

function readPreference(): ThemePreference {
  try {
    const v = localStorage.getItem(STORAGE_KEY);
    return v === 'light' || v === 'dark' ? v : 'system'; // mặc định: theo hệ điều hành
  } catch {
    return 'system';
  }
}
