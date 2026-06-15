using System.IO;
using Autodesk.AutoCAD.Runtime;
using Newtonsoft.Json;

using ASPoint3d = Autodesk.AdvanceSteel.Geometry.Point3d;
using ASVector3d = Autodesk.AdvanceSteel.Geometry.Vector3d;

using Autodesk.AdvanceSteel.Modelling;
using Autodesk.AdvanceSteel.CADAccess;
using Autodesk.AdvanceSteel.DocumentManagement;

using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(Towergeneration.MyCommands))]
[assembly: ExtensionApplication(typeof(Towergeneration.PluginExtension))]

namespace Towergeneration
{
    public class MyCommands
    {
        private const string AngleProfile = "L100x10";

        private const string jsonPath =
            @"C:\Users\Saloni Kumari\Downloads\towercoordinates.json";

        private static readonly double TiltAngle =
            18.435 * Math.PI / 180.0;

        // -----------------------------------------------------------------------
        //  Your face spans X = 0 to 3000, so tower center in X = 1500
        //  Y = 0 for all points (front face)
        //  We treat center of base = (1500, 0, 0)
        // -----------------------------------------------------------------------
        private const double FaceWidth = 3000.0;  // W_base from JSON
        private const double HalfWidth = FaceWidth / 2.0;  // 1500

        // -----------------------------------------------------------------------
        //  Rotate around Z-axis (azimuth) — keeps Z unchanged
        // -----------------------------------------------------------------------
        private static ASPoint3d RotateAroundZ(ASPoint3d pt, double angle)  
        {
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);
            return new ASPoint3d(
                pt.x * cos - pt.y * sin,
                pt.x * sin + pt.y * cos,
                pt.z
            );
        }

        // -----------------------------------------------------------------------
        //  Transform a point for a given face index (0=front, 1=right,
        //  2=back, 3=left)
        //
        //  Strategy (order matters):
        //   1. Shift face so its base-center sits at global origin (0,0,0)
        //      i.e. subtract (HalfWidth, 0, 0)
        //   2. Rotate around Z by azimuth (0/90/180/270) to place face
        //      at correct side of tower
        //   3. Translate outward by HalfWidth along the face's new Y direction
        //      so all 4 faces sit at the correct distance from tower center
        // -----------------------------------------------------------------------
        private static ASPoint3d TransformPoint(ASPoint3d pt, int faceIndex)
        {
            // Step 1 — center the face at origin
            // Your face base goes from X=0 to X=3000, center is X=1500
            ASPoint3d centered = new ASPoint3d(
                pt.x - HalfWidth,   // shift so center of base = 0
                pt.y,               // Y=0 for all JSON points
                pt.z
            );

            // Step 2 — rotate around Z to place at correct azimuth
            double azimuth = faceIndex * Math.PI / 2.0; // 0, 90, 180, 270°
            ASPoint3d rotated = RotateAroundZ(centered, azimuth);

            // Step 3 — push face outward by HalfWidth along its own Y axis
            // After azimuth rotation, the face's outward direction is:
            //   face 0 → -Y direction  (sin(0)=0,  cos(0)=1  → offset in -Y)
            //   face 1 → +X direction
            //   face 2 → +Y direction
            //   face 3 → -X direction
            double[] offsetX = { 0, HalfWidth, 0, -HalfWidth };
            double[] offsetY = { -HalfWidth, 0, HalfWidth, 0 };

            return new ASPoint3d(
                rotated.x + offsetX[faceIndex],
                rotated.y + offsetY[faceIndex],
                rotated.z
            );
        }

        [CommandMethod("GENERATEFROMJSON", CommandFlags.Modal)]
        public void GenerateFromJson()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;

            try
            {
                if (!File.Exists(jsonPath))
                {
                    ed.WriteMessage($"\nJSON file not found: {jsonPath}");
                    return;
                }

                string json = File.ReadAllText(jsonPath);

                TowerData towerData =
                    JsonConvert.DeserializeObject<TowerData>(json);

                if (towerData == null || towerData.Members == null)
                {
                    ed.WriteMessage("\nNo member data found.");
                    return;
                }

                ed.WriteMessage($"\nProject: {towerData.ProjectName}");
                ed.WriteMessage($"\nTotal Members: {towerData.Members.Count}");

                DocumentManager.LockCurrentDocument();

                using (var steelTr =
                       TransactionManager.StartTransaction())
                {
                    ed.WriteMessage($"\n--- Generating from JSON (single face, as-is) ---");

                    foreach (var member in towerData.Members)
                    {
                        if (member.Type != "Angle")
                            continue;

                        var startPt = new ASPoint3d(
                            member.Xs, member.Ys, member.Zs);

                        var endPt = new ASPoint3d(
                            member.Xe, member.Ye, member.Ze);

                        ed.WriteMessage($"\n  Member {member.Mark}");

                        CreateBeam(startPt, endPt);
                    }

                    steelTr.Commit();
                }

                DocumentManager.UnlockCurrentDocument();

                doc.Database.UpdateExt(true);
                ed.Regen();

                ed.WriteMessage("\nTower generation completed — single face from JSON.");
            }
            catch (System.Exception ex)
            {
                try { DocumentManager.UnlockCurrentDocument(); } catch { }
                ed.WriteMessage($"\nERROR: {ex.Message}");
                ed.WriteMessage($"\n{ex.StackTrace}");
            }
        }

        private static void CreateBeam(ASPoint3d startPt, ASPoint3d endPt)
            => CreateLinearMember(startPt, endPt, AngleProfile);

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