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
//    - Internal bracing inside each of the 2 triangles per face
//
//  Internal bracing per triangle (nodes are reference points only – no splits):
//    Outer leg side  : 5 nodes at t = 1/6, 2/6, 3/6, 4/6, 5/6  (from top corner)
//    Inner upper side: 2 nodes at t = 1/3, 2/3                  (from top corner to X)
//    Inner lower side: 3 nodes at t = 1/4, 2/4, 3/4             (from base corner to X)
//
//    Connectivity (leg node → side node):
//      Leg[1] → Upper[1]
//      Leg[2] → Upper[1]
//      Leg[3] → Upper[2]
//      Leg[3] → Lower[1]
//      Leg[4] → Lower[2]
//      Leg[5] → Lower[3]
//
//  Profile: L50x50x5 (AngleProfile), PL6tx110 (PlateProfile)
//  Commands: GENERATETOWER, GENERATELSECTION, GENERATEFROMJSON
// =============================================================================

using System.IO;
using System.Text.Json;

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
        private const string AngleProfile = "L50x50x5";
        private const string PlateProfile = "PL100x12";

        private const string MembersJsonPath =
            @"C:\Advanced POC\members_20260522_171605.json";

        // -----------------------------------------------------------------------
        //  GENERATEFROMJSON – all members from members JSON export
        // -----------------------------------------------------------------------
        [CommandMethod("GENERATEFROMJSON", CommandFlags.Modal)]
        public void GenerateFromJson()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            try
            {
                if (!File.Exists(MembersJsonPath))
                {
                    ed.WriteMessage($"\n[JsonGen] File not found: {MembersJsonPath}");
                    return;
                }

                ed.WriteMessage($"\n[JsonGen] Reading {MembersJsonPath}...");
                var jsonText = File.ReadAllText(MembersJsonPath);
                var data = JsonSerializer.Deserialize<MembersJsonFile>(jsonText);
                if (data?.Members == null || data.Members.Count == 0)
                {
                    ed.WriteMessage("\n[JsonGen] No members in JSON.");
                    return;
                }

                ed.WriteMessage($"\n[JsonGen] Loaded {data.Members.Count} member(s).");
                ed.WriteMessage($"\n[JsonGen] Angle profile: {AngleProfile}");
                ed.WriteMessage($"\n[JsonGen] Plate profile: {PlateProfile}");

                int angleCount = 0, plateCount = 0, skipped = 0;

                DocumentManager.LockCurrentDocument();

                using (var steelTr = TransactionManager.StartTransaction())
                {
                    foreach (var member in data.Members)
                    {
                        var startPt = new ASPoint3d(member.Xs, member.Ys, member.Zs);
                        var endPt   = new ASPoint3d(member.Xe, member.Ye, member.Ze);
                        var type = member.Type?.Trim() ?? "";

                        if (type.Equals("Angle", System.StringComparison.OrdinalIgnoreCase))
                        {
                            CreateLinearMember(startPt, endPt, AngleProfile);
                            angleCount++;
                        }
                        else if (type.Equals("Plate", System.StringComparison.OrdinalIgnoreCase))
                        {
                            CreateLinearMember(startPt, endPt, PlateProfile);
                            plateCount++;
                        }
                        else
                        {
                            ed.WriteMessage(
                                $"\n[JsonGen] Skipped mark {member.Mark}: unknown type '{member.Type}'.");
                            skipped++;
                        }
                    }

                    steelTr.Commit();
                }

                DocumentManager.UnlockCurrentDocument();

                doc.Database.UpdateExt(true);
                ed.Regen();

                ed.WriteMessage(
                    $"\n[JsonGen] Done. Angles: {angleCount}, Plates: {plateCount}, Skipped: {skipped}.");
            }
            catch (System.Exception ex)
            {
                try { DocumentManager.UnlockCurrentDocument(); } catch { }
                ed.WriteMessage($"\n[JsonGen] ERROR: {ex.Message}");
                ed.WriteMessage($"\n[JsonGen] {ex.StackTrace}");
            }
        }

        // -----------------------------------------------------------------------
        //  GENERATELSECTION – single L-angle between two 3D points
        // -----------------------------------------------------------------------
        [CommandMethod("GENERATELSECTION", CommandFlags.Modal)]
        public void GenerateLSection()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            var startPt = new ASPoint3d(0, 0, 0);
            var endPt   = new ASPoint3d(2000, 3150, 5570);

            try
            {
                ed.WriteMessage("\n[LSection] Generating L-section...");
                ed.WriteMessage($"\n[LSection] Start: ({startPt.x}, {startPt.y}, {startPt.z})");
                ed.WriteMessage($"\n[LSection] End  : ({endPt.x}, {endPt.y}, {endPt.z})");
                ed.WriteMessage($"\n[LSection] Profile: {AngleProfile}");

                DocumentManager.LockCurrentDocument();

                using (var steelTr = TransactionManager.StartTransaction())
                {
                    CreateBeam(startPt, endPt);
                    steelTr.Commit();
                }

                DocumentManager.UnlockCurrentDocument();

                doc.Database.UpdateExt(true);
                ed.Regen();

                ed.WriteMessage("\n[LSection] Done.");
            }
            catch (System.Exception ex)
            {
                try { DocumentManager.UnlockCurrentDocument(); } catch { }
                ed.WriteMessage($"\n[LSection] ERROR: {ex.Message}");
                ed.WriteMessage($"\n[LSection] {ex.StackTrace}");
            }
        }

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

                    // ----------------------------------------------------------
                    //  Internal bracing – fill each triangle on all 4 faces.
                    //
                    //  Each face has 2 triangles formed by the X-diagonals:
                    //    Left  triangle : TL corner, BL corner, X-crossing point
                    //    Right triangle : TR corner, BR corner, X-crossing point
                    //
                    //  AddTriangleBracing(apex, baseCorner, oppApex, oppBase)
                    //    apex      = top corner of the triangle's leg side
                    //    baseCorner= base corner of the triangle's leg side
                    //    oppApex   = top corner of the opposite diagonal end
                    //    oppBase   = base corner of the opposite diagonal end
                    //  The X-crossing is computed as the intersection of the two
                    //  diagonals (midpoint of the two diagonal midpoints for a
                    //  planar trapezoid gives the exact crossing point).
                    // ----------------------------------------------------------
                    ed.WriteMessage("\n[TowerGen] Adding internal bracing...");

                    // Front face  (B1=left-base, T1=left-top, B2=right-base, T2=right-top)
                    AddFaceInternalBracing(B1, T1, B2, T2);

                    // Right face
                    AddFaceInternalBracing(B2, T2, B3, T3);

                    // Back face
                    AddFaceInternalBracing(B3, T3, B4, T4);

                    // Left face
                    AddFaceInternalBracing(B4, T4, B1, T1);

                    steelTr.Commit();
                }

                DocumentManager.UnlockCurrentDocument();

                doc.Database.UpdateExt(true);
                ed.Regen();

                ed.WriteMessage("\n[TowerGen] Done.");
            }
            catch (System.Exception ex)
            {
                try { DocumentManager.UnlockCurrentDocument(); } catch { }
                ed.WriteMessage($"\n[TowerGen] ERROR: {ex.Message}");
                ed.WriteMessage($"\n[TowerGen] {ex.StackTrace}");
            }
        }

        // -----------------------------------------------------------------------
        //  AddFaceInternalBracing
        //
        //  Fills the two triangles formed by the X-diagonals on one face.
        //  Parameters (left side and right side of the face):
        //    BL = base-left corner,  TL = top-left corner   (left leg)
        //    BR = base-right corner, TR = top-right corner  (right leg)
        //
        //  The two diagonals are:  BL→TR  and  BR→TL
        //  Their crossing point X is computed by linear interpolation.
        //
        //  Left  triangle vertices : TL, BL, X   (leg = TL→BL, upper = TL→X, lower = BL→X)
        //  Right triangle vertices : TR, BR, X   (leg = TR→BR, upper = TR→X, lower = BR→X)
        //
        //  Reference nodes (NOT physical splits – coordinates only):
        //    Leg side   : 5 nodes at 1/6 … 5/6 from the TOP corner down to BASE
        //    Upper side : 2 nodes at 1/3, 2/3  from the TOP corner toward X
        //    Lower side : 3 nodes at 1/4, 2/4, 3/4 from the BASE corner toward X
        //
        //  Connectivity (leg node index → side node index, 1-based):
        //    Leg[1] → Upper[1]
        //    Leg[2] → Upper[1]
        //    Leg[3] → Upper[2]
        //    Leg[3] → Lower[1]
        //    Leg[4] → Lower[2]
        //    Leg[5] → Lower[3]
        // -----------------------------------------------------------------------
        private static void AddFaceInternalBracing(
            ASPoint3d BL, ASPoint3d TL,
            ASPoint3d BR, ASPoint3d TR)
        {
            // ------------------------------------------------------------------
            //  Compute the X-crossing point of the two diagonals BL→TR and BR→TL.
            //  For a planar trapezoid the crossing lies at the intersection of
            //  the two diagonals.  We solve parametrically:
            //    P = BL + s*(TR-BL) = BR + t*(TL-BR)
            //  Solving for s in 3-D (over-determined; use the XY plane components
            //  which are always non-degenerate for a non-degenerate face):
            //
            //    BL + s*(TR-BL) = BR + t*(TL-BR)
            //    s*(TR-BL) - t*(TL-BR) = BR-BL
            //
            //  Two equations (x and y):
            //    s*dAx - t*dBx = ex
            //    s*dAy - t*dBy = ey
            //  where dA = TR-BL, dB = TL-BR, e = BR-BL
            // ------------------------------------------------------------------
            double dAx = TR.x - BL.x,  dAy = TR.y - BL.y,  dAz = TR.z - BL.z;
            double dBx = TL.x - BR.x,  dBy = TL.y - BR.y;
            double ex  = BR.x - BL.x,  ey  = BR.y - BL.y;

            double det = dAx * (-dBy) - dAy * (-dBx);   // det of [dA | -dB] in XY
            double s;
            if (System.Math.Abs(det) > 1e-9)
            {
                s = (ex * (-dBy) - ey * (-dBx)) / det;
            }
            else
            {
                s = 0.5;   // fallback: midpoint (degenerate face)
            }

            var X = new ASPoint3d(
                BL.x + s * dAx,
                BL.y + s * dAy,
                BL.z + s * dAz);

            // ------------------------------------------------------------------
            //  Build reference nodes for each triangle.
            //  Convention: leg nodes numbered from TOP corner downward (1..5).
            //  Upper nodes numbered from TOP corner toward X (1..2).
            //  Lower nodes numbered from BASE corner toward X (1..3).
            // ------------------------------------------------------------------

            // ---- LEFT triangle  (leg: TL→BL,  upper: TL→X,  lower: BL→X) ----
            BraceTriangle(TL, BL, X);

            // ---- RIGHT triangle (leg: TR→BR,  upper: TR→X,  lower: BR→X) ----
            BraceTriangle(TR, BR, X);
        }

        // -----------------------------------------------------------------------
        //  BraceTriangle
        //
        //  Adds internal members to one triangle.
        //    topCorner  = apex (top of the leg side)
        //    baseCorner = base of the leg side
        //    cross      = X-crossing point (opposite vertex)
        //
        //  Leg nodes   L[1..5] at t = 1/6 … 5/6 from topCorner toward baseCorner
        //  Upper nodes U[1..2] at t = 1/3, 2/3  from topCorner toward cross
        //  Lower nodes Lo[1..3] at t = 1/4, 2/4, 3/4 from baseCorner toward cross
        //
        //  Members:
        //    L[1]  → U[1]
        //    L[2]  → U[1]
        //    L[3]  → U[2]
        //    L[3]  → Lo[1]
        //    L[4]  → Lo[2]
        //    L[5]  → Lo[3]
        // -----------------------------------------------------------------------
        private static void BraceTriangle(
            ASPoint3d topCorner,
            ASPoint3d baseCorner,
            ASPoint3d cross)
        {
            // Leg nodes (from top corner toward base corner)
            var L = new ASPoint3d[6];   // index 1..5 used
            for (int i = 1; i <= 5; i++)
                L[i] = Lerp(topCorner, baseCorner, i / 6.0);

            // Upper side nodes (from top corner toward X-crossing)
            var U1 = Lerp(topCorner, cross, 1.0 / 3.0);
            var U2 = Lerp(topCorner, cross, 2.0 / 3.0);

            // Lower side nodes (from base corner toward X-crossing)
            var Lo1 = Lerp(baseCorner, cross, 1.0 / 4.0);
            var Lo2 = Lerp(baseCorner, cross, 2.0 / 4.0);
            var Lo3 = Lerp(baseCorner, cross, 3.0 / 4.0);

            // Internal members
            CreateBeam(L[1], U1);
            CreateBeam(L[2], U1);
            CreateBeam(L[3], U2);
            CreateBeam(L[3], Lo3);
            CreateBeam(L[4], Lo2);
            CreateBeam(L[4], Lo1);
            CreateBeam(L[5], Lo1);
            CreateBeam(U2,   Lo3);   // upper[2] → lower[3]

            // Midpoint reference nodes (whole members stay intact – coords only)
            // M_upper = midpoint along L[3]→U2
            var M_upper = Lerp(L[3], U2, 0.5);
            CreateBeam(L[2],    M_upper);   // L[2]  → mid(L3-U2)
            CreateBeam(U1,      M_upper);   // U1    → mid(L3-U2)

            // M_lower = midpoint along L[3]→Lo3
            var M_lower = Lerp(L[3], Lo3, 0.5);
            CreateBeam(L[4],    M_lower);   // L[4]  → mid(L3-Lo3)
            CreateBeam(Lo2,     M_lower);   // Lo2   → mid(L3-Lo2)
        }

        // -----------------------------------------------------------------------
        //  Lerp – linear interpolation between two ASPoint3d values
        // -----------------------------------------------------------------------
        private static ASPoint3d Lerp(ASPoint3d a, ASPoint3d b, double t)
        {
            return new ASPoint3d(
                a.x + (b.x - a.x) * t,
                a.y + (b.y - a.y) * t,
                a.z + (b.z - a.z) * t);
        }

        // -----------------------------------------------------------------------
        //  CreateBeam – tower / brace members (AngleProfile)
        // -----------------------------------------------------------------------
        private static void CreateBeam(ASPoint3d startPt, ASPoint3d endPt)
            => CreateLinearMember(startPt, endPt, AngleProfile);

        // -----------------------------------------------------------------------
        //  CreateLinearMember
        //  StraightBeam between two points (angle or plate profile from database).
        //
        //  vUp: X when axis is mostly vertical, else Z.
        // -----------------------------------------------------------------------
        private static void CreateLinearMember(
            ASPoint3d startPt, ASPoint3d endPt, string profile)
        {
            double dx = endPt.x - startPt.x;
            double dy = endPt.y - startPt.y;
            double dz = endPt.z - startPt.z;
            double len = System.Math.Sqrt(dx * dx + dy * dy + dz * dz);

            if (len < 1e-6) return;

            double absNormZ = System.Math.Abs(dz / len);

            ASVector3d vUp = absNormZ > 0.7
                ? ASVector3d.kXAxis
                : ASVector3d.kZAxis;

            var beam = new StraightBeam(profile, startPt, endPt, vUp);
            beam.WriteToDb();
        }
    }
}
