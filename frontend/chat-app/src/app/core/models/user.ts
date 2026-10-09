// Người dùng đang đăng nhập, đọc từ claim trong JWT (DESIGN mục 5: sub, name, display_name).
export interface CurrentUser {
  id: string;          // claim "sub" (UserId, GUID v7)
  userName: string;    // claim "name"
  displayName: string; // claim "display_name"
}

// POST /api/auth/login → LoginResponse (identity-service).
export interface LoginResponse {
  accessToken: string;
  expiresAt: string; // ISO 8601, vd "2026-10-08T12:40:00+00:00"
}

export interface LoginRequest {
  userName: string;
  password: string;
}

export interface RegisterRequest {
  userName: string;
  email: string;
  password: string;
  displayName: string;
}
