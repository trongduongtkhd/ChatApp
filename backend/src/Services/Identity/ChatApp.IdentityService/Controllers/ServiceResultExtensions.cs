using ChatApp.IdentityService.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatApp.IdentityService.Controllers;

public static class ServiceResultExtensions
{
    // Thành công → onSuccess(); lỗi → ProblemDetails với mã HTTP tương ứng.
    public static IActionResult ToActionResult(this ControllerBase controller, ServiceResult result, Func<IActionResult> onSuccess)
    {
        if (result.IsSuccess)
            return onSuccess();

        var status = result.Error switch
        {
            ServiceError.NotFound => StatusCodes.Status404NotFound,
            ServiceError.Forbidden => StatusCodes.Status403Forbidden,
            ServiceError.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };

        return controller.Problem(title: result.Message, statusCode: status);
    }
}
