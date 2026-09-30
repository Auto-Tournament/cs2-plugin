using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AutoTournamentCS2;

/// <summary>Where a server's config scope came from, in preference order.</summary>
public enum ScopeSource
{
    /// <summary><c>+at_config_scope &lt;name&gt;</c> in the game process's start arguments.</summary>
    StartArgument,

    /// <summary>The <c>at_config_scope</c> convar (e.g. from config.cfg).</summary>
    Convar,

    /// <summary><c>-port &lt;n&gt;</c> in the game process's start arguments.</summary>
    CommandLinePort,

    /// <summary>The <c>hostport</c> convar, read after the server has activated.</summary>
    HostportConvar,

    /// <summary>
    /// No explicit scope and no known port: the host plus a hash of the server's install path.
    /// Distinct per install, but a warning is always logged.
    /// </summary>
    InstallPathFallback,

    /// <summary>
    /// Not even an install path: the host plus the process id. Never shared with another
    /// server, but does not survive a restart. A warning is always logged.
    /// </summary>
    ProcessFallback,
}

/// <summary>A resolved scope, where it came from, and whether it may be cached for the process.</summary>
public sealed record ScopeResolution(string Scope, ScopeSource Source, bool IsFinal)
{
    /// <summary>True when the scope came from neither an explicit name nor a real port.</summary>
    public bool IsFallback => Source is ScopeSource.InstallPathFallback or ScopeSource.ProcessFallback;

    public string Description => Source switch
    {
        ScopeSource.StartArgument => "from start argument",
        ScopeSource.Convar => "from at_config_scope convar",
        ScopeSource.CommandLinePort => "from -port start argument",
        ScopeSource.HostportConvar => "from hostport convar",
        ScopeSource.InstallPathFallback => "FALLBACK from install path - no at_config_scope and no game port found",
        ScopeSource.ProcessFallback => "FALLBACK from process id - no at_config_scope, no game port and no install path found; config will not survive a restart",
        _ => Source.ToString(),
    };
}

/// <summary>Everything the plugin knows about itself when the scope is resolved.</summary>
public sealed class ScopeInputs
{
    /// <summary>The game process's argv, ideally from <c>/proc/self/cmdline</c>.</summary>
    public string[]? CommandLineArgs { get; init; }

    /// <summary>Current value of the <c>at_config_scope</c> convar.</summary>
    public string? ConvarScope { get; init; }

    /// <summary>Current value of the <c>ip</c> convar.</summary>
    public string? ConvarBindIp { get; init; }

    /// <summary>
    /// Current value of the <c>hostport</c> convar. Only trusted when
    /// <see cref="ServerActivated"/> is true: before that it still holds the engine default
    /// (27015) on every server, which is exactly how 1.4.26 collapsed all servers to one key.
    /// </summary>
    public int? HostportConvar { get; init; }

    /// <summary>True once the server has activated (first OnMapStart, or a hot reload).</summary>
    public bool ServerActivated { get; init; }

    public string? MachineName { get; init; }

    /// <summary>The server's install directory (distinct per server on a box).</summary>
    public string? InstallPath { get; init; }

    public int ProcessId { get; init; }
}

/// <summary>
/// Derives the stable identity that scopes this server's rows in the shared database.
///
/// Several MatchZy Enhanced servers on one box normally share a single MySQL database, but per-server
/// values (server id, bootstrap URL and token, remote log URL and header, demo upload URL) are
/// stored in <c>at_server_config</c>, so each row carries the server's scope.
///
/// Resolution order:
///   1. <c>+at_config_scope &lt;name&gt;</c> in the game process's start arguments.
///   2. The <c>at_config_scope</c> convar.
///   3. The bind address and <c>-port</c> from the start arguments.
///   4. The <c>hostport</c> convar, only once the server has activated.
///   5. A fallback that is still distinct per server (install path, else process id), with a
///      loud warning. A key that would be identical for every server on the box is never used.
///
/// The start arguments are read from <c>/proc/self/cmdline</c> on Linux.
/// <see cref="Environment.GetCommandLineArgs"/> is not usable there: CounterStrikeSharp hosts
/// .NET inside the cs2 process through hostfxr, so the runtime never sees the game's argv. That
/// is why 1.4.26 found neither <c>-port</c> nor <c>+at_config_scope</c> and resolved every
/// server to <c>&lt;host&gt;:27015</c>. On Windows the managed args come from the process command
/// line, so they are used as the fallback there.
/// </summary>
public static class ServerIdentity
{
    /// <summary>
    /// Scope used for rows written before scoping existed. Reads fall back to it; writes never
    /// target it.
    /// </summary>
    public const string LegacyScope = "";

    /// <summary>Stand-in host component when neither a bind address nor a machine name is usable.</summary>
    public const string UnknownHost = "unknown-host";

    public const string ProcSelfCmdline = "/proc/self/cmdline";

    /// <summary>
    /// Maximum scope length. Kept well inside the <c>VARCHAR(190)</c> column so a scope is never
    /// silently truncated by the database into a collision with another one.
    /// </summary>
    public const int MaxScopeLength = 180;

    /// <summary>Bind addresses that identify no particular interface, and so identify no server.</summary>
    private static readonly string[] UnusableHosts =
    {
        "", "0", "0.0.0.0", "*", "::", "[::]", "::0", "127.0.0.1", "[::1]", "::1", "localhost",
    };

    /// <summary>Command line flags that carry the game port. <c>tv_port</c> is deliberately absent.</summary>
    private static readonly string[] PortFlags = { "-port", "+port", "-hostport", "+hostport" };

    /// <summary>Command line flags that carry the bind address.</summary>
    private static readonly string[] IpFlags = { "-ip", "+ip" };

    /// <summary>Command line flags that carry an explicit scope override.</summary>
    private static readonly string[] ScopeFlags = { "+at_config_scope", "-at_config_scope" };

    /// <summary>
    /// Full resolution, see the class summary for the order. <see cref="ScopeResolution.IsFinal"/>
    /// is false only when the result could still improve once the server activates (no explicit
    /// scope, no port on the command line, server not yet activated).
    /// </summary>
    public static ScopeResolution Resolve(ScopeInputs inputs)
    {
        string[]? args = inputs.CommandLineArgs;

        string? startArgScope = ParseScopeOverride(args);
        if (startArgScope != null)
        {
            return new ScopeResolution(Sanitize(startArgScope), ScopeSource.StartArgument, true);
        }

        if (!string.IsNullOrWhiteSpace(inputs.ConvarScope))
        {
            return new ScopeResolution(Sanitize(inputs.ConvarScope!), ScopeSource.Convar, true);
        }

        string host = ResolveHost(ParseBindIp(args) ?? inputs.ConvarBindIp, inputs.MachineName);

        int? commandLinePort = ParseGamePort(args);
        if (commandLinePort.HasValue)
        {
            return new ScopeResolution(Derive(host, commandLinePort.Value), ScopeSource.CommandLinePort, true);
        }

        if (inputs.ServerActivated && IsUsablePort(inputs.HostportConvar))
        {
            return new ScopeResolution(Derive(host, inputs.HostportConvar!.Value), ScopeSource.HostportConvar, true);
        }

        if (!string.IsNullOrWhiteSpace(inputs.InstallPath))
        {
            return new ScopeResolution(
                Sanitize($"{host}:path-{ShortHash(NormalisePath(inputs.InstallPath!))}"),
                ScopeSource.InstallPathFallback,
                inputs.ServerActivated);
        }

        return new ScopeResolution(
            Sanitize($"{host}:pid-{inputs.ProcessId.ToString(CultureInfo.InvariantCulture)}"),
            ScopeSource.ProcessFallback,
            inputs.ServerActivated);
    }

    /// <summary>
    /// True when the start arguments alone identify this server (explicit scope or game port), so
    /// persistent config can be loaded before the server activates.
    /// </summary>
    public static bool HasCommandLineIdentity(string[]? args) =>
        ParseScopeOverride(args) != null || ParseGamePort(args).HasValue;

    /// <summary>Builds the <c>host:port</c> scope from a resolved bind address or machine name.</summary>
    public static string Derive(string? bindIp, int gamePort, string? machineName) =>
        Derive(ResolveHost(bindIp, machineName), gamePort);

    private static string Derive(string host, int gamePort) =>
        Sanitize($"{host}:{gamePort.ToString(CultureInfo.InvariantCulture)}");

    private static string ResolveHost(string? bindIp, string? machineName)
    {
        if (IsUsableHost(bindIp)) return bindIp!.Trim();
        if (!string.IsNullOrWhiteSpace(machineName)) return machineName!.Trim();
        return UnknownHost;
    }

    /// <summary>
    /// True when a bind address names one particular interface, and so can identify one server.
    /// </summary>
    public static bool IsUsableHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        return !UnusableHosts.Contains(host!.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when a port is in the valid range.</summary>
    public static bool IsUsablePort(int? port) => port.HasValue && port.Value > 0 && port.Value <= 65535;

    /// <summary>
    /// Reads the game process's argv. On Linux this is <c>/proc/self/cmdline</c>, the only
    /// source that has the game's arguments when .NET is hosted inside cs2. Elsewhere, or if that
    /// fails, <see cref="Environment.GetCommandLineArgs"/>.
    /// </summary>
    public static (string[]? Args, string Source) ReadProcessCommandLine()
    {
        try
        {
            if (File.Exists(ProcSelfCmdline))
            {
                string[] args = ParseProcCmdline(File.ReadAllBytes(ProcSelfCmdline));
                if (args.Length > 0) return (args, ProcSelfCmdline);
            }
        }
        catch
        {
            // fall through to the managed args
        }

        try
        {
            return (Environment.GetCommandLineArgs(), "Environment.GetCommandLineArgs()");
        }
        catch
        {
            return (null, "unavailable");
        }
    }

    /// <summary>
    /// Splits the raw bytes of <c>/proc/&lt;pid&gt;/cmdline</c> (NUL-terminated UTF-8 arguments)
    /// into argv.
    /// </summary>
    public static string[] ParseProcCmdline(byte[]? raw)
    {
        if (raw == null || raw.Length == 0) return Array.Empty<string>();

        string text = Encoding.UTF8.GetString(raw);
        List<string> args = text.Split('\0').ToList();

        // The buffer ends with a NUL, which leaves one empty trailing element.
        while (args.Count > 0 && args[args.Count - 1].Length == 0)
        {
            args.RemoveAt(args.Count - 1);
        }

        return args.ToArray();
    }

    /// <summary>
    /// Reads the game port, e.g. <c>... -port 27025 +tv_port 27030 ...</c> gives 27025.
    /// Returns null when no port flag is present or its value is not a valid port.
    /// </summary>
    public static int? ParseGamePort(string[]? args) =>
        int.TryParse(ValueOfFlag(args, PortFlags), NumberStyles.None, CultureInfo.InvariantCulture, out int port)
        && IsUsablePort(port)
            ? port
            : null;

    /// <summary>Reads the bind address (<c>-ip &lt;addr&gt;</c>). Null when absent.</summary>
    public static string? ParseBindIp(string[]? args) => ValueOfFlag(args, IpFlags);

    /// <summary>
    /// Reads an explicit scope override (<c>+at_config_scope &lt;name&gt;</c> or
    /// <c>+at_config_scope=&lt;name&gt;</c>). Null when absent or empty.
    /// </summary>
    public static string? ParseScopeOverride(string[]? args) => ValueOfFlag(args, ScopeFlags);

    /// <summary>
    /// Normalises a scope so the same server always produces byte-identical rows: trimmed,
    /// lower-cased invariantly, internal whitespace collapsed to '-', and bounded in length.
    /// </summary>
    public static string Sanitize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return UnknownHost;

        char[] chars = raw.Trim().ToLowerInvariant().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.IsWhiteSpace(chars[i])) chars[i] = '-';
        }
        string value = new string(chars);

        if (value.Length > MaxScopeLength)
        {
            value = value.Substring(0, MaxScopeLength);
        }

        return value.Length == 0 ? UnknownHost : value;
    }

    /// <summary>
    /// Returns the value of the first of <paramref name="flags"/> present, as either
    /// <c>flag value</c> or <c>flag=value</c>. Flags are matched whole, so <c>+tv_port</c> never
    /// matches <c>-port</c>. The argument after a flag is not taken as its value when it is
    /// itself a flag (starts with '+' or '-'), so <c>+at_config_scope +map de_dust2</c> has
    /// no scope rather than the scope "+map". Index 0 (the executable) is never a flag.
    /// </summary>
    private static string? ValueOfFlag(string[]? args, string[] flags)
    {
        if (args == null) return null;

        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i]?.Trim() ?? "";
            if (arg.Length == 0) continue;

            foreach (string flag in flags)
            {
                if (arg.Equals(flag, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length) break;
                    string next = args[i + 1]?.Trim() ?? "";
                    if (next.Length == 0 || next[0] == '+' || next[0] == '-') break;
                    return next;
                }

                if (arg.Length > flag.Length + 1
                    && arg.StartsWith(flag, StringComparison.OrdinalIgnoreCase)
                    && arg[flag.Length] == '=')
                {
                    string value = arg.Substring(flag.Length + 1).Trim();
                    if (value.Length > 0) return value;
                }
            }
        }

        return null;
    }

    private static string NormalisePath(string path) =>
        path.Trim().Replace('\\', '/').TrimEnd('/');

    private static string ShortHash(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 5).ToLowerInvariant();
    }
}
