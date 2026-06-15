using Autodesk.AutoCAD.Runtime;

namespace Towergeneration
{
    public class PluginExtension : IExtensionApplication
    {
        public void Initialize()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application
                          .DocumentManager.MdiActiveDocument;
            doc?.Editor.WriteMessage(
                "\nTowergeneration plugin loaded." +
                "\n  GENERATEFROMJSON — build tower from JSON" +
                "\n  GENERATEBOM      — number parts & generate BOM PDF");
        }

        public void Terminate() { }
    }
}
