namespace NzbWebDAV.Api.Controllers.MediaIntegrity;

public class MediaIntegrityResponse
{
    public string Message { get; set; } = null!;
    public bool Started { get; set; }
}
