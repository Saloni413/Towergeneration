using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AdvanceSteel.DocumentManagement;
using Autodesk.AdvanceSteel.CADAccess;
using Autodesk.AdvanceSteel.Modelling;
using Newtonsoft.Json;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Drawing;

using WpfBrushes     = System.Windows.Media.Brushes;
using WpfColor       = System.Windows.Media.Color;
using WpfGrid        = System.Windows.Controls.Grid;
using WpfOrientation = System.Windows.Controls.Orientation;

using ASPoint3d  = Autodesk.AdvanceSteel.Geometry.Point3d;
using ASVector3d = Autodesk.AdvanceSteel.Geometry.Vector3d;
using AcadApp    = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(Towergeneration.MyCommands))]
[assembly: ExtensionApplication(typeof(Towergeneration.PluginExtension))]

namespace Towergeneration
{
    // ── BOM row (matches PDF column order) ───────────────────────────────────────
    public class BomRow
    {
        public int    Quantity    { get; set; }
        public string Mark        { get; set; } = "";
        public string Description { get; set; } = "";
        public string Length      { get; set; } = "";   // mm, comma-thousands
        public string Grade       { get; set; } = "";
        public string PartWeight  { get; set; } = "";   // kg, 1 dp
        public string TotalWeight { get; set; } = "";   // kg, 1 dp
        public string Remark      { get; set; } = "";
    }

    // ── Main command class ───────────────────────────────────────────────────────
    public class MyCommands
    {
        private const string AngleProfile = "L100x10";

        private const string JsonPath =
            @"C:\Kalpataru Project\Towergeneration_trial\Correct.json";      

        // L100x10 section: 15.1 kg/m  (EN 10056-1)
        private const double KgPerMetre = 15.1;
        private const string SectionGrade = "S355JR";
        private const string SectionDesc  = "L100X10";

        // ── GENERATEFROMJSON ─────────────────────────────────────────────────────

        [CommandMethod("GENERATEFROMJSON", CommandFlags.Modal)]
        public void GenerateFromJson()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            try
            {
                if (!File.Exists(JsonPath))
                {
                    ed.WriteMessage("\nJSON file not found: " + JsonPath);
                    return;
                }

                string json = File.ReadAllText(JsonPath);
                TowerData? td = JsonConvert.DeserializeObject<TowerData>(json);

                if (td?.Members == null)
                {
                    ed.WriteMessage("\nNo member data found.");
                    return;
                }

                ed.WriteMessage("\nProject : " + td.ProjectName);
                ed.WriteMessage("\nMembers : " + td.Members.Count);

                DocumentManager.LockCurrentDocument();
                using (var tr = TransactionManager.StartTransaction())
                {
                    foreach (var m in td.Members)
                    {
                        if (m.Type != "Angle") continue;
                        ed.WriteMessage("\n  Member " + m.Mark);
                        CreateLinearMember(new ASPoint3d(m.Xs, m.Ys, m.Zs),
                                           new ASPoint3d(m.Xe, m.Ye, m.Ze),
                                           AngleProfile);
                    }
                    tr.Commit();
                }
                DocumentManager.UnlockCurrentDocument();

                doc.Database.UpdateExt(true);
                ed.Regen();
                ed.WriteMessage("\nTower generation complete.");

                // Zoom to fit, switch to SW Isometric, and apply Conceptual visual style
                // so AS StraightBeam members render as actual 3D steel sections, not wireframe lines.
                doc.SendStringToExecute("_ZOOM E \n_-VIEW _SWISO \n_VSCURRENT Conceptual \n",
                                        true, false, false);
            }
            catch (System.Exception ex)
            {
                try { DocumentManager.UnlockCurrentDocument(); } catch { }
                ed.WriteMessage("\nERROR: " + ex.Message);
            }
        }

        // ── GENERATEBOM ──────────────────────────────────────────────────────────
        //
        //  Reads the JSON file, groups members by Mark, computes length and
        //  weight from coordinates + section properties, and shows the BOM
        //  window immediately — no AS numbering command needed.
        // -------------------------------------------------------------------------

        [CommandMethod("GENERATEBOM", CommandFlags.Modal)]
        public static void GenerateBOM()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            try
            {
                if (!File.Exists(JsonPath))
                {
                    ed.WriteMessage("\nJSON file not found: " + JsonPath);
                    return;
                }

                string json = File.ReadAllText(JsonPath);
                TowerData? td = JsonConvert.DeserializeObject<TowerData>(json);

                if (td?.Members == null || td.Members.Count == 0)
                {
                    ed.WriteMessage("\nNo member data found in JSON.");
                    return;
                }

                // Compute length (mm) and weight (kg) for every Angle member
                var raw = td.Members
                    .Where(m => m.Type == "Angle")
                    .Select(m =>
                    {
                        double dx  = m.Xe - m.Xs;
                        double dy  = m.Ye - m.Ys;
                        double dz  = m.Ze - m.Zs;
                        double len = Math.Sqrt(dx*dx + dy*dy + dz*dz); // mm
                        double wt  = KgPerMetre * len / 1000.0;          // kg
                        return (mark: m.Mark, len, wt);
                    })
                    .ToList();

                if (raw.Count == 0)
                {
                    ed.WriteMessage("\nNo Angle members found.");
                    return;
                }

                // Group by Mark, sort numerically where possible
                var rows = raw
                    .GroupBy(r => r.mark)
                    .OrderBy(g =>
                    {
                        if (int.TryParse(g.Key, out int n)) return n;
                        return int.MaxValue;
                    })
                    .ThenBy(g => g.Key)
                    .Select(g =>
                    {
                        int    qty     = g.Count();
                        double lenMm   = g.First().len;
                        double partWt  = KgPerMetre * lenMm / 1000.0;
                        double totalWt = partWt * qty;

                        return new BomRow
                        {
                            Quantity    = qty,
                            Mark        = g.Key,
                            Description = SectionDesc,
                            Length      = ((int)Math.Round(lenMm)).ToString("N0"),
                            Grade       = SectionGrade,
                            PartWeight  = partWt.ToString("F1"),
                            TotalWeight = totalWt.ToString("F1"),
                            Remark      = ""
                        };
                    })
                    .ToList();

                ed.WriteMessage("\n" + rows.Count + " mark(s) — opening BOM window.");
                AcadApp.ShowModalWindow(null, BuildBomWindow(rows), false);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nERROR (GENERATEBOM): " + ex.Message + "\n" + ex.StackTrace);
            }
        }

        // ── BOM window — exact Advance Steel PDF template ────────────────────────

        private static Window BuildBomWindow(List<BomRow> rows)
        {
            // Palette
            var borderBrush = new SolidColorBrush(WpfColor.FromRgb(160, 160, 160));
            var headerGray  = new SolidColorBrush(WpfColor.FromRgb(240, 240, 240));
            var white       = WpfBrushes.White;
            var black       = WpfBrushes.Black;
            var dimGray     = new SolidColorBrush(WpfColor.FromRgb(100, 100, 100));
            const double FS = 11.5;

            // ── local helpers ────────────────────────────────────────────────────

            TextBlock TB(string text,
                         double size = FS,
                         bool bold = false,
                         TextAlignment align = TextAlignment.Left,
                         System.Windows.Media.Brush? fg = null)
                => new TextBlock
                {
                    Text          = text,
                    FontSize      = size,
                    FontWeight    = bold ? FontWeights.Bold : FontWeights.Normal,
                    TextAlignment = align,
                    Foreground    = fg ?? black,
                    TextWrapping  = TextWrapping.Wrap
                };

            Border Wrap(UIElement child,
                        Thickness border,
                        System.Windows.Media.Brush? bg = null,
                        Thickness? pad = null)
                => new Border
                {
                    BorderBrush     = borderBrush,
                    BorderThickness = border,
                    Background      = bg ?? white,
                    Padding         = pad ?? new Thickness(4, 2, 4, 2),
                    Child           = child
                };

            // ── totals ───────────────────────────────────────────────────────────
            int    totalQty = rows.Sum(r => r.Quantity);
            double totalWt  = rows.Sum(r =>
                double.TryParse(r.TotalWeight, out double w) ? w : 0);

            string today = System.DateTime.Now.ToString("d-MMM-yy");

            // ── outer container ──────────────────────────────────────────────────
            var outer = new StackPanel { Orientation = WpfOrientation.Vertical };

            // ════════════════════════════════════════════════════════════════════
            // HEADER: logo  |  Company / Client / Job / Project / Detailer / Date
            // ════════════════════════════════════════════════════════════════════
            var hdr = new WpfGrid();
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Logo
            var logo = new StackPanel
            {
                Orientation       = WpfOrientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(8, 8, 8, 8)
            };
            logo.Children.Add(TB("  \u25b2  AUTODESK\u00ae", 9));
            logo.Children.Add(TB("ADVANCE STEEL", 13, bold: true));
            var logoBorder = Wrap(logo,
                new Thickness(1, 1, 1, 1),
                bg: headerGray,
                pad: new Thickness(0));
            WpfGrid.SetColumn(logoBorder, 0);
            hdr.Children.Add(logoBorder);

            // Right metadata grid
            var meta = new WpfGrid();
            meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

            for (int i = 0; i < 4; i++)
                meta.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            void AddMeta(string text, int r, int c, int cs = 1,
                         bool bold = false, bool topBorder = false)
            {
                var b = Wrap(TB(text, FS, bold),
                    new Thickness(0, topBorder ? 1 : 0, 1, 1),
                    pad: new Thickness(5, 3, 5, 3));
                WpfGrid.SetRow(b, r); WpfGrid.SetColumn(b, c);
                if (cs > 1) WpfGrid.SetColumnSpan(b, cs);
                meta.Children.Add(b);
            }

            // Row 0: Company spans all
            var companyCell = Wrap(TB("Company", 15, bold: true),
                new Thickness(0, 1, 1, 1), pad: new Thickness(6, 4, 6, 4));
            WpfGrid.SetRow(companyCell, 0); WpfGrid.SetColumn(companyCell, 0);
            WpfGrid.SetColumnSpan(companyCell, 4);
            meta.Children.Add(companyCell);

            // Row 1
            AddMeta("Client:",  1, 0); AddMeta("", 1, 1);
            AddMeta("Job No:",  1, 2); AddMeta("", 1, 3);

            // Row 2
            AddMeta("Project:", 2, 0); AddMeta("", 2, 1);
            AddMeta("Date:",    2, 2); AddMeta(today, 2, 3);

            // Row 3
            AddMeta("Detailer:", 3, 0); AddMeta("", 3, 1, cs: 3);

            var metaBorder = Wrap(meta,
                new Thickness(0, 1, 1, 0),
                pad: new Thickness(0));
            WpfGrid.SetColumn(metaBorder, 1);
            hdr.Children.Add(metaBorder);

            // ── Save as PDF button ───────────────────────────────────────────────
            var saveBtn = new System.Windows.Controls.Button
            {
                Content             = "Save as PDF",
                Margin              = new Thickness(0, 0, 0, 8),
                Padding             = new Thickness(14, 5, 14, 5),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Background          = new SolidColorBrush(WpfColor.FromRgb(0, 114, 178)),
                Foreground          = WpfBrushes.White,
                BorderThickness     = new Thickness(0),
                FontWeight          = FontWeights.Bold,
                FontSize            = 11
            };
            saveBtn.Click += (_, __) =>
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title            = "Save BOM as PDF",
                    Filter           = "PDF Files (*.pdf)|*.pdf|All Files (*.*)|*.*",
                    DefaultExt       = ".pdf",
                    FileName         = "TowerBOM.pdf",
                    InitialDirectory = Path.GetDirectoryName(JsonPath) ?? @"C:\"
                };
                if (dlg.ShowDialog() != true) return;
                try
                {
                    WriteBomPdf(rows, dlg.FileName);
                    System.Windows.MessageBox.Show(
                        "BOM saved:\n" + dlg.FileName,
                        "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (System.Exception ex)
                {
                    System.Windows.MessageBox.Show(
                        "Failed to save PDF:\n" + ex.Message,
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            outer.Children.Add(saveBtn);

            outer.Children.Add(hdr);

            // ════════════════════════════════════════════════════════════════════
            // COLUMN HEADERS
            // ════════════════════════════════════════════════════════════════════
            double[] cw = { 60, 60, 150, 80, 70, 80, 80, 80 };
            string[] ch =
            {
                "Quantity", "Mark", "Description",
                "Length\n(mm)", "Grade",
                "Part weight\n(kg)", "Total weight\n(kg)", "Remark"
            };
            TextAlignment[] ca =
            {
                TextAlignment.Center, TextAlignment.Center, TextAlignment.Left,
                TextAlignment.Right,  TextAlignment.Center,
                TextAlignment.Right,  TextAlignment.Right,  TextAlignment.Left
            };

            WpfGrid MakeRow(string[] cells, bool isHdr,
                            System.Windows.Media.Brush? rowBg = null)
            {
                var g = new WpfGrid();
                for (int ci = 0; ci < cw.Length; ci++)
                    g.ColumnDefinitions.Add(new ColumnDefinition
                        { Width = new GridLength(cw[ci]) });

                for (int ci = 0; ci < cells.Length; ci++)
                {
                    bool boldCell = isHdr || (!isHdr && ci == 1);
                    var tb = new TextBlock
                    {
                        Text          = cells[ci],
                        FontSize      = isHdr ? 11 : FS,
                        FontWeight    = boldCell ? FontWeights.Bold : FontWeights.Normal,
                        TextAlignment = ca[ci],
                        TextWrapping  = TextWrapping.Wrap,
                        Foreground    = black
                    };
                    var cell = new Border
                    {
                        BorderBrush     = borderBrush,
                        BorderThickness = new Thickness(ci == 0 ? 1 : 0, 0, 1, 1),
                        Background      = rowBg ?? (isHdr ? headerGray : white),
                        Padding         = new Thickness(4, 3, 4, 3),
                        Child           = tb
                    };
                    WpfGrid.SetColumn(cell, ci);
                    g.Children.Add(cell);
                }
                return g;
            }

            // header row with top border
            outer.Children.Add(new Border
            {
                BorderBrush     = borderBrush,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child           = MakeRow(ch, isHdr: true)
            });

            // ════════════════════════════════════════════════════════════════════
            // DATA ROWS
            // ════════════════════════════════════════════════════════════════════
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var bg = (i % 2 == 1)
                    ? new SolidColorBrush(WpfColor.FromRgb(245, 248, 252))
                    : (System.Windows.Media.Brush)white;

                outer.Children.Add(MakeRow(new[]
                {
                    r.Quantity.ToString(),
                    r.Mark,
                    r.Description,
                    r.Length,
                    r.Grade,
                    r.PartWeight,
                    r.TotalWeight,
                    r.Remark
                }, isHdr: false, rowBg: bg));
            }

            // ════════════════════════════════════════════════════════════════════
            // TOTALS
            // ════════════════════════════════════════════════════════════════════
            var totGrid = new WpfGrid();
            totGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            totGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            totGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            totGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            totGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            void TotCell(string text, int row, int col,
                         bool bold = false, bool leftBorder = false)
            {
                var b = new Border
                {
                    BorderBrush     = borderBrush,
                    BorderThickness = new Thickness(leftBorder ? 1 : 0, 0, 1, 1),
                    Background      = bold ? headerGray : white,
                    Padding         = new Thickness(6, 3, 6, 3),
                    Child           = TB(text, FS, bold)
                };
                WpfGrid.SetRow(b, row); WpfGrid.SetColumn(b, col);
                totGrid.Children.Add(b);
            }

            TotCell("TOTAL QUANTITY", 0, 0, bold: true, leftBorder: true);
            TotCell(totalQty.ToString(), 0, 1);
            TotCell("", 0, 2);

            TotCell("TOTAL WEIGHT",   1, 0, bold: true, leftBorder: true);
            TotCell(totalWt.ToString("N1"), 1, 1);
            TotCell("kg", 1, 2);

            outer.Children.Add(new Border
            {
                Margin = new Thickness(0, 10, 0, 0),
                Child  = totGrid
            });

            // ════════════════════════════════════════════════════════════════════
            // FOOTER
            // ════════════════════════════════════════════════════════════════════
            var footer = new WpfGrid();
            footer.ColumnDefinitions.Add(new ColumnDefinition
                { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition
                { Width = GridLength.Auto });

            var fLeft  = TB("List produced by AUTODESK Advance Steel", 9, fg: dimGray);
            var fRight = TB("Page 1 / 1", 9, align: TextAlignment.Right, fg: dimGray);
            WpfGrid.SetColumn(fLeft,  0);
            WpfGrid.SetColumn(fRight, 1);
            footer.Children.Add(fLeft);
            footer.Children.Add(fRight);

            outer.Children.Add(new Border
            {
                BorderBrush     = borderBrush,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin          = new Thickness(0, 8, 0, 0),
                Padding         = new Thickness(4, 4, 4, 4),
                Child           = footer
            });

            // ── Window ──────────────────────────────────────────────────────────
            return new Window
            {
                Title  = "Bill of Materials",
                Width  = 800,
                Height = 640,
                ResizeMode            = ResizeMode.CanResizeWithGrip,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background            = white,
                Content = new ScrollViewer
                {
                    VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new Border
                    {
                        Background = white,
                        Padding    = new Thickness(14),
                        Child      = outer
                    }
                }
            };
        }

        // ── WriteBomPdf ──────────────────────────────────────────────────────────
        //  Generates a formatted A4-landscape PDF BOM using PdfSharp.
        // -------------------------------------------------------------------------

        private static void WriteBomPdf(List<BomRow> rows, string outputPath)
        {
            const double margin   = 36.0;
            const double hdrH     = 56.0;
            const double colHdrH  = 16.0;
            const double dataRowH = 13.0;

            (string hdr, double w, bool ra)[] cols =
            {
                ("Qty",           46, true ),
                ("Mark",          50, false),
                ("Description",  120, false),
                ("Length\n(mm)",  72, true ),
                ("Grade",         60, false),
                ("Part Wt\n(kg)", 72, true ),
                ("Total Wt\n(kg)",72, true ),
                ("Remark",        72, false),
            };
            double tableW = cols.Sum(c => c.w);

            if (GlobalFontSettings.FontResolver is not WinFontResolver)
                GlobalFontSettings.FontResolver = new WinFontResolver();

            using var doc = new PdfDocument();
            doc.Info.Title  = "Bill of Materials";
            doc.Info.Author = "Advance Steel Plugin";

            var fTitle  = new XFont("Arial", 12, XFontStyleEx.Bold);
            var fSub    = new XFont("Arial",  7, XFontStyleEx.Regular);
            var fColHdr = new XFont("Arial",  7, XFontStyleEx.Bold);
            var fCell   = new XFont("Arial",  7, XFontStyleEx.Regular);
            var fBold   = new XFont("Arial",  7, XFontStyleEx.Bold);
            var fSmall  = new XFont("Arial",  6, XFontStyleEx.Regular);

            var penBlack = new XPen(XColors.Black, 0.4);
            var penLight = new XPen(XColor.FromArgb(180, 180, 180), 0.3);
            var brushHdr = new XSolidBrush(XColor.FromArgb(240, 240, 240));
            var brushAlt = new XSolidBrush(XColor.FromArgb(245, 248, 252));
            var brushTot = new XSolidBrush(XColor.FromArgb(230, 230, 230));
            var fmtRight = new XStringFormat
            {
                Alignment     = XStringAlignment.Far,
                LineAlignment = XLineAlignment.Near
            };

            string today  = DateTime.Now.ToString("d-MMM-yy");
            int    totQty = rows.Sum(r => r.Quantity);
            double totWt  = rows.Sum(r =>
                double.TryParse(r.TotalWeight, out double w) ? w : 0.0);
            int pageNum = 0;

            PdfPage   page = doc.AddPage();
            page.Width  = XUnit.FromMillimeter(297);   // A4 landscape
            page.Height = XUnit.FromMillimeter(210);
            XGraphics gfx = XGraphics.FromPdfPage(page);
            pageNum++;
            double y = margin;

            // ── Header ───────────────────────────────────────────────────────────
            const double logoW = 120.0;
            gfx.DrawRectangle(penBlack, brushHdr, new XRect(margin, y, logoW, hdrH));
            gfx.DrawString("AUTODESK®", fSub, XBrushes.Black,
                           new XRect(margin + 4, y + 4, logoW - 8, 10), XStringFormats.TopLeft);
            gfx.DrawString("ADVANCE STEEL", fTitle, XBrushes.Black,
                           new XRect(margin + 3, y + 16, logoW - 6, 18), XStringFormats.TopLeft);

            double ix = margin + logoW;
            double iw = tableW - logoW;
            double ir = hdrH / 4.0;

            void InfoRow(int row, string lbl1, string val1, string lbl2, string val2)
            {
                double ry = y + row * ir;
                double lw = 52, vw = iw / 2.0 - lw;
                gfx.DrawRectangle(penLight, new XRect(ix,             ry, lw,              ir));
                gfx.DrawString(lbl1, fSub, XBrushes.Black, new XRect(ix + 3,           ry + 2, lw - 5,              ir - 4), XStringFormats.TopLeft);
                gfx.DrawRectangle(penLight, new XRect(ix + lw,        ry, vw,              ir));
                gfx.DrawString(val1, fBold, XBrushes.Black, new XRect(ix + lw + 3,      ry + 2, vw - 5,              ir - 4), XStringFormats.TopLeft);
                gfx.DrawRectangle(penLight, new XRect(ix + lw + vw,   ry, lw,              ir));
                gfx.DrawString(lbl2, fSub, XBrushes.Black, new XRect(ix + lw + vw + 3,  ry + 2, lw - 5,              ir - 4), XStringFormats.TopLeft);
                gfx.DrawRectangle(penLight, new XRect(ix + 2*lw + vw, ry, iw - 2*lw - vw, ir));
                gfx.DrawString(val2, fBold, XBrushes.Black, new XRect(ix + 2*lw + vw + 3, ry + 2, iw - 2*lw - vw - 5, ir - 4), XStringFormats.TopLeft);
            }

            gfx.DrawRectangle(penLight, new XRect(ix, y, iw, ir));
            gfx.DrawString("Company", fBold, XBrushes.Black,
                           new XRect(ix + 4, y + 2, iw - 8, ir - 4), XStringFormats.TopLeft);
            InfoRow(1, "Client :",   "", "Job No :", "");
            InfoRow(2, "Project :",  "", "Date :",   today);
            InfoRow(3, "Detailer :", "", "Units :",  "mm");
            gfx.DrawRectangle(penBlack, new XRect(margin, y, tableW, hdrH));
            y += hdrH + 2;

            // ── Column headers ────────────────────────────────────────────────────
            void DrawColHeaders()
            {
                double cx = margin;
                foreach (var (h, w, _) in cols)
                {
                    gfx.DrawRectangle(penBlack, brushHdr, new XRect(cx, y, w, colHdrH));
                    gfx.DrawString(h, fColHdr, XBrushes.Black,
                                   new XRect(cx + 2, y + 1, w - 4, colHdrH - 2),
                                   XStringFormats.Center);
                    cx += w;
                }
                y += colHdrH;
            }
            DrawColHeaders();

            // ── Data rows ─────────────────────────────────────────────────────────
            const double reserveH = dataRowH * 2 + 18;
            for (int i = 0; i < rows.Count; i++)
            {
                if (y + dataRowH > page.Height.Point - margin - reserveH)
                {
                    gfx.DrawString("Page " + pageNum, fSmall, XBrushes.Gray,
                                   new XRect(margin, page.Height.Point - 22, tableW, 10),
                                   fmtRight);
                    gfx.Dispose();
                    page        = doc.AddPage();
                    page.Width  = XUnit.FromMillimeter(297);
                    page.Height = XUnit.FromMillimeter(210);
                    gfx         = XGraphics.FromPdfPage(page);
                    pageNum++;
                    y = margin;
                    DrawColHeaders();
                }

                var r = rows[i];
                if (i % 2 == 1)
                    gfx.DrawRectangle(brushAlt, new XRect(margin, y, tableW, dataRowH));

                string[] cells =
                {
                    r.Quantity.ToString(), r.Mark, r.Description,
                    r.Length, r.Grade, r.PartWeight, r.TotalWeight, r.Remark
                };
                double cx = margin;
                for (int c = 0; c < cells.Length; c++)
                {
                    gfx.DrawRectangle(penBlack, new XRect(cx, y, cols[c].w, dataRowH));
                    gfx.DrawString(cells[c], c == 1 ? fBold : fCell, XBrushes.Black,
                                   new XRect(cx + 2, y + 1, cols[c].w - 4, dataRowH - 2),
                                   cols[c].ra ? fmtRight : XStringFormats.TopLeft);
                    cx += cols[c].w;
                }
                y += dataRowH;
            }

            // ── Totals ────────────────────────────────────────────────────────────
            y += 3;
            double tLbl = cols[0].w + cols[1].w + cols[2].w;
            double tVal = cols[3].w;

            gfx.DrawRectangle(penBlack, brushTot, new XRect(margin, y, tLbl, dataRowH));
            gfx.DrawString("TOTAL QUANTITY", fBold, XBrushes.Black,
                           new XRect(margin + 3, y + 1, tLbl - 6, dataRowH - 2),
                           XStringFormats.TopLeft);
            gfx.DrawRectangle(penBlack, new XRect(margin + tLbl, y, tVal, dataRowH));
            gfx.DrawString(totQty.ToString(), fBold, XBrushes.Black,
                           new XRect(margin + tLbl + 2, y + 1, tVal - 4, dataRowH - 2), fmtRight);
            y += dataRowH;

            gfx.DrawRectangle(penBlack, brushTot, new XRect(margin, y, tLbl, dataRowH));
            gfx.DrawString("TOTAL WEIGHT", fBold, XBrushes.Black,
                           new XRect(margin + 3, y + 1, tLbl - 6, dataRowH - 2),
                           XStringFormats.TopLeft);
            gfx.DrawRectangle(penBlack, new XRect(margin + tLbl, y, tVal, dataRowH));
            gfx.DrawString(totWt.ToString("N1") + " kg", fBold, XBrushes.Black,
                           new XRect(margin + tLbl + 2, y + 1, tVal - 4, dataRowH - 2), fmtRight);

            // ── Footer ────────────────────────────────────────────────────────────
            double fy = page.Height.Point - 22;
            gfx.DrawString("List produced by AUTODESK Advance Steel",
                           fSmall, XBrushes.Gray,
                           new XRect(margin, fy, tableW - 60, 10), XStringFormats.TopLeft);
            gfx.DrawString("Page " + pageNum, fSmall, XBrushes.Gray,
                           new XRect(margin, fy, tableW, 10), fmtRight);

            gfx.Dispose();
            doc.Save(outputPath);
        }

        // ── GENERATEELEVATION ────────────────────────────────────────────────────
        //
        //  Projects every Angle member onto the XY plane (drop Z = top view),
        //  places AS L100x10 StraightBeam members in model space, then creates a
        //  dedicated paper-space layout "TOP VIEW" with a scaled viewport.  A WPF
        //  window lets the user preview the view and export it as a standalone DWG.
        // -------------------------------------------------------------------------

        [CommandMethod("GENERATEELEVATION", CommandFlags.Modal)]
        public static void GenerateElevation()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            try
            {
                if (!File.Exists(JsonPath))
                {
                    ed.WriteMessage("\nJSON file not found: " + JsonPath);
                    return;
                }

                string json = File.ReadAllText(JsonPath);
                TowerData? td = JsonConvert.DeserializeObject<TowerData>(json);

                if (td?.Members == null || td.Members.Count == 0)
                {
                    ed.WriteMessage("\nNo member data found.");
                    return;
                }

                var members = td.Members.Where(m => m.Type == "Angle").ToList();
                if (members.Count == 0)
                {
                    ed.WriteMessage("\nNo Angle members found.");
                    return;
                }

                // Top view: X horizontal, Y depth (drop Z height).
                double maxX = members.Max(m => Math.Max(m.Xs, m.Xe));
                double minX = members.Min(m => Math.Min(m.Xs, m.Xe));
                double maxY = members.Max(m => Math.Max(m.Ys, m.Ye));
                double minY = members.Min(m => Math.Min(m.Ys, m.Ye));
                double elevWidth  = maxX - minX;
                double elevHeight = maxY - minY;

                // View placed to the right of the 3D tower in model space
                double offsetX  = members.Max(m => Math.Max(m.Xs, m.Xe)) + 2000.0;
                double elevMidX = offsetX + (minX + maxX) / 2.0;
                double elevMidY = (minY + maxY) / 2.0;

                DocumentManager.LockCurrentDocument();
                var db        = doc.Database;
                int lineCount = 0;
                const string layoutName = "TOP VIEW";

                // Build the standalone DWG (Lines only, no AS objects required)
                var elevDwgDb = new Autodesk.AutoCAD.DatabaseServices.Database(true, true);
                using (var elevDbTr = elevDwgDb.TransactionManager.StartTransaction())
                {
                    var eBt  = (Autodesk.AutoCAD.DatabaseServices.BlockTable)
                                elevDbTr.GetObject(elevDwgDb.BlockTableId,
                                    Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
                    var eBtr = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)
                                elevDbTr.GetObject(
                                    eBt[Autodesk.AutoCAD.DatabaseServices.BlockTableRecord.ModelSpace],
                                    Autodesk.AutoCAD.DatabaseServices.OpenMode.ForWrite);
                    foreach (var m in members)
                    {
                        double dx = m.Xe - m.Xs, dy = m.Ye - m.Ys;
                        if (Math.Sqrt(dx * dx + dy * dy) < 1e-6) continue;
                        var el = new Autodesk.AutoCAD.DatabaseServices.Line(
                            new Autodesk.AutoCAD.Geometry.Point3d(m.Xs, m.Ys, 0),
                            new Autodesk.AutoCAD.Geometry.Point3d(m.Xe, m.Ye, 0));
                        eBtr.AppendEntity(el);
                        elevDbTr.AddNewlyCreatedDBObject(el, true);
                    }
                    var eTxt = new Autodesk.AutoCAD.DatabaseServices.DBText
                    {
                        TextString     = layoutName,
                        Height         = 200.0,
                        Position       = new Autodesk.AutoCAD.Geometry.Point3d((minX + maxX) / 2.0, minY - 500.0, 0),
                        HorizontalMode = Autodesk.AutoCAD.DatabaseServices.TextHorizontalMode.TextCenter,
                        VerticalMode   = Autodesk.AutoCAD.DatabaseServices.TextVerticalMode.TextBase,
                        AlignmentPoint = new Autodesk.AutoCAD.Geometry.Point3d((minX + maxX) / 2.0, minY - 500.0, 0),
                    };
                    eBtr.AppendEntity(eTxt);
                    elevDbTr.AddNewlyCreatedDBObject(eTxt, true);
                    elevDbTr.Commit();
                }

                // ── AS L100x10 StraightBeam members in model space (X-Y plane) ──
                using (var asTr = TransactionManager.StartTransaction())
                {
                    foreach (var m in members)
                    {
                        double dx = m.Xe - m.Xs, dy = m.Ye - m.Ys;
                        if (Math.Sqrt(dx * dx + dy * dy) < 1e-6) continue;
                        CreateLinearMember(
                            new ASPoint3d(offsetX + m.Xs, m.Ys, 0),
                            new ASPoint3d(offsetX + m.Xe, m.Ye, 0),
                            AngleProfile);
                        lineCount++;
                    }
                    asTr.Commit();
                }

                // ── Title text + paper-space layout (AutoCAD DB transaction) ──
                using (var acadTr = db.TransactionManager.StartTransaction())
                {
                    var bt  = (Autodesk.AutoCAD.DatabaseServices.BlockTable)
                              acadTr.GetObject(db.BlockTableId,
                                  Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
                    var btr = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)
                              acadTr.GetObject(
                                  bt[Autodesk.AutoCAD.DatabaseServices.BlockTableRecord.ModelSpace],
                                  Autodesk.AutoCAD.DatabaseServices.OpenMode.ForWrite);

                    // Model-space title below the top view
                    double lblX = elevMidX, lblY = minY - 500.0;
                    var msTitle = new Autodesk.AutoCAD.DatabaseServices.DBText();
                    msTitle.SetDatabaseDefaults();
                    msTitle.TextString    = layoutName;
                    msTitle.Height        = 200.0;
                    msTitle.HorizontalMode = Autodesk.AutoCAD.DatabaseServices.TextHorizontalMode.TextCenter;
                    msTitle.VerticalMode   = Autodesk.AutoCAD.DatabaseServices.TextVerticalMode.TextBase;
                    msTitle.Position       = new Autodesk.AutoCAD.Geometry.Point3d(lblX, lblY, 0);
                    msTitle.AlignmentPoint = new Autodesk.AutoCAD.Geometry.Point3d(lblX, lblY, 0);
                    btr.AppendEntity(msTitle);
                    acadTr.AddNewlyCreatedDBObject(msTitle, true);

                    // ── Paper-space layout ────────────────────────────────────
                    var layoutMgr = Autodesk.AutoCAD.DatabaseServices.LayoutManager.Current;
                    if (layoutMgr.LayoutExists(layoutName))
                        layoutMgr.DeleteLayout(layoutName);
                    layoutMgr.CreateLayout(layoutName);

                    var layoutId  = layoutMgr.GetLayoutId(layoutName);
                    var layout    = (Autodesk.AutoCAD.DatabaseServices.Layout)
                                    acadTr.GetObject(layoutId,
                                        Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
                    var layoutBtr = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)
                                    acadTr.GetObject(layout.BlockTableRecordId,
                                        Autodesk.AutoCAD.DatabaseServices.OpenMode.ForWrite);

                    // Viewport sized for A3 landscape (420 × 297 mm) with margins
                    const double paperW = 420.0, paperH = 297.0, vpMargin = 15.0;
                    double vpW = paperW - 2 * vpMargin;
                    double vpH = paperH - 2 * vpMargin - 10.0;  // 10 mm title bar at bottom

                    // Scale to fit view with 10% padding
                    double scaleX    = vpW / elevWidth;
                    double scaleY    = vpH / elevHeight;
                    double fitScale  = Math.Min(scaleX, scaleY) / 1.1;
                    double viewH     = vpH / fitScale;

                    var vp = new Autodesk.AutoCAD.DatabaseServices.Viewport();
                    vp.SetDatabaseDefaults();
                    vp.CenterPoint = new Autodesk.AutoCAD.Geometry.Point3d(
                        paperW / 2.0, vpMargin + 10.0 + vpH / 2.0, 0);
                    vp.Width       = vpW;
                    vp.Height      = vpH;
                    vp.ViewCenter  = new Autodesk.AutoCAD.Geometry.Point2d(elevMidX, elevMidY);
                    vp.ViewHeight  = viewH;
                    vp.CustomScale = fitScale;
                    layoutBtr.AppendEntity(vp);
                    acadTr.AddNewlyCreatedDBObject(vp, true);
                    vp.On = true;

                    // Paper-space title text
                    var psTitle = new Autodesk.AutoCAD.DatabaseServices.DBText();
                    psTitle.SetDatabaseDefaults();
                    psTitle.TextString    = layoutName;
                    psTitle.Height        = 5.0;
                    psTitle.HorizontalMode = Autodesk.AutoCAD.DatabaseServices.TextHorizontalMode.TextCenter;
                    psTitle.VerticalMode   = Autodesk.AutoCAD.DatabaseServices.TextVerticalMode.TextBase;
                    psTitle.Position       = new Autodesk.AutoCAD.Geometry.Point3d(
                        paperW / 2.0, vpMargin / 2.0, 0);
                    psTitle.AlignmentPoint = psTitle.Position;
                    layoutBtr.AppendEntity(psTitle);
                    acadTr.AddNewlyCreatedDBObject(psTitle, true);

                    acadTr.Commit();
                }

                // Switch to the new layout (must be outside the transaction)
                Autodesk.AutoCAD.DatabaseServices.LayoutManager.Current.CurrentLayout = layoutName;

                DocumentManager.UnlockCurrentDocument();
                doc.Database.UpdateExt(true);
                ed.Regen();
                ed.WriteMessage(
                    $"\nLayout '{layoutName}' created with {lineCount} members.");

                // Enter the layout viewport, apply Conceptual visual style so AS members
                // render as actual 3D steel sections, then return to paper space.
                doc.SendStringToExecute("_MSPACE \n_VSCURRENT Conceptual \n_PSPACE \n",
                                        true, false, false);

                // Show preview window with Save-as-DWG option
                try
                {
                    AcadApp.ShowModalWindow(
                        null,
                        BuildElevationWindow(members, minX, maxX, minY, maxY, elevDwgDb),
                        false);
                }
                finally
                {
                    elevDwgDb?.Dispose();
                }
            }
            catch (System.Exception ex)
            {
                try { DocumentManager.UnlockCurrentDocument(); } catch { }
                ed.WriteMessage("\nERROR (GENERATEELEVATION): " + ex.Message
                                + "\n" + ex.StackTrace);
            }
        }

        // ── Elevation preview window with Save-as-DWG ────────────────────────────

        private static Window BuildElevationWindow(
            List<MemberData> members,
            double minX, double maxX, double minY, double maxY,
            Autodesk.AutoCAD.DatabaseServices.Database? elevDwgDb)
        {
            const double canvasW = 620, canvasH = 420;
            double elevW = maxX - minX;
            double elevH = maxY - minY;

            // Scale + translate so the elevation fills the canvas with a 20 px margin
            double scale  = Math.Min((canvasW - 40) / elevW, (canvasH - 40) / elevH);
            double transX = 20 + (canvasW - 40 - elevW * scale) / 2.0;
            double transY = 20 + (canvasH - 40 - elevH * scale) / 2.0;

            var canvas = new Canvas
            {
                Width      = canvasW,
                Height     = canvasH,
                Background = new SolidColorBrush(WpfColor.FromRgb(28, 28, 28))
            };

            foreach (var m in members)
            {
                // Top view: X horizontal, Y depth
                double mDx = m.Xe - m.Xs, mDy = m.Ye - m.Ys;
                double mLen = Math.Sqrt(mDx * mDx + mDy * mDy);
                if (mLen < 1e-6) continue;

                double cx1 = (m.Xs - minX) * scale + transX;
                double cy1 = canvasH - ((m.Ys - minY) * scale + transY);
                double cx2 = (m.Xe - minX) * scale + transX;
                double cy2 = canvasH - ((m.Ye - minY) * scale + transY);

                // Perpendicular in canvas space
                double cpx = -mDy / mLen;
                double cpy = -mDx / mLen;
                double halfOffset = Math.Max(2.0, GetLegSize(m) / 2.0 * scale);

                // Filled polygon representing the beam member cross-section face
                var poly = new System.Windows.Shapes.Polygon
                {
                    Fill            = new SolidColorBrush(WpfColor.FromRgb(70, 130, 180)),
                    Stroke          = WpfBrushes.White,
                    StrokeThickness = 0.5
                };
                poly.Points.Add(new System.Windows.Point(cx1 + cpx * halfOffset, cy1 + cpy * halfOffset));
                poly.Points.Add(new System.Windows.Point(cx2 + cpx * halfOffset, cy2 + cpy * halfOffset));
                poly.Points.Add(new System.Windows.Point(cx2 - cpx * halfOffset, cy2 - cpy * halfOffset));
                poly.Points.Add(new System.Windows.Point(cx1 - cpx * halfOffset, cy1 - cpy * halfOffset));
                canvas.Children.Add(poly);
            }

            var saveBtn = new System.Windows.Controls.Button
            {
                Content             = "Save as DWG",
                Margin              = new Thickness(0, 8, 0, 0),
                Padding             = new Thickness(14, 5, 14, 5),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Background          = new SolidColorBrush(WpfColor.FromRgb(0, 114, 178)),
                Foreground          = System.Windows.Media.Brushes.White,
                BorderThickness     = new Thickness(0),
                FontWeight          = FontWeights.Bold,
                FontSize            = 11
            };

            saveBtn.Click += (_, __) =>
            {
                if (elevDwgDb == null)
                {
                    System.Windows.MessageBox.Show(
                        "No elevation data available. Re-run GENERATEELEVATION.",
                        "No Data", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title            = "Save Elevation as DWG",
                    Filter           = "AutoCAD Drawing (*.dwg)|*.dwg|All Files (*.*)|*.*",
                    DefaultExt       = ".dwg",
                    FileName         = "TopView.dwg",
                    InitialDirectory = Path.GetDirectoryName(JsonPath) ?? @"C:\"
                };
                if (dlg.ShowDialog() != true) return;
                try
                {
                    elevDwgDb.SaveAs(dlg.FileName,
                        Autodesk.AutoCAD.DatabaseServices.DwgVersion.Current);
                    System.Windows.MessageBox.Show(
                        "Elevation saved:\n" + dlg.FileName,
                        "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (System.Exception ex)
                {
                    System.Windows.MessageBox.Show(
                        "Failed to save DWG:\n" + ex.Message,
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            var header = new TextBlock
            {
                Text       = "TOP VIEW — Preview",
                FontSize   = 13,
                FontWeight = FontWeights.Bold,
                Margin     = new Thickness(0, 0, 0, 6)
            };

            var outer = new StackPanel
            {
                Orientation = WpfOrientation.Vertical,
                Margin      = new Thickness(12)
            };
            outer.Children.Add(header);
            outer.Children.Add(new Border
            {
                BorderBrush     = new SolidColorBrush(WpfColor.FromRgb(80, 80, 80)),
                BorderThickness = new Thickness(1),
                Child           = canvas
            });
            outer.Children.Add(saveBtn);

            return new Window
            {
                Title                 = "Top View",
                Width                 = canvasW + 40,
                Height                = canvasH + 110,
                ResizeMode            = ResizeMode.CanResizeWithGrip,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background            = System.Windows.Media.Brushes.White,
                Content               = outer
            };
        }

        private static void SaveTopViewDwg(List<MemberData> members, string outputPath)
        {
            // Top view: X horizontal, Y depth (drop Z height).
            // Write AutoCAD Line entities directly into a fresh DB — no AS transaction needed.
            using var elevDb = new Autodesk.AutoCAD.DatabaseServices.Database(true, true);
            using var tr = elevDb.TransactionManager.StartTransaction();

            var bt  = (Autodesk.AutoCAD.DatabaseServices.BlockTable)
                       tr.GetObject(elevDb.BlockTableId,
                           Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
            var btr = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)
                       tr.GetObject(
                           bt[Autodesk.AutoCAD.DatabaseServices.BlockTableRecord.ModelSpace],
                           Autodesk.AutoCAD.DatabaseServices.OpenMode.ForWrite);


            foreach (var m in members)
            {
                double dx = m.Xe - m.Xs;
                double dy = m.Ye - m.Ys;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-6) continue;

                // ✅ perpendicular direction for thickness
                double px = -dy / len;
                double py = dx / len;

                double halfWidth = GetLegSize(m) / 2.0;

                var p1 = new Autodesk.AutoCAD.Geometry.Point2d(m.Xs + px * halfWidth, m.Ys + py * halfWidth);
                var p2 = new Autodesk.AutoCAD.Geometry.Point2d(m.Xe + px * halfWidth, m.Ye + py * halfWidth);
                var p3 = new Autodesk.AutoCAD.Geometry.Point2d(m.Xe - px * halfWidth, m.Ye - py * halfWidth);
                var p4 = new Autodesk.AutoCAD.Geometry.Point2d(m.Xs - px * halfWidth, m.Ys - py * halfWidth);

                var pline = new Autodesk.AutoCAD.DatabaseServices.Polyline(4);
                pline.AddVertexAt(0, p1, 0, 0, 0);
                pline.AddVertexAt(1, p2, 0, 0, 0);
                pline.AddVertexAt(2, p3, 0, 0, 0);
                pline.AddVertexAt(3, p4, 0, 0, 0);
                pline.Closed = true;

                btr.AppendEntity(pline);
                tr.AddNewlyCreatedDBObject(pline, true);
            }


            double cx = (members.Min(m => Math.Min(m.Xs, m.Xe)) +
                         members.Max(m => Math.Max(m.Xs, m.Xe))) / 2.0;
            double by = members.Min(m => Math.Min(m.Ys, m.Ye)) - 500.0;

            var txt = new Autodesk.AutoCAD.DatabaseServices.DBText
            {
                TextString     = "TOP VIEW",
                Height         = 200.0,
                Position       = new Autodesk.AutoCAD.Geometry.Point3d(cx, by, 0),
                HorizontalMode = Autodesk.AutoCAD.DatabaseServices.TextHorizontalMode.TextCenter,
                VerticalMode   = Autodesk.AutoCAD.DatabaseServices.TextVerticalMode.TextBase,
                AlignmentPoint = new Autodesk.AutoCAD.Geometry.Point3d(cx, by, 0),
            };
            btr.AppendEntity(txt);
            tr.AddNewlyCreatedDBObject(txt, true);

            tr.Commit();
            elevDb.SaveAs(outputPath, Autodesk.AutoCAD.DatabaseServices.DwgVersion.Current);
        }




        private static void SaveFrontViewDwg(List<MemberData> members, string outputPath, bool mirror = false)
        {
            using var elevDb = new Autodesk.AutoCAD.DatabaseServices.Database(true, true);

            using var tr = elevDb.TransactionManager.StartTransaction();

            var bt = (Autodesk.AutoCAD.DatabaseServices.BlockTable)
                tr.GetObject(elevDb.BlockTableId,
                    Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);

            var btr = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)
                tr.GetObject(
                    bt[Autodesk.AutoCAD.DatabaseServices.BlockTableRecord.ModelSpace],
                    Autodesk.AutoCAD.DatabaseServices.OpenMode.ForWrite);

            foreach (var m in members)
            {
                double dx = m.Xe - m.Xs;
                double dz = m.Ze - m.Zs;

                double len = Math.Sqrt(dx * dx + dz * dz);
                if (len < 1e-6) continue;

                double px = -dz / len;
                double pz = dx / len;

                double halfWidth = GetLegSize(m) / 2.0;

                // ✅ Apply mirror here

                double x1 = mirror ? -m.Xs : m.Xs;
                double x2 = mirror ? -m.Xe : m.Xe;


                double z1 = m.Zs;
                double z2 = m.Ze;


                var p1 = new Autodesk.AutoCAD.Geometry.Point2d(m.Xs + px * halfWidth, z1 + pz * halfWidth);
                var p2 = new Autodesk.AutoCAD.Geometry.Point2d(m.Xe + px * halfWidth, z2 + pz * halfWidth);
                var p3 = new Autodesk.AutoCAD.Geometry.Point2d(m.Xe - px * halfWidth, z2 - pz * halfWidth);
                var p4 = new Autodesk.AutoCAD.Geometry.Point2d(m.Xs - px * halfWidth, z1 - pz * halfWidth);

                var pline = new Autodesk.AutoCAD.DatabaseServices.Polyline(4);
                pline.AddVertexAt(0, p1, 0, 0, 0);
                pline.AddVertexAt(1, p2, 0, 0, 0);
                pline.AddVertexAt(2, p3, 0, 0, 0);
                pline.AddVertexAt(3, p4, 0, 0, 0);
                pline.Closed = true;

                btr.AppendEntity(pline);
                tr.AddNewlyCreatedDBObject(pline, true);
            }

            // Title
            double cx = (members.Min(m => Math.Min(m.Xs, m.Xe)) +
                         members.Max(m => Math.Max(m.Xs, m.Xe))) / 2.0;

            double baseZ = mirror
                ? -members.Max(m => Math.Max(m.Zs, m.Ze))
                : members.Min(m => Math.Min(m.Zs, m.Ze));

            var txt = new Autodesk.AutoCAD.DatabaseServices.DBText
            {
                TextString = mirror ? "BACK VIEW" : "FRONT VIEW",
                Height = 200.0,
                Position = new Autodesk.AutoCAD.Geometry.Point3d(cx, baseZ - 500.0, 0),
                HorizontalMode = Autodesk.AutoCAD.DatabaseServices.TextHorizontalMode.TextCenter,
                VerticalMode = Autodesk.AutoCAD.DatabaseServices.TextVerticalMode.TextBase,
                AlignmentPoint = new Autodesk.AutoCAD.Geometry.Point3d(cx, baseZ - 500.0, 0),
            };

            btr.AppendEntity(txt);
            tr.AddNewlyCreatedDBObject(txt, true);

            tr.Commit();

            elevDb.SaveAs(outputPath, Autodesk.AutoCAD.DatabaseServices.DwgVersion.Current);
        }



        private static void SaveRightViewDwg(List<MemberData> members, string outputPath, bool mirror = false)
        {
            // Right elevation: Y horizontal, Z vertical (drop X).
            using var elevDb = new Autodesk.AutoCAD.DatabaseServices.Database(true, true);

            using var tr = elevDb.TransactionManager.StartTransaction();

            var bt = (Autodesk.AutoCAD.DatabaseServices.BlockTable)
                tr.GetObject(elevDb.BlockTableId,
                    Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);

            var btr = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)
                tr.GetObject(
                    bt[Autodesk.AutoCAD.DatabaseServices.BlockTableRecord.ModelSpace],
                    Autodesk.AutoCAD.DatabaseServices.OpenMode.ForWrite);

            foreach (var m in members)
            {

                double y1 = mirror ? -m.Ys : m.Ys;
                double y2 = mirror ? -m.Ye : m.Ye;

                double z1 = m.Zs;
                double z2 = m.Ze;

                double dy = y2 - y1;
                double dz = z2 - z1;

                double len = Math.Sqrt(dy * dy + dz * dz);
                if (len < 1e-6) continue;

                // perpendicular direction
                double py = -dz / len;
                double pz = dy / len;

                double halfWidth = GetLegSize(m) / 2.0;

                var p1 = new Autodesk.AutoCAD.Geometry.Point2d(y1 + py * halfWidth, z1 + pz * halfWidth);
                var p2 = new Autodesk.AutoCAD.Geometry.Point2d(y2 + py * halfWidth, z2 + pz * halfWidth);
                var p3 = new Autodesk.AutoCAD.Geometry.Point2d(y2 - py * halfWidth, z2 - pz * halfWidth);
                var p4 = new Autodesk.AutoCAD.Geometry.Point2d(y1 - py * halfWidth, z1 - pz * halfWidth);


                var pline = new Autodesk.AutoCAD.DatabaseServices.Polyline(4);
                pline.AddVertexAt(0, p1, 0, 0, 0);
                pline.AddVertexAt(1, p2, 0, 0, 0);
                pline.AddVertexAt(2, p3, 0, 0, 0);
                pline.AddVertexAt(3, p4, 0, 0, 0);
                pline.Closed = true;

                btr.AppendEntity(pline);
                tr.AddNewlyCreatedDBObject(pline, true);
            }

            // Title text
            double cy = (members.Min(m => Math.Min(m.Ys, m.Ye)) +
                         members.Max(m => Math.Max(m.Ys, m.Ye))) / 2.0;

            double bz = members.Min(m => Math.Min(m.Zs, m.Ze)) - 500.0;

            var txt = new Autodesk.AutoCAD.DatabaseServices.DBText
            {
                TextString = mirror ? "LEFT VIEW" : "RIGHT VIEW",
                Height = 200.0,
                Position = new Autodesk.AutoCAD.Geometry.Point3d(cy, bz, 0),
                HorizontalMode = Autodesk.AutoCAD.DatabaseServices.TextHorizontalMode.TextCenter,
                VerticalMode = Autodesk.AutoCAD.DatabaseServices.TextVerticalMode.TextBase,
                AlignmentPoint = new Autodesk.AutoCAD.Geometry.Point3d(cy, bz, 0),
            };

            btr.AppendEntity(txt);
            tr.AddNewlyCreatedDBObject(txt, true);

            tr.Commit();

            elevDb.SaveAs(outputPath, Autodesk.AutoCAD.DatabaseServices.DwgVersion.Current);
        }


        // ── SAVEVIEWSDWG ─────────────────────────────────────────────────────
        //
        //  Reads the JSON, projects Angle members onto the XY plane, and saves a
        //  standalone DWG containing only the 2D VIEWS lines.  Run this command
        //  independently (no WPF window) to get a clean 2D DWG on disk.
        // -------------------------------------------------------------------------

        [CommandMethod("SAVETOPVIEWDWG", CommandFlags.Modal)]
        public static void SaveTopViewDwgCommand()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            try
            {
                if (!File.Exists(JsonPath))
                {
                    ed.WriteMessage("\nJSON file not found: " + JsonPath);
                    return;
                }

                string json = File.ReadAllText(JsonPath);
                TowerData? td = JsonConvert.DeserializeObject<TowerData>(json);

                if (td?.Members == null || td.Members.Count == 0)
                {
                    ed.WriteMessage("\nNo member data found.");
                    return;
                }

                var members = td.Members.Where(m => m.Type == "Angle").ToList();
                if (members.Count == 0)
                {
                    ed.WriteMessage("\nNo Angle members found.");
                    return;
                }

                // AutoCAD commands run on the main thread — show dialog directly
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title            = "Save Top View as DWG",
                    Filter           = "AutoCAD Drawing (*.dwg)|*.dwg|All Files (*.*)|*.*",  
                    DefaultExt       = ".dwg",
                    FileName         = "TopView.dwg",
                    InitialDirectory = Path.GetDirectoryName(JsonPath) ?? @"C:\"
                };
                string? savePath = (dlg.ShowDialog() == true) ? dlg.FileName : null;

                if (savePath == null)
                {
                    ed.WriteMessage("\nSave cancelled.");
                    return;
                }

                SaveTopViewDwg(members, savePath);
                ed.WriteMessage("\nElevation DWG saved: " + savePath);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nERROR (SAVETOPVIEWDWG): " + ex.Message
                                + "\n" + ex.StackTrace);
            }
        }



        [CommandMethod("SAVEFRONTVIEWDWG", CommandFlags.Modal)]
        public static void SaveFrontViewDwgCommand()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;

            try
            {
                if (!File.Exists(JsonPath))
                {
                    ed.WriteMessage("\nJSON file not found: " + JsonPath);
                    return;
                }

                string json = File.ReadAllText(JsonPath);

                TowerData? td = JsonConvert.DeserializeObject<TowerData>(json);

                if (td?.Members == null || td.Members.Count == 0)
                {
                    ed.WriteMessage("\nNo member data found.");
                    return;
                }

                var members = td.Members.Where(m => m.Type == "Angle").ToList();

                if (members.Count == 0)
                {
                    ed.WriteMessage("\nNo Angle members found.");
                    return;
                }

                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save Front View as DWG",
                    Filter = "AutoCAD Drawing (*.dwg)|*.dwg|All Files (*.*)|*.*",
                    DefaultExt = ".dwg",
                    FileName = "FrontView.dwg",
                    InitialDirectory = Path.GetDirectoryName(JsonPath) ?? @"C:\\"
                };

                string? savePath = (dlg.ShowDialog() == true) ? dlg.FileName : null;

                if (savePath == null)
                {
                    ed.WriteMessage("\nSave cancelled.");
                    return;
                }

                SaveFrontViewDwg(members, savePath);

                ed.WriteMessage("\nFront View DWG saved: " + savePath);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nERROR (SAVEFRONTVIEWDWG): " + ex.Message + "\n" + ex.StackTrace);
            }
        }



        [CommandMethod("SAVERIGHTVIEWDWG", CommandFlags.Modal)]
        public static void SaveRightViewDwgCommand()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;

            try
            {
                if (!File.Exists(JsonPath))
                {
                    ed.WriteMessage("\nJSON file not found: " + JsonPath);
                    return;
                }

                string json = File.ReadAllText(JsonPath);

                TowerData? td = JsonConvert.DeserializeObject<TowerData>(json);

                if (td?.Members == null || td.Members.Count == 0)
                {
                    ed.WriteMessage("\nNo member data found.");
                    return;
                }

                var members = td.Members.Where(m => m.Type == "Angle").ToList();

                if (members.Count == 0)
                {
                    ed.WriteMessage("\nNo Angle members found.");
                    return;
                }

                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save Right View as DWG",
                    Filter = "AutoCAD Drawing (*.dwg)|*.dwg|All Files (*.*)|*.*",
                    DefaultExt = ".dwg",
                    FileName = "RightView.dwg",
                    InitialDirectory = Path.GetDirectoryName(JsonPath) ?? @"C:\\"
                };

                string? savePath = (dlg.ShowDialog() == true) ? dlg.FileName : null;

                if (savePath == null)
                {
                    ed.WriteMessage("\nSave cancelled.");
                    return;
                }

                SaveRightViewDwg(members, savePath);

                ed.WriteMessage("\nRight View DWG saved: " + savePath);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nERROR (SAVERIGHTVIEWDWG): " + ex.Message + "\n" + ex.StackTrace);
            }
        }





        [CommandMethod("SAVEBACKVIEWDWG", CommandFlags.Modal)]
        public static void SaveBackViewDwgCommand()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;

            try
            {
                if (!File.Exists(JsonPath))
                {
                    ed.WriteMessage("\nJSON file not found: " + JsonPath);
                    return;
                }

                string json = File.ReadAllText(JsonPath);
                TowerData? td = JsonConvert.DeserializeObject<TowerData>(json);

                if (td?.Members == null || td.Members.Count == 0)
                {
                    ed.WriteMessage("\nNo member data found.");
                    return;
                }

                var members = td.Members.Where(m => m.Type == "Angle").ToList();

                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save Back View as DWG",
                    Filter = "AutoCAD Drawing (*.dwg)|*.dwg|All Files (*.*)|*.*",
                    DefaultExt = ".dwg",
                    FileName = "BackView.dwg",
                    InitialDirectory = Path.GetDirectoryName(JsonPath) ?? @"C:\\"
                };

                if (dlg.ShowDialog() != true) return;

                // ✅ Just mirror
                SaveFrontViewDwg(members, dlg.FileName, mirror: true);

                ed.WriteMessage("\nBack View DWG saved: " + dlg.FileName);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nERROR (SAVEBACKVIEWDWG): " + ex.Message);
            }
        }



        [CommandMethod("SAVELEFTVIEWDWG", CommandFlags.Modal)]
        public static void SaveLeftViewDwgCommand()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;

            try
            {
                if (!File.Exists(JsonPath))
                {
                    ed.WriteMessage("\nJSON file not found: " + JsonPath);
                    return;
                }

                string json = File.ReadAllText(JsonPath);
                TowerData? td = JsonConvert.DeserializeObject<TowerData>(json);

                var members = td?.Members?.Where(m => m.Type == "Angle").ToList();
                if (members == null || members.Count == 0)
                {
                    ed.WriteMessage("\nNo Angle members found.");
                    return;
                }

                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save Left View as DWG",
                    Filter = "AutoCAD Drawing (*.dwg)|*.dwg",
                    FileName = "LeftView.dwg"
                };

                if (dlg.ShowDialog() != true) return;

                // ✅ reuse right view with mirror
                SaveRightViewDwg(members, dlg.FileName, mirror: true);

                ed.WriteMessage("\nLeft View DWG saved: " + dlg.FileName);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nERROR (SAVELEFTVIEWDWG): " + ex.Message);
            }
        }







        // ── Elevation drawing helpers ────────────────────────────────────────────

        // Extracts the first leg dimension (mm) from a section description string.
        // "L100X10" → 100   "L80x80x6" → 80   "HL150x150x20" → 150
        private static double GetLegSize(MemberData m)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                m.Description ?? "", @"[Hh]?[Ll](\d+)");
            if (match.Success &&
                double.TryParse(match.Groups[1].Value, out double leg))
                return leg;
            return 100.0;
        }

        // ── Beam creation helpers ─────────────────────────────────────────────────

        private static void CreateLinearMember(ASPoint3d s, ASPoint3d e, string profile)
        {
            double dx  = e.x - s.x, dy = e.y - s.y, dz = e.z - s.z;
            double len = Math.Sqrt(dx*dx + dy*dy + dz*dz);
            if (len < 1e-6) return;

            ASVector3d vUp = Math.Abs(dz / len) > 0.7
                ? ASVector3d.kXAxis
                : ASVector3d.kZAxis;

            new StraightBeam(profile, s, e, vUp).WriteToDb();
        }
    }

    // Resolves Windows system fonts for PdfSharp 6 (which requires an explicit IFontResolver).
    internal sealed class WinFontResolver : IFontResolver
    {
        private static readonly string FontsDir =
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

        private static readonly Dictionary<(string, bool, bool), string> Map = new()
        {
            { ("Arial",           false, false), "arial"   },
            { ("Arial",           true,  false), "arialbd" },
            { ("Arial",           false, true ), "ariali"  },
            { ("Arial",           true,  true ), "arialbi" },
            { ("Times New Roman", false, false), "times"   },
            { ("Times New Roman", true,  false), "timesbd" },
            { ("Times New Roman", false, true ), "timesi"  },
            { ("Times New Roman", true,  true ), "timesbi" },
            { ("Courier New",     false, false), "cour"    },
            { ("Courier New",     true,  false), "courbd"  },
            { ("Courier New",     false, true ), "couri"   },
            { ("Courier New",     true,  true ), "courbi"  },
        };

        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
        {
            if (Map.TryGetValue((familyName, bold, italic), out string? stem))
                return new FontResolverInfo(stem);

            string key = familyName.ToLowerInvariant().Replace(" ", "")
                         + (bold && italic ? "bi" : bold ? "bd" : italic ? "i" : "");
            return new FontResolverInfo(key);
        }

        public byte[]? GetFont(string faceName)
        {
            foreach (string ext in new[] { ".ttf", ".otf", ".ttc" })
            {
                string path = Path.Combine(FontsDir, faceName + ext);
                if (File.Exists(path)) return File.ReadAllBytes(path);
            }
            foreach (string file in Directory.GetFiles(FontsDir))
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(file),
                                  faceName, StringComparison.OrdinalIgnoreCase))
                    return File.ReadAllBytes(file);
            }
            return null;
        }
    }
}
