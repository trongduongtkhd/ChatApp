import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable, of, Subject, timer } from 'rxjs';
import { catchError, concatMap, last, map, take, takeWhile, tap } from 'rxjs/operators';
import { environment } from '../../../environments/environment';
import { CreateGroupRequest, Group, GroupDetail, UpdateGroupRequest, UserSnapshot } from '../models/group';

// REST /api/groups (group-service, qua Gateway). Component không gọi HttpClient trực tiếp mà gọi qua đây.
// Token do JwtInterceptor tự gắn; 401 do ErrorInterceptor xử lý; lỗi khác (403/404/409) ném về nơi gọi.
@Injectable({ providedIn: 'root' })
export class GroupApiService {
  private readonly base = `${environment.apiUrl}/groups`;

  // Phát sau khi sửa nhóm / xóa nhóm / xóa thành viên (kể cả tự rời) thành công → danh sách nhóm ở thanh bên
  // nạp lại (đổi tên, mất nhóm). chat-window và chat-layout không gọi thẳng nhau được (route con / route cha).
  private readonly changedSubject = new Subject<void>();
  readonly changed$: Observable<void> = this.changedSubject.asObservable();

  // Mã các chat riêng đã gặp trong phiên (Phần 11). Bị hủy kết bạn thì server chỉ trả 403 (không nói nhóm là
  // chat riêng nữa) → nhờ tập này chat-window biết hiện "Hai bạn không còn là bạn bè" thay cho câu dành cho nhóm.
  // Chỉ nằm trong bộ nhớ: F5 xong mà mở lại URL chat riêng cũ thì hiện câu chung.
  private readonly directIds = new Set<string>();

  constructor(private readonly http: HttpClient) {}

  // Nhóm mình tham gia (server xếp theo ngày tạo, mới nhất trước).
  list(): Observable<Group[]> {
    return this.http.get<Group[]>(this.base).pipe(tap(groups => groups.forEach(g => this.remember(g))));
  }

  // Chi tiết + thành viên. Không phải thành viên → 403.
  detail(groupId: string): Observable<GroupDetail> {
    return this.http.get<GroupDetail>(`${this.base}/${groupId}`).pipe(tap(d => this.remember(d.group)));
  }

  // Báo thanh bên nạp lại khi phát hiện thay đổi KHÔNG do mình gây ra (vd đang mở chat riêng thì bị hủy kết bạn → 403).
  notifyChanged(): void {
    this.changedSubject.next();
  }

  isKnownDirect(groupId: string): boolean {
    return this.directIds.has(groupId);
  }

  // Chờ nhóm xuất hiện trong GET /api/groups: hỏi mỗi giây, tối đa `tries` lần. Phát true khi thấy, false khi hết lượt.
  // Dùng sau khi chấp nhận kết bạn: chat riêng do group-service tạo SAU (identity → outbox → Kafka → group-service,
  // ~1 s) – eventual consistency. Lỗi mạng ở một lượt hỏi thì coi như "chưa thấy", hỏi tiếp lượt sau.
  waitForGroup(groupId: string, tries = 5): Observable<boolean> {
    return timer(0, 1000).pipe(
      take(tries),
      concatMap(() => this.list().pipe(catchError(() => of([] as Group[])))),
      map(groups => groups.some(g => g.id === groupId)),
      takeWhile(found => !found, true),   // thấy rồi thì dừng (vẫn phát lần true đó)
      last()
    );
  }

  private remember(g: Group): void {
    if (g.isDirect) {
      this.directIds.add(g.id);
    }
  }

  // Người tạo thành Owner (group-service phát member-added cho Owner).
  create(request: CreateGroupRequest): Observable<Group> {
    return this.http.post<Group>(this.base, request);
  }

  // Body kèm version; version cũ → 409 (Bước 9).
  update(groupId: string, request: UpdateGroupRequest): Observable<Group> {
    return this.http.put<Group>(`${this.base}/${groupId}`, request).pipe(tap(() => this.changedSubject.next()));
  }

  delete(groupId: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/${groupId}`).pipe(tap(() => this.changedSubject.next()));
  }

  // Tìm trong user_snapshots (bản sao từ Kafka identity.user-registered), tối đa 20 người.
  searchUsers(q: string): Observable<UserSnapshot[]> {
    return this.http.get<UserSnapshot[]>(`${this.base}/users/search`, { params: new HttpParams().set('q', q) });
  }

  addMember(groupId: string, userId: string): Observable<unknown> {
    return this.http.post(`${this.base}/${groupId}/members`, { userId });
  }

  // Owner xóa người khác, hoặc chính mình tự rời nhóm.
  removeMember(groupId: string, userId: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/${groupId}/members/${userId}`).pipe(tap(() => this.changedSubject.next()));
  }
}
