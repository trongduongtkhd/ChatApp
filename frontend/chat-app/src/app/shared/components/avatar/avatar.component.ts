import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

// Ảnh đại diện bằng chữ viết tắt (không có ảnh thật). Chấm xanh = đang online (chỉ dùng --online).
// kind 'group': chữ đầu 2 từ đầu ("Đồ án Phân tán" → "ĐA").
// kind 'user' : chữ đầu của TÊN – từ cuối trong tên tiếng Việt ("Trần Thị Lan" → "L").
@Component({
  selector: 'app-avatar',
  template: `<span class="ca-avatar" [ngClass]="size" aria-hidden="true">{{ initials }}<span *ngIf="online" class="dot"></span></span>`,
  styles: [':host { display: inline-flex; flex: none; }'],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class AvatarComponent {
  @Input() name: string | null = '';
  @Input() kind: 'group' | 'user' = 'user';
  @Input() size: '' | 'sm' | 'md' | 'lg' = '';
  @Input() online = false;

  get initials(): string {
    const words = (this.name || '?').trim().split(/\s+/);
    // Array.from tách theo ký tự Unicode (không cắt đôi ký tự có dấu ghép).
    const first = (w: string) => Array.from(w)[0] || '';
    const text = this.kind === 'group'
      ? first(words[0]) + (words.length > 1 ? first(words[1]) : '')
      : first(words[words.length - 1]);
    return text.toLocaleUpperCase('vi');
  }
}
