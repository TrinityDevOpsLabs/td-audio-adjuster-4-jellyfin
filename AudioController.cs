using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AudioAdjuster4Jellyfin;

[ApiController]
[Route("AudioAdjuster")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class AudioController(ILibraryManager library, AudioQueue queue) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("movie-button.js")]
    public IActionResult Script() => File(
        typeof(Plugin).Assembly.GetManifestResourceStream("AudioAdjuster4Jellyfin.Configuration.movie-button.js")!,
        "application/javascript");

    [HttpGet("jobs")]
    public IActionResult Jobs() => Ok(queue.GetJobs());

    [HttpPost("movies/{id:guid}")]
    public IActionResult Submit(Guid id)
    {
        if (library.GetItemById(id) is not Movie movie)
            return NotFound(new { Message = "Movie not found." });

        var path = movie.Path;
        if (movie.IsPlaceHolder || !movie.IsFileProtocol || !AudioProcessor.Supports(path))
            return BadRequest(new { Message = "Select a movie stored locally as an MKV or MP4 file." });

        var settings = Plugin.Instance.Configuration;
        return queue.TrySubmit(id, movie.Name, path!, settings)
            ? Accepted()
            : Conflict(new { Message = "This movie is already queued or the queue is full." });
    }
}
