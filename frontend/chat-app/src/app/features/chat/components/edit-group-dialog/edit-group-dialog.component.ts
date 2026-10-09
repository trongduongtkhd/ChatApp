import { HttpErrorResponse } from '@angular/common/http';
import { AfterViewInit, Component, ElementRef, EventEmitter, HostListener, Input, OnInit, Output, ViewChild } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
import { finalize } from 'rxjs/operators';
import { Group } from '../../../../core/models/group';
import { GroupApiService } from '../../../../core/services/group-api.service';

// "Sửa thông tin nhóm" (EditConflict.dc.html). Optimistic Locking nhìn từ người dùng (Phần 4):
// gửi kèm version lúc MỞ dialog; trong lúc mình đang gõ mà người khác lưu trước → server trả 409,
// KHÔNG ghi đè lên họ. Bấm "Tải lại bản mới" → nhận version mới (giữ nguyên chữ mình đang gõ) rồi mới lưu được.
@Component({
  selector: 'app-edit-group-dialog',
  templateUrl: './edit-group-dialog.component.html'
})
export class EditGroupDialogComponent implements OnInit, AfterViewInit {
  @Input() group!: Group;
  @Output() readonly closed = new EventEmitter<void>();
  @Output() readonly saved = new EventEmitter<Group>();

  @ViewChild('nameInput') nameInput?: ElementRef<HTMLInputElement>;

  // Cùng ràng buộc với UpdateGroupRequest bên group-service.
  readonly form = this.fb.group({
    name: ['', [Validators.required, Validators.maxLength(100)]],
    description: ['', Validators.maxLength(500)]
  });

  version = 0;            // version (xmin) đang cầm – gửi kèm PUT
  saving = false;
  reloading = false;
  error: string | null = null;
  conflict: Group | null = null; // bản MỚI NHẤT trên server khi bị 409

  constructor(private readonly fb: FormBuilder, private readonly api: GroupApiService) {}

  ngOnInit(): void {
    this.form.setValue({ name: this.group.name, description: this.group.description ?? '' });
    this.version = this.group.version;
  }

  ngAfterViewInit(): void {
    setTimeout(() => this.nameInput?.nativeElement.focus());
  }

  @HostListener('document:keydown.escape')
  close(): void {
    if (!this.saving) {
      this.closed.emit();
    }
  }

  fieldError(field: 'name' | 'description'): string | null {
    const c = this.form.get(field)!;
    if (!c.invalid || !c.touched) {
      return null;
    }
    return c.hasError('required') ? 'Không được để trống.' : `Tối đa ${field === 'name' ? 100 : 500} ký tự.`;
  }

  save(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.saving = true;
    this.error = null;
    const v = this.form.value;
    this.api.update(this.group.id, { name: v.name.trim(), description: v.description.trim() || null, version: this.version })
      .pipe(finalize(() => (this.saving = false)))
      .subscribe({
        next: g => this.saved.emit(g),
        error: (err: HttpErrorResponse) => this.onError(err)
      });
  }

  // Lấy bản mới nhất: version mới để lần lưu sau qua được; GIỮ chữ người dùng đang gõ (không bắt gõ lại).
  // Người dùng đã thấy "Tên hiện tại" của người kia → lưu tiếp là CỐ Ý ghi đè, không còn "lost update" ngầm.
  reloadLatest(): void {
    if (!this.conflict) {
      return;
    }
    this.version = this.conflict.version;
    this.conflict = null;
  }

  private onError(err: HttpErrorResponse): void {
    switch (err.status) {
      case 409:
        // Hỏi lại bản mới nhất để hiện "Tên hiện tại" (409 không kèm dữ liệu mới).
        this.reloading = true;
        this.api.detail(this.group.id).pipe(finalize(() => (this.reloading = false))).subscribe({
          next: d => (this.conflict = d.group),
          error: () => (this.error = 'Nhóm vừa được người khác sửa, nhưng không tải được bản mới. Đóng rồi mở lại.')
        });
        break;
      case 400:
        this.error = err.error?.title || 'Dữ liệu không hợp lệ.';
        break;
      case 403:
        this.error = 'Chỉ trưởng nhóm mới sửa được thông tin nhóm.';
        break;
      case 404:
        this.error = 'Nhóm không còn tồn tại.';
        break;
      default:
        this.error = 'Không kết nối được máy chủ. Thử lại sau ít phút.';
    }
  }
}
