// BWUI — the widget window: card, desktop layer, placement, menu, theme. See DESIGN.md §4, §6.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Bwui
{
    public abstract class BwuiWindow : Window
    {
        public readonly string AppId;
        public readonly Settings Settings;
        protected readonly Border Card;
        bool placed;
        WidgetSize size = WidgetSize.Regular;

        /// <summary>Edges that stay put when the content changes size (DESIGN.md §6.2). Default: bottom-right.</summary>
        protected bool AnchorRight = true, AnchorBottom = true;

        protected BwuiWindow(string appId, string title)
        {
            AppId = appId;
            Title = title;
            Settings = new Settings(appId);

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            FontFamily = new FontFamily(Tokens.FontFamily);
            UseLayoutRounding = true;
            Resources.MergedDictionaries.Add(Theme.Resources);

            Theme.Mode = Theme.Parse(Settings.Get("theme", "system"));
            size = ParseSize(Settings.Get("size"));
            if (Array.IndexOf(SupportedSizes, size) < 0) size = SupportedSizes[0];

            Card = new Border();
            Content = Card;
            StyleCard();

            if (App.IsSnapshot) return;

            Left = Settings.GetDouble("left") ?? -10000;
            Top = Settings.GetDouble("top") ?? -10000;
            Card.MouseLeftButtonDown += (s, e) =>
            {
                try { DragMove(); } catch { }
                SavePosition();
            };
            SizeChanged += OnSizeChanged;
            try { new WindowInteropHelper(this).Owner = FindWindow("Progman", null); } catch { }
            SourceInitialized += OnSourceInitialized;
            Loaded += OnLoaded;
            ContextMenuOpening += (s, e) => { ContextMenu = BuildMenu(); };
            ContextMenu = new ContextMenu();
            EventHandler onTheme = (s, e) => { StyleCard(); Render(); };
            Theme.Changed += onTheme;
            Closed += (s, e) => Theme.Changed -= onTheme;
        }

        // ---------- for widgets ----------

        /// <summary>Builds the card content. Called on every Render, so build from current state and theme.</summary>
        protected abstract UIElement Build();

        /// <summary>Widget menu items, shown first (DESIGN.md §6.3).</summary>
        protected virtual void AddMenuItems(ContextMenu menu) { }

        /// <summary>Override to fetch now; the presence of an override shows "Refresh now".</summary>
        protected virtual bool CanRefresh { get { return false; } }
        protected virtual void RefreshNow() { }

        /// <summary>Sizes this widget supports, first is the default (DESIGN.md §3.3).</summary>
        protected virtual WidgetSize[] SupportedSizes { get { return new[] { WidgetSize.Regular }; } }

        public WidgetSize CurrentSize { get { return size; } }
        public double ContentWidth { get { return Tokens.ContentWidth(size); } }

        /// <summary>Override to update from GitHub Releases (DESIGN.md §6.10). Null: no updates.</summary>
        protected virtual UpdateSource Updates { get { return null; } }

        Updater updater;
        DispatcherTimer upToDateTimer;

        public void Render()
        {
            var c = Build();
            var fe = c as FrameworkElement;
            if (fe != null && double.IsNaN(fe.Width)) fe.Width = ContentWidth;
            var bar = UpdateBar(fe != null && !double.IsNaN(fe.Width) ? fe.Width : ContentWidth);
            if (bar == null) { Card.Child = c; return; }
            var sp = new StackPanel();
            sp.Children.Add(c);
            sp.Children.Add(Ui.Divider());
            sp.Children.Add(bar);
            Card.Child = sp;
        }

        // ---------- updates (DESIGN.md §6.10) ----------

        void StartUpdater()
        {
            var src = Updates;
            if (src == null) return;
            updater = new Updater(AppId, src, Settings, Dispatcher);
            updater.Changed += (s, e) =>
            {
                Render();
                if (updater.State == UpdateState.UpToDate)
                {
                    // "You're up to date" shows for a few seconds after a manual check.
                    if (upToDateTimer != null) upToDateTimer.Stop();
                    upToDateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                    upToDateTimer.Tick += (s2, e2) => { upToDateTimer.Stop(); if (updater.State == UpdateState.UpToDate) Render(); };
                    upToDateTimer.Start();
                }
            };
            if (updater.IsInstalledCopy) updater.Start();
            Closed += (s, e) => updater.Stop();
        }

        /// <summary>The update bar under the content, or null when there is nothing to say.</summary>
        FrameworkElement UpdateBar(double width)
        {
            UpdateState state;
            string tag, error = null;
            if (App.IsSnapshot)
            {
                if (Array.IndexOf(App.SnapshotArgs ?? new string[0], "update") < 0) return null;
                state = UpdateState.Available; tag = "v9.9.9";
            }
            else
            {
                if (updater == null) return null;
                state = updater.State; tag = updater.LatestTag; error = updater.Error;
                if (state == UpdateState.UpToDate && (upToDateTimer == null || !upToDateTimer.IsEnabled)) return null;
                if (state == UpdateState.Idle || state == UpdateState.Checking) return null;
            }
            Action install = () => { if (updater != null) updater.Install(); };

            if (width < 160)
            {
                // Narrow widgets get a single round button.
                if (state != UpdateState.Available && state != UpdateState.Installing) return null;
                var b = Ui.IconButton(Glyphs.Download, state == UpdateState.Installing ? Text.Updating : Text.InstallUpdate(tag), install);
                b.HorizontalAlignment = HorizontalAlignment.Center;
                if (state == UpdateState.Installing) { b.IsEnabled = false; b.Opacity = 0.45; }
                return b;
            }

            string msg;
            Border action = null;
            switch (state)
            {
                case UpdateState.Available: msg = Text.UpdateAvailable(tag); action = Ui.TextButton(Text.Update, install); break;
                case UpdateState.Installing: msg = Text.Updating; break;
                case UpdateState.UpToDate: msg = Text.UpToDate + " (v" + AppInfo.Version + ")"; break;
                default:
                    msg = Text.UpdateFailed + (string.IsNullOrEmpty(error) ? "" : ": " + error);
                    action = Ui.TextButton(Text.Retry, () => { if (tag != null && Updater.ParseVersion(tag) > Updater.Current) updater.Install(); else updater.Check(true); });
                    break;
            }
            var row = new DockPanel { LastChildFill = true };
            if (action != null)
            {
                action.Margin = new Thickness(Tokens.Space2, 0, 0, 0);
                action.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(action, Dock.Right);
                row.Children.Add(action);
            }
            var t = Ui.Text(msg, TextStyle.Caption, state == UpdateState.Failed ? Theme.Current.TextSecondary : Theme.Current.TextTertiary);
            t.TextWrapping = TextWrapping.Wrap;
            t.TextTrimming = TextTrimming.None;
            t.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(t);
            return row;
        }

        public static MenuItem Item(string header, Action onClick)
        {
            var m = new MenuItem { Header = header };
            m.Click += (s, e) => onClick();
            return m;
        }

        public static MenuItem Check(string header, bool isChecked, Action<bool> onChange)
        {
            var m = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
            m.Click += (s, e) => onChange(m.IsChecked);
            return m;
        }

        // ---------- card & theme ----------

        void StyleCard() { Ui.StyleCard(Card); }

        // ---------- menu ----------

        ContextMenu BuildMenu()
        {
            var m = new ContextMenu();
            m.Resources.MergedDictionaries.Add(Theme.Resources);
            AddMenuItems(m);
            if (m.Items.Count > 0) m.Items.Add(new Separator());
            if (CanRefresh) m.Items.Add(Item(Text.Refresh, RefreshNow));
            if (SupportedSizes.Length > 1)
                m.Items.Add(Item(Text.SizeItem(size), () =>
                {
                    int i = Array.IndexOf(SupportedSizes, size);
                    size = SupportedSizes[(i + 1) % SupportedSizes.Length];
                    Settings.Set("size", size.ToString().ToLowerInvariant());
                    Render();
                }));
            m.Items.Add(Item(Text.ThemeItem(Theme.Mode), () =>
            {
                var next = Theme.Mode == ThemeMode.System ? ThemeMode.Light : Theme.Mode == ThemeMode.Light ? ThemeMode.Dark : ThemeMode.System;
                Settings.Set("theme", Theme.ToKey(next));
                Theme.Mode = next;
            }));
            m.Items.Add(Check(Text.StartWithWindows, Startup.IsEnabled(AppId), on => Startup.Set(AppId, on)));
            if (updater != null && updater.IsInstalledCopy)
            {
                m.Items.Add(new Separator());
                if (updater.State == UpdateState.Available) m.Items.Add(Item(Text.InstallUpdate(updater.LatestTag), updater.Install));
                else
                {
                    var c = Item(Text.CheckForUpdates, () => updater.Check(true));
                    c.IsEnabled = updater.State != UpdateState.Checking && updater.State != UpdateState.Installing;
                    m.Items.Add(c);
                }
                m.Items.Add(Check(Text.AutoUpdate, updater.Auto, on => updater.Auto = on));
            }
            m.Items.Add(new Separator());
            m.Items.Add(Item(Text.Exit, Close));
            return m;
        }

        static WidgetSize ParseSize(string s)
        {
            if (s == "compact") return WidgetSize.Compact;
            if (s == "wide") return WidgetSize.Wide;
            return WidgetSize.Regular;
        }

        // ---------- placement (DESIGN.md §6.2) ----------

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            double? l = Settings.GetDouble("left"), t = Settings.GetDouble("top");
            if (!(l.HasValue && t.HasValue && vs.Contains(new Point(l.Value + 40, t.Value + 40))))
            {
                var wa = SystemParameters.WorkArea;
                Left = wa.Right - ActualWidth;
                Top = wa.Bottom - ActualHeight;
            }
            placed = true;
            SavePosition();
            StartUpdater();
            if (App.DebugMenu)
            {
                // --debug-menu: open the menu, save it as <data dir>\debug-menu.png, then exit (visual checks).
                var m = BuildMenu();
                ContextMenu = m;
                m.PlacementTarget = this;
                m.Placement = System.Windows.Controls.Primitives.PlacementMode.Left;
                m.Opened += (s2, e2) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var rtb = new RenderTargetBitmap((int)Math.Ceiling(m.ActualWidth * 2), (int)Math.Ceiling(m.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                        rtb.Render(m);
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(rtb));
                        using (var fs = File.Create(Path.Combine(Settings.Dir, "debug-menu.png"))) enc.Save(fs);
                        var pop = m.Parent as System.Windows.Controls.Primitives.Popup;
                        File.WriteAllText(Path.Combine(Settings.Dir, "debug-menu.txt"), "popup transparent: " + (pop == null ? "?" : pop.AllowsTransparency.ToString()));
                    }
                    catch (Exception ex) { File.WriteAllText(Path.Combine(Settings.Dir, "debug-menu.txt"), ex.ToString()); }
                    Close();
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Dispatcher.BeginInvoke(new Action(() => m.IsOpen = true), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }

        void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!placed || e.PreviousSize.Width <= 0 || e.PreviousSize.Height <= 0) return;
            if (AnchorRight && e.WidthChanged) Left -= e.NewSize.Width - e.PreviousSize.Width;
            if (AnchorBottom && e.HeightChanged) Top -= e.NewSize.Height - e.PreviousSize.Height;
            SavePosition();
        }

        protected void SavePosition()
        {
            if (!placed) return;
            Settings.SetDouble("left", Math.Round(Left, 1));
            Settings.SetDouble("top", Math.Round(Top, 1));
        }

        // ---------- snapshot (DESIGN.md §6.9) ----------

        public void SaveSnapshot(string file)
        {
            StyleCard();
            Render();
            Content = null;
            var desk = new Border { Background = Ui.Br(Theme.Current.Desk), Padding = new Thickness(4), Child = Card };
            desk.Resources.MergedDictionaries.Add(Theme.Resources);
            TextOptions.SetTextFormattingMode(desk, TextFormattingMode.Ideal);
            desk.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            desk.Arrange(new Rect(desk.DesiredSize));
            desk.UpdateLayout();
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(desk.ActualWidth * 2), (int)Math.Ceiling(desk.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            rtb.Render(desk);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(file)) enc.Save(fs);
        }

        // ---------- desktop layer (DESIGN.md §6.1) ----------

        void OnSourceInitialized(object sender, EventArgs e)
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
            var src = HwndSource.FromHwnd(h);
            if (src != null) src.AddHook(Hook);
            SetWindowPos(h, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        }

        static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_WINDOWPOSCHANGING && !App.DebugMenu)
            {
                var wp = (WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(WINDOWPOS));
                wp.hwndInsertAfter = HWND_BOTTOM;
                wp.flags &= ~SWP_NOZORDER;
                Marshal.StructureToPtr(wp, lParam, false);
            }
            return IntPtr.Zero;
        }

        const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WM_WINDOWPOSCHANGING = 0x46;
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
        static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int idx, int v);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
