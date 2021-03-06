using Ionic.Zip;
using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace BerryBrew
{
    public class Berrybrew
    {
        // sends a setting change message to reconfigure PATH

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, 
            int Msg, 
            IntPtr wParam, 
            string lParam, 
            uint fuFlags, 
            uint uTimeout, 
            IntPtr 
            lpdwResult
        );
        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xffff);
        private const int WM_SETTINGCHANGE = 0x001a;
        private const int SMTO_ABORTIFHUNG = 0x2;

        public bool Debug { set; get; }

        static string assembly_path = Assembly.GetExecutingAssembly().Location;
        static string assembly_directory = Path.GetDirectoryName(assembly_path);       

        public string binPath = assembly_directory;
        public string installPath = null;
        public string rootPath = null;
        public string confPath = null;
        public string archivePath = null;
        public string downloadURL = null;
        public string strawberryURL = null;
        public bool customExec = false;

        internal bool bypassOrphanCheck = false;

        public Message Message = new Message();
        public OrderedDictionary Perls = new OrderedDictionary();

        public Berrybrew()
        {
            this.installPath = Regex.Replace(this.binPath, @"bin", "");
            this.confPath = installPath + @"/data/";

            // config

            dynamic jsonConf = JsonParse("config");
            this.rootPath = jsonConf.root_dir + "\\";
            this.archivePath = jsonConf.temp_dir;
            this.strawberryURL = jsonConf.strawberry_url;
            this.downloadURL = jsonConf.download_url;
            if (jsonConf.custom_exec == "true")
                this.customExec = true;

            Debug = jsonConf.debug;

            // ensure the Perl install dir exists

            CheckRootDir();

            // create the custom perls config file

            string customPerlsFile = this.confPath + @"perls_custom.json";

            if (!File.Exists(customPerlsFile))
            {
                File.WriteAllText(customPerlsFile, @"[]");
            }
            
            // messages

            dynamic jsonMessages = JsonParse("messages");
            foreach (dynamic entry in jsonMessages)
            {
                Message.Add(entry);
            }

            // perls
            
            bool installPerlsIntoSelf = true;
            PerlGenerateObjects(installPerlsIntoSelf);
        }

        ~Berrybrew()
        {
            List<string> orphans = PerlFindOrphans();

            if (orphans.Count > 0 && !this.bypassOrphanCheck)
            {
                string orphanedPerls = Message.Get("perl_orphans");
                Console.WriteLine("\nWARNING! {0}\n\n", orphanedPerls.Trim());
                foreach (string orphan in orphans)
                {
                    Console.WriteLine("  {0}\n", orphan);
                }
            }
        }

        public void Available()
        {
            Message.Print("available_header");

            StrawberryPerl currentPerl = PerlInUse();
            List<int> nameLengths = new List<int>();

            foreach (string perlName in Perls.Keys)
            {
                nameLengths.Add(perlName.Length);
            }

            int maxNameLength = nameLengths.Max();

            foreach (StrawberryPerl perl in Perls.Values)
            {
                string perlNameToPrint = perl.Name + new String(' ', (maxNameLength - perl.Name.Length) + 2);
                Console.Write("\t" + perlNameToPrint);

                if (perl.Custom)
                    Console.Write(" [custom]");
                if (PerlIsInstalled(perl))
                    Console.Write(" [installed]");
                if (perl.Name == currentPerl.Name)
                    Console.Write(" *");
              
                Console.Write("\n");
            }
            Message.Print("available_footer");
        }

        private static bool CheckName (string perlName)
        {
            if (perlName.Length > 25)
            {
                Console.WriteLine(
                    "name for a Perl must be 25 chars or less. You supplied {0}, length {1}",
                    perlName,
                    perlName.Length
                );
                return false;
            }
            return true;
        }

        internal void CheckRootDir()
        {
            if (!Directory.Exists(this.rootPath))
            {
                try
                {
                    Directory.CreateDirectory(this.rootPath);
                }
                catch (Exception err)
                {
                    Console.WriteLine("\nCouldn't create install dir {0}. Please create it manually and run config again", this.rootPath);
                    if (Debug)
                    {
                        Console.WriteLine(err);
                    }
                }
            }
        }

        public void Clean(string subcmd="temp")
        {
            bool cleansed = false;

            switch (subcmd)
            {
                case "temp":
                    cleansed = CleanTemp();
                    if (cleansed)
                        Console.WriteLine("\nremoved all files from {0} temp dir", this.rootPath);
                    else
                        Console.WriteLine("\nno archived perl installation files to remove");
                    break;
                
                case "orphan":
                    cleansed = CleanOrphan();
                    if (!cleansed)
                        Console.WriteLine("\nno orphaned perls to remove");
                    break;
            }
        }

        internal bool CleanOrphan()
        {
            List<string> orphans = PerlFindOrphans();

            foreach (string orphan in orphans)
            {
                FilesystemResetAttributes(orphan);
                Directory.Delete(this.rootPath + orphan, true);
                Console.WriteLine("removed orphan {0} perl instance", orphan);
            }

            if (orphans.Count > 0)
                return true;
            return false;
        }

        internal bool CleanTemp()
        {
            System.IO.DirectoryInfo archiveDir = new DirectoryInfo(archivePath); 
            FilesystemResetAttributes(archiveDir.FullName);

            List<FileInfo> zipFiles = archiveDir.GetFiles().ToList();

            foreach (FileInfo file in zipFiles) 
            {
                file.Delete();
            }

            if (zipFiles.Count > 0)
                return true;
            return false;
        }

        public bool Clone(string sourcePerlName, string destPerlName)
        {
            if (!CheckName(destPerlName))
                return false;

            StrawberryPerl sourcePerl = PerlResolveVersion(sourcePerlName);
            string sourcePerlDir = sourcePerl.InstallPath;
            string destPerlDir = this.rootPath + destPerlName;
            DirectoryInfo src = new DirectoryInfo(sourcePerlDir);

            if (!src.Exists)
            {
                throw new DirectoryNotFoundException(
                    "Source directory does not exist or could not be found: " 
                    + sourcePerlDir
                );
            }
            try
            {
                if (!Directory.Exists(destPerlDir))
                    Directory.CreateDirectory(destPerlDir);

                foreach (string dirPath in Directory.GetDirectories(sourcePerlDir, "*",
                    SearchOption.AllDirectories))
                    Directory.CreateDirectory(dirPath.Replace(sourcePerlDir, destPerlDir));

                foreach (string newPath in Directory.GetFiles(sourcePerlDir, "*.*",
                    SearchOption.AllDirectories))
                    File.Copy(newPath, newPath.Replace(sourcePerlDir, destPerlDir), true);

                if (!Directory.Exists(destPerlDir))
                {
                    Console.WriteLine("\nfailed to clone {0} to {1}", sourcePerlDir, destPerlDir);
                    Environment.Exit(0);
                }

                PerlRegisterCustomInstall(destPerlName, sourcePerl);

                Console.WriteLine("\nSuccessfully installed custom perl '{0}'", destPerlName);
                return true;
            }
            catch (System.IO.IOException err)
            {
                Console.WriteLine("\nClone failed due to disk I/O error... ensure the disk isn't full\n");
                if (Debug)
                    Console.WriteLine(err);
                return false;
            }
        }

        public void Config()
        {
            string configIntro = Message.Get("config_intro");
            Console.WriteLine(configIntro + Version() + "\n");

            if (!PathScan(new Regex("berrybrew.bin"), "machine"))
            {
                Message.Print("add_bb_to_path");

                if (Console.ReadLine() == "y")
                {
                    PathAddBerryBrew(this.binPath);

                    if (PathScan(new Regex("berrybrew.bin"), "machine"))
                        Message.Print("config_success");
                    else
                        Message.Print("config_failure");
                }
            }
            else
                Message.Print("config_complete");
        }
       
        internal void Exec(StrawberryPerl perl, string command, string sysPath)
        {
            Console.WriteLine("Perl-" + perl.Name + "\n==============");

            Process process = new Process();
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;

            List<String> newPath;
            newPath = perl.Paths;
            newPath.Add(sysPath);
            
            System.Environment.SetEnvironmentVariable("PATH", String.Join(";", newPath));

            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = "/c " + perl.PerlPath + @"\" + command;
            process.StartInfo = startInfo;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.Start();

            Console.WriteLine(process.StandardOutput.ReadToEnd());
            Console.WriteLine(process.StandardError.ReadToEnd());
            process.WaitForExit();
        }

        public void ExecCompile(string parameters)
        {
            List<StrawberryPerl> perlsInstalled = PerlsInstalled();
            List<StrawberryPerl> execWith = new List<StrawberryPerl>();
            string command;

            if (parameters.StartsWith("--with"))
            {
                string paramList = Regex.Replace(parameters, @"--with\s+", "");

                string perlStr = paramList.Split(new[] { ' ' }, 2)[0];
                command = paramList.Split(new[] { ' ' }, 2)[1];

                string[] perls = perlStr.Split(',');

                foreach (StrawberryPerl perl in perlsInstalled)
                {
                    foreach (string perlName in perls)
                    {
                        if (perlName.Equals(perl.Name))
                            execWith.Add(perl);
                    }
                }
            }
            else
            {
                command = parameters;
                execWith = perlsInstalled;
            }

            string sysPath = PathRemovePerl(false);

            foreach (StrawberryPerl perl in execWith)
            {
                if (perl.Custom && !this.customExec)
                    continue;
                if (perl.Name.Contains("tmpl") || perl.Name.Contains("template"))
                    continue;
                Exec(perl, command, sysPath);
            }
        }

        private void Extract(StrawberryPerl perl, string archivePath)
        {
            if (File.Exists(archivePath))
            {
                Console.WriteLine("Extracting " + archivePath);
                try
                {
                    if (Debug)
                        Console.WriteLine("\nExtracting {0} to {1}", archivePath, perl.InstallPath);

                    using (ZipFile zip = ZipFile.Read(archivePath))
                    {
                        foreach (ZipEntry e in zip)
                        {
                            e.Extract(perl.InstallPath);
                        }
                    }
                }
                catch (Exception)
                {
                    Console.WriteLine("\nFailed to extract {0} to {1}", archivePath, perl.InstallPath);
                }
            }
        }

        private string Fetch(StrawberryPerl perl)
        {
            WebClient webClient = new WebClient();
            string archivePath = PerlArchivePath(perl);

            if (!File.Exists(archivePath))
            {
                Console.WriteLine("Downloading " + perl.Url + " to " + archivePath);
                webClient.DownloadFile(perl.Url, archivePath);
            }

            Console.WriteLine("Confirming checksum ...");
            using (var cryptoProvider = new SHA1CryptoServiceProvider())
            {
                using (var stream = File.OpenRead(archivePath))
                {
                    string hash = BitConverter.ToString(cryptoProvider.ComputeHash(stream)).Replace("-", "").ToLower();

                    if (perl.Sha1Checksum != hash)
                    {
                        Console.WriteLine("Error checksum of downloaded archive \n"
                            + archivePath
                            + "\ndoes not match expected output\nexpected: "
                            + perl.Sha1Checksum
                            + "\n     got: " + hash);
                        stream.Dispose();
                        Console.Write("Whould you like berrybrew to delete the corrupted download file? y/n [n]");
                        if (Console.ReadLine() == "y")
                        {
                            string retval = FileRemove(archivePath);
                            if (retval == "True")
                            {
                                Console.WriteLine("Deleted! Try to install it again!");
                            }
                            else
                            {
                                Console.WriteLine("Unable to delete " + archivePath);
                            }
                        }
                        Environment.Exit(0);
                    }
                }
            }
            return archivePath;
        }

        internal static string FileRemove(string filename)
        {
            try
            {
                File.Delete(filename);
            }
            catch (Exception ex)
            {
                return ex.ToString();
            }
            return true.ToString();
        }

        internal void FilesystemResetAttributes(string currentDir)
        {
           if (Directory.Exists(currentDir))
           {
               string[] subDirs = Directory.GetDirectories(currentDir);
               foreach(string dir in subDirs)
               FilesystemResetAttributes(dir);
               string[] files = Directory.GetFiles(currentDir);
               foreach (string file in files)
               File.SetAttributes(file, FileAttributes.Normal);
           }
        }

        public string Install(string version)
        {
            StrawberryPerl perl = PerlResolveVersion(version);
            string archive_path = Fetch(perl);
            Extract(perl, archive_path);
            Available();
            return perl.Name;
        }

        internal dynamic JsonParse(string type, bool raw=false)
        {
            string filename = String.Format("{0}.json", type);
            string jsonFile = this.confPath + filename; 

            try
            {
                using (StreamReader r = new StreamReader(jsonFile))
                {
                    string jsonData = r.ReadToEnd();
                    
                    if (raw)
                        return jsonData;

                    try
                    {
                        dynamic json= JsonConvert.DeserializeObject(jsonData);
                        return json;
                    }
                    catch (JsonReaderException error)
                    {
                        Console.WriteLine("\n{0} file is malformed. See berrybrew_error.txt in this directory for details.", jsonFile);
                        using (System.IO.StreamWriter file = new System.IO.StreamWriter(@"berrybrew_error.txt", true))
                        {
                            file.WriteLine(error);
                        }
                        Environment.Exit(0);
                    }
                }
            }
            catch (System.IO.FileNotFoundException err)
            {
                Console.WriteLine("\n{0} file can not be found in {1}", filename, jsonFile);

                if (Debug)
                    Console.WriteLine(err);

                Environment.Exit(0);
            }
            return "";
        }

        internal void JsonWrite(string type, List<Dictionary<string, object>> data, bool fullList=false)
        {
            string jsonString = null;

            if (!fullList)
            {
                dynamic customPerlList = JsonParse("perls_custom", true);
                var perlList = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(customPerlList);

                foreach (Dictionary<string, object> perl in data)
                    perlList.Add(perl);

                jsonString = Newtonsoft.Json.JsonConvert.SerializeObject(perlList);
            }
            else
                jsonString = Newtonsoft.Json.JsonConvert.SerializeObject(data);

            string writeFile = this.confPath + type;
            writeFile = writeFile + @".json";

            System.IO.File.WriteAllText(writeFile, jsonString);
        }

        public void Off()
        {
            PathRemovePerl();
            Console.Write("berrybrew perl disabled. Open a new shell to use system perl\n");
        }

        internal void PathAddBerryBrew(string binPath)
        {
            string path = PathGet();
            List<string> newPath = new List<string>();

            if (path == null)
                newPath.Add(binPath);
            else
            {
                if (path[path.Length - 1] == ';')
                    path = path.Substring(0, path.Length - 1);

                newPath.Add(path);
                newPath.Add(binPath);
            }
            PathSet(newPath);
        }

        internal void PathAddPerl(StrawberryPerl perl)
        {
            string path = PathGet();
            List<string> newPath = perl.Paths;
            newPath.Add(path);
            PathSet(newPath);
        }

        internal static string PathGet()
        {
            string keyName = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment\";
            string path = (string)Registry.LocalMachine.OpenSubKey(keyName).GetValue(
                "PATH",
                "",
                RegistryValueOptions.DoNotExpandEnvironmentNames
            );
            return path;
        }

        internal void PathRemoveBerrybrew()
        {
            string path = PathGet();
            Regex binPath = new Regex("berrybrew.bin");
            List<string> paths = path.Split(';').ToList();
            List<string> updatedPaths = new List<string>();

            foreach (string pathEntry in paths)
            {
                if (!binPath.Match(pathEntry).Success)
                    updatedPaths.Add(pathEntry);
            }
            PathSet(updatedPaths);
        }

        internal string PathRemovePerl(bool process=true)
        {
            string path = PathGet();
            List<String> paths = new List<String>();

            if (path != null)
            {
                paths = path.Split(';').ToList();

                foreach (StrawberryPerl perl in Perls.Values)
                {
                    for (var i = 0; i < paths.Count; i++)
                    {
                        if (paths[i] == perl.PerlPath
                            || paths[i] == perl.CPath
                            || paths[i] == perl.PerlSitePath)
                        {
                            paths[i] = "";
                        }
                    }
                }

                paths.RemoveAll(str => String.IsNullOrEmpty(str));
                
                if (process)
                    PathSet(paths);
            }

            return String.Join(";", paths);
        }

        internal static bool PathScan(Regex binPattern, string target)
        {
            EnvironmentVariableTarget envTarget = new EnvironmentVariableTarget();

            if (target == "machine")
                envTarget = EnvironmentVariableTarget.Machine;
            else 
                envTarget = EnvironmentVariableTarget.User;

            string paths = Environment.GetEnvironmentVariable("path", envTarget);

            if (paths != null)
            {
                foreach (string path in paths.Split(';'))
                {
                    if (binPattern.Match(path).Success)
                        return true;
                }
            }
            return false;
        }

        internal void PathSet(List<string> path)
        {
            path.RemoveAll(str => String.IsNullOrEmpty(str));

            try
            {
                string keyName = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
                Registry.LocalMachine.CreateSubKey(keyName).SetValue(
                    "Path", 
                    String.Join(";", path), 
                    RegistryValueKind.ExpandString
                );
                
                SendMessageTimeout(
                    HWND_BROADCAST, 
                    WM_SETTINGCHANGE, 
                    IntPtr.Zero, 
                    "Environment", 
                    SMTO_ABORTIFHUNG, 
                    100, 
                    IntPtr.Zero
                );
            }
            catch(System.UnauthorizedAccessException err)
            {
                Console.WriteLine("\nAdding berrybrew to the PATH requires Administrator privilege");
                if (Debug)
                    Console.WriteLine(err);
            }
        }

        internal static string PerlArchivePath(StrawberryPerl perl)
        {
            string path;

            try
            {
                if (!Directory.Exists(perl.ArchivePath))
                    Directory.CreateDirectory(perl.ArchivePath);

                return perl.ArchivePath + @"\" + perl.File;
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine("Error, do not have permissions to create directory: " + perl.ArchivePath);
            }

            Console.WriteLine("Creating temporary directory instead");
            do
            {
                path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            } while (Directory.Exists(path));
 
            Directory.CreateDirectory(path);

            return path + @"\" + perl.File;
        }

        public List<string> PerlFindOrphans()
        {
            List<StrawberryPerl> perls = PerlsInstalled();

            string[] dirs = Directory.GetDirectories(this.rootPath);

            List<string> perlInstallations = new List<string>();

            foreach (StrawberryPerl perl in perls)
            {
                perlInstallations.Add(perl.InstallPath);
            }

            List<string> orphans = new List<string>();

            foreach (string dir in dirs)
            {
                if (dir == this.archivePath)
                    continue;

                if (!perlInstallations.Contains(dir) && ! Regex.Match(dir, @".cpanm").Success)
                {
                    string dirBaseName= dir.Remove(0, this.rootPath.Length);
                    orphans.Add(dirBaseName);
                }
            }
            return orphans;
        }

        internal List<StrawberryPerl> PerlGenerateObjects(bool importIntoObject=false)
        {
            List<StrawberryPerl> perls = new List<StrawberryPerl>();
            var Perls = JsonParse("perls");
            var customPerls = JsonParse("perls_custom");

            foreach (var perl in Perls)
            {
                perls.Add(
                    new StrawberryPerl(
                        this,
                        perl.name,
                        perl.file,
                        perl.url,
                        perl.ver,
                        perl.csum,
                        false // custom
                    )
                );
            }

            foreach (var perl in customPerls)
            {
                perls.Add(
                    new StrawberryPerl(
                        this,
                        perl.name,
                        perl.file,
                        perl.url,
                        perl.ver,
                        perl.csum,
                        true // custom
                    )
                );
            }
            if (importIntoObject)
            {
                foreach (StrawberryPerl perl in perls)
                {
                    this.Perls.Add(perl.Name, perl);
                }
            }
            return perls;
        }

        internal StrawberryPerl PerlInUse()
        {
            string path = PathGet();
            StrawberryPerl currentPerl = new StrawberryPerl();

            if (path != null)
            {
                string[] paths = path.Split(';');
                foreach (StrawberryPerl perl in Perls.Values)
                {
                    for (int i = 0; i < paths.Length; i++)
                    {
                        if (paths[i] == perl.PerlPath
                            || paths[i] == perl.CPath
                            || paths[i] == perl.PerlSitePath)
                        {
                            currentPerl = perl;
                            break;
                        }
                    }
                }
            }
            return currentPerl;
        }

        internal static bool PerlIsInstalled(StrawberryPerl perl)
        {
            if (Directory.Exists(perl.InstallPath)
                && File.Exists(perl.PerlPath + @"\perl.exe"))
            {
                return true;
            }
            return false;
        }
        
        internal List<StrawberryPerl> PerlsInstalled()
        {
            List<StrawberryPerl> PerlsInstalled = new List<StrawberryPerl>();

            foreach (StrawberryPerl perl in Perls.Values)
            {
                if (PerlIsInstalled(perl))
                    PerlsInstalled.Add(perl);
            }
            return PerlsInstalled;
        }

        public void PerlRemove(string perlVersionToRemove)
        {
            try
            {
                StrawberryPerl perl = PerlResolveVersion(perlVersionToRemove);
                StrawberryPerl currentPerl = PerlInUse();

                if (perl.Name == currentPerl.Name)
                {
                    Console.WriteLine("Removing Perl " + perlVersionToRemove + " from PATH");
                    PathRemovePerl();
                }

                if (Directory.Exists(perl.InstallPath))
                {
                    try
                    {
                        FilesystemResetAttributes(perl.InstallPath);
                        Directory.Delete(perl.InstallPath, true);
                        Console.WriteLine("Successfully removed Strawberry Perl " + perlVersionToRemove);
                    }
                    catch (System.IO.IOException err)
                    {
                        Console.WriteLine("Unable to completely remove Strawberry Perl " + perlVersionToRemove + " some files may remain");

                        if (Debug)
                            Console.WriteLine(err);
                    }
                }
                else
                {
                    Console.WriteLine("Strawberry Perl " + perlVersionToRemove + " not found (are you sure it's installed?");
                    Environment.Exit(0);
                }

                if (perl.Custom)
                {
                    dynamic customPerlList = JsonParse("perls_custom", true);
                    customPerlList = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(customPerlList);

                    List<Dictionary<string, object>> updatedPerls = new List<Dictionary<string, object>>();

                    foreach (Dictionary<string, object> perlStruct in customPerlList)
                    {
                        if (!perlVersionToRemove.Equals(perlStruct["name"].ToString()))
                            updatedPerls.Add(perlStruct);
                    }
                    JsonWrite("perls_custom", updatedPerls, true);
                }
            }
            catch (ArgumentException err)
            {
                Message.Print("perl_unknown_version");
                
                if (Debug)
                    Console.WriteLine(err);

                Environment.Exit(0);
            }
            catch (UnauthorizedAccessException err)
            {
                Console.WriteLine("Unable to remove Strawberry Perl " + perlVersionToRemove + " permission was denied by System");

                if (Debug)
                    Console.WriteLine(err);
            }
        }

        internal void PerlRegisterCustomInstall(string perlName, StrawberryPerl perlBase=new StrawberryPerl())
        {
            Dictionary<string, object> data = new Dictionary<string, object>();
           
            data["name"] = perlName;
            data["custom"] = perlBase.Custom;
            data["file"] = perlBase.File;
            data["url"] = perlBase.Url;
            data["ver"] = perlBase.Version;
            data["csum"] = perlBase.Sha1Checksum;

            List<Dictionary<string, object>> perlList = new List<Dictionary<string, object>>();
            perlList.Add(data);
            JsonWrite("perls_custom", perlList);

            this.bypassOrphanCheck = true;
        }

        internal StrawberryPerl PerlResolveVersion(string version)
        {
            foreach (StrawberryPerl perl in Perls.Values)
            {
                if (perl.Name == version)
                    return perl;
            }
            throw new ArgumentException("Unknown version: " + version);
        }

        public void PerlUpdateAvailableList()
        {
            //FIXME: incomplete for now. do not use

            using (WebClient client = new WebClient())
            {
                string page = client.DownloadString(this.downloadURL);
                string[] content = page.Split('\n');

                OrderedDictionary strawberryPerls = new OrderedDictionary();

                int i = 0;

                foreach (string line in content)
                {
                    if (line.Contains("no64") || line.Contains("-ld-") || line.Contains("PDL"))
                    {
                        i++;
                        continue;
                    }

                    Match lMatch = Regex.Match(line, @"a href=""(.*?(portable|PDL).zip)""");

                    if (lMatch.Success)
                    {
                        string link = this.strawberryURL + lMatch.Groups[1].Value;

                        Match cMatch = Regex.Match(content[i + 1], @">(\w{40})<");
                        if (cMatch.Success)
                        {
                            strawberryPerls.Add(link, cMatch.Groups[1].Value);
                        }
                    }
                    i++;
                }

                //OrderedDictionary perlDetails = new OrderedDictionary();

                foreach (string link in strawberryPerls.Keys)
                {
                    Match match = Regex.Match(link, @".*/download/.*?/.*(5.*)-portable.zip");
                    if (match.Success)
                    {
                        string verLabel = match.Groups[2].Value;
                        Match extract = Regex.Match(verLabel, @"(5.\d+.\d+).*-(\d{2}bit)");
                        string ver = extract.Groups[1].Value;

                        Console.WriteLine(verLabel);
                    }
                }
            }
        }

        public void Switch(string switchToVersion)
        {
            try
            {
                StrawberryPerl perl = PerlResolveVersion(switchToVersion);

                if (!PerlIsInstalled(perl))
                {
                    Console.WriteLine("Perl version " + perl.Name + " is not installed. Run the command:\n\n\tberrybrew install " + perl.Name);
                    Environment.Exit(0);
                }

                PathRemovePerl();
                PathAddPerl(perl);

                Console.WriteLine("Switched to " + switchToVersion + ", start a new terminal to use it.");
            }
            catch (ArgumentException)
            {
                Message.Print("perl_unknown_version");
                Environment.Exit(0);
            }
        }

        public void Unconfig()
        {
            PathRemoveBerrybrew();
            Message.Print("unconfig");
        }

        public string Version()
        {
            return @"1.10";
        }

        internal Process ProcessCreate(string cmd, bool hidden=true)
        {
            Process process = new Process();
            ProcessStartInfo startInfo = new ProcessStartInfo();

            if (hidden)
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;

            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = "/c " + cmd;
            process.StartInfo = startInfo;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            return process;
        }

        public void Upgrade()
        {
            TimeSpan span = DateTime.Now.Subtract(new DateTime(1970, 1, 1, 0, 0, 0));
            string backupDir = this.installPath + @"/backup_" + span.TotalSeconds;
            Directory.CreateDirectory(backupDir);

            if (System.IO.Directory.Exists(this.confPath))
            {
                string[] files = System.IO.Directory.GetFiles(this.confPath);
                foreach (string s in files)
                {
                    string fileName = System.IO.Path.GetFileName(s);
                    string destFile = System.IO.Path.Combine(backupDir, fileName);
                    System.IO.File.Copy(s, destFile, true);
                }
            }

            string cmd = "cd " + this.installPath + " && git pull";
            Process proc = ProcessCreate(cmd);
            proc.Start();

            while (!proc.StandardOutput.EndOfStream)
            {
                string line = proc.StandardOutput.ReadLine();
                if (Regex.Match(line, @"up-to-date").Success)
                {
                    Console.WriteLine("\nberrybrew is already up to date\n");
                    Environment.Exit(0);
                }
            }

            bool error = false;
            List<string> errorReport = new List<string>();

            while (!proc.StandardError.EndOfStream)
            {
                error = true;
                string line = proc.StandardError.ReadLine();
                errorReport.Add(line);
            }

            if (error)
            {
                Console.WriteLine("\n\nError upgrading berrybrew:\n");
                foreach (string line in errorReport)
                {
                    Console.WriteLine(line);
                }
                Environment.Exit(0);
            }

            string[] bakFiles = System.IO.Directory.GetFiles(backupDir);
            foreach (string s in bakFiles)
            {
                string fileName = System.IO.Path.GetFileName(s);
                string destFile = System.IO.Path.Combine(this.confPath, fileName);
                System.IO.File.Copy(s, destFile, true);
            }

            Console.WriteLine("\nSuccessfully upgraded berrybrew\n");
        }
    }

    public class Message
    {
        public OrderedDictionary msgMap = new OrderedDictionary();

        public string Get(string label)
        {
            return this.msgMap[label].ToString();
        }

        public void Add(dynamic json)
        {
            string content = null;

            foreach (string line in json.content)
            {
                content += String.Format("{0}\n", line);
            }
            this.msgMap.Add(json.label.ToString(), content);
        }
        
        public void Print(string label)
        {
            string msg = this.Get(label);
            Console.WriteLine(msg);
        }

        public void Say(string label)
        {
            string msg = this.Get(label);
            Console.WriteLine(msg);
            Environment.Exit(0);
        }
    }

    public struct StrawberryPerl
    {
        public string Name;
        public string File;
        public bool Custom;
        public string Url;
        public string Version;
        public string ArchivePath;
        public string InstallPath;
        public string CPath;
        public string PerlPath;
        public string PerlSitePath;
        public List<String> Paths;
        public string Sha1Checksum;

        public StrawberryPerl(Berrybrew BB, object name, object file, object url, object version, object csum, bool custom)
        {
            this.Name = name.ToString();
            this.Custom = custom;
            this.File = file.ToString();
            this.Url = url.ToString();
            this.Version = version.ToString();
            this.ArchivePath = BB.archivePath;
            this.InstallPath =  BB.rootPath + name;
            this.CPath = BB.rootPath + name + @"\c\bin";
            this.PerlPath = BB.rootPath + name + @"\perl\bin";
            this.PerlSitePath = BB.rootPath + name + @"\perl\site\bin";
            this.Paths = new List <String>{
                this.CPath, this.PerlPath, this.PerlSitePath
            };
            this.Sha1Checksum = csum.ToString();
        }
    }
}
