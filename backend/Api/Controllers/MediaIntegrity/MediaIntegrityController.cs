using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Tasks;

namespace NzbWebDAV.Api.Controllers.MediaIntegrity;

[ApiController]
[Route("api/media-integrity")]
public class MediaIntegrityController(MediaIntegrityService integrityService) : BaseApiController
{
    private async Task<MediaIntegrityResponse> TriggerIntegrityCheck()
    {
        var started = await integrityService.TriggerManualIntegrityCheckAsync();
        
        var message = started 
            ? "Media integrity check started" 
            : "Media integrity check is already running";
            
        return new MediaIntegrityResponse { Message = message, Started = started };
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var response = await TriggerIntegrityCheck();
        return Ok(response);
    }
}
