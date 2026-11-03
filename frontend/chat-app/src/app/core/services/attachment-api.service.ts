import { HttpClient, HttpEventType } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { DomSanitizer, SafeUrl } from '@angular/platform-browser';
import { BehaviorSubject, Observable, Subscription } from 'rxjs';
import { environment } from '../../../environments/environment';
import { Attachment } from '../models/message';
import { uuidv7 } from '../utils/guid';
import { AuthService } from './auth.service';

export interface AttachmentDraft {
  id: string; file: File; preview?: SafeUrl; previewUrl?: string; status: 'uploading' | 'ready' | 'failed';
  progress: number; error?: string; attachment?: Attachment; subscription?: Subscription;
}

// Bản nháp theo conversation, sống qua đổi phòng/tuần; logout dọn URL và request cục bộ.
@Injectable({ providedIn: 'root' })
export class AttachmentApiService {
  private readonly drafts = new Map<string, AttachmentDraft[]>();
  private readonly changedSubject = new BehaviorSubject(0);
  readonly changed$ = this.changedSubject.asObservable();
  constructor(private readonly http: HttpClient, auth: AuthService, private readonly sanitizer: DomSanitizer) {
    auth.currentUser$.subscribe(u => {
      if (!u) {
        this.drafts.forEach(list => list.forEach(d => { d.subscription?.unsubscribe(); if (d.previewUrl) URL.revokeObjectURL(d.previewUrl); }));
        this.drafts.clear(); this.notify();
      }
    });
  }
  list(group: string): AttachmentDraft[] { return this.drafts.get(group) || []; }
  private base(group: string): string { return `${environment.apiUrl}/chat/groups/${group}/attachments`; }
  private notify(): void { this.changedSubject.next(this.changedSubject.value + 1); }
  content(group: string, id: string): Observable<Blob> { return this.http.get(`${this.base(group)}/${id}/content`, { responseType: 'blob' }); }
  add(group: string, files: FileList): string | null {
    const incoming = Array.from(files), list = this.list(group);
    if (list.length + incoming.length > 5) return 'Mỗi tin tối đa 5 đính kèm.';
    if (list.reduce((s, d) => s + d.file.size, 0) + incoming.reduce((s, f) => s + f.size, 0) > 50 * 1024 * 1024) return 'Tổng file không vượt 50 MiB.';
    for (const f of incoming) {
      const image = /\.(jpe?g|png|webp)$/i.test(f.name);
      if (!/\.(jpe?g|png|webp|pdf|docx|xlsx|pptx|txt|zip)$/i.test(f.name)) return 'Hỗ trợ JPEG, PNG, WebP, PDF, DOCX, XLSX, PPTX, TXT và ZIP.';
      if (!f.size || f.size > (image ? 10 : 25) * 1024 * 1024) return image ? 'Ảnh phải có dữ liệu và không vượt 10 MiB.' : 'File phải có dữ liệu và không vượt 25 MiB.';
      if (f.name.length > 180) return 'Tên file không vượt 180 ký tự.';
    }
    const added = incoming.map(file => {
      const previewUrl = /\.(jpe?g|png|webp)$/i.test(file.name) ? URL.createObjectURL(file) : undefined;
      // Angular 12 chặn blob: ở img[src]. Chỉ trust URL do chính ứng dụng vừa tạo, không trust URL server/client đưa vào.
      return { id: uuidv7(), file, previewUrl, preview: previewUrl ? this.sanitizer.bypassSecurityTrustUrl(previewUrl) : undefined,
        status: 'uploading' as const, progress: 0 };
    });
    this.drafts.set(group, list.concat(added)); added.forEach(d => this.upload(group, d)); this.notify(); return null;
  }
  private upload(group: string, draft: AttachmentDraft): void {
    draft.status = 'uploading'; draft.error = undefined; draft.progress = 0; this.notify();
    draft.subscription = this.http.put<Attachment>(`${this.base(group)}/${draft.id}`, draft.file, {
      headers: { 'X-File-Name': encodeURIComponent(draft.file.name), 'Content-Type': 'application/octet-stream' },
      observe: 'events', reportProgress: true
    }).subscribe({
      next: e => {
        if (e.type === HttpEventType.UploadProgress) draft.progress = Math.round(e.loaded * 100 / (e.total || draft.file.size));
        if (e.type === HttpEventType.Response && e.body) { draft.attachment = e.body; draft.status = 'ready'; }
        this.notify();
      },
      error: e => { draft.status = 'failed'; draft.error = e.error?.title || 'Chưa xác nhận upload. Bấm thử lại để kiểm tra.'; this.notify(); }
    });
  }
  retry(group: string, draft: AttachmentDraft): void {
    draft.status = 'uploading'; this.notify();
    draft.subscription = this.http.get<{ state: string; attachment: Attachment }>(`${this.base(group)}/${draft.id}`).subscribe({
      next: r => {
        if (r.state === 'Ready') { draft.attachment = r.attachment; draft.status = 'ready'; this.notify(); }
        else if (r.state === 'Uploading') { draft.status = 'failed'; draft.error = 'Server đang xử lý upload. Hãy thử lại sau.'; this.notify(); }
        else { this.freshUpload(group, draft); }
      },
      error: e => { if (e.status === 404) this.freshUpload(group, draft); else { draft.status = 'failed'; draft.error = 'Chưa kiểm tra được upload. Hãy thử lại.'; this.notify(); } }
    });
  }
  private freshUpload(group: string, draft: AttachmentDraft): void { draft.id = uuidv7(); this.upload(group, draft); }
  remove(group: string, draft: AttachmentDraft): void {
    draft.subscription?.unsubscribe();
    if (draft.previewUrl) URL.revokeObjectURL(draft.previewUrl);
    this.drafts.set(group, this.list(group).filter(d => d !== draft));
    this.http.delete(`${this.base(group)}/${draft.id}`).subscribe({ error: () => undefined }); this.notify();
  }
  takeReady(group: string): Attachment[] {
    const list = this.list(group);
    if (list.some(d => d.status !== 'ready')) return [];
    const files = list.map(d => d.attachment!);
    list.forEach(d => { if (d.previewUrl) URL.revokeObjectURL(d.previewUrl); });
    this.drafts.delete(group); this.notify(); return files;
  }
}
