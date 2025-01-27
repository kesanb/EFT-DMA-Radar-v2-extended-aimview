using System.Numerics;
using SkiaSharp;
using System.Collections.ObjectModel;

namespace eft_dma_radar
{
    public class ESP
    {
        private readonly Config config;
        private const float AIMVIEW_OBJECT_SPACING = 4f;
        private float uiScale = 1.0f;
        private readonly Dictionary<string, Dictionary<PlayerBones, Vector2>> _skeletonCache = new();
        private readonly Dictionary<string, int> _lastUpdateFrame = new();
        private int _currentFrame = 0;

        public ESP(Config config)
        {
            this.config = config;
        }

        public void SetUIScale(float scale)
        {
            this.uiScale = scale;
        }

        public void DrawPlayer(SKCanvas canvas, Player player, Vector2 screenPos, float distance, AimviewObjectSettings objectSettings, Player localPlayer)
        {
            if (distance >= Math.Max(objectSettings.PaintDistance, objectSettings.TextDistance))
                return;

            // プレイヤータイプごとの設定を取得
            var typeSettings = GetPlayerTypeSettings(player.Type);

            // 直接ペイントを取得
            var paint = player.GetAimviewPaint();
            Vector2 textPosition = screenPos;
            bool isWithinPaintDistance = distance < objectSettings.PaintDistance;
            bool isWithinTextDistance = distance < objectSettings.TextDistance;

            // プレイヤーが自分を狙っているかチェックし、警告を表示
            DrawTargetingWarning(canvas, player, localPlayer, paint, typeSettings);

            if (isWithinPaintDistance)
            {
                switch (typeSettings.ESPStyle)
                {
                    case ESPStyle.Skeleton:
                        DrawPlayerSkeleton(canvas, player, GetAimviewBounds(canvas), distance);
                        break;
                    case ESPStyle.Box:
                        textPosition = DrawPlayerBox(canvas, player, distance, objectSettings);
                        if (textPosition == Vector2.Zero)
                            return;
                        break;
                    case ESPStyle.Dot:
                        DrawPlayerDot(canvas, player, screenPos, distance, objectSettings);
                        break;
                }
            }

            if (isWithinTextDistance)
            {
                DrawPlayerTextInfo(canvas, player, distance, textPosition, objectSettings, typeSettings);
            }
        }

        public void DrawTargetingWarning(SKCanvas canvas, Player player, Player localPlayer, SKPaint basePaint, PlayerTypeSettings typeSettings)
        {
            if (localPlayer == null || !player.IsTargetingLocalPlayer || !typeSettings.ShowTargetedAlert)
                return;

            var bounds = GetAimviewBounds(canvas);
            using var warningPaint = new SKPaint
            {
                Color = player.IsTargetingLocalPlayer ? basePaint.Color.WithAlpha(127) : SKColors.Transparent,
                StrokeWidth = 30,
                Style = SKPaintStyle.Stroke
            };
            canvas.DrawRect(bounds, warningPaint);
        }

        public void DrawPlayerTextInfo(SKCanvas canvas, Player player, float distance, Vector2 screenPos, AimviewObjectSettings objectSettings, PlayerTypeSettings typeSettings)
        {
            var textPaint = player.GetAimviewTextPaint();
            textPaint.TextSize = CalculateFontSize(distance);
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
                
                currentY += textPaint.TextSize * uiScale;
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
                currentY += textPaint.TextSize * uiScale;
            }

            // ヘルス情報の描画
            if (typeSettings.ShowHealth && !string.IsNullOrEmpty(player.HealthStatus))
            {
                canvas.DrawText(player.HealthStatus, textX, currentY, textPaint);
                currentY += textPaint.TextSize * uiScale;
            }

            // 距離情報の描画
            if (typeSettings.ShowDistance && objectSettings.Distance)
            {
                canvas.DrawText($"{distance:F0}m", textX, currentY, textPaint);
            }
        }

        public void DrawPlayerSkeleton(SKCanvas canvas, Player player, SKRect bounds, float distance)
        {
            if (player?.Bones == null || player.Bones.Count == 0)
                return;

            _currentFrame++;

            // キャッシュの更新頻度を決定
            int updateFrequency = GetUpdateFrequency(distance);
            bool shouldUpdate = ShouldUpdatePosition(player.ProfileID, distance);

            // キャッシュの初期化
            if (!_skeletonCache.ContainsKey(player.ProfileID))
            {
                _skeletonCache[player.ProfileID] = new Dictionary<PlayerBones, Vector2>();
                shouldUpdate = true;
            }

            if (shouldUpdate)
            {
                _lastUpdateFrame[player.ProfileID] = _currentFrame;
                _skeletonCache[player.ProfileID].Clear();

                var bonePositions = new List<Vector3>();
                var screenPositions = new List<Vector2>();
                var boneMapping = new Dictionary<int, PlayerBones>();
                int index = 0;

                // 全ボーンの位置を収集
                foreach (var boneType in player.Bones.Keys)
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

                // キャッシュを更新
                for (int i = 0; i < bonePositions.Count; i++)
                {
                    if (visibilityResults[i])
                    {
                        var screenPos = screenPositions[i];
                        screenPos.X += bounds.Left;
                        screenPos.Y += bounds.Top;
                        
                        if (IsWithinDrawingBounds(screenPos, bounds))
                        {
                            _skeletonCache[player.ProfileID][boneMapping[i]] = screenPos;
                        }
                    }
                }
            }

            // キャッシュされた座標を使用して描画
            var paint = player.GetAimviewPaint();
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 2.0f;

            if (_skeletonCache[player.ProfileID].Count >= 2)
            {
                DrawFullSkeleton(canvas, _skeletonCache[player.ProfileID], paint);
                
                // 頭部インジケーターを表示
                if (_skeletonCache[player.ProfileID].TryGetValue(PlayerBones.HumanHead, out Vector2 headPos))
                {
                    paint.Style = SKPaintStyle.Stroke;
                    float headSize = CalculateDistanceBasedSize(distance, 8.0f, 2.0f, 0.02f);
                    canvas.DrawCircle(headPos.X, headPos.Y, headSize, paint);
                }
            }
        }

        private int GetUpdateFrequency(float distance)
        {
            if (distance <= 50f) return 2;
            if (distance <= 100f) return 4;
            return 6;
        }

        private bool ShouldUpdatePosition(string playerId, float distance)
        {
            if (!_lastUpdateFrame.ContainsKey(playerId))
                return true;

            int updateFrequency = GetUpdateFrequency(distance);
            return (_currentFrame - _lastUpdateFrame[playerId]) >= updateFrequency;
        }

        public void DrawFullSkeleton(SKCanvas canvas, Dictionary<PlayerBones, Vector2> visibleBones, SKPaint paint)
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

        public void DrawBoneConnectionIfVisible(SKCanvas canvas, Dictionary<PlayerBones, Vector2> visibleBones, 
            PlayerBones bone1, PlayerBones bone2, SKPaint paint)
        {
            if (visibleBones.TryGetValue(bone1, out Vector2 start) && 
                visibleBones.TryGetValue(bone2, out Vector2 end))
            {
                canvas.DrawLine(start.X, start.Y, end.X, end.Y, paint);
            }
        }

        public Vector2 DrawPlayerBox(SKCanvas canvas, Player player, float distance, AimviewObjectSettings objectSettings)
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

            var bounds = GetAimviewBounds(canvas);
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

        public void DrawPlayerDot(SKCanvas canvas, Player player, Vector2 screenPos, float distance, AimviewObjectSettings objectSettings)
        {
            if (distance >= objectSettings.PaintDistance)
                return;

            // プレイヤーの位置を骨盤の位置から取得
            if (player?.Bones == null || !player.Bones.TryGetValue(PlayerBones.HumanPelvis, out var pelvisBone))
                return;

            // 骨盤の位置を更新
            pelvisBone.UpdatePosition();
            var bounds = GetAimviewBounds(canvas);
            
            // 骨盤の位置をスクリーン座標に変換
            if (!TryGetScreenPosition(pelvisBone.Position, bounds, out Vector2 pelvisScreenPos))
                return;

            var paint = player.GetAimviewPaint();
            paint.Style = SKPaintStyle.Fill;
            
            var dotSize = CalculateDistanceBasedSize(distance);
            canvas.DrawCircle(pelvisScreenPos.X, pelvisScreenPos.Y, dotSize, paint);
        }

        public float CalculateObjectSize(float distance, bool isText = false)
        {
            var size = isText ? 12f : 3f;
            return Math.Max(size * (1f - distance / 1000f) * uiScale, size * 0.3f * uiScale);
        }

        public float CalculateFontSize(float distance, bool small = false)
        {
            var baseSize = small ? 12f : 14f;
            return Math.Max(baseSize * (1f - distance / 1000f) * uiScale, baseSize * 0.3f * uiScale);
        }

        public float CalculateDistanceBasedSize(float distance, float maxSize = 8.0f, float minSize = 1.0f, float decayFactor = 0.02f)
        {
            var scale = (float)Math.Exp(-decayFactor * distance);
            return ((maxSize - minSize) * scale + minSize) * uiScale;
        }

        public bool IsWithinDrawingBounds(Vector2 pos, SKRect bounds)
        {
            return pos.X >= bounds.Left && pos.X <= bounds.Right &&
                   pos.Y >= bounds.Top && pos.Y <= bounds.Bottom;
        }

        public SKRect GetAimviewBounds(SKCanvas canvas)
        {
            return new SKRect(0, 0, canvas.DeviceClipBounds.Width, canvas.DeviceClipBounds.Height);
        }

        public bool TryGetScreenPosition(Vector3 worldPosition, SKRect bounds, out Vector2 screenPosition)
        {
            screenPosition = Vector2.Zero;

            try
            {
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

        public PlayerTypeSettings GetPlayerTypeSettings(PlayerType type)
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

        public void DrawLootItems(SKCanvas canvas, IEnumerable<LootableObject> lootItems, Vector3 myPosition, AimviewObjectSettings settings)
        {
            foreach (var item in lootItems)
            {
                var dist = Vector3.Distance(myPosition, item.Position);
                if (dist > Math.Max(settings.PaintDistance, settings.TextDistance))
                    continue;

                var screenPos = WorldToScreen(item.Position, canvas);
                if (screenPos == null)
                    continue;

                DrawLootableObject(canvas, item, screenPos.Value, dist, settings);
            }
        }

        public void DrawTripwires(SKCanvas canvas, IEnumerable<Tripwire> tripwires, Vector3 myPosition, AimviewObjectSettings settings)
        {
            foreach (var tripwire in tripwires)
            {
                var dist = Vector3.Distance(myPosition, tripwire.FromPos);
                if (dist > Math.Max(settings.PaintDistance, settings.TextDistance))
                    continue;

                var fromScreenPos = WorldToScreen(tripwire.FromPos, canvas);
                var toScreenPos = WorldToScreen(tripwire.ToPos, canvas);
                if (fromScreenPos == null || toScreenPos == null)
                    continue;

                DrawTripwire(canvas, tripwire, fromScreenPos.Value, toScreenPos.Value, dist, settings);
            }
        }

        public void DrawExfils(SKCanvas canvas, IEnumerable<Exfil> exfils, Vector3 myPosition, AimviewObjectSettings settings)
        {
            foreach (var exfil in exfils)
            {
                var dist = Vector3.Distance(myPosition, exfil.Position);
                if (dist > settings.TextDistance)
                    continue;

                var screenPos = WorldToScreen(exfil.Position, canvas);
                if (screenPos == null)
                    continue;

                DrawExfil(canvas, exfil, screenPos.Value, dist, settings);
            }
        }

        public void DrawTransits(SKCanvas canvas, IEnumerable<Transit> transits, Vector3 myPosition, AimviewObjectSettings settings)
        {
            foreach (var transit in transits)
            {
                var dist = Vector3.Distance(myPosition, transit.Position);
                if (dist > settings.TextDistance)
                    continue;

                var screenPos = WorldToScreen(transit.Position, canvas);
                if (screenPos == null)
                    continue;

                DrawTransit(canvas, transit, screenPos.Value, dist, settings);
            }
        }

        private void DrawLootableObject(SKCanvas canvas, LootableObject lootObject, Vector2 screenPos, float distance, AimviewObjectSettings objectSettings)
        {
            var currentY = screenPos.Y;

            if (distance < objectSettings.PaintDistance)
            {
                var objectSize = CalculateObjectSize(distance);
                var paint = lootObject.GetAimviewPaint();
                canvas.DrawCircle(screenPos.X, currentY, objectSize, paint);
                currentY += (AIMVIEW_OBJECT_SPACING + objectSize) * uiScale;
            }

            if (distance < objectSettings.TextDistance)
            {
                var textPaint = lootObject.GetAimviewTextPaint();
                textPaint.TextSize = CalculateFontSize(distance, true);
                textPaint.TextAlign = SKTextAlign.Center;

                if (objectSettings.Name)
                {
                    var name = lootObject switch
                    {
                        LootItem item => item.Item.shortName,
                        LootContainer container => container.Name,
                        LootCorpse corpse => corpse.Name,
                        _ => string.Empty
                    };
                    canvas.DrawText(name, screenPos.X, currentY + textPaint.TextSize, textPaint);
                    currentY += textPaint.TextSize * uiScale;
                }

                if (objectSettings.Value)
                {
                    var value = lootObject switch
                    {
                        LootItem item => TarkovDevManager.GetItemValue(item.Item),
                        LootContainer container => container.Value,
                        LootCorpse corpse => corpse.Value,
                        _ => 0
                    };
                    canvas.DrawText($"{value:N0}₽", screenPos.X, currentY + textPaint.TextSize, textPaint);
                    currentY += textPaint.TextSize * uiScale;
                }

                if (objectSettings.Distance)
                    canvas.DrawText($"{distance:F0}m", screenPos.X, currentY + textPaint.TextSize, textPaint);
            }
        }

        private void DrawTripwire(SKCanvas canvas, Tripwire tripwire, Vector2 fromScreenPos, Vector2 toScreenPos, float distance, AimviewObjectSettings objectSettings)
        {
            if (distance < objectSettings.PaintDistance)
            {
                var tripwirePaint = tripwire.GetAimviewPaint();
                canvas.DrawLine(fromScreenPos.X, fromScreenPos.Y, toScreenPos.X, toScreenPos.Y, tripwirePaint);
            }

            if (distance < objectSettings.TextDistance)
            {
                var textPaint = tripwire.GetAimviewTextPaint();
                textPaint.TextSize = CalculateFontSize(distance, true);
                var currentY = fromScreenPos.Y;

                if (objectSettings.Name)
                {
                    canvas.DrawText("Tripwire", fromScreenPos.X, currentY, textPaint);
                    currentY += textPaint.TextSize * uiScale;
                }

                if (objectSettings.Distance)
                    canvas.DrawText($"{distance:F0}m", fromScreenPos.X, currentY, textPaint);
            }
        }

        private void DrawExfil(SKCanvas canvas, Exfil exfil, Vector2 screenPos, float distance, AimviewObjectSettings objectSettings)
        {
            var objectSize = CalculateFontSize(distance);
            var textPaint = exfil.GetAimviewTextPaint();
            textPaint.TextSize = objectSize;

            var currentY = screenPos.Y;

            if (objectSettings.Name)
            {
                canvas.DrawText(exfil.Name, screenPos.X, currentY, textPaint);
                currentY += objectSize * uiScale;
            }

            if (objectSettings.Distance)
                canvas.DrawText($"{distance:F0}m", screenPos.X, currentY, textPaint);
        }

        private void DrawTransit(SKCanvas canvas, Transit transit, Vector2 screenPos, float distance, AimviewObjectSettings objectSettings)
        {
            var objectSize = CalculateFontSize(distance);
            var textPaint = transit.GetAimviewTextPaint();
            textPaint.TextSize = objectSize;

            var currentY = screenPos.Y;

            if (objectSettings.Name)
            {
                canvas.DrawText(transit.Name, screenPos.X, currentY, textPaint);
                currentY += objectSize * uiScale;
            }

            if (objectSettings.Distance)
                canvas.DrawText($"{distance:F0}m", screenPos.X, currentY, textPaint);
        }

        public Vector2? WorldToScreen(Vector3 worldPosition, SKCanvas canvas)
        {
            try
            {
                Vector2 screenPos;
                var bounds = GetAimviewBounds(canvas);
                var isVisible = Extensions.WorldToScreen(worldPosition, (int)bounds.Width, (int)bounds.Height, out screenPos);

                if (isVisible)
                {
                    screenPos.X += bounds.Left;
                    screenPos.Y += bounds.Top;
                    return screenPos;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }
    }
} 