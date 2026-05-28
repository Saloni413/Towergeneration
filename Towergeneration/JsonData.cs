using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Towergeneration
{
    public class TowerData
    {
        public string FileName { get; set; }
        public string ProcessedAt { get; set; }
        public string ProjectName { get; set; }
        public string DrawingNumber { get; set; }
        public string CoordinateOrigin { get; set; }
        public string Units { get; set; }

        public List<MemberData> Members { get; set; }
    }

    public class MemberData
    {
        public string Mark { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }

        public double Xs { get; set; }
        public double Ys { get; set; }
        public double Zs { get; set; }

        public double Xe { get; set; }
        public double Ye { get; set; }
        public double Ze { get; set; }
    }
}
