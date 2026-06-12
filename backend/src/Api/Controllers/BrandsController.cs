using Backend.Api.Dtos;
using Backend.Core.Onboarding;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.Controllers;

/// <summary>
/// Brand resource endpoints. Thin: validate at the edge (FluentValidation +
/// <c>[ApiController]</c> → automatic 400 ProblemDetails), delegate to the
/// onboarding service, map the result. No data access or business logic here.
/// </summary>
[ApiController]
[Route("brands")]
public sealed class BrandsController : ControllerBase
{
    private readonly IBrandOnboardingService _onboarding;

    public BrandsController(IBrandOnboardingService onboarding) => _onboarding = onboarding;

    /// <summary>
    /// Onboards a brand from its structured identity. The brand id is generated
    /// server-side; onboarding self-scopes to it and writes through RLS. Returns
    /// 201 with the new brand id.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(CreateBrandResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CreateBrandResponse>> Create(
        CreateBrandRequest request,
        CancellationToken cancellationToken)
    {
        var command = new BrandOnboardingCommand(
            request.Name,
            request.Positioning,
            request.ToneDescriptors,
            request.VoiceDo,
            request.VoiceDont,
            request.ColorHexes,
            request.ImageryStyle,
            request.AudienceSegments,
            request.AudiencePainPoints,
            request.ProductContext);

        var brandId = await _onboarding.OnboardAsync(command, cancellationToken);

        var response = new CreateBrandResponse(brandId);
        return Created($"/brands/{brandId}", response);
    }
}
