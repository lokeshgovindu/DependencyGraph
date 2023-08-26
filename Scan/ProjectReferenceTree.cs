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

        /// <summary>Item in this node.</summary>
        public Project Item { get; private set; }

        /// <summary>List of sub items for this node.</summary>
        private HashSet<ProjectReferenceTree> references = new HashSet<ProjectReferenceTree>();

        /// <summary>Depth level for this node</summary>
        public int DepthLevel { get; private set; } = 0;

        /// <summary>Arbitrary additional data.</summary>
        public object data;

        public HashSet<ProjectReferenceTree> References { get => references; set => references = value; }

        /// <summary>Stores parent node that references given node of T.</summary>
        private Dictionary<Project, ProjectReferenceTree> Proj2ProjRefTreeMap;
        private HashSet<ProjectReferenceTree> AllPRTs;

        public static ProjectReferenceTree GetProjectReferenceTree(Project project)
        {
            return new ProjectReferenceTree(project);
        }
        public static ProjectReferenceTree GetProjectReferencesOnly(Project project)
        {
            var ret = new ProjectReferenceTree();
            ret.Item = project;
            ret.DepthLevel = 0;
            ret.Proj2ProjRefTreeMap = new Dictionary<Project, ProjectReferenceTree>();
            ret.AllPRTs = new HashSet<ProjectReferenceTree>();
            ret.AllPRTs.Add(ret);
            var references = project.ProjectReferences();
            foreach (var projRef in references)
            {
                ret.Proj2ProjRefTreeMap.Add(projRef, ret);
                var projRefPRT = new ProjectReferenceTree
                {
                    Item = projRef,
                    DepthLevel = 1,
                    Proj2ProjRefTreeMap = ret.Proj2ProjRefTreeMap,
                    AllPRTs = ret.AllPRTs
                };
                projRefPRT.AllPRTs.Add(projRefPRT);
                ret.references.Add(projRefPRT);
            }
            return ret;
        }

        private ProjectReferenceTree() { }
        public ProjectReferenceTree(Project project, ProjectReferenceTree projectReferenceTree = null, int depthLevel = 0)
        {
            this.Item = project;
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
            }
            AllPRTs.Add(this);
            AddProjects(project.ProjectReferences());
        }

        public void AddProject(Project item)
        {
            if (Proj2ProjRefTreeMap.ContainsKey(item))
            {
                // Parent of refTree containing the item
                var parentRefTree = Proj2ProjRefTreeMap[item];

                // RefTree containing the item
                ProjectReferenceTree refTree = parentRefTree.GetPRTFromReferences(item);

                Debug.Assert(refTree != null, "Value suppose to contain item");
                References.Add(refTree);

                // Do steal parent if it is deeper in tree.
                if (parentRefTree.DepthLevel > DepthLevel) return;
                refTree.SetLevel(DepthLevel + 1);
                Proj2ProjRefTreeMap[item] = this;
                return;
            }

            Proj2ProjRefTreeMap.Add(item, this);
            references.Add(new ProjectReferenceTree(item, this, DepthLevel + 1));
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

        public void Print(string depth = "")
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Console.Out.WriteLine("{0}{1}", depth, Item.Name);
            foreach (var subRefTree in this.References)
            {
                subRefTree.Print(depth + "----");
            }
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
