import {
  AfterViewChecked, ChangeDetectionStrategy, Component, ElementRef, EventEmitter, Input, OnChanges, Output, SimpleChanges, ViewChild
} from '@angular/core';
import { Message, PendingMessage } from '../../../../core/models/message';

// Một khối hiển thị: mốc ngày, hoặc một CỤM tin liên tiếp của cùng một người.
// time: giờ của tin cuối cụm, hiện nhỏ dưới bong bóng cuối.
type Row =
  | { kind: 'day'; key: string; label: string }
  | { kind: 'in'; key: string; senderName: string; messages: Message[]; time: string }
  | { kind: 'out'; key: string; messages: Message[]; time: string };

// Cùng người gửi nhưng cách nhau từ 15 phút trở lên thì tách cụm mới (mỗi cụm một dòng giờ).
const CLUSTER_GAP_MS = 15 * 60 * 1000;

// Danh sách tin (Main.dc.html). Nhận mảng tin ĐÃ sắp theo sequenceNumber từ chat-window, chỉ lo hiển thị + cuộn.
@Component({
  selector: 'app-message-list',
  templateUrl: './message-list.component.html',
  styles: [':host { display: flex; flex-direction: column; flex: 1; min-height: 0; }'],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class MessageListComponent implements OnChanges, AfterViewChecked {
  @Input() messages: Message[] = [];
  @Input() myId = '';
  @Input() onlineIds: ReadonlySet<string> = new Set();
  @Input() pending: PendingMessage[] = [];
  @Output() readonly resend = new EventEmitter<string>();
  @Input() loadingOlder = false;
  @Input() olderError = false;
  @Input() reachedStart = false;
  @Output() readonly loadOlder = new EventEmitter<void>();

  @ViewChild('thread', { static: true }) thread!: ElementRef<HTMLElement>;

  rows: Row[] = [];
  deliveredId: string | null = null;

  // Việc cuộn cần làm SAU khi DOM đã vẽ xong danh sách mới (ngAfterViewChecked).
  private pendingScroll: { mode: 'bottom' } | { mode: 'keep'; height: number; top: number } | null = null;
  private autoScheduled = false;

  ngOnChanges(changes: SimpleChanges): void {
    // Mình vừa bấm Gửi (thêm tin chờ) → luôn cuộn xuống đáy để thấy tin của mình.
    const pc = changes.pending;
    if (pc && (pc.currentValue?.length ?? 0) > (pc.previousValue?.length ?? 0)) {
      this.pendingScroll = { mode: 'bottom' };
    }
    if (changes.messages) {
      this.onMessagesChange(changes.messages.previousValue ?? [], changes.messages.currentValue ?? []);
    }
    // "Đã gửi ✓" chỉ ở tin cuối cùng của mình, và chỉ khi không còn tin nào đang chờ.
    this.deliveredId = this.pending.length ? null : lastOwnId(this.messages, this.myId);
  }

  private onMessagesChange(prev: Message[], next: Message[]): void {
    const el = this.thread.nativeElement;

    if (prev.length === 0) {
      // Lần đầu mở nhóm → cuộn xuống tin mới nhất.
      this.pendingScroll = { mode: 'bottom' };
    } else if (next.length && next[0].sequenceNumber < prev[0].sequenceNumber) {
      // Chèn tin CŨ lên đầu: nội dung dài thêm phía trên → nếu không bù lại, màn hình bị "nhảy".
      // Ghi lại chiều cao + vị trí hiện tại, vẽ xong thì cuộn thêm đúng phần chiều cao vừa tăng.
      this.pendingScroll = { mode: 'keep', height: el.scrollHeight, top: el.scrollTop };
    } else if (el.scrollHeight - el.scrollTop - el.clientHeight < 80) {
      // Tin MỚI ở cuối mà người dùng đang ở đáy → bám theo xuống đáy. Đang đọc tin cũ thì để yên.
      this.pendingScroll = { mode: 'bottom' };
    }
    this.rows = buildRows(next, this.myId);
  }

  ngAfterViewChecked(): void {
    const el = this.thread.nativeElement;
    if (this.pendingScroll) {
      const p = this.pendingScroll;
      this.pendingScroll = null;
      el.scrollTop = p.mode === 'bottom' ? el.scrollHeight : el.scrollHeight - p.height + p.top;
    }
    // Ít tin quá, chưa có thanh cuộn → không thể "cuộn lên đầu" → tự tải tiếp trang cũ hơn.
    // Không emit ngay tại đây: đang giữa lượt kiểm tra thay đổi, cha đổi loadingOlder lúc này sẽ bị
    // Angular báo ExpressionChangedAfterItHasBeenChecked → dời sang lượt sau (setTimeout), chỉ hẹn 1 lần.
    if (this.messages.length && el.scrollHeight <= el.clientHeight && !this.autoScheduled) {
      this.autoScheduled = true;
      setTimeout(() => {
        this.autoScheduled = false;
        this.requestOlder();
      });
    }
  }

  onScroll(): void {
    if (this.thread.nativeElement.scrollTop < 60) {
      this.requestOlder();
    }
  }

  requestOlder(): void {
    if (!this.loadingOlder && !this.reachedStart && !this.olderError) {
      this.loadOlder.emit();
    }
  }

  retryOlder(): void {
    this.loadOlder.emit();
  }

  trackRow(_: number, row: Row): string {
    return row.key;
  }

  trackMsg(_: number, m: Message | PendingMessage): string {
    return m.id;
  }
}

// Gom tin liên tiếp của cùng người thành cụm (tách cụm khi đổi người, qua ngày, hoặc cách nhau ≥ 15 phút);
// chèn mốc ngày khi sang ngày. Mốc ngày và giờ dùng createdAt (chỉ để hiển thị);
// THỨ TỰ vẫn theo sequenceNumber (mảng đầu vào đã sắp).
function buildRows(messages: Message[], myId: string): Row[] {
  const rows: Row[] = [];
  let prev: Message | null = null;
  for (const m of messages) {
    const t = Date.parse(m.createdAt);
    const newDay = !prev || new Date(t).toDateString() !== new Date(Date.parse(prev.createdAt)).toDateString();
    if (newDay) {
      rows.push({ kind: 'day', key: 'd' + m.id, label: dayLabel(t) });
    }
    const split = newDay || t - Date.parse(prev!.createdAt) >= CLUSTER_GAP_MS;
    const last = rows[rows.length - 1];
    const mine = m.senderId === myId;
    if (!split && last.kind === 'out' && mine) {
      last.messages.push(m);
      last.time = clock(t);
    } else if (!split && last.kind === 'in' && !mine && last.messages[0].senderId === m.senderId) {
      last.messages.push(m);
      last.time = clock(t);
    } else if (mine) {
      rows.push({ kind: 'out', key: m.id, messages: [m], time: clock(t) });
    } else {
      rows.push({ kind: 'in', key: m.id, senderName: m.senderName, messages: [m], time: clock(t) });
    }
    prev = m;
  }
  return rows;
}

function lastOwnId(messages: Message[], myId: string): string | null {
  for (let i = messages.length - 1; i >= 0; i--) {
    if (messages[i].senderId === myId) {
      return messages[i].id;
    }
  }
  return null;
}

// Mốc ngày: "Hôm nay", "Hôm qua", "Thứ 2" / "Chủ nhật" (trong 7 ngày), "05/09/2026"
function dayLabel(t: number): string {
  const d = new Date(t);
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  const day = new Date(d);
  day.setHours(0, 0, 0, 0);
  const daysAgo = Math.round((today.getTime() - day.getTime()) / 86400000);
  if (daysAgo <= 0) {
    return 'Hôm nay';
  }
  if (daysAgo === 1) {
    return 'Hôm qua';
  }
  if (daysAgo < 7) {
    return d.getDay() === 0 ? 'Chủ nhật' : `Thứ ${d.getDay() + 1}`;
  }
  return `${pad(d.getDate())}/${pad(d.getMonth() + 1)}/${d.getFullYear()}`;
}

function clock(t: number): string {
  const d = new Date(t);
  return `${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function pad(n: number): string {
  return String(n).padStart(2, '0');
}
