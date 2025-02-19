﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections;
using System.Runtime.CompilerServices;
using Microsoft.Build.Utilities;

/// <summary>
/// Helper class for generating solution or projects.
/// </summary>
public class SolutionProjectBuilder
{
    /// <summary>
    /// List of solutions being generated.
    /// </summary>
    public static List<Solution> m_solutions = new List<Solution>();

    /// <summary>
    /// Currently selected active solution on which all function below operates upon. null if not selected yet.
    /// </summary>
    public static Solution m_solution = null;

    /// <summary>
    /// Currently selected active project on which all function below operates upon. null if not selected yet.
    /// </summary>
    public static Project m_project = null;

    /// <summary>
    /// project which perform update of solution, all newly added projects must be dependent on it.
    /// </summary>
    static String solutionUpdateProject = "";

    /// <summary>
    /// Path where we are building solution / project at. By default same as script is started from.
    /// </summary>
    public static String m_workPath;

    /// <summary>
    /// Sets currently work path (where we are building project / solutions )
    /// if null is passed as input parameter, sets script's directory as working path.
    /// </summary>
    /// <param name="path">Path relative or aboslute, null if relative to calling script</param>
    public static void setWorkPath(String path = null)
    {
        if (path == null)
        {
            m_workPath = Path.GetDirectoryName(Path2.GetScriptPath(2));
            return;
        }

        if (!Path.IsPathRooted(path))
            path = Path.GetFullPath(Path.Combine(getExecutingScript(), path));

        if (!Directory.Exists(path))
            throw new Exception2("Path '" + Exception2.getPath(path) + "' does not exists", 1);

        m_workPath = path;
    }

    /// <summary>
    /// Intialize testing settings from command line arguments.
    /// </summary>
    /// <param name="args"></param>
    public static void initFromArgs(params String[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            String arg = args[i];

            if (!(arg.StartsWith("-") || arg.StartsWith("/")))
                continue;

            switch (arg.Substring(1).ToLower())
            {
                case "x": Exception2.g_bReportFullPath = false; break;
            }
        } //foreach
    }

    /// <summary>
    /// Full path from where current execution proceeds (location of .cs script)
    /// </summary>
    public static String m_currentlyExecutingScriptPath = "";

    /// <summary>
    /// Gets directory and/or filename under which script is currently executing. This is either determined from script executed by invokeScript
    /// or C# initial script location (script which created SolutionProjectBuilder)
    /// </summary>
    /// <returns>Empty string if no script is yet executing</returns>
    public static String getExecutingScript(bool bDirectory = true, bool bFilename = false, bool bExtension = false)
    {
        if (m_currentlyExecutingScriptPath == "")
            return "";

        if (bDirectory && bFilename)
            return m_currentlyExecutingScriptPath;

        if (bDirectory && !bFilename)
            return Path.GetDirectoryName(SolutionProjectBuilder.m_currentlyExecutingScriptPath);

        if (bExtension)
            return Path.GetFileName(SolutionProjectBuilder.m_currentlyExecutingScriptPath);

        return Path.GetFileNameWithoutExtension(SolutionProjectBuilder.m_currentlyExecutingScriptPath);
    }

    /// <summary>
    /// Gets currently executing C# script directory
    /// </summary>
    public static String getCsPath([CallerFilePath] string path = "")
    {
        return path;
    }

    /// <summary>
    /// Gets currently executing C# script directory
    /// </summary>
    public static String getCsDir([CallerFilePath] string path = "")
    {
        return Path.GetDirectoryName(path);
    }

    /// <summary>
    /// Gets currently executing C# script filename
    /// </summary>
    public static String getCsFileName([CallerFilePath] string path = "")
    {
        return Path.GetFileName(path);
    }

    /// <summary>
    /// Relative directory from solution. Set by RunScript.
    /// </summary>
    public static String m_scriptRelativeDir = "";
    static List<String> m_platforms = new List<String>();
    static List<String> m_configurations = new List<String>(new String[] { "Debug", "Release" });
    /// <summary>
    /// If generating only project, this is global root
    /// </summary>
    static Project m_solutionRoot = new Project();
    static String m_groupPath = "";
    private static readonly Destructor Finalise = new Destructor();

    static SolutionProjectBuilder()
    {
        String path = Path2.GetScriptPath(2);

        //
        // We could have couple of issues here. SolutionProjectBuilder can be instantiated directly or indirectly -
        // in this case 2 or 3 varies. But additionally to that we might have or not have syncProj.pdb debug symbols
        // causing path to be resolved (incorrectly) or not resolved (null)
        //  See CsCsInvoke.cs & CsCsInvoke3.cs differences.
        //
        if (path == null || Path.GetFileNameWithoutExtension(path).ToLower() == "solutionprojectbuilder")
            path = Path2.GetScriptPath(3);

        if (path == null)   // If instantiated via C# script project
            path = Path2.GetScriptPath(4);

        if (path != null)
            m_workPath = Path.GetDirectoryName(path);
        //Console.WriteLine(m_workPath);
    }

    /// <summary>
    /// true if to save generated solutions or projects.
    /// </summary>
    public static bool bSaveGeneratedProjects = true;

    /// <summary>
    /// Forces to save generated solution and projects.
    /// </summary>
    public static void SaveGenerated(bool bForce = false)
    {
        try
        {
            if (!bSaveGeneratedProjects && !bForce)
                return;

            UpdateInfo uinfo = new UpdateInfo(true);

            if (m_solutions.Count != 0)
            {
                // Flush project into solution.
                externalproject(null);

                foreach (Solution s in m_solutions)
                {
                    m_solution = s;     // Select just in case if someone is using static generation functions.    
                    s.SaveSolution(uinfo);

                    foreach (Project p in s.projects)
                        if (!p.bDefinedAsExternal)
                            p.SaveProject(uinfo);
                }
            }
            else
            {
                if (m_project != null)
                    m_project.SaveProject(uinfo);
                else
                {
                    Console.WriteLine("No solution or project defined. Use project() or solution() functions in script");
                }
            }

            uinfo.DisplaySummary();

            m_solution = null;
            m_project = null;
            resetConfigurations();
        }
        catch (Exception ex)
        {
            ConsolePrintException(ex);
        }
    }

    /// <summary>
    /// Execute once for each invocation of script. Not executed if multiple scripts are included.
    /// </summary>
    private sealed class Destructor
    {
        ~Destructor()
        {
            SaveGenerated();
        }
    }

    /// <summary>
    /// Creates new solution.
    /// </summary>
    /// <param name="name">Solution name</param>
    public static void solution(String name)
    {
        // Flush last project if any was generated, so it would end up into correct solution
        externalproject(null);

        Solution sln2select = m_solutions.Where(x => x.name == name).FirstOrDefault();

        if (sln2select == null)
        {
            m_solution = new Solution() { name = name };
            m_solution.path = Path.Combine(m_workPath, name);
            if (!m_solution.path.EndsWith(".sln"))
                m_solution.path += ".sln";

            m_solutions.Add(m_solution);
        }
        else
        {
            m_solution = sln2select;
        }
    }

    static void requireSolutionSelected(int callerFrame = 0)
    {
        if (m_solution == null)
            throw new Exception2("Solution not specified (Use solution(\"name\" to specify new solution)", callerFrame + 1);
    }

    static void requireProjectSelected(int callerFrame = 0)
    {
        if (m_project == null)
            throw new Exception2("Project not specified (Use project(\"name\" to specify new project)", callerFrame + 1);
    }

    static void generateConfigurations()
    {
        // Generating configurations for solution
        if (m_project == null)
        {
            requireSolutionSelected(1);

            m_solution.configurations.Clear();

            foreach (String platform in m_platforms)
                foreach (String configuration in m_configurations)
                    m_solution.configurations.Add(configuration + "|" + platform);
        }
        else
        {
            requireProjectSelected(1);

            if (m_project.projectConfig.Count != 0)
                throw new Exception2("You must use platforms() or configurations() before you start to configure project using other functions", 2);

            m_project.configurations.Clear();

            foreach (String platform in m_platforms)
                foreach (String configuration in m_configurations)
                    m_project.configurations.Add(configuration + "|" + platform);
        }
    }

    /// <summary>
    /// Specify platform list to be used for your solution or project.
    ///     For example: platforms("x86", "x64");
    /// </summary>
    /// <param name="platformList">List of platforms to support</param>
    public static void platforms(params String[] platformList)
    {
        m_platforms = platformList.Distinct().ToList();
        generateConfigurations();
    }

    /// <summary>
    /// Specify which configurations to support. Typically "Debug" and "Release".
    /// </summary>
    /// <param name="configurationList">Configuration list to support</param>
    public static void configurations(params String[] configurationList)
    {
        m_configurations = configurationList.Distinct().ToList();
        generateConfigurations();
    }


    /// <summary>
    /// Generates Guid based on String. Key assumption for this algorithm is that name is unique (across where it it's being used)
    /// and if name byte length is less than 16 - it will be fetched directly into guid, if over 16 bytes - then we compute sha-1
    /// hash from string and then pass it to guid.
    /// </summary>
    /// <param name="name">Unique name which is unique across where this guid will be used.</param>
    /// <returns>For example "{706C7567-696E-7300-0000-000000000000}" for "plugins"</returns>
    public static String GenerateGuid(String name)
    {
        byte[] buf = Encoding.UTF8.GetBytes(name);
        byte[] guid = new byte[16];
        if (buf.Length < 16)
        {
            Array.Copy(buf, guid, buf.Length);
        }
        else
        {
            using (SHA1 sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(buf);
                // Hash is 20 bytes, but we need 16. We loose some of "uniqueness", but I doubt it will be fatal
                Array.Copy(hash, guid, 16);
            }
        }

        // Don't use Guid constructor, it tends to swap bytes. We want to preserve original string as hex dump.
        String guidS = "{" + String.Format("{0:X2}{1:X2}{2:X2}{3:X2}-{4:X2}{5:X2}-{6:X2}{7:X2}-{8:X2}{9:X2}-{10:X2}{11:X2}{12:X2}{13:X2}{14:X2}{15:X2}",
            guid[0], guid[1], guid[2], guid[3], guid[4], guid[5], guid[6], guid[7], guid[8], guid[9], guid[10], guid[11], guid[12], guid[13], guid[14], guid[15]) + "}";

        return guidS;
    }


    static void specifyproject(String name)
    {
        if (m_project != null)
        {
            if (m_solution != null)
            {
                if (!m_solution.projects.Contains(m_project))
                    m_solution.projects.Add(m_project);
            }
            else
                // We are collecting projects only (no solution) and this is not last project
                if (name != null)
                m_project.SaveProject(new UpdateInfo(true));
        }

        // Release active project
        m_project = null;
        resetConfigurations();

        if (name == null)       // Will be used to "flush" last filled project.
        {
            return;
        }

        if (m_solution != null)
        {
            m_project = m_solution.projects.Where(x => x.ProjectName == name).FirstOrDefault();

            // Selecting already specified project.
            if (m_project != null)
                return;
        }

        m_project = new Project() { solution = m_solution };

        if (solutionUpdateProject != "")
            dependson(solutionUpdateProject);

        m_project.ProjectName = name;
        m_project.language = "C++";
        String path = Path.Combine(m_scriptRelativeDir, name);
        path = new Regex(@"[^\\/]+[\\/]\.\.[\\/]").Replace(path, "");     // Remove extra upper folder references if any
        m_project.RelativePath = path;

        Project parent = m_solutionRoot;
        if (m_solution != null) parent = m_solution.solutionRoot;

        String pathSoFar = "";

        // Result of group() expansion - try to create correspondent "virtual projects / folders"

        if (m_solution != null)
        {
            foreach (String pathPart in m_groupPath.Split(new char[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Project p = parent.nodes.Where(x => x.ProjectName == pathPart && x.RelativePath == pathPart).FirstOrDefault();
                pathSoFar = pathSoFar + ((pathSoFar.Length != 0) ? "/" : "") + pathPart;
                if (p == null)
                {
                    // GenerateGuid uses extra garbage so group name and project name uuid's won't collide.
                    p = new Project() { solution = m_solution, ProjectName = pathPart, RelativePath = pathPart, ProjectGuid = GenerateGuid("!#¤%!#¤%" + pathSoFar), bIsFolder = true };
                    m_solution.projects.Add(p);
                    parent.nodes.Add(p);
                    p.parent = parent;
                }

                parent = p;
            }
        } //if

        parent.nodes.Add(m_project);
        m_project.parent = parent;
    }

    /// <summary>
    /// Add to solution reference to external project. Call with null parameter to flush currently active project (Either to solution
    /// or to disk).
    /// </summary>
    /// <param name="name">Project name</param>
    public static void externalproject(String name)
    {
        specifyproject(name);
        if (name != null)
            m_project.bDefinedAsExternal = true;
    }

    /// <summary>
    /// Adds new project to solution
    /// </summary>
    /// <param name="name">Project name</param>
    public static void project(String name)
    {
        specifyproject(name);

        if (name != null)
            m_project.bDefinedAsExternal = false;
    }


    /// <summary>
    /// Selects default toolset after vsver or kind project definition.
    /// </summary>
    static void selectDefaultToolset(bool force = false)
    {
        if (m_project.Keyword == EKeyword.Win32Proj || m_project.Keyword == EKeyword.MFCProj || m_project.Keyword == EKeyword.None)
        {
            // We can specify version before configurations / platforms were selected.
            if (m_project.projectConfig.Count == 0)
                return;

            using (new UsingSyncProj(4))
            {
                switch (m_project.fileFormatVersion)
                {
                    case 2010: toolset("v100", force); break;
                    case 2012: toolset("v110", force); break;
                    case 2013: toolset("v120", force); break;
                    case 2015: toolset("v140", force); break;
                    case 2017:
                        toolset("v141", force);
                        systemversion("10.0.17134.0", force);       // Newer visual studio's also enforces specific Windows SDK version
                        break;
                    case 2019:
                        toolset("v141", force);
                        systemversion("10.0.17763.0", force);
                        break;
                    default:
                        // Try to guess the future. 2020 => "v160" ?
                        toolset("v" + (((m_project.fileFormatVersion - 2020) + 16) * 10).ToString());
                        break;
                } //switch
            } //using
        } //if
    }

    /// <summary>
    /// Sets Visual Studio file format version to be used.
    /// </summary>
    /// <param name="vsVersion">2010, 2012, 2013, ...</param>
    /// <param name="force">set to true if besides selecting visual studio project format version, also set default toolset for specific project</param>
    public static void vsver(int vsVersion, bool force = true)
    {
        if (m_solution == null && m_project == null)
            requireSolutionSelected();

        if (m_project != null)
        {
            m_project.SetFileFormatVersion(vsVersion);

            // Reselect default toolset, if file format version changed.
            selectDefaultToolset(force);
        }
        else
        {
            m_solution.fileFormatVersion = vsVersion;
        }
    } //vsver


    /// <summary>
    /// Sets visual studio version
    /// </summary>
    public static void VisualStudioVersion(String v, bool force = true)
    {
        requireSolutionSelected();
        if (!force && m_solution.VisualStudioVersion != null)
            return;
        m_solution.VisualStudioVersion = v;
    }

    /// <summary>
    /// Sets minimum visual studio version
    /// </summary>
    public static void MinimumVisualStudioVersion(String v, bool force = true)
    {
        requireSolutionSelected();
        if (!force && m_solution.MinimumVisualStudioVersion != null)
            return;
        m_solution.MinimumVisualStudioVersion = v;
    }


    /// <summary>
    /// The location function sets the destination directory for a generated solution or project file.
    /// </summary>
    /// <param name="_path"></param>
    public static void location(String _path)
    {
        if (m_project == null)
        {
            setWorkPath(_path);
        }
        else
        {
            String absPath = _path;

            if (!Path.IsPathRooted(_path))
                absPath = Path.Combine(m_project.getProjectFolder(), _path);

            if (!m_project.bDefinedAsExternal && !Directory.Exists(absPath))
                throw new Exception2("Path '" + _path + "' does not exists");

            // Measure relative path against solution path if that one is present or against working path.
            String solPath = m_workPath;
            if (m_project.solution != null)
                solPath = Path.GetDirectoryName(m_project.solution.path);

            // Always recalculate relative directory, since it might change if solution is not specified.
            String dir = Path2.makeRelative(absPath, solPath);
            m_project.RelativePath = Path.Combine(dir, m_project.ProjectName);

        }
    }

    /// <summary>
    /// Sets project / solution generate location to be the same as script current location
    /// </summary>
    /// <param name="subDir">Additional sub-directory if any</param>
    public static void setLocationFromScriptPath(String subDir = null)
    {
        String scriptPath = Path2.GetScriptPath(2 /* 1 - this function, + 1 - One function call back */);
        String dir = Path.GetDirectoryName(scriptPath);
        if (subDir != null)
            dir = Path.Combine(dir, subDir);
        location(dir);
    }


    /// <summary>
    /// Specifies project or solution uuid - could be written in form of normal guid ("{5E40B384-095E-452A-839D-E0B62833256F}")
    /// - use this kind of syntax if you need to produce your project in backwards compatible manner - so existing
    /// solution can load your project.
    /// Could be also written in any string form, but you need to take care that string is unique enough accross where your
    /// project is used. For example project("test1"); uuid("test"); project("test1"); uuid("test"); will result in
    /// two identical project uuid's and Visual studio will try to resave your project with changed uuid.
    /// </summary>
    /// <param name="nameOrUuid">Project uuid or some unique name</param>
    public static void uuid(String nameOrUuid)
    {
        bool forceGuid = nameOrUuid.Contains('{') || nameOrUuid.Contains('}');
        String uuid = "";

        var guidMatch = SolutionProjectBuilder.guidMatcher.Match(nameOrUuid);
        if (guidMatch.Success)
        {
            uuid = "{" + guidMatch.Groups[1].Value + "}";
        }
        else
        {
            if (forceGuid)
                throw new Exception2("Invalid uuid value '" + nameOrUuid + "'");

            uuid = GenerateGuid(nameOrUuid);
        }

        if (m_solution != null)
        {
            Project p = m_solution.projects.Where(x => x.ProjectGuid == uuid).FirstOrDefault();
            if (p != null)
                throw new Exception2("uuid '" + uuid + "' is already used by project '" +
                    p.ProjectName + "' - please consider using different uuid. " +
                    "For more information refer to uuid() function description.");
        }

        if (m_project == null && m_solution == null)
            // You must have at least something selected.
            requireProjectSelected();

        if (m_project != null)
            m_project.ProjectGuid = uuid;
        else
            m_solution.SolutionGuid = uuid;

    } //uuid

    /// <summary>
    /// Sets project programming language (reflects to used project extension if used for non-file specific configuration)
    /// </summary>
    /// <param name="lang">C, C++, C#, if no parameter is specified (null), sets project to autodetect programming language</param>
    public static void language(String lang = null)
    {
        requireProjectSelected();
        ECompileAs compileAs = ECompileAs.Default;

        switch (lang)
        {
            case null:
                compileAs = ECompileAs.Default;
                lang = "C++";       // File extension will be .vcxproj anyway, but to what to compile file will be left to VS.
                break;
            case "C":
                compileAs = ECompileAs.CompileAsC;
                break;
            case "C++":
                compileAs = ECompileAs.CompileAsCpp;
                break;
            case "C#":
                break;
            default:
                throw new Exception2("Language '" + lang + "' is not supported");
        } //switch

        if (!m_project.bDefinedAsExternal)
        {
            // Set up default compilation language
            foreach (var conf in getSelectedConfigurations(false))
                conf.CompileAs = compileAs;
        }

        if (!bLastSetFilterWasFileSpecific)
            m_project.language = lang;
    } //language

    public static Regex guidMatcher = new Regex("^[{(]?([0-9A-Fa-f]{8}[-][0-9A-Fa-f]{4}[-][0-9A-Fa-f]{4}[-][0-9A-Fa-f]{4}[-][0-9A-Fa-f]{12})[)}]?$");

    /// <summary>
    /// Specify one or more non-linking project build order dependencies.
    /// </summary>
    /// <param name="dependencies">List of dependent projects</param>
    public static void dependson(params String[] dependencies)
    {
        requireProjectSelected();

        if (m_project.ProjectDependencies == null)
            m_project.ProjectDependencies = new List<string>();

        m_project.ProjectDependencies.AddRange(dependencies);
    } //dependson

    /// <summary>
    /// Specifies reference to another project. As an input you should provide project path, with optional project guid.
    /// If project guid is not provided, it will be loaded from project itself. (Slight performance penalties)
    /// </summary>
    /// <param name="fileGuidList">Project file path + project guid list</param>
    public static void referencesProject(params String[] fileGuidList)
    {
        requireProjectSelected();

        for (int i = 0; i < fileGuidList.Length; i++)
        {
            String relPath = fileGuidList[i];
            String path = Path.Combine(m_project.getProjectFolder(), relPath);

            String guid = null;

            if (i + 1 < fileGuidList.Length)
            {
                guid = fileGuidList[i + 1];

                Match mGuid = guidMatcher.Match(guid);
                if (!mGuid.Success)
                {
                    String nameForGuid = guid;
                    if (nameForGuid.Length == 0)
                        nameForGuid = Path.GetFileNameWithoutExtension(relPath);

                    guid = GenerateGuid(nameForGuid);
                }
                else
                {
                    guid = "{" + mGuid.Groups[1].Value + "}";
                }
                i++;
            } //if

            if (guid == null)
            {
                if (!File.Exists(path))
                    throw new Exception2("Referenced project '" + Exception2.getPath(path) + "' does not exists.\r\n" +
                        "You can avoid loading project by specifying project guid after project path, for example:\r\n" +
                        "\r\n" +
                        "    referencesProject(\"project.vcxproj\", \"{E3A9D624-DA07-DBBF-B4DD-0E33BE2390AE}\" ); \r\n" +
                        "\r\n" +
                        "or if you're using syncProj on that project:\r\n" +
                        "\r\n" +
                        "    referencesProject(\"project.vcxproj\", \"unique name\" ); or \r\n" +
                        "    referencesProject(\"project.vcxproj\", \"\" );  - same as referencesProject(\"project.vcproj\", \"project\" );"
                        );

                guid = Project.getProjectGuid(path);
            }

            using (new UsingSyncProj(1))
                files(relPath);
            FileInfo fi = m_project.files.Last();
            fi.includeType = IncludeType.ProjectReference;
            fi.Project = guid;
        } //for
    }


    /// <summary>
    /// For C++/cli - adds reference to specific assemblies.
    /// </summary>
    /// <param name="assemblyNamesAndParameters">List of assembly names, for example "System" or path to specific assembly. use '?' as first character to
    /// supress assembly name check. Assemby name can be followed by extra 0-3 booleans to configure assembly copy options.
    /// Once you use booleans - they are applied to all previously listed assemblies. Make separate calls to distingvish different options.
    /// 
    /// references("System", "System.Data");
    /// references("my1.dll", "my2.dll", false);   Copy local will be disabled for both dll's.
    /// 
    /// </param>
    public static void references(params object[] assemblyNamesAndParameters)
    {
        requireProjectSelected();
        List<Tuple<String, bool>> dirs = null;
        String[] referenceFields = new[] { "HintPath", "Private", "CopyLocalSatelliteAssemblies", "ReferenceOutputAssembly" };
        int iFilesFromIndex = m_project.files.Count;

        for (int i = 0; i < assemblyNamesAndParameters.Length; i++)
        {
            String name = assemblyNamesAndParameters[i].ToString();
            bool bCheck = true;
            if (name.StartsWith("?"))
            {
                bCheck = false;
                name = name.Substring(1);
            }

            bool bIsHintPath = name.IndexOf('\\') != -1 || Path.GetExtension(name) != "";

            if (bCheck)
            {
                if (dirs == null)
                {
                    String netVer = m_project.TargetFrameworkVersion;
                    if (netVer == null) netVer = "v4.0";

                    var dotnetDirs = ToolLocationHelper.GetPathToReferenceAssemblies(".NETFramework", netVer, "");
                    if (dotnetDirs.Count == 0)
                        throw new Exception2("Cannot locate .NET Framework " + netVer + "directory, is it possible that it's not installed ?\r\n"
                            + "Use TargetFrameworkVersion() to specify correct .NET version");

                    dirs = dotnetDirs.Select(x => new Tuple<String, bool>(x, false)).ToList();
                    dirs.Add(new Tuple<String, bool>(m_project.getProjectFolder(), true));
                }


                bool bExists = false;
                if (Path.IsPathRooted(name))
                    bExists = File.Exists(name);
                else
                {
                    foreach (var dir in dirs)
                    {
                        bool bIsCustomDll = dir.Item2;
                        String dll = Path.Combine(dir.Item1, name);

                        // User will have to provide file extension, for .NET framework assemblies we try to add
                        // ".dll" automatically
                        if (!bIsCustomDll && Path.GetExtension(dll).ToLower() != ".dll")
                            dll += ".dll";

                        if (File.Exists(dll))
                        {
                            bIsHintPath = bIsCustomDll;
                            bExists = true;
                            break;
                        }
                    }
                }

                if (!bExists)
                {
                    String paths = String.Join("\r\n", dirs.Select(x => "    " + x.Item1).ToArray());
                    throw new Exception2("Assembly referred by name '" + name + "' was not found.\r\n\r\n" +
                        "was searching within following paths: \r\n" + paths);
                }
            }

            FileInfo fi = new FileInfo();
            fi.includeType = IncludeType.Reference;
            if (bIsHintPath)
            {
                //
                // Theoretically name should be resolved from assembly name, which can be different from
                // .dll filename (Only in .net you have "System" assembly name => "System.dll" assembly filename)
                // But when loading project visual studio ignores 'Include=<name>' and replaces it with
                // correct assembly name. From our perspective it does not matter as VS does not try to save project
                // back
                //
                fi.relativePath = Path.GetFileNameWithoutExtension(name);
                fi.HintPath = name;
            }
            else
            {
                fi.relativePath = name;
            }
            m_project.files.Add(fi);

            //
            // If next parameter is boolean, we mark all freshly added files with same copy options
            //
            if (i + 1 < assemblyNamesAndParameters.Length && assemblyNamesAndParameters[i + 1] is bool)
            {
                bool Private = (bool)assemblyNamesAndParameters[++i];
                bool CopyLocalSatelliteAssemblies = true;
                bool ReferenceOutputAssembly = true;

                if (i + 1 < assemblyNamesAndParameters.Length && assemblyNamesAndParameters[i + 1] is bool)
                    CopyLocalSatelliteAssemblies = (bool)assemblyNamesAndParameters[++i];

                if (i + 1 < assemblyNamesAndParameters.Length && assemblyNamesAndParameters[i + 1] is bool)
                    ReferenceOutputAssembly = (bool)assemblyNamesAndParameters[++i];

                for (int iFile = iFilesFromIndex; iFile < m_project.files.Count; iFile++)
                {
                    fi = m_project.files[iFile];
                    fi.Private = Private;
                    fi.CopyLocalSatelliteAssemblies = CopyLocalSatelliteAssemblies;
                    fi.ReferenceOutputAssembly = ReferenceOutputAssembly;
                }

                iFilesFromIndex = m_project.files.Count;
            }
        }
    }


    static Regex unrecognizedEscapeSequence = new Regex("[\x00-\x0D]");

    static void validatePathName(String s)
    {
        var m = unrecognizedEscapeSequence.Match(s);
        if (m.Success)
            throw new Exception2("Unrecognized escape sequence:\r\n'" + s.Substring(0, m.Index) + "**** here ****'\r\n", 1);
    }

    /// <summary>
    /// Sets current "directory" where project should be placed.
    /// </summary>
    /// <param name="groupPath"></param>
    public static void group(String groupPath)
    {
        validatePathName(groupPath);
        m_groupPath = groupPath;
    }

    /// <summary>
    /// Invokes C# Script by source code path. If any error, exception will be thrown.
    /// </summary>
    /// <param name="path">c# script path</param>
    /// <param name="bCompileOnly">true if only to compile</param>
    public static void invokeScript(String path, bool bCompileOnly = false)
    {
        String errors = "";
        String dir;
        String fullPath = path;

        // Is not absolute path, then launch from currently executing .cs location
        if (!Path.IsPathRooted(fullPath))
        {
            // By default search from same place where script is.
            dir = SolutionProjectBuilder.getExecutingScript();

            if (dir == "")
                fullPath = Path.GetFullPath(path);
            else
                fullPath = Path.Combine(dir, path);
        }

        CsScript.RunScript(fullPath, bCompileOnly, true, out errors, "no_exception_handling");
    }


    /// <summary>
    /// Selected configurations (Either project global or file specific) selected by filter.
    /// selectedFileConfigurations is file specific, selectedConfigurations is project wide.
    /// </summary>
    static List<FileConfigurationInfo> selectedFileConfigurations = new List<FileConfigurationInfo>();
    static List<FileConfigurationInfo> selectedConfigurations = new List<FileConfigurationInfo>();
    static String[] selectedFilters = null;     // null if not set

    /// <summary>
    /// true if file specific filtering is active, false if not.
    /// </summary>
    public static bool bLastSetFilterWasFileSpecific = false;

    /// <summary>
    /// Resets static variables to be able to start next testing round.
    /// </summary>
    public static void resetStatics()
    {
        m_solutions = new List<Solution>();
        m_solution = null;
        m_project = null;
        solutionUpdateProject = "";
        m_workPath = null;
        m_scriptRelativeDir = "";
        m_platforms = new List<String>();
        m_configurations = new List<String>(new String[] { "Debug", "Release" });
        m_solutionRoot = new Project();
        m_groupPath = "";
        bSaveGeneratedProjects = true;

        resetConfigurations();
    }


    static void resetConfigurations()
    {
        selectedFileConfigurations = new List<FileConfigurationInfo>();
        selectedConfigurations = new List<FileConfigurationInfo>();
        selectedFilters = null;     // null if not set
        bLastSetFilterWasFileSpecific = false;
    }

    /// <summary>
    /// Gets currently selected configurations by filter.
    /// </summary>
    /// <param name="bForceNonFileSpecific">true to force project specific configuration set.</param>
    /// <param name="callerFrame">Tells how many call call frame behind is end-user code. (Non syncproj code). (Reflects to error reporting)</param>
    static List<FileConfigurationInfo> getSelectedConfigurations(bool bForceNonFileSpecific, int callerFrame = 0)
    {
        List<FileConfigurationInfo> list;

        if (bForceNonFileSpecific)
            list = selectedConfigurations;
        else
            list = (bLastSetFilterWasFileSpecific) ? selectedFileConfigurations : selectedConfigurations;

        if (list.Count == 0)
            filter();

        if (list.Count == 0)
            throw new Exception2("Please specify configurations() and platforms() before using this function. Amount of configurations and platforms must be non-zero.", callerFrame + 1);

        return list;
    }

    /// <summary>
    /// Gets project configuration list, throw exception if cannot be found
    /// </summary>
    static List<Configuration> getSelectedProjectConfigurations()
    {
        List<Configuration> projConfs = getSelectedConfigurations(true).Cast<Configuration>().Where(x => x != null).ToList();
        return projConfs;
    }


    /// <summary>
    /// Selects to which configurations to apply subsequent function calls (like "kind", "symbols", "files"
    /// and so on...)
    /// </summary>
    /// <param name="filters">
    ///     Either configuration name, for example "Debug" / "Release" or
    ///     by platform name, for example: "platforms:Win32", "platforms:x64"
    ///     or by file name, for example: "files:my.cpp"
    /// </param>
    public static void filter(params String[] filters)
    {
        requireProjectSelected();

        Dictionary<String, String> dFilt = new Dictionary<string, string>();

        foreach (String filter in filters)
        {
            String[] v = filter.Split(new char[] { ':' }, 2);

            if (v.Length == 1)
            {
                dFilt["configurations"] = v[0];
            }
            else
            {
                String key = v[0].ToLower();
                if (key != "configurations" && key != "platforms" && key != "files")
                    throw new Exception2("filter tag '" + key + "' is not supported");

                dFilt[key] = v[1];
            } //if-else
        } //for

        IList[] confs2scan;
        Type type;

        if (dFilt.ContainsKey("files"))
        {
            confs2scan = getCurrentProjectFiles(dFilt["files"]).Select(x => x.fileConfig).Cast<IList>().ToArray();
            type = typeof(FileConfigurationInfo);
            bLastSetFilterWasFileSpecific = true;
        }
        else
        {
            bLastSetFilterWasFileSpecific = false;
            type = typeof(Configuration);
            confs2scan = new IList[] { (IList)m_project.projectConfig };
        }

        String confMatchPatten;
        if (dFilt.ContainsKey("configurations"))
            confMatchPatten = "^" + dFilt["configurations"];
        else
            confMatchPatten = ".*?";

        confMatchPatten += "\\|";

        if (dFilt.ContainsKey("platforms"))
            confMatchPatten += dFilt["platforms"] + "$";
        else
            confMatchPatten += ".*";

        Regex reConfMatch = new Regex(confMatchPatten);

        if (bLastSetFilterWasFileSpecific)
            selectedFileConfigurations.Clear();
        else
            selectedConfigurations.Clear();

        if (m_project.configurations.Count == 0)
            throw new Exception2("You must specify configurations() and platforms() before using this function.");


        List<int> confIndexes = new List<int>();


        for (int i = 0; i < m_project.configurations.Count; i++)
            if (reConfMatch.Match(m_project.configurations[i]).Success)
                confIndexes.Add(i);

        bool bFilterResultsAreEmpty = confIndexes.Count == 0;
        if (bFilterResultsAreEmpty)
        {
            String s = "Filtering was done:\r\n";

            if (dFilt.ContainsKey("configurations"))
                s += "* by configuration pattern '" + dFilt["configurations"] + "', project has only following configurations: " + String.Join(",", m_project.getConfigurationNames()) + "\r\n";

            if (dFilt.ContainsKey("platforms"))
                s += "* by platform pattern '" + dFilt["platforms"] + "', project has only following platforms: " + String.Join(",", m_project.getPlatforms());

            throw new Exception2("Specified filter did not select any of configurations, please check the filter.\r\n" + s);
        }

        foreach (IList configItems in confs2scan)
        {
            for (int i = 0; i < m_project.configurations.Count; i++)
            {
                //
                // Add into list same amount of configurations as in m_project.configuration list.
                //
                while (i >= configItems.Count)
                {
                    // new Configuration or new FileConfigurationInfo depending whether it's project configuration or file configuration.
                    FileConfigurationInfo fci = (FileConfigurationInfo)Activator.CreateInstance(type);
                    fci.confName = m_project.configurations[i];
                    if (type == typeof(FileConfigurationInfo))
                        fci.Optimization = EOptimization.ProjectDefault;
                    configItems.Add(fci);
                }

                // We re-create configurations anyway, but will not include into our list if not selected.
                if (!confIndexes.Contains(i))
                    continue;

                if (bLastSetFilterWasFileSpecific)
                    selectedFileConfigurations.Add((FileConfigurationInfo)configItems[i]);
                else
                    selectedConfigurations.Add((FileConfigurationInfo)configItems[i]);
            } //for
        } //for

        if (!bLastSetFilterWasFileSpecific)
            selectedFilters = filters;
    } //filter

    /// <summary>
    /// Selects file specific configurations
    /// </summary>
    /// <param name="fi">File information upon which to select configurations.</param>
    public static void selectConfigurations(FileInfo fi)
    {
        bLastSetFilterWasFileSpecific = true;
        m_project.updateFileConfigurations(fi);
        selectedFileConfigurations = fi.fileConfig.ToList();
    }

    /// <summary>
    /// Removes files from project or disables them from particular build configuration (If selected via filter)
    /// </summary>
    /// <param name="filesToRemove">List of files to remove / disable</param>
    public static void removefiles(params String[] filesToRemove)
    {
        requireProjectSelected();

        foreach (String file in filesToRemove)
        {
            foreach (FileInfo fi in getCurrentProjectFiles(file, true))
            {
                if (!bLastSetFilterWasFileSpecific)
                {
                    m_project.files.Remove(fi);
                }
                else
                {
                    foreach (FileConfigurationInfo fci in fi.fileConfig)
                    {
                        if (selectedFileConfigurations.Contains(fci))
                            fci.ExcludedFromBuild = true;
                    }
                }
            }
        }
    } //removefiles


    /// <summary>
    /// Specifies application type, one of following: 
    /// </summary>
    /// <param name="_kind">
    /// "Application"                       - Window application,
    /// "DynamicLibrary", "SharedLib"       - .dll,
    /// "StaticLibrary", "StaticLib"        - Static library (.lib or .a),
    /// "Utility"                           - Utility project,
    /// "ConsoleApp", "ConsoleApplication"  - Console application.
    /// </param>
    /// <param name="os">
    /// "windows"                           - For Windows OS
    /// "android"                           - For Android / C++ project
    /// "antpackage"                        - (Android) Ant package
    /// "gradlepackage"                     - (Android) Gradle package
    /// </param>
    public static void kind(String _kind, String os = null)
    {
        requireProjectSelected();

        if (os == null) os = "windows";

        bool bIsPackagingProject = false;

        switch (os.ToLower())
        {
            case Project.keyword_Windows:
                if (m_project.Keyword == EKeyword.None)      // flags ("MFC") can also set this
                    m_project.Keyword = EKeyword.Win32Proj;
                break;
            case Project.keyword_Android:
                m_project.Keyword = EKeyword.Android;
                break;
            case Project.keyword_AntPackage:
                m_project.Keyword = EKeyword.AntPackage;
                bIsPackagingProject = true;
                break;
            case Project.keyword_GradlePackage:
                m_project.Keyword = EKeyword.GradlePackage;
                bIsPackagingProject = true;
                break;
            default:
                throw new Exception2("os value is not supported '" + os + "' - supported values are: windows, android, package");
        }
        m_project.bIsPackagingProject = bIsPackagingProject;

        EConfigurationType type;
        EConfigurationType[] enums = Enum.GetValues(typeof(EConfigurationType)).Cast<EConfigurationType>().ToArray();
        ESubSystem subsystem = ESubSystem.Windows;

        String[] enumStrings = enums.Select(x => x.ToString().ToLower()).ToArray();
        String kind = _kind.ToLower();
        int idx = Array.IndexOf(enumStrings, kind);

        if (idx == -1)
        {
            // Aliases
            if (kind == "sharedlib")
                idx = Array.IndexOf(enumStrings, "dynamiclibrary");

            if (kind == "staticlib")
                idx = Array.IndexOf(enumStrings, "staticlibrary");

            if (kind == "consoleapp")
                idx = Array.IndexOf(enumStrings, "consoleapplication");

            // obsolete: Provided here just for compatibility with premake5. "application" should be preferred.
            if (kind == "windowedapp")
                idx = Array.IndexOf(enumStrings, "application");
        }

        if (idx == -1)
            throw new Exception2("kind value is not supported '" + _kind + "' - supported values are: " + String.Join(",", enumStrings));

        type = enums[idx];
        switch (type)
        {
            case EConfigurationType.ConsoleApplication:
                type = EConfigurationType.Application;
                subsystem = ESubSystem.Console;
                break;

            case EConfigurationType.Utility:
                m_project.Keyword = EKeyword.None;
                break;

            default:
                break;
        }

        if (type == EConfigurationType.Utility)
        {
            foreach (String _platform in m_project.getPlatforms())
            {
                String platform = _platform.ToLower();

                if (platform != "win32" && platform != "x64")
                    throw new Exception("Utility projects can be defined only on platform 'Win32' or 'x64' - not others.");
            }
        }

        if (!m_project.bDefinedAsExternal)
        {
            foreach (var conf in getSelectedProjectConfigurations())
            {
                conf.ConfigurationType = type;
                conf.SubSystem = subsystem;
            }
        }

        selectDefaultToolset();
        toolsetCheck();
    } //kind

    /// <summary>
    /// Selects the compiler, linker, etc. which are used to build a project or configuration.
    /// </summary>
    /// <param name="toolset">
    /// For example:<para />
    ///     'v140' - for Visual Studio 2015.<para />
    ///     'v120' - for Visual Studio 2013.<para />
    /// </param>
    /// <param name="force">true - to force set, false - set only if not yet selected.</param>
    public static void toolset(String toolset, bool force = false)
    {
        foreach (var conf in getSelectedProjectConfigurations())
        {
            if (!force && conf.PlatformToolset != null)
                continue;

            conf.PlatformToolset = toolset;
        }

        toolsetCheck();
    }

    /// <summary>
    /// Checks that we are specifying correct toolset suitable for given os.
    /// </summary>
    static void toolsetCheck()
    {
        String[] supportedToolsets = null;

        if (m_project.Keyword == EKeyword.Android)
            supportedToolsets = new String[] { "Gcc_4_9", "Clang_3_8" };

        if (m_project.Keyword == EKeyword.Win32Proj || m_project.Keyword == EKeyword.MFCProj)
            supportedToolsets = new String[] {
                    "v90", "v100", "v110", "v110_xp", "v120", "v120_xp", "v140", "v140_xp", "v141", "v141_xp",
                    "v140_clang_c2",
                    // Theoretical vs2019 support :-)
                    "v160", "v160_xp"
            };

        if (supportedToolsets == null)  // Utility / Package projects does not have toolsets defined.
            return;

        for (int i = 0; i < m_project.configurations.Count; i++)
        {
            Configuration conf = m_project.projectConfig[i];
            String toolset = conf.PlatformToolset;

            if (toolset == null)    // Default, will be valid anyway.
                continue;

            if (!supportedToolsets.Contains(toolset))
                throw new Exception2(
                    "Your currently selected platform '" + m_project.getOs() + "' in configuration '" +
                    m_project.configurations[i] + "' does not support toolset '" + toolset + "'\r\n" +
                    "supported toolsets are: " + String.Join(",", supportedToolsets) + "\r\n" +
                    "Alternatively it's also possible that you need to specify os correctly in kind() function.\r\n",
                    1 /*Called from parent function*/
                );
        } //for
    } //toolsetCheck



    /// <summary>
    /// Sets current android api level. Default is "android-19".
    /// </summary>
    /// <param name="apilevel">Android api level</param>
    public static void androidapilevel(String apilevel)
    {
        foreach (var conf in getSelectedProjectConfigurations())
            conf.AndroidAPILevel = apilevel;
    }

    /// <summary>
    /// Sets specific STL library for Android platform.
    /// </summary>
    /// <param name="useofstl"></param>
    public static void useofstl(String useofstl)
    {
        var values = Configuration.UseOfStl_getSupportedValues();
        int index = values.IndexOf(useofstl);
        if (index == -1)
            throw new Exception2("Use of STL value '" + useofstl + "' is not supported / invalid.\r\nValid values are: " + String.Join(", ", values));

        EUseOfStl v = (EUseOfStl)Enum.Parse(typeof(EUseOfStl), typeof(EUseOfStl).GetEnumNames()[index]);

        foreach (var conf in getSelectedProjectConfigurations())
            conf.UseOfStl = v;
    }

    /// <summary>
    /// Sets Thumb or ARM mode for ARM configuration only.
    /// </summary>
    /// <param name="thumbMode">Thumb mode parameter</param>
    public static void thumbmode(EThumbMode thumbMode = EThumbMode.NotSpecified)
    {
        foreach (var conf in getSelectedProjectConfigurations())
            conf.ThumbMode = thumbMode;
    }


    /// <summary>
    /// Selects character set.
    /// </summary>
    /// <param name="charset">One of following: "Unicode", "Multibyte", "MBCS", "None"</param>
    public static void characterset(String charset)
    {
        ECharacterSet cs;
        switch (charset.ToLower())
        {
            case "notset": cs = ECharacterSet.NotSet; break;
            case "none": cs = ECharacterSet.NotSet; break;
            case "unicode": cs = ECharacterSet.Unicode; break;
            case "mbcs": cs = ECharacterSet.MultiByte; break;
            case "multibyte": cs = ECharacterSet.MultiByte; break;
            default:
                throw new Exception2("characterset value is not supported '" + charset + "' - supported values are: " +
                    String.Join(",", Enum.GetValues(typeof(ECharacterSet)).Cast<ECharacterSet>().Select(x => x.ToString())));
        } //switch

        foreach (var conf in getSelectedProjectConfigurations())
            conf.CharacterSet = cs;
    }

    /// <summary>
    /// Enables / disables CLR support
    /// </summary>
    /// <param name="clr">clr value</param>
    public static void commonLanguageRuntime(ECLRSupport clr)
    {
        List<Configuration> selConfs = getSelectedProjectConfigurations();
        List<Configuration> projConfs = m_project.projectConfig;

        bool bProjectSelected = selConfs.Count == projConfs.Count;

        if (bProjectSelected)
            for (int i = 0; i < selConfs.Count; i++)
                if (selConfs[i] != projConfs[i])
                {
                    bProjectSelected = false;
                    break;
                }

        if (bProjectSelected)
            m_project.CLRSupport = clr;

        foreach (var conf in selConfs)
        {
            if (clr == m_project.CLRSupport)
                conf.CLRSupport = ECLRSupport.None; //Project selected CLR settings, no need to configure per configuration
            else
                conf.CLRSupport = clr;

            m_project.optimize_symbols_recheck(conf);
        }
    }


    /// <summary>
    /// Enables incremental linking. 
    /// Enabling incremental linking increases size of produced executable or dll approximately by 50%.
    /// </summary>
    /// <param name="b">true - enable, false disable</param>
    public static void Linker_General_EnableIncrementalLinking(bool b)
    {
        foreach (var conf in getSelectedProjectConfigurations())
            conf.LinkIncremental = b;
    }

    /// <summary>
    /// Specifies output directory.
    /// </summary>
    public static void targetdir(String directory)
    {
        directory = directory.Replace("/", "\\");
        if (!directory.EndsWith("\\"))
            directory += "\\";

        foreach (var conf in getSelectedProjectConfigurations())
            conf.OutDir = directory;
    }

    /// <summary>
    /// Specifies intermediate Directory.
    /// </summary>
    /// <param name="directory">For example "$(Configuration)\" or "obj\$(Platform)\$(Configuration)\"</param>
    public static void objdir(String directory)
    {
        directory = directory.Replace("/", "\\");
        if (!directory.EndsWith("\\"))
            directory += "\\";

        foreach (var conf in getSelectedProjectConfigurations())
            conf.IntDir = directory;
    }

    /// <summary>
    /// Specifies target name. (Filename without extension)
    /// </summary>
    public static void targetname(String name)
    {
        foreach (var conf in getSelectedProjectConfigurations())
            conf.TargetName = name;
    }

    /// <summary>
    /// Specifies target file extension, including comma separator.
    /// </summary>
    /// <param name="extension">For example ".dll", ".exe"</param>
    public static void targetextension(String extension)
    {
        foreach (var conf in getSelectedProjectConfigurations())
            conf.TargetExt = extension;
    }

    /// <summary>
    /// Specifies the #include form of the precompiled header file name.
    /// </summary>
    /// <param name="file">header file</param>
    public static void pchheader(String file)
    {
        foreach (var conf in getSelectedProjectConfigurations())
        {
            conf.PrecompiledHeader = EPrecompiledHeaderUse.Use;
            conf.PrecompiledHeaderFile = file;
        }
    }

    /// <summary>
    /// Specifies the C/C++ source code file which controls the compilation of the header.
    /// </summary>
    /// <param name="file">precompiled source code which needs to be compiled</param>
    public static void pchsource(String file)
    {
        var bkp1 = selectedFileConfigurations;
        var bkp2 = bLastSetFilterWasFileSpecific;

        List<String> fileFilter = new List<string>();
        fileFilter.Add("files:" + file);

        if (selectedFilters != null)
            fileFilter.AddRange(selectedFilters);

        filter(fileFilter.ToArray());

        foreach (var conf in getSelectedConfigurations(false))
            conf.PrecompiledHeader = EPrecompiledHeaderUse.Create;

        selectedFileConfigurations = bkp1;
        bLastSetFilterWasFileSpecific = bkp2;
    } //pchsource

    /// <summary>
    /// Specified whether debug symbols are enabled or not.
    /// </summary>
    /// <param name="value">
    /// "on" - debug symbols are enabled<para />
    /// "off" - debug symbols are disabled<para />
    /// "fastlink" - debug symbols are enabled + faster linking enabled.<para />
    /// "fulldebug" - Generate Debug Information optimized for sharing and publishing<para />
    /// </param>
    public static void symbols(String value)
    {
        //
        //  symbols preconfigures multiple flags. premake5 also preconfigures multiple. Maybe later if needs to be separated - create separate functions calls for
        //  each configurable parameter.
        //
        EGenerateDebugInformation d;
        bool bUseDebugLibraries = false;
        switch (value.ToLower())
        {
            case "on": d = EGenerateDebugInformation.OptimizeForDebugging; bUseDebugLibraries = true; break;
            case "off": d = EGenerateDebugInformation.No; bUseDebugLibraries = false; break;
            case "fastlink": d = EGenerateDebugInformation.OptimizeForFasterLinking; bUseDebugLibraries = true; break;
            case "fulldebug": d = EGenerateDebugInformation.OptimizeForSharingAndPublishing; bUseDebugLibraries = true; break;
            default:
                throw new Exception2("Allowed symbols() values are: on, off, fastlink, fulldebug");
        }

        foreach (var conf in getSelectedProjectConfigurations())
        {
            conf.GenerateDebugInformation = d;
            conf.UseDebugLibraries = bUseDebugLibraries;
            m_project.optimize_symbols_recheck(conf);
        } //foreach
    }

    /// <summary>
    /// /ASSEMBLYDEBUG Emits the DebuggableAssembly attribute with debug information tracking and disables JIT optimizations
    /// </summary>
    /// <param name="b">true to enable, false to disable</param>
    public static void AssemblyDebug(bool b = true)
    {
        foreach (var conf in getSelectedProjectConfigurations())
            conf.AssemblyDebug = b;
    }


    /// <summary>
    /// Specifies additional include directories.
    /// </summary>
    /// <param name="dirs">List of additional include directories.</param>
    public static void includedirs(params String[] dirs)
    {
        foreach (var conf in getSelectedConfigurations(false))
        {
            if (conf.AdditionalIncludeDirectories.Length != 0)
                conf.AdditionalIncludeDirectories += ";";

            conf.AdditionalIncludeDirectories += String.Join(";", dirs);
        }
    }

    /// <summary>
    /// Specifies additional #using directories.
    /// </summary>
    /// <param name="dirs">List of additional #using directories.</param>
    public static void usingdirs(params String[] dirs)
    {
        foreach (var conf in getSelectedConfigurations(false))
        {
            if (conf.AdditionalUsingDirectories.Length != 0)
                conf.AdditionalUsingDirectories += ";";

            conf.AdditionalUsingDirectories += String.Join(";", dirs);
        }
    }

    /// <summary>
    /// Disables specific compilation warnings.
    /// </summary>
    /// <param name="warnings">Warnings to disable</param>
    public static void disablewarnings(params String[] warnings)
    {
        foreach (var conf in getSelectedConfigurations(false))
        {
            if (conf.DisableSpecificWarnings.Length != 0)
                conf.DisableSpecificWarnings += ";";

            conf.DisableSpecificWarnings += String.Join(";", warnings);
        }
    }

    /// <summary>
    /// Specifies system include directories.
    /// </summary>
    /// <param name="dirs">List of include directories.</param>
    public static void sysincludedirs(params String[] dirs)
    {
        foreach (var conf in getSelectedProjectConfigurations())
        {
            if (conf.IncludePath.Length != 0)
                conf.IncludePath += ";";

            conf.IncludePath += String.Join(";", dirs);
        }
    }

    /// <summary>
    /// Specifies system library directories.
    /// </summary>
    /// <param name="dirs">List of include directories.</param>
    public static void syslibdirs(params String[] dirs)
    {
        foreach (var conf in getSelectedProjectConfigurations())
        {
            if (conf.LibraryPath.Length != 0)
                conf.LibraryPath += ";";

            conf.LibraryPath += String.Join(";", dirs);
        }
    }

    /// <summary>
    /// Specifies additional defines.
    /// </summary>
    /// <param name="defines">defines, like for example "DEBUG", etc...</param>
    public static void defines(params String[] defines)
    {
        foreach (var conf in getSelectedConfigurations(false))
        {
            if (conf.PreprocessorDefinitions.Length != 0)
                conf.PreprocessorDefinitions += ";";

            conf.PreprocessorDefinitions += String.Join(";", defines);
        }
    }

    /// <summary>
    /// Matches files from folder _dir using glob file pattern.
    /// In glob file pattern matching * reflects to any file or folder name, ** refers to any path (including sub-folders).
    /// ? refers to any character.
    /// 
    /// There exists also 3-rd party library for performing similar matching - 'Microsoft.Extensions.FileSystemGlobbing'
    /// but it was dragging a lot of dependencies, I've decided to survive without it.
    /// </summary>
    /// <returns>List of files matches your selection</returns>
    public static String[] matchFiles(String _dir, String _filePattern)
    {
        String filePattern = _filePattern.Replace("/", "\\");

        if (filePattern.IndexOfAny(new char[] { '*', '?' }) == -1)      // Speed up matching, if no asterisk / widlcard, then it can be simply file path.
        {
            //
            // GetFullPath removes upper folder references (..\)
            //
            String path = Path.GetFullPath(Path.Combine(_dir.Replace("/", "\\"), filePattern));
            if (File.Exists(path))
                return new String[] { filePattern };

            if (m_solution != null)
            {
                // Referencing project which is not yet generated, but will be.
                foreach (Project p in m_solution.projects)
                {
                    if (p.getFullPath() == path)
                        return new String[] { filePattern };
                }
            }
            return new String[] { };
        }

        String dir = Path.GetFullPath(_dir.Replace("/", "\\"));        // Make it absolute, just so we can extract relative path'es later on.
        String[] pattParts = filePattern.Replace("/", "\\").Split('\\');
        List<String> scanDirs = new List<string>();
        scanDirs.Add(dir);

        //
        //  By default glob pattern matching specifies "*" to any file / folder name, 
        //  which corresponds to any character except folder separator - in regex that's "[^\\]*"
        //  glob matching also allow double astrisk "**" which also recurses into subfolders. 
        //  We split here each part of match pattern and match it separately.
        //
        for (int iPatt = 0; iPatt < pattParts.Length; iPatt++)
        {
            bool bIsLast = iPatt == (pattParts.Length - 1);
            bool bRecurse = false;

            String regex1 = Regex.Escape(pattParts[iPatt]);         // Escape special regex control characters ("*" => "\*", "." => "\.")
            String pattern = Regex.Replace(regex1, @"\\\*(\\\*)?", delegate (Match m)
                {
                    if (m.ToString().Length == 4)   // "**" => "\*\*" (escaped) - we need to recurse into sub-folders.
                    {
                        bRecurse = true;
                        return ".*";
                    }
                    else
                        return @"[^\\]*";
                }).Replace(@"\?", ".");

            if (pattParts[iPatt] == "..")                           // Special kind of control, just to scan upper folder.
            {
                for (int i = 0; i < scanDirs.Count; i++)
                    scanDirs[i] = scanDirs[i] + "\\..";

                continue;
            }

            if (bIsLast)
                pattern += "$";     // String must end, otherwise don't match. *.h matches .h files, but not ".h.in" and other files

            Regex re = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            int nScanItems = scanDirs.Count;
            for (int i = 0; i < nScanItems; i++)
            {
                String[] items;
                if (!bIsLast)
                    items = Directory.GetDirectories(scanDirs[i], "*", (bRecurse) ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
                else
                    items = Directory.GetFiles(scanDirs[i], "*", (bRecurse) ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

                foreach (String path in items)
                {
                    String matchSubPath = path.Substring(scanDirs[i].Length + 1);
                    if (re.Match(matchSubPath).Success)
                        scanDirs.Add(path);
                }
            }
            scanDirs.RemoveRange(0, nScanItems);    // Remove items what we have just scanned.
        } //for

        //  Make relative and return.
        return scanDirs.Select(x => x.Substring(dir.Length + 1)).ToArray();
    } //matchFiles


    /// <summary>
    /// Locates one or more files from current project by file pattern.
    /// </summary>
    /// <param name="_filePattern">Either file path or glob file pattern</param>
    /// <param name="bAllowNoFiles">set to true if don't throw exception if file does not exists (return empty array)</param>
    /// <returns>List of files matching given pattern</returns>
    /// <exception cref="Exception2">Exception is thrown if no files were matched</exception>
    public static FileInfo[] getCurrentProjectFiles(String _filePattern, bool bAllowNoFiles = false)
    {
        requireProjectSelected();
        FileInfo[] selectedFiles = null;

        String filePattern = _filePattern.Replace("/", "\\");

        if (filePattern.IndexOfAny(new char[] { '*', '?' }) == -1)      // Speed up matching, if no asterisk / widlcard, then it can be simply file path.
        {
            FileInfo fileInfo = m_project.files.Where(x => x.relativePath == filePattern).FirstOrDefault();
            if (fileInfo != null)
                selectedFiles = new FileInfo[] { fileInfo };
        }
        else
        {
            String regex1 = Regex.Escape(filePattern);         // Escape special regex control characters ("*" => "\*", "." => "\.")
            String pattern = Regex.Replace(regex1, @"\\\*(\\\*)?", delegate (Match m)
            {
                if (m.ToString().Length == 4)   // "**" => "\*\*" (escaped) - we need to recurse into sub-folders.
                    return ".*";
                else
                    return @"[^\\]*";
            }).Replace(@"\?", ".");
            Regex re = new Regex("^" + pattern + "$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            selectedFiles = m_project.files.Where(x => re.Match(x.relativePath).Success).ToArray();
        }

        if (selectedFiles == null && bAllowNoFiles)
            selectedFiles = new FileInfo[] { };

        if (!bAllowNoFiles && (selectedFiles == null || selectedFiles.Length == 0))
            throw new Exception2("File not found: '" + _filePattern + "' - please specify correct filename. Should be registered via 'files' function.", 1);

        return selectedFiles;
    }



    /// <summary>
    /// Adds one or more file into project.
    /// </summary>
    /// <param name="filePatterns">File patterns to be added</param>
    public static void files(params String[] filePatterns)
    {
        requireProjectSelected();

        foreach (String _filePattern in filePatterns)
        {
            bool bMandatory = true;

            validatePathName(_filePattern);

            String filePattern;
            if (_filePattern.StartsWith("??"))              // Escape questionmark for pattern matching.
            {
                filePattern = _filePattern.Substring(1);
            }
            else
            {
                if (_filePattern.StartsWith("?"))
                {
                    filePattern = _filePattern.Substring(1);
                    bMandatory = false;
                }
                else
                {
                    filePattern = _filePattern;
                }
            }

            String[] fileList = matchFiles(m_project.getProjectFolder(), filePattern);

            if (fileList.Length == 0)
            {
                if (bMandatory)
                {
                    throw new Exception2("No file found which is specified by pattern '" + filePattern + "'.\r\n" +
                        "If file is generated during project build, please mark it as optional with '?' character in front of filename - for example files(\"?temp.txt\")\r\n" +
                        "Files were searched in folder: '" + Exception2.getPath(m_project.getProjectFolder()) + "'"
                    );
                }
                else
                {
                    // Add only file itself
                    fileList = new String[] { filePattern.Replace("/", "\\") };
                }
            }

            foreach (String file in fileList)
            {
                // If file already exists in project, we just ignore and continue.
                bool bExist = m_project.files.Where(x => x.relativePath.CompareTo(file, true)).FirstOrDefault() != null;
                if (bExist)
                    continue;

                String fullFilePath = Path.Combine(m_project.getProjectFolder(), filePattern);
                FileInfo fi = new FileInfo() { relativePath = file };

                switch (Path.GetExtension(file).ToLower())
                {
                    case ".properties":
                        fi.includeType = IncludeType.AntProjectPropertiesFile; break;
                    case ".xml":
                        {
                            String filename = Path.GetFileNameWithoutExtension(filePattern).ToLower();
                            if (filename == "build")
                                fi.includeType = IncludeType.AntBuildXml;
                            else if (filename.Contains("manifest"))
                                fi.includeType = IncludeType.AndroidManifest;
                            else
                                fi.includeType = IncludeType.Content;
                        }
                        break;
                    case ".c":
                    case ".cc":
                    case ".cxx":
                    case ".cpp":
                        fi.includeType = IncludeType.ClCompile; break;

                    case ".java":
                        fi.includeType = IncludeType.JavaCompile; break;

                    case ".template":
                        fi.includeType = IncludeType.GradleTemplate; break;

                    case ".h":
                        fi.includeType = IncludeType.ClInclude; break;

                    case ".rc":
                        fi.includeType = IncludeType.ResourceCompile; break;

                    case ".ico":
                        fi.includeType = IncludeType.Image; break;

                    case ".txt":
                        fi.includeType = IncludeType.Text; break;

                    case ".def":
                        fi.includeType = IncludeType.None;
                        foreach (var conf in getSelectedProjectConfigurations())
                            conf.ModuleDefinitionFile = fi.relativePath;
                        break;

                    default:
                        fi.includeType = IncludeType.None; break;
                }

                m_project.files.Add(fi);
            }
        } //foreach
    } //files

    /// <summary>
    /// Specifies custom build rule for specific file.
    /// </summary>
    /// <param name="cbt">Custom build rule.</param>
    public static void buildrule(CustomBuildRule cbt)
    {
        requireProjectSelected();
        foreach (var conf in getSelectedConfigurations(false))
            conf.customBuildRule = cbt;
    } //buildrule

    /// <summary>
    /// Specifies commands to be executed after project build
    /// </summary>
    public static void postbuildcommands(params String[] commands)
    {
        requireProjectSelected();
        foreach (var conf in getSelectedProjectConfigurations())
        {
            if (conf.PostBuildEvent.Command.Length != 0) conf.PostBuildEvent.Command += "\r\n";
            conf.PostBuildEvent.Command += String.Join("\r\n", commands);
        }
    }

    /// <summary>
    /// Perform export of files in post build step
    /// </summary>
    /// <param name="toDir">Where to export</param>
    /// <param name="files">files to export</param>
    public static void exportFiles(String toDir, params String[] files)
    {
        if (!toDir.EndsWith("\\"))
            toDir += "\\";

        using (new UsingSyncProj(1))
        {
            foreach (String file in files)
            {
                String cmd = @"echo f 2>nul | xcopy /d /y /q $(ProjectDir)" + file + " $(ProjectDir)" + toDir + file + " >nul";
                postbuildcommands(cmd);
            }
        }
    }

    /// <summary>
    /// Specifies commands to be executed before project build
    /// </summary>
    public static void prebuildcommands(params String[] commands)
    {
        requireProjectSelected();
        foreach (var conf in getSelectedProjectConfigurations())
        {
            if (conf.PreBuildEvent.Command.Length != 0) conf.PreBuildEvent.Command += "\r\n";
            conf.PreBuildEvent.Command += String.Join("\r\n", commands);
        }
    }

    /// <summary>
    /// Specifies commands to be executed before linking phase
    /// </summary>
    public static void prelinkcommands(params String[] commands)
    {
        requireProjectSelected();
        foreach (var conf in getSelectedProjectConfigurations())
        {
            if (conf.PreLinkEvent.Command.Length != 0) conf.PreLinkEvent.Command += "\r\n";
            conf.PreLinkEvent.Command += String.Join("\r\n", commands);
        }
    }


    /// <summary>
    /// Sets up custom build rule for project or solution configuration script.
    /// </summary>
    /// <param name="script2include">Script to include into project</param>
    /// <param name="script2compile">Script which shall be compiled once script2include is changed</param>
    /// <param name="pathToSyncProjExe">Path where syncProj.exe will reside</param>
    /// <param name="inDir">Where solution or project is located.</param>
    static void selfCompileScript(String script2include, String script2compile, String pathToSyncProjExe, String inDir)
    {
        if (script2compile == null)
            script2compile = script2include;

        pathToSyncProjExe = SolutionOrProject.getSyncProjExeLocation(inDir, pathToSyncProjExe);

        using (new UsingSyncProj(2 /*called from projectScript & solutionScript - 2 frames in call stack */))
        {
            files(script2include);
            filter("files:" + script2include);

            String tempLogFile = "$(IntermediateOutputPath)" + Path.GetFileName(script2compile).Replace('.', '_') + "_log.txt";

            //
            //  We collect all C# script dependencies and add them as additional inputs.
            //
            CsScriptInfo csInfo = CsScript.getCsFileInfo(Path.Combine(inDir, script2include), false);
            String additionalInputs = "";

            foreach (String _file in csInfo.csFiles)
            {
                String file = _file;

                if (!Path.IsPathRooted(file))
                    file = "$(ProjectDir)" + _file;

                if (additionalInputs.Length != 0)
                    additionalInputs += ";";

                additionalInputs += file;
            }

            buildrule(
                new CustomBuildRule()
                {
                    Command = "\"" + pathToSyncProjExe + "\" $(ProjectDir)" + script2compile + "\r\n" + "echo 1>" + tempLogFile,
                    Outputs = tempLogFile,
                    Message = "",
                    AdditionalInputs = additionalInputs
                }
            );
            filter();
        }
    }

    /// <summary>
    /// Configures project rebuild step.
    /// </summary>
    /// <param name="script2include">Script to include into project</param>
    /// <param name="script2compile">Script which shall be compiled once script2include is changed</param>
    /// <param name="pathToSyncProjExe">Path where syncProj.exe will reside</param>
    public static void projectScript(String script2include, String script2compile = null, String pathToSyncProjExe = null)
    {
        requireProjectSelected();

        selfCompileScript(script2include, script2compile, pathToSyncProjExe, m_project.getProjectFolder());
    }

    /// <summary>
    /// Configures solution rebuild step.
    /// </summary>
    /// <param name="script2include">Script to include into project</param>
    /// <param name="script2compile">Script which shall be compiled once script2include is changed</param>
    /// <param name="pathToSyncProjExe">Path where syncProj.exe will reside</param>
    public static void solutionScript(String script2include, String script2compile = null, String pathToSyncProjExe = null)
    {
        if (script2compile == null)
            script2compile = script2include;

        requireSolutionSelected();
        String projectName = Path.GetFileNameWithoutExtension(script2compile);

        using (new UsingSyncProj(1))
        {
            group("0 Solution Update");
            project(projectName);
            platforms("Win32");
            configurations(m_configurations.ToArray());
            kind("Utility");
            // Redirect so would not conflict with existing projects.
            objdir("obj/" + projectName + "_temp");

            foreach (String plat in m_solution.getPlatforms())
                configmap(plat, "Win32");
        }

        selfCompileScript(script2include, script2compile, pathToSyncProjExe, m_solution.getSolutionFolder());
        group("");
        solutionUpdateProject = projectName;
    }

    /// <summary>
    /// Defines configuration mapping from solution (first argument) to project (second argument)
    /// For example configmap( "Development", "Debug" )
    /// </summary>
    /// <param name="confList"></param>
    public static void configmap(params String[] confList)
    {
        requireSolutionSelected();
        requireProjectSelected();

        if (confList.Length % 2 == 1)
            throw new Exception2("Input argument count must be dividable by 2 (solution, project configuration pairs)");

        if (m_project.slnConfigurations == null || m_solution.configurations.Count != m_project.slnConfigurations.Count)
        {
            m_project.slnConfigurations.Clear();
            // Make 1 to 1 mapping.
            m_project.slnConfigurations.AddRange(m_solution.configurations);
        }

        for (int i = 0; i < confList.Length; i += 2)
        {
            String from = confList[i].ToLower();
            String to = confList[i + 1];
            int matchMethod = 0;

            for (int iConf = 0; iConf < m_solution.configurations.Count; iConf++)
            {
                String conf = m_solution.configurations[iConf];
                bool match = false;
                String targetConf = "";

                // In full style, for example "Debug|Win32"
                if (from.Contains('|'))
                {
                    match = from == conf.ToLower();
                    targetConf = to;
                }
                else
                {
                    String[] confPlat = conf.Split('|');

                    // In partial style, for example "Debug" or "Win32"
                    if (matchMethod == 0)
                    {
                        // Once we have detected whether we are matching configuration name or platform, 
                        // we keep matching same thing.
                        if (from == confPlat[0].ToLower())
                        {
                            matchMethod = 1;
                        }
                        else
                        {
                            if (from == confPlat[1].ToLower())
                                matchMethod = 2;
                        }
                    } //if

                    switch (matchMethod)
                    {
                        case 1:
                            match = (from == confPlat[0].ToLower());
                            targetConf = to + "|" + confPlat[1];
                            break;
                        case 2:
                            match = (from == confPlat[1].ToLower());
                            targetConf = confPlat[0] + "|" + to;
                            break;
                    }
                }

                if (match)
                    m_project.slnConfigurations[iConf] = targetConf;
            } //foreach
        } //for
    } //configmap

    /// <summary>
    /// Enables certain flags for specific configurations.
    /// </summary>
    /// <param name="flags">
    /// "LinkTimeOptimization" - Enable link-time (i.e. whole program) optimizations.<para />
    /// </param>
    public static void flags(params String[] flags)
    {
        requireProjectSelected();

        using (new UsingSyncProj(3))        //getSelectedProjectConfigurations requires shift by 3.
        {
            foreach (String flag in flags)
            {
                switch (flag.ToLower())
                {
                    case "linktimeoptimization":
                        {
                            foreach (var conf in getSelectedProjectConfigurations())
                                conf.WholeProgramOptimization = EWholeProgramOptimization.UseLinkTimeCodeGeneration;
                        }
                        break;

                    case "mfc":
                        m_project.Keyword = EKeyword.MFCProj;

                        foreach (var conf in getSelectedProjectConfigurations())
                            if (conf.UseOfMfc == EUseOfMfc.None)
                                conf.UseOfMfc = EUseOfMfc.Dynamic;
                        break;

                    case "staticruntime":
                        foreach (var conf in getSelectedProjectConfigurations())
                            conf.UseOfMfc = EUseOfMfc.Static;
                        break;

                    case "nopch":
                        foreach (var conf in getSelectedConfigurations(false))
                            conf.PrecompiledHeader = EPrecompiledHeaderUse.NotUsing;
                        break;

                    case "excludefrombuild":
                    case "excludedfrombuild":
                        foreach (var conf in getSelectedConfigurations(false))
                            conf.ExcludedFromBuild = true;
                        break;

                    default:
                        throw new Exception2("Flag '" + flag + "' is not supported");
                } //switch 
            } //foreach
        }
    } //flags

    /// <summary>
    /// Sets windows SDK version.
    /// </summary>
    /// <param name="ver">Target Platform Version, e.g. "8.1" or "10.0.14393.0"</param>
    /// <param name="force">true - to force set, false - set only if not yet selected.</param>
    public static void systemversion(String ver, bool force = false)
    {
        requireProjectSelected();

        if (!force && m_project.WindowsTargetPlatformVersion != null)
            // Already selected.
            return;

        m_project.WindowsTargetPlatformVersion = ver;
    }

    /// <summary>
    /// Sets .NET Target Framework Version
    /// </summary>
    /// <param name="ver">For example "v4.7.2"</param>
    public static void TargetFrameworkVersion(String ver)
    {
        requireProjectSelected();
        m_project.TargetFrameworkVersion = ver;
    }

    /// <summary>
    /// Adds one or more obj or lib into project to link against.
    /// </summary>
    /// <param name="files"></param>
    public static void links(params String[] files)
    {
        foreach (var conf in getSelectedProjectConfigurations())
        {
            if (conf.AdditionalDependencies.Length != 0)
                conf.AdditionalDependencies += ";";

            conf.AdditionalDependencies += String.Join(";", files);
        }
    } //links


    /// <summary>
    /// Adds one or more library directory from where to search .obj / .lib files.
    /// </summary>
    /// <param name="folders"></param>
    public static void libdirs(params String[] folders)
    {
        foreach (var conf in getSelectedProjectConfigurations())
        {
            if (conf.AdditionalLibraryDirectories.Length != 0)
                conf.AdditionalLibraryDirectories += ";";

            conf.AdditionalLibraryDirectories += String.Join(";", folders);
        }
    } //links


    /// <summary>
    /// Specifies optimization level to be used.
    /// </summary>
    /// <param name="optLevel">Optimization level to enable - one of following: off, size, speed, on(or full)</param>
    public static void optimize(String optLevel)
    {
        EOptimization opt;
        bool bFunctionLevelLinking = true;
        bool bIntrinsicFunctions = true;
        bool bEnableCOMDATFolding = false;
        bool bOptimizeReferences = false;

        switch (optLevel.ToLower())
        {
            case "custom":
                opt = EOptimization.Custom;
                break;
            case "off":
                opt = EOptimization.Disabled;
                bFunctionLevelLinking = false;
                bIntrinsicFunctions = false;
                break;
            case "full":
            case "on":
                opt = EOptimization.Full;
                bEnableCOMDATFolding = true;
                bOptimizeReferences = true;
                break;
            case "size":
                opt = EOptimization.MinSpace;
                break;
            case "speed":
                opt = EOptimization.MaxSpeed;
                break;
            default:
                throw new Exception2("Allowed optimization() values are: off, size, speed, on(or full)");
        }

        foreach (var conf in getSelectedConfigurations(false))
        {
            conf.Optimization = opt;
            conf.FunctionLevelLinking = bFunctionLevelLinking;
            conf.IntrinsicFunctions = bIntrinsicFunctions;
            conf.EnableCOMDATFolding = bEnableCOMDATFolding;
            conf.OptimizeReferences = bOptimizeReferences;
            m_project.optimize_symbols_recheck(conf);
        }
    } //optimize

    /// <summary>
    /// Eliminates functions and data that are never referenced (/OPT:REF option)
    /// </summary>
    /// <param name="bOptimizeReferences">true to enable, false to disable</param>
    public static void Linker_Optimizations_References(bool bOptimizeReferences = true)
    {
        foreach (var conf in getSelectedConfigurations(false))
            conf.OptimizeReferences = bOptimizeReferences;
    }

    /// <summary>
    /// Produces output, which can be used with the Performance Tools profiler.
    /// </summary>
    /// <param name="bProfile">true to enable profiling</param>
    public static void Linker_Advanced_Profile(bool bProfile)
    {
        foreach (var conf in getSelectedConfigurations(false))
            conf.Profile = bProfile;
    }

    /// <summary>
    /// Enable function level linking.
    /// </summary>
    /// <param name="b">true to enable, false to disable</param>
    public static void CCpp_CodeGeneration_EnableFunctionLevelLinking(bool b = true)
    {
        foreach (var conf in getSelectedConfigurations(false))
            conf.FunctionLevelLinking = b;
    }

    /// <summary>
    /// Sets specific run-time library.
    /// 
    /// When same application is being linked with multiple static libraries, application and static libraries
    /// must all use same runtime library - otherwise you will get link errors.
    /// </summary>
    /// <param name="rtl">Run-time library to use</param>
    public static void CCpp_CodeGeneration_RuntimeLibrary(ERuntimeLibrary rtl)
    {
        foreach (var conf in getSelectedConfigurations(false))
            conf.RuntimeLibrary = rtl;
    }

    /// <summary>
    /// Sets exception handling model.
    /// Disabling exception handling reduces size of produced executable or dll approximately by 20%.
    /// </summary>
    /// <param name="eh">Exception level to set</param>
    public static void CCpp_CodeGeneration_EnableCppExceptions(EExceptionHandling eh)
    {
        foreach (var conf in getSelectedConfigurations(false))
            conf.ExceptionHandling = eh;
    }

    /// <summary>
    /// Configures basic run-time checks.
    /// </summary>
    /// <param name="rtc">Checks to use</param>
    public static void CCpp_CodeGeneration_BasicRuntimeChecks(EBasicRuntimeChecks rtc)
    {
        foreach (var conf in getSelectedConfigurations(false))
            conf.BasicRuntimeChecks = rtc;
    }


    /// <summary>
    /// Enables cross-module optimizations by delaying code generation to link-time; requires that linker option 'Link Time Code Generation' be turned on.
    /// </summary>
    /// <param name="wpgen"></param>
    public static void Ccpp_Optimization_WholeProgramGeneration(EWholeProgramOptimization wpgen = EWholeProgramOptimization.UseLinkTimeCodeGeneration)
    {
        foreach (var conf in getSelectedProjectConfigurations())
        {
            conf.WholeProgramOptimization = wpgen;
            m_project.optimize_symbols_recheck(conf);
        }
    }

    /// <summary>
    /// Select specific optimization method.
    /// </summary>
    /// <param name="opt">Optimization level</param>
    public static void Ccpp_Optimization_Optimization(EOptimization opt = EOptimization.Custom)
    {
        foreach (var conf in getSelectedProjectConfigurations())
            conf.Optimization = opt;
    }


    /// <summary>
    /// Passes arguments directly to the compiler command line without translation.
    /// </summary>
    public static void buildoptions(params String[] options)
    {
        foreach (var conf in getSelectedConfigurations(false))
        {
            if (conf.ClCompile_AdditionalOptions.Length != 0)
                conf.ClCompile_AdditionalOptions += " ";

            conf.ClCompile_AdditionalOptions += String.Join(" ", options);
        }
    }

    /// <summary>
    /// Passes arguments directly to the linker command line without translation.
    /// </summary>
    public static void linkoptions(params String[] options)
    {
        foreach (var conf in getSelectedConfigurations(false))
        {
            if (conf.Link_AdditionalOptions.Length != 0)
                conf.Link_AdditionalOptions += " ";

            conf.Link_AdditionalOptions += String.Join(" ", options);
        }
    }

    /// <summary>
    /// Sets current object filename (output of compilation result).
    /// Windows: Can be file or directory name.
    /// Android: Typically: "$(IntDir)%(filename).o"
    /// </summary>
    /// <param name="objFilename"></param>
    public static void objectfilename(String objFilename)
    {
        foreach (var conf in getSelectedConfigurations(false))
            conf.ObjectFileName = objFilename;
    }

    /// <summary>
    /// Advanced feature - forces particular file to preprocess only file (to be compiled).
    /// This can be used for checking define expansion.
    /// </summary>
    /// <param name="file">File to preprocess</param>
    /// <param name="outExtension">Output file extension</param>
    /// <param name="bDoPreprocess">true if you want to preprocess file, false if compile normally, but include only preprocessed output</param>
    public static void preprocessFile(String file, bool bDoPreprocess = true, String outExtension = ".preprocessed")
    {
        requireProjectSelected();

        if (!File.Exists(file))
            throw new Exception2("File '" + file + "' does not exist.");

        files(file);

        if (bDoPreprocess)
        {
            filter("files:" + file);

            if (m_project.Keyword == EKeyword.Win32Proj)
            {
                // MS C++ compiler
                buildoptions("/P /Fi\"%(FullPath)" + outExtension + "\"");
            }
            else
            {
                // clang or gcc
                buildoptions("-E");
                objectfilename("%(FullPath)" + outExtension);
            }
            filter();
        }

        files("?" + file + outExtension);
    } //preprocessFiles


    /// <summary>
    /// Enables show includes only for specific file.
    /// </summary>
    /// <param name="fileList">files for which to enable showIncludes.</param>
    public static void showIncludes(params String[] fileList)
    {
        requireProjectSelected();

        using (new UsingSyncProj(1))
        {
            foreach (String file in fileList)
            {
                // If file already exists in project, we just ignore and continue.
                FileInfo fi = m_project.files.Where(x => x.relativePath == file).FirstOrDefault();
                if (fi == null)
                {
                    files(file);
                    fi = m_project.files.Where(x => x.relativePath == file).FirstOrDefault();
                }

                filter("files:" + file);

                foreach (var conf in getSelectedConfigurations(false))
                {
                    if (m_project.Keyword == EKeyword.Win32Proj)
                    {
                        conf.ShowIncludes = true;
                    }
                    else
                    {
                        buildoptions("-M");
                        objectfilename("");
                    }
                }
            } //foreach

            filter();
        } //using

    } //showIncludes

    /// <summary>
    /// Prints more details about given exception. In visual studio format for errors.
    /// </summary>
    /// <param name="ex">Exception occurred.</param>
    public static void ConsolePrintException(Exception ex, String[] args = null)
    {
        if (args != null && args.Contains("no_exception_handling"))
            throw ex;

        Exception2 ex2 = ex as Exception2;
        String fromWhere = "";
        if (ex2 != null)
            fromWhere = ex2.getThrowLocation();

        if (!ex.Message.Contains("error"))
            Console.WriteLine(fromWhere + "error: " + ex.Message);
        else
            Console.WriteLine(ex.Message);

        // Might contain syncProj source code position, path, function names and parameters which can change from release to another.
        if (Exception2.g_bReportFullPath)
        {
            Console.WriteLine();
            Console.WriteLine("----------------------- Full call stack trace follows -----------------------");
            Console.WriteLine();
            Console.WriteLine(ex.StackTrace);
            Console.WriteLine();
        }
        bSaveGeneratedProjects = false;
    }

    /// <summary>
    /// Returns true if it's developer of syncProj.
    /// </summary>
    public static bool isDeveloper()
    {
        #if WINDOWS
            bool isDeveloper = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\\syncProj")?.GetValue("IsDeveloper")?.ToString() == "1";
            return isDeveloper;
        #else
            return false;
        #endif
    }

    /// <summary>
    /// Sets gradle version
    /// </summary>
    /// <param name="v">version to use</param>
    public static void GradleVersion(String v)
    {
        requireProjectSelected();
        m_project.GradlePackage.GradleVersion = v;
    }

    /// <summary>
    /// Sets Gradle Apk File path.
    /// </summary>
    /// <param name="apkPath">path</param>
    public static void GradleApkFileName(String apkPath)
    {
        foreach (var conf in getSelectedProjectConfigurations())
            conf.ApkFileName = apkPath;
    }

    /// <summary>
    /// Sets Gradle tool name
    /// </summary>
    /// <param name="toolName">path</param>
    public static void GradleToolName(String toolName)
    {
        m_project.GradlePackage.ToolName = toolName;
    }

    /// <summary>
    /// Sets additional gradle options to be passed to Gradle.
    /// </summary>
    /// <param name="additionalOptions">options</param>
    public static void GradleAdditionalOptions(String additionalOptions)
    {
        foreach (var conf in getSelectedProjectConfigurations())
            conf.AdditionalOptions = additionalOptions;
    }

    /// <summary>
    /// Sets Gradle Project Directory. If not specified, uses project directory.
    /// </summary>
    /// <param name="path">path</param>
    public static void GradleProjectDirectory(String path = "")
    {
        requireProjectSelected();
        if (!Project.IsPathProjectOrSolutionRooted(path))
            path = "$(ProjectDir)" + path;

        m_project.GradlePackage.ProjectDirectory = path;
    }

    /// <summary>
    /// Enables multiprocessor build.
    /// </summary>
    /// <param name="bValue">true to enable</param>
    public static void EnableMultiProcessorBuild(bool bValue = true)
    {
        foreach (var conf in getSelectedProjectConfigurations())
        {
            // warning D9030: '/Gm' is incompatible with multiprocessing; ignoring /MP switch
            if (bValue) conf.MinimalRebuild = false;
            conf.MultiProcessorCompilation = bValue;
        }
    }

    /// <summary>
    /// Enables / disables run-time type information
    /// </summary>
    public static void RunTimeTypeInformation(bool b = true)
    {
        foreach (var conf in getSelectedConfigurations(false))
            conf.RuntimeTypeInfo = b;
    }

    /// <summary>
    /// Sets C Language Standard
    /// </summary>
    public static void CLanguageStandard(ECLanguageStandard ls)
    {
        foreach (var conf in getSelectedConfigurations(false))
            conf.CLanguageStandard = ls;
    }

    /// <summary>
    /// Sets C++ Language Standard
    /// </summary>
    public static void CppLanguageStandard(ECppLanguageStandard ls)
    {
        foreach (var conf in getSelectedConfigurations(false))
            conf.CppLanguageStandard = ls;
    }

};

