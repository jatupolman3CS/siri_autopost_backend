using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Api.Middlewares;

/// <summary>
/// An admin acting as a customer ("assist") sees the customer's dashboard read-only: every request
/// that could change something is refused, so nothing is done in the customer's name.
/// </summary>
public sealed class ReadOnlyImpersonationMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context, ICurrentUser current)
    {
        if (context.User.Identity?.IsAuthenticated == true && current.ImpersonatorId is not null &&
            !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            throw new ForbiddenException("โหมดช่วยเหลือดูได้อย่างเดียว แก้ไขข้อมูลแทนลูกค้าไม่ได้");
        return next(context);
    }
}
