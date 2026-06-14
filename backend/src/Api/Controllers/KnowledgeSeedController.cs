using Backend.Core.Multitenancy;
using Backend.Infrastructure.Knowledge.Seed;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.Controllers;

/// <summary>
/// Dev-only: seeds the repeatable coffee-roaster corpus for the brand in X-Brand-Id.
/// Invisible (404) outside the Development environment — never a production surface.
/// </summary>
[ApiController]
[Route("dev/knowledge")]
public sealed class KnowledgeSeedController : ControllerBase
{
    private readonly KnowledgeSeeder _seeder;
    private readonly IBrandContext _brandContext;
    private readonly IHostEnvironment _environment;

    public KnowledgeSeedController(KnowledgeSeeder seeder, IBrandContext brandContext, IHostEnvironment environment)
    {
        _seeder = seeder;
        _brandContext = brandContext;
        _environment = environment;
    }

    [HttpPost("seed")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Seed(CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        if (!_brandContext.HasBrand)
        {
            return BadRequest(new { error = "X-Brand-Id header is required." });
        }

        await _seeder.SeedAsync(_brandContext.RequireBrandId(), cancellationToken);
        return Accepted();
    }
}
