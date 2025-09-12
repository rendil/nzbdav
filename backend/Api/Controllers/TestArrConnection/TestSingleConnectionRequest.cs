namespace NzbWebDAV.Api.Controllers.TestArrConnection;

public class TestSingleConnectionRequest
{
    public string AppType { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string? Name { get; set; }
}
