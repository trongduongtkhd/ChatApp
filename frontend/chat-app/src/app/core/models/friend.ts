// Kiểu dữ liệu khớp DTO bạn bè của identity-service (Dtos/FriendDtos.cs). JSON camelCase.

// GET /api/friends, POST /api/friends/requests/{userId}/accept
// directGroupId: mã chat riêng, identity tự tính (cùng công thức với group-service) → mở /chat/{directGroupId}.
export interface Friend {
  userId: string;
  userName: string;
  displayName: string;
  since: string;
  directGroupId: string;
}

// GET /api/friends/requests/incoming | outgoing, POST /api/friends/requests
// userId là NGƯỜI KIA (người mời mình, hoặc người mình đã mời).
export interface FriendRequest {
  userId: string;
  userName: string;
  displayName: string;
  sentAt: string;
}
