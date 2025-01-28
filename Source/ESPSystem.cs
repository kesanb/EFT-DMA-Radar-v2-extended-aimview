using System.Numerics;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;  // ReadOnlyDictionaryのために追加
using SkiaSharp;

namespace eft_dma_radar
{
    public class ESPSystem : IDisposable
    {
        private readonly Config _config;
        private readonly CancellationTokenSource _cancellationSource;
        private readonly ConcurrentDictionary<string, PlayerBoneData> _boneCache;
        private readonly object _updateLock = new();
        private Task _updateTask;
        private Task _cleanupTask;
        private bool _isDisposed;

        // プレイヤーデータ
        private Player _localPlayer;
        private ReadOnlyDictionary<string, Player> _allPlayers;

        public Player LocalPlayer => _localPlayer;

        private readonly object _playerLock = new object();
        private volatile bool _isUpdating = false;

        public ESPSystem(Config config)
        {
            _config = config;
            _cancellationSource = new CancellationTokenSource();
            _boneCache = new ConcurrentDictionary<string, PlayerBoneData>();
            StartUpdateLoop();
        }

        public void UpdatePlayers(Player localPlayer, ReadOnlyDictionary<string, Player> allPlayers)
        {
            lock (_playerLock)
            {
                _localPlayer = localPlayer;
                _allPlayers = allPlayers;
            }
        }

        private void StartUpdateLoop()
        {
            _updateTask = Task.Run(async () =>
            {
                var updateInterval = TimeSpan.FromMilliseconds(16); // ~60Hz
                var nextUpdate = DateTime.UtcNow;

                while (!_cancellationSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        var now = DateTime.UtcNow;
                        if (now >= nextUpdate)
                        {
                            await UpdateBoneCache();
                            nextUpdate = now + updateInterval;
                        }
                        else
                        {
                            var delay = nextUpdate - now;
                            if (delay > TimeSpan.Zero)
                            {
                                await Task.Delay(delay, _cancellationSource.Token);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Program.Log($"[ESPSystem] UpdateLoop error: {ex.Message}");
                        await Task.Delay(100, _cancellationSource.Token); // エラー時は少し待機
                    }
                }
            }, _cancellationSource.Token);

            // クリーンアップタスクは5秒間隔で実行
            _cleanupTask = Task.Run(async () =>
            {
                while (!_cancellationSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        CleanupCache();
                        await Task.Delay(5000, _cancellationSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, _cancellationSource.Token);
        }

        private async Task UpdateBoneCache()
        {
            if (_isUpdating || _allPlayers == null)
                return;

            _isUpdating = true;
            try
            {
                ReadOnlyDictionary<string, Player> players;
                lock (_playerLock)
                {
                    players = _allPlayers;
                }

                var objectSettings = _config.AimviewSettings.ObjectSettings["Player"];
                var maxDistance = Math.Max(objectSettings.PaintDistance, objectSettings.TextDistance);

                foreach (var kvp in players)
                {
                    if (_cancellationSource.Token.IsCancellationRequested)
                        break;

                    try
                    {
                        var player = kvp.Value;
                        if (player == null || player.Bones == null)
                            continue;

                        Vector3 localPlayerPos;
                        lock (_playerLock)
                        {
                            localPlayerPos = _localPlayer?.Position ?? Vector3.Zero;
                        }
                        var distance = Vector3.Distance(localPlayerPos, player.Position);

                        // 描画範囲外のプレイヤーはスキップ
                        if (distance > maxDistance)
                        {
                            // キャッシュから削除
                            _boneCache.TryRemove(player.ProfileID, out _);
                            continue;
                        }

                        if (!_boneCache.ContainsKey(player.ProfileID) || ShouldUpdatePlayer(player.ProfileID, distance))
                        {
                            await Task.Run(() => UpdatePlayerBones(player, distance), _cancellationSource.Token);
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.Log($"[ESPSystem] Error updating player {kvp.Key}: {ex.Message}");
                    }
                }
            }
            finally
            {
                _isUpdating = false;
            }
        }

        private void UpdatePlayerBones(Player player, float distance) // ボーンの更新(メモリアクセス)
        {
            try
            {
                if (player?.Bones == null)
                    return;

                var bonePositions = new Dictionary<PlayerBones, Vector3>();
                foreach (var kvp in player.Bones)
                {
                    kvp.Value.UpdatePosition();
                    bonePositions[kvp.Key] = kvp.Value.Position;
                }

                _boneCache[player.ProfileID] = new PlayerBoneData
                {
                    BonePositions = bonePositions,
                    LastUpdateTime = DateTime.UtcNow,
                    LastDistance = distance
                };
            }
            catch (Exception ex)
            {
                Program.Log($"[ESPSystem] Error updating bones for player {player?.ProfileID}: {ex.Message}");
            }
        }

        private bool ShouldUpdatePlayer(string profileId, float distance)
        {
            if (!_boneCache.TryGetValue(profileId, out var data))
                return true;

            var updateInterval = 33; // ~30Hz
            var timeSinceLastUpdate = (DateTime.UtcNow - data.LastUpdateTime).TotalMilliseconds;
            return timeSinceLastUpdate >= updateInterval;
        }

        private void CleanupCache()
        {
            if (_allPlayers == null) return;

            var now = DateTime.UtcNow;
            var keysToRemove = _boneCache
                .Where(kvp => 
                    (now - kvp.Value.LastUpdateTime).TotalSeconds > 2 ||
                    !_allPlayers.ContainsKey(kvp.Key))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _boneCache.TryRemove(key, out _);
            }
        }

        public Dictionary<PlayerBones, Vector3> GetPlayerBonePositions(string profileId)
        {
            try
            {
                if (_boneCache.TryGetValue(profileId, out var data))
                {
                    return new Dictionary<PlayerBones, Vector3>(data.BonePositions);
                }
                return null;
            }
            catch (Exception ex)
            {
                Program.Log($"[ESPSystem] GetPlayerBonePositions error: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _cancellationSource.Cancel();
            try
            {
                Task.WaitAll(new[] { _updateTask, _cleanupTask }, 1000);
            }
            catch { }
            _cancellationSource.Dispose();
        }
    }

    public class PlayerBoneData
    {
        public Dictionary<PlayerBones, Vector3> BonePositions { get; set; }
        public DateTime LastUpdateTime { get; set; }
        public float LastDistance { get; set; }

        public PlayerBoneData()
        {
            BonePositions = new Dictionary<PlayerBones, Vector3>();
            LastUpdateTime = DateTime.UtcNow;
        }
    }
} 