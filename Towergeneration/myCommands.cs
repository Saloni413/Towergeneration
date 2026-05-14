// =============================================================================
//  TowerGeneration – Advance Steel Plugin
//  Slanted lattice tower – tapered square section, wider at base than top.
//
//  Dimensions (mm):
//    Base square  : 11875 x 11875  (Z = 0)
//    Top  square  :  8875 x  8875  (Z = 8550)
//    Height       :  8550
//
//  Structure per face (4 faces total):
//    - 2 slanted corner legs  (base outer corner → top inner corner)
//    - 2 cross diagonals (X-brace): base-left→top-right, base-right→top-left
//
//  Top view (plan):
//    Outer square = base corners
//    Inner square = top corners
//    Each corner: one slanted leg connecting outer-base to inner-top
//
//  Profile: "AISC 15.0 Angle identical#@§@#L3-1/2X3-1/2X1/4"
//  Command: GENERATETOWER
// =============================================================================

using Autodesk.AutoCAD.Runtime;

// Advance Steel geometry – aliased to avoid conflict with AutoCAD types
using ASPoint3d  = Autodesk.AdvanceSteel.Geometry.Point3d;
using ASVector3d = Autodesk.AdvanceSteel.Geometry.Vector3d;

// Advance Steel structural objects
using Autodesk.AdvanceSteel.Modelling;

// Advance Steel transaction and document management
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
        //  Tower geometry (all mm)
        //
        //  The tower is centred at the world origin.
        //  Base corners are at ±BaseHalf in X and Y at Z=0.
        //  Top  corners are at ±TopHalf  in X and Y at Z=Height.
        //  Each leg runs from a base corner diagonally up to the nearest top corner,
        //  so the tower tapers inward as it rises.
        // -----------------------------------------------------------------------
        private const double BaseWidth = 11875.0;   // base square side length
        private const double TopWidth  =  8875.0;   // top  square side length
        private const double Height    =  8550.0;   // vertical height

        // Half-widths (tower is centred at origin)
        private const double BaseHalf = BaseWidth / 2.0;   // 5937.5
        private const double TopHalf  = TopWidth  / 2.0;   // 4437.5

        // -----------------------------------------------------------------------
        //  Profile key – two-part format: "RunName#@§@#SectionName"
        //  Sourced directly from AstorProfiles.mdf (USA, AISC 15.0 equal angles).
        // -----------------------------------------------------------------------
        private const string AngleProfile =
            "AISC 15.0 Angle identical#@§@#L3-1/2X3-1/2X1/4";

        // -----------------------------------------------------------------------
        //  GENERATETOWER – type in the Advance Steel command line
        // -----------------------------------------------------------------------
        [CommandMethod("GENERATETOWER", CommandFlags.Modal)]
        public void GenerateTower()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            try
            {
                ed.WriteMessage("\n[TowerGen] Generating slanted lattice tower...");
                ed.WriteMessage($"\n[TowerGen] Base: {BaseWidth} x {BaseWidth} mm");
                ed.WriteMessage($"\n[TowerGen] Top : {TopWidth}  x {TopWidth}  mm");
                ed.WriteMessage($"\n[TowerGen] H   : {Height} mm");
                ed.WriteMessage($"\n[TowerGen] Profile: {AngleProfile}");

                DocumentManager.LockCurrentDocument();

                using (var steelTr = TransactionManager.StartTransaction())
                {
                    // ----------------------------------------------------------
                    //  Corner points
                    //
                    //  Base (Z=0) – labelled B1..B4 going round the square:
                    //    B1 = (-BaseHalf, -BaseHalf, 0)   front-left
                    //    B2 = (+BaseHalf, -BaseHalf, 0)   front-right
                    //    B3 = (+BaseHalf, +BaseHalf, 0)   back-right
                    //    B4 = (-BaseHalf, +BaseHalf, 0)   back-left
                    //
                    //  Top (Z=Height) – labelled T1..T4, same order, inset:
                    //    T1 = (-TopHalf,  -TopHalf,  H)   front-left
                    //    T2 = (+TopHalf,  -TopHalf,  H)   front-right
                    //    T3 = (+TopHalf,  +TopHalf,  H)   back-right
                    //    T4 = (-TopHalf,  +TopHalf,  H)   back-left
                    //
                    //  Each base corner maps to the nearest top corner (same
                    //  quadrant), so the legs splay inward as they rise.
                    // ----------------------------------------------------------
                    var B1 = new ASPoint3d(-BaseHalf, -BaseHalf, 0);
                    var B2 = new ASPoint3d( BaseHalf, -BaseHalf, 0);
                    var B3 = new ASPoint3d( BaseHalf,  BaseHalf, 0);
                    var B4 = new ASPoint3d(-BaseHalf,  BaseHalf, 0);

                    var T1 = new ASPoint3d(-TopHalf, -TopHalf, Height);
                    var T2 = new ASPoint3d( TopHalf, -TopHalf, Height);
                    var T3 = new ASPoint3d( TopHalf,  TopHalf, Height);
                    var T4 = new ASPoint3d(-TopHalf,  TopHalf, Height);

                    // ----------------------------------------------------------
                    //  1. Four slanted corner legs
                    //     Each leg: base corner → corresponding top corner
                    //     (same quadrant → tower tapers inward)
                    // ----------------------------------------------------------
                    ed.WriteMessage("\n[TowerGen] Creating 4 slanted corner legs...");
                    CreateBeam(B1, T1);   // front-left  leg
                    CreateBeam(B2, T2);   // front-right leg
                    CreateBeam(B3, T3);   // back-right  leg
                    CreateBeam(B4, T4);   // back-left   leg

                    // ----------------------------------------------------------
                    //  4. X-bracing on each of the 4 faces
                    //
                    //  Each face is a trapezoid (wider at base, narrower at top).
                    //  Two crossing diagonals form the X visible in the images:
                    //    diagonal A: base-left-corner  → top-right-corner of face
                    //    diagonal B: base-right-corner → top-left-corner  of face
                    //
                    //  Face front  (Y = -BaseHalf / -TopHalf):  B1-B2 / T1-T2
                    //  Face right  (X = +BaseHalf / +TopHalf):  B2-B3 / T2-T3
                    //  Face back   (Y = +BaseHalf / +TopHalf):  B3-B4 / T3-T4
                    //  Face left   (X = -BaseHalf / -TopHalf):  B4-B1 / T4-T1
                    // ----------------------------------------------------------
                    ed.WriteMessage("\n[TowerGen] Creating X-bracing on 4 faces...");

                    // Front face  (B1-B2 bottom, T1-T2 top)
                    CreateBeam(B1, T2);   // diagonal: front-left-base  → front-right-top
                    CreateBeam(B2, T1);   // diagonal: front-right-base → front-left-top

                    // Right face  (B2-B3 bottom, T2-T3 top)
                    CreateBeam(B2, T3);
                    CreateBeam(B3, T2);

                    // Back face   (B3-B4 bottom, T3-T4 top)
                    CreateBeam(B3, T4);
                    CreateBeam(B4, T3);

                    // Left face   (B4-B1 bottom, T4-T1 top)
                    CreateBeam(B4, T1);
                    CreateBeam(B1, T4);

                    steelTr.Commit();
                }

                DocumentManager.UnlockCurrentDocument();

                doc.Database.UpdateExt(true);
                ed.Regen();

                ed.WriteMessage("\n[TowerGen] Done.");
                ed.WriteMessage("\n[TowerGen] Members: 4 legs + 4 top + 4 base + 8 X-braces = 20 total.");
            }
            catch (System.Exception ex)
            {
                try { DocumentManager.UnlockCurrentDocument(); } catch { }
                ed.WriteMessage($"\n[TowerGen] ERROR: {ex.Message}");
                ed.WriteMessage($"\n[TowerGen] {ex.StackTrace}");
            }
        }

        // -----------------------------------------------------------------------
        //  CreateBeam
        //  Creates one StraightBeam between two points using the L-angle profile.
        //
        //  vUp selection:
        //    - If the beam is nearly vertical (axis close to Z), use global X.
        //    - Otherwise use global Z.
        //  This gives correct cross-section orientation for both legs and braces.
        // -----------------------------------------------------------------------
        private static void CreateBeam(ASPoint3d startPt, ASPoint3d endPt)
        {
            double dx = endPt.x - startPt.x;
            double dy = endPt.y - startPt.y;
            double dz = endPt.z - startPt.z;
            double len = System.Math.Sqrt(dx * dx + dy * dy + dz * dz);

            if (len < 1e-6) return;   // skip zero-length

            // Normalised Z component of the beam axis
            double absNormZ = System.Math.Abs(dz / len);

            // If beam is more than 70 % vertical, use X-axis as vUp
            // otherwise use Z-axis as vUp
            ASVector3d vUp = absNormZ > 0.7
                ? ASVector3d.kXAxis
                : ASVector3d.kZAxis;

            var beam = new StraightBeam(AngleProfile, startPt, endPt, vUp);
            beam.WriteToDb();
        }
    }
}
