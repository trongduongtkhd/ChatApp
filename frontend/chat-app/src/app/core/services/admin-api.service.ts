import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';

export interface Page<T> { items: T[]; total: number; page: number; pageSize: number; }
export interface AdminUser {
  id: string; userName: string; displayName: string; email: string; createdAt: string;
  systemRole: string; isLocked: boolean; lockedAt: string | null; lockedBy: string | null;
  lockReason: string | null; version: number;
}
export interface AuditEntry { id: string; actorId: string; action: string; reason?: string; createdAt: string; }
export interface AdminGroup {
  id: string; name: string; description: string | null; ownerId: string; ownerName?: string;
  requiresApproval: boolean; createdAt: string; memberCount: number;
}
export interface AdminMember { userId: string; userName: string | null; displayName: string | null; role: string; joinedAt: string; }
export interface AdminGroupDetail { group: AdminGroup; members: Page<AdminMember>; audit: AuditEntry[]; }
export function describeApiError(err: HttpErrorResponse): string {
  return err.error?.title || (err.status === 403 ? 'Bạn không có quyền thực hiện thao tác này.'
    : err.status === 409 ? 'Dữ liệu đã thay đổi. Hãy tải lại.' : 'Không kết nối được máy chủ. Hãy thử lại.');
}

@Injectable({ providedIn: 'root' })
export class AdminApiService {
  private readonly base = environment.apiUrl + '/admin';
  constructor(private readonly http: HttpClient) {}
  users(q: string, locked: string, page: number): Observable<Page<AdminUser>> {
    let params = new HttpParams().set('q', q).set('page', String(page)).set('pageSize', '20');
    if (locked) params = params.set('locked', locked);
    return this.http.get<Page<AdminUser>>(this.base + '/users', { params });
  }
  user(id: string): Observable<{ user: AdminUser; audit: AuditEntry[] }> {
    return this.http.get<{ user: AdminUser; audit: AuditEntry[] }>(this.base + '/users/' + id);
  }
  lock(user: AdminUser, isLocked: boolean, reason: string): Observable<AdminUser> {
    return this.http.put<AdminUser>(this.base + '/users/' + user.id + '/lock', { isLocked, version: user.version, reason });
  }
  groups(q: string, page: number): Observable<Page<AdminGroup>> {
    return this.http.get<Page<AdminGroup>>(this.base + '/groups', { params: new HttpParams().set('q', q).set('page', String(page)) });
  }
  group(id: string, page: number): Observable<AdminGroupDetail> {
    return this.http.get<AdminGroupDetail>(this.base + '/groups/' + id, { params: new HttpParams().set('page', String(page)) });
  }
}
