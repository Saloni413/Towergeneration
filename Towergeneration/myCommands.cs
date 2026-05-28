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
        private const string AngleProfile = "L50x50x5";

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
