using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

    public static BuildMessage[] Parse(PathEx workspaceRoot, string jsonLine, TL tl)
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
                return ParseCompilerMessage(workspaceRoot, obj);
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

    private static BuildMessage[] ParseCompilerMessage(PathEx workspaceRoot, dynamic obj)
    {
        if (obj.message.spans == null || obj.message.spans.Count == 0)
        {
            return new BuildMessage[] { CreateBuildMessage(workspaceRoot, obj) };
        }

        return (obj.message.spans as IEnumerable<dynamic>).Select(
            s =>
            {
                DetailedBuildMessage msg = CreateBuildMessage(workspaceRoot, obj, s.file_name, s.line_start, s.column_start);
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

    private static DetailedBuildMessage CreateBuildMessage(PathEx workspaceRoot, dynamic obj, dynamic fileInfo = null, dynamic lineInfo = null, dynamic colInfo = null)
    {
        // Check if workspace is WSL to handle path mapping
        WslInfo.TryParse(workspaceRoot, out var wslInfo);

        var srcPath = (string)obj.target.src_path.Value;
        var resolvedSrcPath = ResolvePathForVs(srcPath, workspaceRoot, wslInfo);

        var msg = new DetailedBuildMessage
        {
            Code = GetMessageCode(obj.message),
            ColumnNumber = GetIntValue(colInfo, 1),
            File = resolvedSrcPath,
            HelpKeyword = GetMessageCode(obj.message),
            LineNumber = GetIntValue(lineInfo, 1),
            ProjectFile = ResolvePathForVs(GetProjectFile(obj), workspaceRoot, wslInfo),
            SubCategory = null,
            TaskText = obj.message.message.Value,
            Type = GetMessageType(obj.message.level.Value),
        };

        if (fileInfo != null && fileInfo.Value != null)
        {
            var fileInfoPath = (string)fileInfo.Value;
            msg.File = ResolveFileInfoPath(fileInfoPath, workspaceRoot, wslInfo);
        }

        msg.LogMessage = GetLogMessage(obj.message, msg);

        return msg;
    }

    /// <summary>
    /// Resolves a path from cargo output for VS navigation.
    /// For WSL workspaces, converts Linux absolute paths to Windows UNC paths.
    /// </summary>
    private static string ResolvePathForVs(string path, PathEx workspaceRoot, WslInfo wslInfo)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        if (wslInfo != null && WslInfo.IsLinuxAbsolutePath(path))
        {
            return wslInfo.ToUncPath(path);
        }

        return path;
    }

    /// <summary>
    /// Resolves the file_name field from spans, which may be relative or absolute.
    /// </summary>
    private static string ResolveFileInfoPath(string fileInfoPath, PathEx workspaceRoot, WslInfo wslInfo)
    {
        if (string.IsNullOrEmpty(fileInfoPath))
        {
            return fileInfoPath;
        }

        if (wslInfo != null)
        {
            // For WSL, cargo outputs Linux paths
            if (WslInfo.IsLinuxAbsolutePath(fileInfoPath))
            {
                // Absolute Linux path - convert to UNC
                return wslInfo.ToUncPath(fileInfoPath);
            }
            else
            {
                // Relative path - combine with workspace root (which is already UNC)
                // The workspace root is UNC, and the relative path uses forward slashes from cargo
                var normalizedRelPath = fileInfoPath.Replace('/', '\\');
                return Path.Combine(workspaceRoot, normalizedRelPath);
            }
        }
        else
        {
            // Windows workspace - use standard path combination
            return Path.Combine(workspaceRoot, fileInfoPath);
        }
    }

    private static string GetProjectFile(dynamic obj)
    {
        if (obj.manifest_path != null && obj.manifest_path.Value != null)
        {
            return (string)obj.manifest_path.Value;
        }

        return (string)obj.package_id.Value;
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
