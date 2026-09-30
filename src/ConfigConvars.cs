using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;


namespace AutoTournamentCS2
{
    public partial class AutoTournamentCS2
    {

        // Identity used to scope this server's rows in the MatchZy Enhanced database, so several servers
        // can share one database without overwriting each other's persistent config.
        // Leave empty to derive it from the bind address and game port (see ServerIdentity).
        // This must be set in config.cfg / server.cfg and is deliberately never persisted to the
        // database: a value that decides the scope cannot itself be read from a scoped row.
        public FakeConVar<string> configScopeOverride = new("at_config_scope", "Overrides the identity used to scope this server's persistent config in a shared database. Empty (default) derives it from bind address and game port. Prefer the start argument +at_config_scope <name>, which wins over this convar. Never persisted.", "");

        public FakeConVar<bool> smokeColorEnabled = new("at_smoke_color_enabled", "Whether player-specific smoke color is enabled or not. Default: false", false);
        public FakeConVar<bool> techPauseEnabled = new("at_enable_tech_pause", "Whether .tech command is enabled or not. Default: true", true);
        public FakeConVar<string> techPausePermission  = new("at_tech_pause_flag", "Flag required to use tech pause", "");
        public FakeConVar<int> techPauseDuration  = new("at_tech_pause_duration", "Tech pause duration in seconds. Default value: 300", 300);

        public FakeConVar<int> maxTechPausesAllowed  = new("at_max_tech_pauses_allowed", " Max tech pauses allowed. Default value: 2", 2);

        public FakeConVar<bool> everyoneIsAdmin = new("at_everyone_is_admin", "If set to true, all the players will have admin privilege. Default: false", false);

        public FakeConVar<bool> showCreditsOnMatchStart = new("at_show_credits_on_match_start", "Whether to show the upstream credit line on match start. Default: true", true);

        public FakeConVar<bool> debugChatEnabled = new("at_debug_chat", "Whether to show debug/event logs in in-game chat (e.g. event send status, warmup_end, player_connect). Default: false", false);

        [ConsoleCommand("at_debug_chat_get", "Get current value of at_debug_chat (0/1).")]
        public void AutoTournamentCS2DebugChatGet(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            command.ReplyToCommand($"at_debug_chat = {(debugChatEnabled.Value ? 1 : 0)}");
        }

        // Console debug logging
        public FakeConVar<bool> debugConsoleEnabled = new("at_debug_console", "Whether to write verbose debug logs to the server console. Default: true", true);

        // Crash / transition breadcrumbs (writes checkpoints to console + a file)
        // Useful when diagnosing CS2 segfaults during phase transitions (warmup -> knife/live)
        // and map changes (map change issued, OnMapEnd, OnMapStart, with player/timer counts and memory).
        public FakeConVar<bool> crashDebugBreadcrumbs = new("at_crash_debug_breadcrumbs", "When enabled, writes phase-transition and map-change breadcrumbs to cfg/AutoTournamentCS2/logs/at_breadcrumbs.log to help diagnose crashes. Default: false", false);
        
        // match-safe CS2 update checks (Steam UpToDateCheck)
        public FakeConVar<bool> safeAutoUpdaterEnabled = new(
            "at_safeautoupdater_enabled",
            "When enabled, periodically checks Steam UpToDateCheck and emits update markers/events. Shutdown behavior is controlled by at_safeautoupdater_action. Default: true",
            true
        );
        public FakeConVar<string> safeAutoUpdaterAction = new(
            "at_safeautoupdater_action",
            "What to do when a CS2 update is detected. warn_only = emit markers/events only; restart = kick players + quit when MatchZy Enhanced is idle/postgame/error. Default: warn_only",
            "warn_only"
        );
        public FakeConVar<int> safeAutoUpdaterOfflineBackoffSeconds = new(
            "at_safeautoupdater_offline_backoff_seconds",
            "When Steam update checks fail due to DNS/network (offline servers), MatchZy Enhanced will wait this many seconds before trying again. Default: 1800 (30 minutes)",
            1800
        );

        // Event/Webhook sending master switch (diagnostics)
        public FakeConVar<bool> eventsEnabled = new("at_events_enabled", "Master switch for MatchZy Enhanced event/webhook sending and retry queue processing. Default: true", true);

        // Center HTML notifications
        public FakeConVar<bool> centerHtmlNotifications = new("at_center_html_notifications", "Whether to show important notifications in the center of the screen (match live, pause, etc). Default: false", false);
        public FakeConVar<int> notificationDurationGlobal = new("at_notification_duration_global", "Default duration in seconds for global center HTML notifications. Default: 5", 5);
        public FakeConVar<float> notificationDurationPlayer = new("at_notification_duration_player", "Default duration in seconds for player-specific center HTML notifications. Default: 6", 6.0f);

        public FakeConVar<string> hostnameFormat = new(
            "at_hostname_format",
            "The server hostname to use. Set to \"\" to disable/use existing. Default: {TEAM1} vs {TEAM2}",
            "{TEAM1} vs {TEAM2}"
        );

        public FakeConVar<bool> enableDamageReport = new("at_enable_damage_report", "Whether to show damage report after each round or not. Default: true", true);

        public FakeConVar<bool> stopCommandNoDamage = new("at_stop_command_no_damage", "Whether the stop command becomes unavailable if a player damages a player from the opposing team.", false);

        // Auto-Ready System
        public FakeConVar<bool> autoReadyEnabled = new("at_autoready_enabled", "Whether players are automatically marked as ready when they join. Default: false", false);
        public FakeConVar<int> autoReadyStartDelay = new("at_autoready_start_delay", "Delay in seconds before match starts when auto-ready triggers (countdown). Default: 5", 5);
        public FakeConVar<float> autoReadyCheckDelay = new("at_autoready_check_delay", "Delay in seconds after player joins team before checking auto-ready status. Default: 0.3", 0.3f);
        public FakeConVar<float> autoReadyPlayerReadyDelay = new("at_autoready_ready_delay", "Delay in seconds before auto-ready simulates a player typing .ready once teams are eligible. Default: 2", 2.0f);
        
        // Auto-Ready Simulation (testing helper)
        // When enabled, MatchZy Enhanced will spawn two bots (1 CT, 1 T) during the ready/warmup phase
        // so you can test auto-ready and ready gating without manually joining the server.
        public FakeConVar<bool> autoReadySimulationEnabled = new("at_autoready_simulation_enabled", "When enabled, spawns 2 bots for ready-mode testing (1 CT + 1 T) and counts them as players. Default: false", false);
        public FakeConVar<float> autoReadySimulationBotSpawnDelay = new("at_autoready_simulation_bot_spawn_delay", "Delay in seconds between spawning the two ready-simulation bots. Default: 5", 5.0f);
        public FakeConVar<bool> autoReadySimulationAllowStartWithoutHumans = new("at_autoready_simulation_allow_start_without_humans", "When enabled, allows starting knife/live with 0 humans connected while auto-ready simulation is active (may crash CS2). Default: false", false);
        public FakeConVar<bool> autoReadySimulationKnifeUseSafeMode = new(
            "at_autoready_simulation_knife_use_safe_mode",
            "When enabled and starting knife with 0 humans, uses the legacy 'safe/diagnostic' knife transition modes instead of execing knife.cfg (for crash isolation). Default: false",
            false
        );
        public FakeConVar<int> autoReadySimulationKnifeStartMode = new(
            "at_autoready_simulation_knife_start_mode",
            "Knife transition command sequence when starting with 0 humans (safe mode only). 0=restart_then_warmup_end(same cmd), 1=warmup_end_then_restart(delayed), 2=restart_then_warmup_end(delayed), 3=warmup_end_only, 4=restart_only, 5=step_through_knife_cfg(one command at a time), 6=enter_knife_no_commands. Default: 0",
            0
        );
        public FakeConVar<float> autoReadySimulationKnifeStartCommandDelay = new(
            "at_autoready_simulation_knife_start_command_delay",
            "Delay in seconds between knife transition commands when starting with 0 humans (safe mode only). Default: 0",
            0.0f
        );
        public FakeConVar<int> autoReadySimulationKnifeStartMaxSteps = new(
            "at_autoready_simulation_knife_start_max_steps",
            "When knife_start_mode=5, maximum number of knife.cfg commands to execute (0 = all). Default: 0",
            0
        );
        public FakeConVar<int> autoReadySimulationKnifeStepStartIndex = new(
            "at_autoready_simulation_knife_step_start_index",
            "When knife_start_mode=5, 0-based command index to start executing from within knife.cfg (after filtering comments/empties). Default: 0",
            0
        );
        public FakeConVar<bool> autoReadySimulationSkipAsyncEvents = new(
            "at_autoready_simulation_skip_async_events",
            "When enabled, suppresses Task.Run event sends during bots-only simulation knife/live transitions (for crash isolation). Default: false",
            false
        );

        // Enhanced Pause System
        public FakeConVar<bool> bothTeamsUnpauseRequired = new("at_both_teams_unpause_required", "Whether both teams must type .unpause to resume (only for non-admin pauses). Default: true", true);
        public FakeConVar<int> maxPausesPerTeam = new("at_max_pauses_per_team", "Maximum number of pauses allowed per team. Set to 0 for unlimited. Default: 0", 0);
        public FakeConVar<int> pauseDuration = new("at_pause_duration", "Maximum pause duration in seconds. Set to 0 for unlimited. Default: 0", 0);

        // Side Selection System
        public FakeConVar<bool> sideSelectionEnabled = new("at_side_selection_enabled", "Whether side selection commands (.ct/.t/.stay/.swap) are enabled after knife round. Default: true", true);
        public FakeConVar<int> sideSelectionTime = new("at_side_selection_time", "Time in seconds for side selection after knife round. Default: 60", 60);
        public FakeConVar<float> sideSelectionReminderInterval = new("at_side_selection_reminder_interval", "Interval in seconds between side selection reminder messages. Default: 10", 10.0f);

        // Early Match Termination (.gg)
        public FakeConVar<bool> ggEnabled = new("at_gg_enabled", "Whether .gg command for early match termination is enabled. Default: false", false);
        public FakeConVar<float> ggThreshold = new("at_gg_threshold", "Percentage (0.0-1.0) of team required to type .gg for match to end. Default: 0.8 (80%)", 0.8f);
        public FakeConVar<int> ggMinScoreDiff = new("at_gg_min_score_diff", "Minimum score difference required for the losing team to use .gg. Set to 0 to disable. Example: 6 means a team can only .gg if they are down by at least 6 rounds (e.g. 0-6, 1-7, etc.). Default: 0", 0);

        // FFW (Forfeit/Walkover) System
        public FakeConVar<bool> ffwEnabled = new("at_ffw_enabled", "Whether automatic forfeit system is enabled when a team leaves. Default: false", false);
        public FakeConVar<int> ffwTime = new("at_ffw_time", "Time in seconds before forfeit is declared when a team is missing. Default: 240 (4 minutes)", 240);
        public FakeConVar<float> ffwCheckInterval = new("at_ffw_check_interval", "Interval in seconds between FFW timer checks and warning messages. Default: 10", 10.0f);

        public FakeConVar<string> matchStartMessage = new("at_match_start_message", "Message to show when the match starts. Use $$$ to break message into multiple lines. Set to \"\" to disable.", "");

        // Tournament Status ConVars
        public FakeConVar<string> tournamentStatus = new("at_tournament_status", "Current status of the server (idle/loading/warmup/knife/playing/paused/halftime/postgame/error)", "idle");
        public FakeConVar<string> tournamentMatch = new("at_tournament_match", "Match slug/identifier currently loaded on this server", "");
        public FakeConVar<string> tournamentUpdated = new("at_tournament_updated", "Unix timestamp of last tournament status update", "0");
        public FakeConVar<string> tournamentNextMatch = new("at_tournament_next_match", "Next match slug/identifier queued for this server", "");
        public FakeConVar<string> tournamentGoLiveStatus = new("at_tournament_go_live_status", "Status string published when match goes live. Always uses playing for API compatibility.", "playing");

        // Match report upload
        public FakeConVar<string> matchReportEndpoint = new("at_report_endpoint", "HTTP endpoint for match report uploads (https://host/api/events/report)", "");
        public FakeConVar<string> matchReportServerId = new("at_report_server_id", "Server identifier to send with match report uploads", "");
        public FakeConVar<string> matchReportToken = new("at_report_token", "Authentication token for match report uploads (sent as x-auto-tournament-token)", "");

        // Series End Configuration
        public FakeConVar<int> seriesEndKickDelayNoDemo = new("at_series_end_kick_delay_no_demo", "Additional delay in seconds before kicking players when series ends and demo recording is disabled. Default: 5", 5);
        public FakeConVar<int> seriesEndKickDelayDemoNoUpload = new("at_series_end_kick_delay_demo_no_upload", "Additional delay in seconds before kicking players when series ends, demo recording is enabled but no upload URL. Default: 10", 10);
        public FakeConVar<int> seriesEndKickDelayDemoUpload = new("at_series_end_kick_delay_demo_upload", "Additional delay in seconds before kicking players when series ends, demo recording is enabled with upload URL. Default: 60", 60);

        // Event System Configuration
        public FakeConVar<float> eventRetryInterval = new("at_event_retry_interval", "Interval in seconds between retry attempts for failed event uploads. Default: 30", 30.0f);
        public FakeConVar<float> eventCleanupInterval = new("at_event_cleanup_interval", "Interval in seconds between cleanup of old events from database. Default: 3600 (1 hour)", 3600.0f);
        
        [ConsoleCommand("at_server_id", "Server identifier for event tracking and match reports")]
        public void AutoTournamentCS2ServerId(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string id = command.ArgByIndex(1);
            
            if (string.IsNullOrWhiteSpace(id))
            {
                Log($"[AutoTournamentCS2ServerId] Server ID is required. Usage: at_server_id <server_id>");
                return;
            }
            
            id = id.Trim();
            matchReportServerId.Value = id;
            Log($"[AutoTournamentCS2ServerId] Server ID set to: {id}");
            
            // Persist to database so it survives server restarts
            database.SaveConfigValue("at_server_id", id);
            
            // If remote log URL is already configured, send server_configured event now
            // This handles the case where server_id is set after remote log URL
            if (!string.IsNullOrEmpty(matchConfig.RemoteLogURL))
            {
                SendServerConfiguredEvent("Console");
            }
        }

        [ConsoleCommand("at_whitelist_enabled_default", "Whether Whitelist is enabled by default or not. Default value: false")]
        public void AutoTournamentCS2WLConvar(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string args = command.ArgString;

            isWhitelistRequired = bool.TryParse(args, out bool isWhitelistRequiredValue) ? isWhitelistRequiredValue : args != "0" && isWhitelistRequired;
        }
        
        [ConsoleCommand("at_knife_enabled_default", "Whether knife round is enabled by default or not. Default value: true")]
        public void AutoTournamentCS2KnifeConvar(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            // No-args call acts as a "get"
            if (command.ArgCount <= 1)
            {
                command.ReplyToCommand($"at_knife_enabled_default = {(isKnifeRequired ? 1 : 0)}");
                return;
            }
            string args = command.ArgString;

            isKnifeRequired = bool.TryParse(args, out bool isKnifeRequiredValue) ? isKnifeRequiredValue : args != "0" && isKnifeRequired;
            command.ReplyToCommand($"at_knife_enabled_default = {(isKnifeRequired ? 1 : 0)}");
        }

        [ConsoleCommand("at_playout_enabled_default", "Whether playout (playing out all rounds and disabling overtime/early clinch) is enabled by default or not. Default value: false")]
        public void AutoTournamentCS2PlayoutConvar(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string args = command.ArgString;

            isPlayOutEnabled = bool.TryParse(args, out bool isPlayOutEnabledValue) ? isPlayOutEnabledValue : args != "0" && isPlayOutEnabled;
        }

        [ConsoleCommand("at_save_nades_as_global_enabled", "Whether nades should be saved globally instead of being privated to players by default or not. Default value: false")]
        public void AutoTournamentCS2SaveNadesAsGlobalConvar(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string args = command.ArgString;

            isSaveNadesAsGlobalEnabled = bool.TryParse(args, out bool isSaveNadesAsGlobalEnabledValue) ? isSaveNadesAsGlobalEnabledValue : args != "0" && isSaveNadesAsGlobalEnabled;
        }

        [ConsoleCommand("at_kick_when_no_match_loaded", "Whether to kick all clients and prevent anyone from joining the server if no match is loaded. Default value: false")]
        public void AutoTournamentCS2MatchModeOnlyConvar(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string args = command.ArgString;

            matchModeOnly = bool.TryParse(args, out bool matchModeOnlyValue) ? matchModeOnlyValue : args != "0" && matchModeOnly;
        }

        [ConsoleCommand("at_reset_cvars_on_series_end", "Whether parameters from the cvars section of a match configuration are restored to their original values when a series ends. Default value: true")]
        public void AutoTournamentCS2ResetCvarsOnSeriesEndConvar(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string args = command.ArgString;

            resetCvarsOnSeriesEnd = bool.TryParse(args, out bool resetCvarsOnSeriesEndValue) ? resetCvarsOnSeriesEndValue : args != "0" && resetCvarsOnSeriesEnd;
        }

        [ConsoleCommand("at_minimum_ready_required", "Minimum ready players required to start the match. 0 = everyone connected must ready. Default: 0")]
        public void AutoTournamentCS2MinimumReadyRequired(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            // Since there is already a console command for this purpose, we will use the same.   
            OnReadyRequiredCommand(player, command);
        }

        [ConsoleCommand("at_demo_path", "Path of folder in which demos will be saved. If defined, it must not start with a slash and must end with a slash. Set to empty string to use the csgo root.")]
        public void AutoTournamentCS2DemoPath(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            if (command.ArgCount == 2)
            {
                string path = command.ArgByIndex(1);
                if (path[0] == '/' || path[0] == '.' || path[^1] != '/' || path.Contains("//"))
                {
                    Log($"at_demo_path must end with a slash and must not start with a slash or dot. It will be reset to an empty string! Current value: {demoPath}");
                }
                else
                {
                    demoPath = path;
                }
            }
        }

        [ConsoleCommand("at_demo_name_format", "Format of demo filname")]
        public void AutoTournamentCS2DemoNameFormat(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            if (command.ArgCount == 2)
            {
                string format = command.ArgByIndex(1).Trim();

                if (!string.IsNullOrEmpty(format)) 
                {
                    demoNameFormat = format;
                }
            }
        }

        [ConsoleCommand("at_demo_recording_enabled", "Whether to automatically start demo recording when the match goes live. Default value: true")]
        public void AutoTournamentCS2DemoRecordingEnabled(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string args = command.ArgString;

            isDemoRecordingEnabled = bool.TryParse(args, out bool isDemoRecordingEnabledValue) ? isDemoRecordingEnabledValue : args != "0" && isDemoRecordingEnabled;
        }

        [ConsoleCommand("get5_demo_upload_url", "If defined, recorded demos will be uploaded to this URL once the map ends.")]
        [ConsoleCommand("at_demo_upload_url", "If defined, recorded demos will be uploaded to this URL once the map ends.")]
        public void AutoTournamentCS2DemoUploadURL(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string url = command.ArgByIndex(1);
            
            // Remove quotes if present
            url = url.Trim().Trim('"').Trim('\'');
            
            // If URL is empty and it was set dynamically, don't overwrite it
            if (url == "" && demoUploadURLSetDynamically)
            {
                Log($"[AutoTournamentCS2DemoUploadURL] Ignoring empty URL from config - demoUploadURL was set dynamically and will not be overwritten. Current value: {demoUploadURL}");
                return;
            }
            
            // If URL is empty, just return (initial load from config with empty value)
            if (url == "")
            {
                Log($"[AutoTournamentCS2DemoUploadURL] Demo upload URL not configured (empty). Set at_demo_upload_url to enable automatic uploads.");
                return;
            }
            
            if (!IsValidUrl(url))
            {
                Log($"[AutoTournamentCS2DemoUploadURL] Invalid URL: {url}. Please provide a valid URL for uploading the demo!");
                return;
            }
            
            // Check if we're overwriting a dynamically set value
            if (demoUploadURLSetDynamically && demoUploadURL != url)
            {
                Log($"[AutoTournamentCS2DemoUploadURL] WARNING: Overwriting dynamically set demoUploadURL. Old: {demoUploadURL}, New: {url}");
            }
            
            demoUploadURL = url;
            
            // Mark as dynamically set if it's a valid non-empty URL
            // This prevents config.cfg from overwriting it later
            if (url != "")
            {
                demoUploadURLSetDynamically = true;
                Log($"[AutoTournamentCS2DemoUploadURL] Demo upload URL set to: {url}");
                
                // Persist to database so it survives server restarts
                database.SaveConfigValue("at_demo_upload_url", url);
                Log($"[AutoTournamentCS2DemoUploadURL] Demo upload URL persisted to database.");
            }
        }

        [ConsoleCommand("at_stop_command_available", "Whether .stop command is enabled or not (to restore the current round). Default value: true")]
        public void AutoTournamentCS2StopCommandEnabled(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string args = command.ArgString;

            isStopCommandAvailable = bool.TryParse(args, out bool isStopCommandAvailableValue) ? isStopCommandAvailableValue : args != "0" && isStopCommandAvailable;
        }

        [ConsoleCommand("at_use_pause_command_for_tactical_pause", "Whether to use !pause/.pause command for tactical pause or normal pause (unpauses only when both teams use unpause command, for admin force-unpauses the game). Default value: false")]
        public void AutoTournamentCS2PauseForTacticalCommand(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string args = command.ArgString;

            isPauseCommandForTactical = bool.TryParse(args, out bool isPauseCommandForTacticalValue) ? isPauseCommandForTacticalValue : args != "0" && isPauseCommandForTactical;
        }

        [ConsoleCommand("at_pause_after_restore", "Whether to pause the match after a round is restored. Default value: true")]
        public void AutoTournamentCS2PauseAfterStopEnabled(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string args = command.ArgString;

            pauseAfterRoundRestore = bool.TryParse(args, out bool pauseAfterRoundRestoreValue) ? pauseAfterRoundRestoreValue : args != "0" && pauseAfterRoundRestore;
        }

        [ConsoleCommand("at_chat_prefix", "Default value of chat prefix for MatchZy Enhanced messages. Default value: [{Green}Auto Tournament{Default}]")]
        public void AutoTournamentCS2ChatPrefix(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;

            // No-args call acts as a "get"
            if (command.ArgCount <= 1)
            {
                command.ReplyToCommand($"at_chat_prefix = {chatPrefix}");
                return;
            }

            string args = command.ArgString.Trim();
            // When executed from cfg or RCON, string args are commonly wrapped in quotes.
            // Strip surrounding quotes so we don't print literal quotes in chat
            // and so color tokens at the start (e.g. "{Green}...") are handled correctly.
            args = args.Trim().Trim('"').Trim('\'').Trim();

            if (string.IsNullOrEmpty(args))
            {
                chatPrefix = $"[{ChatColors.Green}Auto Tournament{ChatColors.Default}]";
                command.ReplyToCommand($"at_chat_prefix = {chatPrefix}");
                return;
            }

            args = GetColorTreatedString(args);

            chatPrefix = args;

            Log($"[AutoTournamentCS2ChatPrefix] chatPrefix: {chatPrefix}");
            command.ReplyToCommand($"at_chat_prefix = {chatPrefix}");
            
            // Persist to database so it survives server restarts
            database.SaveConfigValue("at_chat_prefix", chatPrefix);
        }

        [ConsoleCommand("at_admin_chat_prefix", "Chat prefix to show whenever an admin sends message using .asay <message>. Default value: [{Red}ADMIN{Default}]")]
        public void AutoTournamentCS2AdminChatPrefix(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;

            // No-args call acts as a "get"
            if (command.ArgCount <= 1)
            {
                command.ReplyToCommand($"at_admin_chat_prefix = {adminChatPrefix}");
                return;
            }

            string args = command.ArgString.Trim();
            // When executed from cfg or RCON, string args are commonly wrapped in quotes.
            // Strip surrounding quotes so we don't print literal quotes in chat.
            args = args.Trim().Trim('"').Trim('\'').Trim();

            if (string.IsNullOrEmpty(args))
            {
                adminChatPrefix = $"[{ChatColors.Red}ADMIN{ChatColors.Default}]";
                command.ReplyToCommand($"at_admin_chat_prefix = {adminChatPrefix}");
                return;
            }

            args = GetColorTreatedString(args);

            adminChatPrefix = args;

            Log($"[AutoTournamentCS2AdminChatPrefix] adminChatPrefix: {adminChatPrefix}");
            command.ReplyToCommand($"at_admin_chat_prefix = {adminChatPrefix}");
            
            // Persist to database so it survives server restarts
            database.SaveConfigValue("at_admin_chat_prefix", adminChatPrefix);
        }

        [ConsoleCommand("at_chat_messages_timer_delay", "Number of seconds of delay before sending reminder messages from MatchZy Enhanced (like unready message, paused message, etc). Default: 13")]
        public void AutoTournamentCS2ChatMessagesTimerDelay(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;

            if (command.ArgCount >= 2)
            {
                string commandArg = command.ArgByIndex(1);
                if (!string.IsNullOrWhiteSpace(commandArg))
                {
                    if (int.TryParse(commandArg, out int chatTimerDelayValue) && chatTimerDelayValue >= 0)
                    {
                        chatTimerDelay = chatTimerDelayValue;
                    }
                    else
                    {
                        // ReplyToUserCommand(player, $"Invalid value for at_chat_messages_timer_delay. Please specify a valid non-negative number.");
                        ReplyToUserCommand(player, Localizer["at.cvars.invalidvalue"]);
                    }
                }
            } else if (command.ArgCount == 1) {
                ReplyToUserCommand(player, $"at_chat_messages_timer_delay = {chatTimerDelay}");
            }
        }

        [ConsoleCommand("at_autostart_mode", "Whether the plugin will load the match mode, the practice moder or neither by startup. 0 for neither, 1 for match mode, 2 for practice mode. Default: 1")]
        public void AutoTournamentCS2AutoStartConvar(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string args = command.ArgString;

            if (int.TryParse(args, out int autoStartModeValue))
            {
                autoStartMode = autoStartModeValue;
            }

        }

        [ConsoleCommand("at_allow_force_ready", "Whether force ready using !forceready is enabled or not (Currently works in Match Setup only). Default value: True")]
        [ConsoleCommand("get5_allow_force_ready", "Whether force ready using !forceready is enabled or not (Currently works in Match Setup only). Default value: True")]
        public void AutoTournamentCS2AllowForceReadyConvar(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string args = command.ArgString;

            allowForceReady = bool.TryParse(args, out bool allowForceReadyValue) ? allowForceReadyValue : args != "0" && allowForceReady;
        }

        [ConsoleCommand("at_max_saved_last_grenades", "Maximum number of grenade history that may be saved per-map, per-client. Set to 0 to disable. Default value: 512")]
        public void AutoTournamentCS2MaxSavedLastGrenadesConvar(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string args = command.ArgString;

            if (int.TryParse(args, out int maxLastGrenadesSavedLimitValue))
            {
                maxLastGrenadesSavedLimit = maxLastGrenadesSavedLimitValue;
            }
            else
            {
                // command.ReplyToCommand("Usage: at_max_saved_last_grenades <number>");
                ReplyToUserCommand(player, Localizer["at.cc.usage", $"at_max_saved_last_grenades <number>"]);
            }
        }

        [ConsoleCommand("get5_remote_backup_url", "A URL to send backup files to over HTTP. Leave empty to disable.")]
        [ConsoleCommand("at_remote_backup_url", "A URL to send backup files to over HTTP. Leave empty to disable.")]
        [CommandHelper(minArgs: 1, usage: "<remote_backup_upload_url>")]
        public void AutoTournamentCS2BackupUploadURL(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string url = command.ArgByIndex(1);
            if (url.Trim() == "") return;
            if (!IsValidUrl(url))
            {
                Log($"[AutoTournamentCS2BackupUploadURL] Invalid URL: {url}. Please provide a valid URL for uploading the backup!");
                return;
            }
            backupUploadURL = url;
        }

        [ConsoleCommand("get5_remote_backup_header_key", "If defined, a custom HTTP header with this name is added to the backup HTTP request.")]
        [ConsoleCommand("at_remote_backup_header_key", "If defined, a custom HTTP header with this name is added to the backup HTTP request.")]
        [CommandHelper(minArgs: 1, usage: "<remote_backup_header_key>")]
        public void BackupUploadHeaderKeyCommand(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string header = command.ArgByIndex(1).Trim();

            if (header != "") backupUploadHeaderKey = header;
        }

        [ConsoleCommand("get5_remote_backup_header_value", "If defined, the value of the custom header added to the backup HTTP request.")]
        [ConsoleCommand("at_remote_backup_header_value", "If defined, the value of the custom header added to the backup HTTP request.")]
        [CommandHelper(minArgs: 1, usage: "<remote_backup_header_value>")]
        public void BackupUploadHeaderValueCommand(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string headerValue = command.ArgByIndex(1).Trim();

            if (headerValue != "") backupUploadHeaderValue = headerValue;
        }

    }
}
