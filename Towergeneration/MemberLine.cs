using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

// Advance Steel geometry – aliased to avoid conflict with AutoCAD types
using ASPoint3d = Autodesk.AdvanceSteel.Geometry.Point3d;
using ASVector3d = Autodesk.AdvanceSteel.Geometry.Vector3d;

namespace Towergeneration
{
    internal class MemberLine
    {
        public ASPoint3d StartPoint { get; set; }
        public ASPoint3d EndPoint { get; set; }

        public MemberLine(double xs, double ys, double zs,
                          double xe, double ye, double ze)
        {
            StartPoint = new ASPoint3d(xs, ys, zs);
            EndPoint = new ASPoint3d(xe, ye, ze);
        }
    }
}
