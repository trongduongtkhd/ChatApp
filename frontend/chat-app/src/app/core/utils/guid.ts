// GUID v7 (RFC 9562): 48 bit đầu = thời điểm (mili giây Unix), phần còn lại ngẫu nhiên.
// Cùng kiểu Id với server (Guid.CreateVersion7, Phần 2) → messageId tăng dần theo thời gian, index B-tree chèn gọn.
// Sinh Ở CLIENT, KHÔNG cần hỏi ai: 74 bit ngẫu nhiên → xác suất trùng coi như bằng 0 (định danh phi tập trung).
//
//  xxxxxxxx-xxxx-7xxx-yxxx-xxxxxxxxxxxx   (7 = version, y ∈ {8,9,a,b} = variant RFC)
//  |--- 48 bit thời gian ---|
export function uuidv7(): string {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes); // ngẫu nhiên mật mã, không dùng Math.random

  const ms = Date.now();
  // 48 bit = 6 byte, big-endian. Không dùng toán tử bit (chỉ 32 bit) → chia lấy từng byte.
  for (let i = 5, t = ms; i >= 0; i--, t = Math.floor(t / 256)) {
    bytes[i] = t % 256;
  }
  bytes[6] = (bytes[6] & 0x0f) | 0x70; // version 7
  bytes[8] = (bytes[8] & 0x3f) | 0x80; // variant 10xx

  const hex = Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
