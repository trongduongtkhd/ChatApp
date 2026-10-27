// Ngày lịch Việt Nam; tính thứ bằng UTC để không phụ thuộc timezone của trình duyệt.
export function vietnamToday(): string {
  return new Date(Date.now() + 7 * 3600000).toISOString().slice(0, 10);
}
export function mondayFor(date: string): string | null {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) return null;
  const parts = date.split('-').map(Number);
  const day = new Date(Date.UTC(parts[0], parts[1] - 1, parts[2]));
  if (day.toISOString().slice(0, 10) !== date) return null;
  day.setUTCDate(day.getUTCDate() - (day.getUTCDay() + 6) % 7);
  return day.toISOString().slice(0, 10);
}
export function inChatWeek(createdAt: string, monday: string): boolean {
  const start = Date.parse(monday + 'T00:00:00+07:00');
  const time = Date.parse(createdAt);
  return time >= start && time < start + 7 * 86400000;
}
