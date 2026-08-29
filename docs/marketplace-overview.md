# DependencyGraph

Draws your solution's project-to-project reference graph in a dockable tool window — laid out
properly, inside the IDE.

Useful when you are working out what a project actually drags in, why a build takes so long,
whether a reference can be removed, or where a circular reference crept in.

![The tool window](https://raw.githubusercontent.com/lokeshgovindu/DependencyGraph/master/docs/tool-window.png)

## No setup needed

**You do not need Graphviz, and you do not need the DGML editor.** The graph is drawn in the
IDE. Graphviz is only involved if you explicitly pick the Graphviz SVG or PNG export.

Open it from **Tools → Show Dependency Graph**, or right-click a project in Solution Explorer.
The window docks in the document area and the rest of the IDE stays usable while it is open.

## What you see

One box per project, one arrow per reference, pointing from the project that has the reference
to the project it depends on. The project you are looking at is the root, in olive green.

The graph opens at 100% zoom so the labels stay crisp; **Fit** puts the whole graph in view.
The status bar shows the node and connection counts, the current zoom, and the selected
project's reference counts.

## Three views

- **Deepest references only** — one arrow per project, giving a clean tree. The quickest way to
  read the shape of a dependency chain.
- **All references** — every reference of every project. The honest picture, and busier.
- **Project references only** — the selected project and its direct references, nothing
  transitive.

Plus a **Depth** limit of 1–6 levels for large solutions, and four layout **Directions**: top to
bottom, left to right, bottom to top, right to left.

## Getting around

Drag to pan, wheel to zoom. Click a node to select it. Double-click to re-root the graph on that
project. Right-click for highlighting:

- everything that **references** this project — what breaks if you change it
- everything this project **references** directly

## Circular references

A reference that closes a cycle is drawn in red and labelled. Visual Studio will not let you
create one through the UI, but a hand-edited project file can, and the build error does not name
the projects involved. This points straight at them.

## Exporting

Save the graph on screen as **PNG** (the whole graph, not just the visible part), **DGML** for
Visual Studio's directed-graph editor, or **Graphviz DOT**. If `dot.exe` is on your `PATH`,
Graphviz **SVG** and **PNG** are offered too. **Save Solution Graph** does the same for every
project in the solution at once.

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
