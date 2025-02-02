using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Tasks;
using AssettoServer.Server;
using AssettoServer.Network.Tcp;
using AssettoServer.Server.Configuration;
using AssettoServer.Shared.Network.Packets;
using AssettoServer.Shared.Network.Packets.Shared;
using Serilog;

namespace MyLapTimesPlugin
{

    public class MyLapTimesPlugin
    {
        private const string LOG_PREFIX = "[MyLapTimes]";
        private const string DATA_FOLDER = "LapData";

        private readonly ACServerConfiguration _acServerConfiguration;
        private readonly EntryCarManager _entryCarManager;
        private readonly SessionManager _sessionManager;
        private readonly CSPClientMessageTypeManager _cspClientMessageTypeManager;
        private readonly MyLapTimesConfiguration _config;
        private readonly HttpClient _httpClient = new();

        // Multi-track data: trackName -> (driverGuid -> List of LapEntries)
        private readonly Dictionary<string, Dictionary<ulong, List<LapEntry>>> _allDataByTrack =
            new Dictionary<string, Dictionary<ulong, List<LapEntry>>>();


        public MyLapTimesPlugin(
            ACServerConfiguration acServerConfig,
            EntryCarManager entryCarManager,
            SessionManager sessionManager,
            CSPClientMessageTypeManager cspClientMessageTypeManager,
            MyLapTimesConfiguration config
        )
        {
            _acServerConfiguration = acServerConfig;
            _entryCarManager = entryCarManager;
            _sessionManager = sessionManager;
            _cspClientMessageTypeManager = cspClientMessageTypeManager;
            _config = config;

            Log.Information("***************************************************************");
            Log.Information("*                                                             *");
            Log.Information("*       My LapTimes Plugin By SICORPS                         *");
            Log.Information("*                                                             *");
            Log.Information("*       GitHub: https://github.com/wyzed                      *");
            Log.Information("*       Twitter/X: https://x.com/Sicorps                      *");
            Log.Information("*                                                             *");
            Log.Information("*       Follow me for updates and support!                    *");
            Log.Information("*                                                             *");
            Log.Information("***************************************************************");


            // Load all existing lap data
            LoadAllLapData();

            // If plugin is disabled, do not proceed
            if (!_config.Enabled)
            {
                Log.Warning($"{LOG_PREFIX} Plugin disabled via config.");
                return;
            }

            // Hook into client connection events
            _entryCarManager.ClientConnected += OnClientConnected;
            _entryCarManager.ClientDisconnected += OnClientDisconnected;

            Log.Information($"{LOG_PREFIX} Now hooking 'lap-times'!");
        }

        /// <summary>
        /// Loads all existing lap data from the LapData directory into memory.
        /// </summary>
        private void LoadAllLapData()
        {
            try
            {
                if (!Directory.Exists(DATA_FOLDER))
                {
                    Directory.CreateDirectory(DATA_FOLDER);
                    Log.Information($"{LOG_PREFIX} Created LapData directory.");
                }

                var jsonFiles = Directory.GetFiles(DATA_FOLDER, "*.json");
                foreach (var file in jsonFiles)
                {
                    string trackName = Path.GetFileNameWithoutExtension(file);
                    var trackDict = LoadTrackDictionary(trackName);
                    _allDataByTrack[trackName] = trackDict;
                    Log.Information($"{LOG_PREFIX} Loaded lap data for track: {trackName}");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"{LOG_PREFIX} Failed to load all lap data.");
            }
        }

        /// <summary>
        /// Handles client connection.
        /// </summary>
        private void OnClientConnected(ACTcpClient client, EventArgs args)
        {
            if (!_config.Enabled) return;
            client.LapCompleted += OnClientLapCompleted;
        }

        /// <summary>
        /// Handles client disconnection.
        /// </summary>
        private void OnClientDisconnected(ACTcpClient client, EventArgs args)
        {
            client.LapCompleted -= OnClientLapCompleted;
        }

        /// <summary>
        /// Handles lap completion events.
        /// </summary>
        private async void OnClientLapCompleted(ACTcpClient client, LapCompletedEventArgs e)
        {
            // Log the reception of the lap completed event
            Log.Information($"{LOG_PREFIX} LapCompleted event received for driver GUID: {client.Guid}");

            // Standard checks: Ensure plugin is enabled and lap time is valid
            if (!_config.Enabled || e.Packet.LapTime == 0)
            {
                Log.Debug($"{LOG_PREFIX} Plugin disabled or invalid lap time. Skipping lap processing.");
                return;
            }

            uint lapTimeMs = e.Packet.LapTime;
            string rawTrackName = _acServerConfiguration.Server.Track ?? "UnknownTrack";
            ulong driverGuid = client.Guid;

            // Process and sanitize track name
            string trackName = ProcessTrackName(rawTrackName);
            Log.Debug($"{LOG_PREFIX} Processed Track Name: {trackName}");

            // Ensure dictionary for the track exists
            if (!_allDataByTrack.ContainsKey(trackName))
            {
                _allDataByTrack[trackName] = new Dictionary<ulong, List<LapEntry>>();
                Log.Information($"{LOG_PREFIX} Initialized data storage for track: {trackName}");
            }
            var trackDict = _allDataByTrack[trackName];

            // Extract car name using `client.EntryCar.Model`
            string carName = client.EntryCar?.Model ?? "UnknownCar";

            // Create a new LapEntry without TopSpeed
            var newLap = new LapEntry
            {
                DriverGuid = driverGuid,
                DriverName = client.Name ?? "UnknownDriver",
                CarName = carName,
                LapTimeMs = lapTimeMs,
                Cuts = e.Packet.Cuts
            };

            // Add the new lap to the list if it's unique
            if (!trackDict.ContainsKey(driverGuid))
            {
                trackDict[driverGuid] = new List<LapEntry>();
            }

            var driverLaps = trackDict[driverGuid];
            bool isDuplicate = driverLaps.Any(lap => lap.LapTimeMs == newLap.LapTimeMs && lap.Cuts == newLap.Cuts && lap.CarName == newLap.CarName);

            if (!isDuplicate)
            {
                driverLaps.Add(newLap);
                Log.Debug($"{LOG_PREFIX} Recorded new lap for {newLap.DriverName}: {FormatLapTime(lapTimeMs)}");
            }
            else
            {
                Log.Debug($"{LOG_PREFIX} Duplicate lap detected for {newLap.DriverName}. Skipping addition.");
            }

            // Save data
            SaveTrackDictionary(trackName, trackDict);
            Log.Debug($"{LOG_PREFIX} Saved lap data for track: {trackName}");

            // Broadcast detailed lap info to in-game chat and Discord
            string inGameMessage = BuildLapMessage(newLap, trackName, includeEmojis: false);
            string discordMessage = BuildLapMessage(newLap, trackName, includeEmojis: true);

            Log.Debug($"{LOG_PREFIX} In-Game Chat Message: {inGameMessage}");
            BroadcastInGameMessage(inGameMessage, newLap.DriverName);

            Log.Debug($"{LOG_PREFIX} Discord Message: {discordMessage}");
            if (!string.IsNullOrEmpty(_config.DiscordWebhookUrl))
            {
                await PostToDiscordAsync(discordMessage);
                Log.Information($"{LOG_PREFIX} Posted lap info to Discord for {newLap.DriverName}.");
            }

            // If the lap is clean (no cuts), consider it for the leaderboard
            if (newLap.Cuts == 0)
            {
                Log.Debug($"{LOG_PREFIX} Lap is clean. Updating leaderboard.");
                UpdateLeaderboard(trackName, newLap);
            }
            else
            {
                Log.Debug($"{LOG_PREFIX} Lap has {newLap.Cuts} cuts. Not updating leaderboard.");
            }
        }

        /// <summary>
        /// Processes and sanitizes the track name.
        /// Removes 'ks_' prefix if present and sanitizes the name to prevent invalid paths.
        /// </summary>
        private string ProcessTrackName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "UnknownTrack";

            // 1. Extract the last segment after any '/' or '\' characters
            var segments = raw.Split(new char[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            string segment = segments.Length > 0 ? segments.Last() : raw;

            // 2. Remove 'ks_' prefix if present
            if (segment.StartsWith("ks_", StringComparison.OrdinalIgnoreCase))
            {
                segment = segment.Substring(3);
            }

            // 3. Replace any non-alphanumeric characters (excluding '_' and '-') with underscores
            segment = Regex.Replace(segment, @"[^\w\-]", "_");

            // 4. Remove any remaining '..' to prevent directory traversal
            segment = segment.Replace("..", "");

            // 5. Ensure the segment is not empty after sanitization
            if (string.IsNullOrWhiteSpace(segment))
                return "UnknownTrack";

            return segment;
        }

        /// <summary>
        /// Builds a detailed lap message for in-game chat and Discord.
        /// </summary>
        /// <param name="lap">The lap entry.</param>
        /// <param name="trackName">The name of the track.</param>
        /// <param name="includeEmojis">Whether to include emojis in the message.</param>
        /// <returns>The formatted message string.</returns>
        private string BuildLapMessage(LapEntry lap, string trackName, bool includeEmojis)
        {
            // Determine if the lap was clean
            string cleanStatus = lap.Cuts == 0
                ? (includeEmojis ? "✅ Clean Lap" : "Clean Lap")
                : (includeEmojis ? $"⚠️ **{lap.Cuts} Cuts**" : $"{lap.Cuts} Cuts");

            // Determine the lap icon
            string lapIcon = includeEmojis ? "🏁 " : "";

            // Build the message with conditional emojis and formatting
            string message = $"{lapIcon}**{lap.DriverName}** on **{trackName}** in **{lap.CarName}** `{FormatLapTime(lap.LapTimeMs)}` {cleanStatus}";

            return message;
        }

        /// <summary>
        /// Formats lap time from milliseconds to mm:ss.fff.
        /// </summary>
        private string FormatLapTime(uint lapTimeMs)
        {
            TimeSpan ts = TimeSpan.FromMilliseconds(lapTimeMs);
            return ts.ToString(@"mm\:ss\.fff");
        }

        /// <summary>
        /// Builds the leaderboard message for in-game chat and Discord.
        /// </summary>
        private string BuildLeaderboardMessage(string trackName, List<LapEntry> topList, bool includeEmojis)
        {
            var sb = new StringBuilder();
            string headerEmojis = includeEmojis ? "🏁🏁🏁 " : "";
            string footerEmojis = includeEmojis ? " 👑" : "";

            sb.AppendLine($"{headerEmojis}**Track: {trackName}** - Top {topList.Count} Lap Times{footerEmojis}");

            for (int i = 0; i < topList.Count; i++)
            {
                var lap = topList[i];
                TimeSpan ts = TimeSpan.FromMilliseconds(lap.LapTimeMs);
                string timeStr = ts.ToString(@"mm\:ss\.fff");
                sb.AppendLine($"{i + 1}) **{lap.DriverName}** in **{lap.CarName}** — `{timeStr}`");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Updates the leaderboard if the new lap qualifies for the top N clean laps.
        /// </summary>
        private async void UpdateLeaderboard(string trackName, LapEntry newLap)
        {
            var trackDict = _allDataByTrack[trackName];

            // Get all clean laps
            var allCleanLaps = trackDict.Values
                .SelectMany(laps => laps)
                .Where(lap => lap.Cuts == 0)
                .OrderBy(lap => lap.LapTimeMs)
                .ToList();

            // Group by driver and take the best lap per driver
            var bestLaps = allCleanLaps
                .GroupBy(lap => lap.DriverGuid)
                .Select(group => group.OrderBy(lap => lap.LapTimeMs).First())
                .OrderBy(lap => lap.LapTimeMs)
                .Take(_config.MaxTopTimes)
                .ToList();

            // Check if the new lap is within the top N
            bool isInTopN = bestLaps.Any(lap => lap.DriverGuid == newLap.DriverGuid && lap.LapTimeMs == newLap.LapTimeMs);
            if (isInTopN)
            {
                // Generate leaderboard messages
                string inGameLeaderboardMessage = BuildLeaderboardMessage(trackName, bestLaps, includeEmojis: false);
                string discordLeaderboardMessage = BuildLeaderboardMessage(trackName, bestLaps, includeEmojis: true);

                // Broadcast the leaderboard in-game
                BroadcastInGameMessage(inGameLeaderboardMessage, "Leaderboard Update");

                Log.Information($"{LOG_PREFIX} Broadcasted leaderboard update.");

                // Post to Discord if webhook is configured
                if (!string.IsNullOrEmpty(_config.DiscordWebhookUrl))
                {
                    await PostToDiscordAsync(discordLeaderboardMessage);
                    Log.Information($"{LOG_PREFIX} Posted leaderboard to Discord.");
                }
            }
        }

        /// <summary>
        /// Broadcasts a message to in-game chat.
        /// </summary>
        /// <param name="message">The message to broadcast.</param>
        /// <param name="source">The source or title of the message.</param>
        private void BroadcastInGameMessage(string message, string source)
        {
            _entryCarManager.BroadcastPacket(new ChatMessage
            {
                SessionId = 255, // 255 typically represents server messages
                Message = message
            });
            Log.Information($"{LOG_PREFIX} Broadcasted {source}.");
        }

        /// <summary>
        /// Posts a message to Discord via webhook.
        /// </summary>
        private async Task PostToDiscordAsync(string message)
        {
            try
            {
                // Minimal JSON escape
                string escaped = message
                    .Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r");

                string payload = $"{{\"content\":\"{escaped}\"}}";

                var resp = await _httpClient.PostAsync(_config.DiscordWebhookUrl,
                    new StringContent(payload, Encoding.UTF8, "application/json"));

                if (!resp.IsSuccessStatusCode)
                {
                    Log.Error($"{LOG_PREFIX} Discord POST failed: {resp.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"{LOG_PREFIX} Error posting to Discord.");
            }
        }

        /// <summary>
        /// Loads the lap dictionary for a specific track from disk.
        /// </summary>
        private Dictionary<ulong, List<LapEntry>> LoadTrackDictionary(string trackName)
        {
            string safeTrack = trackName.Replace(" ", "_");
            string filePath = Path.Combine(DATA_FOLDER, $"{safeTrack}.json");

            if (!File.Exists(filePath))
                return new Dictionary<ulong, List<LapEntry>>();

            try
            {
                string json = File.ReadAllText(filePath);
                var dict = JsonSerializer.Deserialize<Dictionary<ulong, List<LapEntry>>>(json);
                return dict ?? new Dictionary<ulong, List<LapEntry>>();
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"{LOG_PREFIX} Failed to load dictionary for track: {trackName}");
                return new Dictionary<ulong, List<LapEntry>>();
            }
        }

        /// <summary>
        /// Saves the lap dictionary for a specific track to disk.
        /// </summary>
        private void SaveTrackDictionary(string trackName, Dictionary<ulong, List<LapEntry>> trackDict)
        {
            string safeTrack = trackName.Replace(" ", "_");
            string filePath = Path.Combine(DATA_FOLDER, $"{safeTrack}.json");

            try
            {
                // Serialize and save the in-memory data
                string json = JsonSerializer.Serialize(trackDict, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);
                Log.Debug($"{LOG_PREFIX} Saved lap data for track: {trackName}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"{LOG_PREFIX} Failed to save dictionary for track: {trackName}");
            }
        }
    }

    /// <summary>
    /// Stores lap info for each driver.
    /// </summary>
    public class LapEntry
    {
        public ulong DriverGuid { get; set; }
        public string DriverName { get; set; } = "";
        public string CarName { get; set; } = "";
        public uint LapTimeMs { get; set; }
        public int Cuts { get; set; }
    }
}
