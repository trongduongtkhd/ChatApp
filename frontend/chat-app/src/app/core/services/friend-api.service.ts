import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { Friend, FriendRequest } from '../models/friend';

// REST /api/friends (identity-service, qua Gateway – Phần 11). Lời mời gọi theo userId của NGƯỜI KIA:
// cặp (mình, người kia) là định danh của quan hệ, "mình" server lấy từ JWT.
// Lỗi nghiệp vụ (400/403/404/409) server trả ProblemDetails có `title` tiếng Việt → nơi gọi hiển thị thẳng.
@Injectable({ providedIn: 'root' })
export class FriendApiService {
  private readonly base = `${environment.apiUrl}/friends`;

  constructor(private readonly http: HttpClient) {}

  list(): Observable<Friend[]> {
    return this.http.get<Friend[]>(this.base);
  }

  incoming(): Observable<FriendRequest[]> {
    return this.http.get<FriendRequest[]>(`${this.base}/requests/incoming`);
  }

  outgoing(): Observable<FriendRequest[]> {
    return this.http.get<FriendRequest[]>(`${this.base}/requests/outgoing`);
  }

  // 201; người kia đã mời mình / đã là bạn / đã mời → 409.
  invite(userId: string): Observable<FriendRequest> {
    return this.http.post<FriendRequest>(`${this.base}/requests`, { userId });
  }

  // 200 kèm directGroupId. Chấp nhận lần 2 vẫn 200 (idempotent). Chat riêng xuất hiện sau ~1 s (qua Kafka).
  accept(userId: string): Observable<Friend> {
    return this.http.post<Friend>(`${this.base}/requests/${userId}/accept`, {});
  }

  decline(userId: string): Observable<void> {
    return this.http.post<void>(`${this.base}/requests/${userId}/decline`, {});
  }

  // Hủy lời mời mình đã gửi.
  cancel(userId: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/requests/${userId}`);
  }

  // Hủy kết bạn: group-service gỡ cả 2 khỏi chat riêng (giữ lịch sử, kết bạn lại thì hiện lại).
  remove(userId: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/${userId}`);
  }
}
