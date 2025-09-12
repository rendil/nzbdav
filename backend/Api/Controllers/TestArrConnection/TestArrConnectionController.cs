using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Clients;

namespace NzbWebDAV.Api.Controllers.TestArrConnection;

[ApiController]
[Route("api/test-arr-connection")]
public class TestArrConnectionController(ArrManager arrManager) : BaseApiController
{
    private async Task<TestArrConnectionResponse> TestConnections()
    {
        var success = await arrManager.TestAllConnectionsAsync();
        var instances = arrManager.GetConfiguredInstances();
        
        return new TestArrConnectionResponse
        {
            Success = success,
            ConfiguredInstances = instances,
            Message = success 
                ? $"Successfully connected to all {instances.Count} configured instances" 
                : "Failed to connect to one or more instances"
        };
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var response = await TestConnections();
        return Ok(response);
    }
}
