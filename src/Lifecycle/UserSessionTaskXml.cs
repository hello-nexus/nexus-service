using System;
using System.IO;
using System.Text;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Builds the Task Scheduler XML for the one-shot tasks the service uses to
/// run a command in the active console user's session.
///
/// The task carries NO trigger. Task Scheduler runs it only from an explicit
/// `schtasks /Run`, which is exactly the semantics these call sites want, and
/// a leftover task (when the /Delete fails) can never fire on its own.
///
/// Kept out of the Windows-only callers so the XML shaping runs in the normal
/// test suite on every platform.
/// </summary>
internal static class UserSessionTaskXml
{
    /// <summary>
    /// Splits a command line into the executable and its arguments. A path
    /// wrapped in double quotes is taken verbatim (it may contain spaces);
    /// otherwise the first space separates the two.
    /// </summary>
    internal static (string Exe, string Args) SplitCommand(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            var close = trimmed.IndexOf('"', 1);
            if (close > 0)
            {
                return (trimmed[1..close], trimmed[(close + 1)..].TrimStart());
            }
        }

        var space = trimmed.IndexOf(' ');
        return space < 0 ? (trimmed, string.Empty) : (trimmed[..space], trimmed[(space + 1)..].TrimStart());
    }

    /// <summary>
    /// Task XML running <paramref name="command"/> as <paramref name="username"/>
    /// with an interactive token, the XML equivalent of `/RU user /IT`.
    /// <paramref name="elevated"/> asks for the user's full token (`/RL HIGHEST`),
    /// which Task Scheduler grants an administrator without a UAC prompt; a
    /// standard user gets their limited token either way.
    /// </summary>
    internal static string Build(string username, string command, bool elevated = false)
    {
        var (exe, args) = SplitCommand(command);
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-16"?>""").Append('\n');
        sb.Append("""<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">""").Append('\n');
        sb.Append("  <Triggers />\n");
        sb.Append("  <Principals>\n");
        sb.Append("    <Principal id=\"Author\">\n");
        sb.Append("      <UserId>").Append(Escape(username)).Append("</UserId>\n");
        sb.Append("      <LogonType>InteractiveToken</LogonType>\n");
        sb.Append("      <RunLevel>").Append(elevated ? "HighestAvailable" : "LeastPrivilege").Append("</RunLevel>\n");
        sb.Append("    </Principal>\n");
        sb.Append("  </Principals>\n");
        sb.Append("  <Settings>\n");
        // StartWhenAvailable false: with no trigger there is no missed run to
        // catch up on, and leaving it true lets Task Scheduler treat the task
        // as eligible to launch on its own schedule evaluation.
        sb.Append("    <StartWhenAvailable>false</StartWhenAvailable>\n");
        sb.Append("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n");
        sb.Append("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n");
        sb.Append("    <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>\n");
        sb.Append("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\n");
        sb.Append("    <Enabled>true</Enabled>\n");
        sb.Append("  </Settings>\n");
        sb.Append("  <Actions Context=\"Author\">\n");
        sb.Append("    <Exec>\n");
        sb.Append("      <Command>").Append(Escape(exe)).Append("</Command>\n");
        if (args.Length > 0)
        {
            sb.Append("      <Arguments>").Append(Escape(args)).Append("</Arguments>\n");
        }
        sb.Append("    </Exec>\n");
        sb.Append("  </Actions>\n");
        sb.Append("</Task>\n");
        return sb.ToString();
    }

    /// <summary>
    /// Writes task XML to a scratch file and returns its path. schtasks /XML
    /// rejects the file unless it is UTF-16 with a byte order mark. The caller
    /// deletes the file.
    ///
    /// Staged under the Nexus data directory rather than the system temp dir:
    /// this runs as LocalSystem, whose temp path is C:\Windows\Temp, and a
    /// scheduled task registered from there reads as a staged payload.
    /// </summary>
    internal static string WriteTempFile(string xml)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Nexus", "tasks");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"nexus-task-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        return path;
    }

    private static string Escape(string s) => s
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&apos;", StringComparison.Ordinal);
}
