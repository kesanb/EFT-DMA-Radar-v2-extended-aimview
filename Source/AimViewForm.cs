using System.Numerics;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;

namespace eft_dma_radar
{
    // プレイヤータイプごとの設定クラス
    public class PlayerTypeSettings
    {
        public bool ShowName { get; set; } = true;
        public bool ShowDistance { get; set; } = true;
        public bool ShowWeapon { get; set; } = true;
        public bool ShowHealth { get; set; } = true;
        public bool ShowTargetedAlert { get; set; } = true;
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
        public Button TargetedAlertButton { get; set; }
    }

    public partial class AimViewForm : Form
    {
        // Windows APIのインポート
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        // Windows APIの定数
        private const int GWL_STYLE = -16;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_SYSMENU = 0x00080000;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_HIDEWINDOW = 0x0008;

        private bool _isTitleBarVisible = true;
        private bool _isTaskBarVisible = true;

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

        // プレイヤータイプごとのUIコントロールを管理
        private readonly Dictionary<PlayerType, PlayerTypeControls> _playerTypeControls = new();
        private ComboBox _playerTypeSelector;
        private PlayerType _currentPlayerType = PlayerType.PMC;

        // ESP描画システム
        private readonly ESP esp;

        public AimViewForm(Config config)
        {
            this.config = config;
            this.esp = new ESP(config);
            
            // フォームの基本設定
            this.Text = "AimView";
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.TopMost = true;
            this.MaximizeBox = false;  // タイトルバーのダブルクリックによる最大化を無効化
                  
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

        public void UpdateAndRedraw()
        {
            _currentFrame++;
            if (this.aimViewCanvas != null && !this.aimViewCanvas.IsDisposed)
            {
                this.aimViewCanvas.Invalidate();
            }
        }

        private void AimViewCanvas_PaintSurface(object sender, SKPaintGLSurfaceEventArgs e) // メインループ
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

        private void DrawAimview(SKCanvas canvas) // 描画処理
        {
            if (!this.config.AimviewSettings.Enabled)
                return;

            // レイド中でない場合やレイド終了時のnullチェック
            if (this.LocalPlayer == null || this.AllPlayers == null || this.CameraManager?.ViewMatrix == null)
            {
                // クロスヘアのみ描画
                this.DrawCrosshair(canvas, esp.GetAimviewBounds(canvas));
                return;
            }

            // レイド中かどうかの確認
            if (!Memory.Ready || !Memory.InGame)
            {
                // クロスヘアのみ描画
                this.DrawCrosshair(canvas, esp.GetAimviewBounds(canvas));
                return;
            }

            var aimviewBounds = esp.GetAimviewBounds(canvas);
            Vector3 myPosition;
            try
            {
                myPosition = this.LocalPlayer.Position;
            }
            catch
            {
                // Position取得に失敗した場合は描画を中止
                this.DrawCrosshair(canvas, esp.GetAimviewBounds(canvas));
                return;
            }

            var aimviewSettings = this.config.AimviewSettings;

            // クロスヘアの描画
            this.DrawCrosshair(canvas, esp.GetAimviewBounds(canvas));

            // ローカルプレイヤーの残弾数表示
            if (this.LocalPlayer != null && 
                this.LocalPlayer.ItemInHands.Item != null)
            {
                esp.DrawLocalPlayerAmmo(canvas, this.LocalPlayer);
            }

            // RENDER PLAYERS
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

                    var screenPos = esp.WorldToScreen(pelvisBone.Position, canvas);
                    if (screenPos == null || !esp.IsWithinDrawingBounds(screenPos.Value, aimviewBounds))
                        continue;

                    esp.SetUIScale(this.uiScale);
                    esp.DrawPlayer(canvas, player, screenPos.Value, dist, playerSettings, this.LocalPlayer);
                }
            }

            // RENDER LOOT
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

                    // Draw loot items based on their type
                    var items = nearbyLoot.OfType<LootItem>().ToList();
                    if (items.Any() && looseLootSettings?.Enabled == true)
                        esp.DrawLootItems(canvas, items, myPosition, looseLootSettings);

                    var containers = nearbyLoot.OfType<LootContainer>().ToList();
                    if (containers.Any() && containerSettings?.Enabled == true)
                        esp.DrawLootItems(canvas, containers, myPosition, containerSettings);

                    var corpses = nearbyLoot.OfType<LootCorpse>().ToList();
                    if (corpses.Any() && corpseSettings?.Enabled == true)
                        esp.DrawLootItems(canvas, corpses, myPosition, corpseSettings);
                }
            }

            // RENDER TRIPWIRES
            var tripwireSettings = aimviewSettings.ObjectSettings["Tripwire"];
            if (tripwireSettings?.Enabled == true && this.Tripwires != null)
            {
                esp.DrawTripwires(canvas, this.Tripwires, myPosition, tripwireSettings);
            }

            // RENDER EXFILS
            var exfilSettings = aimviewSettings.ObjectSettings["Exfil"];
            if (exfilSettings?.Enabled == true && this.Exfils != null)
            {
                esp.DrawExfils(canvas, this.Exfils, myPosition, exfilSettings);
            }

            // RENDER TRANSITS
            var transitSettings = aimviewSettings.ObjectSettings["Transit"];
            if (transitSettings?.Enabled == true && this.Transits != null)
            {
                esp.DrawTransits(canvas, this.Transits, myPosition, transitSettings);
            }
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
        
        // タイトルバーの表示/非表示を切り替えるメソッド
        public void ToggleTitleBar(bool? show = null)
        {
            var style = GetWindowLong(this.Handle, GWL_STYLE);
            bool shouldShow = show ?? !_isTitleBarVisible;

            if (!shouldShow)
            {
                style &= ~(WS_CAPTION | WS_SYSMENU);
            }
            else
            {
                style |= (WS_CAPTION | WS_SYSMENU);
            }

            SetWindowLong(this.Handle, GWL_STYLE, style);
            SetWindowPos(this.Handle, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);

            _isTitleBarVisible = shouldShow;
        }

        // タスクバーの表示/非表示を切り替えるメソッド
        public void ToggleTaskBar(bool? show = null)
        {
            IntPtr taskbarHandle = FindWindow("Shell_TrayWnd", null);
            if (taskbarHandle != IntPtr.Zero)
            {
                bool shouldShow = show ?? !_isTaskBarVisible;

                if (!shouldShow)
                {
                    // タスクバーを非表示
                    SetWindowPos(taskbarHandle, IntPtr.Zero, 0, 0, 0, 0, 
                        SWP_HIDEWINDOW | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER);
                }
                else
                {
                    // タスクバーを表示
                    SetWindowPos(taskbarHandle, IntPtr.Zero, 0, 0, 0, 0, 
                        SWP_SHOWWINDOW | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER);
                }
                _isTaskBarVisible = shouldShow;
            }
        }

        private void BtnToggleMaximize_Click(object sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Maximized)
            {
                this.WindowState = FormWindowState.Normal;
                this.FormBorderStyle = FormBorderStyle.Sizable;
                Thread.Sleep(50);
                ToggleTitleBar(true);
                Thread.Sleep(50);
                ToggleTaskBar(true);
                Thread.Sleep(50);
                ((Button)sender).Text = "□";
            }
            else
            {
                ToggleTitleBar(false);
                Thread.Sleep(50);
                ToggleTaskBar(false);
                Thread.Sleep(50);
                this.WindowState = FormWindowState.Maximized;
                this.FormBorderStyle = FormBorderStyle.None;
                ((Button)sender).Text = "❐";
            }
        }

        // プレイヤーリストを更新するためのパブリックメソッドを追加
        public void UpdatePlayerList(ReadOnlyDictionary<string, Player> allPlayers, Player localPlayer)
        {
            this.AllPlayers = allPlayers;
            this.LocalPlayer = localPlayer;
            this.esp.UpdatePlayers(localPlayer, allPlayers);
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

        private bool ShouldUpdatePosition(string playerId, float distance)
        {
            // 常に更新を許可
            return true;
        }

        private void CleanupCaches()
        {
            // キャッシュクリーンアップは不要になったため、空のメソッドとして残す
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            this.esp?.Dispose();
        }

        private PlayerTypeSettings GetPlayerTypeSettings(PlayerType type)
        {
            // BEARとUSECの場合はPMCの設定を使用
            if (type == PlayerType.BEAR || type == PlayerType.USEC)
            {
                type = PlayerType.PMC;
            }

            // 設定が存在しない場合は新しい設定を作成
            if (!this.config.AimviewSettings.PlayerTypeSettings.TryGetValue(type, out var settings))
            {
                settings = new PlayerTypeSettings
                {
                    ShowName = true,
                    ShowDistance = true,
                    ShowWeapon = true,
                    ShowHealth = true,
                    ShowTargetedAlert = true,
                    ESPStyle = ESPStyle.Skeleton
                };
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
                PlayerType.ALL,
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
            var controls = new PlayerTypeControls();
            var settings = GetPlayerTypeSettings(type);

            // ComboBoxの右側からボタンを配置
            int startX = 215; // ComboBoxの位置(90) + サイズ(120) + 間隔(5)

            // ESPスタイルボタン
            controls.ESPStyleButton = CreateButton(GetESPButtonText(settings.ESPStyle), new Point(startX, 5), GetESPButtonColor(settings.ESPStyle), (s, e) => ToggleESPStyle(type));

            // 名前表示ボタン
            controls.NameButton = CreateButton("N", new Point(startX + 35, 5), settings.ShowName ? Color.Green : Color.Red, (s, e) => ToggleName(type));

            // 武器表示ボタン
            controls.WeaponButton = CreateButton("W", new Point(startX + 70, 5), settings.ShowWeapon ? Color.Green : Color.Red, (s, e) => ToggleWeapon(type));

            // 体力表示ボタン
            controls.HealthButton = CreateButton("H", new Point(startX + 105, 5), settings.ShowHealth ? Color.Green : Color.Red, (s, e) => ToggleHealth(type));

            // 距離表示ボタン
            controls.DistanceButton = CreateButton("D", new Point(startX + 140, 5), settings.ShowDistance ? Color.Green : Color.Red, (s, e) => ToggleDistance(type));

            // 狙われ警告ボタン
            controls.TargetedAlertButton = CreateButton("⚠", new Point(startX + 175, 5), settings.ShowTargetedAlert ? Color.Green : Color.Red, (s, e) => ToggleTargetedAlert(type));

            _playerTypeControls[type] = controls;

            // ボタンをフォームに追加
            this.Controls.Add(controls.ESPStyleButton);
            this.Controls.Add(controls.NameButton);
            this.Controls.Add(controls.WeaponButton);
            this.Controls.Add(controls.HealthButton);
            this.Controls.Add(controls.DistanceButton);
            this.Controls.Add(controls.TargetedAlertButton);
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
            controls.NameButton.ForeColor = settings.ShowName ? Color.Green : Color.Red;
            controls.WeaponButton.ForeColor = settings.ShowWeapon ? Color.Green : Color.Red;
            controls.HealthButton.ForeColor = settings.ShowHealth ? Color.Green : Color.Red;
            controls.DistanceButton.ForeColor = settings.ShowDistance ? Color.Green : Color.Red;
            controls.TargetedAlertButton.ForeColor = settings.ShowTargetedAlert ? Color.Green : Color.Red;

            // すべてのボタンを前面に表示
            controls.ESPStyleButton.BringToFront();
            controls.NameButton.BringToFront();
            controls.WeaponButton.BringToFront();
            controls.HealthButton.BringToFront();
            controls.DistanceButton.BringToFront();
            controls.TargetedAlertButton.BringToFront();
            _playerTypeSelector.BringToFront();
        }

        private void UpdateControlsVisibility(PlayerType type)
        {
            foreach (var controls in _playerTypeControls.Values)
            {
                controls.ESPStyleButton.Visible = false;
                controls.NameButton.Visible = false;
                controls.WeaponButton.Visible = false;
                controls.HealthButton.Visible = false;
                controls.DistanceButton.Visible = false;
                controls.TargetedAlertButton.Visible = false;
            }

            if (_playerTypeControls.TryGetValue(type, out var selectedControls))
            {
                selectedControls.ESPStyleButton.Visible = true;
                selectedControls.NameButton.Visible = true;
                selectedControls.WeaponButton.Visible = true;
                selectedControls.HealthButton.Visible = true;
                selectedControls.DistanceButton.Visible = true;
                selectedControls.TargetedAlertButton.Visible = true;
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

            // ALLが選択されている場合、すべてのプレイヤータイプに設定を適用
            if (type == PlayerType.ALL)
            {
                foreach (var playerType in Enum.GetValues(typeof(PlayerType)).Cast<PlayerType>())
                {
                    // ALLとBEAR/USECはスキップ
                    if (playerType == PlayerType.ALL || playerType == PlayerType.BEAR || playerType == PlayerType.USEC)
                        continue;

                    var playerSettings = GetPlayerTypeSettings(playerType);
                    playerSettings.ESPStyle = settings.ESPStyle;
                    if (_playerTypeControls.TryGetValue(playerType, out var playerControls))
                    {
                        playerControls.ESPStyleButton.Text = GetESPButtonText(settings.ESPStyle);
                        playerControls.ESPStyleButton.ForeColor = GetESPButtonColor(settings.ESPStyle);
                    }
                }
            }
            this.aimViewCanvas.Invalidate();
        }

        private void ToggleName(PlayerType type)
        {
            if (!_playerTypeControls.TryGetValue(type, out var controls))
                return;

            var settings = GetPlayerTypeSettings(type);
            settings.ShowName = !settings.ShowName;
            controls.NameButton.ForeColor = settings.ShowName ? Color.Green : Color.Red;

            // ALLが選択されている場合、すべてのプレイヤータイプに設定を適用
            if (type == PlayerType.ALL)
            {
                foreach (var playerType in Enum.GetValues(typeof(PlayerType)).Cast<PlayerType>())
                {
                    // ALLとBEAR/USECはスキップ
                    if (playerType == PlayerType.ALL || playerType == PlayerType.BEAR || playerType == PlayerType.USEC)
                        continue;

                    var playerSettings = GetPlayerTypeSettings(playerType);
                    playerSettings.ShowName = settings.ShowName;
                    if (_playerTypeControls.TryGetValue(playerType, out var playerControls))
                    {
                        playerControls.NameButton.ForeColor = settings.ShowName ? Color.Green : Color.Red;
                    }
                }
            }
            this.aimViewCanvas.Invalidate();
        }

        private void ToggleWeapon(PlayerType type)
        {
            if (!_playerTypeControls.TryGetValue(type, out var controls))
                return;

            var settings = GetPlayerTypeSettings(type);
            settings.ShowWeapon = !settings.ShowWeapon;
            controls.WeaponButton.ForeColor = settings.ShowWeapon ? Color.Green : Color.Red;

            // ALLが選択されている場合、すべてのプレイヤータイプに設定を適用
            if (type == PlayerType.ALL)
            {
                foreach (var playerType in Enum.GetValues(typeof(PlayerType)).Cast<PlayerType>())
                {
                    // ALLとBEAR/USECはスキップ
                    if (playerType == PlayerType.ALL || playerType == PlayerType.BEAR || playerType == PlayerType.USEC)
                        continue;

                    var playerSettings = GetPlayerTypeSettings(playerType);
                    playerSettings.ShowWeapon = settings.ShowWeapon;
                    if (_playerTypeControls.TryGetValue(playerType, out var playerControls))
                    {
                        playerControls.WeaponButton.ForeColor = settings.ShowWeapon ? Color.Green : Color.Red;
                    }
                }
            }
            this.aimViewCanvas.Invalidate();
        }

        private void ToggleHealth(PlayerType type)
        {
            if (!_playerTypeControls.TryGetValue(type, out var controls))
                return;

            var settings = GetPlayerTypeSettings(type);
            settings.ShowHealth = !settings.ShowHealth;
            controls.HealthButton.ForeColor = settings.ShowHealth ? Color.Green : Color.Red;

            // ALLが選択されている場合、すべてのプレイヤータイプに設定を適用
            if (type == PlayerType.ALL)
            {
                foreach (var playerType in Enum.GetValues(typeof(PlayerType)).Cast<PlayerType>())
                {
                    // ALLとBEAR/USECはスキップ
                    if (playerType == PlayerType.ALL || playerType == PlayerType.BEAR || playerType == PlayerType.USEC)
                        continue;

                    var playerSettings = GetPlayerTypeSettings(playerType);
                    playerSettings.ShowHealth = settings.ShowHealth;
                    if (_playerTypeControls.TryGetValue(playerType, out var playerControls))
                    {
                        playerControls.HealthButton.ForeColor = settings.ShowHealth ? Color.Green : Color.Red;
                    }
                }
            }
            this.aimViewCanvas.Invalidate();
        }

        private void ToggleDistance(PlayerType type)
        {
            if (!_playerTypeControls.TryGetValue(type, out var controls))
                return;

            var settings = GetPlayerTypeSettings(type);
            settings.ShowDistance = !settings.ShowDistance;
            controls.DistanceButton.ForeColor = settings.ShowDistance ? Color.Green : Color.Red;

            // ALLが選択されている場合、すべてのプレイヤータイプに設定を適用
            if (type == PlayerType.ALL)
            {
                foreach (var playerType in Enum.GetValues(typeof(PlayerType)).Cast<PlayerType>())
                {
                    // ALLとBEAR/USECはスキップ
                    if (playerType == PlayerType.ALL || playerType == PlayerType.BEAR || playerType == PlayerType.USEC)
                        continue;

                    var playerSettings = GetPlayerTypeSettings(playerType);
                    playerSettings.ShowDistance = settings.ShowDistance;
                    if (_playerTypeControls.TryGetValue(playerType, out var playerControls))
                    {
                        playerControls.DistanceButton.ForeColor = settings.ShowDistance ? Color.Green : Color.Red;
                    }
                }
            }
            this.aimViewCanvas.Invalidate();
        }

        private void ToggleTargetedAlert(PlayerType type)
        {
            if (!_playerTypeControls.TryGetValue(type, out var controls))
                return;

            var settings = GetPlayerTypeSettings(type);
            settings.ShowTargetedAlert = !settings.ShowTargetedAlert;
            controls.TargetedAlertButton.ForeColor = settings.ShowTargetedAlert ? Color.Green : Color.Red;

            // ALLが選択されている場合、すべてのプレイヤータイプに設定を適用
            if (type == PlayerType.ALL)
            {
                foreach (var playerType in Enum.GetValues(typeof(PlayerType)).Cast<PlayerType>())
                {
                    // ALLとBEAR/USECはスキップ
                    if (playerType == PlayerType.ALL || playerType == PlayerType.BEAR || playerType == PlayerType.USEC)
                        continue;

                    var playerSettings = GetPlayerTypeSettings(playerType);
                    playerSettings.ShowTargetedAlert = settings.ShowTargetedAlert;
                    if (_playerTypeControls.TryGetValue(playerType, out var playerControls))
                    {
                        playerControls.TargetedAlertButton.ForeColor = settings.ShowTargetedAlert ? Color.Green : Color.Red;
                    }
                }
            }
            this.aimViewCanvas.Invalidate();
        }
    }
} 