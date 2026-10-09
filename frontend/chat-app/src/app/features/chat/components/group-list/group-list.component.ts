import { ChangeDetectionStrategy, Component, ElementRef, Input } from '@angular/core';
import { Group, groupTitle } from '../../../../core/models/group';
import { Message } from '../../../../core/models/message';

// Một dòng trong danh sách nhóm: nhóm + tin cuối + số chưa đọc.
// last: undefined = đang tải, null = chưa có tin (hoặc không tải được).
export interface GroupListItem {
  group: Group;
  last?: Message | null;
  unread: number;
}

export type GroupListTab = 'all' | 'unread' | 'groups';

// Danh sách chat ở thanh bên: ô tìm kiếm + 3 tab + các dòng. Chỉ HIỂN THỊ: dữ liệu do ChatLayout nạp và truyền vào.
@Component({
  selector: 'app-group-list',
  templateUrl: './group-list.component.html',
  styles: [':host { display: flex; flex-direction: column; min-height: 0; flex: 1; }'],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class GroupListComponent {
  @Input() items: GroupListItem[] = [];
  @Input() myId = '';
  @Input() now = Date.now();
  @Input() onlineGroupId: string | null = null;

  readonly tabs: { id: GroupListTab; label: string }[] = [
    { id: 'all', label: 'Tất cả' },
    { id: 'unread', label: 'Chưa đọc' },
    // Nhóm = bỏ chat riêng 2 người (isDirect, Phần 11).
    { id: 'groups', label: 'Nhóm' }
  ];
  readonly titleOf = groupTitle;
  tab: GroupListTab = 'all';
  query = '';

  // Ở tab "Chưa đọc", mở một nhóm thì nhóm đó được đánh dấu đã đọc → nếu lọc đúng "unread > 0" nó biến
  // khỏi danh sách ngay dưới tay người dùng. Giữ lại nhóm vừa bấm tới khi đổi tab.
  private keepId: string | null = null;

  constructor(private readonly host: ElementRef<HTMLElement>) {}

  get unreadGroups(): number {
    return this.items.filter(i => i.unread > 0).length;
  }

  // Lọc phía client (danh sách nhóm đã nằm sẵn trong bộ nhớ): theo tab rồi theo ô tìm kiếm.
  // Bỏ dấu để "do an" tìm ra "Đồ án". Chat riêng tìm theo tên người kia.
  get filtered(): GroupListItem[] {
    const q = normalize(this.query.trim());
    return this.items.filter(i =>
      (this.tab !== 'unread' || i.unread > 0 || i.group.id === this.keepId) &&
      (this.tab !== 'groups' || !i.group.isDirect) &&
      (!q || normalize(groupTitle(i.group)).includes(q)));
  }

  selectTab(tab: GroupListTab): void {
    this.tab = tab;
    this.keepId = null;
  }

  // Bàn phím cho tablist (mẫu ARIA): ← → chuyển tab và chuyển focus theo.
  onTabKey(e: KeyboardEvent, index: number): void {
    const step = e.key === 'ArrowRight' ? 1 : e.key === 'ArrowLeft' ? -1 : 0;
    if (!step) {
      return;
    }
    e.preventDefault();
    const next = (index + step + this.tabs.length) % this.tabs.length;
    this.selectTab(this.tabs[next].id);
    this.host.nativeElement.querySelectorAll<HTMLElement>('[role="tab"]')[next]?.focus();
  }

  onOpen(item: GroupListItem): void {
    this.keepId = item.group.id;
  }

  trackById(_: number, item: GroupListItem): string {
    return item.group.id;
  }
}

function normalize(s: string): string {
  return s.normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/đ/g, 'd').replace(/Đ/g, 'D').toLowerCase();
}
