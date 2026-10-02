-- Chạy MỘT LẦN khi container postgres khởi động với volume còn trống.
-- Mỗi service có database riêng (Database per service).
-- Sửa file này xong phải chạy: docker compose down -v  (xóa volume) thì mới chạy lại.

CREATE DATABASE identity_db;
CREATE DATABASE group_db;
CREATE DATABASE chat_db;
CREATE DATABASE notification_db;
