// =============================================================================
//  TowerGeneration – Advance Steel Plugin
//  Slanted lattice tower – 3 body sections stacked vertically.
//
//  Section 1 (BOTTOM BODY):  Base 11875 x 11875 -> Top  8875 x  8875,  H = 8550,  Z = 0
//  Section 2 (MIDDLE BODY):  Base  8875 x  8875 -> Top  7296 x  7296,  H = 4500,  Z = 8550
//  Section 3 (UPPER BODY):   Base  7296 x  7296 -> Top  5717 x  5717,  H = 4500,  Z = 13050
//
//  Per face (4 faces, all sections):
//    - 4 slanted corner legs
//    - 2 full-height X-brace diagonals per face
//    - Horizontal ties at the X-crossing level, upper-half midpoint, lower-half midpoint
//    - X-brace sub-diagonals within each half (upper and lower)
//
//  Profile: L50x50x5 (AngleProfile), PL100x12 (PlateProfile)
//  Commands: GENERATETOWER, GENERATELSECTION, GENERATEFROMJSON
// =============================================================================

using System.IO;
using System.Text.Json;

using Autodesk.AutoCAD.Runtime;
using Newtonsoft.Json;

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
        //  Tower geometry (all mm) – three sections stacked vertically.
        //
        //  Taper rate = (11875 - 8875) / 8550 ~ 0.3509 mm width per mm height.
        //  Applied consistently so all sections share the same slope angle.
        //
        //  Section 1 (BOTTOM): Z   0 -> 8550,  base 11875 -> top  8875
        //  Section 2 (MIDDLE): Z 8550 -> 13050, base  8875 -> top  7296
        //  Section 3 (UPPER):  Z 13050 -> 17550, base  7296 -> top  5717
        // -----------------------------------------------------------------------

        // Section 1
        private const double S1_BaseWidth = 11875.0;
        private const double S1_TopWidth  =  8875.0;
        private const double S1_Height    =  8550.0;
        private const double S1_ZOffset   =     0.0;

        // Section 2 – base = section 1 top; same taper rate applied over 4500mm
        private const double S2_BaseWidth = S1_TopWidth;           // 8875
        private const double S2_TopWidth  = S1_TopWidth - (S1_BaseWidth - S1_TopWidth) / S1_Height * 4500.0; // ~7296
        private const double S2_Height    = 4500.0;
        private const double S2_ZOffset   = S1_Height;             // 8550

        // Section 3 – base = section 2 top; same taper rate applied over 4500mm
        private const double S3_BaseWidth = S2_TopWidth;           // ~7296
        private const double S3_TopWidth  = S2_TopWidth - (S1_BaseWidth - S1_TopWidth) / S1_Height * 4500.0; // ~5717
        private const double S3_Height    = 4500.0;
        private const double S3_ZOffset   = S1_Height + S2_Height; // 13050

        // -----------------------------------------------------------------------
        //  Profile key – two-part format: "RunName#@§@#SectionName"
        //  Sourced directly from AstorProfiles.mdf (USA, AISC 15.0 equal angles).
        // -----------------------------------------------------------------------
        private const string AngleProfile = "L50x50x5";
        private const string PlateProfile = "PL100x12";

        private const string jsonPath =
            @"C:\Users\Saloni Kumari\Downloads\test.json";

        // -----------------------------------------------------------------------
        //  GENERATEFROMJSON – all members from members JSON export
        // -----------------------------------------------------------------------
        [CommandMethod("GENERATEFROMJSON", CommandFlags.Modal)]
        public void GenerateFromJson()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;

            // JSON file path
            //string jsonPath = @"C:\TowerData\members.json";

            try
            {
                if (!File.Exists(jsonPath))
                {
                    ed.WriteMessage($"\nJSON file not found: {jsonPath}");
                    return;
                }

                // =========================
                // Read JSON
                // =========================
                string json = File.ReadAllText(jsonPath);

                TowerData towerData =
                    JsonConvert.DeserializeObject<TowerData>(json);

                if (towerData == null || towerData.Members == null)
                {
                    ed.WriteMessage("\nNo member data found.");
                    return;
                }

                ed.WriteMessage(
                    $"\nProject: {towerData.ProjectName}"
                );

                ed.WriteMessage(
                    $"\nTotal Members: {towerData.Members.Count}"
                );

                DocumentManager.LockCurrentDocument();

                using (var steelTr =
                       TransactionManager.StartTransaction())
                {
                    foreach (var member in towerData.Members)
                    {
                        // Only create Angle sections
                        if (member.Type != "Angle")
                            continue;

                        // =========================
                        // Create start/end points
                        // =========================
                        var startPt = new ASPoint3d(
                            member.Xs,
                            member.Ys,
                            member.Zs
                        );

                        var endPt = new ASPoint3d(
                            member.Xe,
                            member.Ye,
                            member.Ze
                        );

                        ed.WriteMessage(
                            $"\nCreating Member: {member.Mark}"
                        );

                        ed.WriteMessage(
                            $"\nType: {member.Type}"
                        );

                        ed.WriteMessage(
                            $"\nSection: {member.Description}"
                        );

                        // =========================
                        // Create beam
                        // =========================
                        CreateBeam(startPt, endPt);
                    }

                    steelTr.Commit();
                }

                DocumentManager.UnlockCurrentDocument();

                doc.Database.UpdateExt(true);
                ed.Regen();

                ed.WriteMessage("\nTower generation completed.");
            }
            catch (System.Exception ex)
            {
                try
                {
                    DocumentManager.UnlockCurrentDocument();
                }
                catch { }

                ed.WriteMessage($"\nERROR: {ex.Message}");
                ed.WriteMessage($"\n{ex.StackTrace}");
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

            // =========================
            // Connected member data
            // =========================
            var members = new List<MemberLine>()
            {
                new MemberLine(0,    0,    0,   1000, 3000, 0),
                new MemberLine(1000, 3000, 0,   2000, 3000, 0),
                new MemberLine(2000, 3000, 0,   3000, 0, 0),
                new MemberLine(3000, 0, 0,   0, 0,    0)
            };

            try
            {
                ed.WriteMessage("\n[LSection] Generating connected L-sections...");

                DocumentManager.LockCurrentDocument();

                using (var steelTr = TransactionManager.StartTransaction())
                {
                    foreach (var member in members)
                    {
                        ed.WriteMessage(
                            $"\n[LSection] Beam: " +
                            $"({member.StartPoint.x}, {member.StartPoint.y}, {member.StartPoint.z}) -> " +
                            $"({member.EndPoint.x}, {member.EndPoint.y}, {member.EndPoint.z})"
                        );

                        CreateBeam(member.StartPoint, member.EndPoint);
                    }

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
        //  GENERATETOWER – generates 3 body sections stacked vertically
        // -----------------------------------------------------------------------
        [CommandMethod("GENERATETOWER", CommandFlags.Modal)]
        public void GenerateTower()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            try
            {
                ed.WriteMessage("\n[TowerGen] Generating 3-section slanted lattice tower...");
                ed.WriteMessage($"\n[TowerGen] Profile: {AngleProfile}");

                DocumentManager.LockCurrentDocument();

                using (var steelTr = TransactionManager.StartTransaction())
                {
                    // Section 1 – BOTTOM BODY: Z 0 -> 8550
                    ed.WriteMessage($"\n[TowerGen] Section 1 (Bottom): base={S1_BaseWidth}, top={S1_TopWidth}, H={S1_Height}, Z={S1_ZOffset}");
                    GenerateTowerBody(S1_BaseWidth, S1_TopWidth, S1_Height, S1_ZOffset);

                    // Section 2 – MIDDLE BODY: Z 8550 -> 13050
                    ed.WriteMessage($"\n[TowerGen] Section 2 (Middle): base={S2_BaseWidth:F0}, top={S2_TopWidth:F0}, H={S2_Height}, Z={S2_ZOffset}");
                    GenerateTowerBody(S2_BaseWidth, S2_TopWidth, S2_Height, S2_ZOffset);

                    // Section 3 – UPPER BODY: Z 13050 -> 17550
                    ed.WriteMessage($"\n[TowerGen] Section 3 (Upper): base={S3_BaseWidth:F0}, top={S3_TopWidth:F0}, H={S3_Height}, Z={S3_ZOffset}");
                    GenerateTowerBody(S3_BaseWidth, S3_TopWidth, S3_Height, S3_ZOffset);

                    steelTr.Commit();
                }

                DocumentManager.UnlockCurrentDocument();

                doc.Database.UpdateExt(true);
                ed.Regen();

                ed.WriteMessage("\n[TowerGen] Done. Total height: " + (S1_Height + S2_Height + S3_Height) + " mm");
            }
            catch (System.Exception ex)
            {
                try { DocumentManager.UnlockCurrentDocument(); } catch { }
                ed.WriteMessage($"\n[TowerGen] ERROR: {ex.Message}");
                ed.WriteMessage($"\n[TowerGen] {ex.StackTrace}");
            }
        }

        // -----------------------------------------------------------------------
        //  GenerateTowerBody – one tapered body section
        //    baseWidth : square side length at the bottom of this section
        //    topWidth  : square side length at the top  of this section
        //    height    : vertical height of this section
        //    zOffset   : Z coordinate of the bottom of this section
        // -----------------------------------------------------------------------
        private static void GenerateTowerBody(
            double baseWidth, double topWidth, double height, double zOffset)
        {
            double bh = baseWidth / 2.0;
            double th = topWidth  / 2.0;
            double zT = zOffset + height;

            // Base corners (at zOffset)
            var B1 = new ASPoint3d(-bh, -bh, zOffset);
            var B2 = new ASPoint3d( bh, -bh, zOffset);
            var B3 = new ASPoint3d( bh,  bh, zOffset);
            var B4 = new ASPoint3d(-bh,  bh, zOffset);

            // Top corners (at zOffset + height)
            var T1 = new ASPoint3d(-th, -th, zT);
            var T2 = new ASPoint3d( th, -th, zT);
            var T3 = new ASPoint3d( th,  th, zT);
            var T4 = new ASPoint3d(-th,  th, zT);

            // 4 slanted corner legs
            CreateBeam(B1, T1);
            CreateBeam(B2, T2);
            CreateBeam(B3, T3);
            CreateBeam(B4, T4);

            // X-bracing on all 4 faces
            CreateBeam(B1, T2); CreateBeam(B2, T1);   // front face
            CreateBeam(B2, T3); CreateBeam(B3, T2);   // right face
            CreateBeam(B3, T4); CreateBeam(B4, T3);   // back  face
            CreateBeam(B4, T1); CreateBeam(B1, T4);   // left  face

            // Internal bracing on all 4 faces
            AddFaceInternalBracing(B1, T1, B2, T2);   // front face
            AddFaceInternalBracing(B2, T2, B3, T3);   // right face
            AddFaceInternalBracing(B3, T3, B4, T4);   // back  face
            AddFaceInternalBracing(B4, T4, B1, T1);   // left  face
        }

        // -----------------------------------------------------------------------
        //  AddFaceInternalBracing
        //
        //  Adds structural sub-bracing to one trapezoidal face.
        //  BL/TL = base/top of left leg,  BR/TR = base/top of right leg.
        //
        //  Seven members per face:
        //    1. Horizontal tie at the X-crossing level (LL -> RL)
        //    2. Horizontal tie at mid of upper half    (ULL -> URL)
        //    3. Upper sub-diagonal left-to-right       (ULL -> TR)
        //    4. Upper sub-diagonal right-to-left       (URL -> TL)
        //    5. Horizontal tie at mid of lower half    (LLL -> LRL)
        //    6. Lower sub-diagonal left-to-right       (LLL -> RL)
        //    7. Lower sub-diagonal right-to-left       (LRL -> LL)
        //
        //  All nodes are on leg lines or at true intersection heights –
        //  no midpoints of diagonal members are used.
        // -----------------------------------------------------------------------
        private static void AddFaceInternalBracing(
            ASPoint3d BL, ASPoint3d TL,
            ASPoint3d BR, ASPoint3d TR)
        {
            // Compute X-crossing: solve BL + s*(TR-BL) = BR + t*(TL-BR) in XY
            double dAx = TR.x - BL.x, dAy = TR.y - BL.y, dAz = TR.z - BL.z;
            double dBx = TL.x - BR.x, dBy = TL.y - BR.y;
            double ex  = BR.x - BL.x, ey  = BR.y - BL.y;

            double det = dAx * (-dBy) - dAy * (-dBx);
            double s   = Math.Abs(det) > 1e-9
                ? (ex * (-dBy) - ey * (-dBx)) / det
                : 0.5;

            var Xcross = new ASPoint3d(BL.x + s * dAx, BL.y + s * dAy, BL.z + s * dAz);

            // Leg nodes at the X-crossing height
            var LL = LerpToZ(BL, TL, Xcross.z);   // left  leg at crossing Z
            var RL = LerpToZ(BR, TR, Xcross.z);   // right leg at crossing Z

            // 1. Horizontal tie at crossing level
            CreateBeam(LL, RL);

            // Upper half: between X-crossing level and top corners
            var ULL = Lerp(LL, TL, 0.5);   // mid of left  leg, upper half
            var URL = Lerp(RL, TR, 0.5);   // mid of right leg, upper half

            // 2. Horizontal at mid of upper half
            CreateBeam(ULL, URL);
            // 3. & 4. X-brace sub-diagonals in upper half
            CreateBeam(ULL, TR);
            CreateBeam(URL, TL);

            // Lower half: between base corners and X-crossing level
            var LLL = Lerp(BL, LL, 0.5);   // mid of left  leg, lower half
            var LRL = Lerp(BR, RL, 0.5);   // mid of right leg, lower half

            // 5. Horizontal at mid of lower half
            CreateBeam(LLL, LRL);
            // 6. & 7. X-brace sub-diagonals in lower half
            CreateBeam(LLL, RL);
            CreateBeam(LRL, LL);
        }

        // -----------------------------------------------------------------------
        //  LerpToZ – walk along line a->b until the given Z is reached
        // -----------------------------------------------------------------------
        private static ASPoint3d LerpToZ(ASPoint3d a, ASPoint3d b, double targetZ)
        {
            double dz = b.z - a.z;
            if (Math.Abs(dz) < 1e-9) return a;
            return Lerp(a, b, (targetZ - a.z) / dz);
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
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);

            if (len < 1e-6) return;

            double absNormZ = Math.Abs(dz / len);

            ASVector3d vUp = absNormZ > 0.7
                ? ASVector3d.kXAxis
                : ASVector3d.kZAxis;

            var beam = new StraightBeam(profile, startPt, endPt, vUp);
            beam.WriteToDb();
        }
    }
}
