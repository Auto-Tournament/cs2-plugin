using System;
using System.Net.Http;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;

namespace AutoTournamentCS2
{
    public partial class AutoTournamentCS2
    {
        [ConsoleCommand("at_webhook_url", "Sets MAT webhook URL (ReadyUp-like). Mirrors at_remote_log_url.")]
        public void AutoTournamentCS2WebhookUrl(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string url = command.ArgByIndex(1);
            if (string.IsNullOrWhiteSpace(url))
            {
                Log("[at_webhook_url] Usage: at_webhook_url <url>");
                return;
            }

            webhookUrl = url.Trim();
            database.SaveConfigValue("at_webhook_url", webhookUrl);
            Log("[at_webhook_url] Webhook URL set and persisted");

            // Reuse existing logic for remote log URL persistence/queue cleanup.
            Server.ExecuteCommand($"at_remote_log_url \"{webhookUrl}\"");
        }

        [ConsoleCommand("at_heartbeat_url", "Sets MAT heartbeat URL (ReadyUp-like) and derives server/bootstrap/report endpoints when possible.")]
        public void AutoTournamentCS2HeartbeatUrl(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string url = command.ArgByIndex(1);
            if (string.IsNullOrWhiteSpace(url))
            {
                Log("[at_heartbeat_url] Usage: at_heartbeat_url <url>");
                return;
            }

            heartbeatUrl = url.Trim();
            database.SaveConfigValue("at_heartbeat_url", heartbeatUrl);
            Log("[at_heartbeat_url] Heartbeat URL set and persisted");

            // Derive server_id from /api/servers/:id/heartbeat
            try
            {
                if (Uri.TryCreate(heartbeatUrl, UriKind.Absolute, out var uri))
                {
                    var seg = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    // ... api servers {id} heartbeat
                    if (seg.Length >= 4 && seg[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
                        seg[1].Equals("servers", StringComparison.OrdinalIgnoreCase) &&
                        seg[3].Equals("heartbeat", StringComparison.OrdinalIgnoreCase))
                    {
                        var serverId = seg[2];
                        if (!string.IsNullOrWhiteSpace(serverId))
                        {
                            Server.ExecuteCommand($"at_server_id \"{serverId}\"");

                            // Derive bootstrap URL: /api/servers/:id/bootstrap
                            string baseUrl = uri.GetLeftPart(UriPartial.Authority);
                            string bootstrap = $"{baseUrl}/api/servers/{serverId}/bootstrap";
                            this.bootstrapUrl = bootstrap;
                            database.SaveConfigValue("at_bootstrap_url", bootstrap);
                            Log("[at_heartbeat_url] Derived at_bootstrap_url and persisted");

                            // Derive report endpoint: /api/events/report
                            string reportEndpoint = $"{baseUrl}/api/events/report";
                            matchReportEndpoint.Value = reportEndpoint;
                            database.SaveConfigValue("at_report_endpoint", reportEndpoint);
                            Log("[at_heartbeat_url] Derived at_report_endpoint and persisted");
                        }
                    }
                }
            }
            catch
            {
                // best-effort only
            }

            // Start heartbeat immediately if possible.
            StartMatHeartbeatTimerIfConfigured();
        }

        [ConsoleCommand("at_match_token", "Sets MAT shared token (ReadyUp-like). Used for webhook auth, heartbeat auth, bootstrap fetch, and match load Authorization header.")]
        public void AutoTournamentCS2MatchToken(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string token = command.ArgByIndex(1);
            if (string.IsNullOrWhiteSpace(token))
            {
                Log("[at_match_token] Usage: at_match_token <token>");
                return;
            }

            matchToken = token.Trim();
            database.SaveConfigValue("at_match_token", matchToken);
            Log("[at_match_token] Token set and persisted (hidden)");

            // Configure webhook auth header for MatchZy Enhanced events.
            Server.ExecuteCommand("at_remote_log_header_key \"X-Auto-Tournament-Token\"");
            Server.ExecuteCommand($"at_remote_log_header_value \"{matchToken}\"");

            // Configure bootstrap token (a change schedules a debounced bootstrap fetch).
            Server.ExecuteCommand($"at_bootstrap_token \"{matchToken}\"");

            // Configure match report token used by /api/events/report auth.
            matchReportToken.Value = matchToken;
            database.SaveConfigValue("at_report_token", matchToken);

            // Start heartbeat immediately if possible.
            StartMatHeartbeatTimerIfConfigured();
        }

        [ConsoleCommand("at_admins_url", "Sets MAT admins URL (optional). Use 'clear' to unset.")]
        public void AutoTournamentCS2AdminsUrl(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string raw = command.ArgByIndex(1);
            if (string.IsNullOrWhiteSpace(raw))
            {
                Log("[at_admins_url] Usage: at_admins_url <url|clear>");
                return;
            }

            string value = raw.Trim();
            if (string.Equals(value, "clear", StringComparison.OrdinalIgnoreCase))
            {
                value = "";
            }

            atAdminsUrl = value;
            database.SaveConfigValue("at_admins_url", value);
            Log($"[at_admins_url] Saved admins URL ({(string.IsNullOrWhiteSpace(value) ? "cleared" : "set")})");

            StartAutoTournamentCS2AdminsRefreshTimerIfConfigured("console");
        }

        [ConsoleCommand("at_admins_refresh_seconds", "Sets MAT admins refresh seconds (optional).")]
        public void AutoTournamentCS2AdminsRefreshSeconds(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string raw = command.ArgByIndex(1);
            if (string.IsNullOrWhiteSpace(raw))
            {
                Log("[at_admins_refresh_seconds] Usage: at_admins_refresh_seconds <seconds>");
                return;
            }

            if (!int.TryParse(raw.Trim(), out int seconds) || seconds < 0)
            {
                Log("[at_admins_refresh_seconds] Invalid seconds value");
                return;
            }

            atAdminsRefreshSeconds = seconds;
            database.SaveConfigValue("at_admins_refresh_seconds", seconds.ToString());
            Log($"[at_admins_refresh_seconds] Saved admins refresh seconds: {seconds}");

            StartAutoTournamentCS2AdminsRefreshTimerIfConfigured("console");
        }

        [ConsoleCommand("at", "Root command (ReadyUp-like compat). Usage: at match load <configUrl>")]
        public void AutoTournamentCS2RootCommand(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;

            string sub1 = command.ArgByIndex(1);
            string sub2 = command.ArgByIndex(2);
            string sub3 = command.ArgByIndex(3);

            if (string.Equals(sub1, "match", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(sub2, "load", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(sub3))
                {
                    Log("[at] Usage: at match load <configUrl>");
                    return;
                }

                string url = sub3.Trim();
                LoadMatchFromMatUrl(url);
                return;
            }

            Log("[at] Unknown command. Usage: at match load <configUrl>");
        }

        private void LoadMatchFromMatUrl(string url)
        {
            // If a match is already setup, allow queuing the next match only once the current
            // series has reached the postgame phase (same behavior as at_loadmatch_url).
            if (isMatchSetup)
            {
                string currentStatus = tournamentStatus.Value ?? string.Empty;
                if (CanQueueMatchLoad(currentStatus))
                {
                    string authHeaderValue = string.IsNullOrWhiteSpace(matchToken) ? "" : $"Bearer {matchToken}";
                    QueueMatchLoad(null, "at match load", url, authHeaderValue == "" ? "" : "Authorization", authHeaderValue);
                }
                else
                {
                    Log($"[at match load] Match already setup (matchid={liveMatchId}, status={currentStatus}). Refusing to load.");
                }
                return;
            }

            if (!IsValidUrl(url))
            {
                Log($"[at match load] Invalid URL: {SecretRedactor.RedactText(url)}");
                UpdateTournamentStatus("error");
                return;
            }

            string token = string.IsNullOrWhiteSpace(matchToken) ? "" : matchToken.Trim();
            string authHeader = string.IsNullOrWhiteSpace(token) ? "" : $"Bearer {token}";

            Log($"[at match load] Fetching match config from {SecretRedactor.RedactText(url)} (auth={(string.IsNullOrWhiteSpace(authHeader) ? "none" : "bearer")})");

            Task.Run(async () =>
            {
                try
                {
                    using var httpClient = new HttpClient();
                    if (!string.IsNullOrWhiteSpace(authHeader))
                    {
                        httpClient.DefaultRequestHeaders.Add("Authorization", authHeader);
                    }

                    var response = await httpClient.GetAsync(url).ConfigureAwait(false);
                    string jsonData = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        Log($"[at match load] HTTP fetch failed ({(int)response.StatusCode}): {SecretRedactor.RedactText(jsonData)}");
                        Server.NextFrame(() =>
                        {
                            UpdateTournamentStatus("error");
                        });
                        return;
                    }

                    Server.NextFrame(() =>
                    {
                        bool success = LoadMatchFromJSON(jsonData);
                        if (!success)
                        {
                            Log("[at match load] Match load failed. Resetting.");
                            UpdateTournamentStatus("error");
                            ResetMatch();
                            return;
                        }

                        loadedConfigFile = url;
                    });
                }
                catch (Exception ex)
                {
                    Log($"[at match load] Exception: {ex.Message}");
                    Server.NextFrame(() =>
                    {
                        UpdateTournamentStatus("error");
                    });
                }
            });
        }

    }
}

