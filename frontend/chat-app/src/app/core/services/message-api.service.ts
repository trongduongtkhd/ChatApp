import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { Message } from '../models/message';

// REST lịch sử tin nhắn (chat-service, qua Gateway → Nginx → 1 trong 2 bản).
@Injectable({ providedIn: 'root' })
export class MessageApiService {
  constructor(private readonly http: HttpClient) {}

  // Phân trang keyset: trả tối đa `limit` tin có seq < beforeSeq, xếp cũ → mới.
  // Không truyền beforeSeq = trang mới nhất. Trang rỗng = hết. Không phải thành viên → 403.
  history(groupId: string, beforeSeq?: number, limit = 50): Observable<Message[]> {
    let params = new HttpParams().set('limit', String(limit));
    if (beforeSeq !== undefined) {
      params = params.set('beforeSeq', String(beforeSeq));
    }
    return this.http.get<Message[]>(`${environment.apiUrl}/chat/groups/${groupId}/messages`, { params });
  }
}
