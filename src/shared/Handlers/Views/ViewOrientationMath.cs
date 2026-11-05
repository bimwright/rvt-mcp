using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Input parsing and camera-vector arithmetic behind revit_set_view_orientation and
    /// revit_create_camera_view. No Revit types, so it is unit-tested without Revit.
    /// </summary>
    public static class ViewOrientationMath
    {
        /// <summary>
        /// Reads a [x,y,z] array of three finite numbers. Returns null with no error when the key is
        /// absent and optional.
        /// </summary>
        public static double[] ReadVector(JObject request, string key, bool required, string unit, out string error)
        {
            error = null;
            var token = request[key];
            if (token == null || token.Type == JTokenType.Null)
            {
                if (required) error = key + " is required as [x,y,z]" + unit + ".";
                return null;
            }

            var array = token as JArray;
            if (array == null || array.Count != 3 || !array.All(t => t.Type == JTokenType.Integer || t.Type == JTokenType.Float))
            {
                error = key + " must be [x,y,z] of three finite numbers" + unit + ".";
                return null;
            }

            var values = array.Select(t => t.Value<double>()).ToArray();
            if (values.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
            {
                error = key + " must be [x,y,z] of three finite numbers" + unit + ".";
                return null;
            }
            return values;
        }

        /// <summary>
        /// Unit up vector perpendicular to <paramref name="forward"/>. Tries the requested up, then +Z,
        /// +Y and +X, so looking straight down still gets a valid camera. Null when forward is zero.
        /// </summary>
        public static double[] PerpendicularUp(double[] forward, double[] up)
        {
            var f = Normalize(forward);
            if (f == null) return null;
            foreach (var candidate in new[] { up, new double[] { 0, 0, 1 }, new double[] { 0, 1, 0 }, new double[] { 1, 0, 0 } })
            {
                if (candidate == null || Length(candidate) < 1e-9) continue;
                var along = Dot(candidate, f);
                var trueUp = Normalize(new[] { candidate[0] - f[0] * along, candidate[1] - f[1] * along, candidate[2] - f[2] * along });
                if (trueUp != null) return trueUp;
            }
            return null;
        }

        public static double[] Normalize(double[] v)
        {
            var length = Length(v);
            if (length < 1e-6) return null;
            return new[] { v[0] / length, v[1] / length, v[2] / length };
        }

        private static double Length(double[] v) => Math.Sqrt(Dot(v, v));

        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    }
}
