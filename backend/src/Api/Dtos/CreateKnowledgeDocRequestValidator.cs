using Backend.Core.Domain;
using FluentValidation;

namespace Backend.Api.Dtos;

public sealed class CreateKnowledgeDocRequestValidator : AbstractValidator<CreateKnowledgeDocRequest>
{
    private const int TitleMax = 200;

    public CreateKnowledgeDocRequestValidator()
    {
        RuleFor(r => r.Title).NotEmpty().MaximumLength(TitleMax);
        RuleFor(r => r.Content).NotEmpty();
        RuleFor(r => r.DocType).IsInEnum();
        RuleFor(r => r.Facet).IsInEnum().When(r => r.Facet.HasValue);

        // Facet is meaningful only for brand_playbook (DL-026).
        RuleFor(r => r.Facet)
            .Null()
            .When(r => r.DocType != DocType.BrandPlaybook)
            .WithMessage("Facet applies only to brand_playbook docs.");
    }
}
