// Khớp MessageDto của chat-service (REST lịch sử và SignalR ReceiveMessage).
// Sắp xếp theo sequenceNumber (Redis INCR, Phần 7), KHÔNG theo createdAt (đồng hồ máy chủ, chỉ để hiển thị).
export interface Message {
  id: string;             // messageId do client sinh (GUID v7) → chống gửi trùng
  groupId: string;
  senderId: string;
  senderName: string;
  content: string;
  sequenceNumber: number;
  createdAt: string;
}

// Tin của mình CHƯA được server xác nhận (chưa có sequenceNumber) – bong bóng mờ "Đang gửi…"
// hoặc chữ đỏ "Không gửi được · Gửi lại". id sinh trước khi gửi, Gửi lại dùng LẠI id này.
export interface PendingMessage {
  id: string;
  groupId: string;
  content: string;
  status: 'sending' | 'failed';
}
