using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Bwui;
using PatchNoteMailbox.Models;
using PatchNoteMailbox.Services;

namespace PatchNoteMailbox.UI
{
    /// <summary>
    /// The desktop dock: one tile per game with an unread badge, and a toggle that collapses the
    /// drawer into a single mailbox tile. BwuiWindow provides the card, desktop pinning, placement,
    /// the standard menu block, theme and --snapshot.
    /// </summary>
    public class DockWindow : BwuiWindow
    {
        // Widget accent (mailbox red). Light 5.4:1 / dark 6.4:1 on Surface, and the badge's Surface-colored
        // count reaches the same ratios on it, so it also passes as a text background (C2).
        private static readonly Color AccentLight = Palette.Hex("#C4314B"), AccentDark = Palette.Hex("#FF8A95");
        public static Color Accent { get { return Theme.Current.Accent(AccentLight, AccentDark); } }

        private const double Tile = 40;                 // game tile and toggle cell, 10 × 4px
        private const double TileGap = Tokens.Space2;    // between game tiles
        private static readonly string[] Directions = { "up", "right", "down", "left" };   // menu cycles clockwise

        private readonly StorageService _storage;
        private readonly PatchNoteFetcher _fetcher;
        private readonly List<GameConfig> _games;
        private readonly AppState _state;
        private readonly PopoverWindow _popover;
        private readonly Dictionary<string, ImageSource> _icons = new Dictionary<string, ImageSource>();
        private readonly HashSet<string> _inFlight = new HashSet<string>();
        private readonly Dictionary<string, string> _errors = new Dictionary<string, string>();

        private DockPanel _root;          // the element Build() returned last; animated on collapse/expand
        private FrameworkElement _drawer; // game tiles + divider
        private bool _animating;
        private DispatcherTimer _timer;
        private bool _snapshotPopover;

        public DockWindow(StorageService storage, PatchNoteFetcher fetcher)
            : base(Program.AppId, Text.T("게임 패치노트 우편함", "Game patch note mailbox"))
        {
            _storage = storage;
            _fetcher = fetcher;
            _games = _storage.LoadGames();
            _state = _storage.LoadState();
            _state.ExpandDirection = NormalizeDirection(_state.ExpandDirection);

            _popover = new PopoverWindow(OnMarkRead, OnMarkAllRead, OnRefreshSingleGame, GetIcon);

            if (App.IsSnapshot)
            {
                UseSnapshotData();
                Render();
                return;
            }

            // The window is sized for the expanded dock and the card sits on the anchored edge inside it,
            // so collapse/expand animates the card only and the window never moves or resizes mid-animation.
            var host = new Grid();
            Content = null;
            host.Children.Add(Card);
            Content = host;
            SizeToContent = SizeToContent.Manual;
            ApplyAnchors();
            ApplyWindowSize();
            MigratePosition();
            Render();

            _popover.IsVisibleChanged += delegate { RenderDock(); };
            Loaded += delegate { StartPolling(); };
            Closing += delegate
            {
                if (_timer != null) _timer.Stop();
                _popover.Close();
            };
        }

        // ---------- direction & geometry ----------

        private string Dir { get { return _state.ExpandDirection; } }
        private bool IsVertical { get { return Dir == "up" || Dir == "down"; } }

        private static string NormalizeDirection(string dir)
        {
            string d = (dir ?? "up").Trim().ToLowerInvariant();
            return Array.IndexOf(Directions, d) >= 0 ? d : "up";
        }

        /// <summary>The toggle stays on the anchored edge; the drawer grows away from it (DESIGN.md §6.2).</summary>
        private void ApplyAnchors()
        {
            AnchorBottom = Dir != "down";
            AnchorRight = Dir != "right";
        }

        /// <summary>Length of the game drawer: tiles, the gaps between them, and a divider with Space3 on each side.</summary>
        private double DrawerExtent()
        {
            int n = _games.Count;
            if (n == 0) return 0;
            return n * Tile + (n - 1) * TileGap + 2 * Tokens.Space3 + 1;
        }

        private void ApplyWindowSize()
        {
            double frame = 2 * (Tokens.ShadowRoom + Tokens.CardPadding);
            double shortSide = Tile + frame;
            double longSide = Tile + DrawerExtent() + frame;
            if (IsVertical)
            {
                Width = shortSide;
                Height = longSide;
                Card.HorizontalAlignment = HorizontalAlignment.Center;
                Card.VerticalAlignment = Dir == "up" ? VerticalAlignment.Bottom : VerticalAlignment.Top;
            }
            else
            {
                Width = longSide;
                Height = shortSide;
                Card.HorizontalAlignment = Dir == "left" ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                Card.VerticalAlignment = VerticalAlignment.Center;
            }
        }

        /// <summary>Center of the toggle relative to the window's top-left, for a direction.</summary>
        private Point ToggleCenter(string dir)
        {
            double edge = Tokens.ShadowRoom + Tokens.CardPadding + Tile / 2;
            double shortSide = Tile + 2 * (Tokens.ShadowRoom + Tokens.CardPadding);
            double drawer = DrawerExtent();
            if (dir == "up") return new Point(shortSide / 2, drawer + edge);
            if (dir == "down") return new Point(shortSide / 2, edge);
            if (dir == "left") return new Point(drawer + edge, shortSide / 2);
            return new Point(edge, shortSide / 2);
        }

        /// <summary>The same point for the pre-BWUI window (slot 46, gap 8, toggle 44, padding 10, margin 16).</summary>
        private static Point LegacyToggleCenter(string dir, int games)
        {
            double drawer = games * (46 + 8) + 9;
            double shortSide = 46 + 10 * 2 + 16 * 2;
            double edge = 16 + 10 + 44 / 2.0;
            if (dir == "up") return new Point(shortSide / 2, drawer + edge);
            if (dir == "down") return new Point(shortSide / 2, edge);
            if (dir == "left") return new Point(drawer + edge, shortSide / 2);
            return new Point(edge, shortSide / 2);
        }

        /// <summary>
        /// First run after the move to BWUI: settings.txt has no position yet, so take the old one from
        /// state.json, shifted so the toggle button stays exactly where the user left it.
        /// </summary>
        private void MigratePosition()
        {
            if (Settings.GetDouble("left").HasValue && Settings.GetDouble("top").HasValue) return;
            if (_state.DockLeft == -1 && _state.DockTop == -1) return;
            Point oldC = LegacyToggleCenter(Dir, _games.Count);
            Point newC = ToggleCenter(Dir);
            double l = Math.Round(_state.DockLeft + oldC.X - newC.X, 1);
            double t = Math.Round(_state.DockTop + oldC.Y - newC.Y, 1);
            Settings.SetDouble("left", l);
            Settings.SetDouble("top", t);
            Left = l;
            Top = t;
        }

        private void CycleDirection()
        {
            if (_animating) return;
            string oldDir = Dir;
            string next = Directions[(Array.IndexOf(Directions, oldDir) + 1) % Directions.Length];
            if (_popover.IsVisible) _popover.Hide();

            // Keep the toggle button still on screen while the dock turns around it.
            Point oldC = ToggleCenter(oldDir);
            double ax = Left + oldC.X, ay = Top + oldC.Y;

            _state.ExpandDirection = next;
            SaveState();
            AnchorRight = AnchorBottom = false;   // placed by hand below, not by BWUI's anchoring
            ApplyWindowSize();
            Render();
            UpdateLayout();

            Point newC = ToggleCenter(next);
            double nl = ax - newC.X, nt = ay - newC.Y;
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (nl + Width > vs.Right) nl = vs.Right - Width;
            if (nt + Height > vs.Bottom) nt = vs.Bottom - Height;
            if (nl < vs.Left) nl = vs.Left;
            if (nt < vs.Top) nt = vs.Top;
            Left = nl;
            Top = nt;
            SavePosition();
            Dispatcher.BeginInvoke(new Action(ApplyAnchors), DispatcherPriority.ContextIdle);
        }

        // ---------- build ----------

        /// <summary>Render unless an animation is running; the animation renders when it ends.</summary>
        private void RenderDock()
        {
            if (!_animating) Render();
        }

        protected override UIElement Build()
        {
            if (_snapshotPopover)
            {
                var g = _games[0];
                return _popover.BuildContent(g, Entry(g.Id), StatusOf(g.Id));
            }

            var root = new DockPanel { LastChildFill = true };
            var toggle = BuildToggle();
            DockPanel.SetDock(toggle, Dir == "up" ? Dock.Bottom : Dir == "down" ? Dock.Top : Dir == "left" ? Dock.Right : Dock.Left);
            root.Children.Add(toggle);

            var drawer = BuildDrawer();
            if (_state.IsCollapsed) drawer.Visibility = Visibility.Collapsed;
            root.Children.Add(drawer);

            double extent = Tile + (_state.IsCollapsed ? 0 : DrawerExtent());
            root.Width = IsVertical ? Tile : extent;
            root.Height = IsVertical ? extent : Tile;

            _root = root;
            _drawer = drawer;
            return root;
        }

        private FrameworkElement BuildDrawer()
        {
            var p = Theme.Current;
            bool vert = IsVertical;
            bool toggleFirst = Dir == "down" || Dir == "right";
            var panel = new StackPanel { Orientation = vert ? Orientation.Vertical : Orientation.Horizontal };
            // While the drawer is squeezed during the animation, keep the end next to the toggle in view.
            if (vert) panel.VerticalAlignment = Dir == "up" ? VerticalAlignment.Bottom : VerticalAlignment.Top;
            else panel.HorizontalAlignment = Dir == "left" ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            if (_games.Count == 0) return panel;

            var divider = new Border
            {
                Background = Ui.Br(p.Divider),
                Width = vert ? Tile : 1,
                Height = vert ? 1 : Tile,
                Margin = vert ? new Thickness(0, Tokens.Space3, 0, Tokens.Space3) : new Thickness(Tokens.Space3, 0, Tokens.Space3, 0)
            };

            if (toggleFirst) panel.Children.Add(divider);
            for (int i = 0; i < _games.Count; i++)
            {
                double lead = i == 0 ? 0 : TileGap;
                var m = vert ? new Thickness(0, lead, 0, 0) : new Thickness(lead, 0, 0, 0);
                panel.Children.Add(BuildGameTile(_games[i], m));
            }
            if (!toggleFirst) panel.Children.Add(divider);
            return panel;
        }

        private FrameworkElement BuildGameTile(GameConfig game, Thickness margin)
        {
            var p = Theme.Current;
            int unread = UnreadCount(game.Id);
            var status = StatusOf(game.Id);
            bool active = _popover.IsVisible && _popover.ActiveGameId == game.Id;
            var entry = Entry(game.Id);
            bool hasNotes = entry.Notes != null && entry.Notes.Count > 0;

            var slot = new Grid { Width = Tile, Height = Tile, Margin = margin, Cursor = Cursors.Hand, Background = Brushes.Transparent };
            var radius = new CornerRadius(Tokens.InnerRadius);
            var tile = new Border { CornerRadius = radius, Background = Ui.Br(p.SurfaceSubtle) };
            if (p.IsHighContrast) { tile.BorderBrush = Ui.Br(p.Border); tile.BorderThickness = new Thickness(1); }

            var logo = GetIcon(game.Id);
            if (logo != null)
            {
                var img = new Image
                {
                    Source = logo,
                    Stretch = Stretch.UniformToFill,
                    Width = Tile, Height = Tile,
                    Clip = new RectangleGeometry(new Rect(0, 0, Tile, Tile), Tokens.InnerRadius, Tokens.InnerRadius)
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                slot.Children.Add(tile);
                slot.Children.Add(img);
            }
            else
            {
                // No logo yet (first run, or the download failed): the first two letters of the name.
                var initials = Ui.Text(game.Name.Length >= 2 ? game.Name.Substring(0, 2) : game.Name, TextStyle.Title);
                initials.HorizontalAlignment = HorizontalAlignment.Center;
                initials.VerticalAlignment = VerticalAlignment.Center;
                tile.Child = initials;
                slot.Children.Add(tile);
            }

            // The open game gets a 2px TextPrimary ring: a shape cue, not a color change.
            if (active)
                slot.Children.Add(new Border { CornerRadius = radius, BorderBrush = Ui.Br(p.TextPrimary), BorderThickness = new Thickness(2), IsHitTestVisible = false });

            if (unread > 0)
            {
                var badge = Ui.CountBadge(unread, Accent);
                badge.Margin = new Thickness(0, -Tokens.Space1, -Tokens.Space1, 0);
                slot.Children.Add(badge);
            }
            else if (status.Error != null && !hasNotes)
            {
                var mark = Ui.StatusMark(Status.Caution);
                mark.Margin = new Thickness(0, 0, -Tokens.Space1, -Tokens.Space1);
                slot.Children.Add(mark);
            }

            // Tooltip / accessible name: name, unread count, data age when stale, last error.
            string tip = game.Name;
            if (unread > 0) tip += Text.T(" · 안 읽음 " + unread + "개", " · " + unread + " unread");
            if (hasNotes && status.Fresh.Caption != null) tip += " · " + status.Fresh.Caption;
            if (status.Error != null) tip += "\n" + status.Error;
            slot.ToolTip = tip;
            AutomationProperties.SetName(slot, tip);

            var captured = game;
            slot.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
            slot.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                OnGameIconClicked(captured);
            };
            return slot;
        }

        private FrameworkElement BuildToggle()
        {
            var cell = new Grid { Width = Tile, Height = Tile };
            if (_state.IsCollapsed)
            {
                // Collapsed: the whole widget is one mailbox tile with the total unread count.
                int total = TotalUnread();
                string tip = Text.T("패치노트 우편함", "Patch note mailbox")
                    + (total > 0 ? Text.T(" · 안 읽은 소식 " + total + "개", " · " + total + " unread") : "")
                    + Text.T(" (클릭해서 펼치기)", " (click to expand)");
                var btn = Ui.IconButton(Ui.Icon(null, Accent, Glyphs.Mail), tip, ToggleCollapse);
                btn.Width = Tile;
                btn.Height = Tile;
                btn.CornerRadius = new CornerRadius(Tokens.InnerRadius);
                cell.Children.Add(btn);
                if (total > 0)
                {
                    var badge = Ui.CountBadge(total, Accent);
                    badge.Margin = new Thickness(0, -Tokens.Space1, -Tokens.Space1, 0);
                    cell.Children.Add(badge);
                }
            }
            else
            {
                string chevron = Dir == "down" ? Glyphs.ChevronUp : Dir == "left" ? Glyphs.ChevronRight : Dir == "right" ? Glyphs.ChevronLeft : Glyphs.ChevronDown;
                var btn = Ui.IconButton(chevron, Text.T("우편함 접기", "Collapse"), ToggleCollapse);
                btn.HorizontalAlignment = HorizontalAlignment.Center;
                btn.VerticalAlignment = VerticalAlignment.Center;
                cell.Children.Add(btn);
            }
            return cell;
        }

        // ---------- collapse / expand ----------

        private void ToggleCollapse()
        {
            if (_animating || _root == null) return;
            bool collapsing = !_state.IsCollapsed;
            if (collapsing && _popover.IsVisible) _popover.Hide();

            _state.IsCollapsed = collapsing;
            _animating = true;

            // Swap the toggle (chevron ↔ mailbox) right away, then slide the drawer.
            var root = _root;
            var drawer = _drawer;
            var oldToggle = root.Children[0];
            var toggle = BuildToggle();
            DockPanel.SetDock(toggle, DockPanel.GetDock(oldToggle));
            root.Children.RemoveAt(0);
            root.Children.Insert(0, toggle);
            drawer.Visibility = Visibility.Visible;

            double full = Tile + DrawerExtent();
            var ease = new CubicEase { EasingMode = collapsing ? EasingMode.EaseInOut : EasingMode.EaseOut };
            var size = new DoubleAnimation
            {
                From = collapsing ? full : Tile,
                To = collapsing ? Tile : full,
                Duration = TimeSpan.FromMilliseconds(collapsing ? 190 : 210),
                EasingFunction = ease
            };
            var fade = new DoubleAnimation
            {
                From = collapsing ? 1.0 : 0.0,
                To = collapsing ? 0.0 : 1.0,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = ease
            };
            size.Completed += delegate
            {
                _animating = false;
                SaveState();
                Render();
            };
            root.BeginAnimation(IsVertical ? HeightProperty : WidthProperty, size);
            drawer.BeginAnimation(OpacityProperty, fade);
        }

        // ---------- menu (DESIGN.md §6.3): widget items, then BWUI's block ----------

        protected override void AddMenuItems(ContextMenu menu)
        {
            menu.Items.Add(Item(_state.IsCollapsed ? Text.T("우편함 펼치기", "Expand") : Text.T("우편함 접기", "Collapse"), ToggleCollapse));
            menu.Items.Add(Item(DirectionItem(Dir), CycleDirection));
            menu.Items.Add(Item(Text.T("games.json 열기", "Open games.json"), OpenGamesJson));
        }

        private static string DirectionItem(string dir)
        {
            string v = dir == "down" ? Text.T("아래로", "Down")
                : dir == "left" ? Text.T("왼쪽으로", "Left")
                : dir == "right" ? Text.T("오른쪽으로", "Right")
                : Text.T("위로", "Up");
            return Text.T("펼치는 방향: ", "Expand direction: ") + v;
        }

        private void OpenGamesJson()
        {
            try { Process.Start("notepad.exe", "\"" + System.IO.Path.Combine(_storage.BaseDir, "games.json") + "\""); }
            catch { }
        }

        protected override UpdateSource Updates
        {
            get { return new UpdateSource("byeonggwan", "windows-game-patchnote-widget", "PatchNoteMailbox.exe"); }
        }

        protected override bool CanRefresh { get { return true; } }
        protected override void RefreshNow() { RefreshAll(); }

        // ---------- popover ----------

        private void OnGameIconClicked(GameConfig game)
        {
            if (_popover.IsVisible && _popover.ActiveGameId == game.Id)
            {
                _popover.Hide();
                return;
            }
            _popover.ShowForGame(game, Entry(game.Id), StatusOf(game.Id), this, Dir);
            RenderDock();
        }

        private void OnMarkRead(string gameId, string idx)
        {
            var entry = Entry(gameId);
            if (!entry.ReadIds.Contains(idx))
            {
                entry.ReadIds.Add(idx);
                SaveState();
            }
            UpdateViews(gameId);
        }

        private void OnMarkAllRead(string gameId)
        {
            var entry = Entry(gameId);
            foreach (var n in entry.Notes)
            {
                if (!entry.ReadIds.Contains(n.Idx)) entry.ReadIds.Add(n.Idx);
            }
            SaveState();
            UpdateViews(gameId);
        }

        private void OnRefreshSingleGame(string gameId)
        {
            foreach (var g in _games)
            {
                if (g.Id == gameId) { var t = RefreshGameAsync(g); break; }
            }
        }

        private void UpdateViews(string gameId)
        {
            RenderDock();
            if (_popover.IsVisible && _popover.ActiveGameId == gameId)
                _popover.RefreshView(Entry(gameId), StatusOf(gameId));
        }

        // ---------- fetching (DESIGN.md §6.4) ----------

        /// <summary>The shortest poll interval in games.json, never under 5 minutes (C12). Doubled on battery (C16).</summary>
        private TimeSpan PollInterval()
        {
            int min = 60;
            bool any = false;
            foreach (var g in _games)
            {
                if (g.PollIntervalMinutes <= 0) continue;
                min = any ? Math.Min(min, g.PollIntervalMinutes) : g.PollIntervalMinutes;
                any = true;
            }
            return App.Interval(TimeSpan.FromMinutes(Math.Max(5, min)));
        }

        private TimeSpan StaleAfter { get { return TimeSpan.FromTicks(PollInterval().Ticks * 2) + TimeSpan.FromMinutes(2); } }
        private static readonly TimeSpan ExpireAfter = TimeSpan.FromDays(1);

        private void StartPolling()
        {
            // First fetch a few seconds after start, never during construction.
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _timer.Tick += delegate
            {
                _timer.Interval = PollInterval();
                RefreshAll();
            };
            _timer.Start();
        }

        private async void RefreshAll()
        {
            foreach (var g in _games.ToArray())
                await RefreshGameAsync(g);
        }

        private async Task RefreshGameAsync(GameConfig game)
        {
            if (_inFlight.Contains(game.Id)) return;   // one fetch per source at a time
            _inFlight.Add(game.Id);
            UpdateViews(game.Id);
            try
            {
                var storage = _storage;
                var fetcher = _fetcher;
                // Network and parsing run on the thread pool; the UI thread only renders.
                await Task.Run(() => fetcher.EnsureIconCachedAsync(game, storage));
                _icons.Remove(game.Id);
                var fetched = await Task.Run(() => fetcher.FetchAsync(game));

                var entry = Entry(game.Id);
                if (fetched != null && fetched.Count > 0) entry.Notes = fetched;
                entry.LastFetchedAt = DateTime.Now.ToString("HH:mm");
                entry.FetchedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                _errors.Remove(game.Id);
                SaveState();
            }
            catch (Exception ex)
            {
                _errors[game.Id] = DescribeError(ex);
            }
            finally
            {
                _inFlight.Remove(game.Id);
            }
            UpdateViews(game.Id);
        }

        /// <summary>A reason the user can act on (DESIGN.md §5 Error).</summary>
        private static string DescribeError(Exception ex)
        {
            var agg = ex as AggregateException;
            if (agg != null && agg.InnerException != null) ex = agg.GetBaseException();
            if (ex is HttpRequestException || ex is WebException || ex is TaskCanceledException)
                return Text.T("사이트에 연결할 수 없어요. 인터넷 연결을 확인해 주세요.", "Can't reach the site. Check your internet connection.");
            if (ex is NotSupportedException)
                return Text.T("games.json의 provider.type을 확인해 주세요.", "Check provider.type in games.json.");
            return Text.T("패치노트를 읽지 못했어요. games.json 설정을 확인해 주세요.", "Couldn't read the patch notes. Check games.json.");
        }

        // ---------- state ----------

        private GameCacheEntry Entry(string gameId)
        {
            GameCacheEntry entry;
            if (!_state.Cache.TryGetValue(gameId, out entry) || entry == null)
            {
                entry = new GameCacheEntry();
                _state.Cache[gameId] = entry;
            }
            if (entry.Notes == null) entry.Notes = new List<PatchNoteItem>();
            if (entry.ReadIds == null) entry.ReadIds = new List<string>();
            return entry;
        }

        private GameStatus StatusOf(string gameId)
        {
            var entry = Entry(gameId);
            DateTime? at = null;
            DateTime parsed;
            if (!string.IsNullOrEmpty(entry.FetchedAtUtc) &&
                DateTime.TryParse(entry.FetchedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
                at = parsed.ToUniversalTime();
            string err;
            _errors.TryGetValue(gameId, out err);
            return new GameStatus
            {
                Loading = _inFlight.Contains(gameId),
                Error = err,
                Fresh = Freshness.Of(at, StaleAfter, ExpireAfter)
            };
        }

        private int UnreadCount(string gameId)
        {
            var entry = Entry(gameId);
            var read = new HashSet<string>(entry.ReadIds);
            int n = 0;
            foreach (var note in entry.Notes)
            {
                if (!read.Contains(note.Idx)) n++;
            }
            return n;
        }

        private int TotalUnread()
        {
            int total = 0;
            foreach (var g in _games) total += UnreadCount(g.Id);
            return total;
        }

        private ImageSource GetIcon(string gameId)
        {
            ImageSource img;
            if (_icons.TryGetValue(gameId, out img)) return img;
            img = Ui.LoadPng(_storage.GetLocalIconPath(gameId));
            if (img != null) _icons[gameId] = img;
            return img;
        }

        private void SaveState()
        {
            if (App.IsSnapshot) return;
            try { _storage.SaveState(_state); } catch { }
        }

        // ---------- snapshot (DESIGN.md §6.9) ----------

        /// <summary>
        /// Sample data for --snapshot, never saved. Extra args: collapsed | popover, an expand direction
        /// (up | down | left | right, default up), and for checking the states: loading | error | stale | expired | empty.
        /// </summary>
        private void UseSnapshotData()
        {
            var extra = App.SnapshotArgs ?? new string[0];
            Func<string, bool> has = delegate(string a) { return Array.IndexOf(extra, a) >= 0; };
            _state.ExpandDirection = "up";
            foreach (var d in Directions)
                if (has(d)) _state.ExpandDirection = d;
            _state.IsCollapsed = has("collapsed");
            _snapshotPopover = has("popover") && _games.Count > 0;

            DateTime at = DateTime.UtcNow.AddMinutes(-3);
            if (has("stale")) at = DateTime.UtcNow.AddHours(-5);
            if (has("expired")) at = DateTime.UtcNow.AddDays(-2);
            string fetchedAt = at.ToString("o", CultureInfo.InvariantCulture);

            _state.Cache.Clear();
            _state.Cache["lostark"] = new GameCacheEntry
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
                ReadIds = new List<string> { "13578", "13565", "13551", "13545", "13542" },
                FetchedAtUtc = fetchedAt
            };
            _state.Cache["lol"] = new GameCacheEntry
            {
                Notes = new List<PatchNoteItem>
                {
                    new PatchNoteItem { Idx = "26.23", Title = "리그 오브 레전드 26.23 패치 노트", Date = "2026.11.18" },
                    new PatchNoteItem { Idx = "26.22", Title = "리그 오브 레전드 26.22 패치 노트", Date = "2026.11.04" }
                },
                ReadIds = new List<string> { "26.22" },
                FetchedAtUtc = fetchedAt
            };

            if (_games.Count == 0) return;
            string first = _games[0].Id;
            if (has("loading") || has("error") || has("empty"))
                _state.Cache[first] = new GameCacheEntry { FetchedAtUtc = has("empty") ? fetchedAt : "" };
            if (has("loading")) _inFlight.Add(first);
            if (has("error")) _errors[first] = DescribeError(new HttpRequestException());
        }
    }
}
