import { HttpErrorResponse } from '@angular/common/http';
import { AfterViewInit, Component, ElementRef, EventEmitter, HostListener, Input, OnDestroy, Output, ViewChild } from '@angular/core';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { forkJoin, Observable, of, Subscription } from 'rxjs';
import { catchError, debounceTime, distinctUntilChanged, finalize, map, switchMap, tap } from 'rxjs/operators';
import { Group, UserSnapshot } from '../../../../core/models/group';
import { GroupApiService } from '../../../../core/services/group-api.service';

// Hộp thoại "Tạo nhóm mới" (CreateGroup.dc.html).
// Tạo = POST /api/groups (mình thành Owner) rồi POST /members cho từng người đã chọn.
// Không có API "tạo nhóm kèm thành viên" → 2 bước, có thể tạo được nhóm nhưng thêm hụt vài người.
@Component({
  selector: 'app-create-group-dialog',
  templateUrl: './create-group-dialog.component.html'
})
export class CreateGroupDialogComponent implements AfterViewInit, OnDestroy {
  @Input() myId = '';
  @Output() readonly closed = new EventEmitter<void>();
  @Output() readonly created = new EventEmitter<Group>();

  @ViewChild('nameInput') nameInput?: ElementRef<HTMLInputElement>;

  // Cùng ràng buộc với CreateGroupRequest bên group-service.
  readonly form = this.fb.group({
    name: ['', [Validators.required, Validators.maxLength(100)]],
    description: ['', Validators.maxLength(500)]
  });
  readonly memberQuery = new FormControl('');

  results: UserSnapshot[] = [];
  selected: UserSnapshot[] = [];
  searching = false;
  searchError = false;

  saving = false;
  error: string | null = null;
  // Nhóm đã tạo nhưng còn người chưa thêm được → giữ dialog mở để báo, bấm "Xong" mới đóng.
  createdGroup: Group | null = null;
  failedNames: string[] = [];

  private readonly sub: Subscription;

  constructor(private readonly fb: FormBuilder, private readonly api: GroupApiService) {
    // Gõ tới đâu tìm tới đó, nhưng:
    // - debounceTime(300): chờ người dùng ngừng gõ 300 ms mới gọi → "lan" là 1 request, không phải 3;
    // - switchMap: có từ khóa mới thì HỦY request cũ → kết quả cũ về muộn không đè kết quả mới.
    this.sub = this.memberQuery.valueChanges.pipe(
      map((q: string) => q.trim()),
      debounceTime(300),
      distinctUntilChanged(),
      tap(() => (this.searchError = false)),
      switchMap(q => (q ? this.search(q) : of([])))
    ).subscribe(users => (this.results = users.filter(u => u.userId !== this.myId)));
  }

  ngAfterViewInit(): void {
    // Thuộc tính autofocus không có tác dụng với phần tử chèn sau khi trang đã tải → tự focus.
    setTimeout(() => this.nameInput?.nativeElement.focus());
  }

  ngOnDestroy(): void {
    this.sub.unsubscribe();
  }

  @HostListener('document:keydown.escape')
  close(): void {
    if (this.saving) {
      return;
    }
    if (this.createdGroup) {
      this.created.emit(this.createdGroup);
    } else {
      this.closed.emit();
    }
  }

  isSelected(u: UserSnapshot): boolean {
    return this.selected.some(s => s.userId === u.userId);
  }

  toggle(u: UserSnapshot): void {
    this.selected = this.isSelected(u) ? this.selected.filter(s => s.userId !== u.userId) : [...this.selected, u];
  }

  fieldError(field: 'name' | 'description'): string | null {
    const c = this.form.get(field)!;
    if (!c.invalid || !c.touched) {
      return null;
    }
    return c.hasError('required') ? 'Không được để trống.' : `Tối đa ${field === 'name' ? 100 : 500} ký tự.`;
  }

  submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.saving = true;
    this.error = null;
    const v = this.form.value;
    const members = this.selected;

    this.api.create({ name: v.name.trim(), description: v.description.trim() || null }).pipe(
      // Thêm thành viên SONG SONG; mỗi lời gọi tự bắt lỗi của mình (trả về người bị lỗi)
      // để một người lỗi không làm hỏng cả forkJoin.
      switchMap(group => (members.length ? forkJoin(members.map(u => this.api.addMember(group.id, u.userId).pipe(
        map(() => null),
        catchError(() => of(u))
      ))) : of([])).pipe(map(failed => ({ group, failed: failed.filter((f): f is UserSnapshot => f !== null) })))),
      finalize(() => (this.saving = false))
    ).subscribe({
      next: ({ group, failed }) => {
        if (failed.length === 0) {
          this.created.emit(group);
          return;
        }
        this.createdGroup = group;
        this.failedNames = failed.map(u => u.displayName);
      },
      error: (err: HttpErrorResponse) => {
        this.error = err.status === 0 || err.status >= 500
          ? 'Không kết nối được máy chủ. Thử lại sau ít phút.'
          : err.error?.title || 'Không tạo được nhóm.';
      }
    });
  }

  private search(q: string): Observable<UserSnapshot[]> {
    this.searching = true;
    return this.api.searchUsers(q).pipe(
      catchError(() => {
        this.searchError = true;
        return of([] as UserSnapshot[]);
      }),
      finalize(() => (this.searching = false))
    );
  }
}
