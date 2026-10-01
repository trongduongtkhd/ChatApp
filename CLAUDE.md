# ChatApp – Đồ án môn Ứng dụng phân tán

## Bối cảnh
- Mình là sinh viên, đang học hệ phân tán. Mục tiêu: **vừa code xong đồ án, vừa HIỂU để viết báo cáo và trả lời giảng viên**.
- Đề tài: ứng dụng chat nhóm theo kiến trúc microservices + event-driven.
- Đồ án chấm theo 8 chương: Mở đầu, Kiến trúc, Tiến trình & luồng, Trao đổi thông tin, Định danh, Đồng bộ hóa, Sao lưu, Tính chịu lỗi.
- Stack: ASP.NET Core Web API (.NET 10), PostgreSQL + EF Core (Npgsql), Redis, Kafka (KRaft), SignalR, gRPC, YARP, Docker Compose; frontend Angular.

## Môi trường máy mình
- .NET SDK 10.
- Node 14 + Angular 12 (bản cũ). **Code Angular phải tương thích Angular 12**: dùng NgModule, `*ngIf`/`*ngFor`, RxJS; KHÔNG dùng standalone component, signals, cú pháp `@if`/`@for`, `inject()` ngoài constructor. Package npm (vd `@microsoft/signalr`) phải chọn phiên bản chạy được với Node 14 / TypeScript của Angular 12 và kiểm tra build.
- Docker Desktop đã có. **Không cài PostgreSQL, Redis, Kafka lên máy**: tất cả chạy bằng Docker Compose.
- Hệ điều hành Windows: lệnh hướng dẫn phải chạy được trên PowerShell.

## Tài liệu thiết kế (đọc trước khi làm bất cứ việc gì)
@docs/DESIGN.md
@docs/PLAN.md
@docs/PROGRESS.md

## Cách làm việc với mình (BẮT BUỘC)
1. **Luôn trả lời bằng tiếng Việt.** Tên code, tên biến giữ tiếng Anh.
2. **Làm đúng một phần trong PLAN.md mỗi lần.** Không tự ý nhảy sang phần sau, không làm thêm tính năng ngoài DESIGN.md.
3. **Trước khi code mỗi phần:** tóm tắt sẽ làm gì, tạo/sửa những file nào, và giải thích ngắn các khái niệm của phần đó. Chờ mình đồng ý rồi mới code.
4. **Code theo từng bước nhỏ.** Sau mỗi bước phải build/chạy được.
5. **Sau khi code xong mỗi phần, luôn viết đủ 3 mục:**
   - **Giải thích khái niệm:** gắn với đúng đoạn code vừa viết (file nào, dòng nào), và khái niệm đó thuộc chương nào của đồ án.
   - **Cách test:** từng lệnh/thao tác cụ thể và kết quả mong đợi.
   - **Kịch bản demo lỗi** (nếu phần đó có): làm sao tạo sự cố và hệ thống phản ứng thế nào.
6. **Cập nhật docs/PROGRESS.md** khi xong một phần: đánh dấu hoàn thành, ghi chú cho báo cáo.
7. Nếu thấy thiết kế cần sửa, **hỏi mình trước**, đồng ý rồi thì cập nhật DESIGN.md.
8. Giải thích dễ hiểu, có ví dụ, giả định mình chưa biết khái niệm đó.

## Quy ước code
- Tuân theo cấu trúc thư mục ở mục "Cấu trúc thư mục" trong docs/DESIGN.md. Không tự tạo thêm project hoặc thư mục cấp cao.
- Một service KHÔNG được tham chiếu (ProjectReference) tới service khác. Chỉ được tham chiếu tới project trong `backend/src/BuildingBlocks/`.
- C# dùng PascalCase; PostgreSQL dùng snake_case qua `UseSnakeCaseNamingConvention()`.
- Không có foreign key giữa các database của các service khác nhau.
- Dùng `async/await` cho mọi thao tác I/O.
- Cấu hình (connection string, Kafka, Redis) đặt trong `appsettings.json` và biến môi trường Docker, không hard-code.

## Lệnh thường dùng
- Chạy hạ tầng: `docker compose up -d`
- Xem container: `docker compose ps`
- Xem log một service: `docker compose logs -f <tên-service>`
- Chạy một service local: `dotnet run --project backend/src/Services/<Tên>/ChatApp.<Tên>Service`
- Chạy Angular: `cd frontend/chat-app; npm start`
