using Microsoft.AspNetCore.Http;
using NWebDav.Server.Helpers;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Tasks;
using Serilog;

namespace NzbWebDAV.Middlewares;

public class ExceptionMiddleware(RequestDelegate next, ConfigManager configManager, MediaIntegrityService mediaIntegrityService)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // If the response has not started, we can write our custom response
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 499; // Non-standard status code for client closed request
                await context.Response.WriteAsync("Client closed request.");
            }
        }
        catch (UsenetArticleNotFoundException e)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 404;
            }

            var filePath = GetRequestFilePath(context);
            Log.Error($"File `{filePath}` has missing articles: {e.Message}");
            
            // Trigger integrity check if enabled
            await TriggerIntegrityCheckOnMissingArticles(context, filePath);
        }
        catch (SeekPositionNotFoundException)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 404;
            }

            var filePath = GetRequestFilePath(context);
            var seekPosition = context.Request.GetRange()?.Start?.ToString() ?? "unknown";
            Log.Error($"File `{filePath}` could not seek to byte position: {seekPosition}");
        }
    }

    private static string GetRequestFilePath(HttpContext context)
    {
        return context.Items["DavItem"] is DavItem davItem
            ? davItem.Path
            : context.Request.Path;
    }
    
    private async Task TriggerIntegrityCheckOnMissingArticles(HttpContext context, string filePath)
    {
        try
        {
            // Only trigger if integrity verification is enabled
            if (!configManager.IsIntegrityCheckingEnabled())
            {
                Log.Debug("Integrity verification is disabled, skipping automatic check for file with missing articles: {FilePath}", filePath);
                return;
            }
            
            // Get the DavItem if available (for internal files)
            if (context.Items["DavItem"] is DavItem davItem)
            {
                Log.Information("Triggering integrity check for internal file due to missing articles: {FilePath} (ID: {DavItemId})", filePath, davItem.Id);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await mediaIntegrityService.CheckSingleFileIntegrityAsync(davItem, CancellationToken.None);
                        Log.Information("Completed automatic integrity check for file with missing articles: {FilePath}", filePath);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to complete automatic integrity check for file with missing articles: {FilePath}", filePath);
                    }
                });
            }
            else
            {
                // For library files (external symlinks), check if it's a library path
                var libraryDir = configManager.GetLibraryDir();
                if (!string.IsNullOrEmpty(libraryDir) && filePath.StartsWith(libraryDir, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information("Triggering integrity check for library file due to missing articles: {FilePath}", filePath);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await mediaIntegrityService.CheckSingleLibraryFileIntegrityAsync(filePath, CancellationToken.None);
                            Log.Information("Completed automatic integrity check for library file with missing articles: {FilePath}", filePath);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Failed to complete automatic integrity check for library file with missing articles: {FilePath}", filePath);
                        }
                    });
                }
                else
                {
                    Log.Debug("File path does not match library directory or internal DAV item, skipping automatic integrity check: {FilePath}", filePath);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error triggering automatic integrity check for file with missing articles: {FilePath}", filePath);
        }
    }
}