// =============================================================================
//  TowerGeneration – Advance Steel Plugin
//  Generates a freestanding open-frame tower made entirely from L-angle sections.
//
//  Structure layout (no bottom frame):
//
//      ┌──────────────────┐   ← Top rectangular frame    (Z = 3000 mm)
//      │                  │
//      ├──────────────────┤   ← Middle rectangular frame  (Z = 1200 mm)
//      │                  │
//     leg  leg  leg  leg      ← Four independent legs, free at bottom (Z = 0)
//
//  All members: L-angle sections via StraightBeam.
//  Profile format: "TableName#@§@#SectionSize"
//
//  Run inside Advance Steel with command:  GENERATETOWER
// =============================================================================

using Autodesk.AutoCAD.Runtime;

// Advance Steel geometry – aliased to avoid conflict with AutoCAD types
using ASPoint3d  = Autodesk.AdvanceSteel.Geometry.Point3d;
using ASVector3d = Autodesk.AdvanceSteel.Geometry.Vector3d;

// Advance Steel structural objects (StraightBeam lives here)
using Autodesk.AdvanceSteel.Modelling;

// Advance Steel transaction manager and document lock
using Autodesk.AdvanceSteel.CADAccess;
using Autodesk.AdvanceSteel.DocumentManagement;

using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(Towergeneration.MyCommands))]
[assembly: ExtensionApplication(typeof(Towergeneration.PluginExtension))]

namespace Towergeneration
{
    public class MyCommands
    {
        // -----------------------------------------------------------------------
        //  Tower geometry  (all values in millimetres – change freely)
        // -----------------------------------------------------------------------
        private const double TotalHeight  = 3000.0;   // full tower height
        private const double Width        = 1000.0;   // X footprint
        private const double Depth        =  800.0;   // Y footprint
        private const double MidElevation = 1200.0;   // middle frame height

        // -----------------------------------------------------------------------
        //  L-angle profile
        //
        //  Format:  "SectionTable#@§@#SectionSize"
        //
        //  The table name must match exactly what is in YOUR Advance Steel
        //  section library.  To find it:
        //    1. In Advance Steel type command:  ASTORPROFILES
        //    2. Browse to the Angles / Equal Leg Angles group
        //    3. Note the exact table name shown in the header
        //
        //  Common table names by region:
        //    International  →  "EN 10056-1 - Equal Leg Angles"
        //    USA (AISC)     →  "AISC Angles"
        //    Australia/NZ   →  "AS/NZS 3679.1 - Equal Angles"
        //
        //  Update AngleTable and AngleSection below to match your installation.
        // -----------------------------------------------------------------------
        private const string AngleTable   = "EN 10056-1 - Equal Leg Angles";
        private const string AngleSection = "L50x50x5";

        // Combined profile key used by StraightBeam constructor
        private static readonly string AngleProfile =
            $"{AngleTable}#@§@#{AngleSection}";

        // -----------------------------------------------------------------------
        //  GENERATETOWER  – main command
        //  Type this in the Advance Steel command line to run the plugin.
        // -----------------------------------------------------------------------
        [CommandMethod("GENERATETOWER", CommandFlags.Modal)]
        public void GenerateTower()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;

            try
            {
                ed.WriteMessage("\n[TowerGen] Starting tower generation...");
                ed.WriteMessage($"\n[TowerGen] Profile: {AngleProfile}");

                // Must lock the AS document before creating objects
                DocumentManager.LockCurrentDocument();

                // Open an Advance Steel write transaction
                using (var steelTr = TransactionManager.StartTransaction())
                {
                    // ----------------------------------------------------------
                    //  Four corner XY positions (Z = 0 at this stage)
                    //
                    //   C3 (0, Depth)  ─────  C4 (Width, Depth)
                    //       │                       │
                    //   C1 (0, 0)      ─────  C2 (Width, 0)
                    // ----------------------------------------------------------
                    var c1 = new ASPoint3d(0,     0,     0);
                    var c2 = new ASPoint3d(Width, 0,     0);
                    var c3 = new ASPoint3d(0,     Depth, 0);
                    var c4 = new ASPoint3d(Width, Depth, 0);

                    // ----------------------------------------------------------
                    //  1. Four vertical corner legs  (Z = 0 → Z = TotalHeight)
                    //     NO bottom frame – legs are free / independent at Z = 0
                    // ----------------------------------------------------------
                    ed.WriteMessage("\n[TowerGen] Creating 4 vertical legs...");
                    CreateVerticalLeg(c1, TotalHeight);
                    CreateVerticalLeg(c2, TotalHeight);
                    CreateVerticalLeg(c3, TotalHeight);
                    CreateVerticalLeg(c4, TotalHeight);

                    // ----------------------------------------------------------
                    //  2. Middle rectangular frame  (Z = MidElevation = 1200 mm)
                    // ----------------------------------------------------------
                    ed.WriteMessage("\n[TowerGen] Creating middle frame at Z=" + MidElevation + "...");
                    CreateFrameLevel(c1, c2, c3, c4, MidElevation);

                    // ----------------------------------------------------------
                    //  3. Top rectangular frame  (Z = TotalHeight = 3000 mm)
                    // ----------------------------------------------------------
                    ed.WriteMessage("\n[TowerGen] Creating top frame at Z=" + TotalHeight + "...");
                    CreateFrameLevel(c1, c2, c3, c4, TotalHeight);

                    // Commit – writes all 12 beams to the model database
                    steelTr.Commit();
                }

                DocumentManager.UnlockCurrentDocument();

                // Force a model regeneration so members appear in the viewport
                doc.Database.UpdateExt(true);
                ed.Regen();

                ed.WriteMessage("\n[TowerGen] Done. Tower generated successfully.");
                ed.WriteMessage("\n[TowerGen] Members created: 4 legs + 4 middle + 4 top = 12 total.");
            }
            catch (System.Exception ex)
            {
                // Always unlock on failure to avoid leaving the document locked
                try { DocumentManager.UnlockCurrentDocument(); } catch { }

                ed.WriteMessage($"\n[TowerGen] ERROR: {ex.Message}");
                ed.WriteMessage($"\n[TowerGen] {ex.StackTrace}");
            }
        }

        // -----------------------------------------------------------------------
        //  CreateVerticalLeg
        //
        //  Creates a single vertical L-angle from (x, y, 0) to (x, y, height).
        //  Beam axis is along Z → use global X-axis as the vUp orientation vector.
        // -----------------------------------------------------------------------
        private static void CreateVerticalLeg(ASPoint3d baseXY, double height)
        {
            var ptBottom = new ASPoint3d(baseXY.x, baseXY.y, 0);
            var ptTop    = new ASPoint3d(baseXY.x, baseXY.y, height);

            CreateAngleBeam(ptBottom, ptTop, ASVector3d.kXAxis);
        }

        // -----------------------------------------------------------------------
        //  CreateFrameLevel
        //
        //  Creates four L-angle beams forming a closed rectangle at 'elevation'.
        //
        //  Plan view:
        //    p3 ─── p4
        //    │       │
        //    p1 ─── p2
        //
        //  Beam axis is horizontal (in XY plane) → use global Z-axis as vUp.
        // -----------------------------------------------------------------------
        private static void CreateFrameLevel(
            ASPoint3d c1, ASPoint3d c2,
            ASPoint3d c3, ASPoint3d c4,
            double elevation)
        {
            var p1 = new ASPoint3d(c1.x, c1.y, elevation);
            var p2 = new ASPoint3d(c2.x, c2.y, elevation);
            var p3 = new ASPoint3d(c3.x, c3.y, elevation);
            var p4 = new ASPoint3d(c4.x, c4.y, elevation);

            var vUp = ASVector3d.kZAxis;   // orientation for horizontal members

            CreateAngleBeam(p1, p2, vUp);  // front  (along X, Y = 0)
            CreateAngleBeam(p3, p4, vUp);  // back   (along X, Y = Depth)
            CreateAngleBeam(p1, p3, vUp);  // left   (along Y, X = 0)
            CreateAngleBeam(p2, p4, vUp);  // right  (along Y, X = Width)
        }

        // -----------------------------------------------------------------------
        //  CreateAngleBeam
        //
        //  Core helper – instantiates one StraightBeam and writes it to the
        //  Advance Steel model database.
        //
        //  StraightBeam(string profileKey, Point3d start, Point3d end, Vector3d vUp)
        //    profileKey  →  "TableName#@§@#SectionSize"
        //    vUp         →  cross-section orientation reference vector
        // -----------------------------------------------------------------------
        private static void CreateAngleBeam(
            ASPoint3d  startPt,
            ASPoint3d  endPt,
            ASVector3d vUp)
        {
            if (endPt.DistanceTo(startPt) < 1e-6) return;  // skip zero-length

            var beam = new StraightBeam(AngleProfile, startPt, endPt, vUp);
            beam.WriteToDb();
        }
    }
}
