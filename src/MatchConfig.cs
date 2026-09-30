using System.Text.Json.Serialization;
using Newtonsoft.Json.Linq;


namespace AutoTournamentCS2
{

    public class MatchConfig
    {
        // Tournament / match flow settings
        [JsonPropertyName("maplist")]
        public List<string> Maplist { get; set; } = new List<string>();

        [JsonPropertyName("maps_pool")]
        public List<string> MapsPool { get; set; } = new List<string>();

        // DEPRECATED: kept for JSON compatibility with upstream / Get5-style veto configs.
        // The enhanced Auto Tournament fork ignores these when deciding maps; maps are driven entirely by the external platform.
        [JsonPropertyName("maps_left_in_veto_pool")]
        public List<string> MapsLeftInVetoPool { get; set; } = new List<string>();

        [JsonPropertyName("map_ban_order")]
        public List<string> MapBanOrder { get; set; } = new List<string>();

        [JsonPropertyName("skip_veto")]
        public bool SkipVeto { get; set; } = true;

        [JsonPropertyName("match_id")]
        public long MatchId { get; set; }

        [JsonPropertyName("num_maps")]
        public int NumMaps { get; set; } = 1;

        // For shuffle tournaments and other flows that specify regulation length
        // and overtime behavior via top-level JSON fields rather than raw cvars.
        //
        // Example payload from the tournament backend:
        // {
        //   "maxRounds": 24,
        //   "overtimeMode": "enabled",   // "enabled" | "disabled"
        //   "overtimeSegments": 2        // optional, advisory only in v1
        // }
        //
        // - MaxRounds maps to mp_maxrounds when present.
        // - OvertimeMode controls mp_overtime_enable when present.
        // - OvertimeSegments is currently informational only; we do not cap OT
        //   segments yet, we simply expose it for logging/telemetry and future
        //   behavior.
        [JsonPropertyName("maxRounds")]
        public int? MaxRounds { get; set; }

        [JsonPropertyName("overtimeMode")]
        public string? OvertimeMode { get; set; }

        [JsonPropertyName("overtimeSegments")]
        public int? OvertimeSegments { get; set; }

        [JsonPropertyName("players_per_team")]
        public int PlayersPerTeam { get; set; } = 5;

        [JsonPropertyName("min_players_to_ready")]
        public int MinPlayersToReady { get; set; } = 12;

        [JsonPropertyName("min_spectators_to_ready")]
        public int MinSpectatorsToReady { get; set; } = 0;

        [JsonPropertyName("current_map_number")]
        public int CurrentMapNumber { get; set; } = 0;

        [JsonPropertyName("map_sides")]
        public List<string> MapSides { get; set; } = new List<string>();

        [JsonPropertyName("series_can_clinch")]
        public bool SeriesCanClinch { get; set; } = true;

        [JsonPropertyName("scrim")]
        public bool Scrim { get; set; } = false;

        [JsonPropertyName("wingman")]
        public bool Wingman { get; set; } = false;

        // When true, MatchZy Enhanced runs the match in bot-driven simulation mode instead of waiting for real players.
        // This flag is optional and defaults to false to preserve existing behavior.
        [JsonPropertyName("simulation")]
        public bool Simulation { get; set; } = false;

        // Optional: when in simulation mode, controls the CS2 host_timescale value used to
        // accelerate or slow down simulated matches. Defaults to 1.0 (normal speed).
        // Only applied in simulation mode; human matches always run at timescale 1.
        [JsonPropertyName("simulation_timescale")]
        public float SimulationTimeScale { get; set; } = 1.0f;

        [JsonPropertyName("match_side_type")]
        public string MatchSideType { get; set; } = "standard";

        [JsonPropertyName("changed_cvars")]
        public Dictionary<string, string> ChangedCvars { get; set; } = new();

        [JsonPropertyName("original_cvars")]
        public Dictionary<string, string> OriginalCvars { get; set; } = new();

        [JsonPropertyName("spectators")]
        public JToken Spectators { get; set; } = new JObject();

        // Optional: per-match admin SteamIDs (Steam64). When provided, any player whose
        // SteamID appears in this list is treated as an admin for the duration of this match,
        // in addition to any global admins defined via CSSharp or MatchZy Enhanced admins.json.
        [JsonPropertyName("admins")]
        public List<string> AdminSteamIds { get; set; } = new List<string>();

        [JsonPropertyName("remote_log_url")]
        public string RemoteLogURL { get; set; } = "";

        [JsonPropertyName("remote_log_header_key")]
        public string RemoteLogHeaderKey { get; set; } = "";

        [JsonPropertyName("remote_log_header_value")]
        public string RemoteLogHeaderValue { get; set; } = "";
    }
}
