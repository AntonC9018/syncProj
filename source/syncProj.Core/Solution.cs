﻿using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

/// <summary>
/// .sln loaded into class.
/// </summary>
public class Solution
{
    /// <summary>
    /// Solution name
    /// </summary>
    public String name;

    /// <summary>
    /// File path from where solution was loaded.
    /// </summary>
    [XmlIgnore]
    public String path;

    /// <summary>
    /// Just an internal project for tracking project hierarchy
    /// </summary>
    public Project solutionRoot = new Project();

    /// <summary>
    /// Solution name for debugger.
    /// </summary>
    [ExcludeFromCodeCoverage]
    public override string ToString()
    {
        return "Solution, name = " + name;
    }

    /// <summary>
    /// Gets solution path
    /// </summary>
    /// <returns></returns>
    public String getSolutionFolder()
    {
        return Path.GetDirectoryName(path);
    }

    double slnVer;                                      // 11.00 - vs2010, 12.00 - vs2015

    /// <summary>
    /// Visual studio version information used for generation, for example 2010, 2012, 2015 and so on...
    /// </summary>
    public int fileFormatVersion;

    /// <summary>
    /// null for old visual studio's
    /// </summary>
    public String VisualStudioVersion;

    /// <summary>
    /// null for old visual studio's
    /// </summary>
    public String MinimumVisualStudioVersion;

    /// <summary>
    /// Solution guid, for example "{00346907-56E9-4CC1-802F-039B70C1FA48}".
    /// Mandatory starting from Visual studio 2017.
    /// </summary>
    public string SolutionGuid;

    /// <summary>
    /// List of project included into solution.
    /// </summary>
    public List<Project> projects = new List<Project>();

    /// <summary>
    /// List of configuration list, in form "{Configuration}|{Platform}", for example "Release|Win32".
    /// To extract individual platforms / configuration list, use following functions.
    /// </summary>
    public List<String> configurations = new List<string>();

    /// <summary>
    /// Extracts platfroms supported by solution
    /// </summary>
    public IEnumerable<String> getPlatforms()
    {
        return configurations.Select(x => x.Split('|')[1]).Distinct();
    }

    /// <summary>
    /// Extracts configuration names supported by solution
    /// </summary>
    public IEnumerable<String> getConfigurations()
    {
        return configurations.Select(x => x.Split('|')[0]).Distinct();
    }


    /// <summary>
    /// Creates new solution.
    /// </summary>
    public Solution() { }

    /// <summary>
    /// Loads visual studio .sln solution
    /// </summary>
    /// <exception cref="System.IO.FileNotFoundException">The file specified in path was not found.</exception>
    public static Solution LoadSolution(string path)
    {
        Solution s = new Solution();
        s.path = path;

        String slnTxt = File.ReadAllText(path);
        //
        //  Extra line feed is used by Visual studio, cmake does not generate extra line feed.
        //
        s.slnVer = Double.Parse(Regex.Match(slnTxt, "[\r\n]?Microsoft Visual Studio Solution File, Format Version ([0-9.]+)", RegexOptions.Multiline).Groups[1].Value, CultureInfo.InvariantCulture);

        int vsNumber = Int32.Parse(Regex.Match(slnTxt, "^\\# Visual Studio (Express |Version )?([0-9]+)", RegexOptions.Multiline).Groups[2].Value);
        if (vsNumber > 2000)
            s.fileFormatVersion = vsNumber;
        else
        {
            switch (vsNumber)
            {
                case 14:
                    s.fileFormatVersion = 2015;
                    break;
                case 15:
                    s.fileFormatVersion = 2017;
                    break;
                case 16:
                    s.fileFormatVersion = 2019;
                    break;
                default:
                    // Every two years new release ?
                    s.fileFormatVersion = (vsNumber - 14) * 2 + 2015;
                    break;
            }

        }

        foreach (String line in new String[] { "VisualStudioVersion", "MinimumVisualStudioVersion" })
        {
            var m = Regex.Match(slnTxt, "^" + line + " = ([0-9.]+)", RegexOptions.Multiline);
            String v = null;
            if (m.Success)
                v = m.Groups[1].Value;

            s.GetType().GetField(line).SetValue(s, v);
        }

        Regex reProjects = new Regex(
            "Project\\(\"(?<ProjectHostGuid>{[A-F0-9-]+})\"\\) = \"(?<ProjectName>.*?)\", \"(?<RelativePath>.*?)\", \"(?<ProjectGuid>{[A-F0-9-]+})\"[\r\n]*(?<dependencies>.*?)EndProject[\r\n]+",
            RegexOptions.Singleline);


        reProjects.Replace(slnTxt, new MatchEvaluator(m =>
        {
            Project p = new Project() { solution = s };

            foreach (String g in reProjects.GetGroupNames())
            {
                if (g == "0")   //"0" - RegEx special kind of group
                    continue;

                //
                // ProjectHostGuid, ProjectName, RelativePath, ProjectGuid fields/properties are set here.
                //
                String v = m.Groups[g].ToString();
                if (g != "dependencies")
                {
                    FieldInfo fi = p.GetType().GetField(g);
                    if (fi != null)
                    {
                        fi.SetValue(p, v);
                    }
                    else
                    {
                        p.GetType().GetProperty(g).SetValue(p, v);
                    }
                    continue;
                }

                if (v == "")    // No dependencies set
                    continue;

                String depsv = new Regex("ProjectSection\\(ProjectDependencies\\)[^\r\n]*?[\r\n]+" + "(.*?)" + "EndProjectSection", RegexOptions.Singleline).Match(v).Groups[1].Value;

                //
                // key is always equal to it's value.
                // http://stackoverflow.com/questions/5629981/question-about-visual-studio-sln-file-format
                //
                p.ProjectDependencies = new Regex("\\s*?({[A-F0-9-]+}) = ({[A-F0-9-]+})[\r\n]+", RegexOptions.Multiline).Matches(depsv).Cast<Match>().Select(x => x.Groups[1].Value).ToList();
            } //foreach

            // Defining language removes the need of project extension
            if (p.language != null)
                p.RelativePath = Path.Combine(Path.GetDirectoryName(p.RelativePath), Path.GetFileNameWithoutExtension(p.RelativePath));

            // Even thus solution does have project name written down - VS ignores that name completely - it uses .vcxproj's ProjectName instead.
            if (!p.bIsFolder)
                p.ProjectName = Path.GetFileName(p.RelativePath);

            s.projects.Add(p);
            return "";
        }
        )
        );

        new Regex("GlobalSection\\(SolutionConfigurationPlatforms\\).*?[\r\n]+(.*?)EndGlobalSection[\r\n]+", RegexOptions.Singleline).Replace(slnTxt, new MatchEvaluator(m2 =>
        {
            s.configurations = new Regex("\\s*(.*)\\s+=").Matches(m2.Groups[1].ToString()).Cast<Match>().Select(x => x.Groups[1].Value).ToList();
            return "";
        }
        ));

        new Regex("GlobalSection\\(ProjectConfigurationPlatforms\\).*?[\r\n]+(.*?)EndGlobalSection[\r\n]+", RegexOptions.Singleline).Replace(slnTxt, new MatchEvaluator(m2 =>
        {
            foreach (Match m3 in new Regex("\\s*({[A-F0-9-]+})\\.(.*?)\\.(.*?)\\s+=\\s+(.*?)[\r\n]+").Matches(m2.Groups[1].ToString()))
            {
                String guid = m3.Groups[1].Value;
                String solutionConfig = m3.Groups[2].Value;
                String action = m3.Groups[3].Value;
                String projectConfig = m3.Groups[4].Value;

                Project p = s.projects.Where(x => x.ProjectGuid == guid).FirstOrDefault();
                if (p == null)
                    continue;

                int iConfigIndex = s.configurations.IndexOf(solutionConfig);
                if (iConfigIndex == -1)
                    continue;

                while (p.slnConfigurations.Count < s.configurations.Count)
                {
                    p.slnConfigurations.Add(null);
                    p.slnBuildProject.Add(false);
                }

                if (action == "ActiveCfg")
                {
                    p.slnConfigurations[iConfigIndex] = projectConfig;
                }
                else
                {
                    if (action.StartsWith("Build"))
                    {
                        p.slnBuildProject[iConfigIndex] = true;
                    }
                    else
                    {
                        if (action.StartsWith("Deploy"))
                        {
                            if (p.slnDeployProject == null) p.slnDeployProject = new List<bool?>();

                            while (p.slnDeployProject.Count < s.configurations.Count)
                                p.slnDeployProject.Add(null);

                            p.slnDeployProject[iConfigIndex] = true;
                        }
                    }
                } //if-esle
            }
            return "";
        }
        ));

        //
        // Initializes parent-child relationship.
        //
        new Regex("GlobalSection\\(NestedProjects\\).*?[\r\n]+(.*?)EndGlobalSection[\r\n]+", RegexOptions.Singleline).Replace(slnTxt, new MatchEvaluator(m4 =>
        {
            String v = m4.Groups[1].Value;
            new Regex("\\s*?({[A-F0-9-]+}) = ({[A-F0-9-]+})[\r\n]+", RegexOptions.Multiline).Replace(v, new MatchEvaluator(m5 =>
            {
                String[] args = m5.Groups.Cast<Group>().Skip(1).Select(x => x.Value).ToArray();
                Project child = s.projects.Where(x => args[0] == x.ProjectGuid).FirstOrDefault();
                Project parent = s.projects.Where(x => args[1] == x.ProjectGuid).FirstOrDefault();
                parent.nodes.Add(child);
                child.parent = parent;
                return "";
            }));
            return "";
        }
        ));


        new Regex("GlobalSection\\(ExtensibilityGlobals\\).*?[\r\n]+(.*?)EndGlobalSection[\r\n]+", RegexOptions.Singleline).Replace(slnTxt, new MatchEvaluator(m4 =>
        {
            String v = m4.Groups[1].Value;
            new Regex("\\s*(.*)\\s+=\\s+(.*?)[\r\n]+").Replace(v, new MatchEvaluator(m5 =>
            {
                String key = m5.Groups[1].Value;
                String value = m5.Groups[2].Value;
                FieldInfo fi = s.GetType().GetField(key);

                // SolutionGuid is retrieved from here
                if (fi != null)
                    fi.SetValue(s, value);
                return "";
            }
            ));
            return "";
        }
        ));

        s.solutionRoot = new Project();

        // All projects which don't have root will become attached to root.
        foreach (Project p in s.projects)
            if (p.parent == null)
            {
                p.parent = s.solutionRoot;
                s.solutionRoot.nodes.Add(p);
            }

        return s;
    } //LoadSolution


    /// <summary>
    /// Saves solution into file.
    /// </summary>
    public void SaveSolution(String _path)
    {
        SaveSolution(new UpdateInfo(), _path);
    }

    /// <summary>
    /// Saves solution into .sln file. Where to save is defined by path.
    /// </summary>
    /// <param name="_path">path to .sln, null if use from 'path' variable.</param>
    /// <param name="uinfo">Update information</param>
    public void SaveSolution(UpdateInfo uinfo, String _path = null)
    {
        String slnPath = _path;

        if (_path == null)
            slnPath = path;

        //
        //  For all projects which does not have uuid, we generated uuid based on project name.
        //
        SolutionProjectBuilder.externalproject(null);   // Release any active project if we have one.

        if (fileFormatVersion >= 2017 && String.IsNullOrEmpty(SolutionGuid))
            SolutionProjectBuilder.uuid(name + "_solution" /* Add some uniqueness - just not to collise with project guids. */);

        foreach (Project p in projects)
        {
            if (String.IsNullOrEmpty(p.ProjectGuid))
            {
                SolutionProjectBuilder.m_project = p;
                SolutionProjectBuilder.uuid(p.ProjectName);
                SolutionProjectBuilder.m_project = null;
            }
        } //foreach

        StringBuilder o = new StringBuilder();

        o.AppendLine();

        int verTag = fileFormatVersion;

        if (verTag == 0)
            verTag = 2015;

        String formatVersion = "12.00";

        if (verTag <= 2010)
            formatVersion = "11.00";

        o.AppendLine("Microsoft Visual Studio Solution File, Format Version " + formatVersion);

        int verTag2;
        switch (verTag)
        {
            case 2015: verTag2 = 14; break;
            case 2017: verTag2 = 15; break;
            case 2019: verTag2 = 16; break;

            default:
                // Try to predict the future here...
                verTag2 = 14 + (verTag - 2015) / 2;
                break;
        }

        if (verTag2 >= 16)
            o.AppendLine("# Visual Studio Version " + verTag2.ToString());
        else
            o.AppendLine("# Visual Studio " + verTag2.ToString());

        // Must be specified, otherwise Visual studio will try to save project after load.
        if (fileFormatVersion >= 2017)
        {
            // Those numbers are pretty ugly to hardcode, but no other choice at the moment.
            String ver = VisualStudioVersion;
            if (ver == null)
            {
                switch (fileFormatVersion)
                {
                    case 2017: ver = "15.0.28307.136"; break;
                    default:
                    case 2019: ver = "16.0.28315.86"; break;
                }
            }

            o.AppendLine("VisualStudioVersion = " + ver);
        }

        // Must be specified, otherwise Visual studio will try to save project after load.
        if (fileFormatVersion >= 2015)
        {
            String ver = MinimumVisualStudioVersion;
            if (ver == null) ver = "10.0.40219.1";
            o.AppendLine("MinimumVisualStudioVersion = " + ver);
        }

        // Visual studio 2015 itself dumps also VisualStudioVersion & MinimumVisualStudioVersion - but we cannot support it, as it's targetted per visual studio toolset version.

        //
        // Dump projects.
        //
        foreach (Project p in projects)
        {
            o.AppendLine("Project(\"" + p.ProjectHostGuid + "\") = \"" + p.ProjectName + "\", \"" + p.getRelativePath() + "\", \"" + p.ProjectGuid.ToUpper() + "\"");

            //
            // Dump project dependencies.
            //
            if (p.ProjectDependencies != null)
            {
                o.AppendLine("	ProjectSection(ProjectDependencies) = postProject");
                foreach (String depProjName in p.ProjectDependencies)
                {
                    String guid = null;

                    // Dependency specified by {guid}
                    if (SolutionProjectBuilder.guidMatcher.Match(depProjName).Success)
                    {
                        guid = depProjName;
                    }
                    else
                    {
                        // Dependency specified by project name
                        Project dproj = projects.Where(x => x.ProjectName == depProjName).FirstOrDefault();
                        if (dproj != null)
                            guid = dproj.ProjectGuid.ToUpper();
                    }

                    if (guid != null)
                        o.AppendLine("		" + guid + " = " + guid);
                }
                o.AppendLine("	EndProjectSection");
            } //if

            o.AppendLine("EndProject");
        }


        List<String> sortedConfs = Project.getSortedConfigurations(configurations, false, null, true);

        //
        // Dump configurations.
        //
        o.AppendLine("Global");
        o.AppendLine("	GlobalSection(SolutionConfigurationPlatforms) = preSolution");
        foreach (String cfg in sortedConfs)
        {
            o.AppendLine("		" + cfg + " = " + cfg);
        }
        o.AppendLine("	EndGlobalSection");


        //
        // Dump solution to project configuration mapping and whether or not to build specific project.
        //
        o.AppendLine("	GlobalSection(ProjectConfigurationPlatforms) = postSolution");
        foreach (Project p in projects)
        {
            if (p.IsSubFolder())       // If sub-folder no need to list it here.
                continue;

            List<String> projConfs = p.getConfigurationNames();
            List<String> projPlatforms = p.getPlatforms();

            foreach (String conf in sortedConfs)
            {
                int iConf = configurations.IndexOf(conf);
                String mappedConf = conf;

                bool bPeformBuild = true;
                bool? bPerformDeploy = null;

                if (p.bIsPackagingProject)
                    bPerformDeploy = true;

                if (p.slnConfigurations != null && iConf < p.slnConfigurations.Count)
                {
                    // Mapped configuration item is specified.
                    mappedConf = p.slnConfigurations[iConf];
                }
                else
                {
                    if (p.bDefinedAsExternal)
                    {
                        // Hack - assume one to one mapping for timebeing.
                        mappedConf = conf;
                    }
                    else
                    {
                        //
                        // Try to map configuration by ourselfs. Map x86 to Win32 automatically.
                        //
                        if (!p.configurations.Contains(conf))
                        {
                            String[] confPlat = conf.Split('|');

                            if (projConfs.Contains(confPlat[0]) && confPlat[1] == "x86" && projPlatforms.Contains("Win32"))
                            {
                                mappedConf = confPlat[0] + '|' + "Win32";
                            }
                            else
                            {
                                // Configuration cannot be mapped (E.g. Solution has "Debug|Arm", project supports only "Debug|Win32".
                                // We disable project build, but try to map configuration anyway - otherwise Visual Studio will 
                                // try to save solution by itself.
                                bPeformBuild = false;
                                bPerformDeploy = null;

                                mappedConf = p.configurations.Where(x => x.StartsWith(confPlat[0])).FirstOrDefault();
                                if (mappedConf == null)
                                    mappedConf = p.configurations[0];
                            } //if-else
                        } //if
                    }
                }

                if (p.slnBuildProject != null && iConf < p.slnBuildProject.Count)
                    bPeformBuild = p.slnBuildProject[iConf];

                if (p.slnDeployProject != null && iConf < p.slnConfigurations.Count)
                    bPerformDeploy = p.slnDeployProject[iConf];

                o.AppendLine("		" + p.ProjectGuid.ToUpper() + "." + conf + ".ActiveCfg = " + mappedConf);
                if (bPeformBuild)
                    o.AppendLine("		" + p.ProjectGuid.ToUpper() + "." + conf + ".Build.0 = " + mappedConf);

                if (bPerformDeploy.HasValue && bPerformDeploy.Value)
                    o.AppendLine("		" + p.ProjectGuid.ToUpper() + "." + conf + ".Deploy.0 = " + mappedConf);

            } //for
        } //foreach
        o.AppendLine("	EndGlobalSection");
        o.AppendLine("	GlobalSection(SolutionProperties) = preSolution");
        o.AppendLine("		HideSolutionNode = FALSE");
        o.AppendLine("	EndGlobalSection");

        //
        // Dump project dependency hierarchy.
        //
        Project root = projects.FirstOrDefault();

        if (root != null)
        {
            while (root.parent != null) root = root.parent;

            //
            // Flatten tree without recursion.
            //
            int treeIndex = 0;
            List<Project> projects2 = new List<Project>();
            projects2.AddRange(root.nodes);

            for (; treeIndex < projects2.Count; treeIndex++)
            {
                if (projects2[treeIndex].nodes.Count == 0)
                    continue;
                projects2.AddRange(projects2[treeIndex].nodes);
            }

            bool bDump = projects2.Count(x => x.parent.parent != null) != 0;

            if (bDump)
                o.AppendLine("	GlobalSection(NestedProjects) = preSolution");

            foreach (Project p in projects2)
            {
                if (p.parent.parent == null)
                    continue;
                o.AppendLine("		" + p.ProjectGuid.ToUpper() + " = " + p.parent.ProjectGuid.ToUpper());
            }

            if (bDump)
                o.AppendLine("	EndGlobalSection");
        } //if

        if (SolutionGuid != null)
        {
            o.AppendLine("	GlobalSection(ExtensibilityGlobals) = postSolution");
            o.AppendLine("		SolutionGuid = " + SolutionGuid);
            o.AppendLine("	EndGlobalSection");
        }

        o.AppendLine("EndGlobal");

        String currentSln = "";
        if (File.Exists(slnPath)) currentSln = File.ReadAllText(slnPath);

        String newSln = o.ToString();
        //
        // Save only if needed.
        //
        if (currentSln == newSln)
        {
            uinfo.MarkFileUpdated(slnPath, false);
        }
        else
        {
            if (SolutionProjectBuilder.isDeveloper() && File.Exists(slnPath)) File.Copy(slnPath, slnPath + ".bkp", true);
            File.WriteAllText(slnPath, newSln, Encoding.UTF8);
            uinfo.MarkFileUpdated(slnPath, true);
        } //if-else
    } //SaveSolution


    /// <summary>
    /// Removes empty folder nodes from solution.
    /// </summary>
    /// <param name="p">Must be null on first call</param>
    /// <returns>true if p was removed, should not be used by caller</returns>
    public bool RemoveEmptyFolders(Project p = null)
    {
        if (p == null)
            p = solutionRoot;

        for (int i = 0; i < p.nodes.Count; i++)
        {
            var p2 = p.nodes[i];
            if (p2.bIsFolder)
                if (RemoveEmptyFolders(p2))
                    i--;
        }

        if (p.nodes.Count == 0 && p.bIsFolder)
        {
            p.parent.nodes.Remove(p);
            p.parent = null;
            projects.Remove(p);
            return true;
        }

        return false;
    }

    /// <summary>
    /// By default after solution is loaded - all dependent projects are specified by project guids.
    /// This function will replace project guids with project names.
    /// </summary>
    public void ChangeProjectDependenciesFromGuidsToNames()
    {
        foreach (Project p in projects)
        {
            if (p.ProjectDependencies == null)
                continue;

            for (int i = 0; i < p.ProjectDependencies.Count; i++)
            {
                Project depp = projects.Where(x => x.ProjectGuid == p.ProjectDependencies[i]).FirstOrDefault();
                if (depp == null)
                    throw new Exception("Project '" + p.ProjectName + "' has dependency on project guid '" + p.ProjectDependencies[i] + "' which does not exists in solution");

                p.ProjectDependencies[i] = depp.ProjectName;
            }
        }
    }


    /// <summary>
    /// Lambda function gets called for each project, return true or false to enable/disable project build, null if don't change
    /// </summary>
    /// <param name="func">Callback function, which selects / deselects project for building</param>
    public void EnableProjectBuild(Func<Project, bool?> func)
    {
        foreach (Project p in projects)
        {
            bool? enable = func(p);

            if (!enable.HasValue)
                continue;

            var l = p.slnBuildProject;
            for (int i = 0; i < l.Count; i++)
                l[i] = enable.Value;
        }
    }


    /// <summary>
    /// Clone Solution.
    /// </summary>
    /// <returns>new solution</returns>
    public Solution Clone()
    {
        Solution s = (Solution)ReflectionEx.DeepClone(this);

        for (int iProject = 0; iProject < s.projects.Count; iProject++)
        {
            Project newp = s.projects[iProject];
            newp.solution = s;
            Project oldp = projects[iProject];

            // References either solutionRoot or some of project.
            if (oldp.parent == solutionRoot)
            {
                newp.parent = s.solutionRoot;
                s.solutionRoot.nodes.Add(newp);
            }
            else
                newp.parent = s.projects[projects.IndexOf(oldp.parent)];

            // References solution projects.
            for (int i = 0; i < oldp.nodes.Count; i++)
                newp.nodes.Add(s.projects[projects.IndexOf(oldp.nodes[i])]);
        }

        return s;
    }


} //class Solution

