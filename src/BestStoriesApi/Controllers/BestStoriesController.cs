using BestStoriesApi.Stories;
using Microsoft.AspNetCore.Mvc;

namespace BestStoriesApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BestStoriesController(StoryStore store) : ControllerBase
{
    /// <summary>The n highest-scoring of Hacker News's best stories, highest first.</summary>
    /// <param name="n">How many stories to return. At least 1. More than exist returns them all.</param>
    [HttpGet]
    [ProducesResponseType<IEnumerable<Story>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Get([FromQuery] int? n, CancellationToken cancellationToken)
    {
        if (n is null or < 1)
            return BadRequest("Query parameter 'n' is required and must be a whole number of at least 1.");

        var snapshot = await store.GetAsync(cancellationToken);
        if (snapshot is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Stories are not available yet. Try again shortly.");

        return Ok(snapshot.Take(n.Value));
    }
}
