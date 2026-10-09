import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnDestroy } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { finalize } from 'rxjs/operators';
import { AuthService } from '../../../../core/services/auth.service';

type Field = 'displayName' | 'userName' | 'email' | 'password';

// Trang đăng ký (Register.dc.html). 409 → lỗi dưới ô Tên đăng nhập (hoặc Email).
// Kiểm tra phía client CHỈ để báo lỗi sớm cho người dùng; server vẫn kiểm tra lại (RegisterRequest),
// vì request có thể được gửi thẳng bằng Postman/curl, bỏ qua giao diện.
@Component({
  selector: 'app-register',
  templateUrl: './register.component.html',
  styleUrls: ['../../auth-layout.scss']
})
export class RegisterComponent implements OnDestroy {
  // Cùng ràng buộc với RegisterRequest bên identity-service.
  readonly form = this.fb.group({
    displayName: ['', [Validators.required, Validators.maxLength(100)]],
    userName: ['', [Validators.required, Validators.minLength(3), Validators.maxLength(50), Validators.pattern(/^[a-zA-Z0-9_.]+$/)]],
    email: ['', [Validators.required, Validators.email, Validators.maxLength(100)]],
    password: ['', [Validators.required, Validators.minLength(6), Validators.maxLength(100)]]
  });

  loading = false;
  error: string | null = null;
  // Lỗi do SERVER trả về, gắn theo từng ô (409 trùng tên/email, 400 từ DataAnnotations).
  serverErrors: Partial<Record<Field, string>> = {};

  private readonly sub: Subscription;

  constructor(
    private readonly fb: FormBuilder,
    private readonly auth: AuthService,
    private readonly router: Router
  ) {
    // Người dùng sửa lại ô nào thì xóa lỗi server của ô đó.
    this.sub = this.form.valueChanges.subscribe(() => {
      for (const field of Object.keys(this.serverErrors) as Field[]) {
        if (this.form.get(field)?.dirty) {
          delete this.serverErrors[field];
        }
      }
    });
  }

  ngOnDestroy(): void {
    this.sub.unsubscribe();
  }

  // Thông báo lỗi của một ô (ưu tiên lỗi server), null nếu ô hợp lệ hoặc chưa đụng tới.
  errorOf(field: Field): string | null {
    if (this.serverErrors[field]) {
      return this.serverErrors[field]!;
    }
    const c = this.form.get(field)!;
    if (!c.invalid || !c.touched) {
      return null;
    }
    if (c.hasError('required')) {
      return 'Không được để trống.';
    }
    switch (field) {
      case 'userName':
        return 'Từ 3 đến 50 ký tự, chỉ gồm chữ không dấu, số, "_" và ".".';
      case 'email':
        return 'Email không hợp lệ.';
      case 'password':
        return 'Mật khẩu tối thiểu 6 ký tự.';
      default:
        return 'Tối đa 100 ký tự.';
    }
  }

  submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.loading = true;
    this.error = null;
    this.serverErrors = {};
    this.form.markAsPristine();

    const v = this.form.value;
    this.auth.register({
      displayName: v.displayName.trim(),
      userName: v.userName.trim(),
      email: v.email.trim(),
      password: v.password
    })
      .pipe(finalize(() => (this.loading = false)))
      .subscribe({
        // register() đã tự đăng nhập → vào thẳng khung chat.
        next: () => this.router.navigate(['/chat']),
        error: (err: HttpErrorResponse) => this.handleError(err)
      });
  }

  private handleError(err: HttpErrorResponse): void {
    if (err.status === 409) {
      // identity-service trả ProblemDetails.title: "UserName đã tồn tại" | "Email đã tồn tại" |
      // "UserName hoặc Email đã tồn tại" (2 request đăng ký trùng đến cùng lúc, unique index chặn).
      const title: string = err.error?.title ?? '';
      if (title.startsWith('Email')) {
        this.serverErrors.email = 'Email này đã được dùng.';
      } else if (title.includes('hoặc')) {
        this.serverErrors.userName = 'Tên đăng nhập hoặc email đã có người dùng.';
      } else {
        this.serverErrors.userName = 'Tên đăng nhập này đã có người dùng.';
      }
      return;
    }
    if (err.status === 400 && err.error?.errors) {
      // [ApiController] trả ValidationProblemDetails: errors = { "UserName": ["..."], ... } → gắn vào ô tương ứng.
      for (const [key, messages] of Object.entries(err.error.errors as Record<string, string[]>)) {
        const field = (key.charAt(0).toLowerCase() + key.slice(1)) as Field;
        this.serverErrors[field] = messages[0];
      }
      return;
    }
    this.error = err.status === 0 || err.status >= 500
      ? 'Không kết nối được máy chủ. Thử lại sau ít phút.'
      : 'Đăng ký không thành công.';
  }
}
