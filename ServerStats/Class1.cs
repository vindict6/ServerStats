// ServerStats.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;

namespace ServerStats
{
    [MinimumApiVersion(80)]
    public class PlayerStatsEventTracker : BasePlugin
    {
        // --- Data Structures for the Database ---

        public class MatchDatabase
        {
            public string MatchID { get; set; } = "";
            public string MapName { get; set; } = "";
            public string WorkshopID { get; set; } = "";
            public string CollectionID { get; set; } = "";
            public DateTime StartTime { get; set; }
            public DateTime LastUpdated { get; set; }
            public int CTWins { get; set; }
            public int TWins { get; set; }
            public bool IsWarmup { get; set; }
            public List<PlayerMatchData> Players { get; set; } = new();
        }

        public class PlayerMatchData
        {
            public ulong SteamID { get; set; }
            public string Name { get; set; } = "Unknown";
            public int TeamNum { get; set; } // 1=Spec, 2=T, 3=CT
            public bool IsBot { get; set; }

            // Cumulative Stats
            public int Kills { get; set; }
            public int Deaths { get; set; }
            public int Assists { get; set; }
            public int ZeusKills { get; set; }

            // Snapshot Stats (Latest known state)
            public int MVPs { get; set; }
            public int Score { get; set; }
            public int Money { get; set; }
            public bool IsAlive { get; set; }
            public int Ping { get; set; }
            public string Inventory { get; set; } = "";
        }

        // We use SteamID (ulong) as key to persist stats if player reconnects
        private readonly ConcurrentDictionary<ulong, PlayerMatchData> _playerStats = new();

        private int _ctWins = 0;
        private int _tWins = 0;
        private string _currentMatchId = "";
        private DateTime _matchStartTime;

        private const int TEAM_CT_MANAGER_ID = 2;
        private const int TEAM_T_MANAGER_ID = 3;

        // Workshop Config Data
        private readonly Dictionary<string, string> _workshopMapIds = new();
        private string _loadedCollectionId = "N/A";
        private readonly List<string> _loadLog = new();

        // --- Configuration Data ---
        private bool _usesMatchLibrarian = true;

        // Fields for File Watching and Warmup Tracking
        private FileSystemWatcher? _fileWatcher;
        private DateTime _lastReloadTime = DateTime.MinValue;
        private bool _wasWarmup = false;
        private string _lastMap = "";

        public override string ModuleName => "PlayerStatsEventTracker";
        public override string ModuleVersion => "3.8.0"; // Version Bump for Split Paths
        public override string ModuleAuthor => "VinSix";

        public override void Load(bool hotReload)
        {
            RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath, HookMode.Post);
            RegisterEventHandler<EventRoundOfficiallyEnded>(OnRoundEnded, HookMode.Post);
            RegisterEventHandler<EventRoundPrestart>(OnRoundPrestart, HookMode.Post);
            RegisterEventHandler<EventMapShutdown>(OnMapShutdown, HookMode.Post);
            RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEnd, HookMode.Post);

            LoadConfigIni();   // Load general settings
            LoadWorkshopIni(); // Load map mappings
            InitializeFileWatcher();

            _lastMap = Server.MapName;
            StartNewMatchId();

            AddTimer(0.5f, () => { _wasWarmup = IsWarmup(); });

            AddCommand("css_players", "Print tracked player stats (humans and bots)", (caller, cmdInfo) =>
            {
                PrintPlayerStats(caller, cmdInfo);
            });

            AddCommand("css_playerslog", "Show the log of loading workshop.ini", (caller, cmdInfo) =>
            {
                CmdLog(caller, cmdInfo);
            });

            AddCommand("css_collectionid", "Output the server's loaded collection ID", (caller, cmdInfo) =>
            {
                cmdInfo.ReplyToCommand($"Server Collection ID: {_loadedCollectionId}");
            });

            AddCommand("css_matchstats_status", "Check if database recording is enabled", (caller, cmdInfo) =>
            {
                cmdInfo.ReplyToCommand($"[ServerStats] Database Recording (UsesMatchLibrarian): {(_usesMatchLibrarian ? "ENABLED" : "DISABLED")}");
            });
        }

        public override void Unload(bool hotReload)
        {
            SaveMatchData();

            if (_fileWatcher != null)
            {
                _fileWatcher.EnableRaisingEvents = false;
                _fileWatcher.Changed -= OnConfigFileChanged;
                _fileWatcher.Dispose();
                _fileWatcher = null;
            }
        }

        // --- SPLIT PATH LOGIC ---

        // 1. Input Directory: .../configs/plugins/ServerStats/
        private string ServerStatsConfigDir => Path.Combine(Server.GameDirectory, "csgo", "addons", "counterstrikesharp", "configs", "plugins", "ServerStats");
        private string WorkshopIniPath => Path.Combine(ServerStatsConfigDir, "workshop.ini");
        private string GeneralConfigPath => Path.Combine(ServerStatsConfigDir, "config.ini");

        // 2. Output Directory: .../configs/plugins/MatchLibrarian/matches/
        private string MatchLibrarianDir => Path.Combine(Server.GameDirectory, "csgo", "addons", "counterstrikesharp", "configs", "plugins", "MatchLibrarian");
        private string MatchesDirPath => Path.Combine(MatchLibrarianDir, "matches");

        private void StartNewMatchId()
        {
            _currentMatchId = DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss");
            _matchStartTime = DateTime.Now;
            _playerStats.Clear();
            _ctWins = 0;
            _tWins = 0;
            Console.WriteLine($"[PlayerStats] Started new Match ID: {_currentMatchId}");
        }

        private void InitializeFileWatcher()
        {
            try
            {
                // Ensure Input Directory Exists (ServerStats)
                if (!Directory.Exists(ServerStatsConfigDir)) Directory.CreateDirectory(ServerStatsConfigDir);

                // Ensure Output Directory Exists (MatchLibrarian)
                if (!Directory.Exists(MatchLibrarianDir)) Directory.CreateDirectory(MatchLibrarianDir);
                if (!Directory.Exists(MatchesDirPath)) Directory.CreateDirectory(MatchesDirPath);

                // Watch the ServerStats folder for config changes
                _fileWatcher = new FileSystemWatcher(ServerStatsConfigDir);
                _fileWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
                _fileWatcher.Filter = "*.ini";
                _fileWatcher.Changed += OnConfigFileChanged;
                _fileWatcher.EnableRaisingEvents = true;
                Console.WriteLine($"[PlayerStats] Watching for config changes in: {ServerStatsConfigDir}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlayerStats] Failed to initialize file watcher: {ex.Message}");
            }
        }

        private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
        {
            if ((DateTime.Now - _lastReloadTime).TotalSeconds < 1) return;
            _lastReloadTime = DateTime.Now;

            if (e.Name != null && e.Name.Contains("workshop.ini"))
            {
                Server.NextFrame(LoadWorkshopIni);
            }
            else if (e.Name != null && e.Name.Contains("config.ini"))
            {
                Server.NextFrame(LoadConfigIni);
            }
        }

        private void LoadConfigIni()
        {
            try
            {
                if (!File.Exists(GeneralConfigPath))
                {
                    File.WriteAllText(GeneralConfigPath, "UsesMatchLibrarian=true");
                    _usesMatchLibrarian = true;
                    return;
                }

                foreach (var line in File.ReadAllLines(GeneralConfigPath))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//") || trimmed.StartsWith("#")) continue;

                    var parts = trimmed.Split('=');
                    if (parts.Length != 2) continue;

                    var key = parts[0].Trim();
                    var value = parts[1].Trim();

                    if (key.Equals("UsesMatchLibrarian", StringComparison.OrdinalIgnoreCase))
                    {
                        if (bool.TryParse(value, out bool result))
                        {
                            _usesMatchLibrarian = result;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlayerStats] Error loading config.ini: {ex.Message}");
            }
        }

        private void LoadWorkshopIni()
        {
            _workshopMapIds.Clear();
            _loadedCollectionId = "N/A"; // Reset before load
            _loadLog.Clear();
            _loadLog.Add($"Reading workshop.ini from: {WorkshopIniPath}");

            try
            {
                if (!File.Exists(WorkshopIniPath))
                {
                    _loadLog.Add("Error: workshop.ini not found.");
                    Console.WriteLine($"[PlayerStats] Error: workshop.ini not found at {WorkshopIniPath}");
                    return;
                }

                string[] lines = File.ReadAllLines(WorkshopIniPath);
                foreach (string line in lines)
                {
                    string trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//") || trimmed.StartsWith("#")) continue;

                    // Expect format: key=value
                    string[] parts = trimmed.Split('=');
                    if (parts.Length < 2) continue;

                    string key = parts[0].Trim();
                    string value = parts[1].Trim();

                    if (key.Equals("collection_id", StringComparison.OrdinalIgnoreCase))
                    {
                        _loadedCollectionId = value;
                        _loadLog.Add($"-> Set Collection ID: {_loadedCollectionId}");
                    }
                    else
                    {
                        // Assume it is a map map (e.g. de_zoo=12345)
                        if (!_workshopMapIds.ContainsKey(key))
                        {
                            _workshopMapIds.Add(key, value);
                        }
                    }
                }
                _loadLog.Add($"DONE: Loaded CollectionID: {_loadedCollectionId} | Mapped Maps: {_workshopMapIds.Count}");
                Console.WriteLine($"[PlayerStats] Loaded CollectionID: {_loadedCollectionId} and {_workshopMapIds.Count} map IDs.");
            }
            catch (Exception ex)
            {
                _loadLog.Add($"EXCEPTION: {ex.Message}");
                Console.WriteLine($"[PlayerStats] Exception loading workshop.ini: {ex.Message}");
            }
        }

        private void CmdLog(CCSPlayerController? caller, CommandInfo info)
        {
            info.ReplyToCommand("--- workshop.ini Load Log ---");
            foreach (var msg in _loadLog) info.ReplyToCommand(msg);
            info.ReplyToCommand("--- End Log ---");
        }

        private bool IsWarmup()
        {
            try
            {
                var gameRulesProxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
                if (gameRulesProxy == null || !gameRulesProxy.IsValid || gameRulesProxy.GameRules == null) return false;
                return gameRulesProxy.GameRules.WarmupPeriod;
            }
            catch { return false; }
        }

        private HookResult OnRoundPrestart(EventRoundPrestart @event, GameEventInfo info)
        {
            string currentMap = Server.MapName;
            bool isWarmupNow = IsWarmup();

            if (!_wasWarmup && isWarmupNow && currentMap == _lastMap)
            {
                Console.WriteLine("[PlayerStats] Warmup restart detected. Resetting stats.");
                StartNewMatchId();
            }
            else if (currentMap != _lastMap)
            {
                Console.WriteLine($"[PlayerStats] Map changed from {_lastMap} to {currentMap}. New Match.");
                _lastMap = currentMap;
                StartNewMatchId();
            }

            _wasWarmup = isWarmupNow;
            return HookResult.Continue;
        }

        private HookResult OnMapShutdown(EventMapShutdown @event, GameEventInfo info)
        {
            SaveMatchData();
            return HookResult.Continue;
        }

        private HookResult OnMatchEnd(EventCsWinPanelMatch @event, GameEventInfo info)
        {
            UpdateTeamScores();
            SaveMatchData();
            Console.WriteLine($"[PlayerStats] Match Finished. Final data saved for ID: {_currentMatchId}");
            return HookResult.Continue;
        }

        // --- Core Tracking Logic ---

        private PlayerMatchData GetOrAddPlayer(CCSPlayerController player)
        {
            if (player == null || !player.IsValid) return new PlayerMatchData();

            ulong steamId = player.SteamID;
            if (steamId == 0) steamId = (ulong)player.Handle.ToInt64();

            return _playerStats.GetOrAdd(steamId, _ => new PlayerMatchData
            {
                SteamID = steamId,
                Name = player.PlayerName ?? "Unknown",
                IsBot = player.IsBot,
                TeamNum = player.TeamNum
            });
        }

        private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
        {
            if (IsWarmup()) return HookResult.Continue;

            try
            {
                var victim = @event.Userid;
                var attacker = @event.Attacker;
                var assister = @event.Assister;

                if (victim != null && victim.IsValid)
                {
                    var data = GetOrAddPlayer(victim);
                    data.Deaths++;
                    UpdateSnapshotData(victim, data);
                }

                if (attacker != null && attacker.IsValid && attacker != victim)
                {
                    var data = GetOrAddPlayer(attacker);
                    data.Kills++;

                    string weaponName = @event.Weapon ?? "";
                    if (weaponName.Contains("taser", StringComparison.OrdinalIgnoreCase))
                    {
                        data.ZeusKills++;
                    }
                    UpdateSnapshotData(attacker, data);
                }

                if (assister != null && assister.IsValid && assister != attacker && assister != victim)
                {
                    var data = GetOrAddPlayer(assister);
                    data.Assists++;
                    UpdateSnapshotData(assister, data);
                }
            }
            catch { }

            return HookResult.Continue;
        }

        private HookResult OnRoundEnded(EventRoundOfficiallyEnded @event, GameEventInfo info)
        {
            if (IsWarmup()) return HookResult.Continue;

            UpdateTeamScores();

            var connectedPlayers = Utilities.GetPlayers().Where(p => p.IsValid && p.Connected == PlayerConnectedState.PlayerConnected);
            foreach (var p in connectedPlayers)
            {
                var data = GetOrAddPlayer(p);
                UpdateSnapshotData(p, data);
            }

            SaveMatchData();
            return HookResult.Continue;
        }

        private void UpdateSnapshotData(CCSPlayerController p, PlayerMatchData data)
        {
            if (p == null || !p.IsValid) return;

            data.Name = p.PlayerName ?? data.Name;
            data.TeamNum = p.TeamNum;
            data.Score = GetPlayerScore(p);
            data.MVPs = GetPlayerMVP(p);
            data.Money = GetPlayerMoney(p);
            data.Inventory = GetPlayerInventory(p);
            data.Ping = p.IsBot ? 0 : (int)p.Ping;

            bool isAlive = false;
            if (p.PlayerPawn?.Value is CCSPlayerPawn pawn && pawn.IsValid && pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE)
            {
                isAlive = true;
            }
            data.IsAlive = isAlive;
        }

        private void SaveMatchData()
        {
            if (!_usesMatchLibrarian) return;
            if (string.IsNullOrEmpty(_currentMatchId)) return;

            try
            {
                var mapName = Server.MapName;
                // Cross-reference map name with workshop.ini data
                string workshopId = _workshopMapIds.TryGetValue(mapName, out var id) ? id : "N/A";

                var currentMatchDb = new MatchDatabase
                {
                    MatchID = _currentMatchId,
                    MapName = mapName,
                    WorkshopID = workshopId,
                    CollectionID = _loadedCollectionId,
                    StartTime = _matchStartTime,
                    LastUpdated = DateTime.Now,
                    CTWins = _ctWins,
                    TWins = _tWins,
                    IsWarmup = IsWarmup(),
                    Players = _playerStats.Values.ToList()
                };

                var now = DateTime.Now;
                var yearFolder = now.ToString("yyyy");
                var monthFolder = now.ToString("MM");

                // --- SAVING TO MATCHLIBRARIAN FOLDER ---
                var dailyDirectory = Path.Combine(MatchesDirPath, yearFolder, monthFolder);

                if (!Directory.Exists(dailyDirectory)) Directory.CreateDirectory(dailyDirectory);

                var dayFileName = $"{now.ToString("dd")}.json";
                var fullFilePath = Path.Combine(dailyDirectory, dayFileName);

                List<MatchDatabase> dailyMatches;

                if (File.Exists(fullFilePath))
                {
                    var existingJson = File.ReadAllText(fullFilePath);
                    try { dailyMatches = JsonSerializer.Deserialize<List<MatchDatabase>>(existingJson) ?? new List<MatchDatabase>(); }
                    catch { dailyMatches = new List<MatchDatabase>(); }
                }
                else
                {
                    dailyMatches = new List<MatchDatabase>();
                }

                var existingIndex = dailyMatches.FindIndex(m => m.MatchID == _currentMatchId);

                if (existingIndex != -1) dailyMatches[existingIndex] = currentMatchDb;
                else dailyMatches.Add(currentMatchDb);

                var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(fullFilePath, JsonSerializer.Serialize(dailyMatches, jsonOptions));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlayerStats] Error saving match data: {ex.Message}");
            }
        }

        private void PrintPlayerStats(CCSPlayerController? caller, CommandInfo cmd)
        {
            UpdateTeamScores();

            var allPlayers = Utilities.GetPlayers()
                .Where(p => p != null && p.IsValid && p.Connected == PlayerConnectedState.PlayerConnected)
                .ToList();

            var humans = allPlayers.Where(p => !p.IsBot && !p.IsHLTV).ToList();
            var bots = allPlayers.Where(p => p.IsBot && !p.IsHLTV).ToList();

            string zeusLeaderName = "None";
            int maxZeusKills = 0;

            foreach (var player in allPlayers)
            {
                var data = GetOrAddPlayer(player);
                if (data.ZeusKills > maxZeusKills)
                {
                    maxZeusKills = data.ZeusKills;
                    zeusLeaderName = player.PlayerName ?? "Unknown";
                }
            }

            if (maxZeusKills > 0)
            {
                cmd.ReplyToCommand($"--- Zeus Leader: {zeusLeaderName} ({maxZeusKills} Kills) ---");
            }

            bool isWarmup = IsWarmup();
            string mapName = Server.MapName;

            // STRICT LOOKUP: Get Workshop ID from the dictionary loaded from workshop.ini
            string workshopId = _workshopMapIds.TryGetValue(mapName, out var id) ? id : "N/A";

            string recordingStatus = _usesMatchLibrarian ? "ON" : "OFF";

            cmd.ReplyToCommand($"--- Status: Map: {mapName} | ID: {_currentMatchId} | CollectionID: {_loadedCollectionId} | WorkshopID: {workshopId} | Warmup: {(isWarmup ? "Yes" : "No")} | DB: {recordingStatus} ---");
            cmd.ReplyToCommand($"--- Humans: {humans.Count}, Bots: {bots.Count} | T Wins: {_tWins}, CT Wins: {_ctWins} ---");

            if (humans.Any())
            {
                cmd.ReplyToCommand("--- Humans ---");
                foreach (var p in humans) PrintSinglePlayerStat(p, cmd);
            }

            if (bots.Any())
            {
                if (humans.Any()) cmd.ReplyToCommand(" ");
                cmd.ReplyToCommand("--- Bots ---");
                foreach (var p in bots) PrintSinglePlayerStat(p, cmd);
            }

            cmd.ReplyToCommand("--- End ---");
        }

        private void PrintSinglePlayerStat(CCSPlayerController p, CommandInfo cmd)
        {
            var data = GetOrAddPlayer(p);
            UpdateSnapshotData(p, data);

            var pingStr = data.IsBot ? "BOT" : data.Ping.ToString();

            string teamStr = "None";
            switch (data.TeamNum)
            {
                case 2: teamStr = "T"; break;
                case 3: teamStr = "CT"; break;
                case 1: teamStr = "SPEC"; break;
            }

            cmd.ReplyToCommand(
                $"[{p.Slot}] {data.Name} | Team:{teamStr} K:{data.Kills} D:{data.Deaths} A:{data.Assists} Z:{data.ZeusKills} MVP:{data.MVPs} Score:{data.Score} Money:${data.Money} Alive:{(data.IsAlive ? "Yes" : "No")} Ping:{pingStr}"
            );

            if (data.IsAlive && !string.IsNullOrEmpty(data.Inventory))
            {
                cmd.ReplyToCommand($" -> Inventory: {data.Inventory}");
            }
        }

        // --- Helper Methods ---

        private string GetPlayerInventory(CCSPlayerController player)
        {
            if (player == null || !player.IsValid || player.PlayerPawn == null || !player.PlayerPawn.IsValid)
                return "N/A";

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null || pawn.WeaponServices.MyWeapons == null)
                return "None";

            List<string> weaponNames = new List<string>();
            foreach (var weaponHandle in pawn.WeaponServices.MyWeapons)
            {
                var weapon = weaponHandle.Value;
                if (weapon == null || !weapon.IsValid) continue;

                string name = weapon.DesignerName;
                if (name.StartsWith("weapon_")) name = name.Substring(7);
                weaponNames.Add(name);
            }
            return weaponNames.Count == 0 ? "None" : string.Join(", ", weaponNames);
        }

        private int GetPlayerMoney(CCSPlayerController player)
        {
            if (player == null || !player.IsValid) return 0;
            var moneyServices = player.InGameMoneyServices;
            return moneyServices == null ? 0 : moneyServices.Account;
        }

        private int GetPlayerScore(CCSPlayerController player)
        {
            if (player == null || !player.IsValid) return 0;
            try
            {
                var pi = player.GetType().GetProperty("Score", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (pi != null && pi.GetValue(player) is int vi) return vi;
            }
            catch { }
            return 0;
        }

        private int GetPlayerMVP(CCSPlayerController player)
        {
            if (player == null || !player.IsValid) return 0;
            try
            {
                var pi = player.GetType().GetProperty("MVPs", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (pi != null && pi.GetValue(player) is int vi) return vi;
            }
            catch { }
            return 0;
        }

        private void UpdateTeamScores()
        {
            _ctWins = 0;
            _tWins = 0;
            try
            {
                var teams = Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager");
                foreach (var team in teams)
                {
                    if (team == null || !team.IsValid) continue;
                    if (team.TeamNum == TEAM_T_MANAGER_ID) _tWins = team.Score;
                    else if (team.TeamNum == TEAM_CT_MANAGER_ID) _ctWins = team.Score;
                }
            }
            catch { }
        }
    }
}