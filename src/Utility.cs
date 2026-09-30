using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Admin;
using System.Text.RegularExpressions;
using System.Text;
using Newtonsoft.Json.Linq;
using System.Drawing;
using System.IO;


namespace AutoTournamentCS2
{
    public partial class AutoTournamentCS2
    {
        public const string warmupCfgPath = "AutoTournamentCS2/warmup.cfg";
        public const string knifeCfgPath = "AutoTournamentCS2/knife.cfg";
        public const string liveCfgPath = "AutoTournamentCS2/live.cfg";
        public const string liveWingmanCfgPath = "AutoTournamentCS2/live_wingman.cfg";
        private static readonly Regex SafeCVarValuePattern = new(@"^(?:[+-]?(?:\d+\.?\d*|\.\d+)|true|false|on|off|yes|no)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private void PrintToAllChat(string message)
        {
            Server.PrintToChatAll($"{chatPrefix} {message}");
        }

        private void CrashBreadcrumb(string step)
        {
            if (!crashDebugBreadcrumbs.Value)
            {
                return;
            }

            try
            {
                string ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                string line = $"[{ts}] {step}";

                // Console log (so it shows in live output)
                Log($"[CrashBreadcrumb] {line}");

                // File log (so we can see the last step even after a segfault)
                string logDir = Path.Combine(Server.GameDirectory, "csgo", "cfg", "AutoTournamentCS2", "logs");
                Directory.CreateDirectory(logDir);

                string logPath = Path.Combine(logDir, "at_breadcrumbs.log");
                File.AppendAllText(logPath, line + Environment.NewLine);
            }
            catch
            {
                // Never let breadcrumb logging crash the plugin.
            }
        }

        /// <summary>
        /// Map-change breadcrumb (at_crash_debug_breadcrumbs). Adds the state that
        /// matters for "crashes on map change with 5+ players / long uptime" reports:
        /// how many controllers the plugin still holds from the previous map, coaches,
        /// pending per-player timers, and process memory / uptime for leak hunting.
        /// Only counts are read; no entity is touched here.
        /// </summary>
        private void MapTransitionBreadcrumb(string step)
        {
            if (!crashDebugBreadcrumbs.Value) return;

            string stats;
            try
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                double uptimeMinutes = (DateTime.Now - process.StartTime).TotalMinutes;
                long workingSetMb = process.WorkingSet64 / (1024 * 1024);
                long managedMb = GC.GetTotalMemory(false) / (1024 * 1024);
                stats = $"trackedPlayers={playerData.Count} readyEntries={playerReadyStatus.Count} " +
                        $"coaches={atTeam1.coach.Count + atTeam2.coach.Count} " +
                        $"autoReadyTimers={autoReadyPendingReadyTimers.Count} practiceTimers={playerTimers.Count} " +
                        $"matchSetup={isMatchSetup} live={isMatchLive} practice={isPractice} sim={isSimulationMode} " +
                        $"uptimeMin={uptimeMinutes:0} workingSetMB={workingSetMb} managedMB={managedMb}";
            }
            catch (Exception e)
            {
                stats = $"stats unavailable: {e.Message}";
            }

            CrashBreadcrumb($"{step} | {stats}");
        }

        private void PrintToPlayerChat(CCSPlayerController player, string message)
        {
            player.PrintToChat($"{chatPrefix} {message}");
        }

        /// <summary>
        /// Displays a center HTML notification to all players
        /// </summary>
        private void PrintToCenterHtmlAll(string message)
        {
            if (!centerHtmlNotifications.Value) return;
            
            var playerEntities = Utilities.GetPlayers().Where(p => p?.IsValid == true && !p.IsBot);
            foreach (var player in playerEntities)
            {
                player.PrintToCenterHtml(message);
            }
        }

        /// <summary>
        /// Displays a center HTML notification to a specific player
        /// </summary>
        private void PrintToCenterHtml(CCSPlayerController player, string message)
        {
            if (!centerHtmlNotifications.Value) return;
            if (player?.IsValid != true || player.IsBot) return;
            
            player.PrintToCenterHtml(message);
        }

        /// <summary>
        /// Displays a center HTML notification to all players on a specific team
        /// </summary>
        private void PrintToCenterHtmlTeam(string teamName, string message)
        {
            if (!centerHtmlNotifications.Value) return;
            
            // Determine which team number (2=T, 3=CT)
            var team = teamName == atTeam1.teamName ? atTeam1 : atTeam2;
            string teamSide = teamSides.ContainsKey(team) ? teamSides[team] : "";
            int teamNum = teamSide == "CT" ? 3 : 2;
            
            var playerEntities = Utilities.GetPlayers().Where(p => p?.IsValid == true && !p.IsBot && p.TeamNum == teamNum);
            foreach (var player in playerEntities)
            {
                player.PrintToCenterHtml(message);
            }
        }

        /// <summary>
        /// Displays a large, styled notification to all players (with duration)
        /// </summary>
        private void ShowNotification(string message, string color = "#00ff00", int size = 20, float? durationSeconds = null)
        {
            if (!centerHtmlNotifications.Value) return;
            
            float actualDuration = durationSeconds ?? notificationDurationGlobal.Value;
            string html = $"<div style='font-size:{size}px; color:{color}; font-weight:bold; text-align:center; margin-top:200px;'>{message}</div>";
            
            // Create unique key for this notification
            string notificationKey = $"global_{message.GetHashCode()}";
            
            // Kill any existing timer for this notification
            if (activeNotificationTimers.ContainsKey(notificationKey) && activeNotificationTimers[notificationKey] != null)
            {
                activeNotificationTimers[notificationKey]?.Kill();
            }
            
            // Send immediately
            PrintToCenterHtmlAll(html);
            
            // Calculate how many times to re-send (every 1 second to keep it visible)
            int repeatCount = (int)Math.Ceiling(actualDuration);
            
            if (repeatCount > 1)
            {
                int[] repeatCounter = { repeatCount - 1 }; // Use array to allow modification in closure
                
                void NotificationTick()
                {
                    if (repeatCounter[0] > 0)
                    {
                        PrintToCenterHtmlAll(html);
                        repeatCounter[0]--;
                        // Schedule next tick
                        activeNotificationTimers[notificationKey] = AddTimer(1.0f, NotificationTick);
                    }
                    else
                    {
                        // Timer finished, clean up
                        if (activeNotificationTimers.ContainsKey(notificationKey))
                        {
                            activeNotificationTimers[notificationKey] = null;
                        }
                    }
                }
                
                activeNotificationTimers[notificationKey] = AddTimer(1.0f, NotificationTick);
            }
        }

        /// <summary>
        /// Displays a styled notification to a specific player (with duration)
        /// </summary>
        private void ShowPlayerNotification(CCSPlayerController player, string message, string color = "#00ff00", int size = 18, float? durationSeconds = null)
        {
            if (!centerHtmlNotifications.Value) return;
            if (player?.IsValid != true || player.IsBot) return;
            
            float actualDuration = durationSeconds ?? notificationDurationPlayer.Value;
            string html = $"<div style='font-size:{size}px; color:{color}; font-weight:bold; text-align:center; margin-top:200px;'>{message}</div>";
            
            // Create unique key for this notification
            string notificationKey = $"player_{player.UserId}_{message.GetHashCode()}";
            
            // Kill any existing timer for this notification
            if (activeNotificationTimers.ContainsKey(notificationKey) && activeNotificationTimers[notificationKey] != null)
            {
                activeNotificationTimers[notificationKey]?.Kill();
            }
            
            // Send immediately
            PrintToCenterHtml(player, html);
            
            // Calculate how many times to re-send (every 1 second to keep it visible)
            int repeatCount = (int)Math.Ceiling(actualDuration);
            
            if (repeatCount > 1)
            {
                int[] repeatCounter = { repeatCount - 1 }; // Use array to allow modification in closure
                CCSPlayerController? playerRef = player; // Capture player reference
                
                void NotificationTick()
                {
                    if (repeatCounter[0] > 0 && playerRef?.IsValid == true)
                    {
                        PrintToCenterHtml(playerRef, html);
                        repeatCounter[0]--;
                        // Schedule next tick
                        activeNotificationTimers[notificationKey] = AddTimer(1.0f, NotificationTick);
                    }
                    else
                    {
                        // Timer finished, clean up
                        if (activeNotificationTimers.ContainsKey(notificationKey))
                        {
                            activeNotificationTimers[notificationKey] = null;
                        }
                    }
                }
                
                activeNotificationTimers[notificationKey] = AddTimer(1.0f, NotificationTick);
            }
        }

        /// <summary>
        /// Displays a styled notification to a specific team (with duration)
        /// </summary>
        private void ShowTeamNotification(string teamName, string message, string color = "#00ff00", int size = 18, float durationSeconds = 6.0f)
        {
            if (!centerHtmlNotifications.Value) return;
            
            string html = $"<div style='font-size:{size}px; color:{color}; font-weight:bold; text-align:center; margin-top:200px;'>{message}</div>";
            
            // Create unique key for this notification
            string notificationKey = $"team_{teamName}_{message.GetHashCode()}";
            
            // Kill any existing timer for this notification
            if (activeNotificationTimers.ContainsKey(notificationKey) && activeNotificationTimers[notificationKey] != null)
            {
                activeNotificationTimers[notificationKey]?.Kill();
            }
            
            // Send immediately
            PrintToCenterHtmlTeam(teamName, html);
            
            // Calculate how many times to re-send (every 1 second to keep it visible)
            int repeatCount = (int)Math.Ceiling(durationSeconds);
            
            if (repeatCount > 1)
            {
                int[] repeatCounter = { repeatCount - 1 }; // Use array to allow modification in closure
                string teamNameRef = teamName; // Capture team name
                
                void NotificationTick()
                {
                    if (repeatCounter[0] > 0)
                    {
                        PrintToCenterHtmlTeam(teamNameRef, html);
                        repeatCounter[0]--;
                        // Schedule next tick
                        activeNotificationTimers[notificationKey] = AddTimer(1.0f, NotificationTick);
                    }
                    else
                    {
                        // Timer finished, clean up
                        if (activeNotificationTimers.ContainsKey(notificationKey))
                        {
                            activeNotificationTimers[notificationKey] = null;
                        }
                    }
                }
                
                activeNotificationTimers[notificationKey] = AddTimer(1.0f, NotificationTick);
            }
        }

        /// <summary>
        /// Starts a countdown timer with center HTML display
        /// </summary>
        private void StartCountdown(int totalSeconds, string messageFormat, string color = "#ffff00", Action? onComplete = null, int marginTop = 200)
        {
            // Kill any existing countdown
            if (countdownDisplayTimer != null)
            {
                countdownDisplayTimer.Kill();
                countdownDisplayTimer = null;
            }

            // If notifications disabled, just run the completion timer
            if (!centerHtmlNotifications.Value)
            {
                if (onComplete != null && totalSeconds > 0)
                {
                    AddTimer(totalSeconds, onComplete);
                }
                return;
            }

            int remainingSeconds = totalSeconds;

            // Create repeating timer that updates every second
            void CountdownTick()
            {
                if (remainingSeconds > 0)
                {
                    string message = string.Format(messageFormat, remainingSeconds);
                    // Position lower on screen to appear below CS2's "MATCH PAUSED" overlay
                    // Use larger font and bright color to be visible despite CS2's overlay
                    string html = $"<div style='font-size:24px; color:{color}; font-weight:bold; text-align:center; margin-top:{marginTop}px; text-shadow: 2px 2px 4px rgba(0,0,0,0.8);'>{message}</div>";
                    PrintToCenterHtmlAll(html);
                    remainingSeconds--;
                }
                else
                {
                    // Countdown finished
                    if (countdownDisplayTimer != null)
                    {
                        countdownDisplayTimer.Kill();
                        countdownDisplayTimer = null;
                    }
                    onComplete?.Invoke();
                }
            }

            // Show initial countdown immediately
            CountdownTick();

            // Then update every second
            if (remainingSeconds > 0)
            {
                countdownDisplayTimer = AddTimer(1.0f, CountdownTick, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);
            }
        }

        private void ReplyToUserCommand(CCSPlayerController? player, string message, bool console = false)
        {
            if (player == null)
            {
                Server.PrintToConsole($"{chatPrefix} {message}");
            }
            else
            {
                if (console)
                {
                    player.PrintToConsole($"{chatPrefix} {message}");
                }
                else
                {
                    player.PrintToChat($"{chatPrefix} {message}");
                }
            }
        }

        private void LoadAdmins()
        {
            string fileName = "AutoTournamentCS2/admins.json";
            string filePath = Path.Join(Server.GameDirectory + "/csgo/cfg", fileName);

            if (File.Exists(filePath))
            {
                try
                {
                    using (StreamReader fileReader = File.OpenText(filePath))
                    {
                        string jsonContent = fileReader.ReadToEnd();
                        if (!string.IsNullOrEmpty(jsonContent))
                        {
                            JsonSerializerOptions options = new()
                            {
                                AllowTrailingCommas = true,
                            };
                            loadedAdmins = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonContent, options) ?? new Dictionary<string, string>();
                        }
                        else
                        {
                            // Handle the case where the JSON content is empty or null
                            loadedAdmins = new Dictionary<string, string>();
                        }
                    }
                    foreach (var kvp in loadedAdmins)
                    {
                        Log($"[ADMIN] Username: {kvp.Key}, Role: {kvp.Value}");
                    }
                }
                catch (Exception e)
                {
                    Log($"[LoadAdmins FATAL] An error occurred: {e.Message}");
                }
            }
            else
            {
                Log("[LoadAdmins] The JSON file does not exist. Creating one with default content");
                Dictionary<string, string> defaultAdmins = new()
                {
                    { "steamid", "" }
                };

                try
                {
                    JsonSerializerOptions options = new()
                    {
                        WriteIndented = true,
                    };
                    string defaultJson = JsonSerializer.Serialize(defaultAdmins, options);
                    string? directoryPath = Path.GetDirectoryName(filePath);
                    if (directoryPath != null)
                    {
                        if (!Directory.Exists(directoryPath))
                        {
                            Directory.CreateDirectory(directoryPath);
                        }
                    }
                    File.WriteAllText(filePath, defaultJson);

                    Log("[LoadAdmins] Created a new JSON file with default content.");
                }
                catch (Exception e)
                {
                    Log($"[LoadAdmins FATAL] Error creating the JSON file: {e.Message}");
                }
            }
        }

        private bool IsPlayerAdmin(CCSPlayerController? player, string command = "", params string[] permissions)
        {
            // Global override: everyone is treated as admin if at_everyone_is_admin is true.
            if (everyoneIsAdmin.Value)
            {
                return true;
            }

            // Commands issued directly from the server console should always be allowed.
            if (player == null)
            {
                return true;
            }

            // Per-match admins defined in the loaded match config (Steam64 IDs).
            // This allows the tournament platform / API to mark specific users as
            // admins for just this match without touching global admin files.
            try
            {
                if (isMatchSetup && matchConfig.AdminSteamIds != null && matchConfig.AdminSteamIds.Count > 0)
                {
                    string steamId = player.SteamID.ToString();
                    if (matchConfig.AdminSteamIds.Contains(steamId))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // If anything goes wrong reading matchConfig, fall through to the
                // normal global admin checks instead of failing the command.
            }

            // CSSharp-style permission flags (addons/counterstrikesharp/configs/admins.json).
            string[] updatedPermissions = permissions.Concat(new[] { "@css/root" }).ToArray();
            RequiresPermissionsOr attr = new(updatedPermissions)
            {
                Command = command
            };
            if (attr.CanExecuteCommand(player))
            {
                return true;
            }

            // Legacy plugin-specific admins.json (csgo/cfg/AutoTournamentCS2/admins.json).
            if (loadedAdmins.ContainsKey(player.SteamID.ToString()))
            {
                return true;
            }

            return false;
        }

        private int GetRealPlayersCount()
        {
            return playerData.Count;
        }

        private string GetTournamentLiveStatusValue()
        {
            try
            {
                string status = (tournamentGoLiveStatus.Value ?? "").Trim().ToLowerInvariant();
                return status switch
                {
                    "" => "playing",
                    "playing" => "playing",
                    _ => "playing"
                };
            }
            catch
            {
                return "playing";
            }
        }

        // Helper function to update tournament status ConVars
        private void UpdateTournamentStatus(string status, string matchSlug = "")
        {
            try
            {
                // Idle states never carry a match: MAT treats "idle" / "warmup with no match"
                // as free, so a stale match id here keeps the server busy forever.
                bool hasActiveMatch = TournamentStatusLogic.HasActiveMatch(isMatchSetup, tournamentStatus.Value, status);

                tournamentStatus.Value = status;
                tournamentMatch.Value = TournamentStatusLogic.ResolveMatchValue(status, matchSlug, tournamentMatch.Value, hasActiveMatch);

                // Update timestamp to current Unix time
                long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                tournamentUpdated.Value = timestamp.ToString();

                Log($"[UpdateTournamentStatus] Status: {status}, Match: {tournamentMatch.Value}, Timestamp: {timestamp}");
            }
            catch (Exception e)
            {
                Log($"[UpdateTournamentStatus FATAL] An error occurred: {e.Message}");
            }
        }

        private void SendUnreadyPlayersMessage()
        {
            if (!isWarmup || matchStarted) return;
            List<string> unreadyPlayers = new();

            foreach (var kvp in playerData)
            {
                var player = kvp.Value;
                if (player == null || !player.IsValid || player.IsHLTV) continue;

                bool isReady = playerReadyStatus.TryGetValue(kvp.Key, out bool ready) && ready;
                if (!isReady)
                {
                    unreadyPlayers.Add(player.PlayerName);
                }
            }
            if (unreadyPlayers.Count > 0)
            {
                string unreadyPlayerList = string.Join(", ", unreadyPlayers);
                string minimumReadyRequiredMessage = isMatchSetup ? "" : $"[Minimum ready players required: {ChatColors.Green}{minimumReadyRequired}{ChatColors.Default}]";

                // Server.PrintToChatAll($"{chatPrefix} Unready players: {unreadyPlayerList}. Please type .ready to ready up! {minimumReadyRequiredMessage}");
                if (isRoundRestorePending)
                {
                    PrintToAllChat(Localizer["at.ready.readytotestorebackupinfomessage", unreadyPlayerList, minimumReadyRequiredMessage]);
                }
                else
                {
                    PrintToAllChat(Localizer["at.utility.unreadyplayers", unreadyPlayerList, minimumReadyRequiredMessage]);
                }
            }
            else
            {
                int countOfReadyPlayers = playerReadyStatus.Count(kv => kv.Value == true);
                if (isMatchSetup)
                {
                    // Server.PrintToChatAll($"{chatPrefix} Current ready players: {ChatColors.Green}{countOfReadyPlayers}{ChatColors.Default}");
                    PrintToAllChat(Localizer["at.utility.readyplayers", countOfReadyPlayers]);
                }
                else
                {
                    // Server.PrintToChatAll($"{chatPrefix} Minimum ready players required {ChatColors.Green}{minimumReadyRequired}{ChatColors.Default}, current ready players: {ChatColors.Green}{countOfReadyPlayers}{ChatColors.Default}");
                    PrintToAllChat(Localizer["at.utility.minimumreadyplayers", minimumReadyRequired, countOfReadyPlayers]);
                }
            }
        }

        private void SendPausedStateMessage()
        {
            if (isPaused && matchStarted)
            {
                var pauseTeamName = unpauseData["pauseTeam"];
                if ((string)pauseTeamName == "Admin")
                {
                    PrintToAllChat(Localizer["at.pause.adminpausedthematch"]);
                }
                else if ((string)pauseTeamName == "RoundRestore" && !(bool)unpauseData["t"] && !(bool)unpauseData["ct"])
                {
                    PrintToAllChat(Localizer["at.pause.pausedbecauserestore"]);
                }
                else if ((bool)unpauseData["t"] && !(bool)unpauseData["ct"])
                {
                    PrintToAllChat(Localizer["at.pause.teamwantstounpause", reverseTeamSides["TERRORIST"].teamName, reverseTeamSides["CT"].teamName]);
                }
                else if (!(bool)unpauseData["t"] && (bool)unpauseData["ct"])
                {
                    PrintToAllChat(Localizer["at.pause.teamwantstounpause", reverseTeamSides["CT"].teamName, reverseTeamSides["TERRORIST"].teamName]);
                }
                else if (!(bool)unpauseData["t"] && !(bool)unpauseData["ct"])
                {
                    PrintToAllChat(Localizer["at.pause.pausedthematch", pauseTeamName]);
                }
            }
        }

        private void ExecWarmupCfg()
        {
            var absolutePath = Path.Join(Server.GameDirectory + "/csgo/cfg", warmupCfgPath);

            string warmupFullPath = Path.Join(Server.GameDirectory + "/csgo/cfg", warmupCfgPath);
            string humansCfgPath = "AutoTournamentCS2/humans.cfg";
            string humansFullPath = Path.Join(Server.GameDirectory + "/csgo/cfg", humansCfgPath);

            bool warmupExists = File.Exists(warmupFullPath);

            // Always execute the generic warmup config first (if present). This should contain
            // common warmup settings but no bot management.
            if (warmupExists)
            {
                Log($"[StartWarmup] Starting warmup! Executing Warmup CFG from {warmupCfgPath}");
                Server.ExecuteCommand($"exec {warmupCfgPath}");
            }
            else
            {
                Log($"[StartWarmup] Starting warmup! Warmup CFG not found in {absolutePath}, using default CFG!");
                Server.ExecuteCommand("mp_autokick 0; mp_autoteambalance 0; mp_buy_anywhere 0; mp_buytime 15; mp_death_drop_gun 0; mp_free_armor 0; mp_ignore_round_win_conditions 0; mp_limitteams 0; mp_respawn_on_death_ct 0; mp_respawn_on_death_t 0; mp_solid_teammates 0; mp_spectators_max 20; mp_maxmoney 16000; mp_startmoney 16000; mp_timelimit 0; sv_alltalk 0; sv_auto_full_alltalk_during_warmup_half_end 0; sv_deadtalk 1; sv_full_alltalk 0; sv_grenade_trajectory 0; sv_hibernate_when_empty 0; mp_weapons_allow_typecount -1; sv_infinite_ammo 0; sv_showimpacts 0; sv_voiceenable 1; sm_cvar sv_mute_players_with_social_penalties 0; sv_mute_players_with_social_penalties 0; tv_relayvoice 1; sv_cheats 0; mp_ct_default_melee weapon_knife; mp_ct_default_secondary weapon_hkp2000; mp_ct_default_primary \"\"; mp_t_default_melee weapon_knife; mp_t_default_secondary weapon_glock; mp_t_default_primary; mp_maxrounds 24; mp_warmup_start; mp_warmup_pausetimer 1; mp_warmuptime 9999; cash_team_bonus_shorthanded 0;");
            }

            // For non-simulation (human) matches we optionally layer on additional bot
            // management from humans.cfg (e.g. bot_kick), but we *never* do this in
            // simulation mode so that our simulated bots are not wiped.
            //
            // IMPORTANT:
            // - We only check !isSimulationMode here. Previously this also checked
            //   !isMatchSetup, which caused humans.cfg (and its bot_kick) to run once
            //   during simulation match setup before isMatchSetup was flipped to true,
            //   immediately kicking newly spawned simulation bots.
            if (!isSimulationMode)
            {
                if (File.Exists(humansFullPath))
                {
                    Log("[StartWarmup] Applying humans.cfg for non-simulation warmup (bot management, etc.)");
                    Server.ExecuteCommand($"exec {humansCfgPath}");
                }
            }
        }

        private void StartWarmup()
        {
            CrashBreadcrumb("StartWarmup: enter");
            unreadyPlayerMessageTimer?.Kill();
            unreadyPlayerMessageTimer = null;
            unreadyPlayerMessageTimer ??= AddTimer(chatTimerDelay, SendUnreadyPlayersMessage, TimerFlags.REPEAT);
            isWarmup = true;
            ExecWarmupCfg();
            
            // Auto-ready simulation helper: when enabled, spawn two bots after warmup config
            // has executed (including humans.cfg bot_kick) so auto-ready can be tested
            // without a human joining. Safe-guarded to never run if a human is connected.
            ClearAutoReadySimulationState();
            ScheduleAutoReadySimulationFlowIfNeeded(3.0f);
            // For real (non-simulation, non-practice) matches, ensure we always return
            // to normal cheats/timescale at the start of warmup so that any previous
            // simulation or practice configuration does not leak into this match.
            ApplyNormalTimescaleAndCheatsForRealMatches();
            UpdateTournamentStatus("warmup");
            TriggerMatchReportUpload("warmup_start");
            CrashBreadcrumb("StartWarmup: exit");
        }

        private void StartKnifeRound()
        {
            CrashBreadcrumb("StartKnifeRound: enter");
            // Kills unready players message timer
            if (unreadyPlayerMessageTimer != null)
            {
                unreadyPlayerMessageTimer.Kill();
                unreadyPlayerMessageTimer = null;
            }

            // Setting match phases bools
            matchStarted = true;
            readyAvailable = false;
            isWarmup = false;

            bool anyHumanConnectedForEvents = Utilities.GetPlayers().Any(p => p?.IsValid == true && !p.IsBot && !p.IsHLTV);
            bool botsOnlyForEvents =
                autoReadySimulationEnabled.Value &&
                autoReadySimulationAllowStartWithoutHumans.Value &&
                !anyHumanConnectedForEvents;

            // Send warmup_ended event
            Log($"[StartKnifeRound] Sending warmup_ended event");
            var warmupEndedEvent = new AutoTournamentCS2WarmupEndedEvent
            {
                MatchId = liveMatchId,
                MapNumber = matchConfig.CurrentMapNumber
            };
            if (!(botsOnlyForEvents && autoReadySimulationSkipAsyncEvents.Value))
            {
                Task.Run(async () =>
                {
                    await SendEventAsync(warmupEndedEvent);
                });
            }
            else
            {
                CrashBreadcrumb("StartKnifeRound: bots-only skipping async warmup_ended event send (crash isolation)");
            }

            // Send knife_round_started event
            Log($"[StartKnifeRound] Sending knife_round_started event");
            var knifeStartedEvent = new AutoTournamentCS2KnifeRoundStartedEvent
            {
                MatchId = liveMatchId,
                MapNumber = matchConfig.CurrentMapNumber
            };
            if (!(botsOnlyForEvents && autoReadySimulationSkipAsyncEvents.Value))
            {
                Task.Run(async () =>
                {
                    await SendEventAsync(knifeStartedEvent);
                });
            }
            else
            {
                CrashBreadcrumb("StartKnifeRound: bots-only skipping async knife_round_started event send (crash isolation)");
            }

            if (isSimulationMode)
            {
                // In simulation mode, we don't actually run a knife round.
                // Randomly assign sides and immediately end the knife phase.
                RandomizeSimulationSides();

                // Randomly pick a winner slot for the knife event payload.
                string winnerSlot = new Random().Next(0, 2) == 0 ? "team1" : "team2";
                var knifeEndedEvent = new AutoTournamentCS2KnifeRoundEndedEvent
                {
                    MatchId = liveMatchId,
                    MapNumber = matchConfig.CurrentMapNumber,
                    Winner = winnerSlot
                };
                Task.Run(async () =>
                {
                    await SendEventAsync(knifeEndedEvent);
                });

                // Go live immediately after "knife" in simulation mode.
                isKnifeRound = false;
                UpdateTournamentStatus("knife");
                StartLive();
                return;
            }

            isKnifeRound = true;

            var absolutePath = Path.Join(Server.GameDirectory + "/csgo/cfg", knifeCfgPath);

            if (File.Exists(Path.Join(Server.GameDirectory + "/csgo/cfg", knifeCfgPath)))
            {
                Log($"[StartKnifeRound] Starting Knife! Executing Knife CFG from {knifeCfgPath}");

                // When starting with 0 humans (auto-ready simulation), CS2 has been observed to segfault
                // during the warmup->knife transition on some servers. We provide an experimental set of
                // alternate command sequences + delays that you can toggle via cvars to see if any are
                // more stable on your environment.
                bool anyHumanConnected = Utilities.GetPlayers().Any(p => p?.IsValid == true && !p.IsBot && !p.IsHLTV);
                bool botsOnlyKnifeStart =
                    autoReadySimulationEnabled.Value &&
                    autoReadySimulationAllowStartWithoutHumans.Value &&
                    !anyHumanConnected;

                if (botsOnlyKnifeStart)
                {
                    int mode = autoReadySimulationKnifeStartMode.Value;
                    float delay = autoReadySimulationKnifeStartCommandDelay.Value;
                    if (delay < 0.0f) delay = 0.0f;
                    if (delay > 5.0f) delay = 5.0f;

                    CrashBreadcrumb($"StartKnifeRound: bots-only knife start mode={mode}, cmdDelay={delay:0.##}");

                    // Fast path (default): behave like a normal server and just exec knife.cfg.
                    // Safe/diagnostic mode can be enabled via cvar if needed.
                    if (!autoReadySimulationKnifeUseSafeMode.Value)
                    {
                        CrashBreadcrumb($"StartKnifeRound: bots-only fast path - exec knife cfg ({knifeCfgPath})");
                        Server.ExecuteCommand($"exec {knifeCfgPath}");
                        CrashBreadcrumb("StartKnifeRound: bots-only fast path - after exec knife cfg");
                    }
                    // Mode 5 is a special diagnostic mode: execute knife.cfg lines one at a time
                    // with breadcrumbs. Run immediately (no extra delay) so we can catch crashes
                    // that happen very quickly after entering the knife phase.
                    else if (mode == 5)
                    {
                        CrashBreadcrumb("StartKnifeRound: bots-only step-through knife.cfg enabled (immediate)");
                        StepThroughKnifeCfgBotsOnly(delay);
                    }
                    else if (mode == 6)
                    {
                        CrashBreadcrumb("StartKnifeRound: bots-only mode 6 (enter knife, no commands) - doing nothing");
                    }
                    else
                    {
                        // Avoid executing knife.cfg in bots-only mode. Your logs show CS2 segfaulting
                        // immediately after the server "execing AutoTournamentCS2/knife.cfg" when no humans are connected.
                        // Instead, apply the knife settings *without* mp_restartgame/mp_warmup_end here, and
                        // then drive the transition using the experimental modes below.
                        CrashBreadcrumb($"StartKnifeRound: bots-only - skipping exec {knifeCfgPath}, applying safe knife settings");
                        Server.ExecuteCommand(
                            "mp_ct_default_secondary \"\";" +
                            "mp_free_armor 1;" +
                            "mp_freezetime 10;" +
                            "mp_give_player_c4 0;" +
                            "mp_maxmoney 0;" +
                            "mp_respawn_immunitytime 0;" +
                            "mp_respawn_on_death_ct 0;" +
                            "mp_respawn_on_death_t 0;" +
                            "mp_roundtime 1.92;" +
                            "mp_roundtime_defuse 1.92;" +
                            "mp_roundtime_hostage 1.92;" +
                            "mp_t_default_secondary \"\";" +
                            "mp_round_restart_delay 3;" +
                            "mp_team_intro_time 0;" +
                            "mp_solid_teammates 1;"
                        );

                        CrashBreadcrumb($"StartKnifeRound: bots-only transition begin (mode={mode})");

                        switch (mode)
                        {
                            case 0:
                                CrashBreadcrumb("StartKnifeRound: bots-only exec 'mp_restartgame 1; mp_warmup_end'");
                                Server.ExecuteCommand("mp_restartgame 1; mp_warmup_end");
                                break;
                            case 1:
                                CrashBreadcrumb("StartKnifeRound: bots-only exec 'mp_warmup_end' then restartgame delayed");
                                Server.ExecuteCommand("mp_warmup_end");
                                AddTimer(delay, () =>
                                {
                                    CrashBreadcrumb("StartKnifeRound: bots-only exec 'mp_restartgame 1' (delayed)");
                                    Server.ExecuteCommand("mp_restartgame 1");
                                });
                                break;
                            case 2:
                                CrashBreadcrumb("StartKnifeRound: bots-only exec 'mp_restartgame 1' then warmup_end delayed");
                                Server.ExecuteCommand("mp_restartgame 1");
                                AddTimer(delay, () =>
                                {
                                    CrashBreadcrumb("StartKnifeRound: bots-only exec 'mp_warmup_end' (delayed)");
                                    Server.ExecuteCommand("mp_warmup_end");
                                });
                                break;
                            case 3:
                                CrashBreadcrumb("StartKnifeRound: bots-only exec 'mp_warmup_end' only");
                                Server.ExecuteCommand("mp_warmup_end");
                                break;
                            case 4:
                                CrashBreadcrumb("StartKnifeRound: bots-only exec 'mp_restartgame 1' only");
                                Server.ExecuteCommand("mp_restartgame 1");
                                break;
                            default:
                                CrashBreadcrumb("StartKnifeRound: bots-only unknown mode; defaulting to mode 2");
                                Server.ExecuteCommand("mp_restartgame 1");
                                AddTimer(delay, () =>
                                {
                                    CrashBreadcrumb("StartKnifeRound: bots-only exec 'mp_warmup_end' (delayed, default)");
                                    Server.ExecuteCommand("mp_warmup_end");
                                });
                                break;
                        }

                        CrashBreadcrumb("StartKnifeRound: bots-only transition scheduled");
                    }
                }
                else
                {
                    // Normal path: rely on knife.cfg to perform the warmup->knife transition.
                    // (knife.cfg already contains mp_restartgame/mp_warmup_end by default)
                    CrashBreadcrumb($"StartKnifeRound: exec knife cfg ({knifeCfgPath})");
                    Server.ExecuteCommand($"exec {knifeCfgPath}");
                    CrashBreadcrumb("StartKnifeRound: after exec knife cfg");
                }
            }
            else
            {
                Log($"[StartKnifeRound] Starting Knife! Knife CFG not found in {absolutePath}, using default CFG!");
                CrashBreadcrumb("StartKnifeRound: knife cfg missing, using default cfg commands");
                Server.ExecuteCommand("mp_ct_default_secondary \"\";mp_free_armor 1;mp_freezetime 10;mp_give_player_c4 0;mp_maxmoney 0;mp_respawn_immunitytime 0;mp_respawn_on_death_ct 0;mp_respawn_on_death_t 0;mp_roundtime 1.92;mp_roundtime_defuse 1.92;mp_roundtime_hostage 1.92;mp_t_default_secondary \"\";mp_round_restart_delay 3;mp_team_intro_time 0;mp_restartgame 1;mp_warmup_end;");
                CrashBreadcrumb("StartKnifeRound: after default cfg commands");
            }

            PrintToAllChat($"{ChatColors.Olive}KNIFE!");
            PrintToAllChat($"{ChatColors.Lime}KNIFE!");
            PrintToAllChat($"{ChatColors.Green}KNIFE!");
            UpdateTournamentStatus("knife");
            CrashBreadcrumb("StartKnifeRound: exit");
        }

        private void StepThroughKnifeCfgBotsOnly(float stepDelaySeconds)
        {
            try
            {
                string knifeCfgFilePath = Path.Join(Server.GameDirectory, "csgo", "cfg", knifeCfgPath);
                var lines = new List<string>();

                if (File.Exists(knifeCfgFilePath))
                {
                    lines.AddRange(File.ReadAllLines(knifeCfgFilePath));
                }
                else
                {
                    // Fallback to a reasonable default list matching the repo knife.cfg
                    lines.AddRange(new[]
                    {
                        "mp_ct_default_secondary \"\"",
                        "mp_free_armor 1",
                        "mp_freezetime 10",
                        "mp_give_player_c4 0",
                        "mp_maxmoney 0",
                        "mp_respawn_immunitytime 0",
                        "mp_respawn_on_death_ct 0",
                        "mp_respawn_on_death_t 0",
                        "mp_roundtime 1.92",
                        "mp_roundtime_defuse 1.92",
                        "mp_roundtime_hostage 1.92",
                        "mp_t_default_secondary \"\"",
                        "mp_round_restart_delay 3",
                        "mp_team_intro_time 0",
                        "mp_restartgame 1",
                        "mp_warmup_end",
                        "mp_solid_teammates 1",
                    });
                }

                // Filter comments/empty lines
                var commands = new List<string>();
                foreach (var raw in lines)
                {
                    if (raw == null) continue;
                    var t = raw.Trim();
                    if (t.Length == 0) continue;
                    if (t.StartsWith("//")) continue;
                    if (t.StartsWith("#")) continue;
                    commands.Add(t);
                }

                int startIndex = autoReadySimulationKnifeStepStartIndex.Value;
                if (startIndex < 0) startIndex = 0;
                if (startIndex > commands.Count) startIndex = commands.Count;

                int maxSteps = autoReadySimulationKnifeStartMaxSteps.Value;
                if (maxSteps > 0 && startIndex + maxSteps < commands.Count)
                {
                    commands = commands.Skip(startIndex).Take(maxSteps).ToList();
                }
                else if (startIndex > 0)
                {
                    commands = commands.Skip(startIndex).ToList();
                }

                if (stepDelaySeconds < 0.0f) stepDelaySeconds = 0.0f;
                if (stepDelaySeconds > 5.0f) stepDelaySeconds = 5.0f;

                CrashBreadcrumb($"StepThroughKnifeCfgBotsOnly: executing {commands.Count} command(s) from knife.cfg, startIndex={startIndex}, stepDelay={stepDelaySeconds:0.##}");

                // Execute the first command immediately (no timer), then schedule one command at a time.
                // This helps isolate whether the crash is caused by:
                // - entering knife phase itself,
                // - a specific command,
                // - or scheduling many timers at once.
                void StepNext(int idx)
                {
                    if (idx < 0 || idx >= commands.Count)
                    {
                        CrashBreadcrumb("StepThroughKnifeCfgBotsOnly: completed all commands");
                        return;
                    }

                    string cmd = commands[idx];
                    CrashBreadcrumb($"StepThroughKnifeCfgBotsOnly: [{idx + 1}/{commands.Count}] exec '{cmd}'");
                    Server.ExecuteCommand(cmd);

                    int next = idx + 1;
                    if (next < commands.Count)
                    {
                        AddTimer(stepDelaySeconds, () => StepNext(next));
                    }
                    else
                    {
                        CrashBreadcrumb("StepThroughKnifeCfgBotsOnly: completed all commands");
                    }
                }

                StepNext(0);
            }
            catch (Exception e)
            {
                Log($"[AutoReadySimulation] StepThroughKnifeCfgBotsOnly failed: {e.Message}");
            }
        }

        private void RandomizeSimulationSides()
        {
            try
            {
                bool team1StartsCT = new Random().Next(0, 2) == 0;

                if (team1StartsCT)
                {
                    teamSides[atTeam1] = "CT";
                    teamSides[atTeam2] = "TERRORIST";
                    reverseTeamSides["CT"] = atTeam1;
                    reverseTeamSides["TERRORIST"] = atTeam2;
                }
                else
                {
                    teamSides[atTeam1] = "TERRORIST";
                    teamSides[atTeam2] = "CT";
                    reverseTeamSides["CT"] = atTeam2;
                    reverseTeamSides["TERRORIST"] = atTeam1;
                }

                isKnifeRequired = false;
                SetTeamNames();
                Log($"[SimulationMode] Randomized sides - team1: {teamSides[atTeam1]}, team2: {teamSides[atTeam2]}");
            }
            catch (Exception e)
            {
                Log($"[SimulationMode] Failed to randomize sides: {e.Message}");
            }
        }

        private void SendSideSelectionMessage()
        {
            if (!isSideSelectionPhase) return;
            PrintToAllChat(Localizer["at.knife.sidedecisionpending", knifeWinnerName]);
            ShowNotification($"🔪 {knifeWinnerName} WON KNIFE<br>Waiting for side selection...", "#ffaa00", 20);
            // Server.PrintToChatAll($"{chatPrefix} {ChatColors.Green}{knifeWinnerName}{ChatColors.Default} Won the knife. Waiting for them to type {ChatColors.Green}.stay{ChatColors.Default} or {ChatColors.Green}.switch{ChatColors.Default}");
        }

        private void StartAfterKnifeWarmup()
        {
            isWarmup = true;
            ExecWarmupCfg();
            knifeWinnerName = knifeWinner == 3 ? reverseTeamSides["CT"].teamName : reverseTeamSides["TERRORIST"].teamName;
            ShowDamageInfo();
            
            // Show knife winner notification immediately
            ShowNotification($"🔪 {knifeWinnerName} WON KNIFE<br>Waiting for side selection...", "#ffaa00", 22);
            
            int sideSelectionSeconds = sideSelectionTime.Value;
            if (sideSelectionEnabled.Value && sideSelectionSeconds > 0)
            {
                sideSelectionRemainingSeconds = sideSelectionSeconds;
                PrintToAllChat(Localizer["at.knife.sidedecisionpendingwithtimer", knifeWinnerName, sideSelectionSeconds]);
                
                // Show countdown on center screen (will replace the initial notification)
                StartCountdown(sideSelectionSeconds, $"🔪 {knifeWinnerName} SIDE SELECTION<br>{{0}}s remaining", "#ffaa00");
                
                // Start countdown timer for side selection
                sideSelectionTimer = AddTimer(sideSelectionSeconds, () =>
                {
                    if (isSideSelectionPhase)
                    {
                        // Cancel reminder timer
                        if (sideSelectionReminderTimer != null)
                        {
                            sideSelectionReminderTimer.Kill();
                            sideSelectionReminderTimer = null;
                        }
                        
                        // Time expired, pick random side
                        bool shouldSwitch = new Random().Next(0, 2) == 1;
                        
                        if (shouldSwitch)
                        {
                            Server.ExecuteCommand("mp_swapteams;");
                            SwapSidesInTeamData(true);
                            PrintToAllChat(Localizer["at.knife.timerexpiredrandomswap", knifeWinnerName]);
                        }
                        else
                        {
                            PrintToAllChat(Localizer["at.knife.timerexpiredrandomstay", knifeWinnerName]);
                        }
                        
                        StartLive();
                    }
                });
                
                // Start reminder timer that fires at configured interval
                sideSelectionReminderTimer = AddTimer(sideSelectionReminderInterval.Value, () =>
                {
                    if (!isSideSelectionPhase || sideSelectionTimer == null)
                    {
                        // Side selection ended, cancel reminder
                        if (sideSelectionReminderTimer != null)
                        {
                            sideSelectionReminderTimer.Kill();
                            sideSelectionReminderTimer = null;
                        }
                        return;
                    }
                    
                    sideSelectionRemainingSeconds -= 10;
                    
                    if (sideSelectionRemainingSeconds <= 0)
                    {
                        // Timer will expire soon, don't show reminder
                        if (sideSelectionReminderTimer != null)
                        {
                            sideSelectionReminderTimer.Kill();
                            sideSelectionReminderTimer = null;
                        }
                        return;
                    }
                    
                    // Show remaining time
                    PrintToAllChat(Localizer["at.knife.sidetimeremaining", knifeWinnerName, sideSelectionRemainingSeconds]);
                }, TimerFlags.REPEAT);
            }
            else
            {
                PrintToAllChat(Localizer["at.knife.sidedecisionpending", knifeWinnerName]);
                // Show notification for side selection without timer
                ShowNotification($"🔪 {knifeWinnerName} WON KNIFE<br>Waiting for side selection...", "#ffaa00", 22);
            }
            // Server.PrintToChatAll($"{chatPrefix} {ChatColors.Green}{knifeWinnerName}{ChatColors.Default} Won the knife. Waiting for them to type {ChatColors.Green}.stay{ChatColors.Default} or {ChatColors.Green}.switch{ChatColors.Default}");
            sideSelectionMessageTimer ??= AddTimer(chatTimerDelay, SendSideSelectionMessage, TimerFlags.REPEAT);
            TriggerMatchReportUpload("post_knife_warmup");
        }

        private void SetLiveFlags()
        {
            // Setting match phases bools
            isWarmup = false;
            isSideSelectionPhase = false;
            matchStarted = true;
            isMatchLive = true;
            readyAvailable = false;
            isKnifeRound = false;
        }

        private void SetupLiveFlagsAndCfg()
        {
            SetLiveFlags();
            KillPhaseTimers();
            ExecLiveCFG();
            // Apply any per-match overtime / regulation settings provided in the
            // JSON match configuration (shuffle tournaments, etc.). This lets the
            // external controller drive mp_maxrounds and mp_overtime_enable without
            // relying on plugin-only convars.
            ApplyOvertimeAndMaxRoundsFromConfig();
            // Adding timer here to make sure that CFG execution is completed till then
            AddTimer(1, () =>
            {
                HandlePlayoutConfig();
                ExecuteChangedConvars();
            });
        }

        private void StartLive()
        {
            CrashBreadcrumb("StartLive: enter");
            SetupLiveFlagsAndCfg();
            CrashBreadcrumb("StartLive: after SetupLiveFlagsAndCfg");
            StartDemoRecording();
            CrashBreadcrumb("StartLive: after StartDemoRecording");

            // Storing 0-0 score backup file as lastBackupFileName, so that .stop functions properly in first round.
            lastBackupFileName = $"at_{liveMatchId}_{matchConfig.CurrentMapNumber}_round00.txt";
            lastAutoTournamentCS2BackupFileName = $"at_{liveMatchId}_{matchConfig.CurrentMapNumber}_round00.json";

            // This is to reload the map once it is over so that all flags are reset accordingly
            Server.ExecuteCommand("mp_match_end_restart true");

            // Professional LIVE announcement + short core command help for players
            PrintToAllChat($"{ChatColors.Lime}MATCH LIVE{ChatColors.Default} — {ChatColors.Green}{atTeam1.teamName}{ChatColors.Default} vs {ChatColors.Green}{atTeam2.teamName}{ChatColors.Default}. Good luck & have fun!");
            
            // Display match rules and configuration
            DisplayMatchRules();
            
            // Show center notification
            ShowNotification($"🔴 MATCH LIVE 🔴<br>{atTeam1.teamName} vs {atTeam2.teamName}", "#00ff00", 24);

            // Send warmup_ended event if not coming from knife round
            if (!isSideSelectionPhase)
            {
                Log($"[StartLive] Sending warmup_ended event");
                var warmupEndedEvent = new AutoTournamentCS2WarmupEndedEvent
                {
                    MatchId = liveMatchId,
                    MapNumber = matchConfig.CurrentMapNumber
                };
                Task.Run(async () =>
                {
                    await SendEventAsync(warmupEndedEvent);
                });
            }

            var goingLiveEvent = new GoingLiveEvent
            {
                MatchId = liveMatchId,
                MapNumber = matchConfig.CurrentMapNumber,
            };

            Task.Run(async () =>
            {
                await SendEventAsync(goingLiveEvent);
            });
            string liveStatus = GetTournamentLiveStatusValue();
            UpdateTournamentStatus(liveStatus);
            CrashBreadcrumb("StartLive: exit");
        }

        private void KillPhaseTimers()
        {
            // Kill match start countdown timer if active
            if (matchStartCountdownTimer != null)
            {
                matchStartCountdownTimer.Kill();
                matchStartCountdownTimer = null;
            }
            unreadyPlayerMessageTimer?.Kill();
            sideSelectionMessageTimer?.Kill();
            pausedStateTimer?.Kill();
            sideSelectionReminderTimer?.Kill();
            unreadyPlayerMessageTimer = null;
            sideSelectionMessageTimer = null;
            pausedStateTimer = null;
            sideSelectionReminderTimer = null;
        }

        private (int alivePlayers, int totalHealth) GetAlivePlayers(int team)
        {
            int count = 0;
            int totalHealth = 0;
            foreach (var key in playerData.Keys)
            {
                CCSPlayerController player = playerData[key];
                if (team == 2 && reverseTeamSides["TERRORIST"].coach.Contains(player)) continue;
                if (team == 3 && reverseTeamSides["CT"].coach.Contains(player)) continue;
                if (!IsPlayerValid(player)) continue;
                if (player.TeamNum == team)
                {
                    if (player.PlayerPawn.Value!.Health > 0) count++;
                    totalHealth += player.PlayerPawn.Value!.Health;
                }
            }
            return (count, totalHealth);
        }

        /// <param name="loadQueuedMatch">
        /// True only for the automatic reset after a series ended normally: a match queued
        /// during postgame is then loaded. Every other reset (css_restart, css_endmatch,
        /// load failures, leaving practice) is an operator or error reset, and loading a
        /// match queued before it would resurrect an allocation the caller may already
        /// have moved elsewhere, so the queue is dropped.
        /// </param>
        private void ResetMatch(bool warmupCfgRequired = true, bool loadQueuedMatch = false)
        {
            if (!loadQueuedMatch)
            {
                ClearQueuedMatch("match reset");
            }

            // Covers the post-series reset, css_restart and css_endmatch.
            RestoreMatchRestartDelay("match reset");

            try
            {
                // We stop demo recording if a live match was restarted
                if (matchStarted && isDemoRecording)
                {
                    Server.ExecuteCommand($"tv_stoprecord");
                    isDemoRecording = false;
                }
                // Reset match data
                matchStarted = false;
                readyAvailable = true;
                isPaused = false;
                isMatchSetup = false;

                isWarmup = true;
                isKnifeRound = false;
                isSideSelectionPhase = false;
                isMatchLive = false;
                liveMatchId = -1;
                isPractice = false;
                isDryRun = false;
                ClearSimulationState();
                ClearAutoReadySimulationState();
                ClearAutoReadyState();
                
                // Reset enhanced features tracking
                pausesUsed.Clear();
                ggVotesCT.Clear();
                ggVotesT.Clear();
                
                // Kill active timers
                if (pauseTimeoutTimer != null)
                {
                    pauseTimeoutTimer.Kill();
                    pauseTimeoutTimer = null;
                }
                if (sideSelectionTimer != null)
                {
                    sideSelectionTimer.Kill();
                    sideSelectionTimer = null;
                }
                if (sideSelectionReminderTimer != null)
                {
                    sideSelectionReminderTimer.Kill();
                    sideSelectionReminderTimer = null;
                }
                if (ffwTimer != null)
                {
                    ffwTimer.Kill();
                    ffwTimer = null;
                }
                ffwTeamMissing = 0;
                ffwRemainingSeconds = 0;
                sideSelectionRemainingSeconds = 0;

                lastBackupFileName = "";
                lastAutoTournamentCS2BackupFileName = "";

                isRoundRestorePending = false;
                playerHasTakenDamage = false;

                // Unready all players
                foreach (var key in playerReadyStatus.Keys)
                {
                    playerReadyStatus[key] = false;
                }

                teamReadyOverride = new()
                {
                    {CsTeam.Terrorist, false},
                    {CsTeam.CounterTerrorist, false},
                    {CsTeam.Spectator, false}
                };

                HandleClanTags();

                // Reset unpause data
                Dictionary<string, object> unpauseData = new()
                {
                    { "ct", false },
                    { "t", false },
                    { "pauseTeam", "" }
                };

                // Reset stop data
                stopData["ct"] = false;
                stopData["t"] = false;

                // Reset owned bots data
                pracUsedBots = new Dictionary<int, Dictionary<string, object>>();
                noFlashList = new();
                lastGrenadesData = new();
                nadeSpecificLastGrenadeData = new();
                UnpauseMatch();

                atTeam1.teamName = "COUNTER-TERRORISTS";
                atTeam2.teamName = "TERRORISTS";

                atTeam1.teamPlayers = null;
                atTeam2.teamPlayers = null;

                HashSet<CCSPlayerController> coaches = GetAllCoaches();

                foreach (var coach in coaches)
                {
                    if (!IsPlayerValid(coach)) continue;
                    coach.Clan = "";
                    SetPlayerVisible(coach);
                }

                atTeam1.coach = new();
                atTeam2.coach = new();
                coachKillTimer?.Kill();
                coachKillTimer = null;

                atTeam1.seriesScore = 0;
                atTeam2.seriesScore = 0;

                Server.ExecuteCommand($"mp_teamname_1 {atTeam1.teamName}");
                Server.ExecuteCommand($"mp_teamname_2 {atTeam2.teamName}");

                teamSides[atTeam1] = "CT";
                teamSides[atTeam2] = "TERRORIST";
                reverseTeamSides["CT"] = atTeam1;
                reverseTeamSides["TERRORIST"] = atTeam2;

                // Keeping the log URLs to avoid their reset on match start.
                matchConfig = new()
                {
                    RemoteLogURL = matchConfig.RemoteLogURL,
                    RemoteLogHeaderKey = matchConfig.RemoteLogHeaderKey,
                    RemoteLogHeaderValue = matchConfig.RemoteLogHeaderValue
                };

                KillPhaseTimers();
                UpdatePlayersMap();

                // Clear any queued next-match identifier when performing a full reset.
                tournamentNextMatch.Value = "";
                // The previous match config is no longer loaded. The match report falls back
                // to it for the match slug, so it must not outlive the match.
                loadedConfigFile = "";

                if (warmupCfgRequired)
                {
                    StartWarmup();
                }
                else
                {
                    // Since we should be already in warmup phase by this point, we are just setting up the SendUnreadyPlayersMessage timer
                    unreadyPlayerMessageTimer?.Kill();
                    unreadyPlayerMessageTimer = null;
                    unreadyPlayerMessageTimer ??= AddTimer(chatTimerDelay, SendUnreadyPlayersMessage, TimerFlags.REPEAT);
                }
                UpdateTournamentStatus("idle", "");
                
                // Note: Auto-ready check not needed here because ResetMatch clears isMatchSetup,
                // so auto-ready won't trigger until a new match is loaded

                // If a new match was queued while the previous series was still active (postgame),
                // automatically load it now that the server has been fully reset to idle.
                TryLoadQueuedMatchAfterReset();
            }
            catch (Exception ex)
            {
                Log($"[ResetMatch - FATAL] [ERROR]: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies high-level overtime / regulation configuration from the loaded
        /// MatchConfig to the underlying CS2 cvars. This is driven by JSON fields
        /// like maxRounds and overtimeMode rather than plugin-only convars.
        ///
        /// - maxRounds (when present) maps directly to mp_maxrounds.
        /// - overtimeMode (\"enabled\" / \"disabled\") controls mp_overtime_enable
        ///   when present.
        ///
        /// NOTE: overtimeSegments is currently advisory only; we do not yet enforce
        /// a hard cap on the number of OT segments, we just keep playing until CS2
        /// ends the match as usual.
        /// </summary>
        private void ApplyOvertimeAndMaxRoundsFromConfig()
        {
            try
            {
                // Apply per-match regulation max rounds if provided.
                if (matchConfig.MaxRounds.HasValue && matchConfig.MaxRounds.Value > 0)
                {
                    int value = matchConfig.MaxRounds.Value;
                    Log($"[OvertimeConfig] Applying maxRounds={value} → mp_maxrounds");
                    Server.ExecuteCommand($"mp_maxrounds {value}");
                }

                // Apply overtime enable/disable if provided.
                if (!string.IsNullOrWhiteSpace(matchConfig.OvertimeMode))
                {
                    string mode = matchConfig.OvertimeMode!.ToLowerInvariant();
                    if (mode == "enabled")
                    {
                        Log("[OvertimeConfig] Enabling overtime via match config (overtimeMode=enabled).");
                        Server.ExecuteCommand("mp_overtime_enable 1");
                    }
                    else if (mode == "disabled")
                    {
                        Log("[OvertimeConfig] Disabling overtime via match config (overtimeMode=disabled).");
                        Server.ExecuteCommand("mp_overtime_enable 0");
                    }

                    // Log the effective value after issuing our commands so we can see if any
                    // external config or plugin is fighting us.
                    try
                    {
                        var otCvar = ConVar.Find("mp_overtime_enable");
                        if (otCvar != null)
                        {
                            int current = otCvar.GetPrimitiveValue<int>();
                            Log($"[OvertimeConfig] mp_overtime_enable is now '{current}' after ApplyOvertimeAndMaxRoundsFromConfig (mode={matchConfig.OvertimeMode}).");
                        }
                    }
                    catch
                    {
                        // Best‑effort diagnostic; ignore failures here so we never break match start.
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[OvertimeConfig FATAL] Error applying overtime/max rounds config: {ex.Message}");
            }
        }

        private void UpdatePlayersMap()
        {
            try
            {
                var playerEntities = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller");
                Log($"[UpdatePlayersMap] CCSPlayerController count: {playerEntities.Count<CCSPlayerController>()} matchModeOnly: {matchModeOnly}");

                // Build into a local map and swap it in only once the whole scan
                // has succeeded.
                //
                // This used to clear playerData up front and fill it in place.
                // Every member access in the loop below can throw on an entity
                // that is mid-disconnect or otherwise not fully valid, and the
                // catch at the bottom swallows it — so one bad entity left
                // playerData empty or half-built, and the plugin then believed
                // nobody was on the server. IsTeamReady logs playerCount:0 for
                // every team, .ready stops registering, and the match never goes
                // live, with nothing but a single FATAL line to say why.
                //
                // Failing now leaves the previous good map in place instead.
                var updatedPlayerData = new Dictionary<int, CCSPlayerController>();
                int updatedConnectedPlayers = 0;

                foreach (var player in playerEntities)
                {
                    try
                    {
                        if (player == null) continue;
                        if (!player.IsValid || player.IsHLTV) continue;

                        bool isSimulationBot = isSimulationMode && player.IsBot;

                        // Outside of simulation mode, we still ignore bots in playerData – they are not
                        // considered "real players" for the ready system or whitelist enforcement.
                        if (!isSimulationBot && player.IsBot) continue;

                        // In normal (non-simulation) matches, enforce that only configured players are
                        // allowed to remain on the server when a match is setup / matchModeOnly is true.
                        // Simulation bots are exempt from this so we do not immediately kick them.
                        if ((isMatchSetup || matchModeOnly) && !isSimulationBot)
                        {
                            CsTeam team = GetPlayerTeam(player);
                            if (team == CsTeam.None && player.UserId.HasValue)
                            {
                                Log($"[UpdatePlayersMap] Executing kickid for player '{player.PlayerName}' (UserId={(ushort)player.UserId.Value}) because team=None in match-only mode (isSimulationMode={isSimulationMode}, IsBot={player.IsBot}).");
                                Server.ExecuteCommand($"kickid {(ushort)player.UserId}");
                                continue;
                            }
                        }

                        // A player controller still exists after a player disconnects
                        // Hence checking whether the player is actually in the server or not
                        if (player.Connected != PlayerConnectedState.PlayerConnected) continue;

                        if (player.UserId.HasValue)
                        {
                            updatedPlayerData[player.UserId.Value] = player;
                        }
                        updatedConnectedPlayers++;
                    }
                    catch (Exception playerError)
                    {
                        // One unreadable entity must not cost us the whole
                        // refresh. Skip it and keep going.
                        Log($"[UpdatePlayersMap] Skipping a player entity that could not be read: {playerError.Message}");
                    }
                }

                // The scan completed, so this map is trustworthy. Publish it.
                playerData = updatedPlayerData;
                connectedPlayers = updatedConnectedPlayers;

                // Adding missing players in playerReadyStatus. Done after the
                // swap so a failed scan can never drop a player's ready state.
                foreach (var key in playerData.Keys)
                {
                    if (!playerReadyStatus.ContainsKey(key))
                    {
                        playerReadyStatus[key] = false;
                    }
                }

                // Removing disconnected players from playerReadyStatus
                foreach (var key in playerReadyStatus.Keys.ToList())
                {
                    if (!playerData.ContainsKey(key))
                    {
                        // Key is not present in playerData, so remove it from playerReadyStatus
                        playerReadyStatus.Remove(key);
                    }
                }
                Log($"[UpdatePlayersMap] CCSPlayerController count: {playerEntities.Count<CCSPlayerController>()}, RealPlayersCount: {GetRealPlayersCount()}");
            }
            catch (Exception e)
            {
                // playerData and connectedPlayers are left untouched: the local
                // map is only published on success, so a failure here means
                // "could not refresh", not "there is nobody on the server".
                Log($"[UpdatePlayersMap FATAL] An error occurred, keeping the previous player map ({playerData.Count} players): {e.Message}");
            }
        }

        public void DetermineKnifeWinner()
        {
            // Knife Round code referred from Get5, thanks to the Get5 team for their amazing job!
            (int tAlive, int tHealth) = GetAlivePlayers(2);
            (int ctAlive, int ctHealth) = GetAlivePlayers(3);
            Log($"[KNIFE OVER] CT Alive: {ctAlive} with Total Health: {ctHealth}, T Alive: {tAlive} with Total Health: {tHealth}");
            if (ctAlive > tAlive)
            {
                knifeWinner = 3;
            }
            else if (tAlive > ctAlive)
            {
                knifeWinner = 2;
            }
            else if (ctHealth > tHealth)
            {
                knifeWinner = 3;
            }
            else if (tHealth > ctHealth)
            {
                knifeWinner = 2;
            }
            else
            {
                // Choosing a winner randomly
                Random random = new();
                knifeWinner = random.Next(2, 4);
            }
        }

        private void HandleKnifeWinner(EventCsWinPanelRound @event)
        {
            DetermineKnifeWinner();
            // Below code is working partially (Winner audio plays correctly for knife winner team, but may display round winner incorrectly)
            // Hence we restart the game with StartAfterKnifeWarmup and allow the winning team to choose side

            @event.FunfactToken = "";

            // Commenting these assignments as they were crashing the server.
            // long empty = 0;
            // @event.FunfactPlayer = null;
            // @event.FunfactData1 = empty;
            // @event.FunfactData2 = empty;
            // @event.FunfactData3 = empty;
            int finalEvent = 10;
            if (knifeWinner == 3)
            {
                finalEvent = 8;
            }
            else if (knifeWinner == 2)
            {
                finalEvent = 9;
            }
            Log($"[KNIFE WINNER] Won by: {knifeWinner}, finalEvent: {@event.FinalEvent}, newFinalEvent: {finalEvent}");
            @event.FinalEvent = finalEvent;
        }

        private void HandleMapChangeCommand(CCSPlayerController? player, string mapName)
        {
            if (!IsPlayerAdmin(player, "css_map", "@css/map"))
            {
                SendPlayerNotAdminMessage(player);
                return;
            }

            if (matchStarted)
            {
                // ReplyToUserCommand(player, $"Map cannot be changed once the match is started!");
                ReplyToUserCommand(player, Localizer["at.utility.matchstarted"]);
                return;
            }

            if (!ExecuteMapChange(mapName, "MapChange"))
            {
                ReplyToUserCommand(player, Localizer["at.cc.invalidmap"]);
            }
        }

        private readonly WorkshopMapRegistry workshopMaps = new();

        /// <summary>
        /// Changes to a map given as an installed map name, a workshop id / path / URL,
        /// or the name of a workshop map. Returns false when the input is unusable.
        /// </summary>
        private bool ExecuteMapChange(string? mapInput, string logTag)
        {
            var (kind, arg) = MapTargetLogic.Resolve(mapInput, Server.IsMapValid, workshopMaps.IdFor);
            if (kind == MapChangeKind.Invalid)
            {
                Log($"[{logTag}] Refusing map change to '{mapInput}': not a map name or workshop id.");
                return false;
            }

            string command = kind switch
            {
                MapChangeKind.WorkshopId => "host_workshop_map",
                MapChangeKind.WorkshopName => "ds_workshop_changelevel",
                _ => "changelevel",
            };

            if (!isSimulationMode)
            {
                Log($"[{logTag}] Executing bot_kick before {command} (non-simulation match).");
                Server.ExecuteCommand("bot_kick");
            }
            else
            {
                Log($"[{logTag}] Skipping bot_kick before {command} because simulation mode is active.");
            }

            if (kind == MapChangeKind.WorkshopId) workshopMaps.MarkPending(arg);
            MapTransitionBreadcrumb($"MapChange[{logTag}]: {command} {arg} (from {Server.MapName})");
            Log($"[{logTag}] {command} {arg} (input: '{mapInput}')");
            Server.ExecuteCommand($"{command} \"{arg}\"");
            return true;
        }

        /// <summary>Whether the server is on this map (name, or the workshop id that loaded it).</summary>
        private bool IsOnMap(string? mapInput) =>
            MapTargetLogic.IsCurrentMap(mapInput, Server.MapName, workshopMaps.IdFor);

        private void HandleReadyRequiredCommand(CCSPlayerController? player, string commandArg)
        {
            if (!IsPlayerAdmin(player, "css_readyrequired", "@css/config"))
            {
                SendPlayerNotAdminMessage(player);
                return;
            }

            if (!string.IsNullOrWhiteSpace(commandArg))
            {
                if (int.TryParse(commandArg, out int readyRequired) && readyRequired >= 0 && readyRequired <= 32)
                {
                    minimumReadyRequired = readyRequired;
                    string minimumReadyRequiredFormatted = (player == null) ? $"{minimumReadyRequired}" : $"{ChatColors.Green}{minimumReadyRequired}{ChatColors.Default}";
                    // ReplyToUserCommand(player, $"Minimum ready players required to start the match are now set to: {minimumReadyRequiredFormatted}");
                    ReplyToUserCommand(player, Localizer["at.utility.minreadyplayers", minimumReadyRequiredFormatted]);
                    CheckLiveRequired();
                }
                else
                {
                    // ReplyToUserCommand(player, $"Invalid value for readyrequired. Please specify a valid non-negative number. Usage: !readyrequired <number_of_ready_players_required>");
                    ReplyToUserCommand(player, Localizer["at.utility.rrinvalidvalue"]);
                }
            }
            else
            {
                string minimumReadyRequiredFormatted = (player == null) ? $"{minimumReadyRequired}" : $"{ChatColors.Green}{minimumReadyRequired}{ChatColors.Default}";
                // ReplyToUserCommand(player, $"Current Ready Required: {minimumReadyRequiredFormatted} .Usage: !readyrequired <number_of_ready_players_required>");
                ReplyToUserCommand(player, Localizer["at.utility.currentreadyrequired", minimumReadyRequiredFormatted]);
            }
        }

        private bool IsLiveRequirementSatisfied()
        {
            if (!readyAvailable || matchStarted) return false;

            int countOfReadyPlayers = playerReadyStatus.Count(kv => kv.Value == true);

            if (isMatchSetup)
            {
                return AreAllConfiguredPlayersConnectedAndOnCorrectTeams()
                    && IsTeamsReady()
                    && IsSpectatorsReady();
            }

            if (minimumReadyRequired == 0)
            {
                return countOfReadyPlayers >= connectedPlayers && connectedPlayers > 0;
            }

            (int ctPlayers, int ctReady) = GetTeamPlayerCount((int)CsTeam.CounterTerrorist, false);
            (int tPlayers, int tReady) = GetTeamPlayerCount((int)CsTeam.Terrorist, false);

            return minimumReadyRequired > 0 &&
                ctPlayers >= minimumReadyRequired &&
                tPlayers >= minimumReadyRequired &&
                ctReady >= minimumReadyRequired &&
                tReady >= minimumReadyRequired;
        }

        private void CheckLiveRequired()
        {
            if (!readyAvailable || matchStarted) return;
            if (handleMatchStartInProgress) return;

            int countOfReadyPlayers = playerReadyStatus.Count(kv => kv.Value == true);

            Log($"[CheckLiveRequired] isMatchSetup={isMatchSetup}, readyAvailable={readyAvailable}, matchStarted={matchStarted}, minimumReadyRequired={minimumReadyRequired}, readyPlayers={countOfReadyPlayers}, connectedPlayers={connectedPlayers}");

            if (isMatchSetup)
            {
                bool allConfiguredPlayersConnectedAndOnCorrectTeams =
                    AreAllConfiguredPlayersConnectedAndOnCorrectTeams();
                bool teamsReady = IsTeamsReady();
                bool specsReady = IsSpectatorsReady();
                Log($"[CheckLiveRequired] Match-setup mode: allConfiguredPlayersConnectedAndOnCorrectTeams={allConfiguredPlayersConnectedAndOnCorrectTeams}, IsTeamsReady={teamsReady}, IsSpectatorsReady={specsReady}");

            }
            else if (minimumReadyRequired != 0)
            {
                (int ctPlayers, int ctReady) = GetTeamPlayerCount((int)CsTeam.CounterTerrorist, false);
                (int tPlayers, int tReady) = GetTeamPlayerCount((int)CsTeam.Terrorist, false);

                Log($"[CheckLiveRequired] No-match mode: minReadyPerTeam={minimumReadyRequired}, CT ready={ctReady}/{ctPlayers}, T ready={tReady}/{tPlayers}");
            }

            if (matchStartCountdownTimer != null && !IsLiveRequirementSatisfied())
            {
                matchStartCountdownTimer.Kill();
                matchStartCountdownTimer = null;
                Log("[CheckLiveRequired] Match start countdown cancelled (requirements no longer satisfied).");
            }

            bool liveRequired = IsLiveRequirementSatisfied();
            Log($"[CheckLiveRequired] liveRequired={liveRequired}");

            if (liveRequired)
            {
                // If auto-ready is enabled, show a countdown before starting the match
                if (autoReadyEnabled.Value && !matchStarted)
                {
                    StartMatchCountdown();
                }
                else
                {
                    HandleMatchStart(allowAutoReadySimulationWithoutHumans: true);
                }
            }
        }

        private bool AreAllConfiguredPlayersConnectedAndOnCorrectTeams()
        {
            // Simulation mode has its own readiness and roster logic (bots).
            if (isSimulationMode) return true;

            // If we don't have explicit per-team player rosters, fall back to the existing
            // players_per_team readiness gate.
            if (atTeam1.teamPlayers is not Newtonsoft.Json.Linq.JObject team1Players ||
                atTeam2.teamPlayers is not Newtonsoft.Json.Linq.JObject team2Players)
            {
                return true;
            }

            bool allOk = true;

            bool IsExpectedPlayerConnectedAndCorrectTeam(string steamIdString)
            {
                if (!ulong.TryParse(steamIdString, out ulong steamId))
                {
                    // Ignore non-steam keys; config should normally be Steam64 -> name.
                    return true;
                }

                CCSPlayerController? player = Utilities.GetPlayerFromSteamId(steamId);
                if (!IsPlayerValid(player))
                {
                    return false;
                }

                // GetPlayerTeam derives the correct in-game CS team based on the match config roster
                // (team1/team2) and the current side assignment (CT/TERRORIST).
                CsTeam expectedTeam = GetPlayerTeam(player!);
                if (expectedTeam == CsTeam.None)
                {
                    return false;
                }

                return player!.TeamNum == (int)expectedTeam;
            }

            foreach (var prop in team1Players.Properties())
            {
                if (!IsExpectedPlayerConnectedAndCorrectTeam(prop.Name))
                {
                    allOk = false;
                }
            }

            foreach (var prop in team2Players.Properties())
            {
                if (!IsExpectedPlayerConnectedAndCorrectTeam(prop.Name))
                {
                    allOk = false;
                }
            }

            return allOk;
        }

        private CounterStrikeSharp.API.Modules.Timers.Timer? matchStartCountdownTimer = null;
        private int matchStartCountdownSeconds = 0;

        private void StartMatchCountdown()
        {
            // Prevent multiple countdowns
            if (matchStartCountdownTimer != null || matchStarted || handleMatchStartInProgress)
            {
                return;
            }

            // Use configurable delay, with minimum of 1 second
            matchStartCountdownSeconds = Math.Max(1, autoReadyStartDelay.Value);
            PrintToAllChat($"{ChatColors.Green}All players ready!{ChatColors.Default}");
            
            matchStartCountdownTimer = AddTimer(1.0f, () =>
            {
                if (matchStarted || !readyAvailable)
                {
                    matchStartCountdownTimer?.Kill();
                    matchStartCountdownTimer = null;
                    return;
                }

                if (!IsLiveRequirementSatisfied())
                {
                    matchStartCountdownTimer?.Kill();
                    matchStartCountdownTimer = null;
                    Log("[StartMatchCountdown] Countdown cancelled mid-run (requirements no longer satisfied).");
                    return;
                }

                matchStartCountdownSeconds--;
                
                if (matchStartCountdownSeconds > 0)
                {
                    PrintToAllChat($"{ChatColors.Yellow}Starting game in {matchStartCountdownSeconds}...{ChatColors.Default}");
                }
                else
                {
                    matchStartCountdownTimer?.Kill();
                    matchStartCountdownTimer = null;
                    Server.NextFrame(() => HandleMatchStart(allowAutoReadySimulationWithoutHumans: true));
                }
            }, TimerFlags.REPEAT);
        }

        private void DisplayMatchRules()
        {
            List<string> rules = new List<string>();
            
            // Pause rules
            if (bothTeamsUnpauseRequired.Value)
            {
                rules.Add($"{ChatColors.Grey}Pauses:{ChatColors.Default} {ChatColors.Red}.pause{ChatColors.Default} to pause, {ChatColors.Red}.unpause{ChatColors.Default} to resume (both teams must unpause)");
            }
            else
            {
                rules.Add($"{ChatColors.Grey}Pauses:{ChatColors.Default} {ChatColors.Red}.pause{ChatColors.Default} to pause, {ChatColors.Red}.unpause{ChatColors.Default} to resume");
            }
            
            if (maxPausesPerTeam.Value > 0)
            {
                rules.Add($"{ChatColors.Grey}Max pauses:{ChatColors.Default} {ChatColors.Yellow}{maxPausesPerTeam.Value}{ChatColors.Default} per team");
            }
            
            if (pauseDuration.Value > 0)
            {
                int minutes = pauseDuration.Value / 60;
                int seconds = pauseDuration.Value % 60;
                string durationText = minutes > 0 ? $"{minutes}m {seconds}s" : $"{seconds}s";
                rules.Add($"{ChatColors.Grey}Max pause length:{ChatColors.Default} {ChatColors.Yellow}{durationText}{ChatColors.Default}");
            }
            
            // GG command
            if (ggEnabled.Value)
            {
                int thresholdPercent = (int)(ggThreshold.Value * 100);
                string ggRule = $"{ChatColors.Grey}Forfeit:{ChatColors.Default} {ChatColors.Red}.gg{ChatColors.Default} to forfeit (requires {ChatColors.Yellow}{thresholdPercent}%{ChatColors.Default} team consensus)";
                if (ggMinScoreDiff.Value > 0)
                {
                    ggRule += $", min score diff: {ChatColors.Yellow}{ggMinScoreDiff.Value}{ChatColors.Default}";
                }
                rules.Add(ggRule);
            }
            
            // Side selection timer
            if (sideSelectionEnabled.Value && sideSelectionTime.Value > 0)
            {
                rules.Add($"{ChatColors.Grey}Side selection:{ChatColors.Default} {ChatColors.Yellow}{sideSelectionTime.Value}s{ChatColors.Default} timer after knife round");
            }
            
            // FFW system
            if (ffwEnabled.Value)
            {
                int ffwMinutes = ffwTime.Value / 60;
                rules.Add($"{ChatColors.Grey}Forfeit on disconnect:{ChatColors.Default} {ChatColors.Yellow}{ffwMinutes}min{ChatColors.Default} timer if entire team leaves");
            }
            
            // Display rules
            if (rules.Count > 0)
            {
                PrintToAllChat($"{ChatColors.Grey}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━{ChatColors.Default}");
                foreach (string rule in rules)
                {
                    PrintToAllChat(rule);
                }
                PrintToAllChat($"{ChatColors.Grey}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━{ChatColors.Default}");
            }
        }

        private void HandleMatchStart(bool allowAutoReadySimulationWithoutHumans = false)
        {
            if (matchStarted)
            {
                Log("[HandleMatchStart] Ignoring: match already started.");
                return;
            }

            if (handleMatchStartInProgress)
            {
                Log("[HandleMatchStart] Ignoring re-entrant call (nested CheckLiveRequired / concurrent start).");
                return;
            }

            handleMatchStartInProgress = true;
            try
            {
                RunHandleMatchStartCore(allowAutoReadySimulationWithoutHumans);
            }
            finally
            {
                handleMatchStartInProgress = false;
            }
        }

        private void RunHandleMatchStartCore(bool allowAutoReadySimulationWithoutHumans)
        {
            CrashBreadcrumb($"HandleMatchStart: enter (isMatchSetup={isMatchSetup}, isKnifeRequired={isKnifeRequired}, allowAutoReadySimOverride={allowAutoReadySimulationWithoutHumans})");
            if (matchStartCountdownTimer != null)
            {
                matchStartCountdownTimer.Kill();
                matchStartCountdownTimer = null;
            }

            // Auto-ready simulation helper safety:
            // When we're running the ready-simulation bots (0 humans on server), we should
            // never transition into knife/live. Doing so appears to trigger CS2 instability
            // on some servers (segfault during knife round). This mode is intended only
            // to validate ready/auto-ready behavior in warmup.
            if (autoReadySimulationEnabled.Value && !isSimulationMode)
            {
                bool anyHumanConnected = Utilities.GetPlayers().Any(p => p?.IsValid == true && !p.IsBot && !p.IsHLTV);
                if (!anyHumanConnected)
                {
                    if (!allowAutoReadySimulationWithoutHumans && !autoReadySimulationAllowStartWithoutHumans.Value)
                    {
                        Log("[AutoReadySimulation] Blocking match start (no human players connected). Staying in warmup.");
                        CrashBreadcrumb("HandleMatchStart: blocked by auto-ready simulation guard (no humans)");
                        return;
                    }

                    Log("[AutoReadySimulation] WARNING: Allowing match start with 0 humans (admin/convar override). This may crash CS2 on some servers.");
                    CrashBreadcrumb("HandleMatchStart: auto-ready simulation override active (0 humans)");
                }
            }

            isPractice = false;
            isDryRun = false;
            if (isRoundRestorePending)
            {
                RestoreRoundBackup(null, pendingRestoreFileName);
                isRoundRestorePending = false;
                pendingRestoreFileName = "";
                return;
            }
            // If default names, we pick a player and use their name as their team name
            if (atTeam1.teamName == "COUNTER-TERRORISTS")
            {
                // atTeam1.teamName = teamName;
                teamSides[atTeam1] = "CT";
                reverseTeamSides["CT"] = atTeam1;
                foreach (var key in playerData.Keys)
                {
                    if (playerData[key].TeamNum == 3)
                    {
                        atTeam1.teamName = "team_" + RemoveSpecialCharacters(playerData[key].PlayerName.Replace(" ", "_"));
                        foreach (var coach in atTeam1.coach)
                        {
                            coach.Clan = $"[{atTeam1.teamName} COACH]";
                        }
                        break;
                    }
                }
                // Server.ExecuteCommand($"mp_teamname_1 {atTeam1.teamName}");
            }

            if (atTeam2.teamName == "TERRORISTS")
            {
                // atTeam2.teamName = teamName;
                teamSides[atTeam2] = "TERRORIST";
                reverseTeamSides["TERRORIST"] = atTeam2;
                foreach (var key in playerData.Keys)
                {
                    if (playerData[key].TeamNum == 2)
                    {
                        atTeam2.teamName = "team_" + RemoveSpecialCharacters(playerData[key].PlayerName.Replace(" ", "_"));
                        foreach (var coach in atTeam2.coach)
                        {
                            coach.Clan = $"[{atTeam2.teamName} COACH]";
                        }
                        break;
                    }
                }
                // Server.ExecuteCommand($"mp_teamname_2 {atTeam2.teamName}");
            }

            Server.ExecuteCommand($"mp_teamname_1 {reverseTeamSides["CT"].teamName}");
            Server.ExecuteCommand($"mp_teamname_2 {reverseTeamSides["TERRORIST"].teamName}");

            HandleClanTags();

            string seriesType = "BO" + matchConfig.NumMaps.ToString();
            CrashBreadcrumb($"HandleMatchStart: InitMatch begin (seriesType={seriesType})");
            liveMatchId = database.InitMatch(atTeam1.teamName, atTeam2.teamName, "-", isMatchSetup, liveMatchId, matchConfig.CurrentMapNumber, seriesType, matchConfig);
            CrashBreadcrumb($"HandleMatchStart: InitMatch done (liveMatchId={liveMatchId})");
            SetupRoundBackupFile();

            CrashBreadcrumb("HandleMatchStart: GetSpawns begin");
            GetSpawns();
            CrashBreadcrumb("HandleMatchStart: GetSpawns done");

            if (isKnifeRequired)
            {
                CrashBreadcrumb("HandleMatchStart: starting knife round");
                StartKnifeRound();
            }
            else
            {
                CrashBreadcrumb("HandleMatchStart: skipping knife, going live");
                StartLive();
            }
            if (showCreditsOnMatchStart.Value)
            {
                Server.PrintToChatAll($"{chatPrefix} {ChatColors.Green}MatchZy Enhanced{ChatColors.Default} is forked from MatchZy by shobhit-pathak.");
            }
            if (matchStartMessage.Value.Trim() != "" && matchStartMessage.Value.Trim() != "\"\"")
            {
                List<string> matchStartMessages = [.. matchStartMessage.Value.Split("$$$")];
                foreach (string message in matchStartMessages)
                {
                    PrintToAllChat(GetColorTreatedString(FormatCvarValue(message.Trim())));
                }
            }
            CrashBreadcrumb("HandleMatchStart: exit");
        }

        public void HandleClanTags()
        {
            // Currently it is not possible to keep updating player tags while in warmup without restarting the match
            // Hence returning from here until we find a proper solution
            return;

            // TODO: Re-enable when clan tag system is fixed
            /*
            if (readyAvailable && !matchStarted)
            {
                foreach (var key in playerData.Keys)
                {
                    if (playerReadyStatus[key])
                    {
                        playerData[key].Clan = "[Ready]";
                    }
                    else
                    {
                        playerData[key].Clan = "[Unready]";
                    }
                    Server.PrintToChatAll($"PlayerName: {playerData[key].PlayerName} Clan: {playerData[key].Clan}");
                }
            }
            else if (matchStarted)
            {
                foreach (var key in playerData.Keys)
                {
                    if (playerData[key].TeamNum == 2)
                    {
                        playerData[key].Clan = reverseTeamSides["TERRORIST"].teamTag;
                    }
                    else if (playerData[key].TeamNum == 3)
                    {
                        playerData[key].Clan = reverseTeamSides["CT"].teamTag;
                    }
                    Server.PrintToChatAll($"PlayerName: {playerData[key].PlayerName} Clan: {playerData[key].Clan}");
                }
            }
            */
        }

        private void HandleMatchEnd()
        {
            if (!isMatchLive) return;

            UpdateTournamentStatus("postgame");

            // This ensures that the mp_match_restart_delay is not shorter than what is required for the GOTV recording to finish.
            // Ref: Get5
            // The raise is computed per map from the operator's original value and the current
            // tv_delay, and undone on reset, so one long-delay match no longer leaks into later ones.
            int currentRestartDelay = ConVar.Find("mp_match_restart_delay")!.GetPrimitiveValue<int>();
            int tvDelay = GetTvDelay();
            bool hasUploadEndpoint = !string.IsNullOrEmpty(demoUploadURL);
            MatchEndDelays delays = matchRestartDelay.Compute(currentRestartDelay, tvDelay, isDemoRecordingEnabled, hasUploadEndpoint);
            int restartDelay = delays.RestartDelay;
            int tvFlushDelay = delays.TvFlushDelay;

            if (delays.CvarToSet is int newRestartDelay)
            {
                Log($"Set mp_match_restart_delay from {currentRestartDelay} to {newRestartDelay} for tv_delay {tvDelay} (original: {matchRestartDelay.OriginalDelay}) to ensure GOTV broadcast can finish.");
                ConVar.Find("mp_match_restart_delay")!.SetValue(newRestartDelay);
            }

            if (!isDemoRecordingEnabled)
            {
                Log($"[HandleMatchEnd] Demo recording disabled - using fast restart delay of {restartDelay}s");
            }
            else if (!hasUploadEndpoint)
            {
                Log($"[HandleMatchEnd] Demo recording enabled, no upload URL - using GOTV flush delay of {restartDelay}s (no upload wait)");
            }
            else
            {
                Log($"[HandleMatchEnd] Demo recording enabled with upload URL - using restart delay of {restartDelay}s (tv_delay {tvDelay})");
            }
            
            int currentMapNumber = matchConfig.CurrentMapNumber;
            Log($"[HandleMatchEnd] MAP ENDED, isMatchSetup: {isMatchSetup} matchid: {liveMatchId} currentMapNumber: {currentMapNumber} tvFlushDelay: {tvFlushDelay} demoRecording: {isDemoRecordingEnabled} uploadURL: {hasUploadEndpoint}");

            if (isDemoRecordingEnabled)
            {
                StopDemoRecording(tvFlushDelay - 0.5f, activeDemoFile, liveMatchId, currentMapNumber);
            }

            (string winnerName, string winnerSlot) = GetMatchWinner();
            (int t1score, int t2score) = GetTeamsScore();
            int team1SeriesScore = atTeam1.seriesScore;
            int team2SeriesScore = atTeam2.seriesScore;

            // High-signal checkpoint log so operators can see exactly what just happened
            // on this map and how it affects the overall series before any branching
            // logic (next map vs. series end) runs.
            string mapLabel = (currentMapNumber >= 0 && currentMapNumber < matchConfig.Maplist.Count)
                ? matchConfig.Maplist[currentMapNumber]
                : "unknown_map";
            Log($"[SeriesCheckpoint] Map {currentMapNumber} ({mapLabel}) finished. Map score: {atTeam1.teamName} {t1score} – {atTeam2.teamName} {t2score}. Series score now: {atTeam1.teamName} {team1SeriesScore} – {atTeam2.teamName} {team2SeriesScore}.");

            string statsPath = Server.GameDirectory + "/csgo/AutoTournamentCS2_Stats/" + liveMatchId.ToString();

            var mapResultEvent = new MapResultEvent
            {
                MatchId = liveMatchId,
                MapNumber = currentMapNumber,
                // Winner comes from the resolved map winner (score, then damage tiebreak),
                // never from a raw score comparison: a tied map used to fall through to
                // "team2" even when the tiebreak had picked team1.
                Winner = new Winner(MatchLogic.SideNumberForSlot(winnerSlot, teamSides[atTeam1]), winnerSlot),
                StatsTeam1 = new AutoTournamentCS2StatsTeam(atTeam1.id, atTeam1.teamName, team1SeriesScore, t1score, 0, 0, new List<StatsPlayer>()),
                StatsTeam2 = new AutoTournamentCS2StatsTeam(atTeam2.id, atTeam2.teamName, team2SeriesScore, t2score, 0, 0, new List<StatsPlayer>())
            };

            Task.Run(async () =>
            {
                await SendEventAsync(mapResultEvent);
                await database.SetMapEndData(liveMatchId, currentMapNumber, winnerName, t1score, t2score, team1SeriesScore, team2SeriesScore);
                await database.WritePlayerStatsToCsv(statsPath, liveMatchId, currentMapNumber);
            });

            // If a match is not setup, it was supposed to be a pug/scrim with 1 map
            // Hence we reset the match once it is over
            // Todo: Support BO3/BO5 in pugs as well
            if (!isMatchSetup)
            {
                EndSeries(winnerName, restartDelay - 1, t1score, t2score);
                return;
            }

            // Remaining maps count every played map, drawn ones included. The old
            // formula (NumMaps - series wins) ignored draws, so after a drawn map the
            // series never ended and the next line indexed past the map list.
            int remainingMaps = MatchLogic.RemainingMaps(matchConfig.NumMaps, matchConfig.Maplist.Count, currentMapNumber);
            Log($"[HandleMatchEnd] MATCH ENDED, remainingMaps: {remainingMaps}, NumMaps: {matchConfig.NumMaps}, Team1SeriesScore: {atTeam1.seriesScore}, Team2SeriesScore: {atTeam2.seriesScore}");
            if (MatchLogic.IsSeriesOver(matchConfig.NumMaps, remainingMaps, atTeam1.seriesScore, atTeam2.seriesScore, matchConfig.SeriesCanClinch))
            {
                // EndSeries picks the winner from the series score; a tied series score
                // (only possible with draws allowed or an even map count) ends as a draw.
                EndSeries(winnerName, restartDelay - 1, t1score, t2score);
                return;
            }
            if (atTeam1.seriesScore > atTeam2.seriesScore)
            {
                Server.PrintToChatAll($"{chatPrefix} {ChatColors.Green}{atTeam1.teamName}{ChatColors.Default} is winning the series {ChatColors.Green}{atTeam1.seriesScore}-{atTeam2.seriesScore}{ChatColors.Default}");

            }
            else if (atTeam2.seriesScore > atTeam1.seriesScore)
            {
                Server.PrintToChatAll($"{chatPrefix} {ChatColors.Green}{atTeam2.teamName}{ChatColors.Default} is winning the series {ChatColors.Green}{atTeam2.seriesScore}-{atTeam1.seriesScore}{ChatColors.Default}");

            }
            else
            {
                Server.PrintToChatAll($"{chatPrefix} The series is tied at {ChatColors.Green}{atTeam1.seriesScore}-{atTeam2.seriesScore}{ChatColors.Default}");
            }
            if (currentMapNumber + 1 >= matchConfig.Maplist.Count)
            {
                // Defensive: never index past the map list; end the series instead.
                Log($"[HandleMatchEnd] No map left in maplist (count {matchConfig.Maplist.Count}) after map {currentMapNumber}; ending series.");
                EndSeries(winnerName, restartDelay - 1, t1score, t2score);
                return;
            }
            matchConfig.CurrentMapNumber += 1;
            string nextMap = matchConfig.Maplist[matchConfig.CurrentMapNumber];

            // Calculate total delay before map change (restartDelay - 4 + 3 = restartDelay - 1)
            int totalDelay = restartDelay - 1;

            if (isPaused)
                UnpauseMatch();

            stopData["ct"] = false;
            stopData["t"] = false;

            KillPhaseTimers();

            // Announce map change with countdown
            string mapDisplayName = nextMap.StartsWith("de_") ? nextMap.Substring(3) : nextMap;
            string timeText;
            if (totalDelay >= 60)
            {
                int minutes = totalDelay / 60;
                int seconds = totalDelay % 60;
                if (seconds > 0)
                {
                    timeText = $"{minutes} minute{(minutes > 1 ? "s" : "")} {seconds} second{(seconds > 1 ? "s" : "")}";
                }
                else
                {
                    timeText = $"{minutes} minute{(minutes > 1 ? "s" : "")}";
                }
            }
            else
            {
                timeText = $"{totalDelay} second{(totalDelay > 1 ? "s" : "")}";
            }

            PrintToAllChat($"{ChatColors.Grey}Next map: {ChatColors.Green}{mapDisplayName}{ChatColors.Default} will load in {ChatColors.Yellow}{timeText}{ChatColors.Default}.");
            PrintToAllChat($"{ChatColors.Grey}Take a break! Use the restroom, grab some water, or stretch! 💧🚶");

            // Schedule countdown announcements
            // 60 seconds remaining (if total delay >= 70 seconds)
            if (totalDelay >= 70)
            {
                AddTimer(totalDelay - 60, () =>
                {
                    if (!isMatchSetup) return;
                    PrintToAllChat($"{ChatColors.Grey}Next map loads in {ChatColors.Yellow}1 minute{ChatColors.Default}...");
                });
            }

            // 30 seconds remaining (if total delay >= 40 seconds)
            if (totalDelay >= 40)
            {
                AddTimer(totalDelay - 30, () =>
                {
                    if (!isMatchSetup) return;
                    PrintToAllChat($"{ChatColors.Grey}Next map loads in {ChatColors.Yellow}30 seconds{ChatColors.Default}...");
                });
            }

            // 15 seconds remaining (if total delay >= 25 seconds)
            if (totalDelay >= 25)
            {
                AddTimer(totalDelay - 15, () =>
                {
                    if (!isMatchSetup) return;
                    PrintToAllChat($"{ChatColors.Yellow}Next map loads in 15 seconds...{ChatColors.Default}");
                });
            }

            // 5 seconds remaining (if total delay >= 10 seconds)
            if (totalDelay >= 10)
            {
                AddTimer(totalDelay - 5, () =>
                {
                    if (!isMatchSetup) return;
                    PrintToAllChat($"{ChatColors.Lime}Next map loads in 5 seconds!{ChatColors.Default}");
                });
            }

            AddTimer(restartDelay - 4, () =>
            {
                if (!isMatchSetup) return;

                // For simulation mode we need to fully reset per-map simulation state before
                // moving to the next map so that:
                // - The FE sees 0 connected / ready players at the start of the new map.
                // - Bots are re-spawned, re-mapped to players and re-readied for each map.
                if (isSimulationMode)
                {
                    Log($"[HandleMatchEnd] Preparing simulation state for next map {nextMap}.");

                    // Clear simulation identity mappings and ready-flow scheduling.
                    simulationPlayersByUserId.Clear();
                    simulationIdentityPool.Clear();
                    assignedSimulationSteamIds.Clear();
                    simulationReadyFlowScheduled = false;

                    // Clear per-player tracking so the heartbeat starts from a clean slate
                    // on the next map; new bots will repopulate these dictionaries.
                    playerReadyStatus.Clear();
                    playerData.Clear();
                    connectedPlayers = 0;
                    playerConnectionTimes.Clear();

                    // Defer the simulation flow to the new map. EventRoundStart on the
                    // target map will schedule a fresh simulation flow (spawn bots, send
                    // synthetic player_connect, simulate !ready, etc.).
                    simulationFlowDeferred = true;
                    simulationTargetMap = nextMap;
                }
                else
                {
                    // For non-simulation matches, ensure any ready-simulation tracking is reset
                    // so that (if enabled) the next map can spawn a fresh pair of bots.
                    ClearAutoReadySimulationState();
                }

                ChangeMap(nextMap, 3.0f);
                matchStarted = false;
                readyAvailable = true;
                isPaused = false;

                // Reset forced-ready overrides between maps so each map’s ready flow is
                // independent. New player/bot connections on the next map will drive a
                // fresh ready cycle and teamReadyOverride will only be set again when
                // teams are actually (or simulated) ready for that map.
                teamReadyOverride[CsTeam.Terrorist] = false;
                teamReadyOverride[CsTeam.CounterTerrorist] = false;
                teamReadyOverride[CsTeam.Spectator] = false;

                isWarmup = true;
                isKnifeRound = false;
                isSideSelectionPhase = false;
                isMatchLive = false;
                isPractice = false;
                isDryRun = false;
                StartWarmup();
                SetMapSides();
                
                // After map change, check if auto-ready should trigger for players already on teams
                // Use a delay to ensure warmup and team assignments are complete
                AddTimer(2.0f, () => {
                    if (autoReadyEnabled.Value && readyAvailable && !matchStarted && isMatchSetup)
                    {
                        CheckAndAutoReadyPlayers();
                    }
                });

                // Auto-ready simulation helper: on new maps, allow spawning the two test bots again.
                ClearAutoReadySimulationState();
                ScheduleAutoReadySimulationFlowIfNeeded(2.0f);
            });
        }

        private void ChangeMap(string mapName, float delay)
        {
            Log($"[ChangeMap] Changing map to {mapName} with delay {delay}");
            AddTimer(delay, () =>
            {
                if (!ExecuteMapChange(mapName, "ChangeMap"))
                {
                    Log($"[ChangeMap] Map change to '{mapName}' was not issued.");
                }
            });
        }

        /// <summary>
        /// Resolves the winner of the map that just ended and credits the series
        /// point. Returns the winner's team name ("Draw" when nobody won) and slot
        /// ("team1" / "team2" / "none"). Every event and DB write about this map
        /// must use this result so they agree with each other.
        /// </summary>
        private (string winnerName, string winnerSlot) GetMatchWinner()
        {
            (int t1score, int t2score) = GetTeamsScore();
            if (t1score != t2score)
            {
                return CreditMapWinner(MatchLogic.ResolveMapWinnerSlot(t1score, t2score, null));
            }

            // The map is tied on score. When the match config disallows draws
            // (overtime disabled with 0 segments, or capped OT) the tie is broken by
            // performance: damage, kills, headshot kills, utility damage, then a
            // deterministic coin flip. A tied map is never recorded as a draw in that
            // mode. When draws are allowed a tied map is recorded as a draw (legacy).
            bool drawsDisallowed = MatchLogic.DrawsDisallowed(matchConfig.OvertimeMode, matchConfig.OvertimeSegments);
            if (!drawsDisallowed)
            {
                Log($"[Tiebreak] Map ended tied on score (team1={t1score}, team2={t2score}) and draws are allowed; recording a draw.");
                return ("Draw", MatchLogic.NoTeam);
            }

            (MatchLogic.TiebreakTotals team1Totals, MatchLogic.TiebreakTotals team2Totals) = GetTiebreakTotals();
            int mapNumber = matchConfig.CurrentMapNumber;
            (string? tiebreakSlot, string criterion) = MatchLogic.ResolveTiedMap(team1Totals, team2Totals, drawsDisallowed: true, liveMatchId, mapNumber);

            if (criterion == MatchLogic.CoinFlipCriterion)
            {
                Log($"[Tiebreak] All performance criteria tied; deterministic coin flip (matchId={liveMatchId}, mapNumber={mapNumber}) picked {tiebreakSlot}.");
            }

            (string tiebreakName, string slot) = CreditMapWinner(MatchLogic.ResolveMapWinnerSlot(t1score, t2score, tiebreakSlot));
            Log($"[Tiebreak] Map ended tied on score (team1={t1score}, team2={t2score}). Draws disallowed " +
                $"(overtimeMode={matchConfig.OvertimeMode ?? "(unset)"}, overtimeSegments={matchConfig.OvertimeSegments?.ToString() ?? "(unset)"}); " +
                $"selecting '{tiebreakName}' ({slot}) as winner by {criterion}.");
            return (tiebreakName, slot);
        }

        private (string winnerName, string winnerSlot) CreditMapWinner(string slot)
        {
            if (slot == MatchLogic.Team1)
            {
                atTeam1.seriesScore++;
                return (atTeam1.teamName, slot);
            }
            if (slot == MatchLogic.Team2)
            {
                atTeam2.seriesScore++;
                return (atTeam2.teamName, slot);
            }
            return ("Draw", MatchLogic.NoTeam);
        }

        /// <summary>
        /// Aggregates the per-team tiebreak criteria (damage, kills, headshot kills,
        /// utility damage) from the current map's player stats. On failure returns
        /// zeroed totals, so the caller falls through to the deterministic coin flip
        /// rather than recording a draw.
        /// </summary>
        private (MatchLogic.TiebreakTotals team1, MatchLogic.TiebreakTotals team2) GetTiebreakTotals()
        {
            int d1 = 0, k1 = 0, h1 = 0, u1 = 0;
            int d2 = 0, k2 = 0, h2 = 0, u2 = 0;
            try
            {
                (Dictionary<ulong, Dictionary<string, object>> playerStatsDictionary, _, _) = GetPlayerStatsDict();

                static int Stat(Dictionary<string, object> stats, string key) =>
                    stats.TryGetValue(key, out var value) && int.TryParse(value?.ToString(), out int parsed) ? parsed : 0;

                foreach (var stats in playerStatsDictionary.Values)
                {
                    if (!stats.TryGetValue("TeamName", out var teamNameObj)) continue;
                    string teamName = teamNameObj?.ToString() ?? string.Empty;

                    if (teamName == atTeam1.teamName)
                    {
                        d1 += Stat(stats, "Damage"); k1 += Stat(stats, "Kills");
                        h1 += Stat(stats, "HeadShotKills"); u1 += Stat(stats, "UtilityDamage");
                    }
                    else if (teamName == atTeam2.teamName)
                    {
                        d2 += Stat(stats, "Damage"); k2 += Stat(stats, "Kills");
                        h2 += Stat(stats, "HeadShotKills"); u2 += Stat(stats, "UtilityDamage");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[Tiebreak FATAL] Failed to aggregate tiebreak stats: {ex.Message}");
            }

            Log($"[Tiebreak] Aggregate totals (damage/kills/headshot_kills/utility_damage) - " +
                $"{atTeam1.teamName}: {d1}/{k1}/{h1}/{u1}, {atTeam2.teamName}: {d2}/{k2}/{h2}/{u2}");
            return (new MatchLogic.TiebreakTotals(d1, k1, h1, u1), new MatchLogic.TiebreakTotals(d2, k2, h2, u2));
        }

        private (int t1score, int t2score) GetTeamsScore()
        {
            var teamEntities = Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager");
            int t1score = 0;
            int t2score = 0;
            foreach (var team in teamEntities)
            {
                if (team.Teamname == teamSides[atTeam1])
                {
                    t1score = team.Score;
                }
                else if (team.Teamname == teamSides[atTeam2])
                {
                    t2score = team.Score;
                }
            }
            return (t1score, t2score);
        }

        private int GetRoundNumer()
        {
            (int t1score, int t2score) = GetTeamsScore();

            return t1score + t2score;
        }

        public void HandlePostRoundStartEvent(EventRoundStart @event)
        {
            if (isDryRun) RandomizeSpawns();
            if (!matchStarted) return;
            playerHasTakenDamage = false;
            HandleCoaches();
            CreateAutoTournamentCS2RoundDataBackup();
            InitPlayerDamageInfo();
            UpdateHostname();
            
            // Reset .gg votes at the start of each round
            ggVotesCT.Clear();
            ggVotesT.Clear();

            // Send round_started event
            if (isMatchLive)
            {
                Log($"[HandlePostRoundStartEvent] Sending round_started event");
                (int t1score, int t2score) = GetTeamsScore();

                var roundStartedEvent = new AutoTournamentCS2RoundStartedEvent
                {
                    MatchId = liveMatchId,
                    MapNumber = matchConfig.CurrentMapNumber,
                    RoundNumber = GetRoundNumer() + 1, // Next round number
                    Team1Score = t1score,
                    Team2Score = t2score
                };

                Task.Run(async () =>
                {
                    await SendEventAsync(roundStartedEvent);
                });
            }

            TriggerMatchReportUpload("round_start");
        }

        private void HandlePostRoundEndEvent(EventRoundEnd @event)
        {
            try
            {
                if (isMatchLive)
                {
                    coachKillTimer?.Kill();
                    coachKillTimer = null;
                    (int t1score, int t2score) = GetTeamsScore();
                    Server.PrintToChatAll($"{chatPrefix} {ChatColors.Green}{atTeam1.teamName} [{t1score} - {t2score}] {atTeam2.teamName}");

                    ShowDamageInfo();

                    (Dictionary<ulong, Dictionary<string, object>> playerStatsDictionary, List<StatsPlayer> playerStatsListTeam1, List<StatsPlayer> playerStatsListTeam2) = GetPlayerStatsDict();

                    int currentMapNumber = matchConfig.CurrentMapNumber;
                    long matchId = liveMatchId;
                    // The round winner is whichever team is on the winning side this round
                    // (sides swap after this event is built), not whoever leads the map.
                    string roundWinnerSlot = MatchLogic.SlotForTeamNum(@event.Winner, teamSides[atTeam1]) ?? MatchLogic.NoTeam;
                    Winner winner = new(@event.Winner.ToString(), roundWinnerSlot);

                    var roundEndEvent = new AutoTournamentCS2RoundEndedEvent
                    {
                        MatchId = liveMatchId,
                        MapNumber = matchConfig.CurrentMapNumber,
                        RoundNumber = GetRoundNumer(),
                        Reason = @event.Reason,
                        RoundTime = 0,
                        Winner = winner,
                        StatsTeam1 = new AutoTournamentCS2StatsTeam(atTeam1.id, atTeam1.teamName, atTeam1.seriesScore, t1score, 0, 0, playerStatsListTeam1),
                        StatsTeam2 = new AutoTournamentCS2StatsTeam(atTeam2.id, atTeam2.teamName, atTeam2.seriesScore, t2score, 0, 0, playerStatsListTeam2),
                    };

                    Task.Run(async () =>
                    {
                        await SendEventAsync(roundEndEvent);
                        await database.UpdatePlayerStatsAsync(matchId, currentMapNumber, playerStatsDictionary);
                        await database.UpdateMapStatsAsync(matchId, currentMapNumber, t1score, t2score);
                    });

                    string round = GetRoundNumer().ToString("D2");
                    lastBackupFileName = $"at_{liveMatchId}_{matchConfig.CurrentMapNumber}_round{round}.txt";
                    lastAutoTournamentCS2BackupFileName = $"at_{liveMatchId}_{matchConfig.CurrentMapNumber}_round{round}.json";
                    Log($"[HandlePostRoundEndEvent] Setting lastBackupFileName to {lastBackupFileName} and lastAutoTournamentCS2BackupFileName to {lastAutoTournamentCS2BackupFileName}");

                    // One of the team did not use .stop command hence display the proper message after the round has ended.
                    if (stopData["ct"] && !stopData["t"])
                    {
                        Server.PrintToChatAll($"{chatPrefix} The round restore request by {ChatColors.Green}{reverseTeamSides["CT"].teamName}{ChatColors.Default} was cancelled as the round ended");
                    }
                    else if (!stopData["ct"] && stopData["t"])
                    {
                        Server.PrintToChatAll($"{chatPrefix} The round restore request by {ChatColors.Green}{reverseTeamSides["TERRORIST"].teamName}{ChatColors.Default} was cancelled as the round ended");
                    }

                    // Invalidate .stop requests after a round is completed.
                    stopData["ct"] = false;
                    stopData["t"] = false;

                    bool swapRequired = IsTeamSwapRequired();

                    // If isRoundRestoring is true, sides will be swapped from round restore if required!
                    if (swapRequired && !isRoundRestoring)
                    {
                        SwapSidesInTeamData(false);
                    }

                    isRoundRestoring = false;
                    TriggerMatchReportUpload("round_end");
                }
            }
            catch (Exception e)
            {
                Log($"[HandlePostRoundEndEvent FATAL] An error occurred: {e.Message}");
            }
        }

        public bool IsTeamSwapRequired()
        {
            // Handling OTs and side swaps (Referred from Get5)
            var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First().GameRules!;
            int roundsPlayed = gameRules.TotalRoundsPlayed;

            int roundsPerHalf = ConVar.Find("mp_maxrounds")!.GetPrimitiveValue<int>() / 2;
            int roundsPerOTHalf = ConVar.Find("mp_overtime_maxrounds")!.GetPrimitiveValue<int>() / 2;

            bool halftimeEnabled = ConVar.Find("mp_halftime")!.GetPrimitiveValue<bool>();

            if (halftimeEnabled)
            {
                if (roundsPlayed == roundsPerHalf)
                {
                    return true;
                }
                // Now in OT.
                if (roundsPlayed >= 2 * roundsPerHalf)
                {
                    int otround = roundsPlayed - 2 * roundsPerHalf;  // round 33 -> round 3, etc.
                    // Do side swaps at OT halves (rounds 3, 9, ...)
                    if ((otround + roundsPerOTHalf) % (2 * roundsPerOTHalf) == 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private void PauseMatch(CCSPlayerController? player, CommandInfo? command)
        {
            if (isMatchLive && isPaused)
            {
                // ReplyToUserCommand(player, "Match is already paused!");
                ReplyToUserCommand(player, Localizer["at.utility.paused"]);
                return;
            }
            if (IsHalfTimePhase())
            {
                // ReplyToUserCommand(player, "You cannot use this command during halftime.");
                ReplyToUserCommand(player, Localizer["at.utility.duringhalftime"]);
                return;
            }
            if (IsPostGamePhase())
            {
                // ReplyToUserCommand(player, "You cannot use this command after the game has ended.");
                ReplyToUserCommand(player, Localizer["at.utility.matchended"]);
                return;
            }
            if (IsTacticalTimeoutActive())
            {
                // ReplyToUserCommand(player, "You cannot use this command when tactical timeout is active.");
                ReplyToUserCommand(player, Localizer["at.utility.tacticaltimeout"]);
                return;
            }
            if (!techPauseEnabled.Value && player != null)
            {
                PrintToPlayerChat(player, Localizer["at.pause.techpausenotenabled"]);
                return;
            }
            if (!string.IsNullOrEmpty(techPausePermission.Value) && techPausePermission.Value != "\"\"")
            {
                if (!IsPlayerAdmin(player, "css_pause", techPausePermission.Value))
                {
                    SendPlayerNotAdminMessage(player);
                    return;
                }
            }
            if (isMatchLive && !isPaused)
            {

                string pauseTeamName = "Admin";
                unpauseData["pauseTeam"] = "Admin";
                bool isAdmin = false;
                Team? pausingTeam = null;
                
                if (player?.TeamNum == 2)
                {
                    pauseTeamName = reverseTeamSides["TERRORIST"].teamName;
                    unpauseData["pauseTeam"] = reverseTeamSides["TERRORIST"].teamName;
                    pausingTeam = reverseTeamSides["TERRORIST"];
                }
                else if (player?.TeamNum == 3)
                {
                    pauseTeamName = reverseTeamSides["CT"].teamName;
                    unpauseData["pauseTeam"] = reverseTeamSides["CT"].teamName;
                    pausingTeam = reverseTeamSides["CT"];
                }
                else
                {
                    isAdmin = true;
                }
                
                // Check pause limit for non-admin pauses
                if (!isAdmin && pausingTeam != null && maxPausesPerTeam.Value > 0)
                {
                    if (!pausesUsed.ContainsKey(pausingTeam))
                    {
                        pausesUsed[pausingTeam] = 0;
                    }
                    
                    if (pausesUsed[pausingTeam] >= maxPausesPerTeam.Value)
                    {
                        PrintToPlayerChat(player!, Localizer["at.pause.nopausesleft", pauseTeamName, maxPausesPerTeam.Value]);
                        return;
                    }
                    
                    pausesUsed[pausingTeam]++;
                    int remaining = maxPausesPerTeam.Value - pausesUsed[pausingTeam];
                    PrintToAllChat(Localizer["at.pause.pausedthematchwithlimit", pauseTeamName, remaining]);
                }
                else
                {
                    PrintToAllChat(Localizer["at.pause.pausedthematch", pauseTeamName]);
                }
                // Server.PrintToChatAll($"{chatPrefix} {ChatColors.Green}{pauseTeamName}{ChatColors.Default} has paused the match. Type .unpause to unpause the match");

                SetMatchPausedFlags();
                
                // Start pause timeout timer if configured
                if (pauseDuration.Value > 0 && !isAdmin)
                {
                    pauseTimeoutTimer = AddTimer(pauseDuration.Value, () =>
                    {
                        if (isPaused)
                        {
                            PrintToAllChat(Localizer["at.pause.timeoutexpired"]);
                            UnpauseMatch();
                        }
                    });

                    // Show countdown on center screen (positioned lower to appear below CS2's "MATCH PAUSED" overlay)
                    // CS2's built-in pause system shows "MATCH PAUSED" overlay at top - we can't disable it
                    // Our countdown appears below it at 350px from top with larger text to be more visible
                    StartCountdown(pauseDuration.Value, "⏸️ AUTO-ENDS IN {0}s", "#ffff00", null, 350);
                }
                // Note: We don't show "PAUSED" notification because CS2's built-in pause system already displays "MATCH PAUSED" overlay
                // Player pauses: Chat only
                // Admin pauses: Chat only (CS2 shows "MATCH PAUSED" overlay)

                // Send match_paused event
                if (player != null && player.UserId.HasValue)
                {
                    Log($"[PauseMatch] Sending match_paused event - paused by {player.PlayerName}");

                    var playerInfo = BuildPlayerInfo(player, pauseTeamName);

                    var matchPausedEvent = new AutoTournamentCS2MatchPausedEvent
                    {
                        MatchId = liveMatchId,
                        MapNumber = matchConfig.CurrentMapNumber,
                        PausedBy = playerInfo,
                        IsTactical = isPauseCommandForTactical,
                        IsAdmin = isAdmin,
                        PauseTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };

                    Task.Run(async () =>
                    {
                        await SendEventAsync(matchPausedEvent);
                    });
                }
            }
        }

        private void ForcePauseMatch(CCSPlayerController? player, CommandInfo? command)
        {
            if (!matchStarted) return;
            if (!IsPlayerAdmin(player, "css_forcepause", "@css/config"))
            {
                SendPlayerNotAdminMessage(player);
                return;
            }
            if (isMatchLive && isPaused)
            {
                // ReplyToUserCommand(player, "Match is already paused!");
                ReplyToUserCommand(player, Localizer["at.utility.paused"]);
                return;
            }
            if (IsHalfTimePhase())
            {
                // ReplyToUserCommand(player, "You cannot use this command during halftime.");
                ReplyToUserCommand(player, Localizer["at.utility.duringhalftime"]);
                return;
            }
            if (IsPostGamePhase())
            {
                // ReplyToUserCommand(player, "You cannot use this command after the game has ended.");
                ReplyToUserCommand(player, Localizer["at.utility.matchended"]);
                return;
            }
            if (IsTacticalTimeoutActive())
            {
                // ReplyToUserCommand(player, "You cannot use this command when tactical timeout is active.");
                ReplyToUserCommand(player, Localizer["at.utility.tacticaltimeout"]);
                return;
            }
            unpauseData["pauseTeam"] = "Admin";
            PrintToAllChat(Localizer["at.pause.adminpausedthematch"]);
            // Server.PrintToChatAll($"{chatPrefix} {ChatColors.Green}Admin{ChatColors.Default} has paused the match.");
            if (player == null)
            {
                Server.PrintToConsole($"[Auto Tournament] {Localizer["at.pause.adminpausedthematch"]}");
            }
            
            // Note: CS2's built-in pause system shows "MATCH PAUSED" overlay, so we don't show additional center HTML
            // Chat message is sufficient
            
            SetMatchPausedFlags();

            // Send match_paused event for admin pause
            Log($"[ForcePauseMatch] Sending match_paused event - admin pause");

            AutoTournamentCS2PlayerInfo adminPlayerInfo;
            if (player != null)
            {
                adminPlayerInfo = BuildPlayerInfo(player, "Admin");
            }
            else
            {
                adminPlayerInfo = new AutoTournamentCS2PlayerInfo("Console", "Admin", "Admin");
            }

            var matchPausedEvent = new AutoTournamentCS2MatchPausedEvent
            {
                MatchId = liveMatchId,
                MapNumber = matchConfig.CurrentMapNumber,
                PausedBy = adminPlayerInfo,
                IsTactical = false,
                IsAdmin = true,
                PauseTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            Task.Run(async () =>
            {
                await SendEventAsync(matchPausedEvent);
            });
        }

        private void ForceUnpauseMatch(CCSPlayerController? player, CommandInfo? command)
        {
            if (matchStarted && isPaused)
            {
                if (!IsPlayerAdmin(player, "css_forceunpause", "@css/config"))
                {
                    SendPlayerNotAdminMessage(player);
                    return;
                }
                PrintToAllChat(Localizer["at.pause.adminunpausedthematch"]);
                UnpauseMatch();

                if (player == null)
                {
                    Server.PrintToConsole("[Auto Tournament] Admin has unpaused the match, resuming the match!");
                }
            }
        }

        private void UnpauseMatch()
        {
            Server.ExecuteCommand("mp_unpause_match;");

            // Calculate pause duration
            long pauseDuration = 0;
            if (pauseStartTime > 0)
            {
                pauseDuration = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - pauseStartTime;
            }

            isPaused = false;
            pauseStartTime = 0;
            unpauseData["ct"] = false;
            unpauseData["t"] = false;

            if (!isPaused && pausedStateTimer != null)
            {
                pausedStateTimer.Kill();
                pausedStateTimer = null;
            }
            
            // Kill pause timeout timer if active
            if (pauseTimeoutTimer != null)
            {
                pauseTimeoutTimer.Kill();
                pauseTimeoutTimer = null;
            }

            // Only send event and update status if match is actually live
            // Don't send events during ResetMatch when match is already ended
            if (isMatchLive && isMatchSetup && liveMatchId > 0)
            {
                // Send match_unpaused event
                Log($"[UnpauseMatch] Sending match_unpaused event - pause duration: {pauseDuration}s");

                var matchUnpausedEvent = new AutoTournamentCS2MatchUnpausedEvent
                {
                    MatchId = liveMatchId,
                    MapNumber = matchConfig.CurrentMapNumber,
                    PauseDuration = pauseDuration
                };

                Task.Run(async () =>
                {
                    await SendEventAsync(matchUnpausedEvent);
                });
                UpdateTournamentStatus(GetTournamentLiveStatusValue());
            }
            else
            {
                Log($"[UnpauseMatch] Skipping event/status update - isMatchLive={isMatchLive}, isMatchSetup={isMatchSetup}, liveMatchId={liveMatchId}");
            }
        }

        private void SetMatchPausedFlags()
        {
            coachKillTimer?.Kill();
            coachKillTimer = null;

            Server.ExecuteCommand("mp_pause_match;");
            isPaused = true;
            pauseStartTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            pausedStateTimer ??= AddTimer(chatTimerDelay, SendPausedStateMessage, TimerFlags.REPEAT);
            UpdateTournamentStatus("paused");
        }
        
        private bool IsTeamFullyMissing(int teamNum)
        {
            if (teamNum != 2 && teamNum != 3) return false;
            
            int teamPlayerCount = 0;
            foreach (var kvp in playerData)
            {
                var p = kvp.Value;
                if (p != null && p.IsValid && !p.IsBot && !p.IsHLTV && p.Connected == PlayerConnectedState.PlayerConnected && p.TeamNum == teamNum)
                {
                    teamPlayerCount++;
                }
            }
            
            return teamPlayerCount == 0;
        }
        
        private void CheckAndStartFFW()
        {
            if (!ffwEnabled.Value || !isMatchLive) return;
            if (ffwTimer != null) return; // Already running
            
            bool tMissing = IsTeamFullyMissing(2);
            bool ctMissing = IsTeamFullyMissing(3);

            Log($"[FFW] CheckAndStartFFW called - isMatchLive={isMatchLive}, tMissing={tMissing}, ctMissing={ctMissing}, ffwEnabled={ffwEnabled.Value}");
            
            if (tMissing && !ctMissing)
            {
                StartFFWTimer(2);
            }
            else if (ctMissing && !tMissing)
            {
                StartFFWTimer(3);
            }
        }
        
        private void StartFFWTimer(int teamNum)
        {
            if (ffwTimer != null) return;
            
            ffwTeamMissing = teamNum;
            ffwRemainingSeconds = ffwTime.Value;
            
            string teamName = teamNum == 2 ? reverseTeamSides["TERRORIST"].teamName : reverseTeamSides["CT"].teamName;
            Log($"[FFW] Starting FFW timer for teamNum={teamNum} (teamName={teamName}), duration={ffwRemainingSeconds}s");
            PrintToAllChat(Localizer["at.ffw.started", teamName, ffwRemainingSeconds / 60]);
            
            // Create timer that fires at configured interval
            float checkInterval = ffwCheckInterval.Value;
            ffwTimer = AddTimer(checkInterval, () =>
            {
                ffwRemainingSeconds -= (int)checkInterval;
                
                if (ffwRemainingSeconds <= 0)
                {
                    // FFW time expired, forfeit the match
                    ExecuteFFW();
                }
                else
                {
                    // Show remaining time in seconds
                    PrintToAllChat(Localizer["at.ffw.warning", teamName, ffwRemainingSeconds]);
                }
            }, TimerFlags.REPEAT);
        }
        
        private void CancelFFWTimer()
        {
            if (ffwTimer == null) return;
            
            string teamName = ffwTeamMissing == 2 ? reverseTeamSides["TERRORIST"].teamName : reverseTeamSides["CT"].teamName;
            Log($"[FFW] Cancelling FFW timer for teamName={teamName}, remainingSeconds={ffwRemainingSeconds}");
            PrintToAllChat(Localizer["at.ffw.cancelled", teamName]);
            
            ffwTimer.Kill();
            ffwTimer = null;
            ffwTeamMissing = 0;
            ffwRemainingSeconds = 0;
        }
        
        private void ExecuteFFW()
        {
            if (ffwTeamMissing == 0) return;
            
            string missingTeamName = ffwTeamMissing == 2 ? reverseTeamSides["TERRORIST"].teamName : reverseTeamSides["CT"].teamName;
            string winningTeamName = ffwTeamMissing == 2 ? reverseTeamSides["CT"].teamName : reverseTeamSides["TERRORIST"].teamName;
            
            Log($"[FFW] Executing forfeit - missingTeam={missingTeamName}, winningTeam={winningTeamName}, ffwRemainingSeconds={ffwRemainingSeconds}");
            PrintToAllChat(Localizer["at.ffw.executed", missingTeamName, winningTeamName]);
            
            // Award win to the team that stayed by setting scores
            var teamEntities = Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager");
            foreach (var team in teamEntities)
            {
                if (ffwTeamMissing == 2) // T forfeited, CT wins
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

            // Use the normal match-end flow so all postgame logic, events, and cleanup run correctly
            HandleMatchEnd();
            
            // Clean up local FFW state
            ffwTimer = null;
            ffwTeamMissing = 0;
            ffwRemainingSeconds = 0;
        }

        private void StartMatchMode()
        {
            if (matchStarted || (!isPractice && !isSleep)) return;
            ExecUnpracCommands();
            ResetMatch();
            RemoveSpawnBeams();
            Server.PrintToChatAll($"{chatPrefix} Match mode loaded!");
        }

        private void ExecLiveCFG()
        {
            int gameMode = GetGameMode();

            var cfgPath = liveCfgPath;
            var absolutePath = Path.Join(Server.GameDirectory + "/csgo/cfg", liveCfgPath);

            if (gameMode == 2)
            {
                absolutePath = Path.Join(Server.GameDirectory + "/csgo/cfg", liveWingmanCfgPath);
                cfgPath = liveWingmanCfgPath;
            }

            // We try to find the CFG in the cfg folder, if it is not there then we execute the default CFG.
            if (File.Exists(absolutePath))
            {
                Log($"[StartLive] Starting Live! Executing Live CFG from {cfgPath}");
                Server.ExecuteCommand($"exec {cfgPath}");
                Server.ExecuteCommand("mp_restartgame 1;mp_warmup_end;");
            }
            else
            {
                Log($"[StartLive] Starting Live! Live CFG not found in {absolutePath}, using default CFG!");
                if (gameMode == 2)
                {
                    Server.ExecuteCommand("ammo_grenade_limit_default 1;ammo_grenade_limit_flashbang 2;ammo_grenade_limit_total 4;cash_player_bomb_defused 300;cash_player_bomb_planted 300;cash_player_damage_hostage -30;cash_player_interact_with_hostage 300;cash_player_killed_enemy_default 300;cash_player_killed_enemy_factor 1;cash_player_killed_hostage -1000;cash_player_killed_teammate -300;cash_player_rescued_hostage 1000;cash_team_bonus_shorthanded 1000;cash_team_elimination_bomb_map 2750;cash_team_elimination_hostage_map_ct 2500;cash_team_elimination_hostage_map_t 2500;cash_team_hostage_alive 0;cash_team_hostage_interaction 600;cash_team_loser_bonus 2000;cash_team_loser_bonus_consecutive_rounds 300;cash_team_planted_bomb_but_defused 600;cash_team_rescued_hostage 600;cash_team_terrorist_win_bomb 3000;cash_team_win_by_defusing_bomb 3000;cash_team_win_by_hostage_rescue 2900;cash_team_win_by_time_running_out_bomb 2750;cash_team_win_by_time_running_out_hostage 2750;ff_damage_reduction_bullets 0.33;ff_damage_reduction_grenade 0.85;ff_damage_reduction_grenade_self 1;ff_damage_reduction_other 0.4;mp_afterroundmoney 0;mp_autokick 0;mp_autoteambalance 0;mp_backup_restore_load_autopause 0;mp_backup_round_auto 1;mp_buy_anywhere 0;mp_buy_during_immunity 0;mp_buytime 20;mp_c4timer 40;mp_ct_default_melee weapon_knife;mp_ct_default_primary \"\";mp_ct_default_secondary weapon_hkp2000;mp_death_drop_defuser 1;mp_death_drop_grenade 2;mp_death_drop_gun 1;mp_defuser_allocation 0;mp_display_kill_assists 1;mp_endmatch_votenextmap 0;mp_forcecamera 1;mp_free_armor 0;mp_freezetime 10;mp_friendlyfire 1;mp_give_player_c4 1;mp_halftime 1;mp_halftime_duration 15;mp_halftime_pausetimer 0;mp_ignore_round_win_conditions 0;mp_limitteams 0;mp_match_can_clinch 1;mp_match_end_restart 1;mp_maxmoney 8000;");
                    Server.ExecuteCommand("mp_maxrounds 16;mp_overtime_enable 1;mp_overtime_halftime_pausetimer 0;mp_overtime_maxrounds 4;mp_overtime_startmoney 8000;mp_playercashawards 1;mp_randomspawn 0;mp_respawn_immunitytime 0;mp_respawn_on_death_ct 0;mp_respawn_on_death_t 0;mp_round_restart_delay 7;mp_roundtime 1.5;mp_roundtime_defuse 1.5;mp_roundtime_hostage 1.5;mp_solid_teammates 1;mp_starting_losses 1;mp_startmoney 800;mp_t_default_melee weapon_knife;mp_t_default_primary \"\";mp_t_default_secondary weapon_glock;mp_teamcashawards 1;mp_timelimit 0;mp_weapons_allow_map_placed 1;mp_weapons_allow_zeus 1;mp_win_panel_display_time 3;spec_freeze_deathanim_time 0;spec_freeze_time 2;spec_freeze_time_lock 2;spec_replay_enable 0;sv_allow_votes 0;sv_auto_full_alltalk_during_warmup_half_end 0;sv_damage_print_enable 0;sv_deadtalk 1;sv_hibernate_postgame_delay 300;sv_ignoregrenaderadio 0;sv_infinite_ammo 0;sv_talk_enemy_dead 0;sv_talk_enemy_living 0;sv_voiceenable 1;tv_relayvoice 0");
                }
                else
                {
                    Server.ExecuteCommand("ammo_grenade_limit_default 1;ammo_grenade_limit_flashbang 2;ammo_grenade_limit_total 4;cash_player_bomb_defused 300;cash_player_bomb_planted 300;cash_player_damage_hostage -30;cash_player_interact_with_hostage 300;cash_player_killed_enemy_default 300;cash_player_killed_enemy_factor 1;cash_player_killed_hostage -1000;cash_player_killed_teammate -300;cash_player_rescued_hostage 1000;cash_team_elimination_bomb_map 3250;cash_team_elimination_hostage_map_ct 3000;cash_team_elimination_hostage_map_t 3000;cash_team_hostage_alive 0;cash_team_hostage_interaction 600;cash_team_loser_bonus 1400;cash_team_loser_bonus_consecutive_rounds 500;cash_team_planted_bomb_but_defused 600;cash_team_rescued_hostage 600;cash_team_terrorist_win_bomb 3500;cash_team_win_by_defusing_bomb 3500;");
                    Server.ExecuteCommand("cash_team_win_by_hostage_rescue 2900;cash_team_win_by_time_running_out_bomb 3250;cash_team_win_by_time_running_out_hostage 3250;ff_damage_reduction_bullets 0.33;ff_damage_reduction_grenade 0.85;ff_damage_reduction_grenade_self 1;ff_damage_reduction_other 0.4;mp_afterroundmoney 0;mp_autokick 0;mp_autoteambalance 0;mp_backup_restore_load_autopause 1;mp_backup_round_auto 1;mp_buy_anywhere 0;mp_buy_during_immunity 0;mp_buytime 20;mp_c4timer 40;mp_ct_default_melee weapon_knife;mp_ct_default_primary \"\";mp_ct_default_secondary weapon_hkp2000;mp_death_drop_defuser 1;mp_death_drop_grenade 2;mp_death_drop_gun 1;mp_defuser_allocation 0;mp_display_kill_assists 1;mp_endmatch_votenextmap 0;mp_forcecamera 1;mp_free_armor 0;mp_freezetime 18;mp_friendlyfire 1;mp_give_player_c4 1;mp_halftime 1;mp_halftime_duration 15;mp_halftime_pausetimer 0;mp_ignore_round_win_conditions 0;mp_limitteams 0;mp_match_can_clinch 1;mp_match_end_restart 0;mp_maxmoney 16000;mp_maxrounds 24;mp_overtime_enable 1;mp_overtime_halftime_pausetimer 0;mp_overtime_maxrounds 6;mp_overtime_startmoney 10000;mp_playercashawards 1;mp_randomspawn 0;mp_respawn_immunitytime 0;mp_respawn_on_death_ct 0;mp_respawn_on_death_t 0;mp_round_restart_delay 5;mp_roundtime 1.92;mp_roundtime_defuse 1.92;mp_roundtime_hostage 1.92;mp_solid_teammates 1;mp_starting_losses 1;mp_startmoney 800;mp_t_default_melee weapon_knife;mp_t_default_primary \"\";mp_t_default_secondary weapon_glock;mp_teamcashawards 1;mp_timelimit 0;mp_weapons_allow_map_placed 1;mp_weapons_allow_zeus 1;mp_win_panel_display_time 3;spec_freeze_deathanim_time 0;spec_freeze_time 2;spec_freeze_time_lock 2;spec_replay_enable 0;sv_allow_votes 1;sv_auto_full_alltalk_during_warmup_half_end 0;sv_damage_print_enable 0;sv_deadtalk 1;sv_hibernate_postgame_delay 300;sv_ignoregrenaderadio 0;sv_infinite_ammo 0;sv_talk_enemy_dead 0;sv_talk_enemy_living 0;sv_voiceenable 1;tv_relayvoice 1;mp_team_timeout_max 3;mp_team_timeout_ot_max 1;mp_team_timeout_ot_add_each 1;mp_team_timeout_time 30;sv_vote_command_delay 0;cash_team_bonus_shorthanded 0;mp_spectators_max 20;mp_team_intro_time 0;mp_restartgame 3;mp_warmup_end;");
                }
            }
        }

        private void SendPlayerNotAdminMessage(CCSPlayerController? player)
        {
            // ReplyToUserCommand(player, "You do not have permission to use this command!");
            ReplyToUserCommand(player, Localizer["at.utility.dontpermission"]);
        }

        private string GetColorTreatedString(string message)
        {
            // Adding extra space before args if message starts with a color name
            // This is because colors cannot be applied from 1st character, hence we make first character as an empty space
            if (message.StartsWith('{')) message = " " + message;

            foreach (var field in typeof(ChatColors).GetFields())
            {
                string pattern = $"{{{field.Name}}}";
                string? replacement = field.GetValue(null)?.ToString();

                if (replacement is null) return message;

                // Create a case-insensitive regular expression pattern for the color name
                string patternIgnoreCase = Regex.Escape(pattern);
                message = Regex.Replace(message, patternIgnoreCase, replacement, RegexOptions.IgnoreCase);
            }

            return message;
        }

        private void SendAvailableCommandsMessage(CCSPlayerController? player)
        {
            if (!IsPlayerValid(player)) return;

            ReplyToUserCommand(player, "Available commands:");

            if (isPractice)
            {
                player!.PrintToChat($" {ChatColors.Green}Spawns: {ChatColors.Default}.spawn, .ctspawn, .tspawn, .bestspawn, .worstspawn");
                player.PrintToChat($" {ChatColors.Green}Bots: {ChatColors.Default}.bot, .nobots, .crouchbot, .boost, .crouchboost");
                player.PrintToChat($" {ChatColors.Green}Nades: {ChatColors.Default}.loadnade, .savenade, .importnade, .listnades");
                player.PrintToChat($" {ChatColors.Green}Nade Throw: {ChatColors.Default}.rethrow, .throwindex <index>, .lastindex, .delay <number>");
                player.PrintToChat($" {ChatColors.Green}Utility & Toggles: {ChatColors.Default}.clear, .fastforward, .last, .back, .solid, .impacts, .traj");
                player.PrintToChat($" {ChatColors.Green}Utility & Toggles: {ChatColors.Default}.savepos, .loadpos");
                player.PrintToChat($" {ChatColors.Green}Sides & Others: {ChatColors.Default}.ct, .t, .spec, .fas, .god, .dryrun, .break, .exitprac");
                return;
            }
            if (readyAvailable)
            {
                player!.PrintToChat($" {ChatColors.Green}Ready/Unready: {ChatColors.Default}.ready, .unready");
                return;
            }
            if (isSideSelectionPhase)
            {
                player!.PrintToChat($" {ChatColors.Green}Side Selection: {ChatColors.Default}.stay, .switch, .ct, .t");
                return;
            }
            if (matchStarted)
            {
                string stopCommandMessage = isStopCommandAvailable ? ", .stop" : "";
                player!.PrintToChat($" {ChatColors.Green}Pause/Restore: {ChatColors.Default}.pause, .unpause, .tac, .tech{stopCommandMessage}");
                return;
            }
        }

        public void LoadClientNames()
        {
            string namesFileName = "Match_" + liveMatchId.ToString() + ".ini";
            string namesFilePath = Server.GameDirectory + "/csgo/AutoTournamentCS2PlayerNames/" + namesFileName;
            string? directoryPath = Path.GetDirectoryName(namesFilePath);
            if (directoryPath != null)
            {
                if (!Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("\"Names\"");
            sb.AppendLine("{");

            WriteClientNamesInFile(sb, atTeam1.teamPlayers);
            WriteClientNamesInFile(sb, atTeam2.teamPlayers);
            WriteClientNamesInFile(sb, matchConfig.Spectators);

            sb.AppendLine("}");
            File.WriteAllText(namesFilePath, sb.ToString());
            Server.ExecuteCommand($"sv_load_forced_client_names_file AutoTournamentCS2PlayerNames/" + namesFileName);
        }

        public void WriteClientNamesInFile(StringBuilder sb, JToken? players)
        {
            if (players == null) return;
            foreach (JProperty player in players)
            {
                string steamId = player.Name;
                string escapedName = player.Value.ToString().Replace("\"", "\\\"").Trim();

                if (string.IsNullOrEmpty(escapedName)) continue;

                sb.AppendLine($"\t\"{steamId}\"\t\t\"{escapedName}\"");
            }
        }

        static bool IsValidUrl(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? result))
            {
                return result != null && (result.Scheme == Uri.UriSchemeHttp || result.Scheme == Uri.UriSchemeHttps);
            }
            return false;
        }

        public string GetConvarStringValue(ConVar? cvar)
        {
            try
            {
                if (cvar == null) return "";
                string convarValue = cvar.Type switch
                {
                    ConVarType.Bool => cvar.GetPrimitiveValue<bool>().ToString(),
                    ConVarType.Float32 or ConVarType.Float64 => cvar.GetPrimitiveValue<float>().ToString(),
                    ConVarType.UInt16 => cvar.GetPrimitiveValue<ushort>().ToString(),
                    ConVarType.Int16 => cvar.GetPrimitiveValue<short>().ToString(),
                    ConVarType.UInt32 => cvar.GetPrimitiveValue<uint>().ToString(),
                    ConVarType.Int32 => cvar.GetPrimitiveValue<int>().ToString(),
                    ConVarType.Int64 => cvar.GetPrimitiveValue<long>().ToString(),
                    ConVarType.UInt64 => cvar.GetPrimitiveValue<ulong>().ToString(),
                    ConVarType.String => cvar.StringValue,
                    _ => "",
                };
                return convarValue;
            }
            catch (Exception ex)
            {
                Log($"[GetConvarStringValue - FATAL] Exception occurred: {ex.Message}");
                return "";
            }

        }

        public void SetConvarValue(ConVar? cvar, string value)
        {
            if (cvar == null) return;
            Dictionary<ConVarType, Action<string>> conversionMap = new()
            {
                { ConVarType.Bool, v => cvar.SetValue(int.TryParse(v, out int intValue) && intValue >= 1 || Convert.ToBoolean(v) ) },
                { ConVarType.Float32, v => cvar.SetValue(Convert.ToSingle(v)) },
                { ConVarType.Float64, v => cvar.SetValue(Convert.ToSingle(v)) },
                { ConVarType.UInt16, v => cvar.SetValue(Convert.ToUInt16(v)) },
                { ConVarType.Int16, v => cvar.SetValue(Convert.ToInt16(v)) },
                { ConVarType.UInt32, v => cvar.SetValue(Convert.ToUInt32(v)) },
                { ConVarType.Int32, v => cvar.SetValue(Convert.ToInt32(v)) },
                { ConVarType.Int64, v => cvar.SetValue(Convert.ToInt64(v)) },
                { ConVarType.UInt64, v => cvar.SetValue(Convert.ToUInt64(v)) },
                { ConVarType.String, v => cvar.SetValue(v) },
            };

            if (conversionMap.TryGetValue(cvar.Type, out var conversion))
            {
                try
                {
                    conversion(value);
                }
                catch (Exception ex)
                {
                    Log($"[SetConvarValue - FATAL] Exception occurred: {ex.Message}");
                }
            }
        }

        public void ExecuteChangedConvars()
        {
            static bool IsSafeCVarValueUnquoted(string value)
            {
                return SafeCVarValuePattern.IsMatch(value);
            }

            static string QuoteAndEscape(string value)
            {
                return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
            }

            foreach (string key in matchConfig.ChangedCvars.Keys)
            {
                string value = matchConfig.ChangedCvars[key];
                string trimmed = value.Trim();

                if (trimmed.Contains(';') || trimmed.Contains('\n') || trimmed.Contains('\r') || trimmed.Contains('`') || trimmed.Contains('|') || trimmed.Contains('&'))
                {
                    Log($"[ExecuteChangedConvars] Skipping unsafe cvar value for {key} (contains command separators).");
                    continue;
                }

                string renderedValue = IsSafeCVarValueUnquoted(trimmed) ? trimmed : QuoteAndEscape(trimmed);
                Log($"[ExecuteChangedConvars] Execing: {key} {(SecretRedactor.IsSecretKey(key) ? SecretRedactor.Hidden(trimmed) : renderedValue)}");
                Server.ExecuteCommand($"{key} {renderedValue}");
            }
        }

        private readonly MatchRestartDelayLogic matchRestartDelay = new();

        /// <summary>
        /// Puts mp_match_restart_delay back to the operator's value if HandleMatchEnd raised it
        /// for GOTV. Safe to call repeatedly; a no-op when nothing was raised.
        /// </summary>
        private void RestoreMatchRestartDelay(string reason)
        {
            var cvar = ConVar.Find("mp_match_restart_delay");
            if (cvar == null) return;
            int current = cvar.GetPrimitiveValue<int>();
            if (matchRestartDelay.Restore(current) is int original)
            {
                Log($"[RestoreMatchRestartDelay] Restoring mp_match_restart_delay from {current} to {original} ({reason}).");
                cvar.SetValue(original);
            }
        }

        public void ResetChangedConvars()
        {
            foreach (string key in matchConfig.OriginalCvars.Keys)
            {
                string value = matchConfig.OriginalCvars[key];
                Log($"[ResetChangedConvars] Execing: {key} \"{SecretRedactor.FormatValue(key, value)}\"");
                Server.ExecuteCommand($"{key} {value}");
            }
        }

        public string FormatCvarValue(string value)
        {
            string formattedTime = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
            (int team1Score, int team2Score) = GetTeamsScore();

            var formattedValue = value
                .Replace("{TIME}", formattedTime.Replace(" ", "_"))
                .Replace("{MATCH_ID}", $"{liveMatchId}")
                .Replace("{MAP}", Server.MapName)
                .Replace("{MAPNUMBER}", matchConfig.CurrentMapNumber.ToString())
                .Replace("{TEAM1}", atTeam1.teamName.Replace(" ", "_"))
                .Replace("{TEAM2}", atTeam2.teamName.Replace(" ", "_"))
                .Replace("{TEAM1_SCORE}", team1Score.ToString())
                .Replace("{TEAM2_SCORE}", team2Score.ToString());
            return formattedValue;
        }

        public void UpdateHostname()
        {
            string hostname = hostnameFormat.Value.Trim();
            if (hostname == "" || hostname == "\"\"") return;
            string formattedHostname = FormatCvarValue(hostname);
            Log($"UPDATING HOSTNAME TO: {formattedHostname}");
            Server.ExecuteCommand($"hostname {formattedHostname}");
        }

        public CCSGameRules GetGameRules()
        {
            var proxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
            if (proxy == null || proxy.GameRules == null)
            {
                Log("[GetGameRules WARN] No CCSGameRulesProxy/GameRules found – the map may not be fully initialized yet.");
                throw new InvalidOperationException("GameRules not available (no CCSGameRulesProxy/GameRules found).");
            }

            return proxy.GameRules;
        }
        
        public int GetGamePhase()
        {
            try
            {
                return GetGameRules().GamePhase;
            }
            catch (Exception e)
            {
                Log($"[GetGamePhase WARN] Failed to read GamePhase: {e.Message}");
                return -1;
            }
        }

        public bool IsHalfTimePhase()
        {
            try
            {
                return GetGamePhase() == 4;
            }
            catch (Exception e)
            {
                Log($"[IsHalfTime FATAL] An error occurred: {e.Message}");
                return false;
            }

        }

        public bool IsPostGamePhase()
        {
            try
            {
                return GetGamePhase() == 5;
            }
            catch (Exception e)
            {
                Log($"[IsPostGamePhase FATAL] An error occurred: {e.Message}");
                return false;
            }
            
        }

        public bool IsTacticalTimeoutActive()
        {
            var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First().GameRules!;

            return (gameRules.CTTimeOutActive || gameRules.TerroristTimeOutActive) && gameRules.FreezePeriod;
        }

        public (Dictionary<ulong, Dictionary<string, object>>, List<StatsPlayer>, List<StatsPlayer>) GetPlayerStatsDict()
        {
            Dictionary<ulong, Dictionary<string, object>> playerStatsDictionary = new Dictionary<ulong, Dictionary<string, object>>();
            List<StatsPlayer> playerStatsListTeam1 = new();
            List<StatsPlayer> playerStatsListTeam2 = new();
            var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First().GameRules!;
            int roundsPlayed = gameRules.TotalRoundsPlayed;
            try
            {
                foreach (int key in playerData.Keys)
                {
                    CCSPlayerController player = playerData[key];
                    if (!player.IsValid || player.ActionTrackingServices == null) continue;

                    var playerStats = player.ActionTrackingServices.MatchStats;

                    // In simulation mode, prefer the configured SteamID if available.
                    ulong steamid64 = player.SteamID;
                    string displaySteamId = steamid64.ToString();
                    string displayName = player.PlayerName;

                    if (isSimulationMode && player.UserId.HasValue &&
                        simulationPlayersByUserId.TryGetValue(player.UserId.Value, out var identity))
                    {
                        if (ulong.TryParse(identity.ConfigSteamId, out var simulatedSteamId64))
                        {
                            steamid64 = simulatedSteamId64;
                            displaySteamId = identity.ConfigSteamId;
                        }
                        displayName = identity.ConfigName;
                    }

                    // Create a nested dictionary to store individual stats for the player
                    Dictionary<string, object> stats = new Dictionary<string, object>
                    {
                        { "PlayerName", displayName },
                        { "Kills", playerStats.Kills },
                        { "Deaths", playerStats.Deaths },
                        { "Assists", playerStats.Assists },
                        { "Damage", playerStats.Damage },
                        { "Enemy2Ks", playerStats.Enemy2Ks },
                        { "Enemy3Ks", playerStats.Enemy3Ks },
                        { "Enemy4Ks", playerStats.Enemy4Ks },
                        { "Enemy5Ks", playerStats.Enemy5Ks },
                        { "EntryCount", playerStats.EntryCount },
                        { "EntryWins", playerStats.EntryWins },
                        { "1v1Count", playerStats.I1v1Count },
                        { "1v1Wins", playerStats.I1v1Wins },
                        { "1v2Count", playerStats.I1v2Count },
                        { "1v2Wins", playerStats.I1v2Wins },
                        { "UtilityCount", playerStats.Utility_Count },
                        { "UtilitySuccess", playerStats.Utility_Successes },
                        { "UtilityDamage", playerStats.UtilityDamage },
                        { "UtilityEnemies", playerStats.Utility_Enemies },
                        { "FlashCount", playerStats.Flash_Count },
                        { "FlashSuccess", playerStats.Flash_Successes },
                        { "HealthPointsRemovedTotal", playerStats.HealthPointsRemovedTotal },
                        { "HealthPointsDealtTotal", playerStats.HealthPointsDealtTotal },
                        { "ShotsFiredTotal", playerStats.ShotsFiredTotal },
                        { "ShotsOnTargetTotal", playerStats.ShotsOnTargetTotal },
                        { "EquipmentValue", playerStats.EquipmentValue },
                        { "MoneySaved", playerStats.MoneySaved },
                        { "KillReward", playerStats.KillReward },
                        { "LiveTime", playerStats.LiveTime },
                        { "HeadShotKills", playerStats.HeadShotKills },
                        { "CashEarned", playerStats.CashEarned },
                        { "EnemiesFlashed", playerStats.EnemiesFlashed }
                    };

                    string teamName = "Spectator";
                    if (player.TeamNum == 3)
                    {
                        teamName = reverseTeamSides["CT"].teamName;
                    }
                    else if (player.TeamNum == 2)
                    {
                        teamName = reverseTeamSides["TERRORIST"].teamName;
                    }

                    stats["TeamName"] = teamName;

                    playerStatsDictionary.Add(steamid64, stats);

                    // Populate PlayerStats instance
                    // Todo: Implement stats which are marked as 0 for now
                    PlayerStats playerStatsInstance = new()
                    {
                        Kills = playerStats.Kills,
                        Deaths = playerStats.Deaths,
                        Assists = playerStats.Assists,
                        FlashAssists = 0,
                        TeamKills = 0,
                        Suicides = 0,
                        Damage = playerStats.Damage,
                        UtilityDamage = playerStats.UtilityDamage,
                        EnemiesFlashed = playerStats.EnemiesFlashed,
                        FriendliesFlashed = 0,
                        KnifeKills = 0,
                        HeadshotKills = playerStats.HeadShotKills,
                        RoundsPlayed = roundsPlayed,
                        BombDefuses = 0,
                        BombPlants = 0,
                        Kills1 = 0,
                        Kills2 = playerStats.Enemy2Ks,
                        Kills3 = playerStats.Enemy3Ks,
                        Kills4 = playerStats.Enemy4Ks,
                        Kills5 = playerStats.Enemy5Ks,
                        OneV1s = playerStats.I1v1Wins,
                        OneV2s = playerStats.I1v2Wins,
                        OneV3s = 0,
                        OneV4s = 0,
                        OneV5s = 0,
                        FirstKillsT = 0,
                        FirstKillsCT = 0,
                        FirstDeathsT = 0,
                        FirstDeathsCT = 0,
                        TradeKills = 0,
                        Kast = 0,
                        Score = player.Score,
                        Mvps = player.MVPs,
                    };

                    StatsPlayer statsPlayer = new()
                    {
                        SteamId = displaySteamId,
                        Name = displayName,
                        Stats = playerStatsInstance
                    };

                    int ctTeamNum = reverseTeamSides["CT"] == atTeam1 ? 1 : 2;
                    int tTeamNum = reverseTeamSides["TERRORIST"] == atTeam1 ? 1 : 2;

                    if (player.TeamNum == 3)
                    {
                        if (ctTeamNum == 1) playerStatsListTeam1.Add(statsPlayer);
                        if (ctTeamNum == 2) playerStatsListTeam2.Add(statsPlayer);
                    }
                    else if (player.TeamNum == 2)
                    {
                        if (tTeamNum == 1) playerStatsListTeam1.Add(statsPlayer);
                        if (tTeamNum == 2) playerStatsListTeam2.Add(statsPlayer);
                    }
                }
            }
            catch (Exception e)
            {
                Log($"[GetPlayerStatsDict FATAL] An error occurred: {e.Message}");
            }

            return (playerStatsDictionary, playerStatsListTeam1, playerStatsListTeam2);
        }

        static string RemoveSpecialCharacters(string input)
        {
            Regex regex = new("[^\\p{L}0-9 _-]");
            return regex.Replace(input, "");
        }

        /// <summary>The game process's argv, read once in Load (see ServerIdentity.ReadProcessCommandLine).</summary>
        private string[]? processCommandLineArgs;

        /// <summary>True once the server has activated (first OnMapStart, or a hot reload).</summary>
        private bool serverActivated;

        /// <summary>
        /// True when Load could not identify this server from its start arguments, so loading
        /// persistent config waits for the first OnMapStart (config.cfg and hostport are applied by then).
        /// </summary>
        private bool persistentConfigLoadPending;

        private string? lastLoggedConfigScope;

        /// <summary>
        /// Resolves the identity that scopes this server's rows in the MatchZy Enhanced database.
        ///
        /// Invoked lazily by Database on the first config read or write, and again on later
        /// accesses only while the result is provisional (see ScopeResolution.IsFinal). It cannot
        /// come from the database, because it decides which rows the database hands back.
        /// </summary>
        private ScopeResolution ResolveServerConfigScope()
        {
            string? convarScope = null;
            try
            {
                convarScope = configScopeOverride?.Value;
            }
            catch { /* the convar is optional */ }

            string? convarBindIp = null;
            try
            {
                convarBindIp = ConVar.Find("ip")?.StringValue;
            }
            catch { /* the convar is optional */ }

            // Before activation hostport still holds the engine default (27015) on every server,
            // so it is only read once the server has activated.
            int? hostport = null;
            if (serverActivated)
            {
                try
                {
                    var hostPortConVar = ConVar.Find("hostport");
                    if (hostPortConVar != null)
                    {
                        try
                        {
                            hostport = hostPortConVar.GetPrimitiveValue<int>();
                        }
                        catch
                        {
                            if (int.TryParse(hostPortConVar.StringValue, out int parsed)) hostport = parsed;
                        }
                    }
                }
                catch { /* the convar is optional */ }
            }

            string? machineName = null;
            try
            {
                machineName = Environment.MachineName;
            }
            catch { /* falls back to ServerIdentity.UnknownHost */ }

            string? installPath = null;
            try
            {
                installPath = Server.GameDirectory;
            }
            catch { /* try the process path below */ }
            if (string.IsNullOrWhiteSpace(installPath))
            {
                try
                {
                    installPath = Environment.ProcessPath;
                }
                catch { /* falls back to the process id */ }
            }

            ScopeResolution resolution = ServerIdentity.Resolve(new ScopeInputs
            {
                CommandLineArgs = processCommandLineArgs,
                ConvarScope = convarScope,
                ConvarBindIp = convarBindIp,
                HostportConvar = hostport,
                ServerActivated = serverActivated,
                MachineName = machineName,
                InstallPath = installPath,
                ProcessId = Environment.ProcessId,
            });

            if (resolution.Scope != lastLoggedConfigScope)
            {
                lastLoggedConfigScope = resolution.Scope;
                string provisional = resolution.IsFinal ? "" : "; provisional until the server activates";
                Log($"[ConfigScope] Using scope '{resolution.Scope}' ({resolution.Description}{provisional})");

                if (resolution.IsFallback && resolution.IsFinal)
                {
                    string warning =
                        $"[ConfigScope] WARNING: could not identify this server for its persistent config. " +
                        $"Neither +at_config_scope nor a game port was found (install path: '{installPath ?? "unknown"}'). " +
                        $"Add '+at_config_scope <name>' to this server's start arguments so its config survives restarts and moves.";
                    Log(warning);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(warning);
                    Console.ResetColor();
                }
            }

            return resolution;
        }

        /// <summary>
        /// Loads persistent configuration from database.
        /// These values override config.cfg and survive server restarts.
        /// </summary>
        private void LoadPersistentConfig()
        {
            try
            {
                Log("[LoadPersistentConfig] Loading persistent configuration from database...");
                
                // Load remote log URL
                var remoteLogUrl = database.LoadConfigValue("at_remote_log_url");
                if (!string.IsNullOrEmpty(remoteLogUrl))
                {
                    matchConfig.RemoteLogURL = remoteLogUrl;
                    Log($"[LoadPersistentConfig] Loaded at_remote_log_url: {SecretRedactor.FormatValue("at_remote_log_url", remoteLogUrl)}");
                }
                
                // Load remote log header key
                var remoteLogHeaderKey = database.LoadConfigValue("at_remote_log_header_key");
                if (!string.IsNullOrEmpty(remoteLogHeaderKey))
                {
                    matchConfig.RemoteLogHeaderKey = remoteLogHeaderKey;
                    Log($"[LoadPersistentConfig] Loaded at_remote_log_header_key: {remoteLogHeaderKey}");
                }
                
                // Load remote log header value
                var remoteLogHeaderValue = database.LoadConfigValue("at_remote_log_header_value");
                if (!string.IsNullOrEmpty(remoteLogHeaderValue))
                {
                    matchConfig.RemoteLogHeaderValue = remoteLogHeaderValue;
                    Log($"[LoadPersistentConfig] Loaded at_remote_log_header_value {SecretRedactor.Hidden(remoteLogHeaderValue)}");
                }
                
                // Load demo upload URL
                var demoUploadUrl = database.LoadConfigValue("at_demo_upload_url");
                if (!string.IsNullOrEmpty(demoUploadUrl))
                {
                    demoUploadURL = demoUploadUrl;
                    Log($"[LoadPersistentConfig] Loaded at_demo_upload_url: {SecretRedactor.FormatValue("at_demo_upload_url", demoUploadUrl)}");
                }
                
                // Load chat prefix
                var chatPrefix = database.LoadConfigValue("at_chat_prefix");
                if (!string.IsNullOrEmpty(chatPrefix))
                {
                    this.chatPrefix = chatPrefix;
                    Log($"[LoadPersistentConfig] Loaded at_chat_prefix: {chatPrefix}");
                }
                
                // Load admin chat prefix
                var adminChatPrefix = database.LoadConfigValue("at_admin_chat_prefix");
                if (!string.IsNullOrEmpty(adminChatPrefix))
                {
                    this.adminChatPrefix = adminChatPrefix;
                    Log($"[LoadPersistentConfig] Loaded at_admin_chat_prefix: {adminChatPrefix}");
                }
                
                // Load server ID
                var serverId = database.LoadConfigValue("at_server_id");
                if (!string.IsNullOrEmpty(serverId))
                {
                    matchReportServerId.Value = serverId;
                    Log($"[LoadPersistentConfig] Loaded at_server_id: {serverId}");
                }

                // Load bootstrap URL/token (server pull-based initialization)
                var bootstrapUrl = database.LoadConfigValue("at_bootstrap_url");
                if (!string.IsNullOrEmpty(bootstrapUrl))
                {
                    this.bootstrapUrl = bootstrapUrl;
                    Log($"[LoadPersistentConfig] Loaded at_bootstrap_url: {SecretRedactor.FormatValue("at_bootstrap_url", bootstrapUrl)}");
                }

                var bootstrapToken = database.LoadConfigValue("at_bootstrap_token");
                if (!string.IsNullOrEmpty(bootstrapToken))
                {
                    this.bootstrapToken = bootstrapToken;
                    Log($"[LoadPersistentConfig] Loaded at_bootstrap_token {SecretRedactor.Hidden(bootstrapToken)}");
                }

                // Load MAT heartbeat integration settings
                var heartbeatUrl = database.LoadConfigValue("at_heartbeat_url");
                if (!string.IsNullOrEmpty(heartbeatUrl))
                {
                    this.heartbeatUrl = heartbeatUrl;
                    Log($"[LoadPersistentConfig] Loaded at_heartbeat_url: {SecretRedactor.FormatValue("at_heartbeat_url", heartbeatUrl)}");
                }

                var matchToken = database.LoadConfigValue("at_match_token");
                if (!string.IsNullOrEmpty(matchToken))
                {
                    this.matchToken = matchToken;
                    Log($"[LoadPersistentConfig] Loaded at_match_token {SecretRedactor.Hidden(matchToken)}");
                }

                var webhookUrl = database.LoadConfigValue("at_webhook_url");
                if (!string.IsNullOrEmpty(webhookUrl))
                {
                    this.webhookUrl = webhookUrl;
                    Log($"[LoadPersistentConfig] Loaded at_webhook_url: {SecretRedactor.FormatValue("at_webhook_url", webhookUrl)}");
                }

                // Load report endpoint if it was persisted by a controller
                var reportEndpoint = database.LoadConfigValue("at_report_endpoint");
                if (!string.IsNullOrEmpty(reportEndpoint))
                {
                    matchReportEndpoint.Value = reportEndpoint;
                    Log($"[LoadPersistentConfig] Loaded at_report_endpoint: {SecretRedactor.FormatValue("at_report_endpoint", reportEndpoint)}");
                }

                // Load report token if it was persisted by a controller
                var reportToken = database.LoadConfigValue("at_report_token");
                if (!string.IsNullOrEmpty(reportToken))
                {
                    matchReportToken.Value = reportToken;
                    Log($"[LoadPersistentConfig] Loaded at_report_token {SecretRedactor.Hidden(reportToken)}");
                }

                // Load optional MAT admin list integration settings
                var adminsUrl = database.LoadConfigValue("at_admins_url");
                if (!string.IsNullOrEmpty(adminsUrl))
                {
                    atAdminsUrl = adminsUrl.Trim();
                    Log($"[LoadPersistentConfig] Loaded at_admins_url: {SecretRedactor.FormatValue("at_admins_url", atAdminsUrl)}");
                }

                var adminsRefreshSecondsRaw = database.LoadConfigValue("at_admins_refresh_seconds");
                if (!string.IsNullOrEmpty(adminsRefreshSecondsRaw) &&
                    int.TryParse(adminsRefreshSecondsRaw.Trim(), out var secs) &&
                    secs >= 0)
                {
                    atAdminsRefreshSeconds = secs;
                    Log($"[LoadPersistentConfig] Loaded at_admins_refresh_seconds: {atAdminsRefreshSeconds}");
                }

                // Load server-level warmup settings (idle-only controls)
                var warmupEnableRaw = database.LoadConfigValue("at_warmup_enable");
                if (!string.IsNullOrEmpty(warmupEnableRaw))
                {
                    atWarmupEnabled = warmupEnableRaw.Trim() != "0";
                    Log($"[LoadPersistentConfig] Loaded at_warmup_enable: {(atWarmupEnabled ? 1 : 0)}");
                }
                var warmupHtml = database.LoadConfigValue("at_warmup_message_html");
                if (!string.IsNullOrEmpty(warmupHtml))
                {
                    atWarmupMessageHtml = warmupHtml;
                    Log("[LoadPersistentConfig] Loaded at_warmup_message_html (hidden)");
                }
                var warmupRespawnRaw = database.LoadConfigValue("at_warmup_respawn");
                if (!string.IsNullOrEmpty(warmupRespawnRaw))
                {
                    atWarmupRespawn = warmupRespawnRaw.Trim() != "0";
                    Log($"[LoadPersistentConfig] Loaded at_warmup_respawn: {(atWarmupRespawn ? 1 : 0)}");
                }
                var warmupIgnoreRaw = database.LoadConfigValue("at_warmup_ignore_win_conditions");
                if (!string.IsNullOrEmpty(warmupIgnoreRaw))
                {
                    atWarmupIgnoreWinConditions = warmupIgnoreRaw.Trim() != "0";
                    Log($"[LoadPersistentConfig] Loaded at_warmup_ignore_win_conditions: {(atWarmupIgnoreWinConditions ? 1 : 0)}");
                }
                var warmupRoundtimeRaw = database.LoadConfigValue("at_warmup_roundtime_minutes");
                if (!string.IsNullOrEmpty(warmupRoundtimeRaw) && float.TryParse(warmupRoundtimeRaw.Trim(), out var rt))
                {
                    atWarmupRoundtimeMinutes = Math.Clamp(rt, 1.0f, 120.0f);
                    Log($"[LoadPersistentConfig] Loaded at_warmup_roundtime_minutes: {atWarmupRoundtimeMinutes}");
                }
                var warmupStartMoneyRaw = database.LoadConfigValue("at_warmup_startmoney");
                if (!string.IsNullOrEmpty(warmupStartMoneyRaw) && int.TryParse(warmupStartMoneyRaw.Trim(), out var sm))
                {
                    atWarmupStartmoney = Math.Clamp(sm, 0, 60000);
                    Log($"[LoadPersistentConfig] Loaded at_warmup_startmoney: {atWarmupStartmoney}");
                }
                var warmupMaxMoneyRaw = database.LoadConfigValue("at_warmup_maxmoney");
                if (!string.IsNullOrEmpty(warmupMaxMoneyRaw) && int.TryParse(warmupMaxMoneyRaw.Trim(), out var mm))
                {
                    atWarmupMaxmoney = Math.Clamp(mm, 0, 60000);
                    Log($"[LoadPersistentConfig] Loaded at_warmup_maxmoney: {atWarmupMaxmoney}");
                }
                var warmupBuyRaw = database.LoadConfigValue("at_warmup_buy_anywhere");
                if (!string.IsNullOrEmpty(warmupBuyRaw))
                {
                    atWarmupBuyAnywhere = warmupBuyRaw.Trim() != "0";
                    Log($"[LoadPersistentConfig] Loaded at_warmup_buy_anywhere: {(atWarmupBuyAnywhere ? 1 : 0)}");
                }
                var warmupInfAmmoRaw = database.LoadConfigValue("at_warmup_infinite_ammo");
                if (!string.IsNullOrEmpty(warmupInfAmmoRaw))
                {
                    atWarmupInfiniteAmmo = warmupInfAmmoRaw.Trim() != "0";
                    Log($"[LoadPersistentConfig] Loaded at_warmup_infinite_ammo: {(atWarmupInfiniteAmmo ? 1 : 0)}");
                }
                
                Log("[LoadPersistentConfig] Persistent configuration loaded successfully.");

                // Start admin refresh polling if configured.
                Server.NextFrame(() =>
                {
                    StartAutoTournamentCS2AdminsRefreshTimerIfConfigured("persistent_config");
                    ApplyAutoTournamentCS2WarmupSettings("persistent_config");
                });
            }
            catch (Exception ex)
            {
                Log($"[LoadPersistentConfig] Error loading persistent config: {ex.Message}");
            }
        }

        private void Log(string message)
        {
            try
            {
                // Allow turning verbose console logging on/off via convar.
                // Falls back to logging if anything goes wrong when reading the value.
                if (debugConsoleEnabled != null && !debugConsoleEnabled.Value)
                {
                    return;
                }
            }
            catch
            {
                // Ignore and continue to log to console.
            }

            Console.WriteLine("[Auto Tournament] " + message);
        }

        private void AutoStart()
        {
            Log($"[AutoStart] autoStartMode: {autoStartMode}");
            if (autoStartMode == 0)
            {
                StartSleepMode();
            }
            if (autoStartMode == 1)
            {
                readyAvailable = true;
                isPractice = false;
                StartWarmup();
            }
            if (autoStartMode == 2)
            {
                StartPracticeMode();
            }
        }

        public int GetGameMode()
        {
            var convar = ConVar.Find("game_mode");
            if (convar != null)
            {
                return convar.GetPrimitiveValue<int>();
            }
            return -1;
        }

        public int GetGameType()
        {
            var convar = ConVar.Find("game_type");
            if (convar != null)
            {
                return convar.GetPrimitiveValue<int>();
            }
            return -1;
        }

        public void SetCorrectGameMode()
        {
            ConVar.Find("game_mode")!.SetValue(matchConfig.Wingman ? 2 : 1);
            ConVar.Find("game_type")!.SetValue(0); // Classic GameType
        }

        public bool IsMapReloadRequiredForGameMode(bool wingman)
        {
            int expectedMode = wingman ? 2 : 1;
            if (GetGameMode() != expectedMode || GetGameType() != 0)
            {
                return true;
            }
            return false;
        }

        public bool IsWingmanMode()
        {
            if (GetGameMode() == 2 && GetGameType() == 0) return true;
            return false;
        }
        public void KickPlayer(CCSPlayerController player, string? reason = null)
        {
            if (player == null || !player.IsValid)
                return;

            // In simulation mode, bots represent configured players and must not be removed
            // by generic MatchZy Enhanced logic. For regular matches, bots can still be kicked.
            if (isSimulationMode && player.IsBot)
            {
                Log($"[KickPlayer] SKIP kick for bot '{player.PlayerName}' (UserId={player.UserId}) because simulation mode is active.");
                return;
            }

            if (!player.UserId.HasValue)
                return;

            // Build kick command (reason included for server logs, even if popup doesn't show it)
            string kickCommand = $"kickid {(ushort)player.UserId.Value}";
            if (!string.IsNullOrEmpty(reason))
            {
                // Escape any existing quotes and wrap the reason in quotes
                string escapedReason = reason.Replace("\"", "\\\"");
                kickCommand += $" \"{escapedReason}\"";
                
                // Send chat message multiple times with different colors for maximum visibility
                // (since kick reason doesn't show in popup, players need to see it in chat)
                ushort userId = (ushort)player.UserId.Value;
                
                // Message 1: Immediately (Lime - very visible)
                PrintToPlayerChat(player, $"{ChatColors.Lime}{reason}{ChatColors.Default}");
                
                // Message 2: After 1 second (Green)
                AddTimer(1.0f, () =>
                {
                    if (player.IsValid && player.UserId.HasValue)
                    {
                        PrintToPlayerChat(player, $"{ChatColors.Green}{reason}{ChatColors.Default}");
                    }
                });
                
                // Message 3: After 2.5 seconds (Yellow - warning color)
                AddTimer(2.5f, () =>
                {
                    if (player.IsValid && player.UserId.HasValue)
                    {
                        PrintToPlayerChat(player, $"{ChatColors.Yellow}{reason}{ChatColors.Default}");
                    }
                });
                
                // Kick after 5 seconds - gives players plenty of time to read the messages
                AddTimer(5.0f, () =>
                {
                    if (player.IsValid && player.UserId.HasValue)
                    {
                        Server.ExecuteCommand(kickCommand);
                    }
                });
            }
            else
            {
                // No reason, kick immediately
                Server.ExecuteCommand(kickCommand);
            }

            Log($"[KickPlayer] Executing kickid for player '{player.PlayerName}' (UserId={(ushort)player.UserId.Value}, IsBot={player.IsBot}, isSimulationMode={isSimulationMode}, reason={reason ?? "none"}).");
        }

        public bool IsPlayerValid(CCSPlayerController? player)
        {
            return (
                player != null &&
                player.IsValid &&
                player.PlayerPawn.IsValid &&
                player.PlayerPawn.Value != null
            );
        }

        public static Color GetPlayerTeammateColor(CCSPlayerController playerController)
        {
            return playerController.CompTeammateColor switch
            {
                1 => Color.FromArgb(50, 255, 0),
                2 => Color.FromArgb(255, 255, 0),
                3 => Color.FromArgb(255, 132, 0),
                4 => Color.FromArgb(255, 0, 255),
                0 => Color.FromArgb(0, 187, 255),
                _ => Color.Red,
            };
        }

        public static string? GetConvarValueFromCFGFile(string filePath, string convarName)
        {
            var fileContent = File.ReadAllText(filePath);

            string pattern = @$"^{convarName}\s+(.+)$";

            Regex regex = new(pattern, RegexOptions.Multiline);

            Match match = regex.Match(fileContent);
            string? value = match.Success ? match.Groups[1].Value : null;
            return value;
        }

        public async Task UploadFileAsync(string? filePath, string fileUploadURL, string headerKey, string headerValue, long matchId, int mapNumber, int roundNumber)
        {
            if (filePath == null || fileUploadURL == "")
            {
                Log($"[UploadFileAsync] Not able to upload the file, either filePath or fileUploadURL is not set. filePath: {filePath} fileUploadURL: {fileUploadURL}");
                if (filePath != null && File.Exists(filePath))
                {
                    FileInfo fileInfo = new FileInfo(filePath);
                    Log($"[UploadFileAsync] Demo file exists locally at: {filePath} (Size: {fileInfo.Length / 1024 / 1024} MB)");
                    Log($"[UploadFileAsync] To enable upload, set at_demo_upload_url in your config.");
                }
                Log($"[DEMO_UPLOAD] SKIPPED matchId={matchId} map={mapNumber} reason=\"missing_filePath_or_uploadUrl\"");
                return;
            }

            try
            {
                // Before uploading, wait briefly for the demo file to finish writing.
                // This reduces flakiness on slower disks or larger demos.
                try
                {
                    long lastSize = -1;
                    int stableTicks = 0;
                    for (int i = 0; i < 10; i++) // up to ~10s
                    {
                        if (!File.Exists(filePath)) break;
                        long size = new FileInfo(filePath).Length;
                        if (size > 0 && size == lastSize)
                        {
                            stableTicks++;
                            if (stableTicks >= 2) break; // stable for ~2s
                        }
                        else
                        {
                            stableTicks = 0;
                            lastSize = size;
                        }
                        await Task.Delay(1000).ConfigureAwait(false);
                    }
                }
                catch { /* best-effort */ }

                using var httpClient = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(60),
                };
                Log($"[UploadFileAsync] ===== Starting demo upload =====");
                Log($"[UploadFileAsync] Upload URL: {fileUploadURL}");
                Log($"[UploadFileAsync] File path: {filePath}");
                Log($"[DEMO_UPLOAD] START matchId={matchId} map={mapNumber} round={roundNumber} url=\"{fileUploadURL}\" file=\"{Path.GetFileName(filePath)}\"");

                if (!File.Exists(filePath))
                {
                    Log($"[UploadFileAsync ERROR] File not found: {filePath}");
                    Log($"[UploadFileAsync ERROR] The demo file was not created. Check if GOTV is enabled (tv_enable 1)");
                    Log($"[DEMO_UPLOAD] FAIL matchId={matchId} map={mapNumber} reason=\"file_not_found\"");

                    // Emit demo upload failure (best-effort).
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await SendEventAsync(new AutoTournamentCS2DemoUploadFailEvent
                            {
                                MatchId = matchId,
                                MapNumber = mapNumber,
                                FileName = Path.GetFileName(filePath),
                                SizeMB = null,
                                Status = "file_not_found",
                                Reason = "file_not_found"
                            });
                            await SendEventAsync(new AutoTournamentCS2DemoUploadedEvent
                            {
                                MatchId = matchId,
                                MapNumber = mapNumber,
                                FileName = Path.GetFileName(filePath),
                                Success = false
                            });
                        }
                        catch { /* best-effort */ }
                    });
                    return;
                }

                FileInfo fileInfo = new FileInfo(filePath);
                long fileSizeBytes = fileInfo.Length;
                double fileSizeMB = fileSizeBytes / 1024.0 / 1024.0;
                Log($"[UploadFileAsync] File found. Size: {fileSizeMB:F2} MB ({fileSizeBytes} bytes)");
                Log($"[DEMO_UPLOAD] FILE_OK matchId={matchId} map={mapNumber} sizeMB={fileSizeMB:F2}");

                // Emit upload started (best-effort).
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SendEventAsync(new AutoTournamentCS2DemoUploadStartedEvent
                        {
                            MatchId = matchId,
                            MapNumber = mapNumber,
                            FileName = Path.GetFileName(filePath),
                            SizeMB = fileSizeMB
                        });
                    }
                    catch { /* best-effort */ }
                });
                Log($"[UploadFileAsync] Opening file stream for upload...");
                using FileStream fileStream = File.OpenRead(filePath);
                using StreamContent content = new(fileStream);
                content.Headers.Add("Content-Type", "application/octet-stream");

                string fileName = Path.GetFileName(filePath);
                content.Headers.Add("Auto-Tournament-FileName", fileName);
                content.Headers.Add("Auto-Tournament-MatchId", matchId.ToString());
                content.Headers.Add("Auto-Tournament-MapNumber", mapNumber.ToString());
                content.Headers.Add("Auto-Tournament-RoundNumber", roundNumber.ToString());

                // For Get5 Panel
                content.Headers.Add("Get5-FileName", fileName);
                content.Headers.Add("Get5-MatchId", matchId.ToString());
                content.Headers.Add("Get5-MapNumber", mapNumber.ToString());
                content.Headers.Add("Get5-RoundNumber", roundNumber.ToString());

                Log($"[UploadFileAsync] HTTP Headers:");
                Log($"[UploadFileAsync]   - Auto-Tournament-FileName: {fileName}");
                Log($"[UploadFileAsync]   - Auto-Tournament-MatchId: {matchId}");
                Log($"[UploadFileAsync]   - Auto-Tournament-MapNumber: {mapNumber}");
                Log($"[UploadFileAsync]   - Auto-Tournament-RoundNumber: {roundNumber}");

                if (!string.IsNullOrEmpty(headerKey) && !string.IsNullOrEmpty(headerValue))
                {
                    httpClient.DefaultRequestHeaders.Add(headerKey, headerValue);
                    Log($"[UploadFileAsync]   - Custom header: {SecretRedactor.FormatCustomHeader(headerKey, headerValue)}");
                }

                HttpResponseMessage? response = null;
                TimeSpan uploadDuration = TimeSpan.Zero;
                string? responseBody = null;

                // Retry a few times on transient failures (network/5xx/429).
                const int maxAttempts = 3;
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        Log($"[UploadFileAsync] Sending POST request to {SecretRedactor.RedactText(fileUploadURL)} (attempt {attempt}/{maxAttempts})...");
                        DateTime uploadStart = DateTime.Now;
                        response = await httpClient.PostAsync(fileUploadURL, content).ConfigureAwait(false);
                        uploadDuration = DateTime.Now - uploadStart;
                        responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        Log($"[UploadFileAsync] Upload completed in {uploadDuration.TotalSeconds:F2} seconds");
                        break;
                    }
                    catch (Exception ex) when (attempt < maxAttempts)
                    {
                        Log($"[UploadFileAsync] Upload attempt {attempt} failed: {ex.Message}");
                        int backoffMs = 1000 * attempt * attempt;
                        await Task.Delay(backoffMs).ConfigureAwait(false);

                        // Rewind stream for retry if possible.
                        try { if (fileStream.CanSeek) fileStream.Position = 0; } catch { /* ignore */ }
                    }
                }

                if (response == null)
                {
                    Log($"[UploadFileAsync] ===== Upload FAILED =====");
                    Log($"[UploadFileAsync] No response after retries");
                    Log($"[DEMO_UPLOAD] FAIL matchId={matchId} map={mapNumber} status=0 reason=\"no_response\" seconds={uploadDuration.TotalSeconds:F2}");
                    return;
                }

                if (response.IsSuccessStatusCode)
                {
                    Log($"[UploadFileAsync] ===== Upload SUCCESS =====");
                    Log($"[UploadFileAsync] Status: {response.StatusCode}");
                    Log($"[UploadFileAsync] MatchId: {matchId}, MapNumber: {mapNumber}");
                    Log($"[UploadFileAsync] FileName: {fileName}");
                    Log($"[UploadFileAsync] FileSize: {fileSizeMB:F2} MB");
                    Log($"[UploadFileAsync] Response: {SecretRedactor.RedactText(responseBody)}");
                    Log($"[UploadFileAsync] ===========================");
                    Log($"[DEMO_UPLOAD] SUCCESS matchId={matchId} map={mapNumber} sizeMB={fileSizeMB:F2} seconds={uploadDuration.TotalSeconds:F2} status={(int)response.StatusCode}");

                    // Emit success markers (best-effort).
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await SendEventAsync(new AutoTournamentCS2DemoUploadSuccessEvent
                            {
                                MatchId = matchId,
                                MapNumber = mapNumber,
                                FileName = fileName,
                                SizeMB = fileSizeMB,
                                Status = ((int)response.StatusCode).ToString()
                            });
                            await SendEventAsync(new AutoTournamentCS2DemoUploadedEvent
                            {
                                MatchId = matchId,
                                MapNumber = mapNumber,
                                FileName = fileName,
                                Success = true
                            });
                        }
                        catch { /* best-effort */ }
                    });

                    // Send success message to chat
                    Server.NextFrame(() =>
                    {
                        PrintToAllChat($"{ChatColors.Green}Demo upload succeeded{ChatColors.Default} ({fileSizeMB:F1} MB)");
                    });
                }
                else
                {
                    string errorReason = (responseBody ?? "").Length > 100 ? (responseBody ?? "").Substring(0, 100) + "..." : (responseBody ?? "");
                    if (string.IsNullOrEmpty(errorReason))
                    {
                        errorReason = $"HTTP {response.StatusCode}";
                    }
                    Log($"[UploadFileAsync] ===== Upload FAILED =====");
                    Log($"[UploadFileAsync] Status code: {response.StatusCode}");
                    Log($"[UploadFileAsync] Response body: {SecretRedactor.RedactText(responseBody)}");
                    Log($"[UploadFileAsync] MatchId: {matchId}, MapNumber: {mapNumber}");
                    Log($"[UploadFileAsync] FileName: {fileName}");
                    Log($"[UploadFileAsync] ===========================");
                    Log($"[DEMO_UPLOAD] FAIL matchId={matchId} map={mapNumber} status={(int)response.StatusCode} reason=\"{errorReason}\" seconds={uploadDuration.TotalSeconds:F2}");

                    // Emit failure markers (best-effort).
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await SendEventAsync(new AutoTournamentCS2DemoUploadFailEvent
                            {
                                MatchId = matchId,
                                MapNumber = mapNumber,
                                FileName = fileName,
                                SizeMB = fileSizeMB,
                                Status = ((int)response.StatusCode).ToString(),
                                Reason = errorReason
                            });
                            await SendEventAsync(new AutoTournamentCS2DemoUploadedEvent
                            {
                                MatchId = matchId,
                                MapNumber = mapNumber,
                                FileName = fileName,
                                Success = false
                            });
                        }
                        catch { /* best-effort */ }
                    });

                    // Send failure message to chat
                    Server.NextFrame(() =>
                    {
                        PrintToAllChat($"{ChatColors.Red}Failed to upload demo: {errorReason}{ChatColors.Default}");
                    });
                }
            }
            catch (Exception e)
            {
                string errorMessage = e.Message;
                if (errorMessage.Length > 100)
                {
                    errorMessage = errorMessage.Substring(0, 100) + "...";
                }
                Log($"[UploadFileAsync] ===== Upload FATAL ERROR =====");
                Log($"[UploadFileAsync] Exception type: {e.GetType().Name}");
                Log($"[UploadFileAsync] Error message: {e.Message}");
                if (e.InnerException != null)
                {
                    Log($"[UploadFileAsync] Inner exception: {e.InnerException.Message}");
                }
                Log($"[UploadFileAsync] Stack trace: {e.StackTrace}");
                Log($"[UploadFileAsync] ==============================");
                Log($"[DEMO_UPLOAD] FATAL matchId={matchId} map={mapNumber} error=\"{errorMessage}\"");

                // Emit fatal marker (best-effort).
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SendEventAsync(new AutoTournamentCS2DemoUploadFailEvent
                        {
                            MatchId = matchId,
                            MapNumber = mapNumber,
                            FileName = Path.GetFileName(filePath),
                            SizeMB = null,
                            Status = "exception",
                            Reason = errorMessage
                        });
                        await SendEventAsync(new AutoTournamentCS2DemoUploadedEvent
                        {
                            MatchId = matchId,
                            MapNumber = mapNumber,
                            FileName = Path.GetFileName(filePath),
                            Success = false
                        });
                    }
                    catch { /* best-effort */ }
                });

                // Send error message to chat
                Server.NextFrame(() =>
                {
                    PrintToAllChat($"{ChatColors.Red}Failed to upload demo: {errorMessage}{ChatColors.Default}");
                });
            }
        }

        public bool HandlePlayerWhitelist(CCSPlayerController player, string steamId)
        {
            // Always allow admins to bypass the MatchZy Enhanced whitelist; they may join to observe
            // or administrate matches without needing a separate whitelist entry.
            if (IsPlayerAdmin(player))
            {
                return false;
            }

            string whitelistfileName = "AutoTournamentCS2/whitelist.cfg";
            string whitelistPath = Path.Join(Server.GameDirectory + "/csgo/cfg", whitelistfileName);
            string? directoryPath = Path.GetDirectoryName(whitelistPath);
            if (directoryPath != null)
            {
                if (!Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }
            }
            if (!File.Exists(whitelistPath)) File.WriteAllLines(whitelistPath, new[] { "Steamid1", "Steamid2" });

            var whiteList = File.ReadAllLines(whitelistPath);

            if (isWhitelistRequired == true)
            {
                if (!whiteList.Contains(steamId.ToString()))
                {
                    Log($"[Whitelist] KICKING PLAYER STEAMID: {steamId}, Name: {player.PlayerName} (Not whitelisted!, IsBot={player.IsBot}, isSimulationMode={isSimulationMode})");
                    PrintToAllChat($"Kicking player {player.PlayerName} - Not whitelisted.");
                    KickPlayer(player);
                    return true;
                }
            }

            return false;
        }

        public void SwitchPlayerTeam(CCSPlayerController player, CsTeam team)
        {
            if (player.Team == team) return;

            Server.NextFrame(() =>
            {
                if (team == CsTeam.Spectator)
                {
                    player.ChangeTeam(team);
                }
                else
                {
                    player.SwitchTeam(team);
                    var gameRules = GetGameRules();
                    if (gameRules.WarmupPeriod)
                    {
                        player.Respawn();
                    }
                }
            });
        }

        public void SetPlayerInvisible(CCSPlayerController player, bool setWeaponsInvisible)
        {
            if (!IsPlayerValid(player)) return;
            var playerPawnValue = player.PlayerPawn.Value;

            if (playerPawnValue != null && playerPawnValue.IsValid)
            {
                playerPawnValue.Render = Color.FromArgb(0, 0, 0, 0);
                Utilities.SetStateChanged(playerPawnValue, "CBaseModelEntity", "m_clrRender");
            }

            if (!setWeaponsInvisible) return;

            var activeWeapon = playerPawnValue!.WeaponServices?.ActiveWeapon.Value;
            if (activeWeapon != null && activeWeapon.IsValid)
            {
                activeWeapon.Render = Color.FromArgb(0, 0, 0, 0);
                activeWeapon.ShadowStrength = 0.0f;
                Utilities.SetStateChanged(activeWeapon, "CBaseModelEntity", "m_clrRender");
            }

            var myWeapons = playerPawnValue.WeaponServices?.MyWeapons;
            if (myWeapons != null)
            {
                foreach (var gun in myWeapons)
                {
                    var weapon = gun.Value;
                    if (weapon != null)
                    {
                        weapon.Render = Color.FromArgb(0, 0, 0, 0);
                        weapon.ShadowStrength = 0.0f;
                        Utilities.SetStateChanged(weapon, "CBaseModelEntity", "m_clrRender");
                    }
                }
            }
        }

        public void SetPlayerVisible(CCSPlayerController player)
        {
            if (!IsPlayerValid(player)) return;

            var playerPawnValue = player.PlayerPawn.Value;
            if (playerPawnValue == null)
                return;

            playerPawnValue.Render = Color.FromArgb(255, 255, 255, 255);
            Utilities.SetStateChanged(playerPawnValue, "CBaseModelEntity", "m_clrRender");
        }

        public void DropWeaponByDesignerName(CCSPlayerController player, string weaponName)
        {
            if (!IsPlayerValid(player) || player.PlayerPawn.Value!.WeaponServices is null) return;
            var matchedWeapon = player.PlayerPawn.Value!.WeaponServices!.MyWeapons
                .Where(weapon => weapon.Value!.DesignerName == weaponName).FirstOrDefault();

            if (matchedWeapon != null && matchedWeapon.IsValid)
            {
                player.PlayerPawn.Value.WeaponServices.ActiveWeapon.Raw = matchedWeapon.Raw;
                player.DropActiveWeapon();
            }
        }

        public void RandomizeSpawns()
        {
            List<CCSPlayerController> players = Utilities.GetPlayers();

            Dictionary<byte, List<Position>> teamSpawns = new()
            {
                { (byte)CsTeam.CounterTerrorist, spawnsData[(byte)CsTeam.CounterTerrorist].Select(position => new Position(position)).ToList() },
                { (byte)CsTeam.Terrorist, spawnsData[(byte)CsTeam.Terrorist].Select(position => new Position(position)).ToList() }
            };

            Random random = new();

            foreach (var player in players)
            {
                if (!IsPlayerValid(player)) continue;

                if (teamSpawns[player.TeamNum].Count == 0) break;

                int randomIndex = random.Next(teamSpawns[player.TeamNum].Count);
                Position spawnPosition = teamSpawns[player.TeamNum][randomIndex];
                teamSpawns[player.TeamNum].RemoveAt(randomIndex);

                spawnPosition.Teleport(player);
            }
        }
    }
}
