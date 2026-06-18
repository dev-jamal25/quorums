namespace Backend.Core.Orchestration;

/// <summary>
/// The regenerate re-entry mode (DL-036). <see cref="SameAngle"/> keeps the selected strategic angle
/// and re-runs Creative Director → Media for a fresh creative; <see cref="ReselectAngle"/> has the
/// Supervisor pick a DIFFERENT banked candidate (no new Strategist call) before the same CD→Media
/// re-run. The endpoint maps the kebab wire value (<c>same-angle</c>/<c>reselect-angle</c>) onto this.
/// </summary>
public enum RegenerateMode
{
    SameAngle,
    ReselectAngle,
}
