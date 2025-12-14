using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KS.RustAnalyzer.Remote;
using KS.RustAnalyzer.TestAdapter.Common;
using Microsoft.VisualStudio.LanguageServer.Client;
using Newtonsoft.Json.Linq;

namespace KS.RustAnalyzer.LanguageService;

/// <summary>
/// LSP middleware layer for URI rewriting between VS and remote rust-analyzer.
/// Intercepts LSP messages to translate file:// URIs between VS-visible paths
/// (e.g., \\wsl$\Ubuntu\...) and remote paths (e.g., /home/user/...).
/// </summary>
public sealed class RustAnalyzerMiddleLayer : ILanguageClientMiddleLayer
{
    private readonly IPathMapper _pathMapper;
    private readonly ILogger _logger;

    // Known URI fields in outgoing requests (VS → rust-analyzer)
    private static readonly HashSet<string> OutgoingUriProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "uri",
        "textDocument.uri",
        "rootUri",
    };

    // Known URI array fields in outgoing requests
    private static readonly HashSet<string> OutgoingUriArrayProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "workspaceFolders",
    };

    // Known URI fields in incoming responses/notifications (rust-analyzer → VS)
    private static readonly HashSet<string> IncomingUriProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "uri",
        "location.uri",
        "targetUri",
        "originSelectionRange.uri",
    };

    public RustAnalyzerMiddleLayer(IPathMapper pathMapper, ILogger logger)
    {
        _pathMapper = pathMapper ?? throw new ArgumentNullException(nameof(pathMapper));
        _logger = logger;
    }

    /// <summary>
    /// Returns true if the middleware can handle the specified method.
    /// </summary>
    public bool CanHandle(string methodName)
    {
        // We handle all methods that might contain URIs
        return true;
    }

    /// <summary>
    /// Handles an outgoing request from VS to rust-analyzer.
    /// Rewrites VS file URIs to remote URIs.
    /// </summary>
    public Task HandleNotificationAsync(string methodName, JToken methodParam, Func<JToken, Task> sendNotification)
    {
        if (_pathMapper.Kind == TargetKind.Local)
        {
            return sendNotification(methodParam);
        }

        try
        {
            var rewritten = RewriteUrisToRemote(methodParam, methodName);
            return sendNotification(rewritten);
        }
        catch (Exception ex)
        {
            _logger?.WriteLine($"[MiddleLayer] Error rewriting notification {methodName}: {ex.Message}");
            return sendNotification(methodParam);
        }
    }

    /// <summary>
    /// Handles an outgoing request from VS to rust-analyzer.
    /// Rewrites VS file URIs to remote URIs.
    /// </summary>
    public async Task<JToken> HandleRequestAsync(string methodName, JToken methodParam, Func<JToken, Task<JToken>> sendRequest)
    {
        if (_pathMapper.Kind == TargetKind.Local)
        {
            return await sendRequest(methodParam).ConfigureAwait(false);
        }

        try
        {
            var rewrittenRequest = RewriteUrisToRemote(methodParam, methodName);
            var response = await sendRequest(rewrittenRequest).ConfigureAwait(false);
            var rewrittenResponse = RewriteUrisToLocal(response, methodName);
            return rewrittenResponse;
        }
        catch (Exception ex)
        {
            _logger?.WriteLine($"[MiddleLayer] Error handling request {methodName}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Rewrites URIs from VS-visible paths to remote paths (for outgoing requests).
    /// </summary>
    private JToken RewriteUrisToRemote(JToken token, string methodName)
    {
        if (token == null)
        {
            return token;
        }

        if (token is JObject obj)
        {
            // Handle initialize request specially
            if (methodName == "initialize")
            {
                return RewriteInitializeRequest(obj);
            }

            RewriteObjectUris(obj, toRemote: true);
            return obj;
        }

        if (token is JArray arr)
        {
            foreach (var item in arr)
            {
                RewriteUrisToRemote(item, methodName);
            }
        }

        return token;
    }

    /// <summary>
    /// Rewrites URIs from remote paths to VS-visible paths (for incoming responses).
    /// </summary>
    private JToken RewriteUrisToLocal(JToken token, string methodName)
    {
        if (token == null)
        {
            return token;
        }

        if (token is JObject obj)
        {
            RewriteObjectUris(obj, toRemote: false);
            return obj;
        }

        if (token is JArray arr)
        {
            foreach (var item in arr)
            {
                RewriteUrisToLocal(item, methodName);
            }
        }

        return token;
    }

    /// <summary>
    /// Specially handles the initialize request which has complex URI fields.
    /// </summary>
    private JObject RewriteInitializeRequest(JObject obj)
    {
        // Rewrite rootUri
        if (obj.TryGetValue("rootUri", out var rootUri) && rootUri.Type == JTokenType.String)
        {
            obj["rootUri"] = MapUriToRemote(rootUri.ToString());
        }

        // Rewrite rootPath (deprecated but still used)
        if (obj.TryGetValue("rootPath", out var rootPath) && rootPath.Type == JTokenType.String)
        {
            obj["rootPath"] = MapPathToRemote(rootPath.ToString());
        }

        // Rewrite workspaceFolders
        if (obj.TryGetValue("workspaceFolders", out var folders) && folders is JArray foldersArr)
        {
            foreach (var folder in foldersArr)
            {
                if (folder is JObject folderObj && folderObj.TryGetValue("uri", out var uri) && uri.Type == JTokenType.String)
                {
                    folderObj["uri"] = MapUriToRemote(uri.ToString());
                }
            }
        }

        return obj;
    }

    /// <summary>
    /// Recursively rewrites URI properties in a JObject.
    /// </summary>
    private void RewriteObjectUris(JObject obj, bool toRemote)
    {
        foreach (var property in obj.Properties())
        {
            if (property.Value.Type == JTokenType.String)
            {
                var propName = property.Name;

                // Check if this is a known URI property
                if (IsUriProperty(propName))
                {
                    var uriValue = property.Value.ToString();
                    property.Value = toRemote ? MapUriToRemote(uriValue) : MapUriToLocal(uriValue);
                }
            }
            else if (property.Value is JObject childObj)
            {
                RewriteObjectUris(childObj, toRemote);
            }
            else if (property.Value is JArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is JObject itemObj)
                    {
                        RewriteObjectUris(itemObj, toRemote);
                    }
                }
            }
        }

        // Handle "changes" in workspace/applyEdit where keys are URIs
        if (obj.TryGetValue("changes", out var changes) && changes is JObject changesObj)
        {
            var newChanges = new JObject();
            foreach (var prop in changesObj.Properties())
            {
                var newKey = toRemote ? MapUriToRemote(prop.Name) : MapUriToLocal(prop.Name);
                newChanges[newKey] = prop.Value;
            }

            obj["changes"] = newChanges;
        }
    }

    /// <summary>
    /// Checks if a property name is a known URI property.
    /// </summary>
    private static bool IsUriProperty(string propertyName)
    {
        return propertyName.Equals("uri", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("targetUri", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Maps a file:// URI from VS-visible to remote format.
    /// </summary>
    private string MapUriToRemote(string uriString)
    {
        if (string.IsNullOrEmpty(uriString))
        {
            return uriString;
        }

        try
        {
            if (!uriString.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                return uriString;
            }

            var uri = new Uri(uriString);
            var mapped = _pathMapper.MapUriToRemote(uri);
            return mapped.ToString();
        }
        catch (Exception ex)
        {
            _logger?.WriteLine($"[MiddleLayer] Failed to map URI to remote: {uriString} - {ex.Message}");
            return uriString;
        }
    }

    /// <summary>
    /// Maps a file:// URI from remote to VS-visible format.
    /// </summary>
    private string MapUriToLocal(string uriString)
    {
        if (string.IsNullOrEmpty(uriString))
        {
            return uriString;
        }

        try
        {
            if (!uriString.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                return uriString;
            }

            var uri = new Uri(uriString);
            var mapped = _pathMapper.MapUriToLocal(uri);
            return mapped.ToString();
        }
        catch (Exception ex)
        {
            _logger?.WriteLine($"[MiddleLayer] Failed to map URI to local: {uriString} - {ex.Message}");
            return uriString;
        }
    }

    /// <summary>
    /// Maps a path from VS-visible to remote format (for rootPath).
    /// </summary>
    private string MapPathToRemote(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        try
        {
            var remotePath = _pathMapper.MapToRemote((PathEx)path);
            return (string)remotePath;
        }
        catch (Exception ex)
        {
            _logger?.WriteLine($"[MiddleLayer] Failed to map path to remote: {path} - {ex.Message}");
            return path;
        }
    }
}
