// Khớp MessageDto của chat-service (REST lịch sử và SignalR ReceiveMessage).
// Sắp xếp theo sequenceNumber (Redis INCR, Phần 7), KHÔNG theo createdAt (đồng hồ máy chủ, chỉ để hiển thị).
export interface Attachment {
  id: string;
  fileName: string;
  contentType: string;
  size: number;
  kind: 'image' | 'file';
}

export interface Message {
  id: string;             // messageId do client sinh (GUID v7) → chống gửi trùng
  groupId: string;
  senderId: string;
  senderName: string;
  content: string;
  sequenceNumber: number;
  createdAt: string;
  attachments?: Attachment[];
}

// Tin của mình CHƯA được server xác nhận (chưa có sequenceNumber) – bong bóng mờ "Đang gửi…"
// hoặc chữ đỏ "Không gửi được · Gửi lại". id sinh trước khi gửi, Gửi lại dùng LẠI id này.
export interface PendingMessage {
  id: string;
  groupId: string;
  content: string;
  status: 'sending' | 'failed';
  attachments?: Attachment[];
}

export function messagePreview(m: Message): string {
  if (m.content) return m.content;
  const files = m.attachments || [];
  return files.length ? files.every(f => f.kind === 'image') ? 'Đã gửi ảnh' : 'Đã gửi tệp: ' + files[0].fileName : '';
}
