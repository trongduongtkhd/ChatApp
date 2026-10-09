import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { UnreadCounter } from '../models/unread-counter';

// REST notification-service: số tin chưa đọc + đánh dấu đã đọc.
@Injectable({ providedIn: 'root' })
export class NotificationApiService {
  private readonly base = `${environment.apiUrl}/notifications`;

  constructor(private readonly http: HttpClient) {}

  // Số chưa đọc của mọi nhóm mình tham gia (nguồn sự thật, dùng khi mở app và sau mỗi lần (re)connect hub).
  unread(): Observable<UnreadCounter[]> {
    return this.http.get<UnreadCounter[]>(`${this.base}/unread`);
  }

  // "Đã đọc tới seq này". Server chỉ nâng mốc (GREATEST): request cũ đến muộn không làm mốc lùi.
  // Không phải thành viên (theo bản sao của notification-service) → 403.
  markRead(groupId: string, lastReadSequence: number): Observable<UnreadCounter> {
    return this.http.post<UnreadCounter>(`${this.base}/groups/${groupId}/read`, { lastReadSequence });
  }
}
