# ChatApp – Đồ án môn Ứng dụng phân tán

Đọc `CLAUDE.md` ở thư mục gốc để biết bối cảnh, quy tắc làm việc và quy ước code (đó là quy tắc chung của dự án, áp dụng cho cả bạn).
Trước khi làm bất cứ việc gì, hãy đọc đủ 3 file: `docs/DESIGN.md`, `docs/PLAN.md`, `docs/PROGRESS.md`.
Lưu ý: các dòng bắt đầu bằng `@` trong CLAUDE.md là cú pháp của công cụ khác, bạn phải tự mở các file đó.

## Frontend: BẮT BUỘC tương thích Angular 12 và Node 14

- Chỉ dùng NgModule, `*ngIf` và `*ngFor` trong template, RxJS.
- CẤM: standalone component, signals (`signal()`, `computed()`, `effect()`), hàm `inject()` ngoài constructor, và cú pháp control-flow mới của Angular 17 (viết là "at-if", "at-for": ký tự `@` đứng trước chữ if, for trong template).
- Cài package npm nào cũng phải chọn bản chạy được với Node 14 và TypeScript của Angular 12, rồi chạy `ng build` để kiểm tra.
