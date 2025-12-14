using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using KS.RustAnalyzer.Remote;
using KS.RustAnalyzer.TestAdapter.Common;
using Newtonsoft.Json.Linq;
using static KS.RustAnalyzer.TestAdapter.Common.DetailedBuildMessage;

namespace KS.RustAnalyzer.TestAdapter.Cargo;

/// <summary>
/// References
/// - https://doc.rust-lang.org/cargo/reference/external-tools.html#json-messages.
/// - https://doc.rust-lang.org/rustc/json.html.
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
    /// Parses a cargo JSON output line.
    /// </summary>
    /// <param name="workspaceRoot">The workspace root path (VS-visible).</param>
    /// <param name="jsonLine">The JSON line to parse.</param>
    /// <param name="tl">Telemetry and logging.</param>
    /// <param name="pathMapper">Optional path mapper for remote targets. Null for local.</param>
    /// <returns>Parsed build messages.</returns>
    public static BuildMessage[] Parse(PathEx workspaceRoot, string jsonLine, TL tl, IPathMapper pathMapper = null)
    {
        dynamic obj;
        try
        {
            obj = JObject.Parse(jsonLine);
        }
        catch (Exception e)
        {
            tl.L.WriteLine("CargoJsonOutputParser failed to parse line: {0}. Exception {1}.", jsonLine, e);
            tl.T.TrackException(e, new[] { ("Id", "JObjectParse"), ("Line", jsonLine) });
            return new[] { new StringBuildMessage { Message = jsonLine } };
        }

        try
        {
            if (obj.reason == "compiler-artifact")
            {
                return ParseCompilerArtifact(obj);
            }
            else if (obj.reason == "compiler-message")
            {
                return ParseCompilerMessage(workspaceRoot, obj, pathMapper);
            }
        }
        catch (Exception e)
        {
            tl.L.WriteLine("CargoJsonOutputParser failed to parse line: {0}. Exception {1}.", jsonLine, e);
            tl.T.TrackException(e, new[] { ("Id", "ParseCompilerX"), ("Line", jsonLine) });
            return new[] { new StringBuildMessage { Message = jsonLine } };
        }

        return Array.Empty<BuildMessage>();
    }

    private static BuildMessage[] ParseCompilerMessage(PathEx workspaceRoot, dynamic obj, IPathMapper pathMapper)
    {
        if (obj.message.spans == null || obj.message.spans.Count == 0)
        {
            return new BuildMessage[] { CreateBuildMessage(workspaceRoot, obj, pathMapper) };
        }

        return (obj.message.spans as IEnumerable<dynamic>).Select(
            s =>
            {
                DetailedBuildMessage msg = CreateBuildMessage(workspaceRoot, obj, pathMapper, s.file_name, s.line_start, s.column_start);
                return msg;
            }).ToArray();
    }

    private static int GetIntValue(dynamic obj, int defaultValue = default)
    {
        var value = 0;
        return obj != null && obj.Value != null && int.TryParse(obj.Value.ToString(), out value)
            ? value
            : defaultValue;
    }

    private static DetailedBuildMessage CreateBuildMessage(PathEx workspaceRoot, dynamic obj, IPathMapper pathMapper, dynamic fileInfo = null, dynamic lineInfo = null, dynamic colInfo = null)
    {
        var msg = new DetailedBuildMessage
        {
            Code = GetMessageCode(obj.message),
            ColumnNumber = GetIntValue(colInfo, 1),
            File = obj.target.src_path.Value,
            HelpKeyword = GetMessageCode(obj.message),
            LineNumber = GetIntValue(lineInfo, 1),
            ProjectFile = GetProjectFile(obj, pathMapper),
            SubCategory = null,
            TaskText = obj.message.message.Value,
            Type = GetMessageType(obj.message.level.Value),
        };

        // Handle file path - may be Linux path from cargo for remote targets
        string filePath = fileInfo != null && fileInfo.Value != null
            ? (string)fileInfo.Value
            : (string)msg.File;

        msg.File = MapFilePath(workspaceRoot, filePath, pathMapper);
        msg.LogMessage = GetLogMessage(obj.message, msg);

        return msg;
    }

    /// <summary>
    /// Maps a file path from cargo output to a VS-visible path.
    /// Handles both absolute and relative paths from remote targets.
    /// </summary>
    private static string MapFilePath(PathEx workspaceRoot, string filePath, IPathMapper pathMapper)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return filePath;
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

    private static dynamic GetProjectFile(dynamic obj, IPathMapper pathMapper)
    {
        string projectPath = null;

        if (obj.manifest_path != null && obj.manifest_path.Value != null)
        {
            projectPath = obj.manifest_path.Value;
        }
        else
        {
            projectPath = obj.package_id.Value;
        }

        // Map the project file path for remote targets
        if (pathMapper != null && pathMapper.Kind != TargetKind.Local && projectPath != null)
        {
            if (projectPath.StartsWith("/", StringComparison.Ordinal))
            {
                var remotePath = new RemotePath(projectPath, pathMapper.Kind);
                return (string)pathMapper.MapToLocal(remotePath);
            }
        }

        return projectPath;
    }

    private static dynamic GetMessageCode(dynamic obj)
    {
        return obj.code != null && obj.code.code != null
            ? obj.code.code.Value
            : "RS0000";
    }

    private static dynamic GetLogMessage(dynamic message, DetailedBuildMessage msg)
    {
        var logMsgText = $@"{msg.File}({msg.LineNumber},{msg.ColumnNumber}): {msg.Type} {msg.Code}: {msg.TaskText}";
        var rendered = message.rendered != null ? $"Details:\r\n{message.rendered.Value}" : string.Empty;

        return $"{logMsgText}\r\n{rendered}";
    }

    private static Level GetMessageType(string level)
    {
        return LevelToMessageTypeMap.TryGetValue(level, out Level type)
            ? type
            : Level.Error;
    }

    private static BuildMessage[] ParseCompilerArtifact(dynamic obj)
    {
        if ((bool)obj.fresh.Value)
        {
            return Array.Empty<BuildMessage>();
        }

        var matches = CompilerArtifactMessageCracker1.Matches(obj.package_id.Value as string);
        if (matches.Count != 0)
        {
            return new[] { new StringBuildMessage { Message = $"   Compiling {matches[0].Groups[1].Value} v{matches[0].Groups[2].Value} ({matches[0].Groups[4].Value})" } };
        }

        matches = CompilerArtifactMessageCracker2.Matches(obj.package_id.Value as string);
        if (matches.Count != 0)
        {
            return new[] { new StringBuildMessage { Message = $"   Compiling {matches[0].Groups[2].Value} v{matches[0].Groups[3].Value}" } };
        }

        throw new InvalidDataException($"Unable to match. Will be shown as is in the output window.");
    }
}
