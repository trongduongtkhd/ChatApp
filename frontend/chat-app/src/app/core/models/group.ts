// Kiểu dữ liệu khớp DTO của group-service (Dtos/GroupDto.cs, MemberRequests.cs). JSON camelCase.

export type GroupRole = 'Owner' | 'Member';

// GET /api/groups, POST /api/groups, PUT /api/groups/{id}
export interface Group {
  id: string;
  name: string;
  description: string | null;
  ownerId: string;   // chat riêng: chỉ để lấp cột, KHÔNG mang quyền – quyền xét theo myRole
  myRole: GroupRole;
  version: number;   // xmin hiện tại – gửi lại khi sửa nhóm (Optimistic Locking, Bước 9)
  createdAt: string;
  updatedAt: string;
  isDirect: boolean; // Phần 11: chat riêng 2 người (tạo từ sự kiện kết bạn), name rỗng
  peer: Peer | null; // chat riêng: người kia; nhóm thường: null
}

// Người kia trong chat riêng. Tên null = bản sao user_snapshots của group-service chưa có (eventual consistency).
export interface Peer {
  userId: string;
  userName: string | null;
  displayName: string | null;
}

// Tên hiển thị của một cuộc trò chuyện: nhóm thường = tên nhóm; chat riêng = tên người kia.
export function groupTitle(g: Group): string {
  return g.isDirect ? g.peer?.displayName || g.peer?.userName || 'Đang đồng bộ…' : g.name;
}

// userName/displayName = null khi bản sao user_snapshots chưa đồng bộ (eventual consistency).
export interface Member {
  userId: string;
  userName: string | null;
  displayName: string | null;
  role: GroupRole;
  joinedAt: string;
}

// GET /api/groups/{id}
export interface GroupDetail {
  group: Group;
  members: Member[];
}

// GET /api/groups/users/search?q=
export interface UserSnapshot {
  userId: string;
  userName: string;
  displayName: string;
}

export interface CreateGroupRequest {
  name: string;
  description: string | null;
}

export interface UpdateGroupRequest {
  name: string;
  description: string | null;
  version: number;
}
