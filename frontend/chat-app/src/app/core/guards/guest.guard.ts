import { Injectable } from '@angular/core';
import { CanActivate, Router, UrlTree } from '@angular/router';
import { AuthService } from '../services/auth.service';

// Đã đăng nhập thì không vào /auth/login, /auth/register nữa → đưa thẳng về /chat.
@Injectable({ providedIn: 'root' })
export class GuestGuard implements CanActivate {
  constructor(private readonly auth: AuthService, private readonly router: Router) {}

  canActivate(): boolean | UrlTree {
    return this.auth.isLoggedIn() ? this.router.parseUrl('/chat') : true;
  }
}
