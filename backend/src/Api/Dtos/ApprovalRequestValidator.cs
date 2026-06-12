using FluentValidation;

namespace Backend.Api.Dtos;

public sealed class ApprovalRequestValidator : AbstractValidator<ApprovalRequest>
{
    private static readonly string[] _validDecisions = ["approve", "reject"];

    public ApprovalRequestValidator()
    {
        RuleFor(r => r.Decision)
            .NotEmpty()
            .Must(d => _validDecisions.Contains(d, StringComparer.OrdinalIgnoreCase))
            .WithMessage("Decision must be 'approve' or 'reject'.");
    }
}
