using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using System.Text.RegularExpressions;

namespace AutoTournamentCS2
{
    public partial class AutoTournamentCS2
    {
        [ConsoleCommand("css_whitelist", "Toggles Whitelisting of players")]
        [ConsoleCommand("css_wl", "Toggles Whitelisting of players")]
        public void OnWLCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (IsPlayerAdmin(player, "css_whitelist", "@css/config"))
            {
                isWhitelistRequired = !isWhitelistRequired;
                string WLStatus = isWhitelistRequired ? Localizer["at.cc.enabled"] : Localizer["at.cc.disabled"];
                if (player == null)
                {
                    //ReplyToUserCommand(player, $"Whitelist is now {WLStatus}!");
                    ReplyToUserCommand(player, Localizer["at.cc.wl", WLStatus]);
                }
                else
                {
                    //player.PrintToChat($"{chatPrefix} Whitelist is now {ChatColors.Green}{WLStatus}{ChatColors.Default}!");
                    PrintToPlayerChat(player, Localizer["at.cc.wl", WLStatus]);
                }
            }
            else
            {
                SendPlayerNotAdminMessage(player);
            }
        }

        [ConsoleCommand("css_save_nades_as_global", "Toggles Global Lineups for players")]
        [ConsoleCommand("css_globalnades", "Toggles Global Lineups for players")]
        public void OnSaveNadesAsGlobalCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (IsPlayerAdmin(player, "css_save_nades_as_global", "@css/config"))
            {
                isSaveNadesAsGlobalEnabled = !isSaveNadesAsGlobalEnabled;
                string GlobalNadesStatus = isSaveNadesAsGlobalEnabled ? Localizer["at.cc.enabled"] : Localizer["at.cc.disabled"];
                if (player == null)
                {
                    //ReplyToUserCommand(player, $"Saving/Loading Lineups Globally is now {GlobalNadesStatus}!");
                    ReplyToUserCommand(player, Localizer["at.cc.globalnades", GlobalNadesStatus]);
                }
                else
                {
                    //player.PrintToChat($"{chatPrefix} Saving/Loading Lineups Globally is now {ChatColors.Green}{GlobalNadesStatus}{ChatColors.Default}!");
                    PrintToPlayerChat(player, Localizer["at.cc.globalnades", GlobalNadesStatus]);

                }
            }
            else
            {
                SendPlayerNotAdminMessage(player);
            }
        }

        [ConsoleCommand("css_ready", "Marks the player ready")]
        public void OnPlayerReady(CCSPlayerController? player, CommandInfo? command)
        {
            if (player == null) return;
            Log($"[!ready command] Sent by: {player.UserId} readyAvailable: {readyAvailable} matchStarted: {matchStarted}");
            if (readyAvailable && !matchStarted)
            {
                if (player.UserId.HasValue)
                {
                    EnsurePlayerTrackedForReady(player);

                    // Auto-ready opt-out: player manually readied, so clear opt-out and any pending timers.
                    autoReadyOptOutUserIds.Remove(player.UserId.Value);
                    if (autoReadyPendingReadyTimers.TryGetValue(player.UserId.Value, out var pending))
                    {
                        pending?.Kill();
                        autoReadyPendingReadyTimers.Remove(player.UserId.Value);
                    }

                    if (!playerReadyStatus.ContainsKey(player.UserId.Value))
                    {
                        playerReadyStatus[player.UserId.Value] = false;
                    }
                    if (playerReadyStatus[player.UserId.Value])
                    {
                        // player.PrintToChat($"{chatPrefix} You are already ready!");
                        PrintToPlayerChat(player, Localizer["at.ready.markedready"]);
                        ShowPlayerNotification(player, "✅ YOU ARE READY", "#00ff00", 18);
                    }
                    else
                    {
                        playerReadyStatus[player.UserId.Value] = true;
                        // player.PrintToChat($"{chatPrefix} {Localizer["at.youareready"]}");
                        PrintToPlayerChat(player, Localizer["at.ready.markedready"]);
                        ShowPlayerNotification(player, "✅ YOU ARE READY", "#00ff00", 18);

                        // Send player_ready event
                        SendPlayerReadyEvent(player, true);
                    }
                    CheckLiveRequired();
                    HandleClanTags();
                }
            }
        }

        [ConsoleCommand("css_unready", "Marks the player unready")]
        [ConsoleCommand("css_notready", "Marks the player unready")]
        public void OnPlayerUnReady(CCSPlayerController? player, CommandInfo? command)
        {
            if (player == null) return;
            Log($"[!unready command] {player.UserId}");
            if (readyAvailable && !matchStarted)
            {
                if (player.UserId.HasValue)
                {
                    // Auto-ready opt-out: if auto-ready is enabled, remember that this player opted out,
                    // and cancel any pending auto-ready timer for them.
                    if (autoReadyEnabled.Value)
                    {
                        autoReadyOptOutUserIds.Add(player.UserId.Value);
                        if (autoReadyPendingReadyTimers.TryGetValue(player.UserId.Value, out var pending))
                        {
                            pending?.Kill();
                            autoReadyPendingReadyTimers.Remove(player.UserId.Value);
                        }
                    }

                    if (!playerReadyStatus.ContainsKey(player.UserId.Value))
                    {
                        playerReadyStatus[player.UserId.Value] = false;
                    }
                    if (!playerReadyStatus[player.UserId.Value])
                    {
                        PrintToPlayerChat(player, Localizer["at.ready.markedunready"]);
                        ShowPlayerNotification(player, "❌ NOT READY<br>Type .ready to ready up", "#ff0000", 18);
                    }
                    else
                    {
                        playerReadyStatus[player.UserId.Value] = false;
                        PrintToPlayerChat(player, Localizer["at.ready.markedunready"]);
                        ShowPlayerNotification(player, "❌ NOT READY<br>Type .ready to ready up", "#ff0000", 18);

                        // Send player_unready event
                        SendPlayerReadyEvent(player, false);
                    }
                    HandleClanTags();
                    CheckLiveRequired();
                }
            }
        }

        [ConsoleCommand("css_stay", "Stays after knife round")]
        public void OnTeamStay(CCSPlayerController? player, CommandInfo? command)
        {
            if (player == null || !isSideSelectionPhase) return;

            Log($"[!stay command] {player.UserId}, TeamNum: {player.TeamNum}, knifeWinner: {knifeWinner}, isSideSelectionPhase: {isSideSelectionPhase}");
            if (player.TeamNum == knifeWinner)
            {
                // Cancel side selection timer
                if (sideSelectionTimer != null)
                {
                    sideSelectionTimer.Kill();
                    sideSelectionTimer = null;
                }
                
                // Cancel reminder timer
                if (sideSelectionReminderTimer != null)
                {
                    sideSelectionReminderTimer.Kill();
                    sideSelectionReminderTimer = null;
                }
                
                PrintToAllChat(Localizer["at.knife.decidedtostay", knifeWinnerName]);
                // Server.PrintToChatAll($"{chatPrefix} {ChatColors.Green}{knifeWinnerName}{ChatColors.Default} has decided to stay!");
                ShowNotification($"🔪 {knifeWinnerName} STAYS", "#00ff00", 20);
                StartLive();
            }
        }

        [ConsoleCommand("css_switch", "Switch after knife round")]
        [ConsoleCommand("css_swap", "Switch after knife round")]
        public void OnTeamSwitch(CCSPlayerController? player, CommandInfo? command)
        {
            if (player == null || !isSideSelectionPhase) return;

            Log($"[!switch command] {player.UserId}, TeamNum: {player.TeamNum}, knifeWinner: {knifeWinner}, isSideSelectionPhase: {isSideSelectionPhase}");

            if (player.TeamNum == knifeWinner)
            {
                // Cancel side selection timer
                if (sideSelectionTimer != null)
                {
                    sideSelectionTimer.Kill();
                    sideSelectionTimer = null;
                }
                
                // Cancel reminder timer
                if (sideSelectionReminderTimer != null)
                {
                    sideSelectionReminderTimer.Kill();
                    sideSelectionReminderTimer = null;
                }
                
                Server.ExecuteCommand("mp_swapteams;");
                SwapSidesInTeamData(true);
                PrintToAllChat(Localizer["at.knife.decidedtoswitch", knifeWinnerName]);
                // Server.PrintToChatAll($"{chatPrefix} {ChatColors.Green}{knifeWinnerName}{ChatColors.Default} has decided to switch!");
                ShowNotification($"🔪 {knifeWinnerName} SWITCHES", "#00ff00", 20);
                StartLive();
            }
        }

        [ConsoleCommand("css_t", "Switches team to Terrorist")]
        public void OnTCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (player == null || player.UserId == null) return;
            if (isSideSelectionPhase && player.TeamNum == knifeWinner)
            {
                if (player.Team == CsTeam.Terrorist)
                {
                    OnTeamStay(player, command);
                }
                else
                {
                    OnTeamSwitch(player, command);
                }
            }

            if (!isPractice) return;
            SideSwitchCommand(player, CsTeam.Terrorist);
        }

        [ConsoleCommand("css_ct", "Switches team to Counter-Terrorist")]
        public void OnCTCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (player == null || player.UserId == null) return;
            if (isSideSelectionPhase && player.TeamNum == knifeWinner)
            {
                if (player.Team == CsTeam.CounterTerrorist)
                {
                    OnTeamStay(player, command);
                }
                else
                {
                    OnTeamSwitch(player, command);
                }
                return;
            }

            if (!isPractice) return;
            SideSwitchCommand(player, CsTeam.CounterTerrorist);
        }

        [ConsoleCommand("css_tech", "Pause the match")]
        public void OnTechCommand(CCSPlayerController? player, CommandInfo? command)
        {
            PauseMatch(player, command);
        }

        [ConsoleCommand("css_pause", "Pause the match")]
        public void OnPauseCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (isPauseCommandForTactical)
            {
                OnTacCommand(player, command);
            }
            else
            {
                PauseMatch(player, command);
            }
        }

        [ConsoleCommand("css_fp", "Pause the match an admin")]
        [ConsoleCommand("css_forcepause", "Pause the match as an admin")]
        [ConsoleCommand("sm_pause", "Pause the match as an admin")]
        public void OnForcePauseCommand(CCSPlayerController? player, CommandInfo? command)
        {
            ForcePauseMatch(player, command);
        }

        [ConsoleCommand("css_fup", "Unpause the match an admin")]
        [ConsoleCommand("css_forceunpause", "Unpause the match as an admin")]
        [ConsoleCommand("sm_unpause", "Unpause the match as an admin")]
        public void OnForceUnpauseCommand(CCSPlayerController? player, CommandInfo? command)
        {
            ForceUnpauseMatch(player, command);
        }

        [ConsoleCommand("css_unpause", "Unpause the match")]
        public void OnUnpauseCommand(CCSPlayerController? player, CommandInfo? command)
        {
            // If a tactical timeout is active (native CS2 timeouts), treat .unpause as an
            // immediate admin-style unpause so players are not stuck waiting for the timer.
            if (IsTacticalTimeoutActive())
            {
                Log($"[OnUnpauseCommand] Tactical timeout active - immediate unpause requested by {(player != null ? player.PlayerName : "Console")}");
                PrintToAllChat(Localizer["at.pause.adminunpausedthematch"]);
                UnpauseMatch();
                return;
            }

            if (isMatchLive && isPaused)
            {
                var pauseTeamName = unpauseData["pauseTeam"];
                if ((string)pauseTeamName == "Admin" && player != null)
                {
                    PrintToPlayerChat(player, Localizer["at.pause.onlyadmincanunpause"]);
                    return;
                }

                string unpauseTeamName = "Admin";
                string remainingUnpauseTeam = "Admin";
                if (player?.TeamNum == 2)
                {
                    unpauseTeamName = reverseTeamSides["TERRORIST"].teamName;
                    remainingUnpauseTeam = reverseTeamSides["CT"].teamName;
                    if (!(bool)unpauseData["t"])
                    {
                        unpauseData["t"] = true;
                    }

                }
                else if (player?.TeamNum == 3)
                {
                    unpauseTeamName = reverseTeamSides["CT"].teamName;
                    remainingUnpauseTeam = reverseTeamSides["TERRORIST"].teamName;
                    if (!(bool)unpauseData["ct"])
                    {
                        unpauseData["ct"] = true;
                    }
                }
                else
                {
                    return;
                }

                int teamsReady = ((bool)unpauseData["t"] ? 1 : 0) + ((bool)unpauseData["ct"] ? 1 : 0);

                // Check if both teams unpause is required
                bool requireBothTeams = bothTeamsUnpauseRequired.Value;

                Log($"[OnUnpauseCommand] Unpause requested by team='{unpauseTeamName}', remainingTeam='{remainingUnpauseTeam}', requireBothTeams={requireBothTeams}, tReady={unpauseData["t"]}, ctReady={unpauseData["ct"]}, teamsReady={teamsReady}");

                if ((bool)unpauseData["t"] && (bool)unpauseData["ct"])
                {
                    PrintToAllChat(Localizer["at.pause.teamsunpausedthematch"]);
                    ShowNotification("▶️ RESUMING ▶️", "#00ff00", 22);
                    UnpauseMatch();
                }
                else if (unpauseTeamName == "Admin")
                {
                    PrintToAllChat(Localizer["at.pause.adminunpausedthematch"]);
                    ShowNotification("▶️ RESUMING ▶️", "#00ff00", 22);
                    UnpauseMatch();
                }
                else if (!requireBothTeams)
                {
                    // If both teams unpause not required, unpause immediately
                    PrintToAllChat(Localizer["at.pause.teamunpausedthematch", unpauseTeamName]);
                    ShowNotification("▶️ RESUMING ▶️", "#00ff00", 22);
                    UnpauseMatch();
                }
                else
                {
                    PrintToAllChat(Localizer["at.pause.teamwantstounpause", unpauseTeamName, remainingUnpauseTeam]);
                    // Server.PrintToChatAll($"{chatPrefix} {ChatColors.Green}{unpauseTeamName}{ChatColors.Default} wants to unpause the match. {ChatColors.Green}{remainingUnpauseTeam}{ChatColors.Default}, please write !unpause to confirm.");

                    // Show team-specific notification to the team that needs to confirm
                    ShowTeamNotification(remainingUnpauseTeam, $"⏸️ {unpauseTeamName} WANTS TO UNPAUSE<br>Type .unpause to confirm", "#ffaa00", 18);

                    // Send unpause_requested event
                    Log($"[OnUnpauseCommand] Sending unpause_requested event - team {unpauseTeamName}");

                    string requestingTeam = player?.TeamNum == 2 ?
                        (reverseTeamSides["TERRORIST"] == atTeam1 ? "team1" : "team2") :
                        (reverseTeamSides["CT"] == atTeam1 ? "team1" : "team2");

                    var unpauseRequestedEvent = new AutoTournamentCS2UnpauseRequestedEvent
                    {
                        MatchId = liveMatchId,
                        MapNumber = matchConfig.CurrentMapNumber,
                        Team = requestingTeam,
                        TeamsReady = teamsReady,
                        TeamsNeeded = 2
                    };

                    Task.Run(async () =>
                    {
                        await SendEventAsync(unpauseRequestedEvent);
                    });
                }
            }
        }

        [ConsoleCommand("css_tac", "Starts a tactical timeout for the requested team")]
        public void OnTacCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (player == null) return;

            if (matchStarted && isMatchLive)
            {
                Log($"[.tac command sent via chat] Sent by: {player.UserId}, connectedPlayers: {connectedPlayers}");
                if (isPaused)
                {
                    // ReplyToUserCommand(player, "Match is already paused, cannot start a tactical timeout!");
                    ReplyToUserCommand(player, Localizer["at.cc.matchpaused"]);
                    return;
                }
                var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First().GameRules!;

                // Map the player to a logical MatchZy Enhanced team for pause tracking
                Team? pausingTeam = null;
                string pauseTeamName = "Unknown";
                if (player.TeamNum == 2 && reverseTeamSides.ContainsKey("TERRORIST"))
                {
                    pausingTeam = reverseTeamSides["TERRORIST"];
                    pauseTeamName = pausingTeam.teamName;
                }
                else if (player.TeamNum == 3 && reverseTeamSides.ContainsKey("CT"))
                {
                    pausingTeam = reverseTeamSides["CT"];
                    pauseTeamName = pausingTeam.teamName;
                }

                // Enforce MatchZy Enhanced per-team pause limit for tactical timeouts as well, if configured
                if (pausingTeam != null && maxPausesPerTeam.Value > 0)
                {
                    if (!pausesUsed.ContainsKey(pausingTeam))
                    {
                        pausesUsed[pausingTeam] = 0;
                    }

                    if (pausesUsed[pausingTeam] >= maxPausesPerTeam.Value)
                    {
                        Log($"[.tac] Pause limit reached for team '{pauseTeamName}' (used={pausesUsed[pausingTeam]}, max={maxPausesPerTeam.Value})");
                        PrintToPlayerChat(player, Localizer["at.pause.nopausesleft", pauseTeamName, maxPausesPerTeam.Value]);
                        return;
                    }
                }

                if (player.TeamNum == 2)
                {
                    if (gameRules.TerroristTimeOuts > 0)
                    {
                        Server.ExecuteCommand("timeout_terrorist_start");

                        if (pausingTeam != null && maxPausesPerTeam.Value > 0)
                        {
                            pausesUsed[pausingTeam]++;
                            int remaining = maxPausesPerTeam.Value - pausesUsed[pausingTeam];
                            Log($"[.tac] Tactical timeout started for '{pauseTeamName}'. pausesUsed={pausesUsed[pausingTeam]}, remaining={remaining}");
                        }
                    }
                    else
                    {
                        // ReplyToUserCommand(player, "You do not have any tactical timeouts left!");
                        ReplyToUserCommand(player, Localizer["at.cc.nomorepauses"]);
                    }
                }
                else if (player.TeamNum == 3)
                {
                    if (gameRules.CTTimeOuts > 0)
                    {
                        Server.ExecuteCommand("timeout_ct_start");

                        if (pausingTeam != null && maxPausesPerTeam.Value > 0)
                        {
                            pausesUsed[pausingTeam]++;
                            int remaining = maxPausesPerTeam.Value - pausesUsed[pausingTeam];
                            Log($"[.tac] Tactical timeout started for '{pauseTeamName}'. pausesUsed={pausesUsed[pausingTeam]}, remaining={remaining}");
                        }
                    }
                    else
                    {
                        // ReplyToUserCommand(player, "You do not have any tactical timeouts left!");
                        ReplyToUserCommand(player, Localizer["at.cc.nomorepauses"]);
                    }
                }
            }
        }

        [ConsoleCommand("css_roundknife", "Toggles knife round for the match")]
        [ConsoleCommand("css_rk", "Toggles knife round for the match")]
        public void OnKnifeCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (IsPlayerAdmin(player, "css_roundknife", "@css/config"))
            {
                isKnifeRequired = !isKnifeRequired;
                string knifeStatus = isKnifeRequired ? Localizer["at.cc.enabled"] : Localizer["at.cc.disabled"];
                if (player == null)
                {
                    // ReplyToUserCommand(player, $"Knife round is now {knifeStatus}!");
                    ReplyToUserCommand(player, Localizer["at.cc.roundknife", knifeStatus]);
                }
                else
                {
                    // player.PrintToChat($"{chatPrefix} Knife round is now {ChatColors.Green}{knifeStatus}{ChatColors.Default}!");
                    PrintToPlayerChat(player, Localizer["at.cc.roundknife", knifeStatus]);
                }
            }
            else
            {
                SendPlayerNotAdminMessage(player);
            }
        }

        [ConsoleCommand("css_readyrequired", "Sets number of ready players required to start the match")]
        public void OnReadyRequiredCommand(CCSPlayerController? player, CommandInfo command)
        {
            if (IsPlayerAdmin(player, "css_readyrequired", "@css/config"))
            {
                if (command.ArgCount >= 2)
                {
                    string commandArg = command.ArgByIndex(1);
                    HandleReadyRequiredCommand(player, commandArg);
                }
                else
                {
                    string minimumReadyRequiredFormatted = (player == null) ? $"{minimumReadyRequired}" : $"{ChatColors.Green}{minimumReadyRequired}{ChatColors.Default}";
                    // ReplyToUserCommand(player, $"Current Ready Required: {minimumReadyRequiredFormatted}. Usage: !readyrequired <number_of_ready_players_required>");
                    ReplyToUserCommand(player, Localizer["at.cc.minreadyrequired", minimumReadyRequiredFormatted]);
                }
            }
            else
            {
                SendPlayerNotAdminMessage(player);
            }
        }

        [ConsoleCommand("css_settings", "Shows the current match configuration/settings")]
        public void OnMatchSettingsCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (player == null) return;

            if (IsPlayerAdmin(player, "css_settings", "@css/config"))
            {
                string knifeStatus = isKnifeRequired ? Localizer["at.cc.enabled"] : Localizer["at.cc.disabled"];
                string playoutStatus = isPlayOutEnabled ? Localizer["at.cc.enabled"] : Localizer["at.cc.disabled"];
                // player.PrintToChat($"{chatPrefix} Current Settings:");
                PrintToPlayerChat(player, Localizer["at.cc.currentsettings"]);
                // player.PrintToChat($"{chatPrefix} Knife: {ChatColors.Green}{knifeStatus}{ChatColors.Default}");
                PrintToPlayerChat(player, Localizer["at.cc.knifestatus", knifeStatus]);
                if (isMatchSetup)
                {
                    // player.PrintToChat($"{chatPrefix} Minimum Ready Players Required (Per Team): {ChatColors.Green}{matchConfig.MinPlayersToReady}{ChatColors.Default}");
                    PrintToPlayerChat(player, Localizer["at.cc.minreadyplayersperteam", matchConfig.MinPlayersToReady]);
                    // player.PrintToChat($"{chatPrefix} Minimum Ready Spectators Required: {ChatColors.Green}{matchConfig.MinSpectatorsToReady}{ChatColors.Default}");
                    PrintToPlayerChat(player, Localizer["at.cc.minreadyspecs", matchConfig.MinSpectatorsToReady]);
                }
                else
                {
                    // player.PrintToChat($"{chatPrefix} Minimum Ready Required: {ChatColors.Green}{minimumReadyRequired}{ChatColors.Default}");
                    PrintToPlayerChat(player, Localizer["at.cc.minreadyplayers", minimumReadyRequired]);
                }
                // player.PrintToChat($"{chatPrefix} Playout: {ChatColors.Green}{playoutStatus}{ChatColors.Default}");
                PrintToPlayerChat(player, Localizer["at.cc.playoutstatus", playoutStatus]);
            }
            else
            {
                SendPlayerNotAdminMessage(player);
            }
        }

        [ConsoleCommand("css_endmatch", "Ends and resets the current match")]
        [ConsoleCommand("get5_endmatch", "Ends and resets the current match")]
        [ConsoleCommand("css_forceend", "Ends and resets the current match")]
        public void OnEndMatchCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (IsPlayerAdmin(player, "css_endmatch", "@css/config"))
            {
                if (!isPractice)
                {
                    // Server.PrintToChatAll($"{chatPrefix} An admin force-ended the match.");
                    PrintToAllChat(Localizer["at.cc.endmatch"]);
                    ReplyClearedQueuedMatch(player, ClearQueuedMatch("css_endmatch"));
                    ResetMatch();
                }
                else
                {
                    // ReplyToUserCommand(player, "Practice mode is active, cannot end the match.");
                    ReplyToUserCommand(player, Localizer["at.cc.endmatchispracc"]);
                }
            }
            else
            {
                SendPlayerNotAdminMessage(player);
            }
        }

        [ConsoleCommand("css_restart", "Restarts the match")]
        [ConsoleCommand("css_rr", "Restarts the match")]
        public void OnRestartMatchCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (IsPlayerAdmin(player, "css_restart", "@css/config"))
            {
                if (!isPractice)
                {
                    ReplyClearedQueuedMatch(player, ClearQueuedMatch("css_restart"));
                    ResetMatch();
                }
                else
                {
                    // ReplyToUserCommand(player, "Practice mode is active, cannot restart the match.");
                    ReplyToUserCommand(player, Localizer["at.cc.rrispracc"]);
                }
            }
            else
            {
                SendPlayerNotAdminMessage(player);
            }
        }

        [ConsoleCommand("css_map", "Changes the map using changelevel")]
        public void OnChangeMapCommand(CCSPlayerController? player, CommandInfo command)
        {
            var mapName = command.ArgByIndex(1);
            HandleMapChangeCommand(player, mapName);
        }

        [ConsoleCommand("css_rmap", "Reloads the current map")]
        private void OnMapReloadCommand(CCSPlayerController? player, CommandInfo? command)
        {

            if (!IsPlayerAdmin(player))
            {
                SendPlayerNotAdminMessage(player);
                return;
            }
            // On a workshop map Server.MapName is the map's name, not its id; ExecuteMapChange
            // reuses the workshop id that loaded it, or falls back to ds_workshop_changelevel.
            if (!ExecuteMapChange(Server.MapName, "MapReload"))
            {
                ReplyToUserCommand(player, Localizer["at.cc.invalidmap"]);
            }
        }

        [ConsoleCommand("css_start", "Force starts the match")]
        [ConsoleCommand("css_force", "Force starts the match")]
        [ConsoleCommand("css_forcestart", "Force starts the match")]
        public void OnStartCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (IsPlayerAdmin(player, "css_start", "@css/config"))
            {
                if (isPractice)
                {
                    // ReplyToUserCommand(player, "Cannot start a match while in practice mode. Please use .exitprac command to exit practice mode first!");
                    ReplyToUserCommand(player, Localizer["at.cc.startisprac"]);
                    return;
                }
                if (matchStarted)
                {
                    //ReplyToUserCommand(player, "Start command cannot be used if match is already started! If you want to unpause, please use .unpause");
                    ReplyToUserCommand(player, Localizer["at.cc.startmatchstarted"]);
                }
                else
                {
                    //Server.PrintToChatAll($"{chatPrefix} {ChatColors.Green}Admin{ChatColors.Default} has started the game!");
                    PrintToAllChat(Localizer["at.cc.gamestarted"]);
                    // Admin override: allow transitioning even if only ready-simulation bots are present.
                    HandleMatchStart(allowAutoReadySimulationWithoutHumans: true);
                }
            }
            else
            {
                SendPlayerNotAdminMessage(player);
            }
        }

        [ConsoleCommand("css_asay", "Say as an admin")]
        public void OnAdminSay(CCSPlayerController? player, CommandInfo? command)
        {
            if (command == null) return;
            if (player == null)
            {
                Server.PrintToChatAll($"{adminChatPrefix} {command.ArgString}");
                return;
            }
            if (!IsPlayerAdmin(player, "css_asay", "@css/chat"))
            {
                SendPlayerNotAdminMessage(player);
                return;
            }
            string message = "";
            for (int i = 1; i < command.ArgCount; i++)
            {
                message += command.ArgByIndex(i) + " ";
            }
            Server.PrintToChatAll($"{adminChatPrefix} {message}");
        }

        [ConsoleCommand("reload_admins", "Reload admins of MatchZy Enhanced")]
        public void OnReloadAdmins(CCSPlayerController? player, CommandInfo? command)
        {
            if (IsPlayerAdmin(player, "reload_admins", "@css/config"))
            {
                LoadAdmins();
                UpdatePlayersMap();
            }
            else
            {
                SendPlayerNotAdminMessage(player);
            }
        }

        [ConsoleCommand("at_reload_config", "Re-executes AutoTournamentCS2/config.cfg to reload MatchZy Enhanced plugin configuration")]
        public void OnReloadConfig(CCSPlayerController? player, CommandInfo? command)
        {
            if (!IsPlayerAdmin(player, "at_reload_config", "@css/config"))
            {
                SendPlayerNotAdminMessage(player);
                return;
            }

            if (isMatchLive)
            {
                ReplyToUserCommand(player, "Cannot reload MatchZy Enhanced config while a match is live. Please wait until postgame or warmup.");
                return;
            }

            Log("[ReloadConfig] Executing AutoTournamentCS2/config.cfg from at_reload_config command.");
            Server.ExecuteCommand("execifexists AutoTournamentCS2/config.cfg");

            ReplyToUserCommand(player, "MatchZy Enhanced configuration reloaded from AutoTournamentCS2/config.cfg.");
        }

        [ConsoleCommand("css_match", "Starts match mode")]
        public void OnMatchCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (!IsPlayerAdmin(player, "css_match", "@css/map", "@custom/prac"))
            {
                SendPlayerNotAdminMessage(player);
                return;
            }

            if (matchStarted)
            {
                // ReplyToUserCommand(player, "MatchZy Enhanced is already in match mode!");
                ReplyToUserCommand(player, Localizer["at.cc.match"]);
                return;
            }

            StartMatchMode();
        }

        [ConsoleCommand("css_exitprac", "Starts match mode")]
        public void OnExitPracCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (!IsPlayerAdmin(player, "css_exitprac", "@css/map", "@custom/prac"))
            {
                SendPlayerNotAdminMessage(player);
                return;
            }

            if (matchStarted)
            {
                //ReplyToUserCommand(player, "MatchZy Enhanced is already in match mode!");
                ReplyToUserCommand(player, Localizer["at.cc.exitprac"]);
                return;
            }

            StartMatchMode();
        }

        [ConsoleCommand("css_rcon", "Triggers provided command on the server")]
        public void OnRconCommand(CCSPlayerController? player, CommandInfo command)
        {
            if (!IsPlayerAdmin(player, "css_rcon", "@css/rcon"))
            {
                SendPlayerNotAdminMessage(player);
                return;
            }
            Server.ExecuteCommand(command.ArgString);
            // ReplyToUserCommand(player, "Command sent successfully!");
            ReplyToUserCommand(player, Localizer["at.cc.rcon"]);

        }

        [ConsoleCommand("css_help", "Triggers provided command on the server")]
        public void OnHelpCommand(CCSPlayerController? player, CommandInfo? command)
        {
            SendAvailableCommandsMessage(player);
        }

        [ConsoleCommand("css_playout", "Toggles playout (Playing of max rounds)")]
        public void OnPlayoutCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (IsPlayerAdmin(player, "css_playout", "@css/config"))
            {
                isPlayOutEnabled = !isPlayOutEnabled;
                string playoutStatus = isPlayOutEnabled ? Localizer["at.cc.enabled"] : Localizer["at.cc.disabled"];
                if (player == null)
                {
                    // ReplyToUserCommand(player, $"Playout is now {playoutStatus}!");
                    ReplyToUserCommand(player, Localizer["at.cc.playout", playoutStatus]);
                }
                else
                {
                    // player.PrintToChat($"{chatPrefix} Playout is now {ChatColors.Green}{playoutStatus}{ChatColors.Default}!");
                    PrintToPlayerChat(player, Localizer["at.cc.playout", playoutStatus]);
                }

                HandlePlayoutConfig();

            }
            else
            {
                SendPlayerNotAdminMessage(player);
            }
        }

        [ConsoleCommand("version", "Returns server version")]
        public void OnVersionCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (command == null) return;
            string steamInfFilePath = Path.Combine(Server.GameDirectory, "csgo", "steam.inf");

            if (!File.Exists(steamInfFilePath))
            {
                command.ReplyToCommand("Unable to locate steam.inf file!");
            }
            var steamInfContent = File.ReadAllText(steamInfFilePath);

            Regex regex = new(@"ServerVersion=(\d+)");
            Match match = regex.Match(steamInfContent);

            // Extract the version number
            string? serverVersion = match.Success ? match.Groups[1].Value : null;

            // Currently returning only server version to show server status as available on Get5
            command.ReplyToCommand((serverVersion != null) ? $"Protocol version {serverVersion} [{serverVersion}/{serverVersion}]" : "Unable to get server version");
        }

        // Overrides noclip console command. Perform the changes on server side.
        public HookResult OnConsoleNoClip(CCSPlayerController? player, CommandInfo? cmd)
        {
            if (player == null || !player.PawnIsAlive || player.Team == CsTeam.Spectator || player.Team == CsTeam.None)
                return HookResult.Stop;
            bool cheatsEnabled = ConVar.Find("sv_cheats")!.GetPrimitiveValue<bool>();
            if (!cheatsEnabled)
            {
                return HookResult.Stop;
            }

            // inspired by cs2-noclip
            if (player.PlayerPawn.Value!.MoveType == MoveType_t.MOVETYPE_NOCLIP)
            {
                player.PlayerPawn.Value.ResetNoclipToWalk();
            }
            else
            {
                player.PlayerPawn.Value.MoveType = MoveType_t.MOVETYPE_NOCLIP;
                player.PlayerPawn.Value.ActualMoveType = MoveType_t.MOVETYPE_OBSERVER;
                Utilities.SetStateChanged(player.PlayerPawn.Value, "CBaseEntity", "m_MoveType");
            }

            return HookResult.Stop;
        }

        [ConsoleCommand("at_version", "Displays the current MatchZy Enhanced version")]
        [ConsoleCommand("css_at_version", "Displays the current MatchZy Enhanced version")]
        [ConsoleCommand("css_version", "Displays the current MatchZy Enhanced version")]
        public void OnAutoTournamentCS2VersionCommand(CCSPlayerController? player, CommandInfo? command)
        {
            string message = $"{chatPrefix} {ChatColors.Green}Auto Tournament{ChatColors.Default} version: {ChatColors.Lime}{ModuleVersion}{ChatColors.Default}";

            if (player == null)
            {
                Server.PrintToConsole($"[Auto Tournament] Version: {ModuleVersion}");
            }
            else
            {
                PrintToPlayerChat(player, message);
            }
        }

        [ConsoleCommand("css_gg", "Vote to end the match early (requires team consensus)")]
        public void OnGGCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (player == null || !player.IsValid || !player.UserId.HasValue) return;

            Log($"[GG] Command received from {player.PlayerName} (UserId={player.UserId}, TeamNum={player.TeamNum}) - ggEnabled={ggEnabled.Value}, isMatchLive={isMatchLive}");
            
            if (!ggEnabled.Value)
            {
                PrintToPlayerChat(player, Localizer["at.gg.disabled"]);
                return;
            }
            
            if (!isMatchLive)
            {
                PrintToPlayerChat(player, Localizer["at.gg.matchnotlive"]);
                return;
            }
            
            if (player.TeamNum != 2 && player.TeamNum != 3)
            {
                PrintToPlayerChat(player, Localizer["at.gg.mustbeonteam"]);
                return;
            }

            // Enforce optional minimum score difference for surrendering via .gg
            if (ggMinScoreDiff.Value > 0)
            {
                (int t1score, int t2score) = GetTeamsScore();

                Team surrenderingTeam = player.TeamNum == 2 ? reverseTeamSides["TERRORIST"] : reverseTeamSides["CT"];
                int playerScore = surrenderingTeam == atTeam1 ? t1score : t2score;
                int opponentScore = surrenderingTeam == atTeam1 ? t2score : t1score;
                int scoreDiff = opponentScore - playerScore;

                Log($"[GG] Score check for {player.PlayerName}: playerScore={playerScore}, opponentScore={opponentScore}, diff={scoreDiff}, ggMinScoreDiff={ggMinScoreDiff.Value}");

                // Only allow .gg if the calling team is currently losing by at least ggMinScoreDiff
                if (scoreDiff < ggMinScoreDiff.Value || scoreDiff <= 0)
                {
                    PrintToPlayerChat(player, $"You can only use .gg when your team is losing by at least {ggMinScoreDiff.Value} rounds.");
                    return;
                }
            }
            
            HashSet<ulong> votes = player.TeamNum == 2 ? ggVotesT : ggVotesCT;
            Team playerTeam = player.TeamNum == 2 ? reverseTeamSides["TERRORIST"] : reverseTeamSides["CT"];
            Team opposingTeam = player.TeamNum == 2 ? reverseTeamSides["CT"] : reverseTeamSides["TERRORIST"];
            string teamName = playerTeam.teamName;
            
            // Check if player already voted
            if (votes.Contains(player.SteamID))
            {
                PrintToPlayerChat(player, Localizer["at.gg.alreadyvoted"]);
                return;
            }
            
            votes.Add(player.SteamID);
            
            // Count players on the team
            int teamPlayerCount = 0;
            foreach (var kvp in playerData)
            {
                var p = kvp.Value;
                if (p != null && p.IsValid && !p.IsBot && !p.IsHLTV && p.TeamNum == player.TeamNum)
                {
                    teamPlayerCount++;
                }
            }
            
            int votesNeeded = (int)Math.Ceiling(teamPlayerCount * ggThreshold.Value);
            int currentVotes = votes.Count;

            Log($"[GG] Vote state for team '{teamName}': currentVotes={currentVotes}, votesNeeded={votesNeeded}, teamPlayerCount={teamPlayerCount}, threshold={ggThreshold.Value}");
            
            PrintToAllChat(Localizer["at.gg.playervoted", player.PlayerName, teamName, currentVotes, votesNeeded]);
            
            // Check if threshold is met
            if (currentVotes >= votesNeeded && votesNeeded > 0)
            {
                PrintToAllChat(Localizer["at.gg.thresholdmet", teamName]);

                Log($"[GG] Threshold met for team '{teamName}'. Forfeiting in favor of '{opposingTeam.teamName}'. Updating scores and ending match.");
                
                // Award win to opposing team by setting scores
                var teamEntities = Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager");
                foreach (var team in teamEntities)
                {
                    if (player.TeamNum == 2) // T forfeited, CT wins
                    {
                        if (team.Teamname == "CT")
                        {
                            team.Score = 16;
                        }
                        else if (team.Teamname == "TERRORIST")
                        {
                            team.Score = 0;
                        }
                    }
                    else // CT forfeited, T wins
                    {
                        if (team.Teamname == "TERRORIST")
                        {
                            team.Score = 16;
                        }
                        else if (team.Teamname == "CT")
                        {
                            team.Score = 0;
                        }
                    }
                }

                // Drive postgame / series-end logic via the normal match-end handler
                HandleMatchEnd();
            }
        }

        [ConsoleCommand("css_te", "Sends a test event to the remote log URL")]
        [ConsoleCommand("css_testevent", "Sends a test event to the remote log URL")]
        public void OnTestEventCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (!IsPlayerAdmin(player, "css_te", "@css/config"))
            {
                SendPlayerNotAdminMessage(player);
                return;
            }

            if (string.IsNullOrEmpty(matchConfig.RemoteLogURL))
            {
                ReplyToUserCommand(player, "Remote log URL is not configured! Set at_remote_log_url first.");
                return;
            }

            // Show the endpoint to the admin
            ReplyToUserCommand(player, $"Sending test event to: {ChatColors.Green}{matchConfig.RemoteLogURL}");

            // Create and send test event
            var testEvent = new AutoTournamentCS2TestEvent
            {
                MatchId = liveMatchId,
                Message = "This is a test event from MatchZy Enhanced",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                TriggeredBy = player?.PlayerName ?? "Console",
                ServerId = matchReportServerId.Value,
                MatchSlug = tournamentMatch.Value
            };

            Task.Run(async () =>
            {
                try
                {
                    await SendEventAsync(testEvent);

                    // Notify admin of success
                    Server.NextFrame(() =>
                    {
                        if (player != null && player.IsValid)
                        {
                            ReplyToUserCommand(player, $"{ChatColors.Green}✓{ChatColors.Default} Test event sent successfully! Check your endpoint logs.");
                        }
                        else
                        {
                            Log("[TestEvent] Test event sent successfully!");
                        }
                    });
                }
                catch (Exception ex)
                {
                    // Notify admin of failure
                    Server.NextFrame(() =>
                    {
                        if (player != null && player.IsValid)
                        {
                            ReplyToUserCommand(player, $"{ChatColors.Red}✗{ChatColors.Default} Failed to send test event: {ex.Message}");
                        }
                        else
                        {
                            Log($"[TestEvent FATAL] Failed to send test event: {ex.Message}");
                        }
                    });
                }
            });
        }

        [ConsoleCommand("at_get_match_stats", "Returns complete match statistics as JSON for a given match ID")]
        public void OnGetMatchStatsCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (command == null || command.ArgCount < 2)
            {
                ReplyToUserCommand(player, "Usage: at_get_match_stats <matchId>");
                return;
            }

            if (!long.TryParse(command.ArgByIndex(1), out long matchId))
            {
                ReplyToUserCommand(player, "Invalid match ID. Must be a number.");
                return;
            }

            var statsJson = database.GetMatchStatsJson(matchId);
            
            if (statsJson == null)
            {
                ReplyToUserCommand(player, $"No stats found for match ID {matchId}");
                Log($"[GetMatchStats] No stats found for match ID {matchId}");
                return;
            }

            // For admin players, send a confirmation
            if (player != null)
            {
                ReplyToUserCommand(player, $"Match stats for ID {matchId} retrieved from database.");
            }

            // Print to console (for API to capture if needed)
            Log($"[GetMatchStats] Match ID {matchId} stats:");
            Log($"[GetMatchStats] {statsJson}");
            
            // Also print to server console for easy viewing
            Server.PrintToConsole($"=== MATCH STATS FOR ID {matchId} ===");
            Server.PrintToConsole(statsJson);
            Server.PrintToConsole($"=== END MATCH STATS ===");
        }

        [ConsoleCommand("at_get_pending_events", "Shows how many events are queued for retry")]
        public void OnGetPendingEventsCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (!IsPlayerAdmin(player, "css_pe", "@css/config"))
            {
                SendPlayerNotAdminMessage(player);
                return;
            }

            var pendingEvents = database.GetPendingEvents(1000);
            int pendingCount = pendingEvents?.Count ?? 0;
            
            ReplyToUserCommand(player, $"Event queue status:");
            ReplyToUserCommand(player, $"  Pending events: {ChatColors.Yellow}{pendingCount}{ChatColors.Default}");
            
            if (pendingCount > 0 && pendingEvents != null)
            {
                var grouped = pendingEvents.GroupBy(e => e.event_type)
                    .Select(g => $"{g.Key}: {g.Count()}")
                    .ToList();
                
                ReplyToUserCommand(player, $"  Breakdown:");
                foreach (var group in grouped.Take(10))
                {
                    ReplyToUserCommand(player, $"    - {group}");
                }
                
                if (grouped.Count > 10)
                {
                    ReplyToUserCommand(player, $"    ... and {grouped.Count - 10} more event types");
                }
            }
            
            Log($"[GetPendingEvents] {pendingCount} events in retry queue");
        }

        [ConsoleCommand("at_clear_event_queue", "Clears all pending/failed events from the retry queue")]
        public void OnClearEventQueueCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (!IsPlayerAdmin(player, "css_pe", "@css/config"))
            {
                SendPlayerNotAdminMessage(player);
                return;
            }

            int cleared = database.ClearEventQueue();
            
            if (cleared > 0)
            {
                ReplyToUserCommand(player, $"Cleared {ChatColors.Green}{cleared}{ChatColors.Default} pending/failed events from queue.");
                Log($"[ClearEventQueue] Admin cleared {cleared} events from queue.");
            }
            else
            {
                ReplyToUserCommand(player, $"No pending/failed events to clear.");
            }
        }
    }
}
