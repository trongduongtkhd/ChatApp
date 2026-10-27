import { Injectable } from '@angular/core';
import { CanActivate, CanLoad, Router, UrlTree } from '@angular/router';
import { AuthService } from '../services/auth.service';

@Injectable({ providedIn: 'root' })
export class AdminGuard implements CanActivate, CanLoad {
  constructor(private readonly auth: AuthService, private readonly router: Router) {}
  canActivate(): boolean | UrlTree { return this.check(); }
  canLoad(): boolean | UrlTree { return this.check(); }
  private check(): boolean | UrlTree {
    return !this.auth.isLoggedIn() ? this.router.parseUrl('/auth/login')
      : this.auth.currentUser?.systemRole === 'SystemAdmin' ? true : this.router.parseUrl('/chat');
  }
}
