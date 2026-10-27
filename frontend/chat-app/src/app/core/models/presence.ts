export interface PresenceSnapshot {
  known: boolean;
  version: number;
  heartbeatSeconds: number;
  leaseSeconds: number;
  onlineUserIds: string[];
  friendIds: string[];
  // Số thành viên KHÁC người xem đang online, theo groupId.
  onlineCountsByGroup: { [groupId: string]: number };
}

export interface TypingNotice {
  groupId: string;
  userId: string;
  displayName: string;
  isTyping: boolean;
}
