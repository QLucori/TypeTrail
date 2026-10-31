using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace TypeTrail {
    internal static class Theme {
        // Shared resources keep templates, popups and selected rows in the same palette.
        static readonly string[,] Colors = {
            { "ThemeText", "#202333", "#EFEDF7" },
            { "ThemeMuted", "#9290A6", "#ADA9BF" },
            { "ThemeCardText", "#444057", "#DED9EB" },
            { "ThemeSurface", "#FFFFFF", "#23232E" },
            { "ThemeCanvas", "#FBFBFE", "#18181F" },
            { "ThemeBorder", "#E5E5EE", "#3B374C" },
            { "ThemeScrollTrack", "#F5F4FA", "#1C1C25" },
            { "ThemeScrollThumb", "#C6C1D3", "#514B63" },
            { "ThemeScrollHover", "#A69BBB", "#716581" },
            { "ThemeScrollActive", "#76659F", "#AA96DD" },
            { "ThemeButton", "#F0F1F6", "#32323F" },
            { "ThemeButtonText", "#53576B", "#E1DDEB" },
            { "ThemeAccentText", "#76659F", "#B6A5FF" },
            { "ThemeFocus", "#9C8CEF", "#B6A5FF" },
            { "ThemeSelection", "#B2A5F4", "#6D5AAB" },
            { "ThemeHover", "#F0EFF8", "#2A2937" },
            { "ThemeSelected", "#EFEBFC", "#353047" },
            { "ThemeSelectedBorder", "#DDD5F6", "#6B5B97" },
            { "ThemeSoftAccent", "#F0EDFB", "#302B44" },
            { "ThemePositive", "#EAF5EE", "#22372E" },
            { "ThemePositiveText", "#3C8560", "#8FD5AC" },
            { "ThemePositiveBorder", "#D4E8DA", "#385B47" },
            { "ThemeDanger", "#F8EBEE", "#3E2934" },
            { "ThemeDangerText", "#B46A79", "#F2A7B6" },
            { "ThemeWarning", "#F4EFE4", "#3A3025" },
            { "ThemeWarningText", "#9B7E49", "#E6C38A" },
            { "Accent", "#6854DF", "#7B61D4" }
        };
        static bool scrollBarsRegistered;
        static void ApplyScrollBar(ScrollBar bar) {
            var style = bar.TryFindResource("AppScrollBarStyle") as Style;
            // Theme control templates can set a local ScrollBar style, which
            // takes precedence over the window's implicit type style.
            if (style != null && bar.Style != style) bar.Style = style;
        }
        static void ApplyScrollBars(DependencyObject root) {
            var bar = root as ScrollBar;
            if (bar != null) ApplyScrollBar(bar);
            for (int i=0; i<VisualTreeHelper.GetChildrenCount(root); i++)
                ApplyScrollBars(VisualTreeHelper.GetChild(root,i));
        }
        public static void Apply(Window window, bool dark) {
            if (!scrollBarsRegistered) {
                EventManager.RegisterClassHandler(typeof(ScrollBar), FrameworkElement.LoadedEvent,
                    new RoutedEventHandler(delegate(object sender,RoutedEventArgs e) { ApplyScrollBar((ScrollBar)sender); }));
                scrollBarsRegistered = true;
            }
            for (int i = 0; i < Colors.GetLength(0); i++) {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Colors[i, dark ? 2 : 1]));
                brush.Freeze(); window.Resources[Colors[i, 0]] = brush;
            }
            ApplyScrollBars(window);
        }
        public static void ApplyTray(Forms.ContextMenuStrip menu, bool dark) {
            menu.Renderer = new Forms.ToolStripProfessionalRenderer(new MenuColors(dark));
            menu.ForeColor = Drawing.ColorTranslator.FromHtml(dark ? "#EFEDF7" : "#202333");
            menu.BackColor = Drawing.ColorTranslator.FromHtml(dark ? "#23232E" : "#FFFFFF");
        }
        sealed class MenuColors : Forms.ProfessionalColorTable {
            readonly bool dark;
            public MenuColors(bool value) { dark = value; UseSystemColors = false; }
            Drawing.Color Surface { get { return Drawing.ColorTranslator.FromHtml(dark ? "#23232E" : "#FFFFFF"); } }
            public override Drawing.Color ToolStripDropDownBackground { get { return Surface; } }
            public override Drawing.Color ImageMarginGradientBegin { get { return Surface; } }
            public override Drawing.Color ImageMarginGradientMiddle { get { return Surface; } }
            public override Drawing.Color ImageMarginGradientEnd { get { return Surface; } }
            public override Drawing.Color MenuBorder { get { return Drawing.ColorTranslator.FromHtml(dark ? "#3B374C" : "#E5E5EE"); } }
            public override Drawing.Color MenuItemSelected { get { return Drawing.ColorTranslator.FromHtml(dark ? "#353047" : "#EFEBFC"); } }
            public override Drawing.Color MenuItemBorder { get { return MenuBorder; } }
        }
    }
}
