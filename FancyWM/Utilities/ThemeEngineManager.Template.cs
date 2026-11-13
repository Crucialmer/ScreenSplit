using System;
using System.Windows.Media;

namespace FancyWM.Utilities
{
    internal static partial class ThemeEngineManager
    {
        private const string HtmlTemplate = @"
<panel>
    <panel-bar>
        <panel-bar-header>
            <panel-bar-handle></panel-bar-handle>
            <panel-bar-button></panel-bar-button>
        </panel-bar-header>
        <panel-bar-tab></panel-bar-tab>
    </panel-bar>
    <window>
        <window-actions></window-actions>
    </window>
    <window class=""preview""></window>
</panel>
<panel class=""preview""></panel>
";
        private static string GetDefaultCss(Func<string, object> R)
        {
            var accent = (Color)R("SystemAccentColor");
            var accentLight1 = (Color)R("SystemAccentColorLight1");
            var accentLight2 = (Color)R("SystemAccentColorLight2");
            var accentLight3 = (Color)R("SystemAccentColorLight3");
            var accentDark1 = (Color)R("SystemAccentColorDark1");
            var accentDark2 = (Color)R("SystemAccentColorDark2");
            var accentDark3 = (Color)R("SystemAccentColorDark3");
            var isDarkTheme = ModernWpf.ThemeManager.Current.ActualApplicationTheme == ModernWpf.ApplicationTheme.Dark;
            var isRounded = Environment.OSVersion.Version.Build >= 22000;

            var previewBorderColor = accentLight2;
            var previewRectangleFill = accentLight1.WithOpacity(0.26);
            var panelBarBackground = isDarkTheme ? Color.FromRgb(0x1F, 0x1F, 0x1F) : Color.FromRgb(0xCD, 0xCD, 0xCD);
            var panelBarButtonBackground = isDarkTheme ? accentLight2 : accentDark1;
            var panelBarBorder = isDarkTheme ? Color.FromArgb(0x19, 0, 0, 0) : Color.FromArgb(0x19, 255, 255, 255);
            var panelBarButtonText = isDarkTheme ? Colors.Black : Colors.White;
            var panelBarTabText = isDarkTheme ? Colors.White : Colors.Black;
            var controlBorderRadius = isRounded ? 10 : 0;
            var overlayBorderRadius = isRounded ? 14 : 0;
            var panelBarButtonBackgroundHover = accentLight1;
            var panelBarButtonBackgroundActive = accentDark1;

            var css = @$"
/* Generated file _default.css - ANY CHANGES WILL NOT BE SAVED */
window:focus {{
    border-color: {previewBorderColor.ToCss()};
    border-width: 2.5px;
    border-radius: {overlayBorderRadius}px;
    filter: drop-shadow(0px 0px 8px {previewBorderColor.WithOpacity(0.5).ToCss()});
}}
window.preview, panel.preview {{
    border-color: {previewBorderColor.ToCss()};
    border-width: 2.5px;
    border-radius: {overlayBorderRadius}px;
    background-color: {previewRectangleFill.ToCss()};
    filter: drop-shadow(0px 0px 10px {previewBorderColor.WithOpacity(0.5).ToCss()});
}}
panel-bar {{
    border-color: {panelBarBorder.ToCss()};
    border-width: 0.5px;
    border-radius: {controlBorderRadius}px; 
    background-color: {panelBarBackground.ToCss()};
    filter: drop-shadow(0px 2px 2px rgba(0,0,0,0.2));
}}
panel-bar-tab {{
    color: {panelBarTabText.ToCss()};
}}
panel-bar-handle, panel-bar-button {{
    color: {panelBarButtonText.ToCss()};
    background-color: {panelBarButtonBackground.ToCss()};
    border-radius: {controlBorderRadius}px;
}}
panel-bar-button:hover {{
    background-color: {panelBarButtonBackgroundHover.ToCss()};
}}
panel-bar-button:active {{
    background-color: {panelBarButtonBackgroundActive.ToCss()};
}}
";
            return css.Trim();
        }
    }
}
