
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace AutoTournamentCS2;
public partial class AutoTournamentCS2
{
    public HookResult EventPlayerConnectFullHandler(EventPlayerConnectFull @event, GameEventInfo info)
    {
        try
        {
            CCSPlayerController? player = @event.Userid;

            // For connect events we only need a valid controller + UserId; the PlayerPawn
            // may not yet be fully initialized at this stage (especially for bots), so
            // using IsPlayerValid here can cause us to skip simulation mappings and
            // player_connect events. Keep the check lightweight.
            if (player == null || !player.IsValid || !player.UserId.HasValue)
            {
                return HookResult.Continue;
            }
            Log($"[FULL CONNECT] Player ID: {player!.UserId}, Name: {player.PlayerName} has connected!");

            // Do not include HLTV / SourceTV in any of the ready system or match player
            // tracking. They are spectators only and should never block warmup going
            // live or appear in "unready players" messages.
            if (player.IsHLTV)
            {
                Log("[EventPlayerConnectFull] Detected HLTV/SourceTV controller; skipping ready tracking and match player mapping.");
                return HookResult.Continue;
            }

            // Handling whitelisted players (skip for simulation bots). Admins are allowed
            // to bypass the MatchZy Enhanced whitelist and may connect even if they are not on the
            // per-server whitelist or in the match roster.
            bool isSimulationBot = isSimulationMode && player.IsBot;
            if (!isSimulationBot && (!player.IsBot || !player.IsHLTV))
            {
                var steamId = player.SteamID;

                bool kicked = HandlePlayerWhitelist(player, steamId.ToString());
                if (kicked) return HookResult.Continue;

                if (isMatchSetup || matchModeOnly)
                {
                    // Allow admins to connect even if they are not part of the current
                    // match configuration; they may spectate or administer the server.
                    if (IsPlayerAdmin(player))
                    {
                        return HookResult.Continue;
                    }

                    CsTeam team = GetPlayerTeam(player);
                    if (team == CsTeam.None)
                    {
                        Log($"[EventPlayerConnectFull] KICKING PLAYER STEAMID: {steamId}, Name: {player.PlayerName} (NOT ALLOWED!)");
                        PrintToAllChat($"Kicking player {player.PlayerName} - Not a player in this game.");
                        KickPlayer(player);
                        return HookResult.Continue;
                    }
                }
            }

            if (player.UserId.HasValue)
            {
                playerData[player.UserId.Value] = player;
                connectedPlayers = GetRealPlayersCount();
                if (readyAvailable && !matchStarted)
                {
                    Log($"[AutoReady] Player connected: userId={player.UserId.Value}, name={player.PlayerName}, autoReadyEnabled={autoReadyEnabled.Value}, isMatchSetup={isMatchSetup}, connectedPlayers={connectedPlayers}, TeamNum={player.TeamNum}");

                    ResetPlayerWarmupReadyAndAutoReady(player);
                    HandleClanTags();

                    if (autoReadyEnabled.Value && (player.TeamNum == (int)CsTeam.CounterTerrorist || player.TeamNum == (int)CsTeam.Terrorist))
                    {
                        AddTimer(autoReadyCheckDelay.Value, () =>
                        {
                            CheckAndAutoReadyPlayers();
                        });
                    }
                }
                else
                {
                    Log($"[AutoReady] Ready system not active on connect (readyAvailable={readyAvailable}, matchStarted={matchStarted}); defaulting new player to ready=true for internal tracking.");
                    playerReadyStatus[player.UserId.Value] = true;
                }
                playerConnectionTimes[player.SteamID] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // In simulation mode, map this bot to a configured player identity.
                if (isSimulationMode && player.IsBot)
                {
                    var identity = AssignSimulationIdentityForBot(player);
                    if (identity != null)
                    {
                        Log($"[EventPlayerConnectFull] Simulation bot mapped to config player SteamID={identity.ConfigSteamId}, Name={identity.ConfigName}, TeamSlot={identity.TeamSlot}");
                    }
                }
            }
            // May not be required, but just to be on safe side so that player data is properly updated in dictionaries
            // Update: Commenting the below function as it was being called multiple times on map change.
            // UpdatePlayersMap();

            if (readyAvailable && !matchStarted)
            {
                // Start Warmup when first player connect and match is not started.
                if (GetRealPlayersCount() == 1)
                {
                    Log($"[FULL CONNECT] First player has connected, starting warmup!");
                    ExecUnpracCommands();
                    AutoStart();
                }
            }

            // Send player_connect event
            if (!string.IsNullOrEmpty(matchConfig.RemoteLogURL) && isMatchSetup)
            {
                Log($"[EventPlayerConnectFull] Sending player_connect event for {player.PlayerName}");
                
                // Team will be assigned later for connects; in simulation mode the identity
                // may already be mapped to a configured player.
                var playerInfo = BuildPlayerInfo(player, "none");

                Log($"[EventPlayerConnectFull] player_connect payload: steamid={playerInfo.SteamId}, name={playerInfo.Name}, team={playerInfo.Team}");

                var playerConnectEvent = new AutoTournamentCS2PlayerConnectedEvent
                {
                    MatchId = liveMatchId,
                    Player = playerInfo
                };

                Task.Run(async () => {
                    await SendEventAsync(playerConnectEvent);
                });
            }
            else
            {
                if (string.IsNullOrEmpty(matchConfig.RemoteLogURL))
                {
                    // Before any remote log URL has ever been configured in this server session,
                    // early connect/disconnect events are expected to be dropped while an external
                    // controller is still wiring up webhooks. Log at most once to avoid spam.
                    if (!remoteLogUrlEverConfigured && !remoteLogUrlMissingWarningLogged)
                    {
                        Log($"[EventPlayerConnectFull] Skipping player_connect event - RemoteLogURL not configured (no URL has been set yet)");
                        remoteLogUrlMissingWarningLogged = true;
                    }
                }
                else
                {
                    Log($"[EventPlayerConnectFull] Skipping player_connect event - Match not setup");
                }
            }

            TriggerMatchReportUpload("player_connect");
            
            // Check if FFW should be cancelled (player from missing team rejoined)
            if (ffwEnabled.Value && ffwTimer != null && player.UserId.HasValue)
            {
                if (player.TeamNum == ffwTeamMissing)
                {
                    CancelFFWTimer();
                }
            }
            
            return HookResult.Continue;

        }
        catch (Exception e)
        {
            Log($"[EventPlayerConnectFull FATAL] An error occurred: {e.Message}");
            return HookResult.Continue;
        }

    }
    public HookResult EventPlayerDisconnectHandler(EventPlayerDisconnect @event, GameEventInfo info)
    {
        try
        {
            CCSPlayerController? player = @event.Userid;

            if (player != null && player.IsValid && player.UserId.HasValue && !IsPlayerValid(player))
            {
                // A player without a pawn (dead, spectating, or a bot leaving on map change) still
                // has to be dropped from tracking. Keeping it left a stale controller under a UserId
                // the engine later hands to a new bot, so that bot was never counted or readied
                // (QA: match 64 stuck in warmup at 5 vs 4).
                int pawnlessUserId = player.UserId.Value;
                playerReadyStatus.Remove(pawnlessUserId);
                playerData.Remove(pawnlessUserId);
                connectedPlayers = GetRealPlayersCount();
                if (isSimulationMode) ReleaseSimulationSlot(pawnlessUserId, "disconnect without pawn");
                Log($"[EventPlayerDisconnect] Removed tracking for pawnless player UserId={pawnlessUserId} ({player.PlayerName}).");
                return HookResult.Continue;
            }

            if (!IsPlayerValid(player)) return HookResult.Continue;
            if (!player!.UserId.HasValue) return HookResult.Continue;
            int userId = player.UserId.Value;

            playerReadyStatus.Remove(userId);
            playerData.Remove(userId);
            connectedPlayers = GetRealPlayersCount();
            playerConnectionTimes.Remove(player.SteamID);
            
            // Auto-ready tracking cleanup
            autoReadyOptOutUserIds.Remove(userId);
            if (autoReadyPendingReadyTimers.TryGetValue(userId, out var pending))
            {
                pending?.Kill();
                autoReadyPendingReadyTimers.Remove(userId);
            }

            bool wasWarmupReady = readyAvailable && !matchStarted;

            if (atTeam1.coach.Contains(player))
            {
                atTeam1.coach.Remove(player);
                SetPlayerVisible(player);
                player.Clan = "";
            }
            else if (atTeam2.coach.Contains(player))
            {
                atTeam2.coach.Remove(player);
                SetPlayerVisible(player);
                player.Clan = "";
            }
            noFlashList.Remove(userId);
            lastGrenadesData.Remove(userId);
            nadeSpecificLastGrenadeData.Remove(userId);

            // Send player_disconnect event
            if (!string.IsNullOrEmpty(matchConfig.RemoteLogURL) && isMatchSetup)
            {
                Log($"[EventPlayerDisconnect] Sending player_disconnect event for {player.PlayerName}");
                
                string teamName = "none";
                if (reverseTeamSides.ContainsKey("CT") && player.TeamNum == 3)
                {
                    teamName = reverseTeamSides["CT"].teamName;
                }
                else if (reverseTeamSides.ContainsKey("TERRORIST") && player.TeamNum == 2)
                {
                    teamName = reverseTeamSides["TERRORIST"].teamName;
                }

                // Build the player info first so that, in simulation mode, we can still
                // resolve the configured identity (SteamID + name) before clearing any
                // internal simulation mapping for this bot.
                var playerInfo = BuildPlayerInfo(player, teamName);

                // Clear simulation mapping for this player, if any, *after* we have built
                // the event payload. This ensures player_disconnect events for simulated
                // matches report the configured player identity instead of the raw bot.
                if (isSimulationMode)
                {
                    ReleaseSimulationSlot(userId, "disconnect");
                }

                var playerDisconnectEvent = new AutoTournamentCS2PlayerDisconnectedEvent
                {
                    MatchId = liveMatchId,
                    Player = playerInfo
                };

                Task.Run(async () => {
                    await SendEventAsync(playerDisconnectEvent);
                });
            }
            else
            {
                if (string.IsNullOrEmpty(matchConfig.RemoteLogURL))
                {
                    // Avoid spamming disconnect warnings before any remote log URL has ever been
                    // configured in this session. Once a URL has been set, we resume logging
                    // normally so operational misconfigurations are visible.
                    if (!remoteLogUrlEverConfigured && !remoteLogUrlMissingWarningLogged)
                    {
                        Log($"[EventPlayerDisconnect] Skipping player_disconnect event - RemoteLogURL not configured (no URL has been set yet)");
                        remoteLogUrlMissingWarningLogged = true;
                    }
                }
                else
                {
                    Log($"[EventPlayerDisconnect] Skipping player_disconnect event - Match not setup");
                }
            }

            if (isSimulationMode)
            {
                // No-op when the event branch above already released it.
                ReleaseSimulationSlot(userId, "disconnect");
            }

            TriggerMatchReportUpload("player_disconnect");

            if (wasWarmupReady)
            {
                CheckLiveRequired();
            }
            
            // Check if FFW should be started (entire team left)
            if (ffwEnabled.Value && isMatchLive && !isSimulationMode)
            {
                CheckAndStartFFW();
            }
            
            return HookResult.Continue;
        }
        catch (Exception e)
        {
            Log($"[EventPlayerDisconnect FATAL] An error occurred: {e.Message}");
            return HookResult.Continue;
        }
    }

    public HookResult EventCsWinPanelRoundHandler(EventCsWinPanelRound @event, GameEventInfo info)
    {
        // EventCsWinPanelRound has stopped firing after Arms Race update, hence we handle knife round winner in EventRoundEnd.

        // Log($"[EventCsWinPanelRound PRE] finalEvent: {@event.FinalEvent}");
        // if (isKnifeRound && matchStarted)
        // {
        //     HandleKnifeWinner(@event);
        // }
        return HookResult.Continue;
    }

    public HookResult EventCsWinPanelMatchHandler(EventCsWinPanelMatch @event, GameEventInfo info)
    {
        try
        {
            HandleMatchEnd();
            // ResetMatch();
            return HookResult.Continue;
        }
        catch (Exception e)
        {
            Log($"[EventCsWinPanelMatch FATAL] An error occurred: {e.Message}");
            return HookResult.Continue;
        }
    }

    public HookResult EventRoundStartHandler(EventRoundStart @event, GameEventInfo info)
    {
        try
        {
            // If we deferred the simulation flow because a changelevel was in progress,
            // schedule it once the first real round starts on the target map. We add a
            // short delay so the server is fully in warmup and ready for connections
            // before we begin spawning bots and sending simulated events.
            if (isSimulationMode && simulationFlowDeferred)
            {
                string currentMap = Server.MapName;
                if (string.IsNullOrEmpty(simulationTargetMap) || string.Equals(currentMap, simulationTargetMap, StringComparison.OrdinalIgnoreCase))
                {
                    Log($"[EventRoundStart] Scheduling deferred simulation flow on map {currentMap} (simulationTargetMap={simulationTargetMap}) after 5s.");
                    simulationFlowDeferred = false;
                    ScheduleSimulationFlowStart(5.0f);
                }
                else
                {
                    Log($"[EventRoundStart] Simulation flow still deferred; currentMap={currentMap}, targetMap={simulationTargetMap}.");
                }
            }

            // In simulation mode, aggressively enforce sv_cheats/host_timescale at the
            // beginning of every round so that any external configs or manual commands
            // cannot accidentally leave the server in a non-simulated state on later
            // maps or rounds (e.g. map 3 in a BO3/BO5 series). For real (non-simulation,
            // non-practice) matches we likewise enforce normal cheats/timescale settings.
            if (isSimulationMode)
            {
                ApplySimulationTimescaleAndCheats();
            }
            else
            {
                ApplyNormalTimescaleAndCheatsForRealMatches();
            }

            HandlePostRoundStartEvent(@event);
            return HookResult.Continue;
        }
        catch (Exception e)
        {
            Log($"[EventRoundStart FATAL] An error occurred: {e.Message}");
            return HookResult.Continue;
        }
    }

    public HookResult EventRoundFreezeEndHandler(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        try
        {
            if (!matchStarted) return HookResult.Continue;
            HashSet<CCSPlayerController> coaches = GetAllCoaches();

            foreach (var coach in coaches)
            {
                if (!IsPlayerValid(coach)) continue;
                // If coaches are still left alive after freezetime ends, this code will force them to spectate their team again.
                if (coach.PlayerPawn.Value?.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;

                Position coachPosition = new(coach.PlayerPawn.Value!.CBodyComponent!.SceneNode!.AbsOrigin, coach.PlayerPawn.Value!.CBodyComponent!.SceneNode!.AbsRotation);
                coach!.PlayerPawn.Value!.Teleport(new Vector(coachPosition.PlayerPosition.X, coachPosition.PlayerPosition.Y, coachPosition.PlayerPosition.Z + 20.0f), coachPosition.PlayerAngle, new Vector(0, 0, 0));
                AddTimer(1.5f, () =>
                {
                    coach!.PlayerPawn.Value!.Teleport(new Vector(coachPosition.PlayerPosition.X, coachPosition.PlayerPosition.Y, coachPosition.PlayerPosition.Z + 20.0f), coachPosition.PlayerAngle, new Vector(0, 0, 0));
                    CsTeam oldTeam = GetCoachTeam(coach);
                    coach.ChangeTeam(CsTeam.Spectator);
                    AddTimer(0.01f, () => coach.ChangeTeam(oldTeam));
                });
            }
            return HookResult.Continue;
        }
        catch (Exception e)
        {
            Log($"[EventRoundFreezeEnd FATAL] An error occurred: {e.Message}");
            return HookResult.Continue;
        }
    }

    public HookResult EventPlayerGivenC4(EventPlayerGivenC4 @event, GameEventInfo info) {
        try {
            if (!matchStarted) return HookResult.Continue;
            if (@event.Userid == null) return HookResult.Continue;
            var recv = @event.Userid;

            // check if coach
            var coaches = reverseTeamSides["TERRORIST"].coach;
            if (coaches.Contains(recv)) {
                TransferCoachBomb(recv);
            }
        } catch (Exception e) {
            Log($"[EventPlayerGivenC4 FATAL] An error occured: {e.Message}");
        }
        return HookResult.Continue;
    }

    public void OnEntitySpawnedHandler(CEntityInstance entity)
    {
        try
        {
            if (!isPractice || entity == null || entity.Entity == null) return;
            if (!Constants.ProjectileTypeMap.ContainsKey(entity.Entity.DesignerName)) return;

            Server.NextFrame(() => {
                CBaseCSGrenadeProjectile projectile = new CBaseCSGrenadeProjectile(entity.Handle);

                if (!projectile.IsValid ||
                    !projectile.Thrower.IsValid ||
                    projectile.Thrower.Value == null ||
                    projectile.Thrower.Value.Controller.Value == null ||
                    projectile.Globalname == "custom"
                ) return;

                CCSPlayerController player = new(projectile.Thrower.Value.Controller.Value.Handle);
                if(!player.IsValid || player.PlayerPawn.Value == null || !player.PlayerPawn.IsValid) return;
                int client = player.UserId!.Value;
                
                Vector position = new(projectile.AbsOrigin!.X, projectile.AbsOrigin.Y, projectile.AbsOrigin.Z);
                QAngle angle = new(projectile.AbsRotation!.X, projectile.AbsRotation.Y, projectile.AbsRotation.Z);
                Vector velocity = new(projectile.AbsVelocity.X, projectile.AbsVelocity.Y, projectile.AbsVelocity.Z);
                string nadeType = Constants.ProjectileTypeMap[entity.Entity.DesignerName];

                if (!lastGrenadesData.ContainsKey(client)) {
                    lastGrenadesData[client] = new();
                }

                if (!nadeSpecificLastGrenadeData.ContainsKey(client))
                {
                    nadeSpecificLastGrenadeData[client] = new(){};
                }

                GrenadeThrownData lastGrenadeThrown = new(
                    position, 
                    angle, 
                    velocity, 
                    player.PlayerPawn.Value.CBodyComponent!.SceneNode!.AbsOrigin, 
                    player.PlayerPawn.Value.EyeAngles,
                    nadeType,
                    DateTime.Now,
                    projectile.ItemIndex
                );

                nadeSpecificLastGrenadeData[client][nadeType] = lastGrenadeThrown;
                lastGrenadesData[client].Add(lastGrenadeThrown);

                if (maxLastGrenadesSavedLimit != 0 && lastGrenadesData[client].Count > maxLastGrenadesSavedLimit)
                {
                    lastGrenadesData[client].RemoveAt(0);
                }

                lastGrenadeThrownTime[(int)projectile.Index] = DateTime.Now;
                if (smokeColorEnabled.Value && nadeType == "smoke")
                {
                    CSmokeGrenadeProjectile smokeProjectile = new(entity.Handle);
                    smokeProjectile.SmokeColor.X = GetPlayerTeammateColor(player).R;
                    smokeProjectile.SmokeColor.Y = GetPlayerTeammateColor(player).G;
                    smokeProjectile.SmokeColor.Z = GetPlayerTeammateColor(player).B;
                }
            });
        }
        catch (Exception e)
        {
            Log($"[OnEntitySpawnedHandler FATAL] An error occurred: {e.Message}");
        }
    }

    public HookResult EventPlayerDeathPreHandler(EventPlayerDeath @event, GameEventInfo info)
    {
        try
        {
            // We do not broadcast the suicide of the coach
            if (!matchStarted) return HookResult.Continue;

            if (@event.Attacker == @event.Userid)
            {
                if (atTeam1.coach.Contains(@event.Attacker!) || atTeam2.coach.Contains(@event.Attacker!))
                {
                    info.DontBroadcast = true;
                }
            }
            return HookResult.Continue;
        }
        catch (Exception e)
        {
            Log($"[EventPlayerDeathPreHandler FATAL] An error occurred: {e.Message}");
            return HookResult.Continue;
        }
    }

    public HookResult EventSmokegrenadeDetonateHandler(EventSmokegrenadeDetonate @event, GameEventInfo info)
    {
        if (!isPractice || isDryRun) return HookResult.Continue;
        CCSPlayerController? player = @event.Userid;
        if (!IsPlayerValid(player)) return HookResult.Continue;
        if(lastGrenadeThrownTime.TryGetValue(@event.Entityid, out var thrownTime)) 
        {
            PrintToPlayerChat(player!, Localizer["at.pracc.smoke", player!.PlayerName, $"{(DateTime.Now - thrownTime).TotalSeconds:0.00}"]);
            lastGrenadeThrownTime.Remove(@event.Entityid);
        }
        return HookResult.Continue;
    }

    public HookResult EventFlashbangDetonateHandler(EventFlashbangDetonate @event, GameEventInfo info)
    {
        if (!isPractice || isDryRun) return HookResult.Continue;
        CCSPlayerController? player = @event.Userid;
        if (!IsPlayerValid(player)) return HookResult.Continue;
        if(lastGrenadeThrownTime.TryGetValue(@event.Entityid, out var thrownTime)) 
        {
            PrintToPlayerChat(player!, Localizer["at.pracc.flash", player!.PlayerName, $"{(DateTime.Now - thrownTime).TotalSeconds:0.00}"]);
            lastGrenadeThrownTime.Remove(@event.Entityid);
        }
        return HookResult.Continue;
    }

    public HookResult EventHegrenadeDetonateHandler(EventHegrenadeDetonate @event, GameEventInfo info)
    {
        if (!isPractice || isDryRun) return HookResult.Continue;
        CCSPlayerController? player = @event.Userid;
        if (!IsPlayerValid(player)) return HookResult.Continue;
        if(lastGrenadeThrownTime.TryGetValue(@event.Entityid, out var thrownTime)) 
        {
            PrintToPlayerChat(player!, Localizer["at.pracc.grenade", player!.PlayerName, $"{(DateTime.Now - thrownTime).TotalSeconds:0.00}"]);
            lastGrenadeThrownTime.Remove(@event.Entityid);
        }
        return HookResult.Continue;
    }

    public HookResult EventMolotovDetonateHandler(EventMolotovDetonate @event, GameEventInfo info)
    {
        if (!isPractice || isDryRun) return HookResult.Continue;
        CCSPlayerController? player = @event.Userid;
        if (!IsPlayerValid(player)) return HookResult.Continue;
        if(lastGrenadeThrownTime.TryGetValue(@event.Get<int>("entityid"), out var thrownTime)) 
        {
            PrintToPlayerChat(player!, Localizer["at.pracc.molotov", player!.PlayerName, $"{(DateTime.Now - thrownTime).TotalSeconds:0.00}"]);
        }
        return HookResult.Continue;
    }

    public HookResult EventDecoyDetonateHandler(EventDecoyStarted @event, GameEventInfo info)
    {
        if (!isPractice || isDryRun) return HookResult.Continue;
        CCSPlayerController? player = @event.Userid;
        if (!IsPlayerValid(player)) return HookResult.Continue;
        if(lastGrenadeThrownTime.TryGetValue(@event.Entityid, out var thrownTime)) 
        {
            PrintToPlayerChat(player!, Localizer["at.pracc.decoy", player!.PlayerName, $"{(DateTime.Now - thrownTime).TotalSeconds:0.00}"]);
            lastGrenadeThrownTime.Remove(@event.Entityid);
        }
        return HookResult.Continue;
    }
}
