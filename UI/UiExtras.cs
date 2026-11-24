// Components this widget needs that BWUI 0.1.0 doesn't have yet. Built only from BWUI tokens and
// Ui helpers, so each one can move into BWUI/ as is (DESIGN.md §4: "If a component is reused by a
// second widget, it moves into BWUI/").
using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Bwui;

namespace PatchNoteMailbox.UI
{
    public static class UiX
    {
        // Fluent glyphs that Bwui.Glyphs doesn't list.
        public const string ChevronUp = "", ChevronDown = "", ChevronLeft = "", ChevronRight = "",
            Close = "";

        /// <summary>
        /// The BWUI card look for a window that isn't a BwuiWindow (a popover): Surface, CardRadius,
        /// CardPadding, ShadowRoom margin, the one card shadow, and a 1px border in High Contrast.
        /// Same as BwuiWindow's private StyleCard; call it again on Theme.Changed.
        /// </summary>
        public static void StyleCard(Border card)
        {
            var p = Theme.Current;
            card.CornerRadius = new CornerRadius(Tokens.CardRadius);
            card.Padding = new Thickness(Tokens.CardPadding);
            card.Margin = new Thickness(Tokens.ShadowRoom);
            card.Background = Ui.Br(p.Surface);
            card.BorderBrush = Ui.Br(p.Border);
            card.BorderThickness = new Thickness(p.IsHighContrast ? 1 : 0);
            card.Effect = p.ShadowOpacity <= 0 ? null : new DropShadowEffect
            {
                BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = p.ShadowOpacity, Color = p.Shadow
            };
        }

        /// <summary>
        /// Like Ui.Icon(null, accent, glyph), but the glyph is white only where white reaches 3:1 on the
        /// accent (C2). Light dark-theme accents get a Surface-colored glyph instead.
        /// </summary>
        public static FrameworkElement AccentIcon(Color accent, string glyph)
        {
            var p = Theme.Current;
            var b = (Border)Ui.Icon(null, accent, null);
            Color fg = p.IsHighContrast ? SystemColors.HighlightTextColor
                : Palette.Contrast(Colors.White, accent) >= 3 ? Colors.White : p.Surface;
            b.Child = Ui.Glyph(glyph, 12, fg);
            return b;
        }

        /// <summary>
        /// Count badge (20px pill) for the top-right corner of a tile: accent fill, the count in
        /// SemiBold Caption, and a 2px Surface ring that cuts it out of the tile. The number itself
        /// is the cue, so the badge never relies on color alone (C3).
        /// </summary>
        public static Border CountBadge(int count, Color accent)
        {
            var p = Theme.Current;
            var fg = p.IsHighContrast ? SystemColors.HighlightTextColor : p.Surface;
            var t = Ui.Text(count > 99 ? "99+" : count.ToString(), TextStyle.Caption, fg);
            t.FontWeight = FontWeights.SemiBold;
            t.TextTrimming = TextTrimming.None;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            return new Border
            {
                MinWidth = 20, Height = 20,
                Padding = new Thickness(Tokens.Space1, 0, Tokens.Space1, 0),
                CornerRadius = new CornerRadius(10),
                Background = Ui.Br(accent),
                BorderBrush = Ui.Br(p.Surface),
                BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Child = t
            };
        }

        /// <summary>
        /// Small status mark for the bottom-right corner of a tile: a Surface disc with a 12px
        /// status glyph (C3: the glyph carries the meaning, not the color).
        /// </summary>
        public static Border StatusMark(Status status)
        {
            var p = Theme.Current;
            var c = p.StatusColor(status, p.TextSecondary);
            return new Border
            {
                Width = Tokens.Space4 + Tokens.Space1, Height = Tokens.Space4 + Tokens.Space1,
                CornerRadius = new CornerRadius((Tokens.Space4 + Tokens.Space1) / 2),
                Background = Ui.Br(p.Surface),
                BorderBrush = Ui.Br(p.Border),
                BorderThickness = new Thickness(p.IsHighContrast ? 1 : 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false,
                Child = Ui.Glyph(status == Status.Critical ? Glyphs.Error : Glyphs.Warning, Tokens.StatusGlyphSize, c)
            };
        }

        /// <summary>TextButton with a disabled state: dimmed like a disabled menu item (45%) and not clickable.</summary>
        public static Border TextButton(string text, bool enabled, Action onClick)
        {
            var b = Ui.TextButton(text, onClick);
            if (!enabled)
            {
                b.IsEnabled = false;
                b.Opacity = 0.45;
                b.Cursor = null;
            }
            return b;
        }

        /// <summary>Clickable list row: transparent, SurfaceSubtle on hover, InnerRadius corners.</summary>
        public static Border ListRow(UIElement content, double height, string accessibleName, Action onClick)
        {
            var row = new Border
            {
                Height = height,
                Padding = new Thickness(Tokens.Space2, 0, Tokens.Space2, 0),
                CornerRadius = new CornerRadius(Tokens.InnerRadius),
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = content
            };
            AutomationProperties.SetName(row, accessibleName);
            row.MouseEnter += delegate { row.Background = Ui.Br(Theme.Current.SurfaceSubtle); };
            row.MouseLeave += delegate { row.Background = Brushes.Transparent; };
            row.MouseLeftButtonDown += delegate(object s, System.Windows.Input.MouseButtonEventArgs e) { e.Handled = true; };
            row.MouseLeftButtonUp += delegate(object s, System.Windows.Input.MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (onClick != null) onClick();
            };
            return row;
        }
    }
}
