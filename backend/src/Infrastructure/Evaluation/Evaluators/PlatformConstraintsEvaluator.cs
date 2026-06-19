using Backend.Core.Evaluation;
using Backend.Core.Generation.PlatformConstraints;

namespace Backend.Infrastructure.Evaluation.Evaluators;

/// <summary>
/// §1.3 PlatformConstraints (DL-030) — the <b>effective</b> output satisfies the surface's constraint set:
/// caption length (hook + body) ≤ max, hashtag count ≤ max, and the CD brief's aspect ratio is allowed.
/// Delegates to the same <see cref="PlatformConstraintValidator"/> + <see cref="SurfaceConstraints"/>
/// the generation pipeline enforces — never re-implements the limits (which are config-bound).
/// </summary>
public sealed class PlatformConstraintsEvaluator : SystemOutputEvaluator
{
    public const string MetricNameConst = "Platform Constraints";

    private readonly PlatformConstraintSet _constraints;

    public PlatformConstraintsEvaluator(PlatformConstraintSet constraints) => _constraints = constraints;

    protected override string MetricName => MetricNameConst;

    protected override Verdict Evaluate(SystemOutput output, EvalCase evalCase)
    {
        if (!_constraints.TryGet(output.TargetSurface, out var surface))
        {
            return Verdict.Fail($"no PlatformConstraints configured for surface '{output.TargetSurface}'");
        }

        if (output.Caption is { } caption)
        {
            // Effective caption length = hook + body (mirrors Copywriting's ProseLength).
            var lengthResult = PlatformConstraintValidator.ValidateCaptionLength(caption.Hook + caption.Body, surface);
            if (!lengthResult.IsValid)
            {
                return Verdict.Fail($"caption length: {lengthResult.Error}");
            }

            var hashtagResult = PlatformConstraintValidator.ValidateHashtags(caption.Hashtags, surface);
            if (!hashtagResult.IsValid)
            {
                return Verdict.Fail($"hashtags: {hashtagResult.Error}");
            }
        }

        if (output.Creative?.MediaPromptBrief is { } brief)
        {
            var aspectResult = PlatformConstraintValidator.ValidateAspectRatio(brief.AspectRatio, surface);
            if (!aspectResult.IsValid)
            {
                return Verdict.Fail($"aspect ratio: {aspectResult.Error}");
            }
        }

        return Verdict.Pass($"effective output satisfies the '{output.TargetSurface}' constraints");
    }
}
