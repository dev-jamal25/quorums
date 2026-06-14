using Backend.Core.Domain;
using FluentValidation;

namespace Backend.Api.Dtos;

public sealed class UpdateKnowledgeDocRequestValidator : AbstractValidator<UpdateKnowledgeDocRequest>
{
    private const int TitleMax = 200;

    public UpdateKnowledgeDocRequestValidator()
    {
        RuleFor(r => r.Title).NotEmpty().MaximumLength(TitleMax);
        RuleFor(r => r.Content).NotEmpty();
        RuleFor(r => r.DocType).IsInEnum();
        RuleFor(r => r.Facet).IsInEnum().When(r => r.Facet.HasValue);

        RuleFor(r => r.Facet)
            .Null()
            .When(r => r.DocType != DocType.BrandPlaybook)
            .WithMessage("Facet applies only to brand_playbook docs.");
    }
}
