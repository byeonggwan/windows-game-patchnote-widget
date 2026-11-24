using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PatchNoteMailbox.Models;

namespace PatchNoteMailbox.UI
{
    public class PopoverWindow : Window
    {
        private const int PageSize = 5;

        private static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
        private static Brush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private static readonly Color CText = C("#1F1E1C");
        private static readonly Color CLabel = C("#6B6962");
        private static readonly Color CMuted = C("#8A8780");
        private static readonly Color CSep = C("#EFEDE7");
        private static readonly Color CDanger = C("#E24B4A");
        private static readonly Color CBtn = C("#F4F3EF");
        private static readonly Color CBtnHover = C("#E9E7E0");
        private static readonly Color CRowHover = C("#F8F7F4");

        private readonly Action<string, string> _onMarkRead;
        private readonly Action<string> _onMarkAllRead;
        private readonly Action<string> _onRefreshGame;
        private readonly Func<string, string> _getIconPath;

        private GameConfig _game;
        private GameCacheEntry _cache;
        private int _page = 1;

        private Border _card;
        private StackPanel _contentStack;

        public string ActiveGameId { get { return _game != null ? _game.Id : null; } }

        public PopoverWindow(
            Action<string, string> onMarkRead,
            Action<string> onMarkAllRead,
            Action<string> onRefreshGame,
            Func<string, string> getIconPath)
        {
            _onMarkRead = onMarkRead;
            _onMarkAllRead = onMarkAllRead;
            _onRefreshGame = onRefreshGame;
            _getIconPath = getIconPath;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            Width = 452;
            SizeToContent = SizeToContent.Height;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic");
            UseLayoutRounding = true;

            _card = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(22),
                Margin = new Thickness(16),
                Padding = new Thickness(18, 15, 18, 13),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 24,
                    ShadowDepth = 4,
                    Direction = 270,
                    Opacity = 0.16,
                    Color = C("#1E283C")
                }
            };

            _contentStack = new StackPanel();
            _card.Child = _contentStack;
            Content = _card;

            SourceInitialized += OnSourceInitialized;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) Hide();
            };
        }

        public void ShowForGame(GameConfig game, GameCacheEntry cache, Window dockWindow)
        {
            bool changedGame = (_game == null || _game.Id != game.Id);
            _game = game;
            _cache = cache ?? new GameCacheEntry();
            if (changedGame) _page = 1;

            Render();
            if (!IsVisible) Show();
            UpdateLayout();
            PositionNearDock(dockWindow);
            Activate();
        }

        public void RefreshView(GameCacheEntry cache)
        {
            if (_game == null) return;
            _cache = cache ?? new GameCacheEntry();
            if (IsVisible) Render();
        }

        public FrameworkElement DetachCardForSnapshot(GameConfig game, GameCacheEntry cache)
        {
            _game = game;
            _cache = cache ?? new GameCacheEntry();
            _page = 1;
            Render();
            Content = null;
            _card.Width = 420;
            return _card;
        }

        private void PositionNearDock(Window dock)
        {
            Rect work = SystemParameters.WorkArea;
            double x = dock.Left - Width + 22;
            if (x < work.Left)
            {
                x = dock.Left + dock.ActualWidth - 22;
            }
            double y = dock.Top + dock.ActualHeight - ActualHeight;

            if (x < work.Left) x = work.Left;
            if (x + Width > work.Right) x = work.Right - Width;
            if (y < work.Top) y = work.Top;
            if (y + ActualHeight > work.Bottom) y = work.Bottom - ActualHeight;

            Left = x;
            Top = y;
        }

        private void Render()
        {
            _contentStack.Children.Clear();
            if (_game == null) return;

            var notes = _cache.Notes ?? new List<PatchNoteItem>();
            var readSet = new HashSet<string>(_cache.ReadIds ?? new List<string>());
            int unreadCount = 0;
            for (int i = 0; i < notes.Count; i++)
            {
                if (!readSet.Contains(notes[i].Idx)) unreadCount++;
            }

            // 1. Clean Header (Icon + Game Name + Actions only)
            var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            string iconPath = _getIconPath(_game.Id);
            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            {
                var iconBorder = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(6),
                    Margin = new Thickness(0, 0, 8, 0),
                    ClipToBounds = true
                };
                var img = new Image { Source = LoadBitmap(iconPath), Stretch = Stretch.UniformToFill };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                iconBorder.Child = img;
                leftPanel.Children.Add(iconBorder);
            }

            leftPanel.Children.Add(new TextBlock
            {
                Text = _game.Name,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = B(CText),
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(leftPanel, 0);
            headerGrid.Children.Add(leftPanel);

            var rightPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (unreadCount > 0)
            {
                var markAllBtn = CreatePillButton("모두 읽음", delegate { _onMarkAllRead(_game.Id); });
                markAllBtn.Margin = new Thickness(0, 0, 6, 0);
                rightPanel.Children.Add(markAllBtn);
            }

            var refreshBtn = CreateCircleButton("↻", "새로고침", delegate { _onRefreshGame(_game.Id); });
            refreshBtn.Margin = new Thickness(0, 0, 6, 0);
            rightPanel.Children.Add(refreshBtn);

            var closeBtn = CreateCircleButton("✕", "닫기", delegate { Hide(); });
            rightPanel.Children.Add(closeBtn);

            Grid.SetColumn(rightPanel, 1);
            headerGrid.Children.Add(rightPanel);
            _contentStack.Children.Add(headerGrid);

            _contentStack.Children.Add(new Border { Height = 1, Background = B(CSep) });

            // 2. Column Header
            var colGrid = new Grid { Margin = new Thickness(4, 8, 4, 6) };
            colGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            colGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            colGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            colGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });

            AddColLabel(colGrid, 1, "IDX", HorizontalAlignment.Left);
            AddColLabel(colGrid, 2, "제목", HorizontalAlignment.Left);
            AddColLabel(colGrid, 3, "등록일", HorizontalAlignment.Right);
            _contentStack.Children.Add(colGrid);

            // 3. 5-Item Paginated List
            int totalPages = Math.Max(1, (int)Math.Ceiling(notes.Count / (double)PageSize));
            if (_page > totalPages) _page = totalPages;
            if (_page < 1) _page = 1;

            int start = (_page - 1) * PageSize;
            int count = Math.Min(PageSize, Math.Max(0, notes.Count - start));

            var listStack = new StackPanel { MinHeight = 186 };
            if (count == 0)
            {
                listStack.Children.Add(new TextBlock
                {
                    Text = "표시할 패치노트가 없습니다.",
                    Foreground = B(CMuted),
                    FontSize = 12,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 72, 0, 0)
                });
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    var item = notes[start + i];
                    bool isRead = readSet.Contains(item.Idx);

                    var rowBorder = new Border
                    {
                        Padding = new Thickness(4, 9, 4, 9),
                        CornerRadius = new CornerRadius(8),
                        Background = Brushes.Transparent,
                        Cursor = Cursors.Hand,
                        ToolTip = item.Title
                    };

                    var rowGrid = new Grid();
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });

                    var dot = new Ellipse
                    {
                        Width = 6,
                        Height = 6,
                        Fill = isRead ? Brushes.Transparent : B(CDanger),
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    Grid.SetColumn(dot, 0);
                    rowGrid.Children.Add(dot);

                    var idxText = new TextBlock
                    {
                        Text = item.Idx,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11.5,
                        Foreground = B(CMuted),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };
                    Grid.SetColumn(idxText, 1);
                    rowGrid.Children.Add(idxText);

                    var titleText = new TextBlock
                    {
                        Text = item.Title,
                        FontSize = 12.5,
                        FontWeight = isRead ? FontWeights.Normal : FontWeights.SemiBold,
                        Foreground = isRead ? B(CMuted) : B(CText),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(0, 0, 8, 0)
                    };
                    Grid.SetColumn(titleText, 2);
                    rowGrid.Children.Add(titleText);

                    var dateText = new TextBlock
                    {
                        Text = item.Date,
                        FontSize = 11.5,
                        Foreground = B(CMuted),
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Right
                    };
                    Grid.SetColumn(dateText, 3);
                    rowGrid.Children.Add(dateText);

                    rowBorder.Child = rowGrid;

                    rowBorder.MouseEnter += delegate { rowBorder.Background = B(CRowHover); };
                    rowBorder.MouseLeave += delegate { rowBorder.Background = Brushes.Transparent; };

                    var capturedItem = item;
                    rowBorder.MouseLeftButtonUp += delegate
                    {
                        OpenInBrowser(capturedItem.Url);
                        _onMarkRead(_game.Id, capturedItem.Idx);
                    };

                    listStack.Children.Add(rowBorder);
                    if (i < count - 1)
                    {
                        listStack.Children.Add(new Border { Height = 1, Background = B(CSep), Margin = new Thickness(4, 0, 4, 0) });
                    }
                }
            }

            _contentStack.Children.Add(listStack);
            _contentStack.Children.Add(new Border { Height = 1, Background = B(CSep), Margin = new Thickness(0, 6, 0, 10) });

            // 4. Pagination Footer
            var footerGrid = new Grid();
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            footerGrid.Children.Add(new TextBlock
            {
                Text = string.Format("총 {0}건", notes.Count),
                FontSize = 11.5,
                Foreground = B(CMuted),
                VerticalAlignment = VerticalAlignment.Center
            });

            var pager = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            pager.Children.Add(CreatePageButton("이전", _page > 1, delegate
            {
                if (_page > 1) { _page--; Render(); }
            }));
            pager.Children.Add(new TextBlock
            {
                Text = string.Format("{0} / {1}", _page, totalPages),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = B(CText),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 10, 0)
            });
            pager.Children.Add(CreatePageButton("다음", _page < totalPages, delegate
            {
                if (_page < totalPages) { _page++; Render(); }
            }));

            Grid.SetColumn(pager, 1);
            footerGrid.Children.Add(pager);
            _contentStack.Children.Add(footerGrid);
        }

        private static void AddColLabel(Grid g, int col, string text, HorizontalAlignment align)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = B(CMuted),
                HorizontalAlignment = align
            };
            Grid.SetColumn(tb, col);
            g.Children.Add(tb);
        }

        private static Border CreateCircleButton(string glyph, string tooltip, Action onClick)
        {
            var b = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = B(CBtn),
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
                Child = new TextBlock
                {
                    Text = glyph,
                    FontSize = 11.5,
                    Foreground = B(CLabel),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            b.MouseEnter += delegate { b.Background = B(CBtnHover); };
            b.MouseLeave += delegate { b.Background = B(CBtn); };
            b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; onClick(); };
            return b;
        }

        private static Border CreatePillButton(string text, Action onClick)
        {
            var b = new Border
            {
                Height = 24,
                Padding = new Thickness(9, 0, 9, 0),
                CornerRadius = new CornerRadius(12),
                Background = B(CBtn),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 11,
                    Foreground = B(CLabel),
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            b.MouseEnter += delegate { b.Background = B(CBtnHover); };
            b.MouseLeave += delegate { b.Background = B(CBtn); };
            b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; onClick(); };
            return b;
        }

        private static Border CreatePageButton(string text, bool enabled, Action onClick)
        {
            var b = new Border
            {
                Padding = new Thickness(9, 4, 9, 4),
                CornerRadius = new CornerRadius(8),
                Background = B(CBtn),
                Opacity = enabled ? 1.0 : 0.4,
                Cursor = enabled ? Cursors.Hand : Cursors.Arrow,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 11,
                    Foreground = B(CLabel)
                }
            };
            if (enabled)
            {
                b.MouseEnter += delegate { b.Background = B(CBtnHover); };
                b.MouseLeave += delegate { b.Background = B(CBtn); };
                b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; onClick(); };
            }
            return b;
        }

        private static void OpenInBrowser(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
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
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int idx, int v);
    }
}
