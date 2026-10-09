namespace ChatApp.IdentityService.Services;

// Cùng mẫu với group-service (không tham chiếu chéo service được nên viết lại ở đây).
public enum ServiceError
{
    None,
    NotFound,   // 404
    Forbidden,  // 403: đã đăng nhập nhưng không được làm thao tác này với lời mời này
    Conflict,   // 409: trạng thái không cho phép, hoặc bị request khác ghi trước (Optimistic Locking)
    BadRequest  // 400: vi phạm quy tắc nghiệp vụ
}

// Kết quả của một thao tác nghiệp vụ: thành công, hoặc lỗi gì kèm thông báo.
// Controller đổi ServiceError sang mã HTTP (ServiceResultExtensions).
public record ServiceResult(ServiceError Error = ServiceError.None, string? Message = null)
{
    public bool IsSuccess => Error == ServiceError.None;

    public static ServiceResult Ok() => new();
    public static ServiceResult Fail(ServiceError error, string message) => new(error, message);
}

public record ServiceResult<T>(T? Value, ServiceError Error = ServiceError.None, string? Message = null)
    : ServiceResult(Error, Message)
{
    public static ServiceResult<T> Ok(T value) => new(value);
    public static new ServiceResult<T> Fail(ServiceError error, string message) => new(default, error, message);
}
