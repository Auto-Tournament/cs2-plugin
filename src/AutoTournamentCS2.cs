using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;


namespace AutoTournamentCS2
{
    [MinimumApiVersion(227)]
    public partial class AutoTournamentCS2 : BasePlugin
    {

        public override string ModuleName => "MatchZy Enhanced";

        public override string ModuleVersion => "2.0.0";

        public override string ModuleAuthor => "sivert (https://github.com/sivert-io/)";

        public override string ModuleDescription => "Enhanced CS2 match management plugin optimized for tournament automation with event reliability, server tracking, and advanced player features!";

        public string chatPrefix = $"[{ChatColors.Green}Auto Tournament{ChatColors.Default}]";
        public string adminChatPrefix = $"[{ChatColors.Red}ADMIN{ChatColors.Default}]";

        // Plugin start phase data
        public bool isPractice = false;
        public bool isSleep = false;
        public bool readyAvailable = false;
        public bool matchStarted = false;
        private bool handleMatchStartInProgress = false;
        public bool isWarmup = false;
        public bool isKnifeRound = false;
        public bool isSideSelectionPhase = false;
        public bool isMatchLive = false;
        public bool isSimulationMode = false;
        
        // Auto-ready simulation mode (ready-phase testing helper)
        // Spawns a minimal set of bots during warmup so auto-ready can be tested
        // without manually joining the server.
        private bool autoReadySimulationFlowScheduled = false;
        private bool autoReadySimulationFlowStartedForMap = false;
        private readonly HashSet<int> autoReadySimulationBotUserIds = new();
        
        // Auto-ready (human) scheduling + opt-out tracking
        private readonly Dictionary<int, CounterStrikeSharp.API.Modules.Timers.Timer?> autoReadyPendingReadyTimers = new();
        private readonly HashSet<int> autoReadyOptOutUserIds = new();
        public long liveMatchId = -1;
        public int autoStartMode = 1;

        // Remote log lifecycle tracking
        // - remoteLogUrlEverConfigured: has a non-empty RemoteLogURL been configured at least once
        //   during this server session (via cvar or match JSON)?
        // - remoteLogUrlMissingWarningLogged: have we already logged that events are being
        //   skipped because RemoteLogURL is not configured? This prevents log spam while
        //   an external controller is still wiring up webhooks.
        private bool remoteLogUrlEverConfigured = false;
        private bool remoteLogUrlMissingWarningLogged = false;

        // Bootstrap init (one-shot configuration pull)
        private string bootstrapUrl = "";
        private string bootstrapToken = "";
        private long lastBootstrapAttemptAt = 0;

        // MAT heartbeat integration (server -> allocator API)
        private string heartbeatUrl = "";
        private string matchToken = "";
        private string webhookUrl = "";
        private CounterStrikeSharp.API.Modules.Timers.Timer? heartbeatTimer = null;

        // Cached CS2 build info for heartbeat (best-effort)
        private int? cachedCs2BuildId = null;
        private string? cachedCs2VersionString = null;
        private long cachedCs2BuildAt = 0;

        // Server DB health reporting state
        private bool? lastReportedDbOk = null;
        private string? lastReportedDbError = null;

        // When true, the current series is in simulation mode but the initial map change
        // is still in progress. In that case we defer spawning bots and starting the
        // simulated ready flow until the first real round start on the target map.
        private bool simulationFlowDeferred = false;
        private string? simulationTargetMap = null;

        public bool mapReloadRequired = false;

        // Pause Data
        public bool isPaused = false;
        public long pauseStartTime = 0;
        public Dictionary<string, object> unpauseData = new Dictionary<string, object> {
            { "ct", false },
            { "t", false },
            { "pauseTeam", "" }
        };

        bool isPauseCommandForTactical = false;
        
        // Enhanced pause system tracking
        public Dictionary<Team, int> pausesUsed = new();
        public CounterStrikeSharp.API.Modules.Timers.Timer? pauseTimeoutTimer = null;
        
        // Early match termination (.gg) tracking
        public HashSet<ulong> ggVotesCT = new();
        public HashSet<ulong> ggVotesT = new();
        
        // FFW (Forfeit/Walkover) tracking
        public CounterStrikeSharp.API.Modules.Timers.Timer? ffwTimer = null;
        public int ffwTeamMissing = 0; // 0 = none, 2 = T, 3 = CT
        public int ffwRemainingSeconds = 0;

        // Knife Data
        public int knifeWinner = 0;
        public string knifeWinnerName = "";

        // Players Data (including admins)
        public int connectedPlayers = 0;
        private Dictionary<int, bool> playerReadyStatus = new Dictionary<int, bool>();
        private Dictionary<int, CCSPlayerController> playerData = new Dictionary<int, CCSPlayerController>();
        private readonly Dictionary<ulong, long> playerConnectionTimes = new();
        private readonly object matchReportUploadLock = new();
        private bool matchReportUploadScheduled = false;

        // Admin Data
        private Dictionary<string, string> loadedAdmins = new Dictionary<string, string>();

        // Timers
        public CounterStrikeSharp.API.Modules.Timers.Timer? unreadyPlayerMessageTimer = null;
        public CounterStrikeSharp.API.Modules.Timers.Timer? sideSelectionMessageTimer = null;
        public CounterStrikeSharp.API.Modules.Timers.Timer? pausedStateTimer = null;
        public CounterStrikeSharp.API.Modules.Timers.Timer? sideSelectionTimer = null;
        public CounterStrikeSharp.API.Modules.Timers.Timer? countdownDisplayTimer = null;
        private Dictionary<string, CounterStrikeSharp.API.Modules.Timers.Timer?> activeNotificationTimers = new();
        public CounterStrikeSharp.API.Modules.Timers.Timer? sideSelectionReminderTimer = null;
        public int sideSelectionRemainingSeconds = 0;

        // Each message is kept in chat display for ~13 seconds, hence setting default chat timer to 13 seconds.
        // Configurable using at_chat_messages_timer_delay <seconds>
        public int chatTimerDelay = 13;

        // Game Config
        public bool isKnifeRequired = true;
        public int minimumReadyRequired = 0; // Number of ready players required start the match. If set to 0, all connected players have to ready-up to start the match.
        public bool isWhitelistRequired = false;
        public bool isSaveNadesAsGlobalEnabled = false;

        public bool isPlayOutEnabled = false;

        public bool playerHasTakenDamage = false;

        // User command - action map
        public Dictionary<string, Action<CCSPlayerController?, CommandInfo?>>? commandActions;

        // SQLite/MySQL Database 
        private Database database = new();

        /// <summary>
        /// Moves the config folder and SQLite file of an install older than 2.0.0 to their new names.
        /// The tables and saved settings are carried over when the database opens. Failures are
        /// logged and never stop the plugin from loading.
        /// </summary>
        private void CarryOverLegacyInstall()
        {
            static void CarryOverLog(string message) => Console.WriteLine("[Auto Tournament] " + message);

            try
            {
                LegacyCarryOver.CarryOverConfigFolder(Path.Combine(Server.GameDirectory, "csgo", "cfg"), CarryOverLog);
            }
            catch (Exception ex)
            {
                CarryOverLog($"[CarryOver] ERROR moving the old config folder: {ex.Message}");
            }

            try
            {
                LegacyCarryOver.CarryOverSqliteFile(ModuleDirectory, CarryOverLog);
            }
            catch (Exception ex)
            {
                CarryOverLog($"[CarryOver] ERROR renaming the old SQLite database: {ex.Message}");
            }
        }

        public override void Load(bool hotReload)
        {
            // Before anything reads cfg/AutoTournamentCS2/ or opens the database: carry an install
            // from before the rename over to the new names, once.
            CarryOverLegacyInstall();

            LoadAdmins();

            database.InitializeDatabase(ModuleDirectory);

            // This sets default config ConVars
            Server.ExecuteCommand("execifexists AutoTournamentCS2/config.cfg");

            // Persistent config is scoped per server so several servers can share one database.
            // The scope is resolved lazily on first use. Its start arguments are read from
            // /proc/self/cmdline: Environment.GetCommandLineArgs() does not carry the game's argv
            // when .NET is hosted inside cs2, which is why 1.4.26 resolved every server to <host>:27015.
            var (commandLineArgs, commandLineSource) = ServerIdentity.ReadProcessCommandLine();
            processCommandLineArgs = commandLineArgs;
            serverActivated = hotReload;
            database.ScopeProvider = ResolveServerConfigScope;

            // Load persistent configuration from database (overrides config.cfg if values exist).
            // When the start arguments identify this server (+at_config_scope or -port) that
            // is safe now. Otherwise the scope depends on config.cfg or hostport, neither of which
            // the engine has applied yet during Load, so wait for the first OnMapStart.
            if (hotReload || ServerIdentity.HasCommandLineIdentity(processCommandLineArgs))
            {
                LoadPersistentConfig();
            }
            else
            {
                persistentConfigLoadPending = true;
                Log($"[ConfigScope] No +at_config_scope or -port in the start arguments (read from {commandLineSource}); loading persistent config after the server activates.");
            }

            // Start event retry background process
            StartEventRetryTimer();

            // Bootstrap init: if a bootstrap URL/token is configured, fetch the single payload
            // and apply it locally (reduces RCON command churn from controllers).
            AddTimer(2.0f, () =>
            {
                TryBootstrapFetch("startup");
            });

            // MAT heartbeat init: if a heartbeat URL is configured, periodically POST heartbeat snapshots.
            // This is MAT's source of truth for server online/configured/allocatable state.
            AddTimer(3.0f, () =>
            {
                StartMatHeartbeatTimerIfConfigured();
            });
            
            // Send server_configured event on startup if webhook is configured
            // Delay slightly to ensure server is fully initialized
            AddTimer(5.0f, () =>
            {
                if (!string.IsNullOrEmpty(matchConfig.RemoteLogURL))
                {
                    SendServerConfiguredEvent("Startup");
                    // Also send an initial health snapshot once the server is reachable.
                    SendServerHealthEvent("startup");
                }
            });

            // DB health reporting:
            // - send periodic snapshots
            // - send immediately on detected changes (polled at a shorter interval)
            AddTimer(60.0f, () =>
            {
                if (!string.IsNullOrEmpty(matchConfig.RemoteLogURL))
                {
                    SendServerHealthEvent("periodic");
                }
            }, TimerFlags.REPEAT);

            AddTimer(10.0f, () =>
            {
                try
                {
                    if (string.IsNullOrEmpty(matchConfig.RemoteLogURL)) return;
                    if (string.IsNullOrEmpty(matchReportServerId.Value)) return;

                    var (ok, _dbType, error) = database.CheckHealth();
                    var changed =
                        !lastReportedDbOk.HasValue ||
                        lastReportedDbOk.Value != ok ||
                        (ok == false && (lastReportedDbError ?? "") != (error ?? ""));

                    if (changed)
                    {
                        lastReportedDbOk = ok;
                        lastReportedDbError = error;
                        SendServerHealthEvent("change");
                    }
                }
                catch
                {
                    // Best-effort only
                }
            }, TimerFlags.REPEAT);

            teamSides[atTeam1] = "CT";
            teamSides[atTeam2] = "TERRORIST";
            reverseTeamSides["CT"] = atTeam1;
            reverseTeamSides["TERRORIST"] = atTeam2;

            if (!hotReload)
            {
                AutoStart();
            }
            else
            {
                // Pluign should not be reloaded while a match is live (this would messup with the match flags which were set)
                // Only hot-reload the plugin if you are testing something and don't want to restart the server time and again.
                UpdatePlayersMap();
                AutoStart();
            }

            // Initialize the match-safe auto-updater (Steam UpToDateCheck) that will never
            // restart the server while a MatchZy Enhanced match is in progress.
            InitializeAutoTournamentCS2SafeAutoUpdater();

            commandActions = new Dictionary<string, Action<CCSPlayerController?, CommandInfo?>> {
                { ".ready", OnPlayerReady },
                { ".r", OnPlayerReady },
                { ".forceready", OnForceReadyCommandCommand },
                { ".unready", OnPlayerUnReady },
                { ".notready", OnPlayerUnReady },
                { ".ur", OnPlayerUnReady },
                { ".stay", OnTeamStay },
                { ".switch", OnTeamSwitch },
                { ".swap", OnTeamSwitch },
                { ".tech", OnTechCommand },
                { ".p", OnPauseCommand },
                { ".pause", OnPauseCommand },
                { ".unpause", OnUnpauseCommand },
                { ".up", OnUnpauseCommand },
                { ".forcepause", OnForcePauseCommand },
                { ".fp", OnForcePauseCommand },
                { ".forceunpause", OnForceUnpauseCommand },
                { ".fup", OnForceUnpauseCommand },
                { ".tac", OnTacCommand },
                { ".roundknife", OnKnifeCommand },
                { ".rk", OnKnifeCommand },
                { ".playout", OnPlayoutCommand },
                { ".start", OnStartCommand },
                { ".force", OnStartCommand },
                { ".forcestart", OnStartCommand },
                { ".restart", OnRestartMatchCommand },
                { ".rr", OnRestartMatchCommand },
                { ".endmatch", OnEndMatchCommand },
                { ".forceend", OnEndMatchCommand },
                { ".reloadmap", OnMapReloadCommand },
                { ".settings", OnMatchSettingsCommand },
                { ".whitelist", OnWLCommand },
                { ".globalnades", OnSaveNadesAsGlobalCommand },
                { ".reload_admins", OnReloadAdmins },
                { ".reload_config", OnReloadConfig },
                { ".tactics", OnPracCommand },
                { ".prac", OnPracCommand },
                { ".showspawns", OnShowSpawnsCommand },
                { ".hidespawns", OnHideSpawnsCommand },
                { ".dryrun", OnDryRunCommand },
                { ".dry", OnDryRunCommand },
                { ".noflash", OnNoFlashCommand },
                { ".noblind", OnNoFlashCommand },
                { ".break", OnBreakCommand },
                { ".bot", OnBotCommand },
                { ".cbot", OnCrouchBotCommand },
                { ".crouchbot", OnCrouchBotCommand },
                { ".boost", OnBoostBotCommand },
                { ".crouchboost", OnCrouchBoostBotCommand },
                { ".nobots", OnNoBotsCommand },
                { ".solid", OnSolidCommand },
                { ".impacts", OnImpactsCommand },
                { ".traj", OnTrajCommand },
                { ".pip", OnTrajCommand },
                { ".god", OnGodCommand },
                { ".ff", OnFastForwardCommand },
                { ".fastforward", OnFastForwardCommand },
                { ".clear", OnClearCommand },
                { ".match", OnMatchCommand },
                { ".uncoach", OnUnCoachCommand },
                { ".exitprac", OnMatchCommand },
                { ".stop", OnStopCommand },
                { ".help", OnHelpCommand },
                { ".t", OnTCommand },
                { ".ct", OnCTCommand },
                { ".spec", OnSpecCommand },
                { ".fas", OnFASCommand },
                { ".watchme", OnFASCommand },
                { ".last", OnLastCommand },
                { ".throw", OnRethrowCommand },
                { ".rethrow", OnRethrowCommand },
                { ".rt", OnRethrowCommand },
                { ".throwsmoke", OnRethrowSmokeCommand },
                { ".rethrowsmoke", OnRethrowSmokeCommand },
                { ".thrownade", OnRethrowGrenadeCommand },
                { ".rethrownade", OnRethrowGrenadeCommand },
                { ".rethrowgrenade", OnRethrowGrenadeCommand },
                { ".throwgrenade", OnRethrowGrenadeCommand },
                { ".rethrowflash", OnRethrowFlashCommand },
                { ".throwflash", OnRethrowFlashCommand },
                { ".rethrowdecoy", OnRethrowDecoyCommand },
                { ".throwdecoy", OnRethrowDecoyCommand },
                { ".throwmolotov", OnRethrowMolotovCommand },
                { ".rethrowmolotov", OnRethrowMolotovCommand },
                { ".timer", OnTimerCommand },
                { ".lastindex", OnLastIndexCommand },
                { ".bestspawn", OnBestSpawnCommand },
                { ".worstspawn", OnWorstSpawnCommand },
                { ".bestctspawn", OnBestCTSpawnCommand },
                { ".worstctspawn", OnWorstCTSpawnCommand },
                { ".besttspawn", OnBestTSpawnCommand },
                { ".worsttspawn", OnWorstTSpawnCommand },
                { ".savepos", OnSavePosCommand},
                { ".loadpos", OnLoadPosCommand},
                { ".version", OnAutoTournamentCS2VersionCommand},
                { ".atversion", OnAutoTournamentCS2VersionCommand},
                { ".te", OnTestEventCommand},
                { ".testevent", OnTestEventCommand},
                { ".gg", OnGGCommand}
            };

            RegisterEventHandler<EventPlayerConnectFull>(EventPlayerConnectFullHandler);
            RegisterEventHandler<EventPlayerDisconnect>(EventPlayerDisconnectHandler);
            RegisterEventHandler<EventCsWinPanelRound>(EventCsWinPanelRoundHandler, hookMode: HookMode.Pre);
            RegisterEventHandler<EventCsWinPanelMatch>(EventCsWinPanelMatchHandler);
            RegisterEventHandler<EventRoundStart>(EventRoundStartHandler);
            RegisterEventHandler<EventRoundFreezeEnd>(EventRoundFreezeEndHandler);
            RegisterEventHandler<EventPlayerGivenC4>(EventPlayerGivenC4);
            RegisterEventHandler<EventPlayerDeath>(EventPlayerDeathPreHandler, hookMode: HookMode.Pre);
            RegisterListener<Listeners.OnClientDisconnectPost>(playerSlot =>
            {
                // May not be required, but just to be on safe side so that player data is properly updated in dictionaries
                // Update: Commenting the below function as it was being called multiple times on map change.
                // UpdatePlayersMap();
            });
            RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawnedHandler);
            RegisterEventHandler<EventPlayerTeam>((@event, info) =>
            {
                CCSPlayerController? player = @event.Userid;
                if (!IsPlayerValid(player)) return HookResult.Continue;

                if (atTeam1.coach.Contains(player!) || atTeam2.coach.Contains(player!))
                {
                    @event.Silent = true;
                    return HookResult.Changed;
                }
                return HookResult.Continue;
            }, HookMode.Pre);

            RegisterEventHandler<EventPlayerTeam>((@event, info) =>
            {
                CCSPlayerController? player = @event.Userid;

                if (!IsPlayerValid(player)) return HookResult.Continue;

                if (player!.IsHLTV || player.IsBot)
                {
                    return HookResult.Continue;
                }

                // Only handle team switching logic if in match setup
                if (isMatchSetup)
                {
                    CsTeam playerTeam = GetPlayerTeam(player);
                    SwitchPlayerTeam(player, playerTeam);
                }

                if (readyAvailable && !matchStarted && player.UserId.HasValue)
                {
                    int teamNum = @event.Team;
                    int oldTeam = @event.Oldteam;
                    Log($"[EventPlayerTeam] Player {player.PlayerName} (UserId={player.UserId.Value}) team {teamNum} (old={oldTeam}, player.TeamNum={player.TeamNum}), isMatchSetup={isMatchSetup}");

                    if (!playerData.ContainsKey(player.UserId.Value))
                    {
                        playerData[player.UserId.Value] = player;
                        Log($"[EventPlayerTeam] Added player {player.PlayerName} to playerData");
                    }

                    bool onCtOrT = teamNum == (int)CsTeam.CounterTerrorist || teamNum == (int)CsTeam.Terrorist;
                    if (onCtOrT && oldTeam != teamNum)
                    {
                        ResetPlayerWarmupReadyAndAutoReady(player);
                        HandleClanTags();
                        CheckLiveRequired();
                    }

                    if (autoReadyEnabled.Value && onCtOrT)
                    {
                        AddTimer(autoReadyCheckDelay.Value, () =>
                        {
                            if (player.IsValid && player.UserId.HasValue && playerData.ContainsKey(player.UserId.Value))
                            {
                                int currentTeamNum = player.TeamNum;
                                Log($"[EventPlayerTeam] Auto-ready timer for {player.PlayerName}, TeamNum={currentTeamNum}");
                                if (currentTeamNum == (int)CsTeam.CounterTerrorist || currentTeamNum == (int)CsTeam.Terrorist)
                                {
                                    CheckAndAutoReadyPlayers();
                                }
                            }
                        });
                    }
                }

                return HookResult.Continue;
            });

            AddCommandListener("jointeam", (player, info) =>
            {
                if (isMatchSetup && player != null && player.IsValid)
                {
                    if (int.TryParse(info.ArgByIndex(1), out int joiningTeam))
                    {
                        int playerTeam = (int)GetPlayerTeam(player);
                        if (joiningTeam != playerTeam)
                        {
                            return HookResult.Stop;
                        }
                    }
                }
                return HookResult.Continue;
            });

            AddCommandListener("noclip", OnConsoleNoClip); // Override noclip
            // MAT "end warmup" sends a raw mp_warmup_end; route it through the start path in simulation.
            AddCommandListener("mp_warmup_end", OnWarmupEndCommand);

            RegisterEventHandler<EventRoundEnd>((@event, info) =>
            {
                if (!isKnifeRound) return HookResult.Continue;

                DetermineKnifeWinner();
                @event.Winner = knifeWinner;
                int finalEvent = 10;
                if (knifeWinner == 3)
                {
                    finalEvent = 8;
                }
                else if (knifeWinner == 2)
                {
                    finalEvent = 9;
                }
                @event.Reason = finalEvent;
                isSideSelectionPhase = true;
                isKnifeRound = false;

                // Send knife_round_ended event
                string winnerTeam = knifeWinner == 3 ?
                    (reverseTeamSides.ContainsKey("CT") ? (reverseTeamSides["CT"] == atTeam1 ? "team1" : "team2") : "none") :
                    (reverseTeamSides.ContainsKey("TERRORIST") ? (reverseTeamSides["TERRORIST"] == atTeam1 ? "team1" : "team2") : "none");

                Log($"[EventRoundEnd] Knife round ended, sending knife_round_ended event - winner: {winnerTeam}");

                var knifeEndedEvent = new AutoTournamentCS2KnifeRoundEndedEvent
                {
                    MatchId = liveMatchId,
                    MapNumber = matchConfig.CurrentMapNumber,
                    Winner = winnerTeam
                };

                Task.Run(async () =>
                {
                    await SendEventAsync(knifeEndedEvent);
                });

                StartAfterKnifeWarmup();

                return HookResult.Changed;
            }, HookMode.Pre);

            RegisterEventHandler<EventRoundEnd>((@event, info) =>
            {
                try
                {
                    if (isDryRun)
                    {
                        StartPracticeMode();
                        isDryRun = false;
                        return HookResult.Continue;
                    }
                    if (!isMatchLive) return HookResult.Continue;
                    HandlePostRoundEndEvent(@event);
                    return HookResult.Continue;
                }
                catch (Exception e)
                {
                    Log($"[EventRoundEnd FATAL] An error occurred: {e.Message}");
                    return HookResult.Continue;
                }

            }, HookMode.Post);

            // RegisterEventHandler<EventMapShutdown>((@event, info) => {
            //     Log($"[EventMapShutdown] Resetting match!");
            //     ResetMatch();
            //     return HookResult.Continue;
            // });

            RegisterListener<Listeners.OnMapEnd>(() =>
            {
                MapTransitionBreadcrumb($"OnMapEnd: {Server.MapName}");

                // The 10 Hz practice !timer display reads a cached controller and its pawn.
                // Controllers from the ending map must not be touched on the next one, so
                // stop those timers here (they are also STOP_ON_MAPCHANGE).
                foreach (var practiceTimer in playerTimers.Values)
                {
                    practiceTimer.KillTimer();
                }
                playerTimers.Clear();
            });

            RegisterListener<Listeners.OnMapStart>(mapName =>
            {
                MapTransitionBreadcrumb($"OnMapStart: {mapName}");
                serverActivated = true;
                workshopMaps.OnMapStarted(mapName);
                if (persistentConfigLoadPending)
                {
                    persistentConfigLoadPending = false;
                    LoadPersistentConfig();

                    // The startup timers in Load may already have run without these values.
                    AddTimer(2.0f, () => TryBootstrapFetch("startup"));
                    AddTimer(3.0f, () => StartMatHeartbeatTimerIfConfigured());
                    AddTimer(5.0f, () =>
                    {
                        if (!string.IsNullOrEmpty(matchConfig.RemoteLogURL))
                        {
                            SendServerConfiguredEvent("Startup");
                            SendServerHealthEvent("startup");
                        }
                    });
                }

                AddTimer(1.0f, () =>
                {
                    MapTransitionBreadcrumb($"OnMapStart+1s: {Server.MapName}");
                    if (!isMatchSetup)
                    {
                        // A map change without a server restart keeps the convars. With no
                        // match loaded, publish idle (clearing any stale match id) before the
                        // autostart mode sets its own state; sleep/practice publish nothing.
                        if (!TournamentStatusLogic.HasActiveMatch(isMatchSetup, tournamentStatus.Value, null))
                        {
                            UpdateTournamentStatus(TournamentStatusLogic.Idle);
                        }
                        AutoStart();
                        return;
                    }

                    if (isWarmup)
                    {
                        StartWarmup();
                    }

                    if (isPractice)
                    {
                        StartPracticeMode();
                    }

                    // For simulation mode in multi-map series (bo3/bo5, etc.),
                    // ensure bots are (re)spawned and the simulated ready flow
                    // is started again on each subsequent map.
                    //
                    // IMPORTANT:
                    // - The first map in a series (CurrentMapNumber == 0) is initialized
                    //   from LoadMatchFromJSON via the deferred EventRoundStart path.
                    // - To avoid double-initializing simulation on the first map (which
                    //   would spawn duplicate bots), we only run this block for maps
                    //   beyond the first (CurrentMapNumber >= 1).
                    if (isSimulationMode && matchConfig != null && matchConfig.CurrentMapNumber >= 1)
                    {
                        // Reset per-map simulation orchestration state so the ready flow
                        // can run again cleanly for the new map.
                        simulationReadyFlowScheduled = false;
                        simulationFlowStarted = false;
                        simulationPlayersByUserId.Clear();
                        assignedSimulationSteamIds.Clear();

                        Log($"[SimulationMode] OnMapStart detected multi-map simulation on map index {matchConfig.CurrentMapNumber} ({Server.MapName}). Starting per-map simulation flow.");
                        MaybeStartSimulationFlow();
                    }
                });
            });

            // RegisterListener<Listeners.OnMapEnd>(() => {
            //     Log($"[Listeners.OnMapEnd] Resetting match!");
            //     ResetMatch();
            // });

            RegisterEventHandler<EventPlayerDeath>((@event, info) =>
            {
                // Setting money back to 16000 when a player dies in warmup
                var player = @event.Userid;
                if (!isWarmup) return HookResult.Continue;
                if (!IsPlayerValid(player)) return HookResult.Continue;
                if (player!.InGameMoneyServices != null) player.InGameMoneyServices.Account = 16000;
                return HookResult.Continue;
            });

            RegisterEventHandler<EventPlayerHurt>((@event, info) =>
            {
                CCSPlayerController? attacker = @event.Attacker;
                CCSPlayerController? victim = @event.Userid;

                if (!IsPlayerValid(attacker) || !IsPlayerValid(victim)) return HookResult.Continue;

                if (isPractice && victim!.IsBot)
                {
                    int damage = @event.DmgHealth;
                    int postDamageHealth = @event.Health;
                    PrintToPlayerChat(attacker!, Localizer["at.pracc.damage", damage, victim.PlayerName, postDamageHealth]);
                    return HookResult.Continue;
                }

                if (!attacker!.IsValid || attacker.IsBot && !(@event.DmgHealth > 0 || @event.DmgArmor > 0))
                    return HookResult.Continue;
                if (matchStarted && victim!.TeamNum != attacker.TeamNum)
                {
                    int targetId = (int)victim.UserId!;
                    UpdatePlayerDamageInfo(@event, targetId);
                    if (attacker != victim) playerHasTakenDamage = true;
                }

                return HookResult.Continue;
            });

            RegisterEventHandler<EventPlayerChat>((@event, info) =>
            {

                int currentVersion = Api.GetVersion();
                int index = @event.Userid + 1;
                var playerUserId = NativeAPI.GetUseridFromIndex(index);

                var originalMessage = @event.Text.Trim();
                var message = @event.Text.Trim().ToLower();

                var parts = originalMessage.Split(' ');
                var messageCommand = parts.Length > 0 ? parts[0] : string.Empty;
                var messageCommandArg = parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : string.Empty;

                CCSPlayerController? player = null;
                if (playerData.TryGetValue(playerUserId, out CCSPlayerController? value) && value != null && value.IsValid)
                {
                    player = value;
                }

                if (player == null)
                {
                    // Somehow we did not had the player in playerData (or only a stale controller), hence updating the maps again before getting the player
                    UpdatePlayersMap();
                    if (!playerData.TryGetValue(playerUserId, out player) || player == null || !player.IsValid)
                    {
                        // Still not tracked (e.g. the refresh skipped them). Resolve the live controller
                        // instead of throwing: a KeyNotFoundException here silently dropped chat
                        // commands such as .ready.
                        player = Utilities.GetPlayerFromUserid(playerUserId);
                    }
                    if (player == null || !player.IsValid)
                    {
                        Log($"[EventPlayerChat] Could not resolve a controller for UserId={playerUserId}; ignoring \"{message}\".");
                        return HookResult.Continue;
                    }
                }

                // Handling player commands
                if (commandActions.ContainsKey(message))
                {
                    commandActions[message](player, null);
                }

                if (message.StartsWith(".map"))
                {
                    HandleMapChangeCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".readyrequired"))
                {
                    HandleReadyRequiredCommand(player, messageCommandArg);
                }

                if (message.StartsWith(".restore"))
                {
                    HandleRestoreCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".asay"))
                {
                    if (IsPlayerAdmin(player, "css_asay", "@css/chat"))
                    {
                        if (messageCommandArg != "")
                        {
                            Server.PrintToChatAll($"{adminChatPrefix} {messageCommandArg}");
                        }
                        else
                        {
                            // ReplyToUserCommand(player, "Usage: .asay <message>");
                            ReplyToUserCommand(player, Localizer["at.cc.usage", ".asay <message>"]);
                        }
                    }
                    else
                    {
                        SendPlayerNotAdminMessage(player);
                    }
                }
                if (message.StartsWith(".savenade") || message.StartsWith(".sn"))
                {
                    HandleSaveNadeCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".delnade") || message.StartsWith(".dn"))
                {
                    HandleDeleteNadeCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".deletenade"))
                {
                    HandleDeleteNadeCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".importnade") || message.StartsWith(".in"))
                {
                    HandleImportNadeCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".listnades") || message.StartsWith(".lin"))
                {
                    HandleListNadesCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".loadnade") || message.StartsWith(".ln"))
                {
                    HandleLoadNadeCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".spawn"))
                {
                    HandleSpawnCommand(player, messageCommandArg, player.TeamNum, "spawn");
                }
                if (message.StartsWith(".ctspawn") || message.StartsWith(".cts"))
                {
                    HandleSpawnCommand(player, messageCommandArg, (byte)CsTeam.CounterTerrorist, "ctspawn");
                }
                if (message.StartsWith(".tspawn") || message.StartsWith(".ts"))
                {
                    HandleSpawnCommand(player, messageCommandArg, (byte)CsTeam.Terrorist, "tspawn");
                }
                if (message.StartsWith(".team1"))
                {
                    HandleTeamNameChangeCommand(player, messageCommandArg, 1);
                }
                if (message.StartsWith(".team2"))
                {
                    HandleTeamNameChangeCommand(player, messageCommandArg, 2);
                }
                if (message.StartsWith(".rcon"))
                {
                    if (IsPlayerAdmin(player, "css_rcon", "@css/rcon"))
                    {
                        Server.ExecuteCommand(messageCommandArg);
                        ReplyToUserCommand(player, "Command sent successfully!");
                    }
                    else
                    {
                        SendPlayerNotAdminMessage(player);
                    }
                }
                if (message.StartsWith(".coach"))
                {
                    HandleCoachCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".back"))
                {
                    HandleBackCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".delay"))
                {
                    HandleDelayCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".throwindex"))
                {
                    HandleThrowIndexCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".throwidx"))
                {
                    HandleThrowIndexCommand(player, messageCommandArg);
                }

                return HookResult.Continue;
            });

            RegisterEventHandler<EventPlayerBlind>((@event, info) =>
            {
                CCSPlayerController? player = @event.Userid;
                CCSPlayerController? attacker = @event.Attacker;
                if (!isPractice) return HookResult.Continue;

                if (!IsPlayerValid(player) || !IsPlayerValid(attacker)) return HookResult.Continue;

                if (attacker!.IsValid)
                {
                    double roundedBlindDuration = Math.Round(@event.BlindDuration, 2);
                    PrintToPlayerChat(attacker, Localizer["at.pracc.blind", player!.PlayerName, roundedBlindDuration]);
                }
                var userId = player!.UserId;
                if (userId != null && noFlashList.Contains((int)userId))
                {
                    Server.NextFrame(() => KillFlashEffect(player));
                }

                return HookResult.Continue;
            });

            RegisterEventHandler<EventSmokegrenadeDetonate>(EventSmokegrenadeDetonateHandler);
            RegisterEventHandler<EventFlashbangDetonate>(EventFlashbangDetonateHandler);
            RegisterEventHandler<EventHegrenadeDetonate>(EventHegrenadeDetonateHandler);
            RegisterEventHandler<EventMolotovDetonate>(EventMolotovDetonateHandler);
            RegisterEventHandler<EventDecoyStarted>(EventDecoyDetonateHandler);

            Console.WriteLine($"[{ModuleName} v{ModuleVersion} LOADED] by {ModuleAuthor}");
        }
    }
}
