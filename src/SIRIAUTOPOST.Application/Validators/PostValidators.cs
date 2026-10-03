using FluentValidation;
using SIRIAUTOPOST.Application.Features.Posts.Commands;
using SIRIAUTOPOST.Domain.Entities;

namespace SIRIAUTOPOST.Application.Validators;

// Input shape checks. Business rules (future schedule time, edit after publish...) live in the Domain.
public sealed class CreatePostCommandValidator : AbstractValidator<CreatePostCommand>
{
    public CreatePostCommandValidator()
    {
        RuleFor(x => x.Content).NotEmpty().WithMessage("กรุณาใส่ข้อความโพสต์")
            .MaximumLength(Post.MaxContentLength).WithMessage($"ข้อความโพสต์ยาวเกิน {Post.MaxContentLength} ตัวอักษร");
        RuleFor(x => x.GroupUrl).NotEmpty().WithMessage("กรุณาใส่ลิงก์กลุ่ม");
    }
}

public sealed class UpdatePostCommandValidator : AbstractValidator<UpdatePostCommand>
{
    public UpdatePostCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Content).NotEmpty().WithMessage("กรุณาใส่ข้อความโพสต์")
            .MaximumLength(Post.MaxContentLength).WithMessage($"ข้อความโพสต์ยาวเกิน {Post.MaxContentLength} ตัวอักษร");
    }
}
