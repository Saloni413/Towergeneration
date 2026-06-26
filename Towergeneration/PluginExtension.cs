using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Forms.Integration;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;

using WpfButton      = System.Windows.Controls.Button;
using WpfControl     = System.Windows.Controls.Control;
using WpfBrushes     = System.Windows.Media.Brushes;
using WpfColor       = System.Windows.Media.Color;
using WpfFontFamily  = System.Windows.Media.FontFamily;
using WpfHAlign      = System.Windows.HorizontalAlignment;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfBrush       = System.Windows.Media.Brush;
using DrawSize       = System.Drawing.Size;
using AcadApp        = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Towergeneration
{
    public class PluginExtension : IExtensionApplication
    {
        private static PaletteSet? _palette;

        // ── Colour palette ────────────────────────────────────────────────────────
        // Aligned with Advance Steel's dark-charcoal theme with steel-blue accents.

        static readonly WpfColor C_BG          = WpfColor.FromRgb(0x1E, 0x1E, 0x1E); // panel background
        static readonly WpfColor C_CARD        = WpfColor.FromRgb(0x27, 0x27, 0x2A); // card / header bg
        static readonly WpfColor C_HEADER_TOP  = WpfColor.FromRgb(0x00, 0x66, 0xB2); // accent stripe at top
        static readonly WpfColor C_STEP_BG     = WpfColor.FromRgb(0x1A, 0x38, 0x5C); // step-header band
        static readonly WpfColor C_STEP_BAR    = WpfColor.FromRgb(0x2E, 0x8B, 0xE8); // left accent bar on step headers
        static readonly WpfColor C_STEP_TEXT   = WpfColor.FromRgb(0xD4, 0xE8, 0xFF); // step-header label
        static readonly WpfColor C_SEP         = WpfColor.FromRgb(0x3A, 0x3A, 0x3D); // separator line
        static readonly WpfColor C_BTN_ACTIVE  = WpfColor.FromArgb(0x99, 0x1B, 0x5E, 0x9C); // active button normal (~60 % opacity)
        static readonly WpfColor C_BTN_HOVER   = WpfColor.FromArgb(0xBB, 0x25, 0x7A, 0xC5); // active button hover (~73 % opacity)
        static readonly WpfColor C_BTN_PRESS   = WpfColor.FromArgb(0xDD, 0x0F, 0x4A, 0x7C); // active button pressed (~87 % opacity)
        static readonly WpfColor C_BTN_DIS_BG  = WpfColor.FromRgb(0x2E, 0x2E, 0x32); // disabled btn bg
        static readonly WpfColor C_BTN_DIS_FG  = WpfColor.FromRgb(0x96, 0xA8, 0xBE); // disabled btn text — clearly readable
        static readonly WpfColor C_FOOTER_BG   = WpfColor.FromRgb(0x14, 0x14, 0x17); // footer band
        static readonly WpfColor C_FOOTER_TEXT = WpfColor.FromRgb(0x88, 0x9A, 0xAD); // footer label

        public void Initialize()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            doc?.Editor.WriteMessage(
                "\nAdvance TowerGen plugin loaded." +
                "\n  GENERATEFROMJSON      — build tower from JSON" +
                "\n  GENERATEBOM           — generate BOM PDF" +
                "\n  GENERATESHOPSKETCHES  — generate shop sketches DWG" +
                "\n  SAVE*VIEWDWG          — export 2D view DWGs");

            if (_palette == null)
            {
                try   { _palette = BuildCommandPalette(); }
                catch (System.Exception ex)
                { doc?.Editor.WriteMessage("\nWarning: could not create command panel: " + ex.Message); }
            }
        }

        public void Terminate()
        {
            try { _palette?.Dispose(); } catch { }
            _palette = null;
        }

        // ── PaletteSet ───────────────────────────────────────────────────────────

        private static PaletteSet BuildCommandPalette()
        {
            var ps = new PaletteSet(
                "ADVANCE TOWERGEN",
                new Guid("7E4A3B2C-1D5F-4E8A-9B6C-0F1A2B3C4D5E"))
            {
                Style = PaletteSetStyles.ShowAutoHideButton
                      | PaletteSetStyles.ShowCloseButton
                      | PaletteSetStyles.Snappable,
                MinimumSize = new DrawSize(230, 540),
                DockEnabled = DockSides.Left | DockSides.Right,
                Dock        = DockSides.Right,
            };

            var host = new ElementHost
            {
                Child     = BuildWpfPanel(),
                Dock      = System.Windows.Forms.DockStyle.Fill,
                BackColor = System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E)
            };

            ps.Add("Commands", host);
            ps.Visible = true;
            return ps;
        }

        // ── WPF content ──────────────────────────────────────────────────────────

        private static FrameworkElement BuildWpfPanel()
        {
            var bgBrush = new SolidColorBrush(C_BG);
            bgBrush.Freeze();

            var btnStyle         = MakeButtonStyle(stretch: true);
            var btnStyleDisabled = MakeButtonStyle(stretch: true, disabled: true);

            // ── Fixed header ──────────────────────────────────────────────────────
            var headerPanel = new StackPanel { Background = bgBrush };
            headerPanel.Children.Add(BuildHeader());
            headerPanel.Children.Add(MakeSeparator());

            // ── Scrollable body (steps only) ──────────────────────────────────────
            var bodyStack = new StackPanel { Background = bgBrush };

            // Step 1
            bodyStack.Children.Add(MakeStepHeader("1", "Input File Selection"));
            bodyStack.Children.Add(MakeBrowserButton("Browse Input File", "http://localhost:5010", btnStyle));
            bodyStack.Children.Add(MakeStepSpacer());

            // Step 2
            bodyStack.Children.Add(MakeStepHeader("2", "Data Extraction"));
            bodyStack.Children.Add(MakeBrowserButton("Extract Bill of Materials (BOM)", "http://localhost:5010/Home/Result", btnStyle));
            bodyStack.Children.Add(MakeBrowserButton("Extract Member Geometry Data", "http://localhost:5010/Home/Result", btnStyle));
            bodyStack.Children.Add(MakeStepSpacer());

            // Step 3
            bodyStack.Children.Add(MakeStepHeader("3", "Data Validation & Transformation"));
            bodyStack.Children.Add(MakeNoopButton("Validate Extracted Data", btnStyleDisabled));
            bodyStack.Children.Add(MakeNoopButton("Add Missing Components", btnStyleDisabled));
            bodyStack.Children.Add(MakeNoopButton("Generate Intermediate JSON File", btnStyleDisabled));
            bodyStack.Children.Add(MakeStepSpacer());

            // Step 4
            bodyStack.Children.Add(MakeStepHeader("4", "Model Generation"));
            bodyStack.Children.Add(MakeNoopButton("Import JSON Data", btnStyleDisabled));
            bodyStack.Children.Add(MakeCommandButton("Generate Structural Members", "GENERATEFROMJSON", btnStyle));
            bodyStack.Children.Add(MakeStepSpacer());

            // Step 5
            bodyStack.Children.Add(MakeStepHeader("5", "Output Generation & Export"));
            bodyStack.Children.Add(MakeCommandButton("Extract Final BOM", "GENERATEBOM", btnStyle));
            bodyStack.Children.Add(BuildViewDropdown(btnStyle));
            bodyStack.Children.Add(MakeCommandButton("Generate Shop Drawings", "GENERATESHOPSKETCHES", btnStyle));
            bodyStack.Children.Add(MakeStepSpacer());

            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = bgBrush,
                Content    = bodyStack
            };

            // ── Fixed footer ──────────────────────────────────────────────────────
            var footer = BuildFooter();

            // ── Grid: row 0 = header (fixed), row 1 = scroll body, row 2 = footer (fixed)
            var grid = new Grid { Background = bgBrush };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid.SetRow(headerPanel, 0);
            Grid.SetRow(scrollViewer, 1);
            Grid.SetRow(footer, 2);

            grid.Children.Add(headerPanel);
            grid.Children.Add(scrollViewer);
            grid.Children.Add(footer);

            return grid;
        }

        // ── Header (logo + title) ─────────────────────────────────────────────────

        private static UIElement BuildHeader()
        {
            // Thin accent stripe at top
            var accentStripe = new Border
            {
                Height     = 3,
                Background = new SolidColorBrush(C_HEADER_TOP)
            };

            // Logo + title row
            var logoRow = new StackPanel
            {
                Orientation       = WpfOrientation.Horizontal,
                Margin            = new Thickness(12, 10, 12, 10),
                VerticalAlignment = VerticalAlignment.Center
            };

            // Tower logo — larger (60 × 60)
            try
            {
                var dllDir    = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                var imagePath = Path.Combine(dllDir, "tower logo.png");
                if (File.Exists(imagePath))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource         = new Uri(imagePath, UriKind.Absolute);
                    bmp.DecodePixelHeight = 60;
                    bmp.CacheOption       = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();

                    logoRow.Children.Add(new System.Windows.Controls.Image
                    {
                        Source              = bmp,
                        Height              = 60,
                        Width               = 60,
                        Margin              = new Thickness(0, 0, 12, 0),
                        VerticalAlignment   = VerticalAlignment.Center,
                        HorizontalAlignment = WpfHAlign.Left,
                        Stretch             = Stretch.Uniform
                    });
                }
            }
            catch { /* logo optional */ }

            // Title + subtitle
            var titleStack = new StackPanel
            {
                Orientation       = WpfOrientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center
            };

            titleStack.Children.Add(new TextBlock
            {
                Text       = "Advance TowerGen",
                Foreground = WpfBrushes.White,
                FontWeight = FontWeights.Bold,
                FontSize   = 16,
                FontFamily = new WpfFontFamily("Segoe UI"),
            });

            titleStack.Children.Add(new TextBlock
            {
                Text       = "Steel Tower Generation Suite",
                Foreground = new SolidColorBrush(WpfColor.FromRgb(0x8A, 0xA8, 0xCC)),
                FontWeight = FontWeights.Normal,
                FontSize   = 10,
                FontFamily = new WpfFontFamily("Segoe UI"),
                Margin     = new Thickness(0, 2, 0, 0)
            });

            logoRow.Children.Add(titleStack);

            var outerStack = new StackPanel { Orientation = WpfOrientation.Vertical };
            outerStack.Children.Add(accentStripe);
            outerStack.Children.Add(new Border
            {
                Background = new SolidColorBrush(C_CARD),
                Child      = logoRow
            });

            return outerStack;
        }

        // ── Step header ───────────────────────────────────────────────────────────

        private static UIElement MakeStepHeader(string stepNum, string title)
        {
            // Number badge
            var badge = new Border
            {
                Width             = 22,
                Height            = 22,
                CornerRadius      = new CornerRadius(11),
                Background        = new SolidColorBrush(C_STEP_BAR),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 8, 0),
                Child             = new TextBlock
                {
                    Text                = stepNum,
                    Foreground          = WpfBrushes.White,
                    FontWeight          = FontWeights.Bold,
                    FontSize            = 11,
                    FontFamily          = new WpfFontFamily("Segoe UI"),
                    HorizontalAlignment = WpfHAlign.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                    TextAlignment       = TextAlignment.Center
                }
            };

            var label = new TextBlock
            {
                Text              = title,
                Foreground        = new SolidColorBrush(C_STEP_TEXT),
                FontWeight        = FontWeights.SemiBold,
                FontSize          = 12,
                FontFamily        = new WpfFontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping      = TextWrapping.Wrap
            };

            var row = new StackPanel
            {
                Orientation = WpfOrientation.Horizontal,
                Margin      = new Thickness(10, 0, 10, 0)
            };
            row.Children.Add(badge);
            row.Children.Add(label);

            return new Border
            {
                Background        = new SolidColorBrush(C_STEP_BG),
                BorderBrush       = new SolidColorBrush(C_STEP_BAR),
                BorderThickness   = new Thickness(3, 0, 0, 0),
                Padding           = new Thickness(0, 9, 0, 9),
                Margin            = new Thickness(0, 8, 0, 4),
                Child             = row
            };
        }

        private static UIElement MakeSeparator()
            => new Border
            {
                Height     = 1,
                Margin     = new Thickness(8, 4, 8, 4),
                Background = new SolidColorBrush(C_SEP)
            };

        private static UIElement MakeStepSpacer()
            => new Border { Height = 4 };

        // ── Button factories ─────────────────────────────────────────────────────

        private static WpfButton MakeCommandButton(string label, string command, Style style)
        {
            var btn = new WpfButton { Content = label, Style = style };
            btn.Click += (_, __) =>
            {
                try
                {
                    var doc = AcadApp.DocumentManager.MdiActiveDocument;
                    doc?.SendStringToExecute(command + "\n", true, false, false);
                }
                catch { }
            };
            return btn;
        }

        private static WpfButton MakeBrowserButton(string label, string url, Style style)
        {
            var btn = new WpfButton { Content = label, Style = style };
            btn.Click += (_, __) =>
            {
                try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
                catch { }
            };
            return btn;
        }

        private static WpfButton MakeNoopButton(string label, Style style)
        {
            var btn = new WpfButton { Content = label, Style = style };
            btn.Click += (_, __) =>
            {
                try
                {
                    var doc = AcadApp.DocumentManager.MdiActiveDocument;
                    doc?.Editor.WriteMessage("\nThis step is not yet implemented.");
                }
                catch { }
            };
            return btn;
        }

        // ── "Generate Model Views" dropdown ──────────────────────────────────────

        private static UIElement BuildViewDropdown(Style btnStyle)
        {
            // Same visual style as other active buttons — no special highlighting
            var dropBtn = new WpfButton
            {
                Content = "Generate Model Views  ▼",
                Style   = btnStyle
            };

            var popupStack = new StackPanel
            {
                Background = new SolidColorBrush(WpfColor.FromRgb(0x2A, 0x2A, 0x2E))
            };

            var viewCommands = new[]
            {
                ("Save Front View DWG",  "SAVEFRONTVIEWDWG"),
                ("Save Back View DWG",   "SAVEBACKVIEWDWG"),
                ("Save Right View DWG",  "SAVERIGHTVIEWDWG"),
                ("Save Left View DWG",   "SAVELEFTVIEWDWG"),
                ("Save Top View DWG",    "SAVETOPVIEWDWG"),
            };

            foreach (var (label, cmd) in viewCommands)
                popupStack.Children.Add(MakeCommandButton(label, cmd, MakeButtonStyle(stretch: true)));

            var popup = new Popup
            {
                Child = new Border
                {
                    Background      = new SolidColorBrush(WpfColor.FromRgb(0x2A, 0x2A, 0x2E)),
                    BorderBrush     = new SolidColorBrush(WpfColor.FromRgb(0x1B, 0x5E, 0x9C)),
                    BorderThickness = new Thickness(1),
                    Child           = popupStack,
                    MinWidth        = 230
                },
                Placement          = PlacementMode.Bottom,
                StaysOpen          = false,
                AllowsTransparency = true,
            };

            dropBtn.Click += (_, __) =>
            {
                popup.PlacementTarget = dropBtn;
                popup.IsOpen          = !popup.IsOpen;
            };

            var container = new Grid();
            container.Children.Add(dropBtn);
            container.Children.Add(popup);
            return container;
        }

        // ── Footer — CCTech branding ──────────────────────────────────────────────

        private static UIElement BuildFooter()
        {
            var footerStack = new StackPanel { Orientation = WpfOrientation.Vertical };

            // Top separator
            footerStack.Children.Add(new Border
            {
                Height     = 1,
                Background = new SolidColorBrush(C_HEADER_TOP)
            });

            System.Windows.Controls.Image? logoImage = null;
            try
            {
                var dllDir   = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                var logoPath = Path.Combine(dllDir, "cctech logo image.png");
                if (File.Exists(logoPath))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource   = new Uri(logoPath, UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();

                    logoImage = new System.Windows.Controls.Image
                    {
                        Source              = bmp,
                        Stretch             = Stretch.Uniform,
                        VerticalAlignment   = VerticalAlignment.Center,
                        HorizontalAlignment = WpfHAlign.Center,
                        Margin              = new Thickness(8, 6, 8, 6)
                    };
                }
            }
            catch { /* logo optional */ }

            var footerBorder = new Border
            {
                Background          = new SolidColorBrush(C_FOOTER_BG),
                HorizontalAlignment = WpfHAlign.Stretch,
                Child               = logoImage
            };

            footerStack.Children.Add(footerBorder);

            return footerStack;
        }

        // ── Button style ─────────────────────────────────────────────────────────

        private static Style MakeButtonStyle(bool stretch = false, bool disabled = false)
        {
            WpfColor normalCol  = disabled ? C_BTN_DIS_BG  : C_BTN_ACTIVE;
            WpfColor hoverCol   = disabled ? C_BTN_DIS_BG  : C_BTN_HOVER;
            WpfColor pressedCol =            C_BTN_PRESS;

            var normalBg  = new SolidColorBrush(normalCol);
            var hoverBg   = new SolidColorBrush(hoverCol);
            var pressedBg = new SolidColorBrush(pressedCol);
            normalBg.Freeze(); hoverBg.Freeze(); pressedBg.Freeze();

            var tmpl  = new ControlTemplate(typeof(WpfButton));
            var bdFac = new FrameworkElementFactory(typeof(Border));
            bdFac.Name = "bd";
            bdFac.SetValue(Border.BackgroundProperty,   normalBg);
            bdFac.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            bdFac.SetValue(Border.PaddingProperty,      new Thickness(10, 9, 10, 9));
            bdFac.SetValue(Border.BorderBrushProperty,
                disabled
                    ? new SolidColorBrush(WpfColor.FromRgb(0x3C, 0x3C, 0x42))
                    : new SolidColorBrush(WpfColor.FromRgb(0x25, 0x7A, 0xC5)));
            bdFac.SetValue(Border.BorderThicknessProperty, new Thickness(1));

            var cpFac = new FrameworkElementFactory(typeof(ContentPresenter));
            cpFac.SetValue(ContentPresenter.HorizontalAlignmentProperty, WpfHAlign.Center);
            cpFac.SetValue(ContentPresenter.VerticalAlignmentProperty,   VerticalAlignment.Center);
            cpFac.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            bdFac.AppendChild(cpFac);
            tmpl.VisualTree = bdFac;

            if (!disabled)
            {
                var hoverTr = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
                hoverTr.Setters.Add(new Setter
                    { Property = Border.BackgroundProperty, Value = hoverBg, TargetName = "bd" });
                tmpl.Triggers.Add(hoverTr);

                var pressedTr = new Trigger { Property = WpfButton.IsPressedProperty, Value = true };
                pressedTr.Setters.Add(new Setter
                    { Property = Border.BackgroundProperty, Value = pressedBg, TargetName = "bd" });
                tmpl.Triggers.Add(pressedTr);
            }

            WpfBrush fgColor = disabled
                ? new SolidColorBrush(C_BTN_DIS_FG)   // clearly readable light gray
                : WpfBrushes.White;

            var style = new Style(typeof(WpfButton));
            style.Setters.Add(new Setter(WpfControl.TemplateProperty,   tmpl));
            style.Setters.Add(new Setter(WpfControl.ForegroundProperty, fgColor));
            style.Setters.Add(new Setter(WpfControl.FontWeightProperty, disabled ? FontWeights.Normal : FontWeights.SemiBold));
            style.Setters.Add(new Setter(WpfControl.FontSizeProperty,   13.0));
            style.Setters.Add(new Setter(WpfControl.FontFamilyProperty, new WpfFontFamily("Segoe UI")));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty,
                new Thickness(8, 3, 8, 3)));
            style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty,
                stretch ? WpfHAlign.Stretch : WpfHAlign.Right));
            style.Setters.Add(new Setter(WpfControl.CursorProperty,
                disabled
                    ? System.Windows.Input.Cursors.Arrow
                    : System.Windows.Input.Cursors.Hand));

            return style;
        }
    }
}
