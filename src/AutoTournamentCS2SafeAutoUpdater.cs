using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;

namespace AutoTournamentCS2;

/// <summary>
/// Match-aware CS2 auto-updater that NEVER restarts while a MatchZy Enhanced match is in progress.
/// It only restarts when at_tournament_status is in a safe state (idle/postgame/error).
/// Implemented as part of the main MatchZy Enhanced plugin.
/// </summary>
public partial class AutoTournamentCS2
{

    private const string SteamApiEndpoint =
        "https://api.steampowered.com/ISteamApps/UpToDateCheck/v0001/?appid=730&version={0}";

    // Timings (seconds)
    private const float DefaultUpdateCheckIntervalSeconds = 300f; // 5 minutes
    private const float ShutdownRetryDelaySeconds = 60f;          // 1 minute

    // State
    private static double _updateFoundTime;
    private static bool _updateAvailable;
    private static bool _restartRequired;
    private static int _requiredVersion;
    private static double _nextUpdateCheckAllowedTime;
    private static bool _offlineWarningLogged;

    // Cvars we care about from MatchZy Enhanced
    private static ConVar? _atTournamentStatus;
    private static ConVar? _atTournamentMatch;

    /// <summary>
    /// Initialize the match-safe auto-updater. Called from AutoTournamentCS2.Load().
    /// </summary>
    private void InitializeAutoTournamentCS2SafeAutoUpdater()
    {
        _atTournamentStatus = ConVar.Find("at_tournament_status");
        _atTournamentMatch = ConVar.Find("at_tournament_match");

        RegisterListener<Listeners.OnGameServerSteamAPIActivated>(OnGameServerSteamAPIActivated);

        // Periodic check for updates
        AddTimer(DefaultUpdateCheckIntervalSeconds, CheckServerVersionTimer, TimerFlags.REPEAT);
    }

    private void OnGameServerSteamAPIActivated()
    {
        Logger.LogInformation("[AutoTournamentCS2SafeAutoUpdater] Steam API activated. Match-safe update checks enabled.");
        _offlineWarningLogged = false;
    }

    /// <summary>
    /// Timer callback that kicks off an async update check.
    /// </summary>
    private void CheckServerVersionTimer()
    {
        try
        {
            if (!safeAutoUpdaterEnabled.Value)
            {
                return;
            }

            // In warn-only mode, once we've detected an update we stop polling until restart.
            // This avoids hammering Steam continuously while waiting for an external server
            // manager/admin to perform the update.
            try
            {
                string action = (safeAutoUpdaterAction.Value ?? "warn_only").Trim().ToLowerInvariant();
                if (action != "restart" && _updateAvailable)
                {
                    return;
                }
            }
            catch
            {
                // best-effort
            }

            // Never perform update checks while a MatchZy Enhanced match is in progress; this keeps
            // all Steam API polling and restart decisions strictly outside live matches.
            string status = GetAutoTournamentCS2Status();
            if (IsMatchInProgress(status))
            {
                return;
            }

            if (_restartRequired)
            {
                // Already committed to restarting; no need to keep hammering the Steam API.
                return;
            }

            // Backoff window (offline/DNS failures).
            if (Server.CurrentTime < _nextUpdateCheckAllowedTime)
            {
                return;
            }

            _ = CheckServerVersionAndMaybeScheduleShutdownAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError("[AutoTournamentCS2SafeAutoUpdater] Error scheduling update check: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Performs the actual Steam UpToDateCheck and, if an update is available, schedules a
    /// shutdown that respects MatchZy Enhanced's tournament status.
    /// </summary>
    private async Task CheckServerVersionAndMaybeScheduleShutdownAsync()
    {
        try
        {
            bool hasUpdate = await IsUpdateAvailableAsync();
            if (!hasUpdate)
            {
                return;
            }

            Server.NextFrame(ManageServerUpdate);
        }
        catch (Exception ex) when (IsTransientNetworkOrDnsFailure(ex))
        {
            ApplyOfflineBackoff(ex);
        }
        catch (Exception ex)
        {
            Logger.LogError("[AutoTournamentCS2SafeAutoUpdater] Error while checking for updates: {Message}", ex.Message);
        }
    }

    private void ManageServerUpdate()
    {
        if (!_updateAvailable)
        {
            _updateFoundTime = Server.CurrentTime;
            _updateAvailable = true;

            // Log a clear, machine-parseable marker for external server managers.
            // Your manager can watch for this exact string:
            //   [AT_UPDATE_AVAILABLE] required_version=<number>
            Logger.LogInformation("[AutoTournamentCS2SafeAutoUpdater] New CS2 update released (Required version: {Version})", _requiredVersion);
            Logger.LogInformation("[AT_UPDATE_AVAILABLE] required_version={Version}", _requiredVersion);

            // Notify remote API (if configured) so admins get a clear warning.
            try
            {
                if (!string.IsNullOrEmpty(matchConfig.RemoteLogURL) && !string.IsNullOrEmpty(matchReportServerId.Value))
                {
                    var ev = new AutoTournamentCS2Cs2UpdateRequiredEvent
                    {
                        MatchId = -1,
                        ServerId = matchReportServerId.Value,
                        RequiredVersion = _requiredVersion,
                        Phase = "available",
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    };
                    Task.Run(async () => { await SendEventAsync(ev); });
                }
            }
            catch
            {
                // Best effort; never block shutdown logic on webhook failures.
            }
        }

        string action = "warn_only";
        try
        {
            action = (safeAutoUpdaterAction.Value ?? "warn_only").Trim().ToLowerInvariant();
        }
        catch
        {
            action = "warn_only";
        }

        if (action != "restart")
        {
            // Default for MAT setups: do not quit the server automatically.
            return;
        }

        _restartRequired = true;

        // Try to shut down, but respect MatchZy Enhanced's status.
        TryShutdownRespectingAutoTournamentCS2();
    }

    /// <summary>
    /// Attempts to shut down the server. If MatchZy Enhanced reports a live/active match,
    /// we defer and reschedule instead of quitting.
    /// </summary>
    private void TryShutdownRespectingAutoTournamentCS2()
    {
        if (!_restartRequired)
        {
            return;
        }

        string status = GetAutoTournamentCS2Status();
        string matchSlug = GetAutoTournamentCS2MatchSlug();

        if (IsMatchInProgress(status))
        {
            Logger.LogInformation(
                "[AutoTournamentCS2SafeAutoUpdater] Update available (version {Version}), but MatchZy Enhanced status is '{Status}' for match '{MatchSlug}'. Deferring shutdown.",
                _requiredVersion, status, string.IsNullOrEmpty(matchSlug) ? "<none>" : matchSlug
            );

            // Reschedule another check after a delay; we keep doing this until status is safe.
            AddTimer(ShutdownRetryDelaySeconds, TryShutdownRespectingAutoTournamentCS2, TimerFlags.STOP_ON_MAPCHANGE);
            return;
        }

        Logger.LogInformation(
            "[AutoTournamentCS2SafeAutoUpdater] MatchZy Enhanced status is '{Status}' (safe). Preparing server shutdown for CS2 update {Version}.",
            status, _requiredVersion
        );

        PrepareServerShutdown();
    }

    private string GetAutoTournamentCS2Status()
    {
        try
        {
            return _atTournamentStatus?.GetPrimitiveValue<string>() ?? "idle";
        }
        catch
        {
            return "idle";
        }
    }

    private string GetAutoTournamentCS2MatchSlug()
    {
        try
        {
            return _atTournamentMatch?.GetPrimitiveValue<string>() ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Treat these MatchZy Enhanced statuses as "match in progress" and never restart during them.
    /// </summary>
    private static bool IsMatchInProgress(string status)
    {
        if (string.IsNullOrEmpty(status))
        {
            return false;
        }

        status = status.ToLowerInvariant();
        return status is "loading"
                     or "warmup"
                     or "knife"
                     or "live"
                     or "playing"
                     or "paused"
                     or "halftime";
        // "postgame", "idle", "error" are considered safe to restart.
    }

    /// <summary>
    /// Kicks all human players and then runs "quit".
    /// </summary>
    private void PrepareServerShutdown()
    {
        if (!IsServerSafeToShutdownNow())
        {
            Logger.LogWarning("[AutoTournamentCS2SafeAutoUpdater] Shutdown aborted: MatchZy Enhanced internal state is not safe yet. Will retry.");
            AddTimer(ShutdownRetryDelaySeconds, TryShutdownRespectingAutoTournamentCS2, TimerFlags.STOP_ON_MAPCHANGE);
            return;
        }

        var players = Utilities.GetPlayers()
            .Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false })
            .ToList();

        foreach (var player in players)
        {
            try
            {
                switch (player.Connected)
                {
                    case PlayerConnectedState.PlayerConnected:
                    case PlayerConnectedState.PlayerConnecting:
                    case PlayerConnectedState.PlayerReconnecting:
                        Server.ExecuteCommand(
                            $"kickid {player.UserId} Due to the game update (Version: {_requiredVersion}), the server is now restarting.");
                        break;
                }
            }
            catch
            {
                // Best effort; ignore failures for individual players.
            }
        }

        AddTimer(1.0f, ShutdownServer);
    }

    private void ShutdownServer()
    {
        if (!IsServerSafeToShutdownNow())
        {
            Logger.LogWarning("[AutoTournamentCS2SafeAutoUpdater] Final shutdown step aborted: server no longer in a safe state. Deferring.");
            AddTimer(ShutdownRetryDelaySeconds, TryShutdownRespectingAutoTournamentCS2, TimerFlags.STOP_ON_MAPCHANGE);
            return;
        }

        // Second machine-parseable marker indicating that we are actually quitting now:
        //   [AT_UPDATE_SHUTDOWN] required_version=<number>
        Logger.LogInformation("[AutoTournamentCS2SafeAutoUpdater] Initiating server shutdown for CS2 update {Version}.", _requiredVersion);
        Logger.LogInformation("[AT_UPDATE_SHUTDOWN] required_version={Version}", _requiredVersion);

        // Notify remote API (if configured) that shutdown is imminent.
        try
        {
            if (!string.IsNullOrEmpty(matchConfig.RemoteLogURL) && !string.IsNullOrEmpty(matchReportServerId.Value))
            {
                var ev = new AutoTournamentCS2Cs2UpdateRequiredEvent
                {
                    MatchId = -1,
                    ServerId = matchReportServerId.Value,
                    RequiredVersion = _requiredVersion,
                    Phase = "shutdown",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                };
                Task.Run(async () => { await SendEventAsync(ev); });
            }
        }
        catch
        {
            // Best effort only.
        }
        Server.ExecuteCommand("quit");
    }

    private bool IsServerSafeToShutdownNow()
    {
        try
        {
            string status = GetAutoTournamentCS2Status();
            if (IsMatchInProgress(status))
            {
                return false;
            }

            if (IsAnyMatchFlowStateActive())
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool IsAnyMatchFlowStateActive()
    {
        return
            isMatchSetup ||
            readyAvailable ||
            isWarmup ||
            isKnifeRound ||
            isMatchLive ||
            matchStarted ||
            isPaused;
    }

    /// <summary>
    /// Console command: manually check if the server is up to date and print the result.
    /// Does NOT schedule a restart; purely informational.
    /// </summary>
    [ConsoleCommand("at_check_for_updates", "Check whether this CS2 server is up to date according to Steam.")]
    public void AutoTournamentCS2CheckForUpdates(CCSPlayerController? player, CommandInfo command)
    {
        // Run the check on the next frame to keep the command handler light.
        Server.NextFrame(async () =>
        {
            string prefix = "[AutoTournamentCS2UpToDate]";

            try
            {
                if (!safeAutoUpdaterEnabled.Value)
                {
                    string disabled = $"{prefix} Update checks are disabled (at_safeautoupdater_enabled 0).";
                    if (player != null && player.IsValid)
                    {
                        player.PrintToChat($" {disabled}");
                    }
                    else
                    {
                        Logger.LogInformation(disabled);
                    }
                    return;
                }

                (bool upToDate, int requiredVersion) = await GetUpdateStatusAsync();

                string msg = upToDate
                    ? $"{prefix} Server is up to date."
                    : $"{prefix} Update available. Required version: {requiredVersion}. The auto-updater will restart once MatchZy Enhanced is idle/postgame.";

                if (player != null && player.IsValid)
                {
                    player.PrintToChat($" {msg}");
                }
                else
                {
                    Logger.LogInformation(msg);
                }
            }
            catch (Exception ex) when (IsTransientNetworkOrDnsFailure(ex))
            {
                // Avoid printing a scary "error" in offline environments.
                string err = $"{prefix} Failed to check for updates (DNS/network/offline): {ex.Message}";
                if (player != null && player.IsValid)
                {
                    player.PrintToChat($" {err}");
                }
                else
                {
                    Logger.LogWarning(err);
                }
            }
            catch (Exception ex)
            {
                string err = $"{prefix} Failed to check for updates: {ex.Message}";
                if (player != null && player.IsValid)
                {
                    player.PrintToChat($" {err}");
                }
                else
                {
                    Logger.LogError(err);
                }
            }
        });
    }

    /// <summary>
    /// Returns (upToDate, requiredVersion). Does NOT mutate restart state.
    /// </summary>
    private async Task<(bool upToDate, int requiredVersion)> GetUpdateStatusAsync()
    {
        string steamInfPatchVersion = await GetSteamInfPatchVersionAsync();

        if (string.IsNullOrWhiteSpace(steamInfPatchVersion))
        {
            throw new InvalidOperationException("steam.inf patch version could not be determined.");
        }

        using HttpClient httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var response = await httpClient.GetAsync(string.Format(SteamApiEndpoint, steamInfPatchVersion));

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Steam UpToDateCheck request failed with status {response.StatusCode}.");
        }

        var upToDateCheckResponse = await response.Content.ReadFromJsonAsync<UpToDateCheckResponse>();

        if (upToDateCheckResponse?.Response is not { Success: true } resp)
        {
            // If Steam says Success=false, treat it as "cannot determine".
            throw new InvalidOperationException("Steam UpToDateCheck did not return a successful response.");
        }

        return (resp.UpToDate, resp.RequiredVersion);
    }

    /// <summary>
    /// Checks for an update and updates internal state if one is found.
    /// </summary>
    private async Task<bool> IsUpdateAvailableAsync()
    {
        (bool upToDate, int requiredVersion) = await GetUpdateStatusAsync();

        if (upToDate)
        {
            return false;
        }

        _requiredVersion = requiredVersion;
        return true;
    }

    private async Task<string> GetSteamInfPatchVersionAsync()
    {
        string steamInfPath = Path.Combine(Server.GameDirectory, "csgo", "steam.inf");

        if (!File.Exists(steamInfPath))
        {
            Logger.LogError("[AutoTournamentCS2SafeAutoUpdater] steam.inf not found at {Path}.", steamInfPath);
            return string.Empty;
        }

        try
        {
            string steamInfContents = await File.ReadAllTextAsync(steamInfPath);
            Match match = PatchVersionRegex().Match(steamInfContents);

            if (match.Success)
            {
                return match.Groups["version"].Value;
            }

            Logger.LogError("[AutoTournamentCS2SafeAutoUpdater] Could not find PatchVersion key in {Path}.", steamInfPath);
            return string.Empty;
        }
        catch (Exception ex)
        {
            Logger.LogError("[AutoTournamentCS2SafeAutoUpdater] Error reading steam.inf: {Message}", ex.Message);
            return string.Empty;
        }
    }

    [GeneratedRegex(@"PatchVersion=(?<version>[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex PatchVersionRegex();

    private void ApplyOfflineBackoff(Exception ex)
    {
        int backoffSeconds = Math.Max(30, safeAutoUpdaterOfflineBackoffSeconds.Value);
        _nextUpdateCheckAllowedTime = Server.CurrentTime + backoffSeconds;

        // Log only once per "offline period" to avoid spamming server consoles.
        if (!_offlineWarningLogged)
        {
            _offlineWarningLogged = true;
            Logger.LogWarning(
                "[AutoTournamentCS2SafeAutoUpdater] Steam update check failed (DNS/network/offline): {Message}. Backing off for {BackoffSeconds}s. (Disable with at_safeautoupdater_enabled 0)",
                ex.Message,
                backoffSeconds
            );
        }
    }

    private static bool IsTransientNetworkOrDnsFailure(Exception ex)
    {
        // HttpClient failures typically arrive as HttpRequestException with an inner SocketException.
        // In many server environments, DNS is intentionally unavailable or outbound traffic is blocked.
        if (ex is HttpRequestException hre)
        {
            if (hre.InnerException is SocketException)
            {
                return true;
            }

            // Sometimes the SocketException is nested deeper.
            Exception? inner = hre.InnerException;
            while (inner != null)
            {
                if (inner is SocketException)
                {
                    return true;
                }
                inner = inner.InnerException;
            }
        }

        // Timeout / cancellation should also be treated as transient network failure.
        return ex is TaskCanceledException;
    }
}

// --- Steam UpToDateCheck DTOs ---

public sealed class UpToDateCheckResponse
{
    [JsonPropertyName("response")]
    public UpToDateCheckInnerResponse? Response { get; set; }
}

public sealed class UpToDateCheckInnerResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("up_to_date")]
    public bool UpToDate { get; set; }

    [JsonPropertyName("required_version")]
    public int RequiredVersion { get; set; }
}
