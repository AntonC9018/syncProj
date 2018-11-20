﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using System.Xml.Linq;
using System.Collections;
using System.Reflection;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

class Dictionary2<TKey, TValue> : Dictionary<TKey, TValue>
{
    new public TValue this[TKey name]
    {
        get
        {
            if (!ContainsKey(name))
            {
                TValue v = default(TValue);
                Type t = typeof(TValue);
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
                    v = (TValue)Activator.CreateInstance(t);
                Add(name, v);
            }
            return base[name];
        }

        set {
            base[name] = value;
        }
    }
}

/// <summary>
/// Info about updated files.
/// </summary>
public class UpdateInfo
{
    bool immidiatePrint = false;

    /// <summary>
    /// Last update information of files which were updated or planned to be updated.
    /// </summary>
    static public UpdateInfo lastUpdateInfo = null;

    /// <summary>
    /// true if testing is in progress
    /// </summary>
    static public bool bTesting = false;

    public UpdateInfo()
    {
        UpdateInfo alreadyUpdated = lastUpdateInfo;
        lastUpdateInfo = this;

        //  If test is in progres, we collect all information into same class instance.
        if (alreadyUpdated != null && bTesting)
        {
            lastUpdateInfo.filesUpdated.AddRange(alreadyUpdated.filesUpdated);
            lastUpdateInfo.filesUpToDate.AddRange(alreadyUpdated.filesUpToDate);
        }
    }

    public UpdateInfo(bool _immidiatePrint): this()
    {
        immidiatePrint = _immidiatePrint;
    }

    /// <summary>
    /// Files which were updated.
    /// </summary>
    public List<String> filesUpdated = new List<string>();

    /// <summary>
    /// Files which are up-to-date
    /// </summary>
    public List<String> filesUpToDate = new List<string>();

    /// <summary>
    /// Adds file which was updated.
    /// </summary>
    /// <param name="path">File path to be updated</param>
    /// <param name="bWasSaved">true if file was saved, false if file is up-to-date</param>
    public void MarkFileUpdated(String path, bool bWasSaved)
    {
        path = Path.GetFullPath(path);
        if (!bWasSaved)
        {
            filesUpToDate.Add(path);
        }
        else
        {
            filesUpdated.Add(path);
        }

        if (!immidiatePrint)
            return;

        if( bWasSaved)
            Console.WriteLine("File updated: " + Exception2.getPath(path));
    }


    /// <summary>
    /// Prints summary about update
    /// </summary>
    [ExcludeFromCodeCoverage]
    public void DisplaySummary()
    {
        // When testing is in progress we don't want detailed information on what was updated as what was not updated, as long as
        // summary accessed files is the same.
        if (!Exception2.g_bReportFullPath)
        { 
            Console.WriteLine((filesUpdated.Count + filesUpToDate.Count) + " files updated");
            return;
        }

        if (filesUpToDate.Count != 0)
            Console.Write(filesUpToDate.Count + " files are up-to-date. ");

        if (filesUpdated.Count != 0)
        {
            if (filesUpdated.Count == 1)
            {
                Console.WriteLine("File updated: " + filesUpdated[0]);
            }
            else
            {
                if (filesUpdated.Count < 3)
                {
                    Console.WriteLine("Files updated: " + String.Join(", ", filesUpdated.Select(x => Path.GetFileName(x))));
                }
                else
                {
                    Console.WriteLine(filesUpdated.Count + " files updated");
                }
            }
        }
        else
        {
            Console.WriteLine();
        }
    } //DisplaySummary
};


/// <summary>
/// Let's make system flexible enough that we can load projects and solutions independently of file format.
/// </summary>
[XmlInclude(typeof(Solution)), XmlInclude(typeof(Project))]
public class SolutionOrProject
{
    public object solutionOrProject;
    public String path;

    public SolutionOrProject(String _path)
    {
        String header = "";
        using (StreamReader sr = new StreamReader(_path, true))
        {
            header += sr.ReadLine();
            header += sr.ReadLine();
            sr.Close();
        }

        if (header.Contains("Microsoft Visual Studio Solution File"))
            solutionOrProject = Solution.LoadSolution(_path);
        else if (header.Contains("<SolutionOrProject"))
            LoadCache(_path);
        else
            solutionOrProject = Project.LoadProject(null, _path);
        
        path = _path;
    }

    public SolutionOrProject()
    {
    }


    public void SaveCache(String path)
    {
        XmlSerializer ser = new XmlSerializer(typeof(SolutionOrProject), typeof(SolutionOrProject).GetNestedTypes());

        using (var ms = new MemoryStream())
        {
            ser.Serialize(ms, this);
            String outS = Encoding.UTF8.GetString(ms.ToArray());
            File.WriteAllText(path, outS);
        }
    } //Save


    static public SolutionOrProject LoadCache(String path)
    {
        XmlSerializer ser = new XmlSerializer(typeof(SolutionOrProject), typeof(SolutionOrProject).GetNestedTypes());

        using (var s = File.OpenRead(path))
        {
            SolutionOrProject prj = (SolutionOrProject)ser.Deserialize(s);
            return prj;
        }
    }

    /// <summary>
    /// Walks through list of items, locates value using fieldName, identifies how many configuration lines we will need
    /// and inserts lines into lines2dump.
    /// </summary>
    /// <param name="proj">Project which hosts configuration list.</param>
    /// <param name="list">List to get values from</param>
    /// <param name="fieldName">Field name to scan </param>
    /// <param name="lines2dump">Lines which shall be created / updated.</param>
    /// <param name="valueToLine">value to config line translator function. Function can return null if no lines needs to be provided.</param>
    /// <param name="forceDefaultValue">If value cannot be configured in style - enabled/disable (one kind of flag only - enable) - specify here default value</param>
    static void ConfigationSpecificValue(Project proj, 
        IList list, 
        String fieldName, 
        Dictionary2<String, List<String>> lines2dump,
        Func<String, String> valueToLine,
        String forceDefaultValue = null
    )
    {
        if (list.Count == 0)
            return;

        Type itemType = list[0].GetType();
        FieldInfo fi = itemType.GetField(fieldName);
        // Value to it's repeatability.
        Dictionary2<String, int> weigths = new Dictionary2<string, int>();
        bool bCheckPlatformsVariation = proj.getPlatforms().Count > 1;
        bool bCheckConfigurationNameVariation = proj.getConfigurationNames().Count > 1;

        String defaultValue = forceDefaultValue;

        //-----------------------------------------
        //  Collect all values for comparison.
        //-----------------------------------------
        String[] values = new string[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            Object o = fi.GetValue(list[i]);
            if (o == null) continue;
            String value = o.ToString();
            values[i] = value;
        }

        if (defaultValue == null)
        {
            //---------------------------------------------------------------------
            // Select generic value, which is repeated in most of configurations.
            //---------------------------------------------------------------------
            for (int i = 0; i < list.Count; i++)
            {
                Object o = fi.GetValue(list[i]);
                if (o == null) continue;
                String value = o.ToString();
                weigths[value]++;
            }


            if (weigths.Values.Count != 0)
            {
                int maxWeight = weigths.Values.Max();
                List<String> maxValues = weigths.Where(x => x.Value == maxWeight).Select(x => x.Key).ToList();

                if (maxValues.Count == 1)
                    defaultValue = maxValues[0];
            } //if
        } //if

        weigths.Clear();
        //---------------------------------------------------------------------
        // Select by config or by platform specific values repeated in most of
        // configuration, and which are not satisfied by defaultValue if any.
        //---------------------------------------------------------------------
        Dictionary<String, String> confKeyToValue = new Dictionary<string, string>();
        Dictionary<String, bool> confKeyUseValue = new Dictionary<string, bool>();

        for (int i = 0; i < values.Length; i++)
        {
            String value = values[i];
            if (value == null) continue;

            String[] configNamePlatform = proj.configurations[i].Split('|');

            //
            // We try to match by configuration name or by platform name, but we must have all values identical for 
            // same configuration or platform - if they are not, then we reset weights counter.
            //
            foreach ( String confKey in new String[] { "c" + configNamePlatform[0], "p" + configNamePlatform[1] })
            {
                if (confKeyUseValue.ContainsKey(confKey))
                {
                    if (!confKeyUseValue[confKey])          //Disallowed to use, values not identical.
                        continue;

                    if (confKeyToValue[confKey] != value)   // Are values identical - if not then disable selection of same config again.
                    {
                        confKeyUseValue[confKey] = false;
                        weigths.Remove(confKey);
                        continue;
                    }

                    if (defaultValue == null || value != defaultValue)
                        weigths[confKey]++;                 // Increment usage count if not default value
                }
                else { 
                    confKeyUseValue[confKey] = true;        // Let's hope that all values will match.
                    confKeyToValue[confKey] = value;
                    
                    if (defaultValue == null || value != defaultValue)
                        weigths[confKey]++;                 // Increment usage count if not default value
                }
            }
        } //for
        
        // if we have some configuration defined, try to add to it.
        foreach (var kv in lines2dump)
            weigths[kv.Key]++;

        // Remove all single repeated entries ( Can list them separately )
        foreach (var k in weigths.Where(x => x.Value <= 1).Select(x => x.Key).ToList())
        weigths.Remove(k);

        // Configuration name "" (global), "c|p<Name>" (specific to configuration name or to platform)
        // "m<ConfigName>|<PlatformName>" - configuration & platform specific item.
        Dictionary<String, String> configToValue = new Dictionary<string, string>();

        //---------------------------------------------------------------------
        // Finally collect all single entries values, to see if defaultValue
        // and by config or by platform matches are needed anymore.
        //---------------------------------------------------------------------
        for (int i = 0; i < list.Count; i++)
        {
            Object o = fi.GetValue(list[i]);
            if (o == null) continue;
            String value = o.ToString();
            String[] configNamePlatform = proj.configurations[i].Split('|');
            String configName = configNamePlatform[0];
            String platform = configNamePlatform[1];

            if (configToValue.ContainsKey("") && configToValue[""] == value)
                continue;

            bool matched = false;

            foreach (var cvpair in configToValue)
            {
                matched = cvpair.Key.StartsWith("c") && cvpair.Key.Substring(1) == configName && cvpair.Value == value;
                if (matched) break;

                matched = cvpair.Key.StartsWith("p") && cvpair.Key.Substring(1) == platform && cvpair.Value == value;
                if (matched) break;
            }

            if (matched)
                continue;

            if (defaultValue != null && defaultValue == value)      // We have default value, and our default value matches our value.
            {
                configToValue[""] = value;      // Rule by default value.
                continue;
            }

            bool bCheckConfigurationName = bCheckConfigurationNameVariation;
            if (configToValue.ContainsKey("c" + configName))
            {
                if (configToValue["c" + configName] == value)
                    continue;

                bCheckConfigurationName = false;
            }

            if (bCheckConfigurationName && weigths.ContainsKey("c" + configName) )
            {
                configToValue["c" + configName] = value;
                continue;
            }

            bool bCheckPlatform = bCheckPlatformsVariation;
            if (configToValue.ContainsKey("p" + platform))
            {
                if (configToValue["p" + platform] == value)
                    continue;

                bCheckPlatform = false;
            }

            if (bCheckPlatform && weigths.ContainsKey("p" + platform))
            {
                configToValue["p" + platform] = value;
                continue;
            }

            configToValue["m" + proj.configurations[i]] = value;
        } //for

        foreach (var cvpair in configToValue)
        {
            String line = valueToLine(cvpair.Value);
            
            if (String.IsNullOrEmpty(line))
                continue;

            lines2dump[cvpair.Key].Add(line);
        }
    } //ConfigationSpecificValue


    //
    //  Parameters used in script generation functions
    //
    static bool bCsScript = false;      // set to true when generating C# script, false if .lua script
    static String brO = " ", brC = "";
    static String arO = " { ", arC = " }";
    static String head = "";
    static String comment = "-- ";
    static String lf = "\r\n";

    /// <summary>
    /// Tries to determine correct path of syncProj.exe
    /// </summary>
    /// <param name="inDir">Folder in which script is located</param>
    /// <param name="pathToSyncProjExe">Initial proposal where it can be stored</param>
    /// <returns>Path to syncProj.exe</returns>
    static public String getSyncProjExeLocation( String inDir, String pathToSyncProjExe = null)
    {
        // Let's try to locate where syncProj.exe is located if not provided or does not exists.
        String inPath = pathToSyncProjExe;

        if (inPath != null)
        {
            if (!Path.IsPathRooted(inPath))
                inPath = Path.Combine(inDir, pathToSyncProjExe);

            if (!File.Exists(inPath))
                inPath = null;
        }

        if (inPath == null)     // If path does not exists or not specified, try to autoprobe
        {
            String exePath = Assembly.GetExecutingAssembly().Location;  // If it was executed from syncProj.exe, then this is the path.

            //
            // With C# script it's bit much more complex. syncProj.exe was copied under temp folder because of
            // copy local = true. 
            //
            // For example path could be:
            //
            // C:\Users\<user>\AppData\Local\Temp\CSSCRIPT\89105968.shell\Debug\bin\<script>.dll
            //
            // And it's not possible to determine where that exe was copied from, except locate
            // script project, load it, and parse hint path out of there.
            //
            if (exePath.Contains("\\CSSCRIPT\\"))
            {
                String mainExe = Assembly.GetEntryAssembly().Location;
                String csScriptName = Path.GetFileNameWithoutExtension(mainExe);
                String csProject = Path.Combine(Path.GetDirectoryName(mainExe), "..\\..", csScriptName + " (script).csproj");
                if (File.Exists(csProject))
                    try
                    {
                        Project p = Project.LoadProject(null, csProject);
                        exePath = p.files.Where(x => x.includeType == IncludeType.Reference && x.relativePath == "syncproj").Select(x => x.HintPath).FirstOrDefault();
                        inPath = Path2.makeRelative(exePath, inDir);
                    }
                    catch { }
            } //if-else

            if (inPath == null)
                inPath = Path2.makeRelative(exePath, inDir);

            if (inPath != null)
                pathToSyncProjExe = inPath;
        } //if

        return pathToSyncProjExe;
    }



    /// <summary>
    /// Builds solution or project .lua/.cs scripts
    /// </summary>
    /// <param name="uinfo">Information about updates peformed (to print out summary)</param>
    /// <param name="path">Full path from where project was loaded.</param>
    /// <param name="solutionOrProject">Solution or project</param>
    /// <param name="bProcessProjects">true to process sub-project, false not</param>
    /// <param name="format">lua or cs</param>
    /// <param name="outFile">Output filename without extension</param>
    /// <param name="outPrefix">Output prefix (To add before project name)</param>
    static public void UpdateProjectScript(UpdateInfo uinfo, String path, object solutionOrProject, String outFile, String format, bool bProcessProjects, String outPrefix)
    {
        bCsScript = (format == "cs");
        if (bCsScript)
        {
            brO = "("; brC = ");";
            arO = "( "; arC = " );";
            head = "    ";
            comment = "// ";
        }
        else
        {
            brO = " "; brC = "";
            arO = " { "; arC = " }";
            head = "";
            comment = "-- ";
        }

        String fileName = outFile;
        if( fileName == null ) fileName = Path.GetFileNameWithoutExtension(path);
        if (outPrefix != "") fileName = outPrefix + fileName;

        Solution sln = solutionOrProject as Solution;

        if (sln != null)            // Visual studio MFC wizard can generate solution name = project name - we try here to separate solution from projects by using suffix.
            fileName += "_sln";

        String outDir = Path.GetDirectoryName(path);
        String outPath = Path.Combine(outDir, fileName + "." + format);

        StringBuilder o = new StringBuilder();
        Project proj = solutionOrProject as Project;

        String pathToSyncProjExe = "";

        //
        // C# script header
        //
        if (bCsScript)
        {
            pathToSyncProjExe = Path2.makeRelative(Assembly.GetExecutingAssembly().Location, Path.GetDirectoryName(Path.GetFullPath(path)));
            o.AppendLine("//css_ref " + pathToSyncProjExe);
            
            o.AppendLine("using System;");
            o.AppendLine();
            o.AppendLine("class Builder: SolutionProjectBuilder");
            o.AppendLine("{");
            o.AppendLine("    static void Main(String[] args)");
            
            o.AppendLine("    {");
            o.AppendLine("        try {");
        }

        o.AppendLine("");

        head = "    " + "    " + "    ";

        if (sln != null)
        {
            // ---------------------------------------------------------------------------------
            //  Building solution
            // ---------------------------------------------------------------------------------
            o.AppendLine(head + "solution" + brO + "\"" + fileName + "\"" + brC);
            o.AppendLine(head + "    " + ((format == "lua") ? comment : "") + "vsver" + brO + sln.fileFormatVersion + brC);

            o.AppendLine(head + "    configurations" + arO + " " + String.Join(",", sln.configurations.Select(x => "\"" + x.Split('|')[0] + "\"").Distinct()) + arC);
            o.AppendLine(head + "    platforms" + arO + String.Join(",", sln.configurations.Select(x => "\"" + x.Split('|')[1] + "\"").Distinct()) + arC);
            o.AppendLine(head + "    solutionScript(\"" + fileName + ".cs" + "\");");

            //
            // Packaging projects cannot include custom build steps, we include them from solution update.
            //
            foreach( Project p in sln.projects )
            {
                if( !p.bIsPackagingProject )
                    continue;

                String name = Path.GetFileNameWithoutExtension( p.RelativePath );
                String dir = Path.GetDirectoryName( p.RelativePath );

                String fileInclude = name;
                if( outPrefix != "" ) fileInclude = outPrefix + name;
                fileInclude = Path.Combine( dir, fileInclude + "." + format );
                fileInclude = fileInclude.Replace("\\", "\\\\");
                o.AppendLine(head + "        projectScript(\"" + fileInclude + "\");");
            }

            String wasInSubGroup = "";
            List<String> groupParts = new List<string>();

            foreach (Project p in sln.projects)
            {
                if (p.IsSubFolder())
                    continue;

                if(bProcessProjects && !p.bDefinedAsExternal)
                    UpdateProjectScript(uinfo, p.getFullPath(), p, null, format, false, outPrefix);
                    
                // Defines group / in which sub-folder we are.
                groupParts.Clear();
                Project pScan = p.parent;
                for (; pScan != null; pScan = pScan.parent)
                    // Solution root does not have project name
                    if (pScan.ProjectName!= null )
                        groupParts.Insert(0, pScan.ProjectName);

                String newGroup = String.Join("/", groupParts);
                if (wasInSubGroup != newGroup)
                {
                    o.AppendLine();
                    o.AppendLine(head + "    group" + brO + "\"" + newGroup + "\"" + brC);
                }
                wasInSubGroup = newGroup;

                // Define project
                String name = Path.GetFileNameWithoutExtension(p.getFullPath());
                String dir = Path.GetDirectoryName(p.RelativePath);
                o.AppendLine();

                if (!p.bDefinedAsExternal && bProcessProjects)
                {
                    String fileInclude = name;
                    if (outPrefix != "") fileInclude = outPrefix + name;

                    // o.AppendLine(head + "    " + comment + "Project '" + fileInclude + "'");
                    fileInclude = Path.Combine(dir, fileInclude + "." + format);

                    if (format == "lua")
                    {
                        fileInclude = fileInclude.Replace("\\", "/");
                        o.AppendLine(head + "    include \"" + fileInclude + "\"");
                    }
                    else {
                        fileInclude = fileInclude.Replace("\\", "\\\\");
                        o.AppendLine(head + "    invokeScript(\"" + fileInclude + "\");");
                    } //if-else
                }
                else
                {
                    o.AppendLine(head + "    externalproject" + brO + "\"" + name + "\"" + brC);
                    o.AppendLine(head + "        location" + brO + "\"" + dir.Replace("\\", "/") + "\"" + brC);
                    o.AppendLine(head + "        uuid" + brO + "\"" + p.ProjectGuid.Substring(1, p.ProjectGuid.Length - 2) + "\"" + brC);
                    o.AppendLine(head + "        language" + brO + "\"" + p.getLanguage() + "\"" + brC);
                    o.AppendLine(head + "        kind" + brO + "\"SharedLib\"" + brC);
                } //if-else

                //
                // Define dependencies of project.
                //
                if (p.ProjectDependencies != null)
                {
                    o.AppendLine();

                    foreach (String projDepGuid in p.ProjectDependencies)
                    {
                        Project depp = sln.projects.Where(x => x.ProjectGuid == projDepGuid).FirstOrDefault();
                        if (depp == null)
                            continue;

                        o.AppendLine(head + "        dependson" + brO + "\"" + outPrefix + depp.ProjectName + "\"" + brC);
                    } //foreach
                } //if
            } //foreach
        }
        else {
            // ---------------------------------------------------------------------------------
            //  Building project
            // ---------------------------------------------------------------------------------
            List<FileInfo> projReferences = proj.files.Where(x => x.includeType == IncludeType.ProjectReference).ToList();

            // ---------------------------------------------------------------------------------
            // Dump project references, pass 1.
            // ---------------------------------------------------------------------------------
            if (projReferences.Count != 0 && format == "lua")
            {
                foreach (FileInfo fi in projReferences)
                {
                    String projPath = fi.relativePath;
                    String projName = Path.GetFileNameWithoutExtension(projPath);
                    String projDir = Path.GetDirectoryName(projPath);
                    o.AppendLine(head + "externalproject" + brO + "\"" + projName + "\"" + brC);
                    o.AppendLine(head + "    location" + brO + "\"" + projDir.Replace("\\", "/") + "\"" + brC);
                    o.AppendLine(head + "    uuid" + brO + "\"" + fi.Project.Substring(1, fi.Project.Length - 2) + "\"" + brC);
                    o.AppendLine(head + "    kind \"SharedLib\"");
                    o.AppendLine();
                }
            }

            // ---------------------------------------------------------------------------------
            //  Building project
            // ---------------------------------------------------------------------------------
            o.AppendLine(head + "project" + brO + "\"" + fileName + "\"" + brC);

            if (format == "lua")
                o.AppendLine(head + "    location \".\"");

            o.AppendLine(head + "    configurations" + arO + " " + String.Join(",", proj.configurations.Select(x => "\"" + x.Split('|')[0] + "\"").Distinct()) + arC);
            o.AppendLine(head + "    platforms" + arO + String.Join(",", proj.configurations.Select(x => "\"" + x.Split('|')[1] + "\"").Distinct()) + arC);
            if(proj.ProjectGuid != null)
                o.AppendLine(head + "    uuid" + brO + "\"" + proj.ProjectGuidShort + "\"" + brC);
            
            if (format != "lua")
                o.AppendLine(head + "    vsver" + brO + proj.fileFormatVersion + brC);
            
            // Packaging projects cannot have custom build step at this moment
            if( bCsScript && !proj.bIsPackagingProject)
                o.AppendLine(head + "    projectScript(\"" + fileName + ".cs" + "\");");

            // ---------------------------------------------------------------------------------
            // Dump project references, pass 2.
            // ---------------------------------------------------------------------------------
            if (format == "lua")
            {
                foreach (FileInfo fi in projReferences)
                {
                    String projPath = fi.relativePath;
                    String projName = Path.GetFileNameWithoutExtension(projPath);

                    o.AppendLine(head + "    links " + arO + "\"" + projName + "\"" + arC);
                }
            }
            else
            {
                foreach (FileInfo fi in projReferences)
                    o.AppendLine(head + "    referencesProject(\"" + fi.relativePath.Replace("\\", "/") + "\", \"" + fi.Project.Substring(1, fi.Project.Length - 2) + "\");");
            }

            // ---------------------------------------------------------------------------------
            // Assembly references
            // ---------------------------------------------------------------------------------
            List<FileInfo> asmReferences = proj.files.Where(x => x.includeType == IncludeType.Reference).ToList();
            asmReferences.Sort(delegate (FileInfo f1, FileInfo f2)
                {
                    int r = f1.GetSortTag() - f2.GetSortTag();
                    if (r != 0)
                        return r;

                    return f1.relativePath.CompareTo(f2.relativePath);
                }
            );

            bool bOpenedJustNow = false;
            bool bOpened = false;
            int sortTag = -1;

            for( int iAsmRef = 0; iAsmRef < asmReferences.Count; iAsmRef++)
            {
                FileInfo fi = asmReferences[iAsmRef];
                FileInfo nextFi = (iAsmRef+1 < asmReferences.Count) ? asmReferences[iAsmRef+1]: null;
                String name = fi.relativePath;

                if (!String.IsNullOrEmpty(fi.HintPath))
                    name = fi.HintPath;

                if (!bOpened)
                {
                    o.Append(head + "    references(");
                    sortTag = fi.GetSortTag();
                    bOpened = true;
                    bOpenedJustNow = true;
                }
                else {
                    bOpenedJustNow = false;
                }

                bool bClose = nextFi == null || fi.GetSortTag() != nextFi.GetSortTag();

                if (!(bOpenedJustNow && bClose))
                {
                    o.AppendLine();
                    o.Append(head + "       ");
                }

                o.Append("\"" + name + "\"");

                if (bClose)
                {
                    bOpened = false;
                    if (!bOpenedJustNow)
                    {
                        o.AppendLine();
                        o.Append(head + "    ");
                    }

                    String args = fi.GetCopyFlagsAsCallParameters();
                    if (args != "")
                        o.Append(",");

                    o.AppendLine(args + ");");
                }
                else
                {
                    o.Append(",");
                }
            }

            Dictionary2<String, List<String>> lines2dump = new Dictionary2<string, List<string>>();

            foreach (var p in proj.projectConfig)
                if (p.SubSystem == ESubSystem.Console && p.ConfigurationType == EConfigurationType.Application)
                    p.ConfigurationType = EConfigurationType.ConsoleApplication;
            
            ConfigationSpecificValue(proj, proj.projectConfig, "ConfigurationType", lines2dump, (s) => {
                String ctype = typeof(EConfigurationType).GetMember(s)[0].GetCustomAttribute<FunctionNameAttribute>().tag;
                if (format == "lua")
                {
                    return "kind" + brO + "\"" + ctype + "\"" + brC;
                }
                else
                {
                    String os = proj.getOs();
                    String r = "kind" + brO + "\"" + ctype + "\"";
                    if ( os != null ) r += ",\"" + os + "\"";
                    r += brC;
                    return r;
                }
            } );

            proj.projectConfig.Where(x => x.ConfigurationType == EConfigurationType.ConsoleApplication).ToList().ForEach(x => x.ConfigurationType = EConfigurationType.Application);

            if( !proj.bIsPackagingProject )
                ConfigationSpecificValue(proj, proj.projectConfig, "UseDebugLibraries", lines2dump, (s) => {
                    return "symbols" + brO + "\"" + ((s == "True") ? "on" : "off") + "\"" + brC;
                });

            if (proj.Keyword == EKeyword.AntPackage || proj.Keyword == EKeyword.Android)
            {
                // If we have at least one api level set, we figure out the rest (defaults), and then we set up api level correctlt everywhere.
                bool bUsesApiLevel = proj.projectConfig.Where(x => x.AndroidAPILevel != null).FirstOrDefault() != null;
                for (int i = 0; i < proj.configurations.Count; i++)
                    if (proj.projectConfig[i].AndroidAPILevel == null)
                        proj.projectConfig[i].AndroidAPILevel = Configuration.getAndroidAPILevelDefault(proj.configurations[i]);

                ConfigationSpecificValue(proj, proj.projectConfig, "AndroidAPILevel", lines2dump, (s) => {
                    return "androidapilevel" + brO + "\"" + s + "\"" + brC;
                });

                ConfigationSpecificValue(proj, proj.projectConfig, "UseOfStl", lines2dump, (s) => 
                {
                    List<String> descriptions = Configuration.UseOfStl_getSupportedValues();
                    List<String> values = typeof(EUseOfStl).GetEnumNames().ToList();
                    int index = values.IndexOf(s);
                    String value;

                    if (index == -1)
                        value = s;
                    else
                        value = descriptions[index];

                    return "useofstl" + brO + "\"" + value + "\"" + brC;
                });

                ConfigationSpecificValue( proj, proj.projectConfig, "ThumbMode", lines2dump, ( s ) =>
                {
                    if( s == "NotSpecified" ) s = ""; else s = "EThumbMode." + s;
                    return "thumbmode" + brO + s + brC;
                });
            }

            if (proj.Keyword == EKeyword.MFCProj)
                o.AppendLine(head + "    flags" + brO + "\"MFC\"" + brC);

            if(proj.CLRSupport != ECLRSupport.None)
                o.AppendLine(head + "    commonLanguageRuntime" + brO + "ECLRSupport." + proj.CLRSupport.ToString() + brC);

            if (!String.IsNullOrEmpty(proj.WindowsTargetPlatformVersion))
                o.AppendLine(head + "    systemversion" + brO + "\"" + proj.WindowsTargetPlatformVersion + "\"" + brC);

            if (!String.IsNullOrEmpty(proj.TargetFrameworkVersion))
                o.AppendLine(head + "    TargetFrameworkVersion" + brO + "\"" + proj.TargetFrameworkVersion + "\"" + brC);

            ConfigationSpecificValue(proj, proj.projectConfig, "PlatformToolset", lines2dump, (s) => {
                return "toolset" + brO + "\"" + s + "\"" + brC;
            });

            if (!proj.bIsPackagingProject)
            {
                ConfigationSpecificValue(proj, proj.projectConfig, "CharacterSet", lines2dump, (s) => {
                    String r = typeof(ECharacterSet).GetMember(s)[0].GetCustomAttribute<FunctionNameAttribute>().tag;
                    return "characterset" + brO + "\"" + r + "\"" + brC;
                });

                ConfigationSpecificValue(proj, proj.projectConfig, "CLRSupport", lines2dump, (s) => {
                    if (s == "None") return null;
                    return "commonLanguageRuntime" + brO + "ECLRSupport." + s + brC;
                });
            }

            ConfigationSpecificValue(proj, proj.projectConfig, "UseOfMfc", lines2dump, (s) => {
                if (s == "Static")
                    return "flags" + brO + "\"StaticRuntime\"" + brC;

                return null;
            }, "Dynamic");

            ConfigationSpecificValue(proj, proj.projectConfig, "OutDir", lines2dump, (s) => { return "targetdir" + brO + "\"" + s.Replace("\\", "\\\\") + "\"" + brC; });
            ConfigationSpecificValue(proj, proj.projectConfig, "IntDir", lines2dump, (s) => { 
                // '!' is needed for premake to disallow to invent folder structure by itself.
                return "objdir" + brO + "\"" + s.Replace("\\", "\\\\")  + ((bCsScript) ? "": "!") + "\"" + brC;
            });
            ConfigationSpecificValue(proj, proj.projectConfig, "TargetName", lines2dump, (s) => { return "targetname" + brO + "\"" + s + "\"" + brC; });
            ConfigationSpecificValue(proj, proj.projectConfig, "TargetExt", lines2dump, (s) => { return "targetextension" + brO + "\"" + s + "\"" + brC; });

            if (!proj.bIsPackagingProject)
                ConfigationSpecificValue(proj, proj.projectConfig, "Optimization", lines2dump, (s) => {
                    String r = typeof(EOptimization).GetMember(s)[0].GetCustomAttribute<FunctionNameAttribute>().tag;
                    return "optimize" + brO + "\"" + r + "\"" + brC; 
                });

            // Can be used only to enabled, for .lua - ConfigationSpecificValue should be changed to operate on some default (false) value.
            ConfigationSpecificValue(proj, proj.projectConfig, "WholeProgramOptimization", lines2dump, (s) => {
                if(s == "UseLinkTimeCodeGeneration" )
                    return "flags" + arO + "\"LinkTimeOptimization\"" + arC;
                return "";
            });


            foreach (String step in new String[] { "PreBuild", "PreLink", "PostBuild" })
            {
                ConfigationSpecificValue(proj, proj.projectConfig, step + "Event", lines2dump, (s) =>
                {
                    BuildEvent bevent = XmlSerializer2.FromString<BuildEvent>(s);

                    if (bevent.Command == "") return "";

                    if (!bCsScript && step == "PreLink")
                        return "";

                    if (bCsScript)
                        return step.ToLower() + "commands" + arO + csQuoteString(bevent.Command) + arC;
                    else
                        return step.ToLower() + "commands" + arO + luaQuoteString(bevent.Command) + arC;
                });
            }

            UpdateConfigurationEntries(proj, proj.projectConfig, lines2dump);

            bool bFiltersActive = false;
            WriteLinesToDump(o, lines2dump, ref bFiltersActive, null);


            List<FileInfo> files2dump = proj.files.Where(x => x.includeType != IncludeType.ProjectReference && x.includeType != IncludeType.Reference).ToList();

            // Sort files by path, so it would be easier to check what is included and what is not included.
            files2dump.Sort(delegate (FileInfo f1, FileInfo f2) { return String.Compare(f1.relativePath, f2.relativePath); });

            //
            // Dump files array.
            //
            if (files2dump.Count != 0)
            {
                o.AppendLine(head + "    files" + arO);
                bool first = true;
                foreach (FileInfo fi in files2dump)
                {
                    if (!first) o.AppendLine(",");
                    first = false;
                    o.Append(head + "        \"" + fi.relativePath.Replace("\\", "/") + "\"");
                }
                o.AppendLine();
                o.AppendLine(head + "    " + arC);
            } //if

            foreach (FileInfo fi in proj.files)
            { 
                lines2dump.Clear();

                // Enable custom build steps for files which has custom build step defined.
                bool bHasCustomBuildStep = fi.fileConfig.Where(x => x.customBuildRule != null).FirstOrDefault() != null;

                if (bHasCustomBuildStep)
                    fi.includeType = IncludeType.CustomBuild;

                UpdateConfigurationEntries(proj, fi.fileConfig, lines2dump, fi.includeType, fi.relativePath);
                WriteLinesToDump(o, lines2dump, ref bFiltersActive, fi.relativePath );
            }

        } //if-else

        o.AppendLine();
        //
        // C# script trailer
        //
        if (bCsScript)
        {
            o.AppendLine("        } catch( Exception ex )");
            o.AppendLine("        {");
            o.AppendLine("            ConsolePrintException(ex, args);");
            o.AppendLine("        }");
            o.AppendLine("    } //Main");
            o.AppendLine("}; //class Builder");
            o.AppendLine();
        }

        String text2save = o.ToString();
        bool bSaveFile = true;

        if (File.Exists(outPath) && File.ReadAllText(outPath) == text2save)
        {
            uinfo.MarkFileUpdated(outPath, false);
            bSaveFile = false;
        }

        if (bSaveFile)
        {
            File.WriteAllText(outPath, text2save);
            uinfo.MarkFileUpdated(outPath, true);
        }

        //
        // .bat can simplify testing, but since projectScript function call this is not needed anymore.
        //
        //if (bCsScript)
        //{
        //    String bat = "@echo off\r\n" + pathToSyncProj + " " + Path.GetFileName(outPath);
        //    String outBat = Path.Combine(Path.GetDirectoryName(outPath), Path.GetFileNameWithoutExtension(outPath) + ".bat");

        //    if (File.Exists(outBat) && File.ReadAllText(outBat) == bat)
        //    {
        //        uinfo.nUpToDate++;
        //        return;
        //    }

        //    File.WriteAllText(outBat, bat);
        //    uinfo.filesUpdated.Add(outBat);
        //} //if

    } //UpdateProjectScript


    static void TagPchEntries(IEnumerable<FileConfigurationInfo> config, String fileName, bool bTag)
    {
        foreach (var cfg in config)
        {
            switch (cfg.PrecompiledHeader)
            {
                case EPrecompiledHeaderUse.NotUsing: cfg.PrecompiledHeaderFile = ""; break;
                case EPrecompiledHeaderUse.Create:
                    if( bTag )
                        cfg.PrecompiledHeaderFile = "?C" + fileName;
                    else
                        cfg.PrecompiledHeaderFile = cfg.PrecompiledHeaderFile.Substring(2);
                    break;
                case EPrecompiledHeaderUse.Use:
                    if( bTag )
                        cfg.PrecompiledHeaderFile = "?U" + cfg.PrecompiledHeaderFile;
                    else
                        cfg.PrecompiledHeaderFile = cfg.PrecompiledHeaderFile.Substring(2);
                    break;
            } //switch
        }
    } //TagPchEntries

    static String csQuoteString(String s)
    {
        s = Regex.Replace(s, @"\r\n|\n\r|\n|\r", "\n");     // Linefeed normalize, take 1.
        bool bUseMultilineQuotation = s.Contains('\n');

        if (!bUseMultilineQuotation)
        {
            int backSlashes = s.Count(x => x == '\\');
            bUseMultilineQuotation = backSlashes > 5;
        }

        if (bUseMultilineQuotation)
        {
            return "@\"" + s.Replace("\"", "\"\"") + "\"";
        }
        else
        { 
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }


    static String luaQuoteString(String s)
    {
        s = Regex.Replace(s, @"\r\n|\n\r|\n|\r", "\n");     // Linefeed normalize, take 1.
        s = s.Replace("\\", "\\\\").Replace("\n", "\\r\\n").Replace("\"", "\\\"");
        return "\"" + s + "\"";
    }


    /// <param name="proj">Project</param>
    /// <param name="config">Either project global configuration entries (Project.projectConfig) or file specified entries (FileInfo.fileConfig)</param>
    /// <param name="fileName">name of file of which configuration is being parsed.</param>
    /// <param name="lines2dump">lines to dump</param>
    /// <param name="includeType">item type to include (Custom build step or compilable source code)</param>
    static void UpdateConfigurationEntries(Project proj, IEnumerable<FileConfigurationInfo> config, Dictionary2<String, List<String>> lines2dump, 
        IncludeType includeType = IncludeType.ClCompile, String fileName = "")
    {
        List<FileConfigurationInfo> configList = config.ToList();
        bool bIsPackagingProject = proj.Keyword == EKeyword.AntPackage || proj.Keyword == EKeyword.GradlePackage;

        if (proj.projectConfig == config)
        {
            // project defaults, by default not using precompiled headers.
            foreach (var conf in config)
                if (conf.PrecompiledHeader == EPrecompiledHeaderUse.ProjectDefault)
                    conf.PrecompiledHeader = EPrecompiledHeaderUse.NotUsing;
        }
        else {
            // Inherit from project configuration
            for (int i = 0; i < configList.Count; i++)
                if (configList[i].PrecompiledHeader == EPrecompiledHeaderUse.ProjectDefault)
                    configList[i].PrecompiledHeader = proj.projectConfig[i].PrecompiledHeader;
        }

        TagPchEntries(config, fileName, true);

        ConfigationSpecificValue(proj, configList, "ExcludedFromBuild", lines2dump, (s) =>
        {
            if (s == "False" || fileName == "") return "";
            return "flags" + arO + "\"ExcludeFromBuild\"" + arC;
        });

        // For custom build type we dont have pchheader info
        if (includeType == IncludeType.ClCompile && !bIsPackagingProject)
        {
            ConfigationSpecificValue(proj, configList, "PrecompiledHeaderFile", lines2dump, (s) =>
            {
                if (s == "")
                    return "flags" + arO + "\"NoPch\"" + arC;

                char cl = s[1];
                s = s.Substring(2);
                switch (cl)
                {
                    case 'C':
                        return "pchsource" + brO + "\"" + s + "\"" + brC;
                    default:
                    case 'U':
                        return "pchheader" + brO + "\"" + s + "\"" + brC;
                }
            });

            ConfigationSpecificValue(proj, configList, "ClCompile_AdditionalOptions", lines2dump, (s) =>
            {
                s = s.Replace(" %(AdditionalOptions)", "");     // Just a extra garbage we don't want.
                if (s == "")
                    return null;
                return "buildoptions" + brO + "\"" + s + "\"" + brC;
            });

            ConfigationSpecificValue(proj, configList, "Link_AdditionalOptions", lines2dump, (s) =>
            {
                s = s.Replace(" %(AdditionalOptions)", "");     // Just a extra garbage we don't want.
                if (s == "")
                    return null;
                return "linkoptions" + brO + "\"" + s + "\"" + brC;
            });

        } //if
                
        TagPchEntries(config, fileName, false);

        if (includeType == IncludeType.CustomBuild)
        {
            ConfigationSpecificValue(proj, configList, "customBuildRule", lines2dump, (s) =>
            {
                CustomBuildRule cbp = CustomBuildRule.FromString(s);
                String r;

                if (bCsScript)
                {
                    r = "buildrule" + arO + "new CustomBuildRule() {" + lf;
                    if( cbp.Message != "" )
                        r += "    Message = " + csQuoteString(cbp.Message) + ", " + lf;
                    r += "    Command = " + csQuoteString(cbp.Command) + ", " + lf;
                    r += "    Outputs = " + csQuoteString(cbp.Outputs);

                    if( cbp.AdditionalInputs != "" )
                        r += "," + lf + "    AdditionalInputs = " + csQuoteString(cbp.AdditionalInputs);

                    if ( cbp.LinkObjects )
                        r += lf;
                    else
                        r += "," + lf + "    LinkObjects = false" + lf;
                    
                    r += "});";
                }
                else
                {
                    r = "buildrule" + arO + lf;
                    r += "    description = " + luaQuoteString(cbp.Message) + ", " + lf;
                    r += "    commands = " + luaQuoteString(cbp.Command) + ", " + lf;
                    r += "    output = " + luaQuoteString(cbp.Outputs)  + lf;
                    r += arC;
                }

                return r;
            });
        } //if

        //---------------------------------------------------------------------------------
        // Semicolon (';') separated lists.
        //  Like defines, additional include directories, libraries
        //---------------------------------------------------------------------------------
        String[] fieldNames = new String[] { 
            "PreprocessorDefinitions", "AdditionalUsingDirectories", "AdditionalIncludeDirectories", 
            "AdditionalDependencies", "LibraryDependencies", 
            "AdditionalLibraryDirectories",
            "IncludePath", "LibraryPath",
            "DisableSpecificWarnings"
        };

        String[] funcNames = new String[] { 
            "defines", "usingdirs", "includedirs", 
            "links", "links", 
            "libdirs",
            "sysincludedirs", "syslibdirs",
            "disablewarnings"
        };

        for (int iListIndex = 0; iListIndex < fieldNames.Length; iListIndex++)
        {
            String commaField = fieldNames[iListIndex];

            FieldInfo fi = typeof(FileConfigurationInfo).GetField(commaField);

            if (fi == null && configList.Count != 0 && configList.First().GetType() == typeof(Configuration))
                fi = typeof(Configuration).GetField(commaField);

            if (fi == null)     // E.g. IncludePath does not exists in FileConfigurationInfo.
                continue;

            List<List<String>> items = new List<List<string>>();
            List<String> origValues = new List<string>();               // To be able to restore model back for new format.

            // Collect all values from semicolon separated string.
            foreach (var cfg in configList)
            {
                String value = (String)fi.GetValue(cfg);
                origValues.Add(value);
                // Sometimes defines can contain linefeeds but from configuration perspective they are useless.
                value = value.Replace("\n", "");
                items.Add(value.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList());
            }

            while (true)
            {
                // Until we still have values.
                String oneValue = null;
                for (int i = 0; i < items.Count; i++)
                    if (items[i].Count != 0)
                    {
                        oneValue = items[i][0];
                        break;
                    }

                if (oneValue == null)
                    break;

                for (int i = 0; i < items.Count; i++)
                    if (items[i].Remove(oneValue))
                        fi.SetValue(configList[i], oneValue);
                    else
                        fi.SetValue(configList[i], "");

                ConfigationSpecificValue(proj, configList, commaField, lines2dump, (s) => { return funcNames[iListIndex] + s; });
            } //for

            // Restore original values into model (to process next format if needed)
            for( int i = 0; i < configList.Count; i++ )
                fi.SetValue(configList[i], origValues[i]);
        } //for

        String[] funcNames2 = new String[] { "defines", "usingdirs",
            "includedirs", "links", "libdirs", "sysincludedirs", "syslibdirs", "disablewarnings" };

        foreach (String funcName in funcNames2)
        {
            foreach (var kv in lines2dump)
            {
                String line = funcName + arO;
                bool bAddLine = false;
                bool first = true;
                List<String> lines2add = new List<string>();

                for (int i = 0; i < kv.Value.Count; i++)
                {
                    String s = kv.Value[i];
                    if (!s.StartsWith(funcName))
                        continue;

                    kv.Value.RemoveAt(i);
                    i--;

                    String oneEntryValue = s.Substring(funcName.Length);

                    if (oneEntryValue == "" ||
                        // Special kind of define which simply tells to inherit from project settings.
                        (funcName == "usingdirs" && oneEntryValue == "%(AdditionalUsingDirectories)") ||
                        (funcName == "includedirs" && oneEntryValue == "%(AdditionalIncludeDirectories)") ||
                        (funcName == "defines" && oneEntryValue == "%(PreprocessorDefinitions)") ||
                        (funcName == "links" && oneEntryValue == "%(AdditionalDependencies)") ||
                        // Android project
                        (funcName == "links" && oneEntryValue == "%(LibraryDependencies)") ||
                        (funcName == "sysincludedirs" && oneEntryValue == "$(IncludePath)") ||
                        (funcName == "syslibdirs" && oneEntryValue == "$(LibraryPath)") ||
                        (funcName == "disablewarnings" && oneEntryValue == "%(DisableSpecificWarnings)")
                        )
                            continue;

                    oneEntryValue = oneEntryValue.Replace("\\", "\\\\").Replace("\"", "\\\"");    // Escape \ => \\, " => \"
                    if (!first) line += ", ";
                    first = false;
                    line += "\"" + oneEntryValue + "\"";
                    bAddLine = true;

                    // Chop off into multiple lines, if too many entries.
                    if (line.Length > 120)
                    { 
                        line += arC;
                        lines2add.Add(line);
                        line = funcName + arO;
                        bAddLine = false;
                        first = true;
                    }
                } //for
                line += arC;
                
                if(bAddLine)
                    lines2add.Add(line);
                
                if(lines2add.Count != 0)
                    kv.Value.AddRange(lines2add);
            } //foreach
        } //foreach

    } //UpdateConfigurationEntries

    static Regex reEndOfMuiltLineString = new Regex("([^\"]|^)\"([^\"]|$)");
    /// <summary>
    /// Adds heading (tab) before each line - s is multiline string.
    /// This code is also detects multiline string start, then heading is not appended.
    /// </summary>
    static String tabbedLine(String tab, String s)
    {
        String r = "";
        s = Regex.Replace(s, @"\r\n|\n\r|\n|\r", "\r\n");     // Linefeed normalize, take 2.
        bool multiLineQuotedString = false;

        foreach (String line in s.Split(new String[] { lf }, StringSplitOptions.None))
        {
            int pos = 0;

            if(!multiLineQuotedString )
                r += tab;

            r += line + lf;

            if (!multiLineQuotedString)
                pos = line.IndexOf("@\"");

            if (pos != -1 && !multiLineQuotedString)
                multiLineQuotedString = true;

            if(multiLineQuotedString && line.Length >= 2 && reEndOfMuiltLineString.Match(line, pos + 2, line.Length - pos - 2).Success )
                multiLineQuotedString = false;
        } //forach

        return r;
    }

    /// <summary>
    /// Writes lines to dump into built string.
    /// </summary>
    static void WriteLinesToDump(StringBuilder o, Dictionary2<String, List<String>> lines2dump, ref bool bFiltersActive, String file = null )
    {
        if (lines2dump.Count == 0)
            return;

        String lhead = head;

        if (file != null)
        {
            file = file.Replace("\\", "/");
            lhead += "    ";
        }

        if (lines2dump.ContainsKey("") && lines2dump[""].Count != 0 )
        {
            if (file != null)
            {
                //
                // Dump "pchsource" line as special kind of line (without applying filters).
                //
                String pchSourceLine = lines2dump[""].Where(x => x.StartsWith("pchsource")).FirstOrDefault();
                if (pchSourceLine != null)
                {
                    if (bFiltersActive)
                        o.AppendLine(head + "    filter " + arO + arC);

                    o.AppendLine(head + "    " + pchSourceLine);
                    lines2dump[""].Remove(pchSourceLine);
                }

                if (lines2dump[""].Count != 0)
                    o.AppendLine(head + "    filter " + arO + "\"files:" + file + "\" " + arC);
            } //if

            foreach (String line in lines2dump[""])
            {
                o.Append(tabbedLine(lhead + "    ", line));
            }
            lines2dump.Remove("");
        }

        foreach (var kv in lines2dump)
        {
            if ( kv.Value.Count == 0)
                continue;
                
            char c = kv.Key.Substring(0, 1)[0];
            String s = kv.Key.Substring(1);
            switch (c)
            {
                case 'c': o.Append(SolutionOrProject.head + "    filter " + arO + "\"" + s + "\""); break;
                case 'p': o.Append(SolutionOrProject.head + "    filter " + arO + "\"platforms:" + s + "\""); break;
                case 'm': o.Append(SolutionOrProject.head + "    filter " + arO + "\"" + s.Replace("|", "\", \"platforms:") + "\""); break;
                default: 
                    throw new Exception2("Internal code check");
            }

            bFiltersActive = true;

            if (file != null)
                o.Append(", \"files:" + file + "\"");
            o.AppendLine(arC);

            foreach (String line in kv.Value)
            {
                if (line.Length == 0) continue;
                o.Append(tabbedLine(head + "        ", line));
            }

            o.AppendLine("");
        } //foreach

        if (lines2dump.Count != 0 && file == null )      // Close any filter if we have ones.
        {
            o.AppendLine(head + "    filter " + arO + arC);
            o.AppendLine("");
        }
    } //WriteLinesToDump



}

/// <summary>
/// use this class like this:
/// 
///     using ( new UsingSyncProj(1) )
///     {
///         files ...
///         filter ...
///         
///     }
/// </summary>
public class UsingSyncProj : IDisposable
{
    int shiftCallerFrame;
    
    /// <summary>
    /// Shifts all callstack frames by N frames.
    /// </summary>
    public UsingSyncProj(int _shiftCallerFrame)
    {
        shiftCallerFrame = _shiftCallerFrame;
        Exception2.shiftCallerFrame += shiftCallerFrame;
    }

    /// <summary>
    /// Restores call stack frame back.
    /// </summary>
    public void Dispose()
    {
        Exception2.shiftCallerFrame -= shiftCallerFrame;
    }
};



/// <summary>
/// Same as Exception, only we save call stack in here (to be able to report error line later on).
/// </summary>
public class Exception2 : Exception
{
    /// <summary>
    /// When testing, this will be set to false.
    /// </summary>
    public static bool g_bReportFullPath = true;

    /// <summary>
    /// returns 'path' if unit test is not active, otherwise picks up only filename without file path.
    /// </summary>
    static public String getPath(String path)
    {
        if (g_bReportFullPath)
            return path;

        return Path.GetFileName(path);
    }


    StackTrace strace;
    String msg;
    int nCallerFrame = 0;
    /// <summary>
    /// Global variable for those cases when syncProj uses functions meant for end-users (like files, filter, etc...)
    /// To get excepting address correctly.
    /// </summary>
    public static int shiftCallerFrame = 0;

    /// <summary>
    /// Creates new exception with stack trace from where exception was thrown.
    /// </summary>
    /// <param name="_msg"></param>
    /// <param name="callerFrame">Frame count which called this function</param>
    public Exception2( String _msg, int callerFrame = 0 )
    {
        msg = _msg;
        strace = new StackTrace(true);
        nCallerFrame = callerFrame + shiftCallerFrame;
    }

    /// <summary>
    /// Tries to determine from which script position exception was thrown. Returns empty line if cannot be detected.
    /// </summary>
    /// <returns>Throw source code line</returns>
    virtual public String getThrowLocation()
    {
        if (strace.FrameCount < nCallerFrame + 2)
            return "";

        StackFrame f = strace.GetFrame(nCallerFrame + 2);
        if (f.GetFileName() != null)
            return Exception2.getPath(f.GetFileName()) + "(" + f.GetFileLineNumber() + "," + f.GetFileColumnNumber() + "): ";
        
        return "";
    } //getThrowLocation


    /// <summary>
    /// Gets exception message
    /// </summary>
    public override string Message
    {
        get
        {
            return msg;
        }
    }

    [ExcludeFromCodeCoverage]   // Not executed during testing, as output varies
    /// <summary>
    /// Formats stack trace into double clickable trace format.
    /// </summary>
    /// <param name="strace">stack trace</param>
    /// <param name="bShowFrames">true if show frame numbers</param>
    /// <returns>String</returns>
    public static String formatStackTrace(StackTrace strace, bool bShowFrames = false)
    {
        String s = "";

        for (int i = 0; i < strace.FrameCount; i++)
        {
            StackFrame sf = strace.GetFrame(i);
            String f = sf.GetFileName();

            if (!g_bReportFullPath)
                f = Path.GetFileName(f);

            // Omit stack trace if filename is not known (Simplify output)
            if (f != null)
            {
                s += f + "(" + sf.GetFileLineNumber() + "," + sf.GetFileColumnNumber() + "): ";
                s += sf.GetMethod() + ((bShowFrames) ? ("frame: " + i.ToString()): "") +  "\r\n";
            }
        }

        return s;
    }

    [ExcludeFromCodeCoverage]   // Not executed during testing, as output varies
    /// <summary>
    /// Format stack trace so it would be double clickable in Visual studio output window.
    /// http://stackoverflow.com/questions/12301055/double-click-to-go-to-source-in-output-window
    /// </summary>
    public override string StackTrace
    {
        get
        {
            return formatStackTrace(strace);
        }
    }

};

/// <summary>
/// Exception which references specific file, specific line.
/// </summary>
public class FileSpecificException : Exception2
{
    String file;
    int line;

    /// <summary>
    /// new exception which references specific file / line of source code position
    /// </summary>
    public FileSpecificException(String _msg, String _file, int _line) :
        base(_msg)
    {
        file = _file;
        line = _line;
    }

    /// <summary>
    /// Gets source code location from where error came from
    /// </summary>
    override public String getThrowLocation()
    {
        return Exception2.getPath(file) + "(" + line + "): ";
    }
};



/// <summary>
/// Path helper class.
/// </summary>
public class Path2
{
    /// <summary>
    /// Gets source code full path of script, executing given function.
    /// </summary>
    /// <param name="iFrame">number of frame in stack.</param>
    /// <returns>Source code path, null if cannot be determined</returns>
    public static String GetScriptPath(int iFrame = 0)
    {
        StackTrace st = new System.Diagnostics.StackTrace(true);
        string fileName = null;

        if (iFrame < 0)
            iFrame = (st.FrameCount + iFrame) % st.FrameCount;

        if( iFrame < st.FrameCount )
            fileName = st.GetFrame(iFrame).GetFileName();                       // For C# script.
        else
            if(st.FrameCount != 0 )
                fileName = st.GetFrame(st.FrameCount - 1).GetFileName();        // For syncproj.exe, which is launching C# script.
        //
        // http://www.csscript.net/help/Environment.html
        //
        if (fileName == null)
        {
            AssemblyDescriptionAttribute asa = Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyDescriptionAttribute), true).
                Cast<AssemblyDescriptionAttribute>().FirstOrDefault();

            if ( asa != null )                                                   // If it's not C# script, it will be null
                fileName = asa.Description;
        }

        return fileName;
    }

    /// <summary>
    /// Rebases file with path fromPath to folder with baseDir.
    /// </summary>
    /// <param name="_fromPath">Full file path (absolute)</param>
    /// <param name="_baseDir">Full base directory path (absolute)</param>
    /// <returns>Relative path to file in respect of baseDir</returns>
    static public String makeRelative(String _fromPath, String _baseDir)
    {
        String pathSep = "\\";
        String fromPath = Path.GetFullPath(_fromPath);
        String baseDir = Path.GetFullPath(_baseDir);            // If folder contains upper folder references, they gets lost here. "c:\test\..\test2" => "c:\test2"

        String[] p1 = Regex.Split(fromPath, "[\\\\/]").Where(x => x.Length != 0).ToArray();
        String[] p2 = Regex.Split(baseDir, "[\\\\/]").Where(x => x.Length != 0).ToArray();
        int i = 0;

        for (; i < p1.Length && i < p2.Length; i++)
            if (String.Compare(p1[i], p2[i], true) != 0)    // Case insensitive match
                break;

        if (i == 0)     // Cannot make relative path, for example if resides on different drive
            return fromPath;
                
        String r = String.Join(pathSep, Enumerable.Repeat("..", p2.Length - i).Concat(p1.Skip(i).Take(p1.Length - i)));
        return r;
    }
};


/// <summary>
/// syncProj tool.
/// </summary>
public partial class syncProj
{
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

            for( int i = 0; i < args.Length; i++ )
            {
                String arg = args[i];

                if (!(arg.StartsWith("-") || arg.StartsWith("/")))
                {
                    inFile = arg;
                    continue;
                }

                switch (arg.Substring(1).ToLower())
                {
                    case "lua": formats.Add("lua"); break;
                    case "cs": formats.Add("cs"); break;
                    case "o": i++; outFile = args[i]; break;
                    case "p": i++; outPrefix = args[i]; break;
                    case "sln": bProcessProjects = false; break;
                    case "t": 
                        StartSelfTests(args, i + 1); 
                        bTestsExecuted = true;
                        SolutionProjectBuilder.bSaveGeneratedProjects = false;
                        break;
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

