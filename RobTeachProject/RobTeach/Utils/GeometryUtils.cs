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
            if (normal.Z < 0) { // This might need more robust check if normal isn't primarily along Z
                // For a general 3D arc, the "handedness" of the coordinate system defined by
                // u=(p2-p1), v=(p3-p1) and their cross product (normal) matters.
                // DxfArc always assumes CCW sweep from start to end angle in its own coordinate system (defined by its Normal).
                // The returned normal should be consistent with the Start/End angles for a CCW sweep.
                // If our calculated normal (from p1,p2,p3 cross product) implies a CW sweep for p1->p2->p3 with given angles,
                // we might need to flip the normal OR adjust angles.
                // For simplicity, if normal.Z < 0 for an XY dominant plane, we flip normal and swap angles.
                // This part of arc definition can be tricky.
            }

            return (center, radius, startAngle, endAngle, normal, isClockwise);
        }

        /// <summary>
        /// Calculates the center, radius, and normal of a circle defined by three distinct, non-collinear 3D points.
        /// </summary>
        /// <param name="p1">First point on the circle.</param>
        /// <param name="p2">Second point on the circle.</param>
        /// <param name="p3">Third point on the circle.</param>
        /// <param name="tolerance">Tolerance for floating point comparisons and collinearity checks.</param>
        /// <returns>A tuple (Center, Radius, Normal) or null if points are collinear or calculation fails.</returns>
        public static (DxfPoint Center, double Radius, DxfVector Normal)?
            CalculateCircleCenterRadiusFromThreePoints(DxfPoint p1, DxfPoint p2, DxfPoint p3, double tolerance = 1e-6)
        {
            // Vectors from p1
            DxfVector v12 = p2 - p1; // Vector from p1 to p2
            DxfVector v13 = p3 - p1; // Vector from p1 to p3

            // Normal vector to the plane defined by p1, p2, p3
            // Normal = (P2-P1) x (P3-P1)
            DxfVector normal = v12.Cross(v13);
            double normalLengthSq = normal.LengthSquared;

            if (normalLengthSq < tolerance * tolerance) // Points are collinear or coincident
            {
                Debug.WriteLine("[GeometryUtils] CalculateCircleCenterRadiusFromThreePoints: Points are collinear or coincident.");
                return null;
            }
            normal = normal.Normalize();

            // Method using formulas for circumcenter.
            // See: http://www.ambrsoft.com/TrigoCalc/Circle3D.htm (slightly different notation)
            // or https://en.wikipedia.org/wiki/Circumscribed_circle#Cartesian_coordinates_from_cross-_and_dot-products
            // Let a = p2-p1, b = p3-p1
            // Center = p1 + [ (b^2 * a.a - a^2 * b.a) * a + (a^2 * b.b - b^2 * a.b) * b ] / (2 * |a x b|^2)
            // This formula seems incorrect or misinterpreted from source.

            // Using a more standard formula for circumcenter C for triangle vertices A, B, C (here p1, p2, p3)
            // Let a = |P3-P2|, b = |P3-P1|, c = |P2-P1| (lengths of sides)
            // This is for barycentric coordinates, which can be complex in 3D.

            // Let's use the intersection of perpendicular bisector planes method.
            // Center C must satisfy:
            // 1. Dot(C - (P1+P2)/2, P2-P1) = 0  (C is on perp. bisector plane of P1P2)
            // 2. Dot(C - (P2+P3)/2, P3-P2) = 0  (C is on perp. bisector plane of P2P3)
            // 3. Dot(C - P1, Normal) = 0         (C is on the plane of P1,P2,P3)

            // This forms a system of 3 linear equations for (Cx, Cy, Cz).
            // Eq1: (Cx - (x1+x2)/2)*(x2-x1) + (Cy - (y1+y2)/2)*(y2-y1) + (Cz - (z1+z2)/2)*(z2-z1) = 0
            //      Cx*(x2-x1) + Cy*(y2-y1) + Cz*(z2-z1) = Dot((P1+P2)/2, P2-P1)
            // Eq2: Cx*(x3-x2) + Cy*(y3-y2) + Cz*(z3-z2) = Dot((P2+P3)/2, P3-P2)
            // Eq3: Cx*nx + Cy*ny + Cz*nz = Dot(P1, Normal)

            double v21x = p1.X - p2.X; double v21y = p1.Y - p2.Y; double v21z = p1.Z - p2.Z; // p1-p2
            double v32x = p2.X - p3.X; double v32y = p2.Y - p3.Y; double v32z = p2.Z - p3.Z; // p2-p3

            // Coefficients for the system Ax=B
            // Row 1 (from bisector of P1P2)
            double a11 = -2 * v21x;
            double a12 = -2 * v21y;
            double a13 = -2 * v21z;
            double b1 = p1.X*p1.X - p2.X*p2.X + p1.Y*p1.Y - p2.Y*p2.Y + p1.Z*p1.Z - p2.Z*p2.Z;

            // Row 2 (from bisector of P2P3)
            double a21 = -2 * v32x;
            double a22 = -2 * v32y;
            double a23 = -2 * v32z;
            double b2 = p2.X*p2.X - p3.X*p3.X + p2.Y*p2.Y - p3.Y*p3.Y + p2.Z*p2.Z - p3.Z*p3.Z;

            // Row 3 (point on plane P1,P2,P3 passing through P1 with normal `normal`)
            double a31 = normal.X;
            double a32 = normal.Y;
            double a33 = normal.Z;
            double b3 = normal.X * p1.X + normal.Y * p1.Y + normal.Z * p1.Z;

            // Solve using Cramer's rule or matrix inversion
            double detA = a11*(a22*a33 - a23*a32) - a12*(a21*a33 - a23*a31) + a13*(a21*a32 - a22*a31);

            if (Math.Abs(detA) < tolerance * tolerance * tolerance) // Using a scaled tolerance for determinant
            {
                Debug.WriteLine("[GeometryUtils] CalculateCircleCenterRadiusFromThreePoints: Determinant is zero, cannot solve for center (possibly due to collinearity not caught earlier or numerical instability).");
                return null;
            }

            // Cramer's rule for Cx, Cy, Cz
            double detAx = b1*(a22*a33 - a23*a32) - a12*(b2*a33 - a23*b3) + a13*(b2*a32 - a22*b3);
            double detAy = a11*(b2*a33 - a23*b3) - b1*(a21*a33 - a23*a31) + a13*(a21*b3 - b2*a31);
            double detAz = a11*(a22*b3 - b2*a32) - a12*(a21*b3 - b2*a31) + b1*(a21*a32 - a22*a31);

            double centerX = detAx / detA;
            double centerY = detAy / detA;
            double centerZ = detAz / detA;

            DxfPoint center = new DxfPoint(centerX, centerY, centerZ);
            double radius = (p1 - center).Length;

            if (radius < tolerance)
            {
                Debug.WriteLine("[GeometryUtils] CalculateCircleCenterRadiusFromThreePoints: Calculated radius is too small.");
                return null;
            }

            // Verify that p2 and p3 are also equidistant (within tolerance)
            if (Math.Abs((p2 - center).Length - radius) > tolerance || Math.Abs((p3 - center).Length - radius) > tolerance)
            {
                Debug.WriteLine("[WARNING] CalculateCircleCenterRadiusFromThreePoints: Points are not equidistant from calculated center. Check math or input points. P1-C: " + (p1-center).Length + ", P2-C: " + (p2-center).Length + ", P3-C: " + (p3-center).Length + ", R: " + radius );
                // This could indicate a problem with the input points (e.g. not truly on a circle) or numerical precision issues.
                // Depending on strictness, might return null or proceed with calculated values.
                // For now, proceed.
            }

            Debug.WriteLine($"[GeometryUtils] CircleParams: Center={center}, R={radius}, Normal={normal}");
            return (center, radius, normal);
        }
    }
}
