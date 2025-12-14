using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using KS.RustAnalyzer.Remote;
using KS.RustAnalyzer.TestAdapter.Common;
using Newtonsoft.Json;
using static KS.RustAnalyzer.TestAdapter.Common.DetailedBuildMessage;

namespace KS.RustAnalyzer.TestAdapter.Cargo;

/// <summary>
/// Parses cargo JSON output (--message-format=json) into build messages.
/// References:
/// - https://doc.rust-lang.org/cargo/reference/external-tools.html#json-messages
/// - https://doc.rust-lang.org/rustc/json.html
/// </summary>
public static class BuildJsonOutputParser
{
    private static readonly IReadOnlyDictionary<string, Level> LevelToMessageTypeMap =
        new Dictionary<string, Level>()
        {
            ["error"] = Level.Error,
            ["warning"] = Level.Warning,
            ["note"] = Level.None,
            ["help"] = Level.None,
            ["failure-note"] = Level.None,
            ["error: internal compiler error"] = Level.Error,
        };

    private static readonly Regex CompilerArtifactMessageCracker1 =
        new(@"^(.*) (.*) \((.*)\+(.*)\)$", RegexOptions.Compiled);

    private static readonly Regex CompilerArtifactMessageCracker2 =
        new(@"^(.*)\+(.*)@(.*)$", RegexOptions.Compiled);

    /// <summary>
    /// JSON serializer settings for cargo messages.
    /// </summary>
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        MissingMemberHandling = MissingMemberHandling.Ignore,
    };

    /// <summary>
    /// Parses a cargo JSON output line.
    /// </summary>
    /// <param name="workspaceRoot">The workspace root path (VS-visible).</param>
    /// <param name="jsonLine">The JSON line to parse.</param>
    /// <param name="tl">Telemetry and logging.</param>
    /// <param name="pathMapper">Optional path mapper for remote targets. Null for local.</param>
    /// <returns>Parsed build messages.</returns>
    public static BuildMessage[] Parse(PathEx workspaceRoot, string jsonLine, TL tl, IPathMapper pathMapper = null)
    {
        // First, determine the message type
        CargoMessage baseMessage;
        try
        {
            baseMessage = JsonConvert.DeserializeObject<CargoMessage>(jsonLine, JsonSettings);
        }
        catch (JsonException e)
        {
            tl.L.WriteLine("CargoJsonOutputParser failed to parse line: {0}. Exception {1}.", jsonLine, e);
            tl.T.TrackException(e, new[] { ("Id", "JsonParse"), ("Line", jsonLine) });
            return new[] { new StringBuildMessage { Message = jsonLine } };
        }

        if (baseMessage?.Reason == null)
        {
            return new[] { new StringBuildMessage { Message = jsonLine } };
        }

        try
        {
            switch (baseMessage.Reason)
            {
                case "compiler-artifact":
                    var artifact = JsonConvert.DeserializeObject<CargoCompilerArtifact>(jsonLine, JsonSettings);
                    return ParseCompilerArtifact(artifact);

                case "compiler-message":
                    var compilerMsg = JsonConvert.DeserializeObject<CargoCompilerMessage>(jsonLine, JsonSettings);
                    return ParseCompilerMessage(workspaceRoot, compilerMsg, pathMapper);

                case "build-finished":
                    // Ignore build-finished messages
                    return Array.Empty<BuildMessage>();

                case "build-script-executed":
                    // Ignore build-script-executed messages
                    return Array.Empty<BuildMessage>();

                default:
                    // Unknown message type - ignore
                    return Array.Empty<BuildMessage>();
            }
        }
        catch (Exception e)
        {
            tl.L.WriteLine("CargoJsonOutputParser failed to process message: {0}. Exception {1}.", jsonLine, e);
            tl.T.TrackException(e, new[] { ("Id", "ProcessMessage"), ("Line", jsonLine) });
            return new[] { new StringBuildMessage { Message = jsonLine } };
        }
    }

    /// <summary>
    /// Parses a compiler message into build messages.
    /// </summary>
    private static BuildMessage[] ParseCompilerMessage(PathEx workspaceRoot, CargoCompilerMessage msg, IPathMapper pathMapper)
    {
        if (msg?.Message == null)
        {
            return Array.Empty<BuildMessage>();
        }

        var diagnostic = msg.Message;
        var spans = diagnostic.Spans;

        // If no spans, create a single message using the target source path
        if (spans == null || spans.Count == 0)
        {
            var singleMsg = CreateBuildMessage(workspaceRoot, msg, pathMapper, null);
            return new BuildMessage[] { singleMsg };
        }

        // Create a message for each span
        var messages = new List<BuildMessage>();
        bool firstMessage = true;

        foreach (var span in spans)
        {
            var buildMsg = CreateBuildMessage(workspaceRoot, msg, pathMapper, span);

            // Only include the full LogMessage on the FIRST message to avoid duplicates in Output Window
            // All messages go to Error List for navigation, but only one shows in Output Window
            // NOTE: Use empty string instead of null - null strings crash VS's native OutputStringThreadSafe
            if (!firstMessage)
            {
                buildMsg.LogMessage = string.Empty;
            }
            firstMessage = false;

            messages.Add(buildMsg);
        }

        return messages.ToArray();
    }

    /// <summary>
    /// Creates a DetailedBuildMessage from a cargo compiler message.
    /// </summary>
    private static DetailedBuildMessage CreateBuildMessage(
        PathEx workspaceRoot,
        CargoCompilerMessage msg,
        IPathMapper pathMapper,
        DiagnosticSpan span)
    {
        var diagnostic = msg.Message;

        // Determine file path
        string filePath = span?.FileName ?? msg.Target?.SrcPath ?? string.Empty;

        // Map the file path for remote targets
        string mappedFilePath = MapFilePath(workspaceRoot, filePath, pathMapper);

        // Map the project file (manifest path)
        string mappedProjectFile = MapFilePath(workspaceRoot, msg.ManifestPath, pathMapper);

        var buildMsg = new DetailedBuildMessage
        {
            Code = diagnostic.Code?.Code ?? "RS0000",
            ColumnNumber = span?.ColumnStart ?? 1,
            File = mappedFilePath ?? string.Empty,
            HelpKeyword = diagnostic.Code?.Code ?? "RS0000",
            LineNumber = span?.LineStart ?? 1,
            ProjectFile = mappedProjectFile ?? string.Empty,
            SubCategory = string.Empty,  // Must not be null - crashes VS's native OutputStringThreadSafe
            TaskText = diagnostic.Message ?? string.Empty,
            Type = GetMessageType(diagnostic.Level),
        };

        // Create user-friendly log message
        buildMsg.LogMessage = CreateLogMessage(diagnostic, buildMsg);

        return buildMsg;
    }

    /// <summary>
    /// Regex to match ANSI escape sequences (colors, formatting, etc.).
    /// </summary>
    private static readonly Regex AnsiEscapeRegex = new(@"\x1b\[[0-9;]*m", RegexOptions.Compiled);

    /// <summary>
    /// Creates a user-friendly log message from the diagnostic.
    /// </summary>
    private static string CreateLogMessage(RustcDiagnostic diagnostic, DetailedBuildMessage msg)
    {
        var logMsgText = $"{msg.File}({msg.LineNumber},{msg.ColumnNumber}): {msg.Type} {msg.Code}: {msg.TaskText}";

        // Strip ANSI escape codes from rendered output - they can crash VS's native output pane
        var rendered = !string.IsNullOrEmpty(diagnostic.Rendered)
            ? $"Details:\r\n{StripAnsiCodes(diagnostic.Rendered)}"
            : string.Empty;

        return $"{logMsgText}\r\n{rendered}";
    }

    /// <summary>
    /// Strips ANSI escape codes from a string.
    /// Rustc outputs colored text with escape sequences like \x1b[31m that
    /// can cause AccessViolationException in VS's native OutputStringThreadSafe.
    /// </summary>
    private static string StripAnsiCodes(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        return AnsiEscapeRegex.Replace(input, string.Empty);
    }

    /// <summary>
    /// Maps a file path from cargo output to a VS-visible path.
    /// Handles both absolute and relative paths from remote targets.
    /// </summary>
    private static string MapFilePath(PathEx workspaceRoot, string filePath, IPathMapper pathMapper)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return string.Empty;  // Never return null - crashes VS's native OutputStringThreadSafe
        }

        // No mapper or local target - use traditional Path.Combine
        if (pathMapper == null || pathMapper.Kind == TargetKind.Local)
        {
            // Relative path - combine with workspace root
            if (!Path.IsPathRooted(filePath))
            {
                return Path.Combine(workspaceRoot, filePath);
            }

            return filePath;
        }

        // Remote target - convert Linux path to VS-visible path
        try
        {
            if (filePath.StartsWith("/", StringComparison.Ordinal))
            {
                // Absolute Linux path - map directly
                var remotePath = new RemotePath(filePath, pathMapper.Kind);
                return (string)pathMapper.MapToLocal(remotePath);
            }
            else
            {
                // Relative path - combine with remote workspace root, then map
                var remoteWorkspace = pathMapper.MapToRemote(workspaceRoot);
                var fullRemotePath = remoteWorkspace.Combine(filePath);
                return (string)pathMapper.MapToLocal(fullRemotePath);
            }
        }
        catch
        {
            // If mapping fails, fall back to combining workspace root with the file path
            // This ensures we still show a reasonable path in the output
            var normalizedFilePath = filePath.Replace("/", @"\");
            return Path.Combine(workspaceRoot, normalizedFilePath);
        }
    }

    /// <summary>
    /// Converts rustc level string to message type.
    /// </summary>
    private static Level GetMessageType(string level)
    {
        if (string.IsNullOrEmpty(level))
        {
            return Level.None;
        }

        return LevelToMessageTypeMap.TryGetValue(level, out Level type)
            ? type
            : Level.Error;
    }

    /// <summary>
    /// Parses a compiler artifact message.
    /// </summary>
    private static BuildMessage[] ParseCompilerArtifact(CargoCompilerArtifact artifact)
    {
        if (artifact == null || artifact.Fresh)
        {
            return Array.Empty<BuildMessage>();
        }

        var packageId = artifact.PackageId ?? string.Empty;

        var matches = CompilerArtifactMessageCracker1.Matches(packageId);
        if (matches.Count != 0)
        {
            return new[] { new StringBuildMessage { Message = $"   Compiling {matches[0].Groups[1].Value} v{matches[0].Groups[2].Value} ({matches[0].Groups[4].Value})" } };
        }

        matches = CompilerArtifactMessageCracker2.Matches(packageId);
        if (matches.Count != 0)
        {
            return new[] { new StringBuildMessage { Message = $"   Compiling {matches[0].Groups[2].Value} v{matches[0].Groups[3].Value}" } };
        }

        // If we can't parse the package_id, try to use the target name
        if (artifact.Target != null && !string.IsNullOrEmpty(artifact.Target.Name))
        {
            return new[] { new StringBuildMessage { Message = $"   Compiling {artifact.Target.Name}" } };
        }

        // Fallback - just show something
        return new[] { new StringBuildMessage { Message = $"   Compiling (unknown package)" } };
    }
}
