namespace ChatApp.GroupService.Services;

public enum ServiceError
{
    None,
    NotFound,   // 404
    Forbidden,  // 403: đã đăng nhập nhưng không có quyền với nhóm này
    Conflict,   // 409: trùng dữ liệu hoặc version cũ (Optimistic Locking)
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
