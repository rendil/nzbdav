namespace NzbWebDAV.Api.Controllers.TestArrConnection;

public class TestArrConnectionResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> ConfiguredInstances { get; set; } = new();
}
