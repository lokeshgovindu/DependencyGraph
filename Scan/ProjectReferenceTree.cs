using EnvDTE;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Project = EnvDTE.Project;

namespace DependencyGraph.Scan
{
    public class ProjectReferenceTree
    {
        public static Func<ProjectReferenceTree, string> toString;

        public string ProjectName;

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
            }
        }

        /// <summary>List of sub items for this node.</summary>
        private HashSet<ProjectReferenceTree> _references = new HashSet<ProjectReferenceTree>();

        /// <summary>Depth level for this node</summary>
        public int DepthLevel { get; private set; } = 0;

        /// <summary>Arbitrary additional data.</summary>
        public object data;

        public HashSet<ProjectReferenceTree> References { get => _references; set => _references = value; }

        /// <summary>Stores parent node that references given node of T.</summary>
        private Dictionary<Project, ProjectReferenceTree> Proj2ProjRefTreeMap;

        public HashSet<ProjectReferenceTree> AllPRTs;

        public static ProjectReferenceTree GetProjectReferenceTree(Project project)
        {
            return new ProjectReferenceTree(project);
        }

        public int ReferencesCount      { get; private set; }
        public int ReferencedByCount    { get; private set; }

        public static ProjectReferenceTree GetProjectReferencesOnly(Project project)
        {
            //Console.Out.WriteLine("[GetProjectReferencesOnly] item.Name = {0}", project.Name);
            var ret = new ProjectReferenceTree 
            {
                Item                = project,
                DepthLevel          = 0,
                Proj2ProjRefTreeMap = new Dictionary<Project, ProjectReferenceTree>(),
                AllPRTs             = new HashSet<ProjectReferenceTree>()
            };

            ret.AllPRTs.Add(ret);
            var references = project.ProjectReferences();
            foreach (var projRef in references)
            {
                ret.Proj2ProjRefTreeMap.Add(projRef, ret);
                var projRefPRT = new ProjectReferenceTree
                {
                    Item                = projRef,
                    DepthLevel          = 1,
                    Proj2ProjRefTreeMap = ret.Proj2ProjRefTreeMap,
                    AllPRTs             = ret.AllPRTs
                };
                projRefPRT.AllPRTs.Add(projRefPRT);
                ret._references.Add(projRefPRT);
            }
            return ret;
        }

        private ProjectReferenceTree() { }

        public ProjectReferenceTree(Project project, ProjectReferenceTree projectReferenceTree = null, int depthLevel = 0)
        {
            //Console.Out.WriteLine("[ProjectReferenceTree] item.Name = {0}, depth = {1}", project.Name, depthLevel);
            this.Item       = project;
            this.DepthLevel = depthLevel;
            if (projectReferenceTree == null)
            {
                Proj2ProjRefTreeMap = new Dictionary<Project, ProjectReferenceTree>();
                AllPRTs = new HashSet<ProjectReferenceTree>();
            }
            else
            {
                this.Proj2ProjRefTreeMap = projectReferenceTree.Proj2ProjRefTreeMap;
                this.AllPRTs = projectReferenceTree.AllPRTs;
                this.ReferencedByCount += 1;
            }
            AllPRTs.Add(this);
            var references = project.ProjectReferences();
            this.ReferencesCount = references.Count;
            foreach (var refProj in references)
            {
                AddProject(refProj);
            }
        }

        public void AddProject(Project item)
        {
            if (Proj2ProjRefTreeMap.ContainsKey(item))
            {
                // Parent of refTree containing the item
                var parentRefTree = Proj2ProjRefTreeMap[item];

                // RefTree containing the item
                ProjectReferenceTree refTree = parentRefTree.GetPRTFromReferences(item);
                refTree.ReferencedByCount += 1;

                Debug.Assert(refTree != null, "Value suppose to contain item");
                References.Add(refTree);

                // Do steal parent if it is deeper in tree.
                if (parentRefTree.DepthLevel > DepthLevel) return;

                refTree.SetLevel(DepthLevel + 1);
                Proj2ProjRefTreeMap[item] = this;
                return;
            }

            Proj2ProjRefTreeMap.Add(item, this);
            _references.Add(new ProjectReferenceTree(item, this, DepthLevel + 1));
        }

        private void AddProjects(IEnumerable<Project> projects)
        {
            foreach (var project in projects) AddProject(project);
        }

        private void SetLevel(int depthLevel)
        {
            this.DepthLevel = depthLevel;
            foreach (var s in References)
            {
                var pr = Proj2ProjRefTreeMap[s.Item];
                if (pr != this || pr.DepthLevel > depthLevel) continue;
                Proj2ProjRefTreeMap[s.Item] = this;
                s.SetLevel(depthLevel + 1);
            }
        }

        public void PrintReferenceTree(string depth = "")
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Console.Out.WriteLine("{0}{1}", depth, Item.Name);
            foreach (var subRefTree in this.References)
            {
                subRefTree.PrintReferenceTree(depth + "----");
            }
        }

        public void PrintReferenceMap(string depth = "")
        {
            foreach (var parent in this.AllPRTs)
            {
                Console.WriteLine("{0}", parent.ProjectName);
                foreach (var child in parent.References)
                {
                    Console.WriteLine("  -- {0}", child.ProjectName);
                }
            }
        }

        public void PrintGraphVizDot()
        {
            Console.WriteLine("digraph {");
            foreach (var parent in this.AllPRTs)
            {
                //Console.WriteLine("    {0} -- ", parent.Item.Name);
                foreach (var child in parent.References)
                {
                    Console.WriteLine("    {0} -> {1}", parent.ProjectName, child.ProjectName);
                }
            }
            Console.WriteLine("}");
        }

        #region Utils
        /// <summary>Returns only direct sub nodes that are on specified level.</summary>
        /// <param name="d">Depth level - negative value will retrun subs on level next to this node level.</param>
        public List<ProjectReferenceTree> SubsOnLevel(int d = -1)
        {
            if (d < 0) d = this.DepthLevel + 1;
            var ls = new List<ProjectReferenceTree>();
            foreach (var s in References) if (s.DepthLevel == d) ls.Add(s);
            return ls;
        }

        public List<ProjectReferenceTree> AllOnLevel(int d = -1)
        {
            if (d < 0) d = this.DepthLevel + 1;
            var ret = new List<ProjectReferenceTree>();
            foreach (var prt in AllPRTs) if (prt.DepthLevel == d) ret.Add(prt);
            if (ret.Count == 0) return null;
            return ret;
        }

        public ProjectReferenceTree GetDeepestReference()
        {
            if (!Proj2ProjRefTreeMap.ContainsKey(Item)) return null;
            return Proj2ProjRefTreeMap[Item];
        }

        public ProjectReferenceTree GetPRTFromReferences(Project project)
        {
            foreach (var sr in References)
                if (sr.Item.Equals(project)) return sr;
            return null;
        }

        /// <summary>Perform action on each node in the tree</summary>
        /// <param name="action"></param>
        public void WithEach(Action<ProjectReferenceTree> action)
        {
            foreach (var projectReferenceTree in AllPRTs) action(projectReferenceTree);
        }
        #endregion

        public override string ToString()
        {
            return (toString?.Invoke(this) ?? base.ToString()) + $"({DepthLevel})";
        }

        public static implicit operator bool(ProjectReferenceTree r) => r != null;
    }
}
