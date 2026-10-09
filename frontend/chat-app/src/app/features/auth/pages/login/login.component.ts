import { HttpErrorResponse } from '@angular/common/http';
import { Component } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { finalize } from 'rxjs/operators';
import { AuthService } from '../../../../core/services/auth.service';

// Trang đăng nhập (Login.dc.html). 401 → hộp đỏ "Tên đăng nhập hoặc mật khẩu không đúng."
@Component({
  selector: 'app-login',
  templateUrl: './login.component.html',
  styleUrls: ['../../auth-layout.scss']
})
export class LoginComponent {
  // Reactive Forms: form khai báo trong TypeScript, template chỉ gắn [formGroup]/formControlName.
  readonly form = this.fb.group({
    userName: ['', Validators.required],
    password: ['', Validators.required]
  });

  loading = false;
  error: string | null = null;

  constructor(
    private readonly fb: FormBuilder,
    private readonly auth: AuthService,
    private readonly router: Router
  ) {}

  invalid(name: string): boolean {
    const control = this.form.get(name);
    return !!control && control.invalid && control.touched;
  }

  submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched(); // hiện lỗi "bắt buộc" cho các ô chưa đụng tới
      return;
    }
    this.loading = true;
    this.error = null;
    this.auth.login(this.form.value)
      .pipe(finalize(() => (this.loading = false)))
      .subscribe({
        next: () => this.router.navigate(['/chat']),
        error: (err: HttpErrorResponse) => (this.error = this.describe(err))
      });
  }

  private describe(err: HttpErrorResponse): string {
    if (err.status === 401) {
      // Cố ý không nói sai tên hay sai mật khẩu (server cũng vậy) → không giúp kẻ xấu dò tên đăng nhập.
      return 'Tên đăng nhập hoặc mật khẩu không đúng.';
    }
    if (err.status === 0 || err.status >= 500) {
      // 0 = không tới được server; 502/504 = Gateway không gọi được identity-service.
      return 'Không kết nối được máy chủ. Thử lại sau ít phút.';
    }
    return 'Đăng nhập không thành công.';
  }
}
