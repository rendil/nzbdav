using System.Text;
using System.Text.Json;
using Serilog;

namespace NzbWebDAV.Clients;

public abstract class ArrClient : IDisposable
{
    protected readonly HttpClient _httpClient;
    protected readonly string _baseUrl;
    protected readonly string _apiKey;
    protected readonly string _instanceName;
    
    public string InstanceName => _instanceName;

    protected ArrClient(string baseUrl, string apiKey, string instanceName)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _instanceName = instanceName;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("X-Api-Key", _apiKey);
    }

    public abstract Task<bool> DeleteFileAsync(string filePath, CancellationToken ct = default);
    public abstract Task<bool> TestConnectionAsync(CancellationToken ct = default);
    
    protected async Task<T?> GetAsync<T>(string endpoint, CancellationToken ct = default)
    {
        try
        {
            var url = $"{_baseUrl}{endpoint}";
            var response = await _httpClient.GetAsync(url, ct);
            
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to GET {Url}: {StatusCode} {ReasonPhrase}", 
                    url, response.StatusCode, response.ReasonPhrase);
                return default;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<T>(content, WebJsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error making GET request to {InstanceName}: {Endpoint}", _instanceName, endpoint);
            return default;
        }
    }

    protected async Task<T?> PostAsync<T>(string endpoint, object? body = null, CancellationToken ct = default)
    {
        try
        {
            var url = $"{_baseUrl}{endpoint}";
            var json = body != null ? JsonSerializer.Serialize(body, WebJsonOptions) : "{}";
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync(url, content, ct);
            
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to POST {Url}: {StatusCode} {ReasonPhrase}", 
                    url, response.StatusCode, response.ReasonPhrase);
                return default;
            }

            var responseContent = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<T>(responseContent, WebJsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error making POST request to {InstanceName}: {Endpoint}", _instanceName, endpoint);
            return default;
        }
    }

    protected async Task<bool> DeleteAsync(string endpoint, CancellationToken ct = default)
    {
        try
        {
            var url = $"{_baseUrl}{endpoint}";
            var response = await _httpClient.DeleteAsync(url, ct);
            
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to DELETE {Url}: {StatusCode} {ReasonPhrase}", 
                    url, response.StatusCode, response.ReasonPhrase);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error making DELETE request to {InstanceName}: {Endpoint}", _instanceName, endpoint);
            return false;
        }
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    // Common JSON serializer options for consistent API communication
    protected static readonly JsonSerializerOptions WebJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };
}
