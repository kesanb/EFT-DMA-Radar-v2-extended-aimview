using System.Numerics;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Linq;

namespace eft_dma_radar
{
    // プレイヤータイプごとの設定クラス
    public class PlayerTypeSettings
    {
        public bool ShowName { get; set; } = true;
        public bool ShowDistance { get; set; } = true;
        public bool ShowWeapon { get; set; } = true;
        public bool ShowHealth { get; set; } = true;
        public ESPStyle ESPStyle { get; set; } = ESPStyle.Skeleton;
    }

    // プレイヤータイプごとのUIコントロール
    public class PlayerTypeControls
    {
        public Button ESPStyleButton { get; set; }
        public Button NameButton { get; set; }
        public Button WeaponButton { get; set; }
        public Button HealthButton { get; set; }
        public Button DistanceButton { get; set; }
    }

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

        // プレイヤータイプごとのUIコントロールを管理
        private readonly Dictionary<PlayerType, PlayerTypeControls> _playerTypeControls = new();
        private ComboBox _playerTypeSelector;
        private PlayerType _currentPlayerType = PlayerType.PMC;

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

            // クロスヘア切り替えボタンの設定
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
                this.config.AimviewSettings.CrosshairStyle = (CrosshairStyle)(((int)this.config.AimviewSettings.CrosshairStyle + 1) % 3);
                this.btnToggleCrosshair.ForeColor = this.GetCrosshairButtonColor();
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
            this.Controls.Add(this.btnToggleMaximize);
            this.Controls.Add(this.btnRefresh);

            // プレイヤータイプごとのコントロールを初期化
            InitializePlayerTypeControls();

            // コントロールの表示順序を調整
            this.aimViewCanvas.SendToBack();
            this.btnToggleMaximize.BringToFront();
            this.btnRefresh.BringToFront();
            this.btnToggleCrosshair.BringToFront();
            this._playerTypeSelector.BringToFront();
            
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

            // プレイヤータイプごとの設定を取得
            var typeSettings = GetPlayerTypeSettings(player.Type);

            // キャッシュされたペイントを使用
            var paint = GetCachedPaint(player);
            Vector2 textPosition = screenPos;
            bool isWithinPaintDistance = distance < objectSettings.PaintDistance;
            bool isWithinTextDistance = distance < objectSettings.TextDistance;

            // 位置の更新が必要かチェック
            bool shouldUpdate = ShouldUpdatePosition(player.ProfileID, distance);

            if (isWithinPaintDistance)
            {
                switch (typeSettings.ESPStyle)
                {
                    case ESPStyle.Skeleton:
                        DrawPlayerSkeleton(canvas, player, this.GetAimviewBounds(), distance);
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

            if (isWithinTextDistance)
            {
                DrawPlayerTextInfo(canvas, player, distance, textPosition, objectSettings, typeSettings);
            }

            // 定期的なキャッシュのクリーンアップ
            CleanupCaches();
        }

        private void DrawPlayerTextInfo(SKCanvas canvas, Player player, float distance, Vector2 screenPos, AimviewObjectSettings objectSettings, PlayerTypeSettings typeSettings)
        {
            if (distance >= objectSettings.TextDistance)
                return;

            // プレイヤータイプの設定を取得（BEARとUSECの場合はPMCの設定を使用）
            if (player.Type == PlayerType.BEAR || player.Type == PlayerType.USEC)
            {
                typeSettings = GetPlayerTypeSettings(PlayerType.PMC);
            }

            var textPaint = player.GetAimviewTextPaint();
            textPaint.TextSize = this.CalculateFontSize(distance);
            textPaint.TextAlign = SKTextAlign.Left;
            var textX = screenPos.X + 20;
            var currentY = screenPos.Y + 20;

            // 名前の描画
            if (typeSettings.ShowName && objectSettings.Name && !string.IsNullOrEmpty(player.Name))
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
            if (typeSettings.ShowWeapon && 
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
            if (typeSettings.ShowHealth && !string.IsNullOrEmpty(player.HealthStatus))
            {
                canvas.DrawText(player.HealthStatus, textX, currentY, textPaint);
                currentY += textPaint.TextSize * this.uiScale;
            }

            // 距離情報の描画
            if (typeSettings.ShowDistance && objectSettings.Distance)
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

        private void DrawPlayerSkeleton(SKCanvas canvas, Player player, SKRect bounds, float distance)
        {
            if (player?.Bones == null || player.Bones.Count == 0)
                return;

            var bonePositions = new List<Vector3>();
            var screenPositions = new List<Vector2>();
            var boneMapping = new Dictionary<int, PlayerBones>();
            int index = 0;

            // 距離に応じて処理するボーンを選択（パフォーマンスモードが有効な場合）
            var bonesToProcess = this.config.AimviewSettings.usePerformanceSkeleton ? 
                GetDistanceBasedBones(distance) : 
                player.Bones.Keys.ToArray();

            // 選択されたボーンの位置を収集
            foreach (var boneType in bonesToProcess)
            {
                if (player.Bones.TryGetValue(boneType, out var bone))
                {
                    bone.UpdatePosition();
                    bonePositions.Add(bone.Position);
                    boneMapping[index] = boneType;
                    index++;
                }
            }

            // 一括でスクリーン座標に変換
            var visibilityResults = Extensions.WorldToScreenCombined(bonePositions, bounds.Width, bounds.Height, screenPositions);

            // 可視ボーンのマッピングを作成
            var visibleBones = new Dictionary<PlayerBones, Vector2>();
            for (int i = 0; i < bonePositions.Count; i++)
            {
                if (visibilityResults[i])
                {
                    var screenPos = screenPositions[i];
                    screenPos.X += bounds.Left;
                    screenPos.Y += bounds.Top;
                    
                    if (IsWithinDrawingBounds(screenPos, bounds))
                    {
                        visibleBones[boneMapping[i]] = screenPos;
                    }
                }
            }

            // ボーンの接続を描画
            var paint = player.GetAimviewPaint();
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 2.0f;

            if (visibleBones.Count >= 2)
            {
                if (!this.config.AimviewSettings.usePerformanceSkeleton || distance <= this.config.AimviewSettings.performanceSkeletonDistance)
                {
                    // パフォーマンスモード無効時または閾値以下：フルスケルトン
                    DrawFullSkeleton(canvas, visibleBones, paint);
                    
                    // 頭部インジケーターを表示
                    if (visibleBones.TryGetValue(PlayerBones.HumanHead, out Vector2 headPos))
                    {
                        paint.Style = SKPaintStyle.Stroke;
                        float headSize = CalculateDistanceBasedSize(distance, 8.0f, 2.0f, 0.02f);
                        canvas.DrawCircle(headPos.X, headPos.Y, headSize, paint);
                    }
                }
            }

            // パフォーマンスモード有効時の遠距離：骨盤位置にドットを表示
            if (this.config.AimviewSettings.usePerformanceSkeleton && distance > this.config.AimviewSettings.performanceSkeletonDistance && 
                visibleBones.TryGetValue(PlayerBones.HumanPelvis, out Vector2 pelvisPos))
            {
                paint.Style = SKPaintStyle.Fill;
                float dotSize = CalculateDistanceBasedSize(distance, 6.0f, 2.0f, 0.02f);
                canvas.DrawCircle(pelvisPos.X, pelvisPos.Y, dotSize, paint);
            }
        }

        private PlayerBones[] GetDistanceBasedBones(float distance)
        {
            if (!this.config.AimviewSettings.usePerformanceSkeleton || distance <= this.config.AimviewSettings.performanceSkeletonDistance)
            {
                // パフォーマンスモード無効時または閾値以下：フルスケルトン
                return new[]
                {
                    PlayerBones.HumanHead,
                    PlayerBones.HumanSpine3,
                    PlayerBones.HumanPelvis,
                    PlayerBones.HumanLForearm1,
                    PlayerBones.HumanLPalm,
                    PlayerBones.HumanRForearm1,
                    PlayerBones.HumanRPalm,
                    PlayerBones.HumanLCalf,
                    PlayerBones.HumanLFoot,
                    PlayerBones.HumanRCalf,
                    PlayerBones.HumanRFoot
                };
            }
            else
            {
                // 遠距離：骨盤位置のみ
                return new[]
                {
                    PlayerBones.HumanPelvis
                };
            }
        }

        private void DrawFullSkeleton(SKCanvas canvas, Dictionary<PlayerBones, Vector2> visibleBones, SKPaint paint)
        {
            // メインボディ
            DrawBoneConnectionIfVisible(canvas, visibleBones, PlayerBones.HumanHead, PlayerBones.HumanSpine3, paint);
            DrawBoneConnectionIfVisible(canvas, visibleBones, PlayerBones.HumanSpine3, PlayerBones.HumanPelvis, paint);

            // 腕
            DrawBoneConnectionIfVisible(canvas, visibleBones, PlayerBones.HumanSpine3, PlayerBones.HumanLForearm1, paint);
            DrawBoneConnectionIfVisible(canvas, visibleBones, PlayerBones.HumanLForearm1, PlayerBones.HumanLPalm, paint);
            DrawBoneConnectionIfVisible(canvas, visibleBones, PlayerBones.HumanSpine3, PlayerBones.HumanRForearm1, paint);
            DrawBoneConnectionIfVisible(canvas, visibleBones, PlayerBones.HumanRForearm1, PlayerBones.HumanRPalm, paint);

            // 脚
            DrawBoneConnectionIfVisible(canvas, visibleBones, PlayerBones.HumanPelvis, PlayerBones.HumanLCalf, paint);
            DrawBoneConnectionIfVisible(canvas, visibleBones, PlayerBones.HumanLCalf, PlayerBones.HumanLFoot, paint);
            DrawBoneConnectionIfVisible(canvas, visibleBones, PlayerBones.HumanPelvis, PlayerBones.HumanRCalf, paint);
            DrawBoneConnectionIfVisible(canvas, visibleBones, PlayerBones.HumanRCalf, PlayerBones.HumanRFoot, paint);
        }

        private void DrawBoneConnectionIfVisible(SKCanvas canvas, Dictionary<PlayerBones, Vector2> visibleBones, 
            PlayerBones bone1, PlayerBones bone2, SKPaint paint)
        {
            if (visibleBones.TryGetValue(bone1, out Vector2 start) && 
                visibleBones.TryGetValue(bone2, out Vector2 end))
            {
                canvas.DrawLine(start.X, start.Y, end.X, end.Y, paint);
            }
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
                
                // ボタンの位置を調整
                this.btnToggleMaximize.Location = new Point(5, 5);
                this.btnRefresh.Location = new Point(30, 5);
                this.btnToggleCrosshair.Location = new Point(55, 5);
            }
            // 最大化解除時に元のスタイルに戻す
            else if (this.WindowState != FormWindowState.Maximized && this.FormBorderStyle == FormBorderStyle.None)
            {
                this.FormBorderStyle = this.previousBorderStyle;
                this.TopMost = this.wasTopMost;
                
                // ボタンの位置を元に戻す
                this.btnToggleMaximize.Location = new Point(5, 5);
                this.btnRefresh.Location = new Point(30, 5);
                this.btnToggleCrosshair.Location = new Point(55, 5);
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

            // 距離に関係なく33ms（約30fps）に固定
            var updateInterval = TimeSpan.FromMilliseconds(33);

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

        private void InitializePlayerTypeControls()
        {
            // プレイヤータイプセレクターの初期化
            _playerTypeSelector = new ComboBox
            {
                Location = new Point(90, 5),
                Size = new Size(120, 20),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f)
            };

            // プレイヤータイプの追加
            _playerTypeSelector.Items.AddRange(new object[]
            {
                PlayerType.PMC,
                PlayerType.Scav,
                PlayerType.PlayerScav,
                PlayerType.Boss,
                PlayerType.BossFollower,
                PlayerType.Raider,
                PlayerType.Rogue,
                PlayerType.Cultist
            });

            this.Controls.Add(_playerTypeSelector);
            _playerTypeSelector.SelectedIndexChanged += PlayerTypeSelector_SelectedIndexChanged;

            // 最初のプレイヤータイプのコントロールを作成
            _currentPlayerType = PlayerType.PMC;
            InitializeControlsForPlayerType(_currentPlayerType);
            _playerTypeSelector.SelectedItem = _currentPlayerType;

            // 初期プレイヤータイプのコントロールを表示
            UpdateControlsVisibility(_currentPlayerType);
        }

        private void InitializeControlsForPlayerType(PlayerType type)
        {
            var settings = GetPlayerTypeSettings(type);
            var controls = new PlayerTypeControls
            {
                ESPStyleButton = CreateButton("SK", new Point(215, 5), GetESPButtonColor(settings.ESPStyle), (s, e) => ToggleESPStyle(type)),
                NameButton = CreateButton("Na", new Point(250, 5), settings.ShowName ? Color.LimeGreen : Color.Red, (s, e) => ToggleName(type)),
                WeaponButton = CreateButton("We", new Point(285, 5), settings.ShowWeapon ? Color.LimeGreen : Color.Red, (s, e) => ToggleWeapon(type)),
                HealthButton = CreateButton("HP", new Point(320, 5), settings.ShowHealth ? Color.LimeGreen : Color.Red, (s, e) => ToggleHealth(type)),
                DistanceButton = CreateButton("Dis", new Point(355, 5), settings.ShowDistance ? Color.LimeGreen : Color.Red, (s, e) => ToggleDistance(type))
            };

            // ESPスタイルのテキストを設定
            controls.ESPStyleButton.Text = GetESPButtonText(settings.ESPStyle);

            _playerTypeControls[type] = controls;

            // コントロールをフォームに追加
            this.Controls.Add(controls.ESPStyleButton);
            this.Controls.Add(controls.NameButton);
            this.Controls.Add(controls.WeaponButton);
            this.Controls.Add(controls.HealthButton);
            this.Controls.Add(controls.DistanceButton);

            // 初期状態では非表示
            controls.ESPStyleButton.Visible = false;
            controls.NameButton.Visible = false;
            controls.WeaponButton.Visible = false;
            controls.HealthButton.Visible = false;
            controls.DistanceButton.Visible = false;
        }

        private Button CreateButton(string text, Point location, Color foreColor, EventHandler clickHandler)
        {
            var button = new Button
            {
                Text = text,
                Size = new Size(30, 20),
                Location = location,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = foreColor,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += clickHandler;
            return button;
        }

        private void PlayerTypeSelector_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_playerTypeSelector.SelectedItem == null)
                return;

            var newType = (PlayerType)_playerTypeSelector.SelectedItem;
            _currentPlayerType = newType;

            // 選択されたタイプのコントロールが存在しない場合は作成
            if (!_playerTypeControls.ContainsKey(newType))
            {
                InitializeControlsForPlayerType(newType);
            }

            // 選択されたタイプのコントロールを表示
            UpdateControlsVisibility(newType);

            // 選択されたプレイヤータイプの設定を反映
            var controls = _playerTypeControls[newType];
            var settings = GetPlayerTypeSettings(newType);
            
            // ボタンの状態を更新
            controls.ESPStyleButton.Text = GetESPButtonText(settings.ESPStyle);
            controls.ESPStyleButton.ForeColor = GetESPButtonColor(settings.ESPStyle);
            controls.NameButton.ForeColor = settings.ShowName ? Color.LimeGreen : Color.Red;
            controls.WeaponButton.ForeColor = settings.ShowWeapon ? Color.LimeGreen : Color.Red;
            controls.HealthButton.ForeColor = settings.ShowHealth ? Color.LimeGreen : Color.Red;
            controls.DistanceButton.ForeColor = settings.ShowDistance ? Color.LimeGreen : Color.Red;

            // すべてのボタンを前面に表示
            controls.ESPStyleButton.BringToFront();
            controls.NameButton.BringToFront();
            controls.WeaponButton.BringToFront();
            controls.HealthButton.BringToFront();
            controls.DistanceButton.BringToFront();
            _playerTypeSelector.BringToFront();
        }

        private void UpdateControlsVisibility(PlayerType type)
        {
            // すべてのコントロールを非表示
            foreach (var controls in _playerTypeControls.Values)
            {
                controls.ESPStyleButton.Visible = false;
                controls.NameButton.Visible = false;
                controls.WeaponButton.Visible = false;
                controls.HealthButton.Visible = false;
                controls.DistanceButton.Visible = false;
            }

            // 選択されたタイプのコントロールを表示
            if (_playerTypeControls.TryGetValue(type, out var selectedControls))
            {
                selectedControls.ESPStyleButton.Visible = true;
                selectedControls.NameButton.Visible = true;
                selectedControls.WeaponButton.Visible = true;
                selectedControls.HealthButton.Visible = true;
                selectedControls.DistanceButton.Visible = true;
            }
        }

        private void ToggleESPStyle(PlayerType type)
        {
            if (!_playerTypeControls.TryGetValue(type, out var controls))
                return;

            var settings = GetPlayerTypeSettings(type);
            settings.ESPStyle = (ESPStyle)(((int)settings.ESPStyle + 1) % 3);
            controls.ESPStyleButton.Text = GetESPButtonText(settings.ESPStyle);
            controls.ESPStyleButton.ForeColor = GetESPButtonColor(settings.ESPStyle);
            this.aimViewCanvas.Invalidate();
        }

        private void ToggleName(PlayerType type)
        {
            if (!_playerTypeControls.TryGetValue(type, out var controls))
                return;

            var settings = GetPlayerTypeSettings(type);
            settings.ShowName = !settings.ShowName;
            controls.NameButton.ForeColor = settings.ShowName ? Color.LimeGreen : Color.Red;
            this.aimViewCanvas.Invalidate();
        }

        private void ToggleWeapon(PlayerType type)
        {
            if (!_playerTypeControls.TryGetValue(type, out var controls))
                return;

            var settings = GetPlayerTypeSettings(type);
            settings.ShowWeapon = !settings.ShowWeapon;
            controls.WeaponButton.ForeColor = settings.ShowWeapon ? Color.LimeGreen : Color.Red;
            this.aimViewCanvas.Invalidate();
        }

        private void ToggleHealth(PlayerType type)
        {
            if (!_playerTypeControls.TryGetValue(type, out var controls))
                return;

            var settings = GetPlayerTypeSettings(type);
            settings.ShowHealth = !settings.ShowHealth;
            controls.HealthButton.ForeColor = settings.ShowHealth ? Color.LimeGreen : Color.Red;
            this.aimViewCanvas.Invalidate();
        }

        private void ToggleDistance(PlayerType type)
        {
            if (!_playerTypeControls.TryGetValue(type, out var controls))
                return;

            var settings = GetPlayerTypeSettings(type);
            settings.ShowDistance = !settings.ShowDistance;
            controls.DistanceButton.ForeColor = settings.ShowDistance ? Color.LimeGreen : Color.Red;
            this.aimViewCanvas.Invalidate();
        }

        private PlayerTypeSettings GetPlayerTypeSettings(PlayerType type)
        {
            // BEARとUSECの場合はPMCの設定を使用
            if (type == PlayerType.BEAR || type == PlayerType.USEC)
            {
                type = PlayerType.PMC;
            }

            if (!this.config.AimviewSettings.PlayerTypeSettings.TryGetValue(type, out var settings))
            {
                settings = new PlayerTypeSettings();
                this.config.AimviewSettings.PlayerTypeSettings[type] = settings;
            }
            return settings;
        }

        private string GetESPButtonText(ESPStyle style)
        {
            return style switch
            {
                ESPStyle.Skeleton => "SK",
                ESPStyle.Box => "Box",
                ESPStyle.Dot => "Dot",
                _ => "SK"
            };
        }

        private Color GetESPButtonColor(ESPStyle style)
        {
            return style switch
            {
                ESPStyle.Skeleton => Color.LimeGreen,
                ESPStyle.Box => Color.Yellow,
                ESPStyle.Dot => Color.Red,
                _ => Color.Red
            };
        }

        private Color GetESPButtonColor()
        {
            var settings = GetPlayerTypeSettings(_currentPlayerType);
            return GetESPButtonColor(settings.ESPStyle);
        }

        private string GetESPButtonText()
        {
            var settings = GetPlayerTypeSettings(_currentPlayerType);
            return GetESPButtonText(settings.ESPStyle);
        }
    }
} 