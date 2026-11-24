using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PatchNoteMailbox.Models;
using PatchNoteMailbox.Services;

namespace PatchNoteMailbox.UI
{
    public class DockWindow : Window
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "PatchNoteMailbox";
        private const double CardMargin = 16;
        private const double CardPadding = 10;
        private const double SlotSize = 46;
        private const double SlotSpacing = 8;
        private const double ToggleSize = 44;

        private static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
        private static Brush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private static readonly Color CText = C("#1F1E1C");
        private static readonly Color CLabel = C("#6B6962");
        private static readonly Color CSep = C("#EFEDE7");
        private static readonly Color CDanger = C("#E24B4A");
        private static readonly Color CBtn = C("#F4F3EF");
        private static readonly Color CBtnHover = C("#E9E7E0");

        private readonly StorageService _storage;
        private readonly PatchNoteFetcher _fetcher;
        private List<GameConfig> _games;
        private AppState _state;

        private Grid _canvasGrid;
        private Border _card;
        private StackPanel _rootPanel;
        private Border _gamesClip;
        private StackPanel _gamesPanel;
        private Border _divider;
        private Grid _toggleSlot;
        private PopoverWindow _popover;
        private DispatcherTimer _pollTimer;
        private bool _loaded;
        private bool _animating;

        public DockWindow(StorageService storage, PatchNoteFetcher fetcher)
        {
            _storage = storage;
            _fetcher = fetcher;
            _games = _storage.LoadGames();
            _state = _storage.LoadState();
            _state.ExpandDirection = NormalizeDirection(_state.ExpandDirection);

            Title = "게임 패치노트 우편함";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Manual;
            MinWidth = 0;
            MinHeight = 0;
            WindowStartupLocation = WindowStartupLocation.Manual;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic");
            UseLayoutRounding = true;

            _canvasGrid = new Grid { Background = Brushes.Transparent };

            _card = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(22),
                Margin = new Thickness(CardMargin),
                Padding = new Thickness(CardPadding),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 3,
                    Direction = 270,
                    Opacity = 0.16,
                    Color = C("#1E283C")
                }
            };

            _rootPanel = new StackPanel();
            _gamesPanel = new StackPanel();
            _gamesClip = new Border { ClipToBounds = true, Child = _gamesPanel };
            _divider = new Border { Background = B(CSep) };
            _toggleSlot = new Grid { Width = ToggleSize, Height = ToggleSize, Cursor = Cursors.Hand };

            _card.Child = _rootPanel;
            _canvasGrid.Children.Add(_card);
            Content = _canvasGrid;

            _card.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                try { DragMove(); } catch { }
                SavePosition();
            };

            _popover = new PopoverWindow(
                OnMarkRead,
                OnMarkAllRead,
                OnRefreshSingleGame,
                delegate(string id) { return _storage.GetLocalIconPath(id); });

            ContextMenu = BuildContextMenu();
            ApplyFixedWindowDimensions();
            RenderDock(true);

            // Sit cleanly on the Windows desktop just like AiUsageWidget
            try { new WindowInteropHelper(this).Owner = FindWindow("Progman", null); } catch { }
            SourceInitialized += OnSourceInitialized;
            Loaded += OnLoaded;
            Closing += OnClosing;
        }

        private static string NormalizeDirection(string dir)
        {
            string d = (dir ?? "up").Trim().ToLowerInvariant();
            if (d == "down" || d == "left" || d == "right") return d;
            return "up";
        }

        private bool IsVerticalDir()
        {
            string d = NormalizeDirection(_state.ExpandDirection);
            return d == "up" || d == "down";
        }

        private bool IsToggleFirst()
        {
            string d = NormalizeDirection(_state.ExpandDirection);
            return d == "down" || d == "right";
        }

        public static void RenderSnapshot(StorageService storage, PatchNoteFetcher fetcher, string outPath, string mode)
        {
            var games = storage.LoadGames();
            foreach (var g in games)
            {
                try { fetcher.EnsureIconCachedAsync(g, storage).Wait(); } catch { }
            }

            var w = new DockWindow(storage, fetcher);
            w._state.ExpandDirection = "up";
            // Always use clean showcase unread state in snapshot previews (does not save to disk)
            w._state.Cache["lostark"] = new GameCacheEntry
            {
                Notes = new List<PatchNoteItem>
                {
                    new PatchNoteItem { Idx = "13598", Title = "11월 18일(수) 업데이트 내역 안내", Date = "2026.11.18" },
                    new PatchNoteItem { Idx = "13585", Title = "11월 4일(수) 업데이트 내역 안내", Date = "2026.11.04" },
                    new PatchNoteItem { Idx = "13578", Title = "10월 21일(수) 업데이트 내역 안내", Date = "2026.10.21" },
                    new PatchNoteItem { Idx = "13565", Title = "10월 7일(수) 업데이트 내역 안내", Date = "2026.10.07" },
                    new PatchNoteItem { Idx = "13551", Title = "9월 23일(수) 업데이트 내역 안내", Date = "2026.09.23" },
                    new PatchNoteItem { Idx = "13545", Title = "9월 16일(수) 업데이트 내역 안내", Date = "2026.09.16" },
                    new PatchNoteItem { Idx = "13542", Title = "9월 9일(수) 업데이트 내역 안내", Date = "2026.09.09" }
                },
                ReadIds = new List<string> { "13578", "13565", "13551", "13545", "13542" }
            };
            w._state.Cache["lol"] = new GameCacheEntry
            {
                Notes = new List<PatchNoteItem>
                {
                    new PatchNoteItem { Idx = "26.23", Title = "리그 오브 레전드 26.23 패치 노트", Date = "2026.11.18" },
                    new PatchNoteItem { Idx = "26.22", Title = "리그 오브 레전드 26.22 패치 노트", Date = "2026.11.04" }
                },
                ReadIds = new List<string> { "26.22" }
            };

            FrameworkElement target;
            if (mode == "popover")
            {
                var g = games[0];
                GameCacheEntry cache;
                if (!w._state.Cache.TryGetValue(g.Id, out cache)) cache = new GameCacheEntry();
                target = w._popover.DetachCardForSnapshot(g, cache);
            }
            else
            {
                w._state.IsCollapsed = (mode == "collapsed");
                w.ApplyFixedWindowDimensions();
                w.RenderDock(true);
                w._canvasGrid.Children.Remove(w._card);
                w._card.HorizontalAlignment = HorizontalAlignment.Center;
                w._card.VerticalAlignment = VerticalAlignment.Center;
                target = w._card;
            }

            var desk = new Border
            {
                Background = new SolidColorBrush(C("#C9D6E3")),
                Padding = new Thickness(24),
                Child = target
            };
            desk.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            desk.Arrange(new Rect(desk.DesiredSize));
            desk.UpdateLayout();

            int pw = (int)Math.Ceiling(desk.ActualWidth * 2);
            int ph = (int)Math.Ceiling(desk.ActualHeight * 2);
            var rtb = new RenderTargetBitmap(pw, ph, 192, 192, PixelFormats.Pbgra32);
            rtb.Render(desk);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPath)));
            using (var fs = File.Create(outPath)) enc.Save(fs);
        }

        private double GetExpandedDrawerExtent()
        {
            // Each game slot (46) + spacing (8) + divider (1) + divider margin (8)
            return _games.Count * (SlotSize + SlotSpacing) + 9;
        }

        private void ApplyFixedWindowDimensions()
        {
            string dir = NormalizeDirection(_state.ExpandDirection);
            double drawerExtent = GetExpandedDrawerExtent();
            double shortSide = SlotSize + CardPadding * 2 + CardMargin * 2;
            double longSide = drawerExtent + ToggleSize + CardPadding * 2 + CardMargin * 2;

            if (dir == "up" || dir == "down")
            {
                Width = shortSide;
                Height = longSide;
                _card.HorizontalAlignment = HorizontalAlignment.Center;
                _card.VerticalAlignment = (dir == "up") ? VerticalAlignment.Bottom : VerticalAlignment.Top;
            }
            else
            {
                Width = longSide;
                Height = shortSide;
                _card.HorizontalAlignment = (dir == "left") ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                _card.VerticalAlignment = VerticalAlignment.Center;
            }
        }

        // Compute the toggle button's center relative to the Window's Top-Left
        private Point GetToggleCenterOffset(string dir)
        {
            double drawerExtent = GetExpandedDrawerExtent();
            double shortSide = SlotSize + CardPadding * 2 + CardMargin * 2;
            double edgeToCenter = CardMargin + CardPadding + ToggleSize / 2.0;

            if (dir == "up")
                return new Point(shortSide / 2.0, drawerExtent + edgeToCenter);
            if (dir == "down")
                return new Point(shortSide / 2.0, edgeToCenter);
            if (dir == "left")
                return new Point(drawerExtent + edgeToCenter, shortSide / 2.0);
            // right
            return new Point(edgeToCenter, shortSide / 2.0);
        }

        private void SetExpandDirection(string newDir)
        {
            string oldDir = NormalizeDirection(_state.ExpandDirection);
            string targetDir = NormalizeDirection(newDir);
            if (oldDir == targetDir) return;

            if (_popover.IsVisible) _popover.Hide();

            // Keep the toggle button stationary on screen while switching direction
            Point oldOffset = GetToggleCenterOffset(oldDir);
            double anchorScreenX = Left + oldOffset.X;
            double anchorScreenY = Top + oldOffset.Y;

            _state.ExpandDirection = targetDir;
            ApplyFixedWindowDimensions();
            RenderDock(true);

            Point newOffset = GetToggleCenterOffset(targetDir);
            double nextLeft = anchorScreenX - newOffset.X;
            double nextTop = anchorScreenY - newOffset.Y;

            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (nextLeft < vs.Left) nextLeft = vs.Left;
            if (nextLeft + Width > vs.Right) nextLeft = vs.Right - Width;
            if (nextTop < vs.Top) nextTop = vs.Top;
            if (nextTop + Height > vs.Bottom) nextTop = vs.Bottom - Height;

            Left = nextLeft;
            Top = nextTop;
            SavePosition();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            RestorePosition();
            _loaded = true;
            SavePosition();
            await RefreshAllAsync();

            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
            _pollTimer.Tick += async delegate { await RefreshAllAsync(); };
            _pollTimer.Start();
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_pollTimer != null) _pollTimer.Stop();
            SavePosition();
            if (_popover != null) _popover.Close();
        }

        private void RestorePosition()
        {
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            bool hasSaved = !(_state.DockLeft == -1 && _state.DockTop == -1);
            if (hasSaved && vs.Contains(new Point(_state.DockLeft + 30, _state.DockTop + 30)))
            {
                Left = _state.DockLeft;
                Top = _state.DockTop;
            }
            else
            {
                // Place cleanly beside AiUsageWidget at bottom-right
                Rect work = SystemParameters.WorkArea;
                Left = work.Right - Width - 310;
                Top = work.Bottom - Height - 12;
            }
        }

        private void SavePosition()
        {
            if (!_loaded) return;
            _state.DockLeft = Left;
            _state.DockTop = Top;
            _storage.SaveState(_state);
        }

        private void RenderDock(bool applyCollapsedImmediately)
        {
            bool isVert = IsVerticalDir();
            bool toggleFirst = IsToggleFirst();

            _rootPanel.Orientation = isVert ? Orientation.Vertical : Orientation.Horizontal;
            _gamesPanel.Orientation = isVert ? Orientation.Vertical : Orientation.Horizontal;

            // Arrange root children based on expand direction
            _rootPanel.Children.Clear();
            if (toggleFirst)
            {
                _rootPanel.Children.Add(_toggleSlot);
                _rootPanel.Children.Add(_gamesClip);
            }
            else
            {
                _rootPanel.Children.Add(_gamesClip);
                _rootPanel.Children.Add(_toggleSlot);
            }

            // Configure divider
            _divider.Width = isVert ? 26 : 1;
            _divider.Height = isVert ? 1 : 26;
            _divider.HorizontalAlignment = HorizontalAlignment.Center;
            _divider.VerticalAlignment = VerticalAlignment.Center;
            if (toggleFirst)
            {
                _divider.Margin = isVert ? new Thickness(0, 8, 0, 0) : new Thickness(8, 0, 0, 0);
            }
            else
            {
                _divider.Margin = isVert ? new Thickness(0, 0, 0, 8) : new Thickness(0, 0, 8, 0);
            }
            if (_divider.Parent != null)
            {
                var parent = _divider.Parent as Panel;
                if (parent != null) parent.Children.Remove(_divider);
            }

            // Populate game icons
            _gamesPanel.Children.Clear();
            if (toggleFirst)
            {
                _gamesPanel.Children.Add(_divider);
            }

            for (int i = 0; i < _games.Count; i++)
            {
                var game = _games[i];
                int unread = GetUnreadCount(game.Id);
                bool isActive = _popover.IsVisible && _popover.ActiveGameId == game.Id;

                Thickness slotMargin;
                if (toggleFirst)
                    slotMargin = isVert ? new Thickness(0, SlotSpacing, 0, 0) : new Thickness(SlotSpacing, 0, 0, 0);
                else
                    slotMargin = isVert ? new Thickness(0, 0, 0, SlotSpacing) : new Thickness(0, 0, SlotSpacing, 0);

                var slot = new Grid
                {
                    Width = SlotSize,
                    Height = SlotSize,
                    Margin = slotMargin,
                    Cursor = Cursors.Hand,
                    ToolTip = unread > 0
                        ? string.Format("{0} (안 읽음 {1}개)", game.Name, unread)
                        : game.Name
                };

                var iconContainer = new Border
                {
                    Width = 42,
                    Height = 42,
                    CornerRadius = new CornerRadius(11),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    BorderThickness = new Thickness(isActive ? 2 : 0),
                    BorderBrush = isActive ? B(CText) : Brushes.Transparent,
                    Background = B(CBtn)
                };

                string iconPath = _storage.GetLocalIconPath(game.Id);
                if (File.Exists(iconPath))
                {
                    var bmp = LoadBitmap(iconPath);
                    if (bmp != null)
                    {
                        var img = new Image
                        {
                            Source = bmp,
                            Stretch = Stretch.UniformToFill,
                            Width = 42,
                            Height = 42,
                            Clip = new RectangleGeometry(new Rect(0, 0, 42, 42), 11, 11)
                        };
                        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                        iconContainer.Child = img;
                    }
                }
                else
                {
                    string initials = game.Name.Length >= 2 ? game.Name.Substring(0, 2) : game.Name;
                    iconContainer.Child = new TextBlock
                    {
                        Text = initials,
                        Foreground = B(CText),
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 12,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                }

                slot.Children.Add(iconContainer);
                if (unread > 0)
                {
                    slot.Children.Add(CreateUnreadBadge(unread));
                }

                var captured = game;
                slot.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs ev) { ev.Handled = true; };
                slot.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs ev)
                {
                    ev.Handled = true;
                    OnGameIconClicked(captured);
                };

                _gamesPanel.Children.Add(slot);
            }

            if (!toggleFirst)
            {
                _gamesPanel.Children.Add(_divider);
            }

            if (applyCollapsedImmediately)
            {
                _gamesClip.BeginAnimation(HeightProperty, null);
                _gamesClip.BeginAnimation(WidthProperty, null);
                _gamesClip.BeginAnimation(OpacityProperty, null);

                double full = GetExpandedDrawerExtent();
                if (isVert)
                {
                    _gamesClip.Width = double.NaN;
                    _gamesClip.Height = _state.IsCollapsed ? 0 : full;
                }
                else
                {
                    _gamesClip.Height = double.NaN;
                    _gamesClip.Width = _state.IsCollapsed ? 0 : full;
                }
                _gamesClip.Opacity = _state.IsCollapsed ? 0 : 1;
            }

            RenderToggleButton();
        }

        private string GetCollapseArrow()
        {
            string dir = NormalizeDirection(_state.ExpandDirection);
            if (dir == "down") return "▲";
            if (dir == "left") return "▶";
            if (dir == "right") return "◀";
            return "▼";
        }

        private void RenderToggleButton()
        {
            _toggleSlot.Children.Clear();
            int totalUnread = GetTotalUnreadCount();

            var btnBg = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(12),
                Background = B(CBtn),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (_state.IsCollapsed)
            {
                btnBg.Child = CreateMailboxGlyph();
                _toggleSlot.ToolTip = totalUnread > 0
                    ? string.Format("패치노트 우편함 (안 읽은 소식 {0}개 · 클릭해서 펼치기)", totalUnread)
                    : "패치노트 우편함 (클릭해서 펼치기)";
                _toggleSlot.Children.Add(btnBg);
                if (totalUnread > 0)
                {
                    _toggleSlot.Children.Add(CreateUnreadBadge(totalUnread));
                }
            }
            else
            {
                btnBg.Width = 32;
                btnBg.Height = 32;
                btnBg.CornerRadius = new CornerRadius(16);
                btnBg.Child = new TextBlock
                {
                    Text = GetCollapseArrow(),
                    FontSize = 10,
                    Foreground = B(CLabel),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _toggleSlot.ToolTip = "우편함 접기";
                _toggleSlot.Children.Add(btnBg);
            }

            btnBg.MouseEnter += delegate { btnBg.Background = B(CBtnHover); };
            btnBg.MouseLeave += delegate { btnBg.Background = B(CBtn); };
            _toggleSlot.MouseLeftButtonDown -= OnToggleMouseDown;
            _toggleSlot.MouseLeftButtonUp -= OnToggleMouseUp;
            _toggleSlot.MouseLeftButtonDown += OnToggleMouseDown;
            _toggleSlot.MouseLeftButtonUp += OnToggleMouseUp;
        }

        private void OnToggleMouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        private void OnToggleMouseUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ToggleCollapse();
        }

        private void ToggleCollapse()
        {
            if (_animating) return;

            _state.IsCollapsed = !_state.IsCollapsed;
            if (_state.IsCollapsed && _popover.IsVisible)
            {
                _popover.Hide();
            }

            bool collapsing = _state.IsCollapsed;
            bool isVert = IsVerticalDir();
            double fullExtent = GetExpandedDrawerExtent();
            DependencyProperty sizeProp = isVert ? HeightProperty : WidthProperty;

            _animating = true;
            RenderToggleButton();

            var ease = new CubicEase { EasingMode = collapsing ? EasingMode.EaseInOut : EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(collapsing ? 190 : 210);

            var sizeAnim = new DoubleAnimation
            {
                From = collapsing ? fullExtent : 0,
                To = collapsing ? 0 : fullExtent,
                Duration = dur,
                EasingFunction = ease
            };

            var opacityAnim = new DoubleAnimation
            {
                From = collapsing ? 1.0 : 0.0,
                To = collapsing ? 0.0 : 1.0,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = ease
            };

            sizeAnim.Completed += delegate
            {
                _animating = false;
                _storage.SaveState(_state);
            };

            _gamesClip.BeginAnimation(sizeProp, sizeAnim);
            _gamesClip.BeginAnimation(OpacityProperty, opacityAnim);
        }

        private static UIElement CreateMailboxGlyph()
        {
            return new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 2,5 L 18,5 L 18,15 L 2,15 Z M 2,6 L 10,11.5 L 18,6"),
                Stroke = B(CText),
                StrokeThickness = 1.7,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static Border CreateUnreadBadge(int count)
        {
            return new Border
            {
                MinWidth = 19,
                Height = 19,
                Padding = new Thickness(5, 0, 5, 0),
                CornerRadius = new CornerRadius(9.5),
                Background = B(CDanger),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -2, -2, 0),
                Child = new TextBlock
                {
                    Text = count > 99 ? "99+" : count.ToString(),
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        private void OnGameIconClicked(GameConfig game)
        {
            if (_popover.IsVisible && _popover.ActiveGameId == game.Id)
            {
                _popover.Hide();
                RenderDock(false);
                return;
            }

            GameCacheEntry entry;
            if (!_state.Cache.TryGetValue(game.Id, out entry))
            {
                entry = new GameCacheEntry();
                _state.Cache[game.Id] = entry;
            }

            _popover.ShowForGame(game, entry, this, _state.ExpandDirection);
            RenderDock(false);
        }

        private void OnMarkRead(string gameId, string idx)
        {
            GameCacheEntry entry;
            if (!_state.Cache.TryGetValue(gameId, out entry)) return;
            if (entry.ReadIds == null) entry.ReadIds = new List<string>();
            if (!entry.ReadIds.Contains(idx))
            {
                entry.ReadIds.Add(idx);
                _storage.SaveState(_state);
            }
            RenderDock(false);
            _popover.RefreshView(entry);
        }

        private void OnMarkAllRead(string gameId)
        {
            GameCacheEntry entry;
            if (!_state.Cache.TryGetValue(gameId, out entry)) return;
            if (entry.ReadIds == null) entry.ReadIds = new List<string>();
            if (entry.Notes != null)
            {
                for (int i = 0; i < entry.Notes.Count; i++)
                {
                    string id = entry.Notes[i].Idx;
                    if (!entry.ReadIds.Contains(id)) entry.ReadIds.Add(id);
                }
            }
            _storage.SaveState(_state);
            RenderDock(false);
            _popover.RefreshView(entry);
        }

        private async void OnRefreshSingleGame(string gameId)
        {
            for (int i = 0; i < _games.Count; i++)
            {
                if (_games[i].Id == gameId)
                {
                    await RefreshGameAsync(_games[i]);
                    break;
                }
            }
        }

        private async Task RefreshAllAsync()
        {
            for (int i = 0; i < _games.Count; i++)
            {
                await RefreshGameAsync(_games[i]);
            }
        }

        private async Task RefreshGameAsync(GameConfig game)
        {
            try
            {
                await _fetcher.EnsureIconCachedAsync(game, _storage);
                var fetched = await _fetcher.FetchAsync(game);

                GameCacheEntry entry;
                if (!_state.Cache.TryGetValue(game.Id, out entry))
                {
                    entry = new GameCacheEntry();
                    _state.Cache[game.Id] = entry;
                }

                if (fetched != null && fetched.Count > 0)
                {
                    entry.Notes = fetched;
                }
                entry.LastFetchedAt = DateTime.Now.ToString("HH:mm");
                _storage.SaveState(_state);

                RenderDock(false);
                if (_popover.IsVisible && _popover.ActiveGameId == game.Id)
                {
                    _popover.RefreshView(entry);
                }
            }
            catch { }
        }

        private int GetUnreadCount(string gameId)
        {
            GameCacheEntry entry;
            if (!_state.Cache.TryGetValue(gameId, out entry) || entry.Notes == null) return 0;
            var readSet = new HashSet<string>(entry.ReadIds ?? new List<string>());
            int cnt = 0;
            for (int i = 0; i < entry.Notes.Count; i++)
            {
                if (!readSet.Contains(entry.Notes[i].Idx)) cnt++;
            }
            return cnt;
        }

        private int GetTotalUnreadCount()
        {
            int total = 0;
            for (int i = 0; i < _games.Count; i++)
            {
                total += GetUnreadCount(_games[i].Id);
            }
            return total;
        }

        private ContextMenu BuildContextMenu()
        {
            var m = new ContextMenu();
            var refresh = new MenuItem { Header = "지금 새로고침" };
            refresh.Click += async delegate { await RefreshAllAsync(); };
            m.Items.Add(refresh);

            var toggleCollapse = new MenuItem { Header = "접기 / 펼치기" };
            toggleCollapse.Click += delegate { ToggleCollapse(); };
            m.Items.Add(toggleCollapse);

            // 4-way expand direction submenu
            var dirMenu = new MenuItem { Header = "펼치는 방향" };
            var dirUp = new MenuItem { Header = "위로", IsCheckable = true };
            var dirDown = new MenuItem { Header = "아래로", IsCheckable = true };
            var dirLeft = new MenuItem { Header = "왼쪽으로", IsCheckable = true };
            var dirRight = new MenuItem { Header = "오른쪽으로", IsCheckable = true };

            dirUp.Click += delegate { SetExpandDirection("up"); };
            dirDown.Click += delegate { SetExpandDirection("down"); };
            dirLeft.Click += delegate { SetExpandDirection("left"); };
            dirRight.Click += delegate { SetExpandDirection("right"); };

            dirMenu.Items.Add(dirUp);
            dirMenu.Items.Add(dirDown);
            dirMenu.Items.Add(dirLeft);
            dirMenu.Items.Add(dirRight);
            m.Items.Add(dirMenu);

            m.Items.Add(new Separator());

            var startup = new MenuItem { Header = "Windows 시작 시 실행", IsCheckable = true };
            m.Opened += delegate
            {
                string d = NormalizeDirection(_state.ExpandDirection);
                dirUp.IsChecked = (d == "up");
                dirDown.IsChecked = (d == "down");
                dirLeft.IsChecked = (d == "left");
                dirRight.IsChecked = (d == "right");
                startup.IsChecked = IsStartupEnabled();
            };
            startup.Click += delegate { SetStartup(startup.IsChecked); };
            m.Items.Add(startup);

            var openCfg = new MenuItem { Header = "games.json 열기" };
            openCfg.Click += delegate
            {
                try { Process.Start("notepad.exe", System.IO.Path.Combine(_storage.BaseDir, "games.json")); }
                catch { }
            };
            m.Items.Add(openCfg);

            m.Items.Add(new Separator());
            var exit = new MenuItem { Header = "종료" };
            exit.Click += delegate { Close(); };
            m.Items.Add(exit);
            return m;
        }

        private static bool IsStartupEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    return k != null && k.GetValue(AppName) != null;
                }
            }
            catch { return false; }
        }

        private static void SetStartup(bool enable)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (enable) k.SetValue(AppName, "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
                    else k.DeleteValue(AppName, false);
                }
            }
            catch { }
        }

        private static BitmapImage LoadBitmap(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
            var src = HwndSource.FromHwnd(h);
            if (src != null) src.AddHook(Hook);
            SetWindowPos(h, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        }

        private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_WINDOWPOSCHANGING)
            {
                var wp = (WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(WINDOWPOS));
                wp.hwndInsertAfter = HWND_BOTTOM;
                wp.flags &= ~SWP_NOZORDER;
                Marshal.StructureToPtr(wp, lParam, false);
            }
            return IntPtr.Zero;
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int WM_WINDOWPOSCHANGING = 0x46;
        private const uint SWP_NOSIZE = 0x1;
        private const uint SWP_NOMOVE = 0x2;
        private const uint SWP_NOZORDER = 0x4;
        private const uint SWP_NOACTIVATE = 0x10;
        private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int idx, int v);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
