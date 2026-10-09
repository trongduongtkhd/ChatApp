import { Pipe, PipeTransform } from '@angular/core';

// Giờ ở góc phải mỗi dòng danh sách chat: hôm nay "22:24", hôm qua "Hôm qua",
// trong 7 ngày "T2"…"CN", cũ hơn "05/09" (khác năm thì "05/09/2025").
// Pipe "pure": chỉ tính lại khi tham số đổi. Truyền `now` (đổi mỗi phút) để qua nửa đêm "22:24" tự thành "Hôm qua".
@Pipe({ name: 'listTime' })
export class ListTimePipe implements PipeTransform {
  transform(iso: string | null | undefined, now: number = Date.now()): string {
    if (!iso) {
      return '';
    }
    const t = new Date(iso);
    const today = new Date(now);
    today.setHours(0, 0, 0, 0);
    const day = new Date(t);
    day.setHours(0, 0, 0, 0);
    const daysAgo = Math.round((today.getTime() - day.getTime()) / 86400000);
    const pad = (n: number) => String(n).padStart(2, '0');

    if (daysAgo <= 0) {
      return `${pad(t.getHours())}:${pad(t.getMinutes())}`;
    }
    if (daysAgo === 1) {
      return 'Hôm qua';
    }
    if (daysAgo < 7) {
      const d = t.getDay();
      return d === 0 ? 'CN' : `T${d + 1}`;
    }
    const dm = `${pad(t.getDate())}/${pad(t.getMonth() + 1)}`;
    return t.getFullYear() === today.getFullYear() ? dm : `${dm}/${t.getFullYear()}`;
  }
}
