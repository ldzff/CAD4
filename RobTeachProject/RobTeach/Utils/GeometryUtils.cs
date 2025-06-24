using System;
using System.Diagnostics;
using System.Windows; // Required for Point
using IxMilia.Dxf; // Required for DxfPoint, DxfVector

namespace RobTeach.Utils
{
    public static class GeometryUtils
    {
        public static (DxfPoint Center, double Radius, double StartAngle, double EndAngle, DxfVector Normal, bool IsClockwise)? CalculateArcParametersFromThreePoints(DxfPoint p1, DxfPoint p2, DxfPoint p3, double tolerance = 1e-6)
        {
            // Implementation of 3-point to arc parameters calculation.
            // Source for algorithm idea: https://www.ambrsoft.com/TrigoCalc/Circle3D.htm and various geometry resources.

            // Check for collinearity or coincident points
            // Vector P1P2
            double v12x = p2.X - p1.X;
            double v12y = p2.Y - p1.Y;
            double v12z = p2.Z - p1.Z;

            // Vector P1P3
            double v13x = p3.X - p1.X;
            double v13y = p3.Y - p1.Y;
            double v13z = p3.Z - p1.Z;

            // Cross product (P1P2) x (P1P3)
            double crossX = v12y * v13z - v12z * v13y;
            double crossY = v12z * v13x - v12x * v13z;
            double crossZ = v12x * v13y - v12y * v13x;

            double crossLengthSq = crossX * crossX + crossY * crossY + crossZ * crossZ;
            if (crossLengthSq < tolerance * tolerance) // Points are collinear or too close
            {
                Debug.WriteLine("[WARNING] CalculateArcParametersFromThreePoints: Points are collinear or coincident.");
                return null;
            }

            DxfVector normal = new DxfVector(crossX, crossY, crossZ).Normalize();

            // Using a formula for circumcenter of a triangle in 2D (projected, assuming normal is mainly Z)
            Point pt1_2d = new Point(p1.X, p1.Y); // Using System.Windows.Point for 2D calculations
            Point pt2_2d = new Point(p2.X, p2.Y);
            Point pt3_2d = new Point(p3.X, p3.Y);

            double D_2d = 2 * (pt1_2d.X * (pt2_2d.Y - pt3_2d.Y) + pt2_2d.X * (pt3_2d.Y - pt1_2d.Y) + pt3_2d.X * (pt1_2d.Y - pt2_2d.Y));
            if (Math.Abs(D_2d) < tolerance) // Collinear in 2D projection
            {
                Debug.WriteLine("[WARNING] CalculateArcParametersFromThreePoints: Points are collinear in 2D projection.");
                return null;
            }

            double pt1Sq_2d = pt1_2d.X * pt1_2d.X + pt1_2d.Y * pt1_2d.Y;
            double pt2Sq_2d = pt2_2d.X * pt2_2d.X + pt2_2d.Y * pt2_2d.Y;
            double pt3Sq_2d = pt3_2d.X * pt3_2d.X + pt3_2d.Y * pt3_2d.Y;

            double centerX_2d = (pt1Sq_2d * (pt2_2d.Y - pt3_2d.Y) + pt2Sq_2d * (pt3_2d.Y - pt1_2d.Y) + pt3Sq_2d * (pt1_2d.Y - pt2_2d.Y)) / D_2d;
            double centerY_2d = (pt1Sq_2d * (pt3_2d.X - pt2_2d.X) + pt2Sq_2d * (pt1_2d.X - pt3_2d.X) + pt3Sq_2d * (pt2_2d.X - pt1_2d.X)) / D_2d;

            // Assuming Z is constant for the arc based on p1.Z. This is a simplification.
            // For true 3D arcs, the center's Z would be on the plane defined by p1, p2, p3.
            DxfPoint center = new DxfPoint(centerX_2d, centerY_2d, p1.Z);

            double radius = Math.Sqrt(Math.Pow(pt1_2d.X - centerX_2d, 2) + Math.Pow(pt1_2d.Y - centerY_2d, 2));

            double startAngle = Math.Atan2(p1.Y - center.Y, p1.X - center.X) * (180.0 / Math.PI);
            double midAngle = Math.Atan2(p2.Y - center.Y, p2.X - center.X) * (180.0 / Math.PI);
            double endAngle = Math.Atan2(p3.Y - center.Y, p3.X - center.X) * (180.0 / Math.PI);

            startAngle = (startAngle % 360 + 360) % 360;
            midAngle = (midAngle % 360 + 360) % 360;
            endAngle = (endAngle % 360 + 360) % 360;

            bool isClockwise;
            double sweepCCW = (endAngle - startAngle + 360) % 360;
            double midRelativeToStartCCW = (midAngle - startAngle + 360) % 360;

            if (midRelativeToStartCCW < sweepCCW)
            {
                isClockwise = false;
            }
            else
            {
                isClockwise = true;
                double tempAngle = startAngle;
                startAngle = endAngle;
                endAngle = tempAngle;
            }

            // Ensure DxfArc standard: StartAngle < EndAngle for CCW, or adjust if normal is flipped.
            // The DxfArc entity itself typically expects angles to define a CCW path if Normal is +Z.
            // The isClockwise flag helps understand the original P1->P2->P3 orientation.

            // If the geometric normal points generally in -Z, flip it and swap angles to maintain CCW interpretation for DXF.
            if (normal.Z < 0) {
                normal = new DxfVector(-normal.X, -normal.Y, -normal.Z);
                double temp = startAngle;
                startAngle = endAngle;
                endAngle = temp;
                // isClockwise remains based on original p1,p2,p3 order, but angles are now for CCW DXF arc with positive normal.
            }

            return (center, radius, startAngle, endAngle, normal, isClockwise);
        }
    }
}
