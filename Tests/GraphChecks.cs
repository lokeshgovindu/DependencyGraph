// Offscreen render test for the shipped DependencyGraph UI code.
//
// Drives the REAL GraphBuilder, GraphPalette and ImageSaver out of DependencyGraph.dll against a
// synthetic ProjectReferenceTree (built by reflection, since the real one needs a live EnvDTE
// Project), hosts a real MSAGL GraphViewer, and renders the result to PNG.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Msagl.Drawing;
using Microsoft.Msagl.WpfGraphControl;

public static class RenderTest
{
    static Assembly _dg;
    static Type _prtType, _graphBuilder, _palette, _imageSaver, _visibility;
    static int _failures;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what);
        if (!ok) _failures++;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : ".";
        string dgPath = args.Length > 1 ? args[1] : "DependencyGraph.dll";

        _dg = Assembly.LoadFrom(Path.GetFullPath(dgPath));
        _prtType      = _dg.GetType("DependencyGraph.Scan.ProjectReferenceTree", true);
        _graphBuilder = _dg.GetType("DependencyGraph.UI.GraphBuilder", true);
        _palette      = _dg.GetType("DependencyGraph.UI.GraphPalette", true);
        _imageSaver   = _dg.GetType("DependencyGraph.UI.ImageSaver", true);
        _visibility   = _dg.GetType("DependencyGraph.UI.ConnectionsVisibility", true);
        Console.WriteLine("Loaded " + _dg.GetName().Name + " " + _dg.GetName().Version);

        // GraphPalette.ForVsTheme() must survive with no VS shell present.
        object pal = _palette.GetMethod("ForVsTheme", BindingFlags.Public | BindingFlags.Static)
                             .Invoke(null, null);
        Check(pal != null, "GraphPalette.ForVsTheme() returns a palette outside Visual Studio");
        bool isDark = (bool)_palette.GetProperty("IsDark").GetValue(pal);
        Console.WriteLine("        (fell back to the " + (isDark ? "dark" : "light") + " palette)");

        var root = BuildTree();

        foreach (var dir in new[] { LayerDirection.TB, LayerDirection.LR })
        {
            Console.WriteLine();
            Console.WriteLine("=== LayerDirection." + dir + " ===");
            RenderOne(root, dir, pal, outDir);
        }

        CheckDepthLimit(root, pal);
        CheckCycleDetection(pal);
        CheckImpactView(pal);
        CheckGrouping(root, pal, outDir);
        CheckCollapse(root, pal, outDir);
        CheckDragging(root, pal);
        CheckStoredPositions();
        CheckTooltips();
        CheckSolutionView(pal);

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : _failures + " CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>The tree from Screenshots/1-ShowDeepestReferencesOnly.png.</summary>
    static object BuildTree()
    {
        var all = MakeSet();

        var launcher = NewPrt("SolutionNodesLauncher", 0, all);
        var nodes    = NewPrt("SolutionNodes",         1, all);
        var scan     = NewPrt("SolutionScan",          2, all);
        var x        = NewPrt("X",                     2, all);
        var a        = NewPrt("A",                     3, all);
        var empty    = NewPrt("EmptyProject",          3, all);
        var empty2   = NewPrt("EmptyProject2",         3, all);

        Refs(launcher, nodes);
        Refs(nodes, scan, x, a, empty);
        Refs(scan, a, empty);
        Refs(x, empty2);
        Refs(a, empty);

        return launcher;
    }

    static object MakeSet()
    {
        var setType = typeof(HashSet<>).MakeGenericType(_prtType);
        return Activator.CreateInstance(setType);
    }

    static object NewPrt(string name, int depth, object allSet)
    {
        var ctor = _prtType.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
            null, Type.EmptyTypes, null);
        object prt = ctor.Invoke(null);

        _prtType.GetField("ProjectName").SetValue(prt, name);
        // Key is the graph node id now; in this harness the names are unique so reuse them.
        _prtType.GetProperty("Key").GetSetMethod(true).Invoke(prt, new object[] { name });
        _prtType.GetProperty("DepthLevel").GetSetMethod(true).Invoke(prt, new object[] { depth });
        _prtType.GetProperty("References").SetValue(prt, MakeSet());
        _prtType.GetField("AllPRTs").SetValue(prt, allSet);

        ((IEnumerable)allSet).GetType().GetMethod("Add").Invoke(allSet, new[] { prt });
        return prt;
    }

    static void Refs(object from, params object[] targets)
    {
        object set = _prtType.GetProperty("References").GetValue(from);
        var add = set.GetType().GetMethod("Add");
        foreach (var t in targets) add.Invoke(set, new[] { t });
    }

    static void RenderOne(object root, LayerDirection dir, object pal, string outDir)
    {
        // ConnectionsVisibility.All - the only mode that needs no live EnvDTE Project, since
        // Deepest goes through GetDeepestReference() and the DTE-backed parent map.
        object all = Enum.Parse(_visibility, "All");
        var graph = BuildGraph(root, all, dir, pal, int.MaxValue);

        Check(graph.NodeCount == 7, "GraphBuilder produced 7 nodes (got " + graph.NodeCount + ")");
        Check(graph.EdgeCount == 9, "GraphBuilder produced 9 edges (got " + graph.EdgeCount + ")");

        var rootNode = graph.FindNode("SolutionNodesLauncher");
        Check(rootNode != null, "root node is present");
        Check(rootNode != null && rootNode.UserData == root, "node UserData round-trips to its project");

        // Colour the root differently from the rest - proves the palette reached the graph.
        var other = graph.FindNode("EmptyProject");
        Check(rootNode != null && other != null
              && !rootNode.Attr.FillColor.Equals(other.Attr.FillColor),
            "root node is filled differently from a non-root node");

        // Host a real GraphViewer and lay the graph out.
        var host = new Grid { Width = 1100, Height = 760, Background = System.Windows.Media.Brushes.White };
        var viewer = new GraphViewer { LayoutEditingEnabled = false, RunLayoutAsync = false };
        viewer.BindToPanel(host);

        host.Measure(new Size(1100, 760));
        host.Arrange(new Rect(0, 0, 1100, 760));
        host.UpdateLayout();

        viewer.Graph = graph;

        host.Measure(new Size(1100, 760));
        host.Arrange(new Rect(0, 0, 1100, 760));
        host.UpdateLayout();
        viewer.SetInitialTransform();
        host.UpdateLayout();

        Check(graph.GeometryGraph != null, "MSAGL computed a geometry graph (layout ran)");
        if (graph.GeometryGraph != null)
        {
            double w = graph.Width, h = graph.Height;
            Console.WriteLine(string.Format("        graph bounds: {0:0} x {1:0}", w, h));
            Check(w > 0 && h > 0, "graph has positive bounds");

            bool tallerThanWide = h > w;
            Check(dir == LayerDirection.TB ? tallerThanWide : !tallerThanWide,
                dir == LayerDirection.TB
                    ? "TB layout is taller than it is wide"
                    : "LR layout is wider than it is tall");

            // Depth must advance along the expected axis only.
            var rootPos = graph.FindNode("SolutionNodesLauncher").Pos;
            var leafPos = graph.FindNode("EmptyProject2").Pos;
            if (dir == LayerDirection.TB)
                Check(Math.Abs(leafPos.Y - rootPos.Y) > Math.Abs(leafPos.X - rootPos.X),
                    "TB: root->leaf separation is mostly vertical");
            else
                Check(Math.Abs(leafPos.X - rootPos.X) > Math.Abs(leafPos.Y - rootPos.Y),
                    "LR: root->leaf separation is mostly horizontal");
        }

        // Now the real ImageSaver, on the real GraphCanvas.
        string file = Path.Combine(outDir, "graph-" + dir + ".png");
        // Same ExportBounds the tool window uses, so this exercises the shipping path.
        var exportBounds = _graphBuilder.GetMethod("ExportBounds",
            BindingFlags.Public | BindingFlags.Static);
        var bounds = (Rect)exportBounds.Invoke(null, new object[] { graph, viewer });
        Console.WriteLine(string.Format("        export bounds: ({0:0.0},{1:0.0} {2:0.0}x{3:0.0})",
            bounds.X, bounds.Y, bounds.Width, bounds.Height));
        Check(!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0,
            "GraphBuilder.ExportBounds returned a usable rectangle");

        var asPng = _imageSaver.GetMethod("AsPNG",
            BindingFlags.Public | BindingFlags.Static, null,
            new[] { typeof(FrameworkElement), typeof(string), typeof(Rect) }, null);
        bool saved = (bool)asPng.Invoke(null, new object[] { viewer.GraphCanvas, file, bounds });

        Check(saved, "ImageSaver.AsPNG reported success");
        Check(File.Exists(file) && new FileInfo(file).Length > 3000,
            "PNG written and non-trivial in size ("
            + (File.Exists(file) ? new FileInfo(file).Length.ToString("N0") : "0") + " bytes)");

        if (File.Exists(file))
        {
            var img = new System.Windows.Media.Imaging.BitmapImage(new Uri(Path.GetFullPath(file)));
            Console.WriteLine(string.Format("        png: {0} x {1}", img.PixelWidth, img.PixelHeight));

            // The whole graph must be captured, not just the visible viewport...
            Check(img.PixelWidth >= graph.Width && img.PixelHeight >= graph.Height,
                "PNG covers the full graph extent, not just the viewport");

            // ...and cropped to it. The capture is at the on-screen zoom, so compare shape
            // rather than absolute size: a tight crop has the graph aspect ratio, whereas
            // viewport padding would distort it.
            double graphAspect = graph.Width / graph.Height;
            double pngAspect = (double)img.PixelWidth / img.PixelHeight;
            Check(Math.Abs(pngAspect - graphAspect) / graphAspect < 0.30,
                string.Format("PNG is cropped tight to the graph (aspect {0:0.00} vs graph {1:0.00})",
                    pngAspect, graphAspect));

            Check(!IsBlank(file), "PNG actually contains drawn content (not blank)");
        }
    }

    static void DumpCanvas(System.Windows.Controls.Canvas c)
    {
        Console.WriteLine("        canvas: RenderTransform=" + c.RenderTransform.Value
            + "  children=" + System.Windows.Media.VisualTreeHelper.GetChildrenCount(c));
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(c);
        for (int i = 0; i < n; ++i)
        {
            var ch = System.Windows.Media.VisualTreeHelper.GetChild(c, i) as FrameworkElement;
            if (ch == null) { Console.WriteLine("          [" + i + "] (not FrameworkElement)"); continue; }
            var b = System.Windows.Media.VisualTreeHelper.GetDescendantBounds(ch);
            Rect inParent = Rect.Empty;
            try { if (!b.IsEmpty) inParent = ch.TransformToAncestor(c).TransformBounds(b); } catch { }
            Console.WriteLine(string.Format(
                "          [{0}] {1,-14} rt={2,-5} local=({3:0.0},{4:0.0} {5:0.0}x{6:0.0}) inCanvas=({7:0.0},{8:0.0} {9:0.0}x{10:0.0})",
                i, ch.GetType().Name,
                ch.RenderTransform == null ? "null" : (ch.RenderTransform.Value.IsIdentity ? "id" : "XFRM"),
                b.IsEmpty ? 0 : b.X, b.IsEmpty ? 0 : b.Y, b.IsEmpty ? 0 : b.Width, b.IsEmpty ? 0 : b.Height,
                inParent.IsEmpty ? 0 : inParent.X, inParent.IsEmpty ? 0 : inParent.Y,
                inParent.IsEmpty ? 0 : inParent.Width, inParent.IsEmpty ? 0 : inParent.Height));
        }
    }

    /// <summary>True when every pixel is the same colour, i.e. nothing was actually drawn.</summary>
    static bool IsBlank(string file)
    {
        var img = new System.Windows.Media.Imaging.BitmapImage(new Uri(Path.GetFullPath(file)));
        int stride = img.PixelWidth * 4;
        var px = new byte[stride * img.PixelHeight];
        var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(
            img, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        conv.CopyPixels(px, stride, 0);

        var distinct = new HashSet<int>();
        for (int i = 0; i + 3 < px.Length; i += 4)
        {
            distinct.Add((px[i] << 16) | (px[i + 1] << 8) | px[i + 2]);
            if (distinct.Count > 4) return false;
        }
        Console.WriteLine("        distinct colours: " + distinct.Count);
        return distinct.Count <= 1;
    }

    /// <summary>Depth limiting must cut whole levels, edges included.</summary>
    static void CheckDepthLimit(object root, object pal)
    {
        Console.WriteLine();
        Console.WriteLine("=== depth limit ===");

        object all = Enum.Parse(_visibility, "All");

        // Breadth-first distance from the root, which is what the depth control means:
        //   0 hops  launcher
        //   1 hop   SolutionNodes
        //   2 hops  SolutionScan, X, A, EmptyProject  (all referenced by SolutionNodes)
        //   3 hops  EmptyProject2                    (only reachable via X)
        var expected = new Dictionary<int, int> { { 0, 1 }, { 1, 2 }, { 2, 6 }, { 3, 7 } };

        foreach (var kv in expected)
        {
            var g = BuildGraph(root, all, LayerDirection.TB, pal, kv.Key);
            Check(g.NodeCount == kv.Value,
                string.Format("depth {0} keeps {1} nodes (got {2})", kv.Key, kv.Value, g.NodeCount));

            // No edge may point at a node that was cut.
            bool dangling = false;
            foreach (var e in g.Edges)
                if (g.FindNode(e.Source) == null || g.FindNode(e.Target) == null) dangling = true;
            Check(!dangling, string.Format("depth {0} leaves no dangling edges", kv.Key));
        }
    }

    /// <summary>A reference that closes a cycle must be drawn in the cycle colour.</summary>
    static void CheckCycleDetection(object pal)
    {
        Console.WriteLine();
        Console.WriteLine("=== cycle detection ===");

        var all = MakeSet();
        var a = NewPrt("Cyclic.A", 0, all);
        var b = NewPrt("Cyclic.B", 1, all);
        Refs(a, b);
        Refs(b, a);          // hand-edited csproj territory: VS would refuse this in the UI

        object vis = Enum.Parse(_visibility, "All");
        var g = BuildGraph(a, vis, LayerDirection.TB, pal, int.MaxValue);

        Check(g.NodeCount == 2 && g.EdgeCount == 2,
            string.Format("cyclic pair gives 2 nodes and 2 edges (got {0}/{1})", g.NodeCount, g.EdgeCount));

        var cycleColour = _palette.GetProperty("CycleEdge").GetValue(pal);
        int flagged = 0;
        foreach (var e in g.Edges)
            if (e.Attr.Color.Equals(cycleColour)) flagged++;

        Check(flagged == 1,
            string.Format("exactly one edge is flagged as the cycle (got {0})", flagged));

        bool labelled = false;
        foreach (var e in g.Edges) if (e.LabelText == "cycle") labelled = true;
        Check(labelled, "the cycle edge carries a label");
    }

    /// <summary>
    /// The impact view: every project that reaches the root, with the real edges between them.
    /// Built by reflection over a synthetic solution, since the real factory needs EnvDTE.
    /// </summary>
    static void CheckImpactView(object pal)
    {
        Console.WriteLine();
        Console.WriteLine("=== impact view (referenced by) ===");

        // Core is referenced by Services and Ui; Services by App; Ui by nothing.
        // So everything reaches Core, but only App reaches Services.
        var all = MakeSet();
        var core     = NewPrt("Core",     0, all);
        var services = NewPrt("Services", 1, all);
        var ui       = NewPrt("Ui",       1, all);
        var app      = NewPrt("App",      2, all);
        Refs(services, core);
        Refs(ui, core);
        Refs(app, services);

        object vis = Enum.Parse(_visibility, "ReferencedBy");

        // No depth limit: all four reach Core.
        var g = BuildGraph(core, vis, LayerDirection.TB, pal, int.MaxValue);
        Check(g.NodeCount == 4, "impact of Core includes all 4 projects (got " + g.NodeCount + ")");
        Check(g.EdgeCount == 3, "and the 3 real reference edges (got " + g.EdgeCount + ")");

        // Arrows must still mean "depends on": Services -> Core, never Core -> Services.
        bool correctDirection = true;
        foreach (var e in g.Edges)
            if (e.Source == "Core") correctDirection = false;
        Check(correctDirection, "arrows point at the dependency, so the root is a sink");

        // Depth 1 walks backwards one hop: Core plus its direct referencers.
        var g1 = BuildGraph(core, vis, LayerDirection.TB, pal, 1);
        Check(g1.NodeCount == 3, "depth 1 gives Core + its 2 direct referencers (got " + g1.NodeCount + ")");
    }

    /// <summary>
    /// Grouping by solution folder: a box per folder, each carrying the folder's name.
    /// </summary>
    /// <remarks>
    /// The name is the point of the box - an unlabelled rectangle tells you nothing - and MSAGL
    /// draws a subgraph's label only if the label has been given a size, so this renders the
    /// graph for real and looks for the text in the visual tree.
    /// </remarks>
    static void CheckGrouping(object root, object pal, string outDir)
    {
        Console.WriteLine();
        Console.WriteLine("=== grouping by solution folder ===");

        var folders = new Dictionary<string, string>
        {
            { "SolutionNodes",  "Components"   },
            { "SolutionScan",   "Components"   },
            { "X",              "Presentation" },
            { "EmptyProject2",  "Presentation" },
        };
        Func<string, string> folderOf = key =>
        {
            string folder;
            return folders.TryGetValue(key, out folder) ? folder : null;
        };

        object all = Enum.Parse(_visibility, "All");
        var graph = BuildGraph(root, all, LayerDirection.TB, pal, int.MaxValue, folderOf, null);

        var subgraphs = new List<Subgraph>();
        if (graph.RootSubgraph != null)
            foreach (var s in graph.RootSubgraph.AllSubgraphsDepthFirstExcludingSelf())
                subgraphs.Add(s);

        Check(subgraphs.Count == 2, "two folder boxes were created (got " + subgraphs.Count + ")");
        foreach (var s in subgraphs)
            Check(s.LabelText == s.Id, "box '" + s.Id + "' carries its folder name as the label");

        // Size on the label is what makes MSAGL reserve a top margin for it and draw the text.
        foreach (var s in subgraphs)
            Check(s.Label != null && s.Label.Width > 0 && s.Label.Height > 0,
                "box '" + s.Id + "' label has a measured size ("
                + (s.Label == null ? "no label" : s.Label.Width.ToString("0") + "x" + s.Label.Height.ToString("0")) + ")");

        var host = new Grid { Width = 1100, Height = 760, Background = System.Windows.Media.Brushes.White };
        var viewer = new GraphViewer { LayoutEditingEnabled = false, RunLayoutAsync = false };
        viewer.BindToPanel(host);
        host.Measure(new Size(1100, 760));
        host.Arrange(new Rect(0, 0, 1100, 760));
        host.UpdateLayout();

        var registerLabels = _graphBuilder.GetMethod("RegisterSubgraphLabels",
            BindingFlags.Public | BindingFlags.Static);
        object folderNames = registerLabels.Invoke(null, new object[] { graph, viewer });
        Check(folderNames != null, "RegisterSubgraphLabels returned the folder-name set");

        viewer.Graph = graph;
        folderNames.GetType().GetMethod("Reposition").Invoke(folderNames, null);
        host.Measure(new Size(1100, 760));
        host.Arrange(new Rect(0, 0, 1100, 760));
        host.UpdateLayout();
        viewer.SetInitialTransform();
        host.UpdateLayout();

        var texts = new List<string>();
        CollectTexts(viewer.GraphCanvas, texts);
        foreach (var s in subgraphs)
            Check(texts.Contains(s.Id), "'" + s.Id + "' is drawn on the canvas");

        // The name has to end up in the band across the top of its box, not across the middle of
        // its own contents - which is where the viewer puts a node's label by default.
        var byText = new Dictionary<string, TextBlock>();
        foreach (var tb in FindTextBlocks(viewer.GraphCanvas))
            if (!byText.ContainsKey(tb.Text)) byText[tb.Text] = tb;

        foreach (var s in subgraphs)
        {
            var cluster = s.GeometryNode as Microsoft.Msagl.Core.Layout.Cluster;
            var boundary = cluster == null ? null : cluster.RectangularBoundary;
            Check(boundary != null && boundary.TopMargin >= s.Label.Height,
                "box '" + s.Id + "' reserves a band for its name (top margin "
                + (boundary == null ? "none" : boundary.TopMargin.ToString("0")) + ")");

            TextBlock element;
            if (cluster == null || !byText.TryGetValue(s.Id, out element)) continue;

            // The element's transform carries MSAGL's y flip, so its offset is the label's
            // bottom edge in graph coordinates.
            double bottom = element.RenderTransform.Value.OffsetY;
            double top = bottom - element.Height;
            Check(top >= cluster.BoundingBox.Top - boundary.TopMargin - 1
                  && bottom <= cluster.BoundingBox.Top + 1,
                "'" + s.Id + "' sits inside that band, clear of the projects in it");
        }

        string file = Path.Combine(outDir, "graph-grouped.png");
        var exportBounds = _graphBuilder.GetMethod("ExportBounds",
            BindingFlags.Public | BindingFlags.Static);
        var bounds = (Rect)exportBounds.Invoke(null, new object[] { graph, viewer });
        var asPng = _imageSaver.GetMethod("AsPNG",
            BindingFlags.Public | BindingFlags.Static, null,
            new[] { typeof(FrameworkElement), typeof(string), typeof(Rect) }, null);
        asPng.Invoke(null, new object[] { viewer.GraphCanvas, file, bounds });
        Console.WriteLine("        wrote " + file);
        Console.WriteLine("        canvas text: " + string.Join(" | ", texts.ToArray()));
    }

    /// <summary>
    /// Collapsing a solution folder down to one node, and expanding it again.
    /// </summary>
    /// <remarks>
    /// MSAGL's own collapse button only flips a flag on the geometry cluster and leaves the
    /// layout untouched, so collapsing is done by rebuilding the graph with the folder as a
    /// single node. Which means the thing to check is that a collapse aggregates the folder's
    /// references onto that node, and that expanding again gives back exactly the graph that was
    /// there before - the round trip is what was broken when the button was MSAGL's.
    /// </remarks>
    static void CheckCollapse(object root, object pal, string outDir)
    {
        Console.WriteLine();
        Console.WriteLine("=== collapsing a solution folder ===");

        var folders = new Dictionary<string, string>
        {
            { "SolutionNodes",  "Components"   },
            { "SolutionScan",   "Components"   },
            { "X",              "Presentation" },
            { "EmptyProject2",  "Presentation" },
        };
        Func<string, string> folderOf = key =>
        {
            string folder;
            return folders.TryGetValue(key, out folder) ? folder : null;
        };

        object all = Enum.Parse(_visibility, "All");

        var expanded = BuildGraph(root, all, LayerDirection.TB, pal, int.MaxValue, folderOf, null, null);
        var collapsed = BuildGraph(root, all, LayerDirection.TB, pal, int.MaxValue, folderOf, null,
            new List<string> { "Components" });

        // The two projects in Components give up their nodes to one node standing for the folder.
        Check(collapsed.NodeCount == expanded.NodeCount - 1,
            "collapsing Components replaces its 2 projects with 1 node ("
            + expanded.NodeCount + " -> " + collapsed.NodeCount + ")");

        Check(collapsed.FindNode("SolutionNodes") == null && collapsed.FindNode("SolutionScan") == null,
            "the projects inside it are no longer drawn separately");

        var folderNode = collapsed.FindNode("folder:Components");
        Check(folderNode != null, "a node stands in for the folder");
        Check(folderNode != null && folderNode.LabelText.Contains("Components")
              && folderNode.LabelText.Contains("2 projects"),
            "and says which folder it is and how many projects are in it ("
            + (folderNode == null ? "no node" : folderNode.LabelText) + ")");

        // Components is not itself boxed any more; Presentation still is.
        var boxes = new List<string>();
        if (collapsed.RootSubgraph != null)
            foreach (var s in collapsed.RootSubgraph.AllSubgraphsDepthFirstExcludingSelf())
                boxes.Add(s.Id);
        Check(boxes.Count == 1 && boxes[0] == "Presentation",
            "no box is drawn for a collapsed folder (boxes: " + string.Join(",", boxes.ToArray()) + ")");

        // Every reference that crossed the folder boundary has to survive, pointed at the folder
        // node instead - and references between the two projects inside it must not become a
        // self-loop.
        foreach (var edge in collapsed.Edges)
            Check(edge.Source != edge.Target,
                "no self-loop from references inside the folder (" + edge.Source + " -> " + edge.Target + ")");

        var launcherEdges = new List<string>();
        foreach (var edge in collapsed.Edges)
            if (edge.Source == "SolutionNodesLauncher") launcherEdges.Add(edge.Target);
        Check(launcherEdges.Count == 1 && launcherEdges[0] == "folder:Components",
            "a reference into the folder now points at the folder node (got "
            + string.Join(",", launcherEdges.ToArray()) + ")");

        Check(GraphBuilderCollapsedFolderOf(folderNode) == "Components",
            "the folder node reports which folder it is, so clicking it can expand it");
        Check(GraphBuilderCollapsedFolderOf(collapsed.FindNode("EmptyProject")) == null,
            "an ordinary project node reports no folder");

        // Expanding again: same node and edge counts as before it was ever collapsed.
        var reexpanded = BuildGraph(root, all, LayerDirection.TB, pal, int.MaxValue, folderOf, null,
            new List<string>());
        Check(reexpanded.NodeCount == expanded.NodeCount
              && reexpanded.EdgeCount == expanded.EdgeCount,
            "expanding restores the graph exactly (" + reexpanded.NodeCount + " nodes, "
            + reexpanded.EdgeCount + " edges vs " + expanded.NodeCount + "/" + expanded.EdgeCount + ")");
    }

    /// <summary>
    /// Dragging a node, and dragging a whole folder box.
    /// </summary>
    /// <remarks>
    /// The point of covering this offscreen is that the alternative - MSAGL's own LayoutEditor -
    /// killed the IDE outright on the first drag, so the replacement wants checking somewhere it
    /// can be watched.
    /// </remarks>
    static void CheckDragging(object root, object pal)
    {
        Console.WriteLine();
        Console.WriteLine("=== dragging ===");

        var folders = new Dictionary<string, string>
        {
            { "SolutionNodes",  "Components"   },
            { "SolutionScan",   "Components"   },
            { "X",              "Presentation" },
            { "EmptyProject2",  "Presentation" },
        };
        Func<string, string> folderOf = key =>
        {
            string folder;
            return folders.TryGetValue(key, out folder) ? folder : null;
        };

        object all = Enum.Parse(_visibility, "All");
        var graph = BuildGraph(root, all, LayerDirection.TB, pal, int.MaxValue, folderOf, null, null);

        var host = new Grid { Width = 1100, Height = 760, Background = System.Windows.Media.Brushes.White };
        var viewer = new GraphViewer { LayoutEditingEnabled = false, RunLayoutAsync = false };
        viewer.BindToPanel(host);
        host.Measure(new Size(1100, 760));
        host.Arrange(new Rect(0, 0, 1100, 760));
        host.UpdateLayout();
        viewer.Graph = graph;
        host.UpdateLayout();

        var drag = _dg.GetType("DependencyGraph.UI.GraphDrag", true)
                      .GetMethod("Move", BindingFlags.Public | BindingFlags.Static);
        var delta = new Microsoft.Msagl.Core.Geometry.Point(60, -25);

        // A single project.
        var node = graph.FindNode("A");
        var before = node.GeometryNode.Center;
        object movedResult = drag.Invoke(null, new object[] { graph, node, delta });
        var moved = (System.Collections.ICollection)movedResult.GetType().GetField("Edges").GetValue(movedResult);
        var movedNodes = (System.Collections.ICollection)movedResult.GetType().GetField("Nodes").GetValue(movedResult);

        var after = node.GeometryNode.Center;
        Check(Math.Abs(after.X - (before.X + 60)) < 0.001 && Math.Abs(after.Y - (before.Y - 25)) < 0.001,
            "dragging a project moves it by exactly the drag delta");
        Check(moved.Count > 0, "and reports the references that have to be redrawn (got " + moved.Count + ")");
        Check(movedNodes.Count == 1, "and reports the one node that moved (got " + movedNodes.Count + ")");

        // Every reference reported has to still reach the node's new position, or the arrows come
        // away from the box. The line stops an arrowhead's length short of the boundary, which is
        // where the arrow is then drawn, so allow for that.
        double reach = Microsoft.Msagl.Core.Layout.Arrowhead.DefaultArrowheadLength + 2;
        foreach (Edge edge in moved)
        {
            var geometry = edge.GeometryEdge;
            bool fromMoved = geometry.Source == node.GeometryNode;
            var end = fromMoved ? geometry.Curve.Start : geometry.Curve.End;
            var box = node.GeometryNode.BoundingBox;
            Check(end.X >= box.Left - reach && end.X <= box.Right + reach
                  && end.Y >= box.Bottom - reach && end.Y <= box.Top + reach,
                "reference " + edge.Source + " -> " + edge.Target + " still meets the node it moved");
        }

        // The graph's bounds have to grow with the node, or the dragged node falls outside the
        // rectangle that framing and PNG export are both measured from.
        var dragged = node.GeometryNode.BoundingBox;
        var bounds = graph.BoundingBox;
        Check(bounds.Left <= dragged.Left + 0.001 && bounds.Right >= dragged.Right - 0.001
              && bounds.Bottom <= dragged.Bottom + 0.001 && bounds.Top >= dragged.Top - 0.001,
            "the graph's bounds still contain the node after it was dragged");

        // Nothing else may move.
        var untouched = graph.FindNode("EmptyProject");
        var untouchedBefore = untouched.GeometryNode.Center;
        drag.Invoke(null, new object[] { graph, node, delta });
        Check(untouched.GeometryNode.Center == untouchedBefore,
            "dragging one project leaves the others where they were");

        // A whole folder box: the box and every project in it move together.
        //
        // The subgraph is taken off the viewer's own VNode, the way the control gets it from a
        // mouse event, rather than out of the graph - dragging a box moved the box but left its
        // projects behind, and only the object identity could explain it.
        Subgraph box2 = null;
        foreach (var entity in viewer.Entities)
        {
            var vnode = entity as VNode;
            if (vnode != null && vnode.Node is Subgraph s && s.Id == "Components") box2 = s;
        }
        Check(box2 != null, "the folder box is reachable as a VNode, as the mouse handler sees it");

        var inside = graph.FindNode("SolutionNodes");
        var insideBefore = inside.GeometryNode.Center;
        var boxBefore = box2.GeometryNode.BoundingBox;

        drag.Invoke(null, new object[] { graph, box2, delta });

        object boxResult = drag.Invoke(null, new object[] { graph, box2, delta });
        var boxNodes = (System.Collections.ICollection)boxResult.GetType().GetField("Nodes").GetValue(boxResult);

        Check(Math.Abs(inside.GeometryNode.Center.X - (insideBefore.X + 120)) < 0.001,
            "dragging a folder box moves the projects inside it");

        // Reporting them is what makes them repaint. Missing this is why the first attempt left
        // the projects drawn at their old positions while the box moved away from them.
        bool reportsInside = false;
        foreach (Node n in boxNodes) if (n.Id == "SolutionNodes") reportsInside = true;
        Check(reportsInside, "and reports each of them, so their visuals get repainted (reported "
            + boxNodes.Count + ")");
        Check(Math.Abs(box2.GeometryNode.BoundingBox.Left - (boxBefore.Left + 120)) < 0.001,
            "and the box follows them ("
            + boxBefore.Left.ToString("0.0") + " -> " + box2.GeometryNode.BoundingBox.Left.ToString("0.0") + ")");
        Check(Math.Abs(box2.GeometryNode.BoundingBox.Width - boxBefore.Width) < 0.5,
            "without the box growing (" + boxBefore.Width.ToString("0.0")
            + " -> " + box2.GeometryNode.BoundingBox.Width.ToString("0.0") + ")");
    }

    /// <summary>
    /// Dragged positions surviving on disk, per solution.
    /// </summary>
    /// <remarks>
    /// Project keys are solution-relative paths, so the ids going through this file contain
    /// backslashes, dots and spaces; and the file is in a place a person can open and edit, so a
    /// damaged one has to be ignored rather than allowed to throw on the way to drawing a graph.
    /// </remarks>
    static void CheckStoredPositions()
    {
        Console.WriteLine();
        Console.WriteLine("=== stored positions ===");

        var type = _dg.GetType("DependencyGraph.State.NodePositions", true);
        var load = type.GetMethod("Load", BindingFlags.Public | BindingFlags.Static);
        var save = type.GetMethod("Save", BindingFlags.Public | BindingFlags.Static);
        var fileFor = type.GetMethod("FileFor", BindingFlags.Public | BindingFlags.Static);

        // A path that does not exist is fine - nothing is ever opened from it.
        string solution = @"D:\checks\Contoso Demo\Contoso.Demo.sln";
        string file = (string)fileFor.Invoke(null, new object[] { solution });
        Console.WriteLine("        file: " + file);

        Check(file != null && file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase),
            "a solution maps to a file");
        Check(file != null && file.IndexOf("Contoso.Demo", StringComparison.OrdinalIgnoreCase) >= 0,
            "named after the solution, so the folder can be read by eye");
        Check(file != null && file.StartsWith(
                  Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                  StringComparison.OrdinalIgnoreCase),
            "under LocalApplicationData, not beside the solution");

        // Two solutions of the same name in different folders must not share a file.
        string other = (string)fileFor.Invoke(null, new object[] { @"D:\elsewhere\Contoso.Demo.sln" });
        Check(other != file, "two solutions sharing a name get different files");

        // The same solution must map to the same file every run, whatever the casing.
        string again = (string)fileFor.Invoke(null, new object[] { solution.ToUpperInvariant() });
        Check(again == file, "the mapping is stable and case-insensitive");

        try
        {
            var offsets = new Dictionary<string, Microsoft.Msagl.Core.Geometry.Point>
            {
                { @"Components\Contoso.Core\Contoso.Core.csproj",
                    new Microsoft.Msagl.Core.Geometry.Point(60.5, -25.25) },
                { "Presentation", new Microsoft.Msagl.Core.Geometry.Point(-3, 0.125) },
            };

            save.Invoke(null, new object[] { solution, offsets });
            Check(File.Exists(file), "saving writes the file");

            var read = (System.Collections.IDictionary)load.Invoke(null, new object[] { solution });
            Check(read.Count == 2, "and reading it back gives both entries (got " + read.Count + ")");

            var core = read[@"Components\Contoso.Core\Contoso.Core.csproj"];
            Check(core != null && core.ToString() == offsets[@"Components\Contoso.Core\Contoso.Core.csproj"].ToString(),
                "a project key with backslashes and dots round-trips exactly (" + core + ")");

            var box = read["Presentation"];
            Check(box != null && box.ToString() == offsets["Presentation"].ToString(),
                "so does a folder box, with a fractional offset (" + box + ")");

            // Nothing left to remember means no file left behind.
            save.Invoke(null, new object[] { solution, new Dictionary<string, Microsoft.Msagl.Core.Geometry.Point>() });
            Check(!File.Exists(file), "resetting to nothing deletes the file");

            // A damaged file must read as "no offsets", not throw.
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, "# DependencyGraph node offsets v1\nnot\ta\tnumber\n"
                                    + "NaN\t0\tbad.csproj\n1e400\t0\thuge.csproj\n"
                                    + "\t\t\n12\tgood.csproj\n7\t8\tfine.csproj\n");
            var salvaged = (System.Collections.IDictionary)load.Invoke(null, new object[] { solution });
            Check(salvaged.Count == 1 && salvaged["fine.csproj"] != null,
                "a damaged file yields only the lines that parse (got " + salvaged.Count + ")");

            File.WriteAllBytes(file, new byte[] { 0x00, 0xFF, 0x00, 0xFF });
            var binary = (System.Collections.IDictionary)load.Invoke(null, new object[] { solution });
            Check(binary.Count == 0, "and binary rubbish yields nothing at all rather than throwing");
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }

        var none = (System.Collections.IDictionary)load.Invoke(null, new object[] { null });
        Check(none.Count == 0, "no solution means no offsets, and no exception");
    }

    /// <summary>
    /// The hover tooltips: that the graph surface has one, that its timings are long enough for
    /// text worth reading, and that the text says something true about the node.
    /// </summary>
    /// <remarks>
    /// Only the text is checked here. The wiring that shows it - the tooltip on the graph host and
    /// the ToolTipService timings - lives in the control's XAML, and the control cannot be built
    /// outside Visual Studio, so that part is confirmed by hovering in the IDE.
    /// </remarks>
    static void CheckTooltips()
    {
        Console.WriteLine();
        Console.WriteLine("=== tooltips ===");

        var type = _dg.GetType("DependencyGraph.UI.NodeTooltip", true);
        var forNode = type.GetMethod("For", BindingFlags.Public | BindingFlags.Static);
        Check(forNode != null, "NodeTooltip.For builds the text");
        if (forNode == null) return;

        Check((string)forNode.Invoke(null, new object[] { null, null }) == null,
            "empty canvas gets no tooltip, so none is shown over the background");

        object pal = _palette.GetMethod("ForVsTheme", BindingFlags.Public | BindingFlags.Static)
                             .Invoke(null, null);
        object all = Enum.Parse(_visibility, "All");
        var graph = BuildGraph(BuildTree(), all, LayerDirection.TB, pal, int.MaxValue);

        Func<string, string> folderOf = key => key == "SolutionNodes" ? "Components" : null;

        string text = (string)forNode.Invoke(null,
            new object[] { graph.FindNode("SolutionNodes"), folderOf });
        Console.WriteLine("        node tooltip:");
        foreach (var line in (text ?? "").Split('\n')) Console.WriteLine("          | " + line);

        Check(text != null && text.StartsWith("SolutionNodes", StringComparison.Ordinal),
            "a project's tooltip leads with its name");
        Check(text != null && text.IndexOf("Solution folder: Components", StringComparison.Ordinal) >= 0,
            "and names the solution folder it is in");
        Check(text != null && text.IndexOf("References 4 projects", StringComparison.Ordinal) >= 0,
            "and how many projects it references");
        Check(text != null && text.IndexOf("Referenced by 1 project\n", StringComparison.Ordinal) >= 0,
            "and how many reference it, in the singular when there is one");
        Check(text != null && text.IndexOf("Double-click", StringComparison.Ordinal) >= 0,
            "and what double-clicking does, which nothing else in the window says");

        // Without a scanned solution there is no folder lookup, and the tooltip still has to work.
        string noFolder = (string)forNode.Invoke(null,
            new object[] { graph.FindNode("SolutionNodes"), null });
        Check(noFolder != null && noFolder.IndexOf("Solution folder", StringComparison.Ordinal) < 0,
            "with no folder lookup it simply leaves the folder out");

        string rootText = (string)forNode.Invoke(null,
            new object[] { graph.FindNode("SolutionNodesLauncher"), null });
        Check(rootText != null && rootText.IndexOf("drawn from", StringComparison.Ordinal) >= 0,
            "the root project's tooltip says that is what it is");

        // A collapsed folder node has no project behind it, so it must not fall through to one.
        var collapsedGraph = BuildGraph(BuildTree(), all, LayerDirection.TB, pal, int.MaxValue,
            key => key == "SolutionNodes" || key == "SolutionScan" ? "Components" : null,
            null, new List<string> { "Components" });

        string folderText = (string)forNode.Invoke(null,
            new object[] { collapsedGraph.FindNode("folder:Components"), null });
        Check(folderText != null && folderText.StartsWith("Components", StringComparison.Ordinal)
              && folderText.IndexOf("Click it to expand", StringComparison.Ordinal) >= 0,
            "a collapsed folder's tooltip says what it is and how to open it");
    }

    /// <summary>
    /// The whole-solution view: every project, not only what one project can reach.
    /// </summary>
    /// <remarks>
    /// A solution normally has several entry points - an app, a test host, a command-line tool -
    /// and a view rooted at one of them cannot see the others or anything only they reference.
    /// That is the whole point of this view, so the check is built on a solution shaped that way:
    /// two entry points over a shared dependency.
    /// </remarks>
    static void CheckSolutionView(object pal)
    {
        Console.WriteLine();
        Console.WriteLine("=== whole-solution view ===");

        var all = MakeSet();
        var app  = NewPrt("App",  0, all);
        var tool = NewPrt("Tool", 0, all);
        var core = NewPrt("Core", 1, all);
        var util = NewPrt("Util", 2, all);
        Refs(app, core);
        Refs(tool, core);
        Refs(core, util);

        object allRefs  = Enum.Parse(_visibility, "All");
        object solution = Enum.Parse(_visibility, "Solution");

        // Rooted at App, Tool is invisible - it is not reachable from App.
        var rooted = BuildGraph(app, allRefs, LayerDirection.TB, pal, int.MaxValue);
        Check(rooted.NodeCount == 3 && rooted.FindNode("Tool") == null,
            "a rooted view cannot see the solution's other entry point (got "
            + rooted.NodeCount + " nodes)");

        var whole = BuildGraph(app, solution, LayerDirection.TB, pal, int.MaxValue);
        Check(whole.NodeCount == 4, "the whole-solution view draws every project (got "
            + whole.NodeCount + ")");
        Check(whole.FindNode("Tool") != null, "including the entry point the rooted view missed");
        Check(whole.EdgeCount == 3, "with every reference between them (got " + whole.EdgeCount + ")");

        var edges = new List<string>();
        foreach (var edge in whole.Edges) edges.Add(edge.Source + "->" + edge.Target);
        edges.Sort();
        Check(string.Join(",", edges.ToArray()) == "App->Core,Core->Util,Tool->Core",
            "and the references are the real ones (" + string.Join(" ", edges.ToArray()) + ")");

        // Both entry points are drawn as roots, since neither is referenced by anything.
        Check(!whole.FindNode("App").Attr.FillColor.Equals(whole.FindNode("Core").Attr.FillColor)
              && whole.FindNode("Tool").Attr.FillColor.Equals(whole.FindNode("App").Attr.FillColor),
            "both entry points are filled as roots, and a referenced project is not");

        // Depth counts down from every entry point at once, not from one arbitrary project.
        var shallow = BuildGraph(app, solution, LayerDirection.TB, pal, 1);
        Check(shallow.NodeCount == 3 && shallow.FindNode("Util") == null,
            "depth 1 keeps both entry points and their shared dependency, and stops (got "
            + shallow.NodeCount + ")");

        var justRoots = BuildGraph(app, solution, LayerDirection.TB, pal, 0);
        Check(justRoots.NodeCount == 2,
            "depth 0 leaves the entry points alone (got " + justRoots.NodeCount + ")");
    }

    static string GraphBuilderCollapsedFolderOf(Node node)
    {
        return (string)_graphBuilder
            .GetMethod("CollapsedFolderOf", BindingFlags.Public | BindingFlags.Static)
            .Invoke(null, new object[] { node });
    }

    static List<TextBlock> FindTextBlocks(System.Windows.DependencyObject root)
    {
        var found = new List<TextBlock>();
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            var tb = child as TextBlock;
            if (tb != null) found.Add(tb);
            found.AddRange(FindTextBlocks(child));
        }
        return found;
    }

    static void CollectTexts(System.Windows.DependencyObject root, List<string> into)
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            var tb = child as TextBlock;
            if (tb != null && !string.IsNullOrEmpty(tb.Text)) into.Add(tb.Text);
            CollectTexts(child, into);
        }
    }

    /// <summary>
    /// Calls GraphBuilder.Build by reflection, filling its optional parameters.
    /// </summary>
    /// <remarks>
    /// Reflection does not apply default values, so every optional parameter has to be passed
    /// explicitly. Funnelling the calls through here means adding another one is a single edit
    /// rather than a hunt for every call site - which is exactly how these checks broke once.
    /// </remarks>
    static Graph BuildGraph(object root, object visibility, LayerDirection direction,
        object palette, int maxDepth, Func<string, string> folderOf = null,
        ICollection<string> focusKeys = null, ICollection<string> collapsedFolders = null)
    {
        var build = _graphBuilder.GetMethod("Build", BindingFlags.Public | BindingFlags.Static);
        var args = new object[] { root, visibility, direction, palette, maxDepth, folderOf,
            focusKeys, collapsedFolders };

        if (build.GetParameters().Length != args.Length)
            throw new InvalidOperationException(
                $"GraphBuilder.Build now takes {build.GetParameters().Length} parameters; " +
                $"BuildGraph passes {args.Length}. Update BuildGraph.");

        return (Graph)build.Invoke(null, args);
    }
}
