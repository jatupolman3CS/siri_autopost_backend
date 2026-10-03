using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Api.Middlewares;

// Turns known exceptions into RFC 7807 problem responses; anything else is logged and becomes 500.
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (Exception ex) when (!ctx.Response.HasStarted)
        {
            var (status, problem) = ex switch
            {
                ValidationException v => (StatusCodes.Status400BadRequest, (ProblemDetails)new ValidationProblemDetails(
                    v.Errors.GroupBy(e => JsonName(e.PropertyName))
                        .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()))
                {
                    Title = "ข้อมูลไม่ถูกต้อง",
                }),
                NotFoundException => (StatusCodes.Status404NotFound, new ProblemDetails { Title = ex.Message }),
                DomainException => (StatusCodes.Status422UnprocessableEntity, new ProblemDetails { Title = ex.Message }),
                _ => (StatusCodes.Status500InternalServerError, new ProblemDetails { Title = "เกิดข้อผิดพลาดในระบบ" }),
            };
            if (status == StatusCodes.Status500InternalServerError)
                logger.LogError(ex, "Unhandled exception for {Method} {Path}", ctx.Request.Method, ctx.Request.Path);

            problem.Status = status;
            problem.Instance = ctx.Request.Path;
            ctx.Response.StatusCode = status;
            await ctx.Response.WriteAsJsonAsync(problem, problem.GetType(), options: null, contentType: "application/problem+json");
        }
    }

    // "GroupUrl" -> "groupUrl", to match the camelCase JSON the client sent.
    private static string JsonName(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
