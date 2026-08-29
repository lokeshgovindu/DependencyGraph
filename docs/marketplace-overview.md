# DependencyGraph

Draws your solution's project-to-project reference graph in a dockable tool window — laid out
properly, inside the IDE.

Useful when you are working out what a project actually drags in, why a build takes so long,
whether a reference can be removed, or where a circular reference crept in.

![The tool window](https://raw.githubusercontent.com/lokeshgovindu/DependencyGraph/master/docs/tool-window.png)

## Works in Community and Professional

Visual Studio's built-in Code Map needs the **Enterprise** edition to create or edit a graph —
Community and Professional can only open one someone else made. This does the job in any edition.

**No setup either.** You do not need Graphviz, and you do not need the DGML editor. The graph is
drawn in the IDE. Graphviz is only involved if you explicitly pick the Graphviz SVG or PNG
export.

Open it from **Tools → Show Dependency Graph**, or right-click a project in Solution Explorer.
The window docks in the document area and the rest of the IDE stays usable while it is open.

## What you see

One box per project, one arrow per reference, pointing from the project that has the reference to
the project it depends on. The project you are looking at is the root, in olive green.

The graph opens at 100% zoom so the labels stay crisp; **Fit** puts the whole graph in view. The
status bar shows the node and connection counts, the current zoom, and the selected project's
reference counts and path.

## Four views

- **Deepest references only** — one arrow per project, giving a clean tree. The quickest way to
  read the shape of a dependency chain.
- **All references** — every reference of every project. The honest picture, and busier.
- **Project references only** — the selected project and its direct references, nothing
  transitive.
- **Referenced by (impact)** — turned around: everything that depends on this project, directly
  or transitively. The "what breaks if I change this?" view.

![The impact view](https://raw.githubusercontent.com/lokeshgovindu/DependencyGraph/master/docs/impact-view.png)

## Finding your way around a big solution

Three levers, which matter once a solution runs to hundreds of projects:

- **Depth** — stop after 1 to 6 levels of references.
- **Focus** — Ctrl+click several projects, then draw only those and the references between them.
- **Group** — draw a box around each solution folder, with the folder's name across the top.
  Click the chevron beside a name to collapse that folder to a single node, with its references
  aggregated onto it; click the node to expand it again.

![Grouping and the legend](https://raw.githubusercontent.com/lokeshgovindu/DependencyGraph/master/docs/grouped-and-legend.png)

Plus an **Excluded projects** setting for the test and benchmark projects that reference half the
solution, and four layout **Directions**: top to bottom, left to right, bottom to top, right to
left.

## Move things where you want them

Automatic layout is a starting point. Drag a node, or a whole folder box, to where the graph
reads better - its references follow it and nothing else moves. What you dragged is remembered
across rebuilds, so changing view, direction or depth does not throw your arrangement away.
Right-click the background to put everything back.

## Circular references

A reference that closes a cycle is drawn in red and labelled. Visual Studio will not let you
create one through the UI, but a hand-edited project file can, and the build error does not name
the projects involved. This points straight at them.

## Getting around

Hover a node for its solution folder, its reference counts and its path. Drag to pan, wheel to
zoom. Click a node to select it. Double-click to re-root the graph on that
project. Right-click for highlighting — everything that **references** this project, or
everything it **references** directly.

## Exporting

Save the graph on screen as **PNG** (the whole graph, not just the visible part), **DGML** for
Visual Studio's directed-graph editor, or **Graphviz DOT**. If Graphviz is available, **SVG** and
**PNG** through `dot` too. **Save Solution Graph** does the same for every project at once.

## Settings

**Tools → Options → DependencyGraph → General**: the view, direction and depth the window opens
with, node label font size, excluded project patterns, and a direct path to `dot.exe` if you
would rather not put Graphviz on your `PATH`.

## Themes

Follows your Visual Studio theme, light or dark, and re-colours itself if you switch theme while
the window is open.

## Requirements

Visual Studio 2022 (17.x) or 2026 (18.x), `amd64`. Project references are read through the
C#/VB/F# project system; C++ and other project kinds appear but have no readable references.

## Links

- [Source and full documentation](https://github.com/lokeshgovindu/DependencyGraph)
- [Report an issue](https://github.com/lokeshgovindu/DependencyGraph/issues)
- [Release notes](https://github.com/lokeshgovindu/DependencyGraph/releases)
