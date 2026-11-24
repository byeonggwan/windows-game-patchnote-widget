using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Bwui;
using PatchNoteMailbox.Models;

namespace PatchNoteMailbox.UI
{
    /// <summary>What the dock knows about one game's data source (DESIGN.md §5).</summary>
    public class GameStatus
    {
        public bool Loading;      // a fetch is running
        public string Error;      // reason the last fetch failed, or null
        public Freshness Fresh;   // age of the last good fetch
    }

    /// <summary>
    /// Transient patch note list for one game. Not a BwuiWindow (it isn't pinned to the desktop and has
    /// no menu), but it has the same card: Theme colors, CardRadius, CardPadding, the card shadow, Ui text styles.
    /// </summary>
    public class PopoverWindow : Window
    {
        private const int PageSize = 5;
        private const double RowHeight = 36;                       // 9 × 4px; also above the 28px click target (C9)
        private const double ColDot = Tokens.Space3, ColIdx = 48, ColDate = 68;

        private readonly Action<string, string> _onMarkRead;
        private readonly Action<string> _onMarkAllRead;
        private readonly Action<string> _onRefreshGame;
        private readonly Func<string, ImageSource> _getIcon;
        private readonly Border _card;

        private GameConfig _game;
        private GameCacheEntry _cache;
        private GameStatus _status = new GameStatus();
        private int _page = 1;

        public string ActiveGameId { get { return _game != null ? _game.Id : null; } }

        /// <summary>The popover uses BWUI's Wide content width (352).</summary>
        public static double ContentWidth { get { return Tokens.ContentWidth(WidgetSize.Wide); } }

        public PopoverWindow(
            Action<string, string> onMarkRead,
            Action<string> onMarkAllRead,
            Action<string> onRefreshGame,
            Func<string, ImageSource> getIcon)
        {
            _onMarkRead = onMarkRead;
            _onMarkAllRead = onMarkAllRead;
            _onRefreshGame = onRefreshGame;
            _getIcon = getIcon;

            Title = Text.T("패치노트", "Patch notes");
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            FontFamily = new FontFamily(Tokens.FontFamily);
            UseLayoutRounding = true;

            _card = new Border();
            Ui.StyleCard(_card);
            Content = _card;

            // Follow Light / Dark / High Contrast like the dock does.
            EventHandler onTheme = delegate
            {
                Ui.StyleCard(_card);
                if (IsVisible) Render();
            };
            Theme.Changed += onTheme;
            Closed += delegate { Theme.Changed -= onTheme; };

            SourceInitialized += OnSourceInitialized;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) Hide();
            };
        }

        public void ShowForGame(GameConfig game, GameCacheEntry cache, GameStatus status, Window dock, string expandDirection)
        {
            bool changedGame = (_game == null || _game.Id != game.Id);
            _game = game;
            _cache = cache ?? new GameCacheEntry();
            _status = status ?? new GameStatus();
            if (changedGame) _page = 1;

            Render();
            if (!IsVisible) Show();
            UpdateLayout();
            PositionNearDock(dock, expandDirection);
            Activate();
        }

        public void RefreshView(GameCacheEntry cache, GameStatus status)
        {
            if (_game == null) return;
            _cache = cache ?? new GameCacheEntry();
            _status = status ?? new GameStatus();
            if (IsVisible) Render();
        }

        /// <summary>The card content for a game, for the dock's --snapshot (the dock's card has the same look).</summary>
        public FrameworkElement BuildContent(GameConfig game, GameCacheEntry cache, GameStatus status)
        {
            _game = game;
            _cache = cache ?? new GameCacheEntry();
            _status = status ?? new GameStatus();
            _page = 1;
            return Build();
        }

        private void PositionNearDock(Window dock, string expandDirection)
        {
            // The two cards sit Space3 apart: overlap the transparent shadow margins of both windows.
            double overlap = 2 * Tokens.ShadowRoom - Tokens.Space3;
            Rect work = SystemParameters.WorkArea;
            string dir = (expandDirection ?? "up").ToLowerInvariant();
            double w = ActualWidth, h = ActualHeight;
            double x, y;

            if (dir == "left" || dir == "right")
            {
                // Horizontal dock: open above if there is room, otherwise below.
                x = (dir == "left") ? dock.Left + dock.ActualWidth - w : dock.Left;
                y = dock.Top - h + overlap;
                if (y < work.Top) y = dock.Top + dock.ActualHeight - overlap;
            }
            else
            {
                // Vertical dock: open to the left if there is room, otherwise to the right.
                x = dock.Left - w + overlap;
                if (x < work.Left) x = dock.Left + dock.ActualWidth - overlap;
                y = (dir == "down") ? dock.Top : dock.Top + dock.ActualHeight - h;
            }

            if (x < work.Left) x = work.Left;
            if (x + w > work.Right) x = work.Right - w;
            if (y < work.Top) y = work.Top;
            if (y + h > work.Bottom) y = work.Bottom - h;

            Left = x;
            Top = y;
        }

        private void Render()
        {
            if (_game == null) return;
            _card.Child = Build();
        }

        private FrameworkElement Build()
        {
            var p = Theme.Current;
            var accent = DockWindow.Accent;
            double w = ContentWidth;
            var notes = _cache.Notes ?? new List<PatchNoteItem>();
            var readSet = new HashSet<string>(_cache.ReadIds ?? new List<string>());
            int unreadCount = 0;
            for (int i = 0; i < notes.Count; i++)
            {
                if (!readSet.Contains(notes[i].Idx)) unreadCount++;
            }

            var root = new StackPanel { Width = w };

            // 1. Header: game logo + name; trailing: freshness caption and actions.
            var trailing = new StackPanel { Orientation = Orientation.Horizontal };
            string asOf = notes.Count > 0 ? _status.Fresh.Caption : null;
            if (asOf != null)
            {
                var c = Ui.Text(asOf, TextStyle.Caption);
                c.VerticalAlignment = VerticalAlignment.Center;
                c.Margin = new Thickness(0, 0, Tokens.Space2, 0);
                trailing.Children.Add(c);
            }
            if (unreadCount > 0)
            {
                var markAll = Ui.TextButton(Text.T("모두 읽음", "Mark all read"), delegate { _onMarkAllRead(_game.Id); });
                markAll.Margin = new Thickness(0, 0, Tokens.Space2, 0);
                trailing.Children.Add(markAll);
            }
            var refresh = Ui.IconButton(Glyphs.Refresh, Text.T("새로고침", "Refresh"), delegate { _onRefreshGame(_game.Id); });
            refresh.Margin = new Thickness(0, 0, Tokens.Space2, 0);
            trailing.Children.Add(refresh);
            trailing.Children.Add(Ui.IconButton(Glyphs.Close, Text.T("닫기", "Close"), delegate { Hide(); }));

            root.Children.Add(Ui.Header(GameIcon(accent), _game.Name, trailing));
            root.Children.Add(Ui.Divider());

            // 2. States without a list (DESIGN.md §5). Data, once there is any, is always shown.
            if (notes.Count == 0)
            {
                FrameworkElement body;
                if (_status.Loading || (_status.Error == null && !_status.Fresh.HasData))
                    body = Ui.Skeleton(w, 3);                                                   // Loading
                else if (_status.Error != null)
                    body = Ui.Message(_status.Error, Text.Retry, delegate { _onRefreshGame(_game.Id); });   // Error
                else
                    body = Ui.Message(Text.T("아직 올라온 패치노트가 없어요", "No patch notes yet"));          // Empty
                body.Margin = new Thickness(0);
                root.Children.Add(body);
                return root;
            }

            // 3. Column labels and a 5-item page, latest first. Rows are spaced, not divided (§4 Divider).
            var cols = RowGrid();
            AddCell(cols, 1, Ui.Text("IDX", TextStyle.Caption), HorizontalAlignment.Left);
            AddCell(cols, 2, Ui.Text(Text.T("제목", "Title"), TextStyle.Caption), HorizontalAlignment.Left);
            AddCell(cols, 3, Ui.Text(Text.T("등록일", "Date"), TextStyle.Caption), HorizontalAlignment.Right);
            cols.Margin = new Thickness(Tokens.Space2, 0, Tokens.Space2, Tokens.Space1);
            root.Children.Add(cols);

            int totalPages = Math.Max(1, (int)Math.Ceiling(notes.Count / (double)PageSize));
            if (_page > totalPages) _page = totalPages;
            if (_page < 1) _page = 1;
            int start = (_page - 1) * PageSize;
            int count = Math.Min(PageSize, Math.Max(0, notes.Count - start));

            var list = new StackPanel { MinHeight = PageSize * RowHeight, Opacity = _status.Fresh.BodyOpacity };
            for (int i = 0; i < count; i++)
            {
                var item = notes[start + i];
                list.Children.Add(BuildRow(item, readSet.Contains(item.Idx), accent));
            }
            root.Children.Add(list);
            root.Children.Add(Ui.Divider());

            // 4. Footer: total and pager.
            var footer = new DockPanel { LastChildFill = true };
            var pager = new StackPanel { Orientation = Orientation.Horizontal };
            pager.Children.Add(Ui.TextButton(Text.T("이전", "Prev"), _page > 1, delegate
            {
                if (_page > 1) { _page--; Render(); }
            }));
            var pageText = Ui.Text(string.Format("{0} / {1}", _page, totalPages), TextStyle.Label, p.TextPrimary);
            pageText.VerticalAlignment = VerticalAlignment.Center;
            pageText.Margin = new Thickness(Tokens.Space3, 0, Tokens.Space3, 0);
            pager.Children.Add(pageText);
            pager.Children.Add(Ui.TextButton(Text.T("다음", "Next"), _page < totalPages, delegate
            {
                if (_page < totalPages) { _page++; Render(); }
            }));
            DockPanel.SetDock(pager, Dock.Right);
            footer.Children.Add(pager);
            var total = Ui.Text(Text.T(string.Format("총 {0}건", notes.Count), string.Format("{0} notes", notes.Count)), TextStyle.Caption);
            total.VerticalAlignment = VerticalAlignment.Center;
            footer.Children.Add(total);
            root.Children.Add(footer);
            return root;
        }

        private FrameworkElement BuildRow(PatchNoteItem item, bool isRead, Color accent)
        {
            var p = Theme.Current;
            var g = RowGrid();

            // Unread: a dot AND a SemiBold title (and "Unread" in the accessible name), never color alone (C3).
            if (!isRead)
            {
                var dot = new Ellipse
                {
                    Width = Tokens.Space2, Height = Tokens.Space2,
                    Fill = Ui.Br(accent),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                Grid.SetColumn(dot, 0);
                g.Children.Add(dot);
            }

            AddCell(g, 1, Ui.Text(item.Idx, TextStyle.Caption), HorizontalAlignment.Left);

            var title = Ui.Text(item.Title, TextStyle.Body, isRead ? p.TextSecondary : p.TextPrimary);
            if (!isRead) title.FontWeight = FontWeights.SemiBold;
            title.Margin = new Thickness(0, 0, Tokens.Space2, 0);
            AddCell(g, 2, title, HorizontalAlignment.Left);

            AddCell(g, 3, Ui.Text(item.Date, TextStyle.Caption), HorizontalAlignment.Right);

            string name = (isRead ? "" : Text.T("안 읽음, ", "Unread, ")) + item.Title + ", " + item.Date;
            var captured = item;
            var row = Ui.ListRow(g, RowHeight, name, delegate
            {
                OpenInBrowser(captured.Url);
                _onMarkRead(_game.Id, captured.Idx);
            });
            row.ToolTip = item.Title;
            return row;
        }

        private static Grid RowGrid()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColDot) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColIdx) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColDate) });
            return g;
        }

        private static void AddCell(Grid g, int col, TextBlock t, HorizontalAlignment align)
        {
            t.HorizontalAlignment = align;
            t.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(t, col);
            g.Children.Add(t);
        }

        private FrameworkElement GameIcon(Color accent)
        {
            var logo = _getIcon(_game.Id);
            var icon = logo != null ? Ui.Icon(logo, accent) : Ui.Icon(null, accent, Glyphs.Mail);
            if (logo != null)
                icon.Clip = new RectangleGeometry(new Rect(0, 0, Tokens.IconSize, Tokens.IconSize), 6, 6);
            return icon;
        }

        private static void OpenInBrowser(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            // Keep the popover out of the taskbar and Alt+Tab, like the dock (C10).
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int idx, int v);
    }
}
