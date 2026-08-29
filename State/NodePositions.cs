using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Msagl.Core.Geometry;

namespace DependencyGraph.State
{
    /// <summary>
    /// Remembers where the user dragged nodes and folder boxes to, one file per solution, so an
    /// arrangement survives closing the solution and closing Visual Studio.
    /// </summary>
    /// <remarks>
    /// Kept under LocalApplicationData rather than beside the solution: this is one person's view
    /// state, and writing files into somebody's repository - even ignored ones - is not this
    /// extension's business. Keyed by the solution's full path, so two solutions never share a
    /// file and a solution keeps its arrangement wherever it is opened from. Moving or renaming
    /// the solution loses it, which is the right trade for not touching the source tree.
    ///
    /// The format is one line per node instead of JSON to avoid a serializer dependency for six
    /// hundred bytes of numbers, and every read is defensive: a file that has been hand-edited,
    /// truncated or written by a later version is ignored rather than allowed to throw on the way
    /// to drawing a graph.
    /// </remarks>
    internal static class NodePositions
    {
        private const string Header = "# DependencyGraph node offsets v1";

        /// <summary>
        /// Guard against a runaway file: an offset is only useful while its node still exists, and
        /// a solution the size of this cap has other problems.
        /// </summary>
        private const int MaxEntries = 5000;

        /// <summary>The offsets stored for a solution, empty when there are none.</summary>
        public static Dictionary<string, Point> Load(string solutionPath)
        {
            var offsets = new Dictionary<string, Point>();

            string file = FileFor(solutionPath);
            if (file == null || !File.Exists(file)) return offsets;

            string[] lines;
            try { lines = File.ReadAllLines(file, Encoding.UTF8); }
            catch (IOException) { return offsets; }
            catch (UnauthorizedAccessException) { return offsets; }

            foreach (string line in lines)
            {
                if (line.Length == 0 || line[0] == '#') continue;

                // id last, and split on the first two tabs only: a project key is a path and can
                // contain anything else, but not a tab.
                int first = line.IndexOf('\t');
                if (first <= 0) continue;
                int second = line.IndexOf('\t', first + 1);
                if (second <= first + 1 || second == line.Length - 1) continue;

                if (!double.TryParse(line.Substring(0, first), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double x)) continue;
                if (!double.TryParse(line.Substring(first + 1, second - first - 1),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out double y)) continue;

                // A stored offset has to be a real number: NaN or infinity would put a node
                // nowhere and take the layout with it.
                if (double.IsNaN(x) || double.IsNaN(y)
                    || double.IsInfinity(x) || double.IsInfinity(y)) continue;

                offsets[line.Substring(second + 1)] = new Point(x, y);
                if (offsets.Count >= MaxEntries) break;
            }

            return offsets;
        }

        /// <summary>
        /// Writes the offsets for a solution, or deletes the file when there are none left.
        /// </summary>
        public static void Save(string solutionPath, IDictionary<string, Point> offsets)
        {
            string file = FileFor(solutionPath);
            if (file == null) return;

            try
            {
                if (offsets == null || offsets.Count == 0)
                {
                    if (File.Exists(file)) File.Delete(file);
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(file));

                var text = new StringBuilder();
                text.AppendLine(Header);
                text.AppendLine("# " + solutionPath);

                int written = 0;
                foreach (var pair in offsets)
                {
                    // A tab in the id would come back as part of the numbers, so drop it rather
                    // than write a line that cannot be read.
                    if (string.IsNullOrEmpty(pair.Key) || pair.Key.IndexOf('\t') >= 0) continue;

                    text.Append(pair.Value.X.ToString("R", CultureInfo.InvariantCulture));
                    text.Append('\t');
                    text.Append(pair.Value.Y.ToString("R", CultureInfo.InvariantCulture));
                    text.Append('\t');
                    text.AppendLine(pair.Key);

                    if (++written >= MaxEntries) break;
                }

                File.WriteAllText(file, text.ToString(), Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>
        /// Where a solution's offsets live, or null if there is no solution to key on.
        /// </summary>
        /// <remarks>
        /// The solution's own name is in the file name so the folder can be read by eye, with a
        /// hash of the full path after it because two solutions can share a name.
        /// </remarks>
        public static string FileFor(string solutionPath)
        {
            if (string.IsNullOrEmpty(solutionPath)) return null;

            string name;
            try { name = Path.GetFileNameWithoutExtension(solutionPath); }
            catch (ArgumentException) { return null; }

            // Lower-cased along with the hash below. Windows will hand back whatever casing the
            // solution was opened with - "d:\build\..." one day, "D:\Build\..." the next - and a
            // file name that follows the casing would look like a different solution each time.
            name = name.ToLowerInvariant();

            foreach (char bad in Path.GetInvalidFileNameChars())
                name = name.Replace(bad, '_');
            if (name.Length > 40) name = name.Substring(0, 40);

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DependencyGraph", "positions", name + "-" + Fingerprint(solutionPath) + ".txt");
        }

        /// <summary>
        /// A short, stable stand-in for the solution's path.
        /// </summary>
        /// <remarks>
        /// String.GetHashCode would have done the job but is not guaranteed stable between runs or
        /// between processes, so a file written today could be unreachable tomorrow.
        /// </remarks>
        private static string Fingerprint(string solutionPath)
        {
            using (var sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(
                    Encoding.UTF8.GetBytes(solutionPath.ToLowerInvariant()));

                var text = new StringBuilder(16);
                for (int i = 0; i < 8; i++) text.Append(hash[i].ToString("x2"));
                return text.ToString();
            }
        }
    }
}
