using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// syncProj tool.
/// </summary>
public class syncProjCLI
{
    public static bool bAllowExceptionThrow = false;

    /// <summary>
    /// Entry point
    /// </summary>
    public static int Main(params String[] args)
    {
        // Force English for exception messages for non-English windows.
        System.Threading.Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-us");
        try
        {
            String inFile = null;
            List<String> formats = new List<string>();
            String outFile = null;
            String outPrefix = "";
            bool bProcessProjects = true;
            bool bTestsExecuted = false;

            bAllowExceptionThrow = true;
            bool testingPerformed = GoogleTestBootstrap.TestMain(false, args, new syncProjUnitSuiteInfo());
            bAllowExceptionThrow = false;
            if (testingPerformed)
            {
                SolutionProjectBuilder.bSaveGeneratedProjects = false;
                return 0;
            }

            for( int i = 0; i < args.Length; i++ )
            {
                String arg = args[i];

                if (!(arg.StartsWith("-") || arg.StartsWith("/")))
                {
                    inFile = arg;
                    continue;
                }
                arg = arg.TrimStart(new char[] { '/', '-' });

                switch (arg.ToLower())
                {
                    case "lua": formats.Add("lua"); break;
                    case "cs": formats.Add("cs"); break;
                    case "o": i++; outFile = args[i]; break;
                    case "p": i++; outPrefix = args[i]; break;
                    case "sln": bProcessProjects = false; break;
                    case "x": Exception2.g_bReportFullPath = false; break;
                    case "css_debug":
                        CsScriptInfo.g_bCsDebug = true; break;
                }
            } //foreach

            if (inFile != null && Path.GetExtension(inFile).ToLower() == ".cs")
            {
                try
                {
                    SolutionProjectBuilder.m_workPath = Path.GetDirectoryName(Path.GetFullPath(inFile));
                    
                    if( !Exception2.g_bReportFullPath )
                        Console.WriteLine(Path.GetFileName(inFile) + " :");
                    else
                        Console.WriteLine(inFile + " :");
                    
                    SolutionProjectBuilder.invokeScript(inFile);
                    return 0;
                }
                catch (Exception ex)
                {
                    SolutionProjectBuilder.ConsolePrintException(ex, args);
                }
                return -2;
            } //if

            String ext = null;
            if( inFile != null ) ext = Path.GetExtension(inFile).ToLower();
            //
            // If we have solution, let's export by default in C# script format.
            //
            if (inFile != null && ( ext == ".sln" || ext == ".vcxproj" ) && formats.Count == 0 )
                formats.Add("cs");

            if (ext == ".log" || ext == ".txt")
            {
                return MakeLogToSolutionBuilder.makeLogToSolution(inFile);
            }


                
            if (inFile == null || formats.Count == 0)
            {
                if (bTestsExecuted)
                    return 0;

                Console.WriteLine("Usage(1): syncProj <.sln or .vcxproj file> (-lua|-cs) [-o file]");
                Console.WriteLine("");
                Console.WriteLine("         Parses solution or project and generates premake5 .lua script or syncProj C# script.");
                Console.WriteLine("");
                Console.WriteLine(" -cs     - C# script output");
                Console.WriteLine(" -lua    - premake5's lua script output");
                Console.WriteLine("");
                Console.WriteLine(" -o      - sets output file (without extension)");
                Console.WriteLine(" -p      - sets prefix for all output files");
                Console.WriteLine(" -sln    - does not processes projects (Solution only load)");
                Console.WriteLine(" -t      - starts self-diagnostic tests (requires tests directory)");
                Console.WriteLine(" -keep   - keeps test results");
                Console.WriteLine("");
                Console.WriteLine("Usage(2): syncProj <.cs>");
                Console.WriteLine("");
                Console.WriteLine("         Executes syncProj C# script.");
                Console.WriteLine("");
                return -2;
            }

            SolutionOrProject proj = new SolutionOrProject(inFile);

            Solution s = proj.solutionOrProject as Solution;
            if (s != null && bProcessProjects)
            {
                foreach (Project p in s.projects)
                {
                    if (p.IsSubFolder())
                        continue;
                    
                    Project.LoadProject(s, null, p);
                }
            }

            UpdateInfo uinfo = new UpdateInfo();
            foreach ( String format in formats )
                SolutionOrProject.UpdateProjectScript(uinfo, proj.path, proj.solutionOrProject, outFile, format, bProcessProjects, outPrefix);

            Console.Write(Exception2.getPath(inFile) + ": ");
            uinfo.DisplaySummary();

        }
        catch (Exception ex)
        {
            if (bAllowExceptionThrow)
                throw;

            Console.WriteLine("Error: " + ex.Message);
            return -2;
        }

        return 0;
    } //Main
}


/// <summary>
/// This is rather experimental class, which tries to reverse engineere make log file and construct solution / project 
/// out of it - there can be quite many different issues / problems related to make log file parsing - usage
/// of 3-rd party executables, etc etc. This class is not main purpose of syncProj, that's why I'm excluding it from code coverage.
/// </summary>
[ExcludeFromCodeCoverage]
class MakeLogToSolutionBuilder : SolutionProjectBuilder
{
    public static int makeLogToSolution(String inFile)
    {
        try
        {
            String file = File.ReadAllText(inFile);
            List<String> lines = Regex.Split(file, "[\r\n|;]").ToList();
            Regex reCmdExtract = new Regex("^([^ (]+)");
            //  Files to compile
            Dictionary<String, String> cl = new Dictionary<string, string>();
            Regex reObjectFile = new Regex("(^| )[-/]Fo([^ ]+)");
            Regex reYasmObjectFile = new Regex("(^| )[-/]o +([^ ]+)");
            Regex reYasmIsDoubleOption = new Regex("^[-/][foI]");

            // Custom commands to execute (yasm)
            Dictionary<String, String> custCommand = new Dictionary<string, string>();

            m_workPath = Path.GetDirectoryName(inFile);
            String solutioName = Path.GetFileNameWithoutExtension(inFile);
            solution(solutioName);
            configurations("Debug", "Release");
            platforms("Win32");
            vsver(2013);

            // Scan through each line in make log file
            for (int iLine = 0; iLine < lines.Count; iLine++)
            {
                String _line = lines[iLine];
                String line = _line.TrimStart(' ', '\t');
                line = line.TrimEnd(' ', '\t');

                //  Extract command which shall be executed (cl, link, yasm, lib, other)
                var match = reCmdExtract.Match(line);
                if (!match.Success)
                {
                    lines.RemoveAt(iLine);
                    iLine--;
                    continue;
                }

                String cmd = match.Groups[1].Value;

                // Printinf / processing commands, not intrested.
                if (cmd == "awk" || cmd == "gsub" || cmd == "if" || cmd == "printf" || cmd == "rm" || cmd == "echo" || cmd == "cp"
                    // what is this ?
                    || cmd == ":"
                    // ffmpeg, documentation generation
                    || cmd == "perl"
                    || cmd == "pod2man"
                    || cmd.StartsWith("doc/")
                )
                {
                    lines.RemoveAt(iLine);
                    iLine--;
                    continue;
                }

                // Accept changed line
                lines[iLine] = line;

                // Compile source to object file.
                if (cmd == "cl")
                {
                    var re = reObjectFile.Match(line);
                    if (re.Success)
                        cl[re.Groups[2].Value] = line.Substring(3);
                    continue;
                }

                // Custom build step (execute yasm)
                if (cmd == "yasm")
                {
                    var re = reYasmObjectFile.Match(line);
                    if (re.Success)
                        custCommand[re.Groups[2].Value] = line;
                    continue;
                }

                //
                // combine into static library or perform linking into exe.
                //
                if (cmd == "lib" || cmd == "link")
                {
                    String[] optsOrFiles = line.Substring(cmd.Length + 1).Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries );
                    String[] opts = optsOrFiles.Where(x => x.StartsWith("-")).ToArray();
                    String[] objFiles = optsOrFiles.Where(x => !x.StartsWith("-")).ToArray();
                    int clFiles = 0;
                    String projectName = "";

                    //
                    //  Determine output filename
                    //
                    foreach (String opt in opts)
                    {
                        if (opt == "-nologo")
                            continue;
                        if (opt.StartsWith("-out:"))
                            projectName = opt.Substring(5);
                    }

                    String projectDir = Path.GetDirectoryName(projectName);
                    projectName = Path.Combine(projectDir, Path.GetFileNameWithoutExtension(projectName));
                    String toRoot = Path2.makeRelative(m_workPath, Path.Combine(m_workPath, projectDir));

                    //
                    //  Define new project.
                    //
                    project(projectName);
                    platforms("Win32");
                    vsver(2013);
                    systemversion("8.1");

                    if (cmd == "lib")
                        kind("StaticLib");
                    else
                        kind("ConsoleApp");

                    Dictionary<String, Dictionary<String, int>> optIdToCount = new Dictionary<string, Dictionary<string, int>>();
                    List<String> srcFiles = Enumerable.Repeat(string.Empty, objFiles.Length).ToList();

                    // Determine where out file will reside relatively to solution root
                    String projDirRelativeToSolution = Path2.makeRelative(m_workPath, m_project.getProjectFolder());

                    if (projDirRelativeToSolution != "")
                        projDirRelativeToSolution += "\\";

                    //
                    //  iPass == 0  - collect information about source code filenames
                    //  iPass == 1  - dump info into project
                    //
                    for (int iPass = 0; iPass < 2; iPass++)
                    {
                        if (iPass == 1)
                        {
                            foreach (var tagDict in optIdToCount)
                            {
                                foreach (var idValuePair in tagDict.Value)
                                {
                                    // Not common
                                    if (idValuePair.Value != clFiles)
                                        continue;

                                    if (tagDict.Key == "I")
                                        includedirs(projDirRelativeToSolution + idValuePair.Key);       // -I<include directory>
                                    else if (tagDict.Key == "D")
                                        defines(idValuePair.Key);                                       // -D<define>
                                    else
                                        disablewarnings(idValuePair.Key);                               // -wd<warning to disable>
                                } //foreach
                            } //foreach
                        } //if
                        
                        for ( int iObj = 0; iObj < objFiles.Length; iObj++ )
                        {
                            String obj = objFiles[iObj];
                            String srcPath = "";

                            if (iPass == 1)
                            {
                                if (bLastSetFilterWasFileSpecific) filter();
                                String fullPath = Path.Combine(m_project.getProjectFolder(), toRoot, srcFiles[iObj]);
                                srcPath = Path2.makeRelative(fullPath, m_project.getProjectFolder());

                                if (srcFiles[iObj] == "")   // Can happen when cmd == "links".
                                    continue;

                                files(srcPath);             // Add file into project
                            }

                            if (custCommand.ContainsKey(obj))
                            {

                                if (iPass == 0)
                                {
                                    //
                                    // Determine yasm input file.
                                    //
                                    String[] yasmOpt = custCommand[obj].Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
                                    for (int i = 0; i < yasmOpt.Length; i++)
                                        if (reYasmIsDoubleOption.Match(yasmOpt[i]).Success)
                                            i++;
                                        else
                                            if (!yasmOpt[i].StartsWith("-") && !yasmOpt[i].StartsWith("/"))
                                            {
                                                srcFiles[iObj] = yasmOpt[i];
                                                break;
                                            }
                                }
                                else
                                {
                                    //
                                    // Dump yasm execution as custom build step.
                                    //
                                    filter("files:" + srcPath);

                                    String yasmCmd = custCommand[obj];

                                    String outFile = "";
                                    
                                    yasmCmd = reYasmObjectFile.Replace(
                                        yasmCmd,
                                        new MatchEvaluator(m => { 
                                            outFile = "$(ProjectDir)$(IntermediateOutputPath)" + Regex.Replace(m.Groups[2].Value, "[\\/]", "_") + "bj"; 
                                            return m.Groups[1].Value + "-o " + outFile; 
                                        }
                                    ));

                                    yasmCmd = "$(ProjectDir)" + projDirRelativeToSolution + yasmCmd;

                                    if (projDirRelativeToSolution != "")
                                        yasmCmd = "pushd " + projDirRelativeToSolution.Substring(0, projDirRelativeToSolution.Length - 1) + "\r\n" + yasmCmd + "\r\npopd";

                                    buildrule(new CustomBuildRule()
                                    {
                                        Command = yasmCmd,
                                        Message = "Assembling '" + srcPath + "'...",
                                        Outputs = outFile
                                    });
                                }
                                continue;
                            }

                            if (!cl.ContainsKey(obj))
                            {
                                var p = m_solution.projects.Where(x => Path.GetFileNameWithoutExtension(x.ProjectName) == Path.GetFileNameWithoutExtension(obj)).FirstOrDefault();

                                if (p != null)
                                {
                                    referencesProject("?" + p.getRelativePath(), "");
                                    continue;
                                }

                                if (Path.GetExtension(obj).ToLower() == ".lib")
                                    links(obj);
                                else
                                    throw new Exception2("Object file '" + obj + "' does not have command line information");

                                continue;
                            }

                            // Compilable files (cl).
                            if( iPass == 0 ) clFiles++;


                            String[] compileOptions = cl[obj].Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                            foreach (String co in compileOptions)
                            {
                                if (!co.StartsWith("-"))
                                {
                                    srcFiles[iObj] = co;
                                    continue;
                                }

                                if (co == "-nologo")
                                    continue;

                                String opt = co.Substring(1, 1);
                                String value = co.Substring(2);

                                // Ignore options
                                if (opt == "Z" || opt == "W" || opt == "O" || opt == "c" || opt == "F")
                                    continue;

                                if (opt == "w")
                                {
                                    if (co.Substring(2, 1) == "d")
                                        value = co.Substring(3);
                                    else
                                        continue;
                                }

                                if (iPass == 0)
                                {
                                    if (opt == "I" || opt == "D" || opt == "w")
                                    {
                                        if (!optIdToCount.ContainsKey(opt))
                                            optIdToCount.Add(opt, new Dictionary<String, int>());

                                        if (!optIdToCount[opt].ContainsKey(value))
                                            optIdToCount[opt][value] = 0;

                                        optIdToCount[opt][value]++;
                                    } //if
                                }
                                else
                                {
                                    if (optIdToCount[opt][value] == clFiles)
                                        continue;

                                    // File specific include or define
                                    if (!bLastSetFilterWasFileSpecific)
                                        filter("files:" + srcPath);

                                    if (opt == "I")
                                        includedirs(projDirRelativeToSolution + value);
                                    else if (opt == "D")
                                        defines(value);
                                    else
                                        disablewarnings(value);
                                } //if-else
                            } //foreach
                        } //foreach
                    } 
                } //if
            } //for

            bSaveGeneratedProjects = false;
            UpdateInfo uinfo = new UpdateInfo();
            SolutionOrProject.UpdateProjectScript(uinfo, m_solution.path, m_solution, null, "cs", true, null);
            Console.Write(Exception2.getPath(inFile) + ": ");
            uinfo.DisplaySummary();

        }
        catch (Exception ex)
        {
            SolutionProjectBuilder.ConsolePrintException(ex);
        }
        return 0;
    }
};
