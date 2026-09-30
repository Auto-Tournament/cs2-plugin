using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace AutoTournamentCS2;

public partial class AutoTournamentCS2
{
    public Dictionary<CsTeam, bool> teamReadyOverride = new() {
        {CsTeam.Terrorist, false},
        {CsTeam.CounterTerrorist, false},
        {CsTeam.Spectator, false}
    };

    public bool allowForceReady = true;

    public bool IsTeamsReady()
    {
        return IsTeamReady((int)CsTeam.CounterTerrorist) && IsTeamReady((int)CsTeam.Terrorist);
    }

    public bool IsSpectatorsReady()
    {
        return IsTeamReady((int)CsTeam.Spectator);
    }

    public bool IsTeamReady(int team)
    {
        int minPlayers = GetPlayersPerTeam(team);
        int minReady = GetTeamMinReady(team);
        (int playerCount, int readyCount) = GetTeamPlayerCount(team, false);

        // team is the CS2 team number (1 = Spectator, 2 = T, 3 = CT).
        Log($"[IsTeamReady] team: {team} ({ReadyLogic.TeamLabel(team)}) minPlayers:{minPlayers} minReady:{minReady} playerCount:{playerCount} readyCount:{readyCount} tracked:{playerData.Count}");

        bool forced = allowForceReady
            && Enum.IsDefined(typeof(CsTeam), team)
            && teamReadyOverride.ContainsKey((CsTeam)team)
            && IsTeamForcedReady((CsTeam)team);

        return ReadyLogic.IsTeamReady(team, playerCount, readyCount, minPlayers, minReady, readyAvailable, forced);
    }

    /// <summary>
    /// Makes sure a player who is readying up is in playerData under a live controller.
    /// The ready count only walks playerData, so a player missing from it (or tracked
    /// under a stale controller) could type .ready and never be counted.
    /// </summary>
    private void EnsurePlayerTrackedForReady(CCSPlayerController player)
    {
        if (!player.IsValid || !player.UserId.HasValue) return;
        int userId = player.UserId.Value;
        bool isTracked = playerData.TryGetValue(userId, out var tracked);
        bool trackedValid = isTracked && tracked != null && tracked.IsValid;
        if (!ReadyLogic.NeedsTrackingOnReady(player.IsHLTV, player.IsBot, isTracked, trackedValid)) return;

        playerData[userId] = player;
        connectedPlayers = GetRealPlayersCount();
        Log($"[!ready command] {player.PlayerName} (UserId={userId}, TeamNum={player.TeamNum}) was {(isTracked ? "tracked under a stale controller" : "not tracked")}; registered now.");
    }

    public int GetPlayersPerTeam(int team)
    {
        if (team == (int)CsTeam.CounterTerrorist || team == (int)CsTeam.Terrorist) return matchConfig.PlayersPerTeam;
        if (team == (int)CsTeam.Spectator) return matchConfig.MinSpectatorsToReady;
        return 0;
    }

    public int GetTeamMinReady(int team)
    {
        if (team == (int)CsTeam.CounterTerrorist || team == (int)CsTeam.Terrorist) return matchConfig.MinPlayersToReady;
        if (team == (int)CsTeam.Spectator) return matchConfig.MinSpectatorsToReady;
        return 0;
    }

    public (int, int) GetTeamPlayerCount(int team, bool includeCoaches = false)
    {
        int playerCount = 0;
        int readyCount = 0;
        foreach (var key in playerData.Keys)
        {
            if (!playerData[key].IsValid) continue;
            if (playerData[key].TeamNum == team) {
                playerCount++;
                // playerReadyStatus may not yet have been initialized for every connected player.
                // Treat missing entries as "not ready" instead of throwing.
                if (playerReadyStatus.TryGetValue(key, out bool isReady) && isReady) readyCount++;
            }
        }
        return (playerCount, readyCount);
    }

    public bool IsTeamForcedReady(CsTeam team) {
        return teamReadyOverride[team];
    }

    /// <summary>
    /// Marks CT/T players as ready (simulated .ready) when auto-ready is enabled.
    /// Match start is still gated by normal readiness checks (team size / min ready).
    /// </summary>
    public void CheckAndAutoReadyPlayers()
    {
        // Skip if auto-ready is disabled, ready system not available, or match started
        if (!autoReadyEnabled.Value || !readyAvailable || matchStarted)
        {
            Log($"[CheckAndAutoReadyPlayers] Skipping - autoReadyEnabled={autoReadyEnabled.Value}, readyAvailable={readyAvailable}, matchStarted={matchStarted}");
            return;
        }

        // Skip auto-ready in simulation mode - it has its own ready logic
        if (isSimulationMode)
        {
            Log($"[CheckAndAutoReadyPlayers] Skipping - simulation mode");
            return;
        }

        (int ctPlayerCount, int ctReadyCount) = GetTeamPlayerCount((int)CsTeam.CounterTerrorist, false);
        (int tPlayerCount, int tReadyCount) = GetTeamPlayerCount((int)CsTeam.Terrorist, false);
        int totalPlayersOnPlayableTeams = ctPlayerCount + tPlayerCount;

        Log($"[CheckAndAutoReadyPlayers] isMatchSetup={isMatchSetup}, CT={ctPlayerCount} (ready: {ctReadyCount}), T={tPlayerCount} (ready: {tReadyCount}), totalPlayable={totalPlayersOnPlayableTeams}");
        Log($"[CheckAndAutoReadyPlayers] playerData count: {playerData.Count}, readyStatus count: {playerReadyStatus.Count}");

        // Debug: Log all players in playerData
        foreach (var kvp in playerData)
        {
            if (kvp.Value.IsValid)
            {
                bool isReady = playerReadyStatus.TryGetValue(kvp.Key, out bool ready) && ready;
                Log($"[CheckAndAutoReadyPlayers] Player in playerData: UserId={kvp.Key}, Name={kvp.Value.PlayerName}, TeamNum={kvp.Value.TeamNum}, Ready={isReady}");
            }
        }

        if (totalPlayersOnPlayableTeams <= 0)
        {
            Log("[CheckAndAutoReadyPlayers] No players on CT/T yet; skipping auto-ready scheduling.");
            return;
        }

        bool isUnbalancedEveryoneReadyMode = !isMatchSetup && minimumReadyRequired == 0 && (ctPlayerCount == 0 || tPlayerCount == 0);
        if (isUnbalancedEveryoneReadyMode)
        {
            Log("[CheckAndAutoReadyPlayers] Waiting for at least one player on both CT and T before auto-readying in everyone-ready mode.");
            return;
        }

        bool anyReadyScheduled = false;

        foreach (var key in playerData.Keys)
        {
            if (!playerData[key].IsValid) continue;
            
            var p = playerData[key];
            // Only mark players on CT or T teams, skip spectators
            if (p.TeamNum == (int)CsTeam.CounterTerrorist || p.TeamNum == (int)CsTeam.Terrorist)
            {
                if (!playerReadyStatus.ContainsKey(key))
                {
                    playerReadyStatus[key] = false;
                }

                // Respect opt-out: players who typed .unready should not be auto-readied until they type .ready again.
                if (autoReadyOptOutUserIds.Contains(key))
                {
                    continue;
                }

                // Only schedule auto-ready if they're not already ready and we don't already have a timer pending.
                if (!playerReadyStatus[key] && !autoReadyPendingReadyTimers.ContainsKey(key))
                {
                    float baseDelay = autoReadyPlayerReadyDelay.Value;
                    if (baseDelay < 0.0f) baseDelay = 0.0f;

                    // Small jitter so it feels like real humans readying up.
                    float jitter = (new Random().Next(0, 100) / 100.0f) * 1.0f; // 0.0 - 1.0s
                    float delay = baseDelay + jitter;

                    autoReadyPendingReadyTimers[key] = AddTimer(delay, () =>
                    {
                        autoReadyPendingReadyTimers.Remove(key);

                        if (!autoReadyEnabled.Value || !readyAvailable || matchStarted) return;
                        if (autoReadyOptOutUserIds.Contains(key)) return;
                        if (!playerData.TryGetValue(key, out var delayedPlayer) || !IsPlayerValid(delayedPlayer)) return;
                        if (delayedPlayer.TeamNum != (int)CsTeam.CounterTerrorist && delayedPlayer.TeamNum != (int)CsTeam.Terrorist) return;

                        // Simulate the player typing .ready by calling the same handler.
                        Log($"[AutoReady] Simulating .ready for {delayedPlayer.PlayerName} (UserId={key}) after {delay:0.##}s.");
                        OnPlayerReady(delayedPlayer, null);
                    });

                    anyReadyScheduled = true;
                    Log($"[AutoReady] Scheduled simulated .ready for {p.PlayerName} (UserId={key}) in {delay:0.##}s.");
                }
            }
        }

        if (anyReadyScheduled)
        {
            Log("[CheckAndAutoReadyPlayers] Scheduled simulated .ready for unready players on CT/T.");
        }
        
        Log("[CheckAndAutoReadyPlayers] Auto-ready pass complete - checking if match can start.");
        CheckLiveRequired();
    }

    [ConsoleCommand("css_forceready", "Force-readies the team")]
    public void OnForceReadyCommandCommand(CCSPlayerController? player, CommandInfo? command)
    {
        Log($"{readyAvailable} {isMatchSetup} {allowForceReady} {IsPlayerValid(player)}");
        if (!readyAvailable || !isMatchSetup || !allowForceReady || !IsPlayerValid(player)) return;

        int minReady = GetTeamMinReady(player!.TeamNum);
        (int playerCount, int readyCount) = GetTeamPlayerCount(player!.TeamNum, false);

        if (playerCount < minReady) 
        {
            // ReplyToUserCommand(player, $"You must have at least {minReady} player(s) on the server to ready up.");
            ReplyToUserCommand(player, Localizer["at.rs.minreadyplayers", minReady]);
            return;
        }

        foreach (var key in playerData.Keys)
        {
            if (!playerData[key].IsValid) continue;
            if (playerData[key].TeamNum == player.TeamNum) {
                playerReadyStatus[key] = true;
                // ReplyToUserCommand(playerData[key], $"Your team was force-readied by {player.PlayerName}");
                ReplyToUserCommand(playerData[key], Localizer["at.rs.forcereadiedby", player.PlayerName]);
            }
        }

        teamReadyOverride[(CsTeam)player.TeamNum] = true;
        CheckLiveRequired();
    }

    private void ClearAutoReadyState()
    {
        foreach (var kv in autoReadyPendingReadyTimers)
        {
            kv.Value?.Kill();
        }
        autoReadyPendingReadyTimers.Clear();
        autoReadyOptOutUserIds.Clear();
    }

    private void ResetPlayerWarmupReadyAndAutoReady(CCSPlayerController? player)
    {
        if (player == null || !player.UserId.HasValue) return;
        if (!readyAvailable || matchStarted) return;

        int uid = player.UserId.Value;
        if (autoReadyPendingReadyTimers.TryGetValue(uid, out var pending))
        {
            pending?.Kill();
            autoReadyPendingReadyTimers.Remove(uid);
        }

        playerReadyStatus[uid] = false;
    }

    private void ClearAutoReadySimulationState()
    {
        autoReadySimulationFlowScheduled = false;
        autoReadySimulationFlowStartedForMap = false;
        autoReadySimulationBotUserIds.Clear();
    }

    private void ScheduleAutoReadySimulationFlowIfNeeded(float delaySeconds)
    {
        if (!autoReadySimulationEnabled.Value)
        {
            return;
        }

        // Full simulation mode has its own bot spawning and ready flow.
        if (isSimulationMode)
        {
            return;
        }

        if (matchStarted || !readyAvailable)
        {
            return;
        }

        // Only schedule once per map/warmup cycle.
        if (autoReadySimulationFlowScheduled || autoReadySimulationFlowStartedForMap)
        {
            return;
        }

        autoReadySimulationFlowScheduled = true;
        AddTimer(delaySeconds, () =>
        {
            autoReadySimulationFlowScheduled = false;
            StartAutoReadySimulationFlow();
        });
    }

    private void StartAutoReadySimulationFlow()
    {
        if (!autoReadySimulationEnabled.Value || isSimulationMode)
        {
            return;
        }

        if (matchStarted || !readyAvailable)
        {
            return;
        }

        if (autoReadySimulationFlowStartedForMap)
        {
            return;
        }

        // Only run when no humans are connected to avoid interfering with real players.
        bool anyHumanConnected = false;

        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (player == null) continue;
            if (!player.IsValid) continue;
            if (player.IsHLTV) continue;
            if (!player.UserId.HasValue) continue;
            if (player.Connected != PlayerConnectedState.PlayerConnected) continue;

            if (!player.IsBot)
            {
                anyHumanConnected = true;
            }
        }

        if (anyHumanConnected)
        {
            Log($"[AutoReadySimulation] Skipping ready-simulation bot spawn (human player connected).");
            return;
        }

        autoReadySimulationFlowStartedForMap = true;
        autoReadySimulationBotUserIds.Clear();

        float delayBetweenBots = autoReadySimulationBotSpawnDelay.Value;
        if (delayBetweenBots < 0.0f) delayBetweenBots = 0.0f;

        Log($"[AutoReadySimulation] Spawning ready-simulation bots: 1 CT now, 1 T in {delayBetweenBots:0.##}s.");

        // Ensure bots can stay when server is empty, and avoid team limits kicking them.
        // Also clear any pre-existing bots (CS2 may spawn bots at startup depending on gamemode cfg).
        Server.ExecuteCommand("bot_join_after_player 0; mp_autokick 0; mp_autoteambalance 0; mp_limitteams 0; bot_quota_mode normal; bot_kick; bot_quota 0");

        // Spawn first bot on CT
        AddTimer(0.0f, () =>
        {
            if (!autoReadySimulationEnabled.Value || matchStarted || !readyAvailable) return;
            Server.ExecuteCommand("bot_join_team CT");
            Server.ExecuteCommand("bot_quota 1");
        });

        // Spawn second bot on T after configured delay
        AddTimer(delayBetweenBots, () =>
        {
            if (!autoReadySimulationEnabled.Value || matchStarted || !readyAvailable) return;
            Server.ExecuteCommand("bot_join_team T");
            Server.ExecuteCommand("bot_quota 2");
        });

        // After both spawns have had time to connect, register them in MatchZy Enhanced's ready tracking.
        float registrationDelay = delayBetweenBots + 3.0f;
        AddTimer(registrationDelay, EnsureAutoReadySimulationBotsTracked);
    }

    private void EnsureAutoReadySimulationBotsTracked()
    {
        if (!autoReadySimulationEnabled.Value || isSimulationMode)
        {
            return;
        }

        if (matchStarted || !readyAvailable)
        {
            return;
        }

        int added = 0;
        int totalBotsFound = 0;
        var trackedBots = new List<CCSPlayerController>();

        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (player == null) continue;
            if (!player.IsValid) continue;
            if (player.IsHLTV) continue;
            if (!player.IsBot) continue;
            if (!player.UserId.HasValue) continue;
            if (player.Connected != PlayerConnectedState.PlayerConnected) continue;

            totalBotsFound++;
            int userId = player.UserId.Value;

            if (!playerData.ContainsKey(userId))
            {
                playerData[userId] = player;
                added++;
            }

            autoReadySimulationBotUserIds.Add(userId);
            trackedBots.Add(player);
        }

        connectedPlayers = GetRealPlayersCount();

        Log($"[AutoReadySimulation] Bot registration pass complete: totalBotsFound={totalBotsFound}, newlyTracked={added}, trackedBotUserIds={autoReadySimulationBotUserIds.Count}.");

        // Start bots as unready, then simulate them typing .ready with a small stagger.
        trackedBots.Sort((a, b) => (a.UserId ?? 0).CompareTo(b.UserId ?? 0));

        float delayStep = 0.5f;
        for (int i = 0; i < trackedBots.Count; i++)
        {
            var bot = trackedBots[i];
            if (!bot.UserId.HasValue) continue;

            int userId = bot.UserId.Value;
            playerReadyStatus[userId] = false;

            float delay = 0.75f + (i * delayStep);
            AddTimer(delay, () =>
            {
                if (!autoReadySimulationEnabled.Value || isSimulationMode) return;
                if (matchStarted || !readyAvailable) return;
                if (!IsPlayerValid(bot) || !bot.UserId.HasValue) return;

                Log($"[AutoReadySimulation] Simulating .ready for bot UserId={bot.UserId.Value}, Name={bot.PlayerName} after {delay:0.##}s.");
                OnPlayerReady(bot, null);
            });
        }
    }
}
