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
            @"C:\08-06-2026 (Advance Steel)\tower_members_3d_manual.json";

        private const double FaceWidth = 3000.0;
        private const double HalfWidth = FaceWidth / 2.0;

        // L100x10 section: 15.1 kg/m  (EN 10056-1)
        private const double KgPerMetre = 15.1;
        private const string SectionGrade = "S355JR";
        private const string SectionDesc  = "L100X10";

        // ── Geometry helpers ─────────────────────────────────────────────────────

        private static ASPoint3d RotateAroundZ(ASPoint3d pt, double angle)
        {
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            return new ASPoint3d(pt.x * cos - pt.y * sin,
                                 pt.x * sin + pt.y * cos,
                                 pt.z);
        }

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
                        CreateBeam(new ASPoint3d(m.Xs, m.Ys, m.Zs),
                                   new ASPoint3d(m.Xe, m.Ye, m.Ze));
                    }
                    tr.Commit();
                }
                DocumentManager.UnlockCurrentDocument();

                doc.Database.UpdateExt(true);
                ed.Regen();
                ed.WriteMessage("\nTower generation complete.");
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

        // ── Beam creation helpers ─────────────────────────────────────────────────

        private static void CreateBeam(ASPoint3d s, ASPoint3d e)
            => CreateLinearMember(s, e, AngleProfile);

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
}
