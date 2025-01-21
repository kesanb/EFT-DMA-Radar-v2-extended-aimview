using System.Numerics;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using System.Collections.ObjectModel;

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
        private Button btnToggleSkeleton;
        private Button btnToggleName;
        private Button btnToggleDistance;
        private Button btnToggleWeapon;
        private Button btnToggleHealth;
        private Button btnToggleMaximize;
        private Button btnRefresh;
        private bool showCrosshair = true;
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

        public AimViewForm(Config config)
        {
            this.config = config;
            
            // フォームの基本設定
            this.Text = "AimView";
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.TopMost = true;
            
            // 背景色の設定
            UpdateBackgroundColor();

            // クロスヘアの状態を設定ファイルから読み込む
            this.showCrosshair = this.config.AimviewSettings.ShowCrosshair;

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
                ForeColor = this.showCrosshair ? Color.LimeGreen : Color.Red,
                Cursor = Cursors.Hand
            };
            this.btnToggleCrosshair.FlatAppearance.BorderSize = 0;
            this.btnToggleCrosshair.Click += (s, e) =>
            {
                this.showCrosshair = !this.showCrosshair;
                this.btnToggleCrosshair.ForeColor = this.showCrosshair ? Color.LimeGreen : Color.Red;
                // 設定ファイルに保存
                this.config.AimviewSettings.ShowCrosshair = this.showCrosshair;
                this.aimViewCanvas.Invalidate();
            };

            // スケルトン切り替えボタンの設定
            this.btnToggleSkeleton = new Button
            {
                Text = "SK",
                Size = new Size(30, 20),
                Location = new Point(90, 5),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = this.config.AimviewSettings.useSkeleton ? Color.LimeGreen : Color.Red,
                Cursor = Cursors.Hand
            };
            this.btnToggleSkeleton.FlatAppearance.BorderSize = 0;
            this.btnToggleSkeleton.Click += (s, e) =>
            {
                this.config.AimviewSettings.useSkeleton = !this.config.AimviewSettings.useSkeleton;
                this.btnToggleSkeleton.ForeColor = this.config.AimviewSettings.useSkeleton ? Color.LimeGreen : Color.Red;
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
            this.Controls.Add(this.btnToggleSkeleton);
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
            this.btnToggleSkeleton.BringToFront();
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
                if (this.showCrosshair)
                {
                    var bounds = this.GetAimviewBounds();
                    this.DrawCrosshair(canvas, bounds);
                }
                return;
            }

            var aimviewBounds = this.GetAimviewBounds();
            var myPosition = this.LocalPlayer.Position;
            var aimviewSettings = this.config.AimviewSettings;

            // クロスヘアの描画（最適化：条件チェックを1回に）
            if (this.showCrosshair)
            {
                this.DrawCrosshair(canvas, aimviewBounds);
            }

            // RENDER PLAYERS - with skeleton
            var playerSettings = aimviewSettings.ObjectSettings["Player"];
            if (playerSettings != null && playerSettings.Enabled)
            {
                var maxDistance = Math.Max(playerSettings.PaintDistance, playerSettings.TextDistance);
                var activePlayers = this.AllPlayers
                    .Select(x => x.Value)
                    .Where(p => p != null && p != this.LocalPlayer && p.IsActive && p.IsAlive)
                    .Where(p => Vector3.Distance(myPosition, p.Position) <= maxDistance)
                    .ToList();

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

            // RENDER LOOT（最適化：距離チェックを1回に）
            var looseLootSettings = aimviewSettings.ObjectSettings["LooseLoot"];
            var containerSettings = aimviewSettings.ObjectSettings["Container"];
            var corpseSettings = aimviewSettings.ObjectSettings["Corpse"];
            if ((looseLootSettings?.Enabled ?? false) || 
                (containerSettings?.Enabled ?? false) || 
                (corpseSettings?.Enabled ?? false))
            {
                if (this.config.ProcessLoot && this.Loot?.Filter != null)
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
                    canvas.DrawText($"{item.Item.basePrice:N0}₽", screenPos.X, currentY + textPaint.TextSize, textPaint);
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

            // ViewMatrixのnullチェック
            if (this.CameraManager == null || this.CameraManager.ViewMatrix == null)
                return false;

            var isVisible = Extensions.WorldToScreen(worldPosition, (int)bounds.Width, (int)bounds.Height, out screenPosition);

            if (isVisible)
            {
                screenPosition.X += bounds.Left;
                screenPosition.Y += bounds.Top;
            }

            return isVisible;
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
            // スケルトン表示の使用有無に応じて適切なメソッドを呼び出す
            if (this.config.AimviewSettings.useSkeleton)
            {
                DrawAimviewPlayerSkeleton(canvas, player, screenPos, distance, objectSettings);
            }
            else
            {
                DrawAimviewPlayerDot(canvas, player, screenPos, distance, objectSettings);
            }
        }

        private void DrawPlayerTextInfo(SKCanvas canvas, Player player, float distance, Vector2 screenPos, AimviewObjectSettings objectSettings)
        {
            if (distance >= objectSettings.TextDistance)
                return;

            var textPaint = player.GetAimviewTextPaint();
            textPaint.TextSize = this.CalculateFontSize(distance);
            textPaint.TextAlign = SKTextAlign.Left;
            var textX = screenPos.X + 20;
            var currentY = screenPos.Y + 20;  // 基準点から下に20ピクセル移動

            // 名前を表示（Settings.jsonのName設定を参照）
            if (objectSettings.Name && !string.IsNullOrEmpty(player.Name))
            {
                // プレイヤー名を描画
                canvas.DrawText(player.Name, textX, currentY, textPaint);
                
                // アンダーラインを描画
                float textWidth = textPaint.MeasureText(player.Name);
                var underlinePaint = new SKPaint
                {
                    Color = textPaint.Color,
                    StrokeWidth = 1,
                    Style = SKPaintStyle.Stroke
                };
                canvas.DrawLine(textX, currentY + 2, textX + textWidth, currentY + 2, underlinePaint);
                
                currentY += textPaint.TextSize * this.uiScale;
            }

            // 武器情報を表示
            if (this.config.AimviewSettings.showWeaponInfo && player.ItemInHands.Item is not null && !string.IsNullOrEmpty(player.ItemInHands.Item.Short))
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

            // ヘルス情報を表示
            if (this.config.AimviewSettings.showHealthInfo && !string.IsNullOrEmpty(player.HealthStatus))
            {
                canvas.DrawText(player.HealthStatus, textX, currentY, textPaint);
                currentY += textPaint.TextSize * this.uiScale;
            }

            // 距離を表示（Settings.jsonのDistance設定を参照）
            if (objectSettings.Distance)
            {
                canvas.DrawText($"{distance:F0}m", textX, currentY, textPaint);
            }
        }

        private void DrawAimviewPlayerDot(SKCanvas canvas, Player player, Vector2 screenPos, float distance, AimviewObjectSettings objectSettings)
        {
            // プレイヤーの位置を骨盤の位置から取得
            if (player?.Bones == null || !player.Bones.TryGetValue(PlayerBones.HumanPelvis, out var pelvisBone))
                return;

            // 骨盤の位置を更新
            pelvisBone.UpdatePosition();
            var bounds = this.GetAimviewBounds();
            
            // 骨盤の位置をスクリーン座標に変換
            if (!this.TryGetScreenPosition(pelvisBone.Position, bounds, out Vector2 pelvisScreenPos))
                return;

            if (distance < objectSettings.PaintDistance)
            {
                var paint = player.GetAimviewPaint();
                paint.Style = SKPaintStyle.Fill;
                
                var dotSize = CalculateDistanceBasedSize(distance);
                canvas.DrawCircle(pelvisScreenPos.X, pelvisScreenPos.Y, dotSize, paint);
            }

            // テキスト情報の表示
            DrawPlayerTextInfo(canvas, player, distance, pelvisScreenPos, objectSettings);
        }

        private void DrawAimviewPlayerSkeleton(SKCanvas canvas, Player player, Vector2 screenPos, float distance, AimviewObjectSettings objectSettings)
        {
            // 既存のスケルトン描画コードをここに移動
            if (player?.Bones == null || objectSettings == null)
                return;

            if (distance < objectSettings.PaintDistance)
            {
                var paint = player.GetAimviewPaint();
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 2.0f;
                paint.Color = paint.Color.WithAlpha(200);

                // 距離に応じて更新頻度を決定
                var updateFrequency = GetUpdateFrequency(distance);
                var playerId = player.ProfileID;

                if (string.IsNullOrEmpty(playerId))
                    return;

                if (!_playerUpdateFrames.ContainsKey(playerId))
                {
                    _playerUpdateFrames[playerId] = 0;
                }

                var shouldUpdate = _playerUpdateFrames[playerId] <= 0;
                if (shouldUpdate)
                {
                    _playerUpdateFrames[playerId] = updateFrequency;
                }
                else
                {
                    _playerUpdateFrames[playerId]--;
                }

                // 必要な主要ボーンのみを定義
                var requiredBones = new[]
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

                // ボーンの位置を画面座標に変換（最適化）
                var bounds = this.GetAimviewBounds();
                var bones = new Dictionary<PlayerBones, Vector2>(11);

                // ボーンの位置を更新（距離に応じた頻度で）
                if (shouldUpdate)
                {
                    foreach (var bone in requiredBones)
                    {
                        if (player.Bones.TryGetValue(bone, out var boneTransform))
                        {
                            boneTransform.UpdatePosition();
                        }
                    }
                }

                // スクリーン座標に変換
                foreach (var bone in requiredBones)
                {
                    if (player.Bones.TryGetValue(bone, out var boneTransform))
                    {
                        var worldPos = boneTransform.Position;
                        if (this.TryGetScreenPosition(worldPos, bounds, out Vector2 boneScreenPos))
                        {
                            bones[bone] = boneScreenPos;
                        }
                    }
                }

                // シンプルなスケルトンの描画
                if (bones.Count > 0)
                {
                    // 脊椎の描画
                    if (bones.ContainsKey(PlayerBones.HumanPelvis) && bones.ContainsKey(PlayerBones.HumanSpine3))
                    {
                        this.DrawBoneConnection(canvas, bones, PlayerBones.HumanPelvis, PlayerBones.HumanSpine3, paint);
                    }

                    // 頭部の描画（円で表示）
                    if (bones.ContainsKey(PlayerBones.HumanHead))
                    {
                        var headPos = bones[PlayerBones.HumanHead];
                        paint.Style = SKPaintStyle.Stroke;
                        
                        var headSize = CalculateDistanceBasedSize(distance);
                        canvas.DrawCircle(headPos.X, headPos.Y, headSize, paint);
                    }

                    // 左腕の描画
                    if (bones.ContainsKey(PlayerBones.HumanSpine3))
                    {
                        if (bones.ContainsKey(PlayerBones.HumanLForearm1))
                        {
                            paint.Color = paint.Color.WithAlpha(255);
                            this.DrawBoneConnection(canvas, bones, PlayerBones.HumanSpine3, PlayerBones.HumanLForearm1, paint);
                            paint.Color = paint.Color.WithAlpha(200);
                            
                            if (bones.ContainsKey(PlayerBones.HumanLPalm))
                            {
                                paint.Color = paint.Color.WithAlpha(255);
                                this.DrawBoneConnection(canvas, bones, PlayerBones.HumanLForearm1, PlayerBones.HumanLPalm, paint);
                                paint.Color = paint.Color.WithAlpha(200);
                            }
                        }
                    }

                    // 右腕の描画
                    if (bones.ContainsKey(PlayerBones.HumanSpine3))
                    {
                        if (bones.ContainsKey(PlayerBones.HumanRForearm1))
                        {
                            paint.Color = paint.Color.WithAlpha(255);
                            this.DrawBoneConnection(canvas, bones, PlayerBones.HumanSpine3, PlayerBones.HumanRForearm1, paint);
                            paint.Color = paint.Color.WithAlpha(200);
                            
                            if (bones.ContainsKey(PlayerBones.HumanRPalm))
                            {
                                paint.Color = paint.Color.WithAlpha(255);
                                this.DrawBoneConnection(canvas, bones, PlayerBones.HumanRForearm1, PlayerBones.HumanRPalm, paint);
                                paint.Color = paint.Color.WithAlpha(200);
                            }
                        }
                    }

                    // 左脚の描画
                    if (bones.ContainsKey(PlayerBones.HumanPelvis) && bones.ContainsKey(PlayerBones.HumanLCalf))
                    {
                        this.DrawBoneConnection(canvas, bones, PlayerBones.HumanPelvis, PlayerBones.HumanLCalf, paint);
                        
                        if (bones.ContainsKey(PlayerBones.HumanLFoot))
                        {
                            this.DrawBoneConnection(canvas, bones, PlayerBones.HumanLCalf, PlayerBones.HumanLFoot, paint);
                        }
                    }

                    // 右脚の描画
                    if (bones.ContainsKey(PlayerBones.HumanPelvis) && bones.ContainsKey(PlayerBones.HumanRCalf))
                    {
                        this.DrawBoneConnection(canvas, bones, PlayerBones.HumanPelvis, PlayerBones.HumanRCalf, paint);
                        
                        if (bones.ContainsKey(PlayerBones.HumanRFoot))
                        {
                            this.DrawBoneConnection(canvas, bones, PlayerBones.HumanRCalf, PlayerBones.HumanRFoot, paint);
                        }
                    }
                }
            }

            // テキスト情報の表示
            DrawPlayerTextInfo(canvas, player, distance, screenPos, objectSettings);
        }

        private void DrawBoneConnection(SKCanvas canvas, Dictionary<PlayerBones, Vector2> bones, PlayerBones bone1, PlayerBones bone2, SKPaint paint)
        {
            if (bones == null || !bones.ContainsKey(bone1) || !bones.ContainsKey(bone2))
                return;

            var start = bones[bone1];
            var end = bones[bone2];
            canvas.DrawLine(start.X, start.Y, end.X, end.Y, paint);
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
                this.btnToggleSkeleton.Location = new Point(90, 5);
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
                this.btnToggleSkeleton.Location = new Point(90, 5);
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
    }
} 