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
        private const int MAX_MESSAGE_LENGTH = 200;       // Safe limit based on logs

        private readonly ACServerConfiguration _acServerConfiguration;
        private readonly EntryCarManager _entryCarManager;
        private readonly SessionManager _sessionManager;
        private readonly CSPClientMessageTypeManager _cspClientMessageTypeManager;
        private readonly MyLapTimesConfiguration _config;
        private readonly HttpClient _httpClient = new();

        private readonly Dictionary<string, Dictionary<ulong, List<LapEntry>>> _allDataByTrack =
            new Dictionary<string, Dictionary<ulong, List<LapEntry>>>();

        public MyLapTimesPlugin(
            ACServerConfiguration acServerConfig,
            EntryCarManager entryCarManager,
            SessionManager sessionManager,
            CSPClientMessageTypeManager cspClientMessageTypeManager,
            MyLapTimesConfiguration config)
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

            LoadAllLapData();

            if (!_config.Enabled)
            {
                Log.Warning($"{LOG_PREFIX} Plugin disabled via config.");
                return;
            }

            _entryCarManager.ClientConnected += OnClientConnected;
            _entryCarManager.ClientDisconnected += OnClientDisconnected;

            Log.Information($"{LOG_PREFIX} Now hooking 'lap-times'!");
        }

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

        private void OnClientConnected(ACTcpClient client, EventArgs args)
        {
            if (!_config.Enabled) return;
            client.LapCompleted += OnClientLapCompleted;
            client.ChatMessageReceived += OnChatMessageReceived;
        }

        private void OnClientDisconnected(ACTcpClient client, EventArgs args)
        {
            client.LapCompleted -= OnClientLapCompleted;
            client.ChatMessageReceived -= OnChatMessageReceived;
        }

        private void OnChatMessageReceived(ACTcpClient client, ChatMessageEventArgs args)
        {
            string message = args.ChatMessage.Message.Trim().ToLower();
            string trackName = ProcessTrackName(_acServerConfiguration.Server.Track ?? "UnknownTrack");
            var trackData = _allDataByTrack.ContainsKey(trackName) ? _allDataByTrack[trackName] : new Dictionary<ulong, List<LapEntry>>();

            if (message == "/leaderboard")
            {
                SendLeaderboardToClient(client, trackName, trackData);
            }
            else if (message == "/laptimes")
            {
                SendLapTimesToClient(client, trackName, trackData);
            }
        }

        private void SendLeaderboardToClient(ACTcpClient client, string trackName, Dictionary<ulong, List<LapEntry>> trackData)
        {
            try
            {
                var topLaps = trackData.Values
                    .SelectMany(laps => laps)
                    .Where(lap => lap.Cuts == 0)
                    .GroupBy(lap => lap.DriverGuid)
                    .Select(g => g.OrderBy(lap => lap.LapTimeMs).First())
                    .OrderBy(lap => lap.LapTimeMs)
                    .Take(_config.MaxTopTimes)
                    .ToList();

                var lines = new List<string> { $"Track: {trackName} - Top {_config.MaxTopTimes} Lap Times" };
                if (topLaps.Any())
                {
                    for (int i = 0; i < topLaps.Count; i++)
                    {
                        var lap = topLaps[i];
                        lines.Add($"{i + 1}) {lap.DriverName} in {lap.CarName} — {FormatLapTime(lap.LapTimeMs)}");
                    }
                }
                else
                {
                    lines.Add("No clean laps recorded yet.");
                }

                SendChunkedMessages(client, lines, "leaderboard");
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"{LOG_PREFIX} Failed to send leaderboard to client: {client.Name}");
            }
        }

        private void SendLapTimesToClient(ACTcpClient client, string trackName, Dictionary<ulong, List<LapEntry>> trackData)
        {
            try
            {
                var lines = new List<string> { $"Your Top 3 Laps on {trackName}:" };
                if (trackData.ContainsKey(client.Guid))
                {
                    var userLaps = trackData[client.Guid]
                        .Where(lap => lap.Cuts == 0)
                        .OrderBy(lap => lap.LapTimeMs)
                        .Take(3)
                        .ToList();

                    if (userLaps.Any())
                    {
                        for (int i = 0; i < userLaps.Count; i++)
                        {
                            var lap = userLaps[i];
                            lines.Add($"{i + 1}) {lap.CarName} - {FormatLapTime(lap.LapTimeMs)}");
                        }
                    }
                    else
                    {
                        lines.Add("No clean laps recorded yet.");
                    }
                }
                else
                {
                    lines.Add("No laps recorded yet.");
                }

                SendChunkedMessages(client, lines, "lap times");
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"{LOG_PREFIX} Failed to send lap times to client: {client.Name}");
            }
        }

        private void SendChunkedMessages(ACTcpClient client, List<string> lines, string messageType)
        {
            int delay = 1000; // Start with 1-second delay
            foreach (string line in lines)
            {
                string trimmedLine = line.Length > MAX_MESSAGE_LENGTH ? line.Substring(0, MAX_MESSAGE_LENGTH) : line;
                Log.Information($"{LOG_PREFIX} Sending {messageType} chunk of length {trimmedLine.Length} to {client.Name}");
                Task.Delay(delay).ContinueWith(_ =>
                {
                    try
                    {
                        client.SendPacket(new ChatMessage
                        {
                            SessionId = 255,
                            Message = trimmedLine
                        });
                        Log.Information($"{LOG_PREFIX} Sent {messageType} chunk to client: {client.Name} (GUID: {client.Guid}) with delay {delay}ms");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"{LOG_PREFIX} Failed to send {messageType} chunk to client: {client.Name}");
                    }
                });
                delay += 1000; // Increase delay by 1 second for each chunk
            }
        }

        private async void OnClientLapCompleted(ACTcpClient client, LapCompletedEventArgs e)
        {
            Log.Information($"{LOG_PREFIX} LapCompleted event received for driver GUID: {client.Guid}");

            if (!_config.Enabled || e.Packet.LapTime == 0)
            {
                Log.Debug($"{LOG_PREFIX} Plugin disabled or invalid lap time. Skipping lap processing.");
                return;
            }

            uint lapTimeMs = e.Packet.LapTime;
            string rawTrackName = _acServerConfiguration.Server.Track ?? "UnknownTrack";
            ulong driverGuid = client.Guid;
            string trackName = ProcessTrackName(rawTrackName);
            Log.Debug($"{LOG_PREFIX} Processed Track Name: {trackName}");

            if (!_allDataByTrack.ContainsKey(trackName))
            {
                _allDataByTrack[trackName] = new Dictionary<ulong, List<LapEntry>>();
                Log.Information($"{LOG_PREFIX} Initialized data storage for track: {trackName}");
            }
            var trackDict = _allDataByTrack[trackName];

            string carName = client.EntryCar?.Model ?? "UnknownCar";
            var newLap = new LapEntry
            {
                DriverGuid = driverGuid,
                DriverName = client.Name ?? "UnknownDriver",
                CarName = carName,
                LapTimeMs = lapTimeMs,
                Cuts = e.Packet.Cuts
            };

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
                SaveTrackDictionary(trackName, trackDict);

                string inGameMessage = BuildLapMessage(newLap, trackName, includeEmojis: false);
                Log.Information($"{LOG_PREFIX} Sending lap time message of length {inGameMessage.Length} to {client.Name}");
                client.SendPacket(new ChatMessage
                {
                    SessionId = 255,
                    Message = inGameMessage
                });
                Log.Debug($"{LOG_PREFIX} Sent lap time to client: {newLap.DriverName}");

                if (_config.BroadcastMessages && !string.IsNullOrEmpty(_config.DiscordWebhookUrl))
                {
                    string discordMessage = BuildLapMessage(newLap, trackName, includeEmojis: true);
                    Log.Debug($"{LOG_PREFIX} Discord Message: {discordMessage}");
                    await PostToDiscordAsync(discordMessage);
                    Log.Information($"{LOG_PREFIX} Posted lap info to Discord for {newLap.DriverName}.");
                }

                if (newLap.Cuts == 0)
                {
                    Log.Debug($"{LOG_PREFIX} Lap is clean. Updating leaderboard.");
                    await UpdateLeaderboard(trackName, newLap, client);
                }
            }
        }

        private string ProcessTrackName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "UnknownTrack";

            var segments = raw.Split(new char[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            string segment = segments.Length > 0 ? segments.Last() : raw;
            if (segment.StartsWith("ks_", StringComparison.OrdinalIgnoreCase))
            {
                segment = segment.Substring(3);
            }
            segment = Regex.Replace(segment, @"[^\w\-]", "_").Replace("..", "");
            return string.IsNullOrWhiteSpace(segment) ? "UnknownTrack" : segment;
        }

        private string BuildLapMessage(LapEntry lap, string trackName, bool includeEmojis)
        {
            string cleanStatus = lap.Cuts == 0
                ? (includeEmojis ? "✅ Clean Lap" : "Clean Lap")
                : (includeEmojis ? $"⚠️ **{lap.Cuts} Cuts**" : $"{lap.Cuts} Cuts");
            string lapIcon = includeEmojis ? "🏁 " : "";
            return $"{lapIcon}**{lap.DriverName}** on **{trackName}** in **{lap.CarName}** `{FormatLapTime(lap.LapTimeMs)}` {cleanStatus}";
        }

        private string FormatLapTime(uint lapTimeMs)
        {
            TimeSpan ts = TimeSpan.FromMilliseconds(lapTimeMs);
            return ts.ToString(@"mm\:ss\.fff");
        }

        private string BuildLeaderboardMessage(string trackName, List<LapEntry> topList, bool includeEmojis)
        {
            var lines = new List<string> { $"{(includeEmojis ? "🏁🏁🏁 " : "")}Track: {trackName} - Top {_config.MaxTopTimes} Lap Times{(includeEmojis ? " 👑" : "")}" };
            for (int i = 0; i < topList.Count; i++)
            {
                var lap = topList[i];
                string timeStr = FormatLapTime(lap.LapTimeMs);
                lines.Add($"{i + 1}) {lap.DriverName} in {lap.CarName} — {timeStr}");
            }
            return string.Join("\n", lines);
        }

        private async Task UpdateLeaderboard(string trackName, LapEntry newLap, ACTcpClient client)
        {
            var trackDict = _allDataByTrack[trackName];
            var allCleanLaps = trackDict.Values
                .SelectMany(laps => laps)
                .Where(lap => lap.Cuts == 0)
                .OrderBy(lap => lap.LapTimeMs)
                .ToList();

            var bestLaps = allCleanLaps
                .GroupBy(lap => lap.DriverGuid)
                .Select(group => group.OrderBy(lap => lap.LapTimeMs).First())
                .OrderBy(lap => lap.LapTimeMs)
                .Take(_config.MaxTopTimes)
                .ToList();

            bool isInTopN = bestLaps.Any(lap => lap.DriverGuid == newLap.DriverGuid && lap.LapTimeMs == newLap.LapTimeMs);
            if (isInTopN)
            {
                string inGameLeaderboardMessage = BuildLeaderboardMessage(trackName, bestLaps, includeEmojis: false);
                string discordLeaderboardMessage = BuildLeaderboardMessage(trackName, bestLaps, includeEmojis: true);

                if (_config.BroadcastMessages)
                {
                    var lines = inGameLeaderboardMessage.Split('\n').ToList();
                    SendChunkedBroadcast(lines, "Leaderboard Update");
                    Log.Information($"{LOG_PREFIX} Broadcasted leaderboard update.");
                }

                if (_config.BroadcastMessages && !string.IsNullOrEmpty(_config.DiscordWebhookUrl))
                {
                    await PostToDiscordAsync(discordLeaderboardMessage);
                    Log.Information($"{LOG_PREFIX} Posted leaderboard to Discord.");
                }
            }
        }

        private void SendChunkedBroadcast(List<string> lines, string source)
        {
            int delay = 1000; // Start with 1-second delay
            foreach (string line in lines)
            {
                string trimmedLine = line.Length > MAX_MESSAGE_LENGTH ? line.Substring(0, MAX_MESSAGE_LENGTH) : line;
                Log.Information($"{LOG_PREFIX} Sending broadcast chunk of length {trimmedLine.Length} for {source}");
                Task.Delay(delay).ContinueWith(_ =>
                {
                    try
                    {
                        _entryCarManager.BroadcastPacket(new ChatMessage
                        {
                            SessionId = 255,
                            Message = trimmedLine
                        });
                        Log.Information($"{LOG_PREFIX} Broadcasted {source} chunk with delay {delay}ms");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"{LOG_PREFIX} Failed to broadcast {source} chunk");
                    }
                });
                delay += 1000; // Increase delay by 1 second for each chunk
            }
        }

        private async Task PostToDiscordAsync(string message)
        {
            try
            {
                string escaped = message.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
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

        private Dictionary<ulong, List<LapEntry>> LoadTrackDictionary(string trackName)
        {
            string safeTrack = trackName.Replace(" ", "_");
            string filePath = Path.Combine(DATA_FOLDER, $"{safeTrack}.json");
            if (!File.Exists(filePath)) return new Dictionary<ulong, List<LapEntry>>();
            try
            {
                string json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<Dictionary<ulong, List<LapEntry>>>(json) ?? new Dictionary<ulong, List<LapEntry>>();
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"{LOG_PREFIX} Failed to load dictionary for track: {trackName}");
                return new Dictionary<ulong, List<LapEntry>>();
            }
        }

        private void SaveTrackDictionary(string trackName, Dictionary<ulong, List<LapEntry>> trackDict)
        {
            string safeTrack = trackName.Replace(" ", "_");
            string filePath = Path.Combine(DATA_FOLDER, $"{safeTrack}.json");
            try
            {
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

    public class LapEntry
    {
        public ulong DriverGuid { get; set; }
        public string DriverName { get; set; } = "";
        public string CarName { get; set; } = "";
        public uint LapTimeMs { get; set; }
        public int Cuts { get; set; }
    }
}
