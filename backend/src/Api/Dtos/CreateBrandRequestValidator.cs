using System.Text.RegularExpressions;
using FluentValidation;

namespace Backend.Api.Dtos;

/// <summary>
/// Edge validation for <see cref="CreateBrandRequest"/>. Runs once at the HTTP
/// boundary so the onboarding service and domain can trust every field. Each
/// multi-value identity facet requires at least one non-blank entry; the color
/// palette additionally requires well-formed hex values.
/// </summary>
public sealed partial class CreateBrandRequestValidator : AbstractValidator<CreateBrandRequest>
{
    private const int ShortText = 200;
    private const int LongText = 2000;

    public CreateBrandRequestValidator()
    {
        RuleFor(request => request.Name)
            .NotEmpty()
            .MaximumLength(ShortText);

        RuleFor(request => request.Positioning)
            .NotEmpty()
            .MaximumLength(LongText);

        RuleFor(request => request.ImageryStyle)
            .NotEmpty()
            .MaximumLength(LongText);

        RuleFor(request => request.ProductContext)
            .NotEmpty()
            .MaximumLength(LongText);

        // Each identity facet requires at least one non-blank entry.
        RuleFor(request => request.ToneDescriptors).NotEmpty();
        RuleForEach(request => request.ToneDescriptors).NotEmpty();

        RuleFor(request => request.AudienceSegments).NotEmpty();
        RuleForEach(request => request.AudienceSegments).NotEmpty();

        RuleFor(request => request.AudiencePainPoints).NotEmpty();
        RuleForEach(request => request.AudiencePainPoints).NotEmpty();

        // Do/don't language is optional in count, but any supplied entry must be
        // non-blank — empty strings carry no guidance.
        RuleForEach(request => request.VoiceDo).NotEmpty();
        RuleForEach(request => request.VoiceDont).NotEmpty();

        RuleFor(request => request.ColorHexes)
            .NotEmpty()
            .WithMessage("At least one brand color is required.");
        RuleForEach(request => request.ColorHexes)
            .Must(hex => HexColor().IsMatch(hex))
            .WithMessage("'{PropertyValue}' is not a valid hex color (expected #RGB or #RRGGBB).");
    }

    [GeneratedRegex(@"^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex HexColor();
}
