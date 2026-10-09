// Khớp UnreadCounterDto của notification-service (GET /api/notifications/unread, POST .../read).
export interface UnreadCounter {
  groupId: string;
  unreadCount: number;
  lastReadSequence: number; // mốc "đã đọc tới seq này" (chỉ tăng – GREATEST ở server, Phần 9)
}

// SignalR /hubs/notifications → UnreadCountChanged(groupId, unreadCount)
export interface UnreadChange {
  groupId: string;
  unreadCount: number;
}
