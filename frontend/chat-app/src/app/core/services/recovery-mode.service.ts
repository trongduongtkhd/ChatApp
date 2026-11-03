import { HttpClient } from '@angular/common/http';
import { Inject, Injectable } from '@angular/core';
import { DOCUMENT } from '@angular/common';
import { of } from 'rxjs';
import { catchError, shareReplay, tap } from 'rxjs/operators';
@Injectable({ providedIn: 'root' })
export class RecoveryModeService {
  readonly mode$ = this.http.get<{recoveryMode: boolean; backupId?: string}>('/api/system/mode')
    .pipe(catchError(() => of({recoveryMode: false})), tap(mode => this.document.body.classList.toggle('ca-recovery-mode', mode.recoveryMode)), shareReplay(1));
  constructor(private readonly http: HttpClient, @Inject(DOCUMENT) private readonly document: Document) {}
}
