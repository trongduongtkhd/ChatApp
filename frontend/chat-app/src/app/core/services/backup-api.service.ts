import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';

export interface BackupItem { id: string; status: string; message: string; updatedAt: string; checkpointMs?: number; }
export interface BackupSettings { period: string; retentionCount: number; walArchiveHours?: number; }
export interface RecoveryState { id?: string; status: string; message?: string; url?: string; }
export interface BackupOverview { enabled: boolean; items: BackupItem[]; settings: BackupSettings; recovery: RecoveryState; }
@Injectable({ providedIn: 'root' })
export class BackupApiService {
  private readonly base = '/api/admin/backups';
  constructor(private readonly http: HttpClient) {}
  list(): Observable<BackupOverview> { return this.http.get<BackupOverview>(this.base); }
  create(): Observable<{id: string}> { return this.http.post<{id: string}>(this.base, {}); }
  verify(id: string): Observable<{verified: boolean}> { return this.http.post<{verified: boolean}>(this.base + '/' + id + '/verify', {}); }
  restore(id: string): Observable<unknown> { return this.http.post(this.base + '/' + id + '/restore', {}); }
  cleanup(): Observable<unknown> { return this.http.delete(this.base + '/recovery'); }
  schedule(settings: BackupSettings): Observable<BackupSettings> { return this.http.put<BackupSettings>(this.base + '/settings', settings); }
}
