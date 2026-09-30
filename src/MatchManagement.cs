using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using Newtonsoft.Json.Linq;


namespace AutoTournamentCS2
{

    public partial class AutoTournamentCS2
    {
        public MatchConfig matchConfig = new();

        public bool isMatchSetup = false;

        // When a loadmatch_url request is issued while a match is still in postgame,
        // we queue the next match here and auto-load it after ResetMatch() completes.
        private bool isMatchQueued = false;
        private string? queuedMatchUrl;
        private string? queuedMatchHeaderName;
        private string? queuedMatchHeaderValue;
        private string? queuedMatchIdentifier;

        public bool matchModeOnly = false;

        public bool resetCvarsOnSeriesEnd = true;

        public string loadedConfigFile = "";

        public Team atTeam1 = new()
        {
            teamName = "COUNTER-TERRORISTS"
        };
        public Team atTeam2 = new()
        {
            teamName = "TERRORISTS"
        };

        public Dictionary<Team, string> teamSides = new();
        public Dictionary<string, Team> reverseTeamSides = new();

        [ConsoleCommand("css_team1", "Sets team name for team1")]
        public void OnTeam1Command(CCSPlayerController? player, CommandInfo command)
        {
            HandleTeamNameChangeCommand(player, command.ArgString, 1);
        }

        [ConsoleCommand("css_team2", "Sets team name for team2")]
        public void OnTeam2Command(CCSPlayerController? player, CommandInfo command)
        {
            HandleTeamNameChangeCommand(player, command.ArgString, 2);
        }

        [ConsoleCommand("at_loadmatch", "Loads a match from the given JSON file path (relative to the csgo/ directory)")]
        public void LoadMatch(CCSPlayerController? player, CommandInfo command)
        {
            try
            {
                if (player != null) return;
                if (isMatchSetup)
                {
                    // command.ReplyToCommand($"[LoadMatch] A match is already setup with id: {liveMatchId}, cannot load a new match!");
                    ReplyToUserCommand(player, Localizer["at.mm.matchisalreadysetup", liveMatchId]);
                    Log($"[LoadMatch] A match is already setup with id: {liveMatchId}, cannot load a new match!");
                    return;
                }
                string fileName = command.ArgString;
                string filePath = Path.Join(Server.GameDirectory + "/csgo", fileName);
                if (!File.Exists(filePath))
                {
                    // command.ReplyToCommand($"[LoadMatch] Provided file does not exist! Usage: at_loadmatch <filename>");
                    ReplyToUserCommand(player, Localizer["at.mm.filedoesntexist"]);
                    Log($"[LoadMatch] Provided file does not exist! Usage: at_loadmatch <filename>");
                    return;
                }
                string jsonData = File.ReadAllText(filePath);
                bool success = LoadMatchFromJSON(jsonData);
                if (!success)
                {
                    // command.ReplyToCommand("Match load failed! Resetting current match");
                    ReplyToUserCommand(player, Localizer["at.mm.matchloadfailed"]);
                    UpdateTournamentStatus("error");
                    ResetMatch();
                }
                loadedConfigFile = fileName;
            }
            catch (Exception e)
            {
                Log($"[LoadMatch - FATAL] An error occured: {e.Message}");
                UpdateTournamentStatus("error");
                return;
            }
        }

        [ConsoleCommand("get5_loadmatch_url", "Loads a match from the given URL")]
        [ConsoleCommand("at_loadmatch_url", "Loads a match from the given URL")]
        public void LoadMatchFromURL(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string url = command.ArgByIndex(1);

            string headerName = command.ArgCount > 3 ? command.ArgByIndex(2) : "";
            string headerValue = command.ArgCount > 3 ? command.ArgByIndex(3) : "";

            // If a match is already setup, allow queuing the next match only once the current
            // series has reached the postgame phase. The queued match will be auto-loaded
            // from ResetMatch() after demo upload and cleanup finishes.
            if (isMatchSetup)
            {
                string currentStatus = tournamentStatus.Value ?? string.Empty;
                if (CanQueueMatchLoad(currentStatus))
                {
                    QueueMatchLoad(player, "LoadMatchDataCommand", url, headerName, headerValue);
                }
                else
                {
                    // command.ReplyToCommand($"[LoadMatchDataCommand] A match is already setup with id: {liveMatchId}, cannot load a new match!");
                    ReplyToUserCommand(player, Localizer["at.mm.get5matchisalreadysetup", liveMatchId]);
                    Log($"[LoadMatchDataCommand] A match is already setup with id: {liveMatchId}, cannot load a new match! (status={currentStatus})");
                }
                return;
            }

            Log($"[LoadMatchDataCommand] Match setup request received with URL: {SecretRedactor.RedactText(url)} header: {SecretRedactor.FormatCustomHeader(headerName, headerValue)}");

            if (!IsValidUrl(url))
            {
                // command.ReplyToCommand($"[LoadMatchDataCommand] Invalid URL: {url}. Please provide a valid URL to load the match!");
                ReplyToUserCommand(player, Localizer["at.mm.invalidurl", url]);
                Log($"[LoadMatchDataCommand] Invalid URL: {url}. Please provide a valid URL to load the match!");
                return;
            }
            try
            {
                HttpClient httpClient = new();
                if (headerName != "")
                {
                    httpClient.DefaultRequestHeaders.Add(headerName, headerValue);
                }
                HttpResponseMessage response = httpClient.GetAsync(url).Result;

                if (response.IsSuccessStatusCode)
                {
                    string jsonData = response.Content.ReadAsStringAsync().Result;
                    Log($"[LoadMatchFromURL] Received following data: {SecretRedactor.RedactText(jsonData)}");

                    bool success = LoadMatchFromJSON(jsonData);
                    if (!success)
                    {
                        // command.ReplyToCommand("Match load failed! Resetting current match");
                        ReplyToUserCommand(player, Localizer["at.mm.matchloadfailed"]);
                        UpdateTournamentStatus("error");
                        ResetMatch();
                    }
                    loadedConfigFile = url;
                }
                else
                {
                    // command.ReplyToCommand($"[LoadMatchFromURL] HTTP request failed with status code: {response.StatusCode}");
                    ReplyToUserCommand(player, Localizer["at.mm.httprequestfailed", response.StatusCode]);
                    UpdateTournamentStatus("error");
                    Log($"[LoadMatchFromURL] HTTP request failed with status code: {response.StatusCode}");
                }
            }
            catch (Exception e)
            {
                Log($"[LoadMatchFromURL - FATAL] An error occured: {e.Message}");
                UpdateTournamentStatus("error");
                return;
            }
        }

        /// <summary>
        /// A load request can be queued while the current series is in postgame, or
        /// replace an already queued one.
        /// </summary>
        private static bool CanQueueMatchLoad(string currentStatus)
        {
            return string.Equals(currentStatus, "postgame", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(currentStatus, "queued", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Queues a match config URL to load once the current series has been reset.
        /// The reply carries "queued_match=&lt;identifier&gt;" (the config file name
        /// without extension, e.g. r2m1) so the caller can tell from the RCON reply that
        /// the load was queued rather than executed.
        /// </summary>
        private void QueueMatchLoad(CCSPlayerController? player, string source, string url, string? headerName, string? headerValue)
        {
            string identifier = DeriveIdentifierFromUrlOrPath(url) ?? url;
            string? replaced = isMatchQueued ? queuedMatchIdentifier : null;

            queuedMatchUrl = url;
            queuedMatchHeaderName = headerName;
            queuedMatchHeaderValue = headerValue;
            queuedMatchIdentifier = identifier;
            isMatchQueued = true;

            // Surface this state to the allocator / UI so it knows a new match is lined up.
            UpdateTournamentStatus("queued");
            tournamentNextMatch.Value = identifier;

            string replacedText = replaced != null ? $" (replaced queued match {replaced})" : "";
            string message = $"[{source}] Current match {liveMatchId} is finishing. Queued next match {identifier} from URL: {url} to load after reset{replacedText}. queued_match={identifier}";
            Log(message);
            ReplyToUserCommand(player, message);
        }

        /// <summary>
        /// Drops a queued match load, if any. Returns the identifier of the dropped match.
        /// </summary>
        private string? ClearQueuedMatch(string reason)
        {
            if (!isMatchQueued)
            {
                return null;
            }

            string? identifier = queuedMatchIdentifier;
            isMatchQueued = false;
            queuedMatchUrl = null;
            queuedMatchHeaderName = null;
            queuedMatchHeaderValue = null;
            queuedMatchIdentifier = null;
            tournamentNextMatch.Value = "";

            // The current series is still finishing; step back from "queued" so the
            // allocator does not wait for a load that will no longer happen.
            if (string.Equals(tournamentStatus.Value, "queued", StringComparison.OrdinalIgnoreCase))
            {
                // Without a match there is no series to finish: go straight back to idle.
                UpdateTournamentStatus(isMatchSetup ? "postgame" : TournamentStatusLogic.IdleStatus(isWarmup));
            }

            Log($"[MatchQueue] Cleared queued match {identifier ?? "unknown"} ({reason}).");
            return identifier;
        }

        [ConsoleCommand("at_clear_queued_match", "Clears a match queued by at_loadmatch_url during postgame so it is not loaded after reset")]
        public void ClearQueuedMatchCommand(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;

            string? cleared = ClearQueuedMatch("at_clear_queued_match");

            // With no match on the server this is an idle server: make sure the status convars
            // say so, so MAT can allocate it again even if a stale match id was left behind.
            if (!isMatchSetup)
            {
                UpdateTournamentStatus(TournamentStatusLogic.IdleStatus(isWarmup));
            }

            if (cleared == null)
            {
                ReplyToUserCommand(player, "[MatchQueue] No queued match to clear. cleared_queued_match=none");
                return;
            }
            ReplyClearedQueuedMatch(player, cleared);
        }

        private void ReplyClearedQueuedMatch(CCSPlayerController? player, string? cleared)
        {
            if (cleared == null) return;
            ReplyToUserCommand(player, $"[MatchQueue] Cleared queued match {cleared}; it will not be loaded. cleared_queued_match={cleared}");
        }

        /// <summary>
        /// If a match was queued while the previous series was still active (postgame),
        /// load it now that the series has been reset after it ended normally.
        /// </summary>
        private void TryLoadQueuedMatchAfterReset()
        {
            if (!isMatchQueued || string.IsNullOrWhiteSpace(queuedMatchUrl))
            {
                return;
            }

            string url = queuedMatchUrl!;
            string? headerName = queuedMatchHeaderName;
            string? headerValue = queuedMatchHeaderValue;
            string? identifier = queuedMatchIdentifier;

            // Clear the queue state up-front so we don't accidentally loop if something fails.
            isMatchQueued = false;
            queuedMatchUrl = null;
            queuedMatchHeaderName = null;
            queuedMatchHeaderValue = null;
            queuedMatchIdentifier = null;
            tournamentNextMatch.Value = "";

            Log($"[MatchQueue] Attempting to auto-load queued match {identifier} from URL after reset: {url}");

            try
            {
                HttpClient httpClient = new();
                if (!string.IsNullOrEmpty(headerName))
                {
                    httpClient.DefaultRequestHeaders.Add(headerName, headerValue);
                }

                HttpResponseMessage response = httpClient.GetAsync(url).Result;

                if (response.IsSuccessStatusCode)
                {
                    string jsonData = response.Content.ReadAsStringAsync().Result;
                    Log($"[LoadQueuedMatch] Received following data for queued match: {SecretRedactor.RedactText(jsonData)}");

                    bool success = LoadMatchFromJSON(jsonData);
                    if (!success)
                    {
                        Log("[LoadQueuedMatch] Queued match load failed. Keeping server in idle/error state.");
                        UpdateTournamentStatus("error");
                    }
                    else
                    {
                        loadedConfigFile = url;
                    }
                }
                else
                {
                    Log($"[LoadQueuedMatch] HTTP request for queued match failed with status code: {response.StatusCode}");
                    UpdateTournamentStatus("error");
                }
            }
            catch (Exception e)
            {
                Log($"[LoadQueuedMatch - FATAL] An error occured while loading queued match: {e.Message}");
                UpdateTournamentStatus("error");
            }
        }

        static string ValidateMatchJsonStructure(JObject jsonData)
        {
            string[] requiredFields = { "maplist", "team1", "team2", "num_maps" };

            // Check if any required field is missing
            foreach (string field in requiredFields)
            {
                if (jsonData[field] == null)
                {
                    return $"Missing mandatory field: {field}";
                }
            }

            foreach (var property in jsonData.Properties())
            {
                string field = property.Name;

                switch (field)
                {
                    case "matchid":
                    case "players_per_team":
                    case "min_players_to_ready":
                    case "min_spectators_to_ready":
                    case "num_maps":
                        int numMaps;
                        if (!int.TryParse(jsonData[field]!.ToString(), out numMaps))
                        {
                            return $"{field} should be an integer!";

                        }
                        if (field == "num_maps" && numMaps > jsonData["maplist"]!.ToObject<List<string>>()!.Count)
                        {
                            return $"{field} should be less than or equal to the number of maps in maplist!";
                        }

                        break;

                    case "cvars":
                        if (jsonData[field]!.Type != JTokenType.Object)
                        {
                            return $"{field} should be a JSON structure!";
                        }
                        break;

                    case "team1":
                    case "team2":
                    case "spectators":
                        if (jsonData[field]!.Type != JTokenType.Object)
                        {
                            return $"{field} should be a JSON structure!";
                        }
                        if ((field != "spectators") && (jsonData[field]!["players"] == null || jsonData[field]!["players"]!.Type != JTokenType.Object))
                        {
                            return $"{field} should have 'players' JSON!";
                        }
                        break;

                    case "veto_mode":
                        if (jsonData[field]!.Type != JTokenType.Array)
                        {
                            return $"{field} should be an Array!";
                        }
                        break;

                    case "maplist":
                        if (jsonData[field]!.Type != JTokenType.Array)
                        {
                            return $"{field} should be an Array!";
                        }
                        if (!jsonData[field]!.Any())
                        {
                            return $"{field} should contain atleast 1 map!";
                        }

                        break;
                    case "map_sides":
                        if (jsonData[field]!.Type != JTokenType.Array)
                        {
                            return $"{field} should be an Array!";
                        }
                        string[] allowedValues = { "team1_ct", "team1_t", "team2_ct", "team2_t", "knife" };
                        bool allElementsValid = jsonData[field]!.All(element => allowedValues.Contains(element.ToString()));

                        if (!allElementsValid)
                        {
                            return $"{field} should be \"team1_ct\", \"team1_t\", or \"knife\"!";
                        }

                        if (jsonData[field]!.ToObject<List<string>>()!.Count < jsonData["num_maps"]!.Value<int>())
                        {
                            return $"{field} should be equal to or greater than num_maps!";
                        }
                        break;

                    case "skip_veto":
                    case "clinch_series":
                    case "wingman":
                        if (!bool.TryParse(jsonData[field]!.ToString(), out bool result))
                        {
                            return $"{field} should be a boolean!";
                        }
                        break;
                }
            }

            return "";
        }

        public bool LoadMatchFromJSON(string jsonData)
        {

            JObject jsonDataObject = JObject.Parse(jsonData);

            string validationError = ValidateMatchJsonStructure(jsonDataObject);

            if (validationError != "")
            {
                Log($"[LoadMatchDataCommand] {validationError}");
                UpdateTournamentStatus("error");
                return false;
            }

            if (jsonDataObject["matchid"] != null)
            {
                liveMatchId = (long)jsonDataObject["matchid"]!;
            }

            // Update tournament status to loading with match ID
            UpdateTournamentStatus("loading", liveMatchId.ToString());
            // A new match starts from the operator's restart delay, not a previous match's GOTV raise.
            RestoreMatchRestartDelay("match load");
            JToken team1 = jsonDataObject["team1"]!;
            JToken team2 = jsonDataObject["team2"]!;
            JToken maplist = jsonDataObject["maplist"]!;

            if (team1["id"] != null) atTeam1.id = team1["id"]!.ToString();
            if (team2["id"] != null) atTeam2.id = team2["id"]!.ToString();

            atTeam1.teamName = RemoveSpecialCharacters(team1["name"]!.ToString());
            atTeam2.teamName = RemoveSpecialCharacters(team2["name"]!.ToString());
            atTeam1.teamPlayers = team1["players"];
            atTeam2.teamPlayers = team2["players"];

            // Preserve any externally-configured remote log settings across match loads so that
            // an outside controller (Auto Tournament) does not need to constantly
            // reapply them after every at_loadmatch_url call.
            string previousRemoteLogUrl = matchConfig.RemoteLogURL;
            string previousRemoteLogHeaderKey = matchConfig.RemoteLogHeaderKey;
            string previousRemoteLogHeaderValue = matchConfig.RemoteLogHeaderValue;

            matchConfig = new()
            {
                MatchId = liveMatchId,
                MapsPool = maplist.ToObject<List<string>>()!,
                MapsLeftInVetoPool = maplist.ToObject<List<string>>()!,
                NumMaps = jsonDataObject["num_maps"]!.Value<int>(),
                MinPlayersToReady = minimumReadyRequired,
                RemoteLogURL = previousRemoteLogUrl,
                RemoteLogHeaderKey = previousRemoteLogHeaderKey,
                RemoteLogHeaderValue = previousRemoteLogHeaderValue
            };

            GetOptionalMatchValues(jsonDataObject);

            // If the JSON payload explicitly provides a remote_log_url and/or headers, treat
            // that as an authoritative configuration as well.
            if (jsonDataObject["remote_log_url"] != null)
            {
                string? remoteUrl = jsonDataObject["remote_log_url"]!.ToString();
                if (!string.IsNullOrWhiteSpace(remoteUrl) && IsValidUrl(remoteUrl))
                {
                    matchConfig.RemoteLogURL = remoteUrl;
                    remoteLogUrlEverConfigured = true;
                    remoteLogUrlMissingWarningLogged = false;
                }
            }
            if (jsonDataObject["remote_log_header_key"] != null)
            {
                matchConfig.RemoteLogHeaderKey = jsonDataObject["remote_log_header_key"]!.ToString();
            }
            if (jsonDataObject["remote_log_header_value"] != null)
            {
                matchConfig.RemoteLogHeaderValue = jsonDataObject["remote_log_header_value"]!.ToString();
            }

            // Track whether this match should be run in bot-driven simulation mode.
            isSimulationMode = matchConfig.Simulation;

            if (isSimulationMode)
            {
                // Validate that we actually have configured players for simulation.
                bool team1HasPlayers = atTeam1.teamPlayers is JObject t1 && t1.Properties().Any();
                bool team2HasPlayers = atTeam2.teamPlayers is JObject t2 && t2.Properties().Any();

                if (!team1HasPlayers && !team2HasPlayers)
                {
                    Log("[LOADMATCH] Simulation requested but no players configured for team1/team2. Aborting match load.");
                    UpdateTournamentStatus("error");
                    return false;
                }
            }

            // Validate that we have enough maps to play the series.
            if (matchConfig.MapsPool.Count < matchConfig.NumMaps)
            {
                Log($"[LOADMATCH] The map pool {matchConfig.MapsPool.Count} is not large enough to play a series of {matchConfig.NumMaps} maps.");
                return false;
            }

            GetCvarValues(jsonDataObject);

            Log($"[LOADMATCH] MinPlayersToReady: {matchConfig.MinPlayersToReady} SeriesClinch: {matchConfig.SeriesCanClinch}");
            Log($"[LOADMATCH] MapsPool: {string.Join(", ", matchConfig.MapsPool)} MapsLeftInVetoPool: {string.Join(", ", matchConfig.MapsLeftInVetoPool)}");

            LoadClientNames();

            // Build the final maplist deterministically from the configured pool.
            matchConfig.Maplist.Clear();
            for (int i = 0; i < matchConfig.NumMaps; i++)
            {
                matchConfig.Maplist.Add(matchConfig.MapsPool[i]);

                // Ensure there is a side entry per map.
                if (matchConfig.MapSides.Count < matchConfig.Maplist.Count)
                {
                    if (matchConfig.MatchSideType == "standard" || matchConfig.MatchSideType == "always_knife")
                    {
                        matchConfig.MapSides.Add("knife");
                    }
                    else if (matchConfig.MatchSideType == "random")
                    {
                        matchConfig.MapSides.Add(new Random().Next(0, 2) == 0 ? "team1_ct" : "team1_t");
                    }
                    else
                    {
                        matchConfig.MapSides.Add("team1_ct");
                    }
                }
            }

            string mapName = matchConfig.Maplist[0];

            // After a server restart the server can already be on the match map with no SourceTV
            // master (created only on map load with tv_enable 1). Without a reload tv_record then
            // writes nothing and the first match's demo is lost (QA: match 62, file_not_found).
            bool sourceTvReloadRequired = DemoFileLocator.RequiresMapReloadForSourceTv(isDemoRecordingEnabled, IsSourceTvActive());
            if (sourceTvReloadRequired)
            {
                Log("[LoadMatch] Demo recording is enabled but no SourceTV master is running; forcing tv_enable 1 and reloading the map so demos are recorded.");
                Server.ExecuteCommand("tv_enable 1");
            }

            bool willChangeMap = IsMapReloadRequiredForGameMode(matchConfig.Wingman) || mapReloadRequired || !IsOnMap(mapName) || sourceTvReloadRequired;

            if (willChangeMap)
            {
                SetCorrectGameMode();
                ChangeMap(mapName, 0);

                // In simulation mode we don't want to spawn bots on the *old* map right
                // before a changelevel, as they will immediately be kicked. Instead we
                // defer the simulation flow until after the new map is fully loaded.
                if (isSimulationMode)
                {
                    simulationFlowDeferred = true;
                    simulationTargetMap = mapName;
                }
            }

            readyAvailable = true;

            // This is done before starting warmup so that cvars like get5_remote_log_url are set properly to send the events
            ExecuteChangedConvars();

            StartWarmup();

            isMatchSetup = true;
            
            // Auto-ready simulation helper: when enabled, spawn two bots (1 CT + 1 T) after warmup
            // has started so auto-ready/ready gating can be tested without a human joining.
            // We delay slightly so warmup.cfg + humans.cfg (bot_kick) have already executed.
            ScheduleAutoReadySimulationFlowIfNeeded(2.0f);

            // If this match is configured for simulation and we are *not* in the middle of a
            // map change, schedule the simulation flow shortly after warmup is active so the
            // server is fully ready to accept connections (just like in a human match).
            if (isSimulationMode && !simulationFlowDeferred)
            {
                ScheduleSimulationFlowStart(5.0f);
            }

            SetMapSides();

            SetTeamNames();
            UpdatePlayersMap();
            UpdateHostname();

            // If auto-ready is enabled, check if players already on teams should be auto-readied
            if (autoReadyEnabled.Value && readyAvailable && !matchStarted)
            {
                // Use a small delay to ensure all team assignments are complete
                AddTimer(1.0f, () => {
                    CheckAndAutoReadyPlayers();
                });
            }

            var seriesStartedEvent = new AutoTournamentCS2SeriesStartedEvent
            {
                MatchId = liveMatchId,
                NumberOfMaps = matchConfig.NumMaps,
                Team1 = new(atTeam1.id, atTeam1.teamName),
                Team2 = new(atTeam2.id, atTeam2.teamName),
            };

            Task.Run(async () =>
            {
                await SendEventAsync(seriesStartedEvent);
            });

            Log($"[LoadMatchFromJSON] Success with matchid: {liveMatchId}!");
            return true;
        }

        public void SetMapSides()
        {
            int mapNumber = matchConfig.CurrentMapNumber;
            if (matchConfig.MapSides[mapNumber] == "team1_ct" || matchConfig.MapSides[mapNumber] == "team2_t")
            {
                teamSides[atTeam1] = "CT";
                teamSides[atTeam2] = "TERRORIST";
                reverseTeamSides["CT"] = atTeam1;
                reverseTeamSides["TERRORIST"] = atTeam2;
                isKnifeRequired = false;
            }
            else if (matchConfig.MapSides[mapNumber] == "team2_ct" || matchConfig.MapSides[mapNumber] == "team1_t")
            {
                teamSides[atTeam2] = "CT";
                teamSides[atTeam1] = "TERRORIST";
                reverseTeamSides["CT"] = atTeam2;
                reverseTeamSides["TERRORIST"] = atTeam1;
                isKnifeRequired = false;
            }
            else if (matchConfig.MapSides[mapNumber] == "knife")
            {
                isKnifeRequired = true;
            }

            SetTeamNames();
        }

        public void SetTeamNames()
        {
            Server.ExecuteCommand($"mp_teamname_1 {reverseTeamSides["CT"].teamName}");
            Server.ExecuteCommand($"mp_teamname_2 {reverseTeamSides["TERRORIST"].teamName}");
        }

        public void GetCvarValues(JObject jsonDataObject)
        {
            try
            {
                if (jsonDataObject["cvars"] == null) return;

                foreach (JProperty cvarData in jsonDataObject["cvars"]!)
                {
                    string cvarName = cvarData.Name;
                    string cvarValue = cvarData.Value.ToString();

                    var cvar = ConVar.Find(cvarName);
                    matchConfig.ChangedCvars[cvarName] = cvarValue;
                    if (cvar != null)
                    {
                        matchConfig.OriginalCvars[cvarName] = GetConvarStringValue(cvar);
                    }
                }

            }
            catch (Exception e)
            {
                Log($"[GetCvarValues FATAL] An error occurred: {e.Message}");
            }
        }

        public void GetOptionalMatchValues(JObject jsonDataObject)
        {
            // Map and roster/spectator configuration
            if (jsonDataObject["map_sides"] != null)
            {
                matchConfig.MapSides = jsonDataObject["map_sides"]!.ToObject<List<string>>()!;
            }
            if (jsonDataObject["players_per_team"] != null)
            {
                matchConfig.PlayersPerTeam = jsonDataObject["players_per_team"]!.Value<int>();
            }
            if (jsonDataObject["min_players_to_ready"] != null)
            {
                matchConfig.MinPlayersToReady = jsonDataObject["min_players_to_ready"]!.Value<int>();
            }
            if (jsonDataObject["min_spectators_to_ready"] != null)
            {
                matchConfig.MinSpectatorsToReady = jsonDataObject["min_spectators_to_ready"]!.Value<int>();
            }
            if (jsonDataObject["spectators"] != null && jsonDataObject["spectators"]!["players"] != null)
            {
                matchConfig.Spectators = jsonDataObject["spectators"]!["players"]!;
                if (matchConfig.Spectators is JArray spectatorsArray && spectatorsArray.Count == 0)
                {
                    // Convert the empty JArray to an empty JObject
                    matchConfig.Spectators = new JObject();
                }
            }

            // Optional: per-match admin SteamIDs. When present, these Steam64 IDs are treated
            // as admins for the duration of this match in addition to any global admins from
            // CSSharp or MatchZy Enhanced admins.json.
            if (jsonDataObject["admins"] != null)
            {
                try
                {
                    var adminIds = jsonDataObject["admins"]!.ToObject<List<string>>() ?? new List<string>();
                    matchConfig.AdminSteamIds = adminIds;
                }
                catch (Exception)
                {
                    Log("[LOADMATCH] Invalid admins list in JSON; ignoring per-match admins.");
                    matchConfig.AdminSteamIds = new List<string>();
                }
            }

            // Series / flow toggles
            if (jsonDataObject["clinch_series"] != null)
            {
                matchConfig.SeriesCanClinch = bool.Parse(jsonDataObject["clinch_series"]!.ToString());
            }
            if (jsonDataObject["skip_veto"] != null)
            {
                matchConfig.SkipVeto = bool.Parse(jsonDataObject["skip_veto"]!.ToString());
            }
            if (jsonDataObject["wingman"] != null)
            {
                matchConfig.Wingman = bool.Parse(jsonDataObject["wingman"]!.ToString());
            }

            // Simulation / practice controls
            if (jsonDataObject["simulation"] != null)
            {
                matchConfig.Simulation = bool.Parse(jsonDataObject["simulation"]!.ToString());
            }
            if (jsonDataObject["simulation_timescale"] != null)
            {
                // Allow either numeric or string; clamp to a safe range so we don't accidentally
                // set something extreme like 0 or 100x speed.
                try
                {
                    float ts = jsonDataObject["simulation_timescale"]!.Value<float>();
                    if (float.IsNaN(ts) || float.IsInfinity(ts)) throw new Exception("NaN/Inf");

                    // Clamp between 0.1x and 10x (the range MAT allows) to avoid crazy values.
                    float clamped = MatchLogic.ClampSimulationTimeScale(ts);
                    if (clamped != ts)
                    {
                        Log($"[LOADMATCH] simulation_timescale {ts} is outside {MatchLogic.MinSimulationTimeScale}-{MatchLogic.MaxSimulationTimeScale}; using {clamped}.");
                    }

                    matchConfig.SimulationTimeScale = clamped;
                }
                catch (Exception)
                {
                    Log("[LOADMATCH] Invalid simulation_timescale value in JSON; falling back to default 1.0");
                    matchConfig.SimulationTimeScale = 1.0f;
                }
            }

            // Veto / map selection mode
            if (jsonDataObject["veto_mode"] != null)
            {
                matchConfig.MapBanOrder = jsonDataObject["veto_mode"]!.ToObject<List<string>>()!;
            }

            // Tournament overtime / regulation configuration (shuffle tournaments, etc.).
            // These are advisory match-level settings that we translate into the
            // appropriate CS2 cvars when the match goes live.
            if (jsonDataObject["maxRounds"] != null)
            {
                try
                {
                    matchConfig.MaxRounds = jsonDataObject["maxRounds"]!.Value<int>();
                    Log($"[GetOptionalMatchValues] Parsed maxRounds={matchConfig.MaxRounds}");
                }
                catch (Exception e)
                {
                    Log($"[GetOptionalMatchValues] Failed to parse maxRounds: {e.Message}");
                }
            }

            if (jsonDataObject["overtimeMode"] != null)
            {
                try
                {
                    matchConfig.OvertimeMode = jsonDataObject["overtimeMode"]!.ToString();
                    Log($"[GetOptionalMatchValues] Parsed overtimeMode='{matchConfig.OvertimeMode}'");
                }
                catch (Exception e)
                {
                    Log($"[GetOptionalMatchValues] Failed to parse overtimeMode: {e.Message}");
                }
            }

            if (jsonDataObject["overtimeSegments"] != null)
            {
                try
                {
                    matchConfig.OvertimeSegments = jsonDataObject["overtimeSegments"]!.Value<int>();
                    Log($"[GetOptionalMatchValues] Parsed overtimeSegments={matchConfig.OvertimeSegments}");
                }
                catch (Exception e)
                {
                    Log($"[GetOptionalMatchValues] Failed to parse overtimeSegments: {e.Message}");
                }
            }
        }

        public void HandleTeamNameChangeCommand(CCSPlayerController? player, string teamName, int teamNum)
        {
            if (!IsPlayerAdmin(player, "css_team", "@css/config"))
            {
                SendPlayerNotAdminMessage(player);
                return;
            }
            if (matchStarted)
            {
                // ReplyToUserCommand(player, "Team names cannot be changed once the match is started!");
                ReplyToUserCommand(player, Localizer["at.mm.teamcannotbechanged"]);
                return;
            }
            teamName = RemoveSpecialCharacters(teamName.Trim());
            if (teamName == "")
            {
                // ReplyToUserCommand(player, $"Usage: !team{teamNum} <name>");
                ReplyToUserCommand(player, Localizer["at.cc.usage", $"!team{teamNum} <name>"]);
            }

            if (teamNum == 1)
            {
                atTeam1.teamName = teamName;
                teamSides[atTeam1] = "CT";
                reverseTeamSides["CT"] = atTeam1;
                foreach (var coach in atTeam1.coach)
                {
                    coach.Clan = $"[{atTeam1.teamName} COACH]";
                }
            }
            else if (teamNum == 2)
            {
                atTeam2.teamName = teamName;
                teamSides[atTeam2] = "TERRORIST";
                reverseTeamSides["TERRORIST"] = atTeam2;
                foreach (var coach in atTeam2.coach)
                {
                    coach.Clan = $"[{atTeam2.teamName} COACH]";
                }
            }
            Server.ExecuteCommand($"mp_teamname_{teamNum} {teamName};");
        }

        public void SwapSidesInTeamData(bool swapTeams)
        {
            // if (swapTeams) {
            //     // Here, we sync atTeam1 and atTeam2 with the actual team1 and team2
            //     (atTeam2, atTeam1) = (atTeam1, atTeam2);
            // }

            (teamSides[atTeam1], teamSides[atTeam2]) = (teamSides[atTeam2], teamSides[atTeam1]);
            (reverseTeamSides["CT"], reverseTeamSides["TERRORIST"]) = (reverseTeamSides["TERRORIST"], reverseTeamSides["CT"]);

            // Send side_swap event
            if (isMatchLive)
            {
                Log($"[SwapSidesInTeamData] Sending side_swap event");

                var sideSwapEvent = new AutoTournamentCS2SideSwapEvent
                {
                    MatchId = liveMatchId,
                    MapNumber = matchConfig.CurrentMapNumber,
                    Team1Side = teamSides[atTeam1],
                    Team2Side = teamSides[atTeam2]
                };

                Task.Run(async () =>
                {
                    await SendEventAsync(sideSwapEvent);
                });

                // Check if this is halftime or overtime
                var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First().GameRules!;
                int roundsPlayed = gameRules.TotalRoundsPlayed;
                int roundsPerHalf = ConVar.Find("mp_maxrounds")!.GetPrimitiveValue<int>() / 2;
                int roundsPerOTHalf = ConVar.Find("mp_overtime_maxrounds")!.GetPrimitiveValue<int>() / 2;

                if (roundsPlayed == roundsPerHalf)
                {
                    // This is halftime
                    Log($"[SwapSidesInTeamData] Halftime detected, sending halftime_started event");
                    (int t1score, int t2score) = GetTeamsScore();
                    var halftimeStartedEvent = new AutoTournamentCS2HalftimeStartedEvent
                    {
                        MatchId = liveMatchId,
                        MapNumber = matchConfig.CurrentMapNumber,
                        Team1Score = t1score,
                        Team2Score = t2score
                    };

                    Task.Run(async () =>
                    {
                        await SendEventAsync(halftimeStartedEvent);
                    });
                    UpdateTournamentStatus("halftime");
                }
                else if (roundsPlayed >= 2 * roundsPerHalf)
                {
                    // This is overtime
                    int otround = roundsPlayed - 2 * roundsPerHalf;
                    if ((otround + roundsPerOTHalf) % (2 * roundsPerOTHalf) == 0)
                    {
                        int overtimeNumber = (otround / (2 * roundsPerOTHalf)) + 1;
                        Log($"[SwapSidesInTeamData] Overtime detected, sending overtime_started event - OT#{overtimeNumber}");

                        var overtimeStartedEvent = new AutoTournamentCS2OvertimeStartedEvent
                        {
                            MatchId = liveMatchId,
                            MapNumber = matchConfig.CurrentMapNumber,
                            OvertimeNumber = overtimeNumber
                        };

                        Task.Run(async () =>
                        {
                            await SendEventAsync(overtimeStartedEvent);
                        });
                    }
                }
            }
        }

        private CsTeam GetPlayerTeam(CCSPlayerController player)
        {
            CsTeam playerTeam = CsTeam.None;
            var steamId = player.SteamID;
            try
            {
                if (atTeam1.teamPlayers != null && atTeam1.teamPlayers[steamId.ToString()] != null)
                {
                    if (teamSides[atTeam1] == "CT")
                    {
                        playerTeam = CsTeam.CounterTerrorist;
                    }
                    else if (teamSides[atTeam1] == "TERRORIST")
                    {
                        playerTeam = CsTeam.Terrorist;
                    }

                }
                else if (atTeam2.teamPlayers != null && atTeam2.teamPlayers[steamId.ToString()] != null)
                {
                    if (teamSides[atTeam2] == "CT")
                    {
                        playerTeam = CsTeam.CounterTerrorist;
                    }
                    else if (teamSides[atTeam2] == "TERRORIST")
                    {
                        playerTeam = CsTeam.Terrorist;
                    }
                }
                else if (matchConfig.Spectators != null && matchConfig.Spectators[steamId.ToString()] != null)
                {
                    playerTeam = CsTeam.Spectator;
                }
            }
            catch (Exception ex)
            {
                Log($"[GetPlayerTeam - FATAL] Exception occurred: {ex.Message}");
            }
            return playerTeam;
        }

        public void EndSeries(string? winnerName, int restartDelay, int t1score, int t2score)
        {
            long matchId = liveMatchId;
            (int team1Score, int team2Score) = (atTeam1.seriesScore, atTeam2.seriesScore);
            Log($"[SeriesCheckpoint] SERIES END reached for match {matchId}. Final map score: {atTeam1.teamName} {t1score} – {atTeam2.teamName} {t2score}. Final series score: {atTeam1.teamName} {team1Score} – {atTeam2.teamName} {team2Score}.");

            // The series winner is decided by the series score alone. Callers pass the
            // winner of the *last map*, which is not the series winner when a series is
            // played out (e.g. 2-1 where the loser took the final map) and is "Draw"
            // for a drawn final map.
            string winnerTeam = MatchLogic.ResolveMapWinnerSlot(team1Score, team2Score, null);
            string? seriesWinnerName = winnerTeam == MatchLogic.Team1 ? atTeam1.teamName
                : winnerTeam == MatchLogic.Team2 ? atTeam2.teamName
                : null;
            if (seriesWinnerName != winnerName)
            {
                Log($"[SeriesCheckpoint] Last map winner '{winnerName ?? "none"}' differs from series winner '{seriesWinnerName ?? "none"}'; using the series winner.");
            }
            winnerName = seriesWinnerName;

            if (winnerName == null)
            {
                PrintToAllChat($"{ChatColors.Green}{atTeam1.teamName}{ChatColors.Default} and {ChatColors.Green}{atTeam2.teamName}{ChatColors.Default} have tied the match");
            }
            else
            {
                Server.PrintToChatAll($"{chatPrefix} {ChatColors.Green}{winnerName}{ChatColors.Default} has won the match");
            }

            var seriesResultEvent = new AutoTournamentCS2SeriesResultEvent()
            {
                MatchId = matchId,
                // Side is the side the series winner finished the last map on ("0" for a draw).
                Winner = new Winner(MatchLogic.SideNumberForSlot(winnerTeam, teamSides[atTeam1]), winnerTeam),
                Team1SeriesScore = team1Score,
                Team2SeriesScore = team2Score,
                TimeUntilRestore = 10,
            };

            Task.Run(async () =>
            {
                await database.SetMatchEndData(matchId, winnerName ?? "Draw", team1Score, team2Score);
                // Making sure that map end event is fired first
                await Task.Delay(2000);
                await SendEventAsync(seriesResultEvent);
            });

            if (resetCvarsOnSeriesEnd) ResetChangedConvars();
            isMatchLive = false;

            // In simulation mode, schedule bots to disconnect gradually after the series ends
            // and restore normal server behavior (timescale/cheats) for subsequent human matches.
            if (isSimulationMode)
            {
                ScheduleSimulationBotDisconnects();
                Log("[SimulationMode] Series ended - resetting host_timescale to 1 and sv_cheats to 0.");
                Server.ExecuteCommand("host_timescale 1; sv_cheats 0");
            }

            // Calculate kick delay based on demo recording and upload configuration
            bool hasUploadEndpoint = !string.IsNullOrEmpty(demoUploadURL);
            int kickDelay;
            
            if (!isDemoRecordingEnabled)
            {
                // Demo recording disabled - very fast
                kickDelay = restartDelay + seriesEndKickDelayNoDemo.Value;
            }
            else if (!hasUploadEndpoint)
            {
                // Demo recording enabled but no upload URL - don't wait for upload that won't happen
                kickDelay = restartDelay + seriesEndKickDelayDemoNoUpload.Value;
            }
            else
            {
                // Demo recording enabled with upload URL - wait for upload to complete
                kickDelay = restartDelay + seriesEndKickDelayDemoUpload.Value;
            }
            
            Log($"[EndSeries] Demo recording: {isDemoRecordingEnabled}, Upload URL: {hasUploadEndpoint}, kickDelay: {kickDelay}s");

            int minutes = kickDelay / 60;
            int seconds = kickDelay % 60;
            string timeText = minutes > 0 ? $"{minutes} minute{(minutes > 1 ? "s" : "")} {seconds} second{(seconds > 1 ? "s" : "")}" : $"{seconds} second{(seconds > 1 ? "s" : "")}";

            string resetMessage;
            if (!isDemoRecordingEnabled)
            {
                resetMessage = $"{ChatColors.Grey}Series ended. Server will reset in {ChatColors.Yellow}{timeText}{ChatColors.Default}.";
            }
            else if (!hasUploadEndpoint)
            {
                resetMessage = $"{ChatColors.Grey}Series ended. Server will reset in {ChatColors.Yellow}{timeText}{ChatColors.Default} after demo is saved.";
            }
            else
            {
                resetMessage = $"{ChatColors.Grey}Series ended. Server will reset in {ChatColors.Yellow}{timeText}{ChatColors.Default} after demo upload completes.";
            }
            
            PrintToAllChat(resetMessage);
            PrintToAllChat($"{ChatColors.Grey}All players will be disconnected to prepare the server for the next match.{ChatColors.Default}");

            // Show countdown on center screen for last 30 seconds (or full duration if less than 30s)
            int countdownStart = kickDelay > 30 ? 30 : kickDelay;
            if (kickDelay > 30)
            {
                // Start countdown from 30 seconds mark
                AddTimer(kickDelay - 30, () =>
                {
                    StartCountdown(30, "⚠️ SERVER RESTART<br>{0}s", "#ff0000");
                });
            }
            else if (kickDelay > 0)
            {
                // Start countdown immediately if less than 30 seconds
                StartCountdown(kickDelay, "⚠️ SERVER RESTART<br>{0}s", "#ff0000");
            }

            AddTimer(kickDelay, () =>
            {
                // Kick all players with winner message
                Log("[EndSeries] Kicking all players after series end...");
                var playerEntities = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller");
                int kickedCount = 0;
                
                // Build kick reason message
                string? kickReason = null;
                if (!string.IsNullOrEmpty(winnerName))
                {
                    kickReason = Localizer["at.match.won", winnerName];
                }
                
                foreach (var player in playerEntities)
                {
                    if (player == null) continue;
                    if (!player.IsValid || player.IsBot || player.IsHLTV) continue;
                    if (player.Connected != PlayerConnectedState.PlayerConnected) continue;

                    if (player.UserId.HasValue)
                    {
                        Log($"[EndSeries] Kicking player: {player.PlayerName} (UserId: {player.UserId}, reason: {kickReason ?? "none"})");
                        KickPlayer(player, kickReason);
                        kickedCount++;
                    }
                }
                Log($"[EndSeries] Kicked {kickedCount} players. Resetting match...");

                // Update status to idle to indicate server is ready for allocation
                UpdateTournamentStatus("idle", "");

                // Reset match after a short delay to ensure kicks are processed
                AddTimer(2.0f, () =>
                {
                    ResetMatch(false, loadQueuedMatch: true);
                });
            });
        }

        public void HandlePlayoutConfig()
        {
            if (isPlayOutEnabled)
            {
                Server.ExecuteCommand("mp_overtime_enable 0");
                Server.ExecuteCommand("mp_match_can_clinch false");
            }
            else
            {
                var absoluteCfgPath = Path.Join(Server.GameDirectory + "/csgo/cfg", GetGameMode() == 1 ? liveCfgPath : liveWingmanCfgPath);
                string? matchCanClinch = GetConvarValueFromCFGFile(absoluteCfgPath, "mp_match_can_clinch");
                Server.ExecuteCommand($"mp_match_can_clinch {matchCanClinch ?? "1"}");

                // If the JSON explicitly specified an overtimeMode, that is the single
                // source of truth here. Re-assert it after the live.cfg/base cfg has
                // been applied so that nothing can silently re-enable overtime.
                if (!string.IsNullOrWhiteSpace(matchConfig.OvertimeMode))
                {
                    string mode = matchConfig.OvertimeMode!.ToLowerInvariant();
                    if (mode == "enabled")
                    {
                        Log("[OvertimeConfig] HandlePlayoutConfig confirming overtime enabled from match config.");
                        Server.ExecuteCommand("mp_overtime_enable 1");
                    }
                    else if (mode == "disabled")
                    {
                        Log("[OvertimeConfig] HandlePlayoutConfig confirming overtime disabled from match config.");
                        Server.ExecuteCommand("mp_overtime_enable 0");
                    }
                }
                else
                {
                    // No JSON override – fall back to whatever the base cfg says.
                    string? overtimeEnabled = GetConvarValueFromCFGFile(absoluteCfgPath, "mp_overtime_enable");
                    Server.ExecuteCommand($"mp_overtime_enable {overtimeEnabled ?? "1"}");
                }
            }
        }

    }
}
