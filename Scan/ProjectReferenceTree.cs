using EnvDTE;
using System;
using System.Collections.Generic;
using System.Linq;
using Project = EnvDTE.Project;

namespace DependencyGraph.Scan
{
    /// <summary>
    /// One project in a reference graph, plus the shared bookkeeping for the whole graph
    /// (<see cref="AllPRTs"/> and the owner map behind <see cref="GetDeepestReference"/>).
    /// </summary>
    public class ProjectReferenceTree
    {
        /// <summary>Display name of the project. Not unique within a solution.</summary>
        public string ProjectName;

        /// <summary>
        /// Stable identity of the project, used as the graph node id and as the key of
        /// <see cref="Proj2ProjRefTreeMap"/>.
        /// </summary>
        /// <remarks>
        /// Taken from Project.UniqueName - the solution-relative project path - for two reasons.
        /// Two projects in different solution folders can share a Name, and keying on the name
        /// merged them into a single node; and the previous key was the EnvDTE.Project COM object
        /// itself, which relies on DTE handing back the same runtime callable wrapper every time.
        /// </remarks>
        public string Key { get; private set; }

        public Project _item;

        /// <summary>Item in this node.</summary>
        public Project Item
        {
            get => _item;
            private set
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                _item = value;
                ProjectName = _item.Name;
                Key = KeyOf(_item);
            }
        }

        /// <summary>Stable identity for a project, falling back if the shell will not answer.</summary>
        public static string KeyOf(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (!string.IsNullOrEmpty(project.UniqueName)) return project.UniqueName;
            }
            catch { /* some project kinds throw rather than answering */ }

            try
            {
                if (!string.IsNullOrEmpty(project.FullName)) return project.FullName;
            }
            catch { }

            return project.Name;
        }

        /// <summary>
        /// True for a project that is referenced but is not itself in the solution, so its own
        /// references were never scanned and the graph beneath it is incomplete.
        /// </summary>
        public bool IsExternal { get; set; }

        /// <summary>Projects this project references directly.</summary>
        private HashSet<ProjectReferenceTree> _references = new HashSet<ProjectReferenceTree>();

        public HashSet<ProjectReferenceTree> References { get => _references; set => _references = value; }

        /// <summary>Depth level for this node</summary>
        public int DepthLevel { get; private set; } = 0;

        /// <summary>Deepest project that references this one, keyed by project key.</summary>
        private Dictionary<string, ProjectReferenceTree> Proj2ProjRefTreeMap;

        public HashSet<ProjectReferenceTree> AllPRTs;

        /// <summary>How many projects this one references directly.</summary>
        /// <remarks>
        /// Derived rather than counted during construction. The stored version was set from the
        /// reference list length, which double-counted a project referenced twice, and was never
        /// set at all by <see cref="GetProjectReferencesOnly"/> - so that mode reported zero for
        /// every node.
        /// </remarks>
        public int ReferencesCount => _references.Count;

        /// <summary>How many projects in this graph reference this one.</summary>
        public int ReferencedByCount =>
            AllPRTs == null ? 0 : AllPRTs.Count(p => p != this && p.References.Contains(this));

        /// <summary>
        /// Every project that reaches this one through references, directly or transitively, with
        /// the real reference edges between them.
        /// </summary>
        /// <remarks>
        /// The impact graph: what breaks if this project changes. Arrows keep their usual meaning
        /// - they always point from the project that has the reference to the project it depends
        /// on - so the root ends up as a sink, with the projects that depend on it above.
        ///
        /// This is the one view that cannot be built by walking outwards from the root, because
        /// a project does not know who references it. It needs the whole solution's references
        /// first, so the callers pass them in.
        /// </remarks>
        public static ProjectReferenceTree GetReferencedByTree(Project root, IList<Project> allProjects)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Direct references of every project in the solution, by key.
            var projectsByKey = new Dictionary<string, Project>();
            var referencesOf = new Dictionary<string, List<string>>();
            var referencersOf = new Dictionary<string, List<string>>();
            var inSolution = new HashSet<string>();

            foreach (var project in allProjects)
            {
                string key = KeyOf(project);
                projectsByKey[key] = project;
                inSolution.Add(key);
                if (!referencesOf.ContainsKey(key)) referencesOf[key] = new List<string>();

                foreach (var reference in project.ProjectReferences())
                {
                    string refKey = KeyOf(reference);
                    referencesOf[key].Add(refKey);

                    if (!referencersOf.TryGetValue(refKey, out var list))
                        referencersOf[refKey] = list = new List<string>();
                    list.Add(key);

                    // A referenced project can sit outside the solution's project list.
                    if (!projectsByKey.ContainsKey(refKey)) projectsByKey[refKey] = reference;
                }
            }

            // Walk the references backwards from the root to find everything that reaches it,
            // recording how many hops away each one is.
            string rootKey = KeyOf(root);
            var hops = new Dictionary<string, int> { { rootKey, 0 } };
            var queue = new Queue<string>();
            queue.Enqueue(rootKey);

            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                if (!referencersOf.TryGetValue(current, out var referencers)) continue;

                foreach (var referencer in referencers)
                    if (!hops.ContainsKey(referencer))
                    {
                        hops[referencer] = hops[current] + 1;
                        queue.Enqueue(referencer);
                    }
            }

            // Build a node per project in that set.
            var shared = new Dictionary<string, ProjectReferenceTree>();
            var all = new HashSet<ProjectReferenceTree>();
            var nodes = new Dictionary<string, ProjectReferenceTree>();

            foreach (var pair in hops)
            {
                if (!projectsByKey.TryGetValue(pair.Key, out var project)) continue;

                var node = new ProjectReferenceTree
                {
                    Item                = project,
                    DepthLevel          = pair.Value,
                    Proj2ProjRefTreeMap = shared,
                    AllPRTs             = all,
                    IsExternal          = !inSolution.Contains(pair.Key),
                };
                nodes[pair.Key] = node;
                all.Add(node);
            }

            // Then the real reference edges, restricted to that set.
            foreach (var node in nodes)
            {
                if (!referencesOf.TryGetValue(node.Key, out var references)) continue;

                foreach (var refKey in references)
                    if (nodes.TryGetValue(refKey, out var target) && target != node.Value)
                        node.Value._references.Add(target);
            }

            return nodes.TryGetValue(rootKey, out var rootNode) ? rootNode : null;
        }

        /// <summary>The selected project and its direct references only - a two level graph.</summary>
        public static ProjectReferenceTree GetProjectReferencesOnly(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var ret = new ProjectReferenceTree
            {
                Item                = project,
                DepthLevel          = 0,
                Proj2ProjRefTreeMap = new Dictionary<string, ProjectReferenceTree>(),
                AllPRTs             = new HashSet<ProjectReferenceTree>()
            };

            ret.AllPRTs.Add(ret);

            foreach (var projRef in project.ProjectReferences())
            {
                string key = KeyOf(projRef);

                // A project can appear twice in the reference list; keep the first node for it.
                if (ret.Proj2ProjRefTreeMap.TryGetValue(key, out var existing))
                {
                    ret._references.Add(existing);
                    continue;
                }

                var projRefPRT = new ProjectReferenceTree
                {
                    Item                = projRef,
                    DepthLevel          = 1,
                    Proj2ProjRefTreeMap = ret.Proj2ProjRefTreeMap,
                    AllPRTs             = ret.AllPRTs
                };

                ret.Proj2ProjRefTreeMap.Add(key, ret);
                ret.AllPRTs.Add(projRefPRT);
                ret._references.Add(projRefPRT);
            }

            return ret;
        }

        private ProjectReferenceTree() { }

        public ProjectReferenceTree(Project project, ProjectReferenceTree projectReferenceTree = null, int depthLevel = 0)
        {
            this.Item       = project;
            this.DepthLevel = depthLevel;

            if (projectReferenceTree == null)
            {
                Proj2ProjRefTreeMap = new Dictionary<string, ProjectReferenceTree>();
                AllPRTs = new HashSet<ProjectReferenceTree>();
            }
            else
            {
                this.Proj2ProjRefTreeMap = projectReferenceTree.Proj2ProjRefTreeMap;
                this.AllPRTs = projectReferenceTree.AllPRTs;
            }

            AllPRTs.Add(this);

            foreach (var refProj in project.ProjectReferences())
            {
                AddProject(refProj);
            }
        }

        public void AddProject(Project item)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string key = KeyOf(item);

            if (Proj2ProjRefTreeMap.TryGetValue(key, out var parentRefTree))
            {
                // The node already exists somewhere in the graph - reference it rather than
                // building a second copy.
                ProjectReferenceTree refTree = parentRefTree.GetPRTFromReferences(key);
                if (refTree == null) return;

                References.Add(refTree);

                // Take ownership unless the current owner is deeper than this project, so the
                // map ends up naming the deepest project that references it - which is what
                // the "deepest references only" view draws, and what makes DepthLevel the
                // length of the longest path to a project rather than the shortest.
                if (parentRefTree.DepthLevel > DepthLevel) return;

                refTree.SetLevel(DepthLevel + 1);
                Proj2ProjRefTreeMap[key] = this;
                return;
            }

            Proj2ProjRefTreeMap.Add(key, this);
            _references.Add(new ProjectReferenceTree(item, this, DepthLevel + 1));
        }

        private void SetLevel(int depthLevel)
        {
            SetLevel(depthLevel, new HashSet<ProjectReferenceTree>());
        }

        /// <summary>
        /// Pushes this node - and the nodes it owns - down to a new depth.
        /// </summary>
        /// <remarks>
        /// The visited set is what stops the recursion. Visual Studio refuses to create circular
        /// project references, so a cycle should not reach us, but nothing here would survive one:
        /// without the guard two projects referencing each other recurse until the stack runs out.
        /// </remarks>
        private void SetLevel(int depthLevel, HashSet<ProjectReferenceTree> visited)
        {
            if (!visited.Add(this)) return;

            this.DepthLevel = depthLevel;

            foreach (var s in References)
            {
                if (!Proj2ProjRefTreeMap.TryGetValue(s.Key, out var owner)) continue;
                if (owner != this || owner.DepthLevel > depthLevel) continue;

                Proj2ProjRefTreeMap[s.Key] = this;
                s.SetLevel(depthLevel + 1, visited);
            }
        }

        #region Utils

        /// <summary>
        /// The deepest project that references this one, or null for the root. This is the
        /// single edge drawn per node in "deepest references only" mode.
        /// </summary>
        public ProjectReferenceTree GetDeepestReference()
        {
            return Proj2ProjRefTreeMap.TryGetValue(Key, out var parent) ? parent : null;
        }

        public ProjectReferenceTree GetPRTFromReferences(string key)
        {
            foreach (var sr in References)
                if (sr.Key == key) return sr;
            return null;
        }

        /// <summary>
        /// Flags every node that is not one of the solution's own projects.
        /// </summary>
        /// <remarks>
        /// The forward views follow references outwards and will happily walk into a project that
        /// is referenced but not part of the solution, so which nodes are external can only be
        /// decided by comparing against the solution's project list afterwards.
        /// </remarks>
        public void MarkExternals(IEnumerable<string> solutionKeys)
        {
            var inSolution = new HashSet<string>(solutionKeys);
            foreach (var node in AllPRTs) node.IsExternal = !inSolution.Contains(node.Key);
        }

        /// <summary>Perform action on each node in the graph.</summary>
        public void WithEach(Action<ProjectReferenceTree> action)
        {
            foreach (var projectReferenceTree in AllPRTs) action(projectReferenceTree);
        }

        #endregion

        public override string ToString() => $"{ProjectName} (depth {DepthLevel})";

        public static implicit operator bool(ProjectReferenceTree r) => r != null;
    }
}
