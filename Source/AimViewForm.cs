using System.Numerics;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Linq;

namespace eft_dma_radar
{
    public partial class AimViewForm : Form
    {
        private readonly SKGLControl aimViewCanvas;
        private readonly Config config;
        private readonly object renderLock = new();
        private float uiScale = 1.0f;
        private const float AIMVIEW_OBJECT_SPACING = 4f;
        private Button btnToggleCrosshair;
        private Button btnToggleESPstyle;
        private Button btnToggleName;
        private Button btnToggleDistance;
        private Button btnToggleWeapon;
        private Button btnToggleHealth;
        private Button btnToggleMaximize;
        private Button btnRefresh;
        private FormBorderStyle previousBorderStyle;
        private bool wasTopMost;
        private readonly Dictionary<string, int> _playerUpdateFrames = new();
        private int _currentFrame = 0;
        private Matrix4x4? _lastViewMatrix;
        private const float RAPID_MOVEMENT_THRESHOLD = 0.1f; // 急速な動きの閾値

        // ESPに必要なデータを保持するプロパティ
        public Player LocalPlayer { get; set; }
        public ReadOnlyDictionary<string, Player> AllPlayers { get; set; }
        public LootManager Loot { get; set; }
        public List<Grenade> Grenades { get; set; }
        public List<Tripwire> Tripwires { get; set; }
        public List<Exfil> Exfils { get; set; }
        public List<Transit> Transits { get; set; }
        public QuestManager QuestManager { get; set; }
        public List<PlayerCorpse> Corpses { get; set; }
        public CameraManager CameraManager { get; set; }

        // RestartRadarのコールバック
        public Action OnRestartRadarRequested { get; set; }

        // キャッシュシステムの追加
        private readonly ConcurrentDictionary<string, SKPaint> _paintCache = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastUpdateTime = new();
        private readonly ConcurrentDictionary<string, Vector2> _lastPositions = new();
        private DateTime _lastCleanupTime = DateTime.Now;
        private const int CLEANUP_INTERVAL_MS = 5000; // 5秒ごとにクリーンアップ

        // メモリプール用の静的フィールド
        private static readonly ConcurrentDictionary<string, Queue<SKPaint>> _paintPool = new();
        private static readonly ConcurrentDictionary<string, Queue<SKPath>> _pathPool = new();
        private const int POOL_SIZE = 100;
        private const int MAX_CACHED_OBJECTS = 1000;

        public AimViewForm(Config config)
        {
            this.config = config;
            
            // フォームの基本設定
            this.Text = "AimView";
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.TopMost = true;
            
            // 背景色の設定
            UpdateBackgroundColor();

            // 最大化切り替えボタンの設定
            this.btnToggleMaximize = new Button
            {
                Text = "□",
                Size = new Size(20, 20),
                Location = new Point(5, 5),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            this.btnToggleMaximize.FlatAppearance.BorderSize = 0;
            this.btnToggleMaximize.Click += this.BtnToggleMaximize_Click;

            // Refreshボタンの追加
            this.btnRefresh = new Button
            {
                Text = "⟳",
                Size = new Size(20, 20),
                Location = new Point(30, 5),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            this.btnRefresh.FlatAppearance.BorderSize = 0;
            this.btnRefresh.Font = new Font("Segoe UI", 10f);
            this.btnRefresh.Click += BtnRefresh_Click;

            // クロスヘア切り替えボタンの設定を修正
            this.btnToggleCrosshair = new Button
            {
                Text = "CH",
                Size = new Size(30, 20),
                Location = new Point(55, 5),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = this.GetCrosshairButtonColor(),
                Cursor = Cursors.Hand
            };
            this.btnToggleCrosshair.FlatAppearance.BorderSize = 0;
            this.btnToggleCrosshair.Click += (s, e) =>
            {
                // クロスヘアスタイルを切り替え
                this.config.AimviewSettings.CrosshairStyle = (CrosshairStyle)(((int)this.config.AimviewSettings.CrosshairStyle + 1) % 3);
                this.btnToggleCrosshair.ForeColor = this.GetCrosshairButtonColor();
                this.aimViewCanvas.Invalidate();
            };

            // スケルトン切り替えボタンの設定
            this.btnToggleESPstyle = new Button
            {
                Text = GetESPButtonText(),
                Size = new Size(30, 20),
                Location = new Point(90, 5),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = GetESPButtonColor(),
                Cursor = Cursors.Hand
            };
            this.btnToggleESPstyle.FlatAppearance.BorderSize = 0;
            this.btnToggleESPstyle.Click += (s, e) =>
            {
                // ESPスタイルを切り替え
                this.config.AimviewSettings.ESPStyle = (ESPStyle)(((int)this.config.AimviewSettings.ESPStyle + 1) % 3);
                this.btnToggleESPstyle.Text = GetESPButtonText();
                this.btnToggleESPstyle.ForeColor = GetESPButtonColor();
                this.aimViewCanvas.Invalidate();
            };

            // 名前表示切り替えボタン
            this.btnToggleName = new Button
            {
                Text = "Na",
                Size = new Size(30, 20),
                Location = new Point(125, 5),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = this.config.AimviewSettings.ObjectSettings["Player"].Name ? Color.LimeGreen : Color.Red,
                Cursor = Cursors.Hand
            };
            this.btnToggleName.FlatAppearance.BorderSize = 0;
            this.btnToggleName.Click += (s, e) =>
            {
                var playerSettings = this.config.AimviewSettings.ObjectSettings["Player"];
                playerSettings.Name = !playerSettings.Name;
                this.btnToggleName.ForeColor = playerSettings.Name ? Color.LimeGreen : Color.Red;
                this.aimViewCanvas.Invalidate();
            };

            // 武器情報の表示切り替えボタン
            this.btnToggleWeapon = new Button
            {
                Text = "We",
                Size = new Size(30, 20),
                Location = new Point(160, 5),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = this.config.AimviewSettings.showWeaponInfo ? Color.LimeGreen : Color.Red,
                Cursor = Cursors.Hand
            };
            this.btnToggleWeapon.FlatAppearance.BorderSize = 0;
            this.btnToggleWeapon.Click += (s, e) =>
            {
                this.config.AimviewSettings.showWeaponInfo = !this.config.AimviewSettings.showWeaponInfo;
                this.btnToggleWeapon.ForeColor = this.config.AimviewSettings.showWeaponInfo ? Color.LimeGreen : Color.Red;
                this.aimViewCanvas.Invalidate();
            };

            // ヘルス情報の表示切り替えボタン
            this.btnToggleHealth = new Button
            {
                Text = "HP",
                Size = new Size(30, 20),
                Location = new Point(195, 5),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = this.config.AimviewSettings.showHealthInfo ? Color.LimeGreen : Color.Red,
                Cursor = Cursors.Hand
            };
            this.btnToggleHealth.FlatAppearance.BorderSize = 0;
            this.btnToggleHealth.Click += (s, e) =>
            {
                this.config.AimviewSettings.showHealthInfo = !this.config.AimviewSettings.showHealthInfo;
                this.btnToggleHealth.ForeColor = this.config.AimviewSettings.showHealthInfo ? Color.LimeGreen : Color.Red;
                this.aimViewCanvas.Invalidate();
            };

            // 距離表示切り替えボタン
            this.btnToggleDistance = new Button
            {
                Text = "Dis",
                Size = new Size(30, 20),
                Location = new Point(230, 5),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = this.config.AimviewSettings.ObjectSettings["Player"].Distance ? Color.LimeGreen : Color.Red,
                Cursor = Cursors.Hand
            };
            this.btnToggleDistance.FlatAppearance.BorderSize = 0;
            this.btnToggleDistance.Click += (s, e) =>
            {
                var playerSettings = this.config.AimviewSettings.ObjectSettings["Player"];
                playerSettings.Distance = !playerSettings.Distance;
                this.btnToggleDistance.ForeColor = playerSettings.Distance ? Color.LimeGreen : Color.Red;
                this.aimViewCanvas.Invalidate();
            };

            // AimViewキャンバスの設定
            this.aimViewCanvas = new SKGLControl
            {
                Dock = DockStyle.Fill
            };
            
            // コントロールの追加
            this.Controls.Add(this.aimViewCanvas);
            this.Controls.Add(this.btnToggleCrosshair);
            this.Controls.Add(this.btnToggleESPstyle);
            this.Controls.Add(this.btnToggleName);
            this.Controls.Add(this.btnToggleWeapon);
            this.Controls.Add(this.btnToggleHealth);
            this.Controls.Add(this.btnToggleDistance);
            this.Controls.Add(this.btnToggleMaximize);
            this.Controls.Add(this.btnRefresh);
            this.btnToggleMaximize.BringToFront();
            this.btnRefresh.BringToFront();
            this.btnToggleDistance.BringToFront();
            this.btnToggleHealth.BringToFront();
            this.btnToggleWeapon.BringToFront();
            this.btnToggleName.BringToFront();
            this.btnToggleESPstyle.BringToFront();
            this.btnToggleCrosshair.BringToFront();
            
            // イベントハンドラの設定
            this.aimViewCanvas.PaintSurface += this.AimViewCanvas_PaintSurface;
            this.FormClosing += this.AimViewForm_FormClosing;
            
            // 初期状態を設定
            this.TopMost = true;
            this.Visible = config.AimviewSettings.Enabled;
        }

        private void UpdateBackgroundColor()
        {
            if (this.config.AimviewSettings.useTransparentBackground)
            {
                this.BackColor = Color.Black; // 透明化のために黒を使用
                this.TransparencyKey = Color.Black;
            }
            else
            {
                this.BackColor = Color.Black;
                this.TransparencyKey = Color.Empty; // 透明化を無効化
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            
            if (this.Visible)
            {
                // 表示時に設定を反映
                this.Location = new Point(this.config.AimviewSettings.X, this.config.AimviewSettings.Y);
                this.ClientSize = new Size(this.config.AimviewSettings.Width, this.config.AimviewSettings.Height);
            }
        }

        private void AimViewForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            // ユーザーが閉じる操作をした場合でも、フォームを完全に閉じる
            this.config.AimviewSettings.Enabled = false;
        }

        private void AimViewCanvas_PaintSurface(object sender, SKPaintGLSurfaceEventArgs e)
        {
            if (!this.config.AimviewSettings.Enabled)
                return;

            var canvas = e.Surface.Canvas;
            
            // 背景色の設定に応じてキャンバスをクリア
            if (this.config.AimviewSettings.useTransparentBackground)
            {
                canvas.Clear(SKColors.Transparent);
            }
            else
            {
                canvas.Clear(new SKColor(0, 0, 0, 255)); // 完全な黒で不透明に設定
            }

            lock (this.renderLock)
            {
                this.DrawAimview(canvas);
            }
        }

        public void UpdateAndRedraw()
        {
            _currentFrame++;
            if (this.aimViewCanvas != null && !this.aimViewCanvas.IsDisposed)
            {
                this.aimViewCanvas.Invalidate();
            }
        }

        private void DrawAimview(SKCanvas canvas)
        {
            if (!this.config.AimviewSettings.Enabled)
                return;

            // レイド中でない場合やレイド終了時のnullチェック
            if (this.LocalPlayer == null || this.AllPlayers == null || this.CameraManager?.ViewMatrix == null)
            {
                // クロスヘアのみ描画
                this.DrawCrosshair(canvas, this.GetAimviewBounds());
                return;
            }

            // レイド中かどうかの確認
            if (!Memory.Ready || !Memory.InGame)
            {
                // クロスヘアのみ描画
                this.DrawCrosshair(canvas, this.GetAimviewBounds());
                return;
            }

            var aimviewBounds = this.GetAimviewBounds();
            Vector3 myPosition;
            try
            {
                myPosition = this.LocalPlayer.Position;
            }
            catch
            {
                // Position取得に失敗した場合は描画を中止
                this.DrawCrosshair(canvas, aimviewBounds);
                return;
            }

            var aimviewSettings = this.config.AimviewSettings;

            // クロスヘアの描画
            this.DrawCrosshair(canvas, aimviewBounds);

            // RENDER PLAYERS - with skeleton
            var playerSettings = aimviewSettings.ObjectSettings["Player"];
            if (playerSettings != null && playerSettings.Enabled)
            {
                // プレイヤーデータが有効な場合のみ描画を行う
                // LocalPlayerとAllPlayersの存在チェックを優先し、より柔軟な条件にする
                if (this.LocalPlayer != null && this.AllPlayers != null && this.CameraManager?.ViewMatrix != null)
                {
                    var maxDistance = Math.Max(playerSettings.PaintDistance, playerSettings.TextDistance);
                    var activePlayers = this.AllPlayers
                        .Select(x => x.Value)
                        .Where(p => p != null && p != this.LocalPlayer && p.IsActive && p.IsAlive)
                        .Where(p => Vector3.Distance(myPosition, p.Position) <= maxDistance)
                        .ToList();

                    // プレイヤーが見つかった場合のみ描画処理を続行
                    if (activePlayers.Any())
                    {
                        foreach (var player in activePlayers)
                        {
                            var dist = Vector3.Distance(myPosition, player.Position);

                            // プレイヤーのボーン情報を確認（最適化：早期リターン）
                            if (player.Bones == null || player.Bones.Count == 0)
                                continue;

                            // 骨盤の位置を基準点として使用
                            if (!player.Bones.TryGetValue(PlayerBones.HumanPelvis, out var pelvisBone))
                                continue;

                            if (!this.TryGetScreenPosition(pelvisBone.Position, aimviewBounds, out Vector2 screenPos))
                                continue;

                            if (this.IsWithinDrawingBounds(screenPos, aimviewBounds))
                            {
                                this.DrawAimviewPlayer(canvas, player, screenPos, dist, playerSettings);
                            }
                        }
                    }
                }
            }

            // RENDER LOOT（最適化：距離チェックを1回に）
            var looseLootSettings = aimviewSettings.ObjectSettings["LooseLoot"];
            var containerSettings = aimviewSettings.ObjectSettings["Container"];
            var corpseSettings = aimviewSettings.ObjectSettings["Corpse"];
            if ((looseLootSettings?.Enabled ?? false) || 
                (containerSettings?.Enabled ?? false) || 
                (corpseSettings?.Enabled ?? false))
            {
                // レイド中かつLootが有効な場合のみ描画を行う
                if (this.config.ProcessLoot && this.Loot?.Filter != null && this.LocalPlayer != null && this.CameraManager?.ViewMatrix != null)
                {
                    var maxLootDistance = Math.Max(
                        Math.Max(
                            looseLootSettings?.PaintDistance ?? 0,
                            containerSettings?.PaintDistance ?? 0
                        ),
                        corpseSettings?.PaintDistance ?? 0
                    );

                    var nearbyLoot = this.Loot.Filter
                        .Where(i => i != null && Vector3.Distance(myPosition, i.Position) <= maxLootDistance)
                        .ToList();

                    foreach (var item in nearbyLoot)
                    {
                        var dist = Vector3.Distance(myPosition, item.Position);
                        var objectSettings = item switch
                        {
                            LootItem => looseLootSettings,
                            LootContainer => containerSettings,
                            LootCorpse => corpseSettings,
                            _ => null
                        };

                        if (objectSettings == null || (!objectSettings.Enabled || (dist > objectSettings.PaintDistance && dist > objectSettings.TextDistance)))
                            continue;

                        if (!this.TryGetScreenPosition(item.Position, aimviewBounds, out Vector2 screenPos))
                            continue;

                        if (this.IsWithinDrawingBounds(screenPos, aimviewBounds))
                            this.DrawLootableObject(canvas, item, screenPos, dist, objectSettings);
                    }
                }
            }

            // RENDER TRIPWIRES（最適化：距離チェックを1回に）
            var tripwireSettings = aimviewSettings.ObjectSettings["Tripwire"];
            if (tripwireSettings.Enabled && this.Tripwires != null)
            {
                var maxTripwireDistance = Math.Max(tripwireSettings.PaintDistance, tripwireSettings.TextDistance);
                var nearbyTripwires = this.Tripwires
                    .Where(t => Vector3.Distance(myPosition, t.FromPos) <= maxTripwireDistance)
                    .ToList();

                foreach (var tripwire in nearbyTripwires)
                {
                    var dist = Vector3.Distance(myPosition, tripwire.FromPos);
                    var toPos = tripwire.ToPos;

                    if (!this.TryGetScreenPosition(tripwire.FromPos, aimviewBounds, out Vector2 fromScreenPos))
                        continue;
                    if (!this.TryGetScreenPosition(toPos, aimviewBounds, out Vector2 toScreenPos))
                        continue;

                    if (this.IsWithinDrawingBounds(fromScreenPos, aimviewBounds))
                        this.DrawAimviewTripwire(canvas, tripwire, fromScreenPos, toScreenPos, dist, tripwireSettings);
                }
            }

            // RENDER EXFIL（最適化：距離チェックを1回に）
            var exfilSettings = aimviewSettings.ObjectSettings["Exfil"];
            if (exfilSettings.Enabled && this.Exfils != null)
            {
                var nearbyExfils = this.Exfils
                    .Where(e => Vector3.Distance(myPosition, e.Position) <= exfilSettings.TextDistance)
                    .ToList();

                foreach (var exfil in nearbyExfils)
                {
                    if (!this.TryGetScreenPosition(exfil.Position, aimviewBounds, out Vector2 screenPos))
                        continue;

                    if (this.IsWithinDrawingBounds(screenPos, aimviewBounds))
                        this.DrawAimviewExfil(canvas, exfil, screenPos, Vector3.Distance(myPosition, exfil.Position), exfilSettings);
                }
            }

            // RENDER TRANSIT（最適化：距離チェックを1回に）
            var transitSettings = aimviewSettings.ObjectSettings["Transit"];
            if (transitSettings.Enabled && this.Transits != null)
            {
                var nearbyTransits = this.Transits
                    .Where(t => Vector3.Distance(myPosition, t.Position) <= transitSettings.TextDistance)
                    .ToList();

                foreach (var transit in nearbyTransits)
                {
                    if (!this.TryGetScreenPosition(transit.Position, aimviewBounds, out Vector2 screenPos))
                        continue;

                    if (this.IsWithinDrawingBounds(screenPos, aimviewBounds))
                        this.DrawAimviewTransit(canvas, transit, screenPos, Vector3.Distance(myPosition, transit.Position), transitSettings);
                }
            }
        }

        private void DrawLootableObject(SKCanvas canvas, LootableObject lootObject, Vector2 screenPos, float distance, AimviewObjectSettings objectSettings)
        {
            switch (lootObject)
            {
                case LootItem item:
                    this.DrawAimviewLootItem(canvas, item, screenPos, distance, objectSettings);
                    break;
                case LootContainer container:
                    this.DrawAimviewLootContainer(canvas, container, screenPos, distance, objectSettings);
                    break;
                case LootCorpse corpse:
                    this.DrawAimviewLootCorpse(canvas, corpse, screenPos, distance, objectSettings);
                    break;
            }
        }

        private void DrawAimviewLootItem(SKCanvas canvas, LootItem item, Vector2 screenPos, float distance, AimviewObjectSettings objectSettings)
        {
            var currentY = screenPos.Y;

            if (distance < objectSettings.PaintDistance)
            {
                var objectSize = this.CalculateObjectSize(distance);
                var itemPaint = item.GetAimviewPaint();
                canvas.DrawCircle(screenPos.X, currentY, objectSize, itemPaint);
                currentY += (objectSize + AIMVIEW_OBJECT_SPACING) * this.uiScale;
            }

            if (distance < objectSettings.TextDistance)
            {
                var textPaint = item.GetAimviewTextPaint();
                textPaint.TextSize = this.CalculateFontSize(distance, true);
                textPaint.TextAlign = SKTextAlign.Center;

                if (objectSettings.Name)
                {
                    canvas.DrawText(item.Item.shortName, screenPos.X, currentY + textPaint.TextSize, textPaint);
                    currentY += textPaint.TextSize * this.uiScale;
                }

                if (objectSettings.Value)
                {
                    var itemValue = TarkovDevManager.GetItemValue(item.Item);
                    canvas.DrawText($"{itemValue:N0}₽", screenPos.X, currentY + textPaint.TextSize, textPaint);
                    currentY += textPaint.TextSize * this.uiScale;
                }

                if (objectSettings.Distance)
                    canvas.DrawText($"{distance:F0}m", screenPos.X, currentY + textPaint.TextSize, textPaint);
            }
        }

        private void DrawAimviewLootContainer(SKCanvas canvas, LootContainer container, Vector2 screenPos, float distance, AimviewObjectSettings objectSettings)
        {
            var currentY = screenPos.Y;

            if (distance < objectSettings.PaintDistance)
            {
                var objectSize = this.CalculateObjectSize(distance);
                var containerPaint = container.GetAimviewPaint();
                canvas.DrawCircle(screenPos.X, currentY, objectSize, containerPaint);
                currentY += (objectSize + AIMVIEW_OBJECT_SPACING) * this.uiScale;
            }

            if (distance < objectSettings.TextDistance)
            {
                var textPaint = container.GetAimviewTextPaint();
                textPaint.TextSize = this.CalculateFontSize(distance, true);
                textPaint.TextAlign = SKTextAlign.Center;

                if (objectSettings.Name)
                {
                    canvas.DrawText(container.Name, screenPos.X, currentY + textPaint.TextSize, textPaint);
                    currentY += textPaint.TextSize * this.uiScale;
                }

                if (objectSettings.Value)
                {
                    canvas.DrawText($"{container.Value:N0}₽", screenPos.X, currentY + textPaint.TextSize, textPaint);
                    currentY += textPaint.TextSize * this.uiScale;
                }

                if (objectSettings.Distance)
                    canvas.DrawText($"{distance:F0}m", screenPos.X, currentY + textPaint.TextSize, textPaint);
            }
        }

        private void DrawAimviewLootCorpse(SKCanvas canvas, LootCorpse corpse, Vector2 screenPos, float distance, AimviewObjectSettings objectSettings)
        {
            var currentY = screenPos.Y;

            if (distance < objectSettings.PaintDistance)
            {
                var objectSize = this.CalculateObjectSize(distance);
                var corpsePaint = corpse.GetAimviewPaint();
                canvas.DrawCircle(screenPos.X, currentY, objectSize, corpsePaint);
                currentY += (objectSize + AIMVIEW_OBJECT_SPACING) * this.uiScale;
            }

            if (distance < objectSettings.TextDistance)
            {
                var textPaint = corpse.GetAimviewTextPaint();
                textPaint.TextSize = this.CalculateFontSize(distance, true);
                textPaint.TextAlign = SKTextAlign.Center;

                if (objectSettings.Name)
                {
                    canvas.DrawText(corpse.Name, screenPos.X, currentY + textPaint.TextSize, textPaint);
                    currentY += textPaint.TextSize * this.uiScale;
                }

                if (objectSettings.Value)
                {
                    canvas.DrawText($"{corpse.Value:N0}₽", screenPos.X, currentY + textPaint.TextSize, textPaint);
                    currentY += textPaint.TextSize * this.uiScale;
                }

                if (objectSettings.Distance)
                    canvas.DrawText($"{distance:F0}m", screenPos.X, currentY + textPaint.TextSize, textPaint);
            }
        }

        private void DrawAimviewTripwire(SKCanvas canvas, Tripwire tripwire, Vector2 fromScreenPos, Vector2 toScreenPos, float distance, AimviewObjectSettings objectSettings)
        {
            if (distance < objectSettings.PaintDistance)
            {
                var tripwirePaint = tripwire.GetAimviewPaint();
                canvas.DrawLine(fromScreenPos.X, fromScreenPos.Y, toScreenPos.X, toScreenPos.Y, tripwirePaint);
            }

            if (distance < objectSettings.TextDistance)
            {
                var textPaint = tripwire.GetAimviewTextPaint();
                textPaint.TextSize = this.CalculateFontSize(distance, true);
                var currentY = fromScreenPos.Y;

                if (objectSettings.Name)
                {
                    canvas.DrawText("Tripwire", fromScreenPos.X, currentY, textPaint);
                    currentY += textPaint.TextSize * this.uiScale;
                }

                if (objectSettings.Distance)
                    canvas.DrawText($"{distance:F0}m", fromScreenPos.X, currentY, textPaint);
            }
        }

        private void DrawAimviewExfil(SKCanvas canvas, Exfil exfil, Vector2 screenPos, float distance, AimviewObjectSettings objectSettings)
        {
            var objectSize = this.CalculateFontSize(distance);
            var textPaint = exfil.GetAimviewTextPaint();
            textPaint.TextSize = objectSize;

            var currentY = screenPos.Y;

            if (objectSettings.Name)
            {
                canvas.DrawText(exfil.Name, screenPos.X, currentY, textPaint);
                currentY += objectSize * this.uiScale;
            }

            if (objectSettings.Distance)
                canvas.DrawText($"{distance:F0}m", screenPos.X, currentY, textPaint);
        }

        private void DrawAimviewTransit(SKCanvas canvas, Transit transit, Vector2 screenPos, float distance, AimviewObjectSettings objectSettings)
        {
            var objectSize = this.CalculateFontSize(distance);
            var textPaint = transit.GetAimviewTextPaint();
            textPaint.TextSize = objectSize;

            var currentY = screenPos.Y;

            if (objectSettings.Name)
            {
                canvas.DrawText(transit.Name, screenPos.X, currentY, textPaint);
                currentY += objectSize * this.uiScale;
            }

            if (objectSettings.Distance)
                canvas.DrawText($"{distance:F0}m", screenPos.X, currentY, textPaint);
        }

        private bool TryGetScreenPosition(Vector3 worldPosition, SKRect bounds, out Vector2 screenPosition)
        {
            screenPosition = Vector2.Zero;

            try
            {
                // ViewMatrixとCameraManagerの完全性チェック
                if (this.CameraManager == null || this.CameraManager.ViewMatrix == null || !Memory.Ready || !Memory.InGame)
                {
                    return false;
                }

                var isVisible = Extensions.WorldToScreen(worldPosition, (int)bounds.Width, (int)bounds.Height, out screenPosition);

                if (isVisible)
                {
                    screenPosition.X += bounds.Left;
                    screenPosition.Y += bounds.Top;
                }

                return isVisible;
            }
            catch
            {
                return false;
            }
        }

        private SKRect GetAimviewBounds()
        {
            return new SKRect(
                0,
                0,
                this.ClientSize.Width,
                this.ClientSize.Height
            );
        }

        private void DrawCrosshair(SKCanvas canvas, SKRect drawingLocation)
        {
            if (this.config.AimviewSettings.CrosshairStyle == CrosshairStyle.None)
                return;

            float centerX = drawingLocation.Right - (this.config.AimviewSettings.Width / 2);
            float centerY = drawingLocation.Bottom - (this.config.AimviewSettings.Height / 2);

            // 十字線の描画
            if (this.config.AimviewSettings.CrosshairStyle == CrosshairStyle.Cross)
            {
                canvas.DrawLine(
                    drawingLocation.Left,
                    drawingLocation.Bottom - (this.config.AimviewSettings.Height / 2),
                    drawingLocation.Right,
                    drawingLocation.Bottom - (this.config.AimviewSettings.Height / 2),
                    SKPaints.PaintAimviewCrosshair
                );

                canvas.DrawLine(
                    drawingLocation.Right - (this.config.AimviewSettings.Width / 2),
                    drawingLocation.Top,
                    drawingLocation.Right - (this.config.AimviewSettings.Width / 2),
                    drawingLocation.Bottom,
                    SKPaints.PaintAimviewCrosshair
                );
            }
            // サークルの描画
            else if (this.config.AimviewSettings.CrosshairStyle == CrosshairStyle.Circle)
            {
                canvas.DrawCircle(centerX, centerY, this.config.AimviewSettings.CircleRadius * this.uiScale, SKPaints.PaintAimviewCrosshair);
            }
        }

        private float CalculateObjectSize(float distance, bool isText = false)
        {
            var size = isText ? 12f : 3f;
            return Math.Max(size * (1f - distance / 1000f) * this.uiScale, size * 0.3f * this.uiScale);
        }

        private float CalculateFontSize(float distance, bool small = false)
        {
            var baseSize = small ? 12f : 14f;
            return Math.Max(baseSize * (1f - distance / 1000f) * this.uiScale, baseSize * 0.3f * this.uiScale);
        }

        private float CalculateDistanceBasedSize(float distance, float maxSize = 8.0f, float minSize = 1.0f, float decayFactor = 0.02f)
        {
            var scale = (float)Math.Exp(-decayFactor * distance);
            return ((maxSize - minSize) * scale + minSize) * this.uiScale;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            
            // フォームのサイズに合わせてAimViewの設定を更新
            this.config.AimviewSettings.Width = this.ClientSize.Width;
            this.config.AimviewSettings.Height = this.ClientSize.Height;
            
            this.UpdateAndRedraw();
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            
            // フォームの位置に合わせてAimViewの設定を更新
            this.config.AimviewSettings.X = this.Location.X;
            this.config.AimviewSettings.Y = this.Location.Y;
        }

        private bool IsWithinDrawingBounds(Vector2 pos, SKRect bounds)
        {
            return pos.X >= bounds.Left && pos.X <= bounds.Right &&
                   pos.Y >= bounds.Top && pos.Y <= bounds.Bottom;
        }

        private void DrawAimviewPlayer(SKCanvas canvas, Player player, Vector2 screenPos, float distance, AimviewObjectSettings objectSettings)
        {
            if (distance >= Math.Max(objectSettings.PaintDistance, objectSettings.TextDistance))
                return;

            // キャッシュされたペイントを使用
            var paint = GetCachedPaint(player);
            Vector2 textPosition = screenPos;
            bool isWithinPaintDistance = distance < objectSettings.PaintDistance;
            bool isWithinTextDistance = distance < objectSettings.TextDistance;

            // 位置の更新が必要かチェック
            bool shouldUpdate = ShouldUpdatePosition(player.ProfileID, distance);

            if (isWithinPaintDistance)
            {
                switch (this.config.AimviewSettings.ESPStyle)
                {
                    case ESPStyle.Skeleton:
                        DrawAimviewPlayerSkeleton(canvas, player, screenPos, distance, objectSettings, shouldUpdate);
                        break;
                    case ESPStyle.Box:
                        textPosition = DrawAimviewPlayerBox(canvas, player, distance, objectSettings, shouldUpdate);
                        if (textPosition == Vector2.Zero)
                            return;
                        break;
                    case ESPStyle.Dot:
                        DrawAimviewPlayerDot(canvas, player, screenPos, distance, objectSettings, shouldUpdate);
                        break;
                }
            }

            if (isWithinTextDistance && (objectSettings.Distance || objectSettings.Name || 
                this.config.AimviewSettings.showWeaponInfo || 
                this.config.AimviewSettings.showHealthInfo))
            {
                DrawPlayerTextInfo(canvas, player, distance, textPosition, objectSettings);
            }

            // 定期的なキャッシュのクリーンアップ
            CleanupCaches();
        }

        private void DrawPlayerTextInfo(SKCanvas canvas, Player player, float distance, Vector2 screenPos, AimviewObjectSettings objectSettings)
        {
            if (distance >= objectSettings.TextDistance)
                return;

            var textPaint = player.GetAimviewTextPaint();
            textPaint.TextSize = this.CalculateFontSize(distance);
            textPaint.TextAlign = SKTextAlign.Left;
            var textX = screenPos.X + 20;
            var currentY = screenPos.Y + 20;

            // 名前の描画
            if (objectSettings.Name && !string.IsNullOrEmpty(player.Name))
            {
                // プレイヤー名を描画
                canvas.DrawText(player.Name, textX, currentY, textPaint);
                
                // アンダーラインを描画（プレイヤー名の強調）
                float textWidth = textPaint.MeasureText(player.Name);
                using var underlinePaint = new SKPaint
                {
                    Color = textPaint.Color,
                    StrokeWidth = 1,
                    Style = SKPaintStyle.Stroke
                };
                canvas.DrawLine(textX, currentY + 2, textX + textWidth, currentY + 2, underlinePaint);
                
                currentY += textPaint.TextSize * this.uiScale;
            }

            // 武器情報の描画
            if (this.config.AimviewSettings.showWeaponInfo && 
                player.ItemInHands.Item is not null && 
                !string.IsNullOrEmpty(player.ItemInHands.Item.Short))
            {
                var weaponText = player.ItemInHands.Item.Short;
                if (!string.IsNullOrEmpty(player.ItemInHands.Item.GearInfo.AmmoType))
                {
                    var ammoMsg = player.isOfflinePlayer ? $"/{player.ItemInHands.Item.GearInfo.AmmoCount}" : "";
                    weaponText += $" ({player.ItemInHands.Item.GearInfo.AmmoType}{ammoMsg})";
                }
                
                canvas.DrawText(weaponText, textX, currentY, textPaint);
                currentY += textPaint.TextSize * this.uiScale;
            }

            // ヘルス情報の描画
            if (this.config.AimviewSettings.showHealthInfo && !string.IsNullOrEmpty(player.HealthStatus))
            {
                canvas.DrawText(player.HealthStatus, textX, currentY, textPaint);
                currentY += textPaint.TextSize * this.uiScale;
            }

            // 距離情報の描画
            if (objectSettings.Distance)
            {
                canvas.DrawText($"{distance:F0}m", textX, currentY, textPaint);
            }
        }

        private void DrawAimviewPlayerDot(SKCanvas canvas, Player player, Vector2 screenPos, float distance, AimviewObjectSettings objectSettings, bool shouldUpdate)
        {
            if (distance >= objectSettings.PaintDistance)
                return;

            // プレイヤーの位置を骨盤の位置から取得
            if (player?.Bones == null || !player.Bones.TryGetValue(PlayerBones.HumanPelvis, out var pelvisBone))
                return;

            // 骨盤の位置を更新
            pelvisBone.UpdatePosition();
            var bounds = this.GetAimviewBounds();
            
            // 骨盤の位置をスクリーン座標に変換
            if (!this.TryGetScreenPosition(pelvisBone.Position, bounds, out Vector2 pelvisScreenPos))
                return;

            var paint = player.GetAimviewPaint();
            paint.Style = SKPaintStyle.Fill;
            
            var dotSize = CalculateDistanceBasedSize(distance);
            canvas.DrawCircle(pelvisScreenPos.X, pelvisScreenPos.Y, dotSize, paint);
        }
        
        private Vector2 DrawAimviewPlayerBox(SKCanvas canvas, Player player, float distance, AimviewObjectSettings objectSettings, bool shouldUpdate)
        {
            if (player?.Bones == null || distance >= objectSettings.PaintDistance)
                return Vector2.Zero;

            // 頭とベースのボーンの位置を取得
            if (!player.Bones.TryGetValue(PlayerBones.HumanHead, out var headBone) ||
                !player.Bones.TryGetValue(PlayerBones.HumanBase, out var baseBone))
                return Vector2.Zero;

            // ボーンの位置を更新
            headBone.UpdatePosition();
            baseBone.UpdatePosition();

            var bounds = this.GetAimviewBounds();
            if (!TryGetScreenPosition(headBone.Position, bounds, out Vector2 headScreenPos) ||
                !TryGetScreenPosition(baseBone.Position, bounds, out Vector2 baseScreenPos))
                return Vector2.Zero;

            // ボックスのサイズを計算（距離に応じて調整）
            float height = baseScreenPos.Y - headScreenPos.Y;
            float width = height * 0.4f;

            using var paint = player.GetAimviewPaint();
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = Math.Max(1f, 2f * (1f - distance / objectSettings.PaintDistance));

            var boxRect = SKRect.Create(
                headScreenPos.X - width / 2,
                headScreenPos.Y,
                width,
                height
            );

            canvas.DrawRect(boxRect, paint);

            return baseScreenPos;
        }

        private void DrawAimviewPlayerSkeleton(SKCanvas canvas, Player player, Vector2 screenPos, float distance, 
            AimviewObjectSettings objectSettings, bool shouldUpdate)
        {
            if (player?.Bones == null || distance >= objectSettings.PaintDistance)
                return;

            var paint = GetCachedPaint(player);
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = CalculateLineWidth(distance);
            paint.Color = paint.Color.WithAlpha(200);

            var bounds = GetAimviewBounds();
            var bonePositions = new Dictionary<PlayerBones, Vector2>();

            // ボーンの位置を更新（キャッシュを使用）
            if (shouldUpdate)
            {
                foreach (var bone in player.Bones)
                {
                    bone.Value.UpdatePosition();
                    if (TryGetScreenPosition(bone.Value.Position, bounds, out Vector2 pos))
                    {
                        bonePositions[bone.Key] = pos;
                        _lastPositions[$"{player.ProfileID}_{bone.Key}"] = pos;
                    }
                }
            }
            else
            {
                foreach (var bone in player.Bones)
                {
                    if (_lastPositions.TryGetValue($"{player.ProfileID}_{bone.Key}", out Vector2 pos))
                    {
                        bonePositions[bone.Key] = pos;
                    }
                }
            }

            // ボーンの接続を描画
            var boneConnections = new (PlayerBones start, PlayerBones end)[]
            {
                (PlayerBones.HumanPelvis, PlayerBones.HumanSpine3),
                (PlayerBones.HumanSpine3, PlayerBones.HumanHead),
                (PlayerBones.HumanSpine3, PlayerBones.HumanLForearm1),
                (PlayerBones.HumanLForearm1, PlayerBones.HumanLPalm),
                (PlayerBones.HumanSpine3, PlayerBones.HumanRForearm1),
                (PlayerBones.HumanRForearm1, PlayerBones.HumanRPalm),
                (PlayerBones.HumanPelvis, PlayerBones.HumanLCalf),
                (PlayerBones.HumanLCalf, PlayerBones.HumanLFoot),
                (PlayerBones.HumanPelvis, PlayerBones.HumanRCalf),
                (PlayerBones.HumanRCalf, PlayerBones.HumanRFoot)
            };

            foreach (var connection in boneConnections)
            {
                if (bonePositions.TryGetValue(connection.start, out var startPos) &&
                    bonePositions.TryGetValue(connection.end, out var endPos))
                {
                    // 線の太さを距離に応じて調整
                    paint.StrokeWidth = CalculateLineWidth(distance);
                    canvas.DrawLine(startPos.X, startPos.Y, endPos.X, endPos.Y, paint);
                }
            }

            // 頭部を円で描画
            if (bonePositions.TryGetValue(PlayerBones.HumanHead, out var headPos))
            {
                var headSize = CalculateDistanceBasedSize(distance);
                paint.Style = SKPaintStyle.Stroke;
                canvas.DrawCircle(headPos.X, headPos.Y, headSize, paint);
            }
        }

        private float CalculateLineWidth(float distance)
        {
            return Math.Max(2.0f * (1.0f - distance / 1000.0f), 0.5f);
        }

        private void BtnToggleMaximize_Click(object sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Maximized)
            {
                this.WindowState = FormWindowState.Normal;
                ((Button)sender).Text = "□";
            }
            else
            {
                this.WindowState = FormWindowState.Maximized;
                ((Button)sender).Text = "❐";
            }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            
            // 最大化時にタイトルバーを非表示に
            if (this.WindowState == FormWindowState.Maximized && this.FormBorderStyle != FormBorderStyle.None)
            {
                this.previousBorderStyle = this.FormBorderStyle;
                this.wasTopMost = this.TopMost;
                this.FormBorderStyle = FormBorderStyle.None;
                this.TopMost = true;
                
                // ボタンの位置を調整（新しい配置順序）
                this.btnToggleMaximize.Location = new Point(5, 5);
                this.btnRefresh.Location = new Point(30, 5);
                this.btnToggleCrosshair.Location = new Point(55, 5);
                this.btnToggleESPstyle.Location = new Point(90, 5);
                this.btnToggleName.Location = new Point(125, 5);
                this.btnToggleWeapon.Location = new Point(160, 5);
                this.btnToggleHealth.Location = new Point(195, 5);
                this.btnToggleDistance.Location = new Point(230, 5);
            }
            // 最大化解除時に元のスタイルに戻す
            else if (this.WindowState != FormWindowState.Maximized && this.FormBorderStyle == FormBorderStyle.None)
            {
                this.FormBorderStyle = this.previousBorderStyle;
                this.TopMost = this.wasTopMost;
                
                // ボタンの位置を元に戻す（新しい配置順序）
                this.btnToggleMaximize.Location = new Point(5, 5);
                this.btnRefresh.Location = new Point(30, 5);
                this.btnToggleCrosshair.Location = new Point(55, 5);
                this.btnToggleESPstyle.Location = new Point(90, 5);
                this.btnToggleName.Location = new Point(125, 5);
                this.btnToggleWeapon.Location = new Point(160, 5);
                this.btnToggleHealth.Location = new Point(195, 5);
                this.btnToggleDistance.Location = new Point(230, 5);
            }
            
            // 最大化ボタンのテキストを更新
            this.btnToggleMaximize.Text = (this.WindowState == FormWindowState.Maximized) ? "❐" : "□";
        }

        private int GetUpdateFrequency(float distance)
        {
            // 通常の距離ベースの更新頻度のみを保持
            if (distance <= 30f) return 1;
            if (distance <= 50f) return 3;
            if (distance <= 100f) return 7;
            return 11;
        }

        // プレイヤーリストを更新するためのパブリックメソッドを追加
        public void UpdatePlayerList(ReadOnlyDictionary<string, Player> allPlayers, Player localPlayer)
        {
            this.AllPlayers = allPlayers;
            this.LocalPlayer = localPlayer;
        }

        private void BtnRefresh_Click(object sender, EventArgs e)
        {
            OnRestartRadarRequested?.Invoke();
        }

        private Color GetCrosshairButtonColor()
        {
            return this.config.AimviewSettings.CrosshairStyle switch
            {
                CrosshairStyle.None => Color.Red,
                CrosshairStyle.Cross => Color.LimeGreen,
                CrosshairStyle.Circle => Color.Yellow,
                _ => Color.Red
            };
        }

        private Color GetESPButtonColor()
        {
            switch (this.config.AimviewSettings.ESPStyle)
            {
                case ESPStyle.Skeleton:
                    return Color.LimeGreen;
                case ESPStyle.Box:
                    return Color.Yellow;
                case ESPStyle.Dot:
                    return Color.Red;
                default:
                    return Color.Red;
            }
        }

        private string GetESPButtonText()
        {
            return this.config.AimviewSettings.ESPStyle switch
            {
                ESPStyle.Skeleton => "SK",
                ESPStyle.Box => "Box",
                ESPStyle.Dot => "Dot",
                _ => "SK"
            };
        }

        private SKPaint GetCachedPaint(Player player)
        {
            var paint = _paintCache.GetOrAdd(player.ProfileID, _ => 
            {
                var newPaint = GetPaintFromPool("default");
                CopyPaintProperties(player.GetAimviewPaint(), newPaint);
                return newPaint;
            });

            return paint;
        }

        private void CopyPaintProperties(SKPaint source, SKPaint target)
        {
            if (source == null || target == null)
                return;

            target.Color = source.Color;
            target.Style = source.Style;
            target.StrokeWidth = source.StrokeWidth;
            target.StrokeCap = source.StrokeCap;
            target.StrokeJoin = source.StrokeJoin;
            target.TextAlign = source.TextAlign;
            target.TextSize = source.TextSize;
            target.TextEncoding = source.TextEncoding;
            target.FilterQuality = source.FilterQuality;
            target.IsAntialias = source.IsAntialias;
            target.IsDither = source.IsDither;
        }

        private bool ShouldUpdatePosition(string playerId, float distance)
        {
            var now = DateTime.Now;
            if (!_lastUpdateTime.TryGetValue(playerId, out var lastUpdate))
            {
                _lastUpdateTime[playerId] = now;
                return true;
            }

            var updateInterval = distance switch
            {
                <= 50 => TimeSpan.FromMilliseconds(16),  // 60fps
                <= 100 => TimeSpan.FromMilliseconds(33), // 30fps
                <= 200 => TimeSpan.FromMilliseconds(66), // 15fps
                _ => TimeSpan.FromMilliseconds(100)      // 10fps
            };

            if (now - lastUpdate >= updateInterval)
            {
                _lastUpdateTime[playerId] = now;
                return true;
            }

            return false;
        }

        private void CleanupCaches()
        {
            var now = DateTime.Now;
            if (now - _lastCleanupTime < TimeSpan.FromMilliseconds(CLEANUP_INTERVAL_MS))
                return;

            _lastCleanupTime = now;
            var expiredTime = TimeSpan.FromSeconds(5);

            // 期限切れのアイテムを一括で特定
            var expiredItems = _lastUpdateTime
                .Where(x => now - x.Value > expiredTime)
                .Select(x => x.Key)
                .ToList();

            foreach (var key in expiredItems)
            {
                if (_paintCache.TryRemove(key, out var paint))
                {
                    ReturnPaintToPool("default", paint);
                }
                _lastUpdateTime.TryRemove(key, out _);
                _lastPositions.TryRemove(key, out _);
            }

            // メモリ使用量の監視
            MonitorMemoryUsage();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            
            // すべてのキャッシュをクリア
            foreach (var paint in _paintCache.Values)
            {
                paint.Dispose();
            }
            _paintCache.Clear();
            _lastUpdateTime.Clear();
            _lastPositions.Clear();

            // プールされたオブジェクトをクリア
            foreach (var queue in _paintPool.Values)
            {
                while (queue.Count > 0)
                {
                    queue.Dequeue().Dispose();
                }
            }
            foreach (var queue in _pathPool.Values)
            {
                while (queue.Count > 0)
                {
                    queue.Dequeue().Dispose();
                }
            }
            _paintPool.Clear();
            _pathPool.Clear();
        }

        // オブジェクトプーリング用のメソッド
        private SKPaint GetPaintFromPool(string key)
        {
            if (!_paintPool.TryGetValue(key, out var queue))
            {
                queue = new Queue<SKPaint>();
                _paintPool[key] = queue;
            }

            if (queue.Count > 0)
                return queue.Dequeue();

            return new SKPaint();
        }

        private void ReturnPaintToPool(string key, SKPaint paint)
        {
            if (!_paintPool.TryGetValue(key, out var queue))
            {
                queue = new Queue<SKPaint>();
                _paintPool[key] = queue;
            }

            if (queue.Count < POOL_SIZE)
            {
                paint.Reset();
                queue.Enqueue(paint);
            }
            else
            {
                paint.Dispose();
            }
        }

        private SKPath GetPathFromPool(string key)
        {
            if (!_pathPool.TryGetValue(key, out var queue))
            {
                queue = new Queue<SKPath>();
                _pathPool[key] = queue;
            }

            if (queue.Count > 0)
                return queue.Dequeue();

            return new SKPath();
        }

        private void ReturnPathToPool(string key, SKPath path)
        {
            if (!_pathPool.TryGetValue(key, out var queue))
            {
                queue = new Queue<SKPath>();
                _pathPool[key] = queue;
            }

            if (queue.Count < POOL_SIZE)
            {
                path.Reset();
                queue.Enqueue(path);
            }
            else
            {
                path.Dispose();
            }
        }

        // メモリ使用量の監視と制御
        private void MonitorMemoryUsage()
        {
            if (_paintCache.Count > MAX_CACHED_OBJECTS)
            {
                var itemsToRemove = _paintCache.Count - MAX_CACHED_OBJECTS;
                var oldestItems = _lastUpdateTime.OrderBy(x => x.Value).Take(itemsToRemove);
                
                foreach (var item in oldestItems)
                {
                    _paintCache.TryRemove(item.Key, out var paint);
                    _lastUpdateTime.TryRemove(item.Key, out _);
                    _lastPositions.TryRemove(item.Key, out _);
                }
            }
        }
    }
} 