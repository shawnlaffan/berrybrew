using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.Zip;
using Newtonsoft.Json;

namespace Berrybrew
{
    public class Berrybrew
    {
        internal static void AddBinToPath(string bin_path)
        {
            // get user PATH and remove trailing semicolon if exists
            string path = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
            string[] new_path;

            if (path == null)
            {
                new_path = new string[] { bin_path };
            }

            else
            {
                if (path[path.Length - 1] == ';')
                    path = path.Substring(0, path.Length - 1);

                new_path = new string[] { path, bin_path };
            }
            Environment.SetEnvironmentVariable("PATH", String.Join(";", new_path), EnvironmentVariableTarget.User);
        }

        internal static void AddPerlToPath(StrawberryPerl perl)
        {
            string path = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);

            List<string> new_path = perl.Paths;
            new_path.Add(path);

            Environment.SetEnvironmentVariable("PATH", String.Join(";", new_path), EnvironmentVariableTarget.Machine);
        }

        internal static void Available()
        {
            List<StrawberryPerl> perls = GatherPerls();
            string available_header = Messages("available_header");
            Console.WriteLine(available_header);

            StrawberryPerl current_perl = CheckWhichPerlInPath();
            string column_spaces = "               ";

            foreach (StrawberryPerl perl in perls)
            {
                // cheap printf
                string name_to_print = perl.Name + column_spaces.Substring(0, column_spaces.Length - perl.Name.Length);

                Console.Write("\t" + name_to_print);

                if (PerlInstalled(perl))
                    Console.Write(" [installed]");

                if (perl.Name == current_perl.Name)
                    Console.Write("*");

                Console.Write("\n");
            }
            string available_footer = Messages("available_footer");
            Console.WriteLine(available_footer);
        }

        internal static StrawberryPerl CheckWhichPerlInPath()
        {
            // get user PATH and remove trailing semicolon if exists
            string path = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);

            StrawberryPerl current_perl = new StrawberryPerl();

            if (path != null)
            {
                string[] paths = path.Split(';');
                foreach (StrawberryPerl perl in GatherPerls())
                {
                    for (int i = 0; i < paths.Length; i++)
                    {
                        if (paths[i] == perl.PerlPath
                            || paths[i] == perl.CPath
                            || paths[i] == perl.PerlSitePath)
                        {
                            current_perl = perl;
                            break;
                        }
                    }
                }
            }
            return current_perl;
        }

        internal static void CompileExec(string parameters)
        {
            List<StrawberryPerl> perls_installed = GetInstalledPerls();
            List<StrawberryPerl> exec_with = new List<StrawberryPerl>();
            string command;

            if (parameters.StartsWith("--with"))
            {
                string param_list = Regex.Replace(parameters, @"--with\s+", "");

                string perl_str = param_list.Split(new[] { ' ' }, 2)[0];
                command = param_list.Split(new[] { ' ' }, 2)[1];

                string[] perls = perl_str.Split(',');

                foreach (StrawberryPerl perl in perls_installed)
                {
                    foreach (string perl_name in perls)
                    {
                        if (perl_name.Equals(perl.Name))
                            exec_with.Add(perl);
                    }
                }
            }
            else
            {
                command = parameters;
                exec_with = perls_installed;
            }

            // get the current PATH, as we'll need it in Exec() to update
            // sub shells

            string sys_path = System.Environment.GetEnvironmentVariable("PATH");

            foreach (StrawberryPerl perl in exec_with)
            {
                Exec(perl, command, sys_path);
            }
        }

        internal static void Config()
        {
            string config_intro = Messages("config_intro");
            Console.WriteLine(config_intro + Version() + "\n");

            if (!ScanUserPath(new Regex("berrybrew.bin"))
                   && !ScanSystemPath(new Regex("berrybrew.bin")))
            {
                string add_bb_to_path = Messages("add_bb_to_path");
                Console.Write(add_bb_to_path);

                if (Console.ReadLine() == "y")
                {
                    //get the full path of the assembly
                    string assembly_path = Assembly.GetExecutingAssembly().Location;

                    //get the parent directory
                    string assembly_directory = Path.GetDirectoryName(assembly_path);

                    AddBinToPath(assembly_directory);

                    if (ScanUserPath(new Regex("berrybrew.bin")))
                    {
                        string config_success = Messages("config_success");
                        Console.WriteLine(config_success);
                    }
                    else
                    {
                        string config_failure = Messages("config_failure");
                        Console.WriteLine(config_failure);
                    }
                }
            }
            else
            {
                string config_complete = Messages("config_complete");
                Console.Write(config_complete);
            }
        }
       
        internal static void Exec(StrawberryPerl perl, string command, string SysPath)
        {

            Console.WriteLine("Perl-" + perl.Name + "\n==============");

            System.Diagnostics.Process process = new System.Diagnostics.Process();
            System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo();
            startInfo.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;

            List<String> NewPath;
            NewPath = perl.Paths;
            NewPath.Add(SysPath);

            System.Environment.SetEnvironmentVariable("PATH", String.Join(";", NewPath));

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

        internal static void Extract(StrawberryPerl perl, string archive_path)
        {
            if (File.Exists(archive_path))
            {
                Console.WriteLine("Extracting " + archive_path);
                ExtractZip(archive_path, perl.InstallPath);
            }
        }

        internal static void ExtractZip(string archive_path, string outFolder)
        {
            ZipFile zf = null;
            try
            {
                FileStream fs = File.OpenRead(archive_path);
                zf = new ZipFile(fs);
                foreach (ZipEntry zipEntry in zf)
                {
                    if (!zipEntry.IsFile)
                    {
                        continue;           // Ignore directories
                    }
                    String entryFileName = zipEntry.Name;
                    // to remove the folder from the entry:- entryFileName = Path.GetFileName(entryFileName);
                    // Optionally match entrynames against a selection list here to skip as desired.
                    // The unpacked length is available in the zipEntry.Size property.

                    byte[] buffer = new byte[4096];     // 4K is optimum
                    Stream zipStream = zf.GetInputStream(zipEntry);

                    // Manipulate the output filename here as desired.
                    String fullZipToPath = Path.Combine(outFolder, entryFileName);
                    string directoryName = Path.GetDirectoryName(fullZipToPath);
                    if (directoryName.Length > 0)
                        Directory.CreateDirectory(directoryName);

                    // Unzip file in buffered chunks. This is just as fast as unpacking to a buffer the full size
                    // of the file, but does not waste memory.
                    // The "using" will close the stream even if an exception occurs.
                    using (FileStream streamWriter = File.Create(fullZipToPath))
                    {
                        StreamUtils.Copy(zipStream, streamWriter, buffer);
                    }
                }
            }
            finally
            {
                if (zf != null)
                {
                    zf.IsStreamOwner = true; // Makes close also shut the underlying stream
                    zf.Close(); // Ensure we release resources
                }
            }
        }

        internal static string Fetch(StrawberryPerl perl)
        {
            WebClient webClient = new WebClient();
            string archive_path = GetDownloadPath(perl);

            // Download if archive doesn't already exist
            if (!File.Exists(archive_path))
            {
                Console.WriteLine("Downloading " + perl.Url + " to " + archive_path);
                webClient.DownloadFile(perl.Url, archive_path);
            }

            Console.WriteLine("Confirming checksum ...");
            using (var cryptoProvider = new SHA1CryptoServiceProvider())
            {
                using (var stream = File.OpenRead(archive_path))
                {
                    string hash = BitConverter.ToString(cryptoProvider.ComputeHash(stream)).Replace("-", "").ToLower();

                    if (perl.Sha1Checksum != hash)
                    {
                        Console.WriteLine("Error checksum of downloaded archive \n"
                            + archive_path
                            + "\ndoes not match expected output\nexpected: "
                            + perl.Sha1Checksum
                            + "\n     got: " + hash);
                        stream.Dispose();
                        Console.Write("Whould you like berrybrew to delete the corrupted download file? y/n [n]");
                        if (Console.ReadLine() == "y")
                        {
                            string retval = RemoveFile(archive_path);
                            if (retval == "True")
                            {
                                Console.WriteLine("Deleted! Try to install it again!");
                            }
                            else
                            {
                                Console.WriteLine("Unable to delete " + archive_path);
                            }
                        }
                        Environment.Exit(0);
                    }
                }
            }
            return archive_path;
        }

        internal static string Messages(string name)
        {
            List<Message> messages = new List<Message>();

            string assembly_path = Assembly.GetExecutingAssembly().Location;
            string assembly_directory = Path.GetDirectoryName(assembly_path);

            string json_path = String.Format("{0}/data/messages.json", assembly_directory);
            string json_file = Regex.Replace(json_path, @"bin", "");

            using (StreamReader r = new StreamReader(json_file))
            {
                string json = r.ReadToEnd();
                dynamic msgs = JsonConvert.DeserializeObject(json);

                foreach (var msg in msgs)
                {
                    string content = null;

                    foreach (string content_line in msg.content)
                    {
                        content += String.Format("{0}\n", content_line);
                    }

                    messages.Add(
                        new Message(
                            msg.label,
                            content
                        )
                    );
                }
            }

            foreach (Message msg in messages)
            {
                if (msg.Label == name)
                {
                    return msg.Content;
                }
            }
            return "";
        }

        internal static List<StrawberryPerl> GatherPerls()
        {
            List<StrawberryPerl> perls = new List<StrawberryPerl>();

            //get the full path of the assembly
            string assembly_path = Assembly.GetExecutingAssembly().Location;

            //get the parent directory
            string assembly_directory = Path.GetDirectoryName(assembly_path);

            string json_path = String.Format("{0}/data/perls.json", assembly_directory);
            string json_file = Regex.Replace(json_path, @"bin", "");

            using (StreamReader r = new StreamReader(json_file))
            {
                string json = r.ReadToEnd();
                dynamic list = JsonConvert.DeserializeObject(json);
                foreach (var version in list)
                {
                    //Console.WriteLine("{0} {1}", version.file, version.ver);
                    perls.Add(
                        new StrawberryPerl(
                            version.name,
                            version.file,
                            version.url,
                            version.ver,
                            version.csum
                        )
                    );
                }
            }

            return perls;
        }

        internal static string GetDownloadPath(StrawberryPerl perl)
        {
            string path;

            try
            {
                if (!Directory.Exists(perl.ArchivePath))
                    Directory.CreateDirectory(perl.ArchivePath);

                return perl.ArchivePath + @"\" + perl.ArchiveName;
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

            return path + @"\" + perl.ArchiveName;

        }

        internal static List<StrawberryPerl> GetInstalledPerls()
        {
            List<StrawberryPerl> perls = GatherPerls();
            List<StrawberryPerl> PerlsInstalled = new List<StrawberryPerl>();

            foreach (StrawberryPerl perl in perls)
            {
                if (PerlInstalled(perl))
                    PerlsInstalled.Add(perl);
            }
            return PerlsInstalled;
        }

        static void Main(string[] args)
        {
            //Console.Write("{0}", Messages("help"));
            
            if (args.Length == 0)
            {
                PrintHelp();
                Environment.Exit(0);
            }

            switch (args[0])
            {
                case "version":
                    PrintVersion();
                    break;

                case "install":
                    if (args.Length == 1)
                    {
                        string install_ver_required = Messages("install_ver_required");
                        Console.WriteLine(install_ver_required);
                        Environment.Exit(0);
                    }
                    try
                    {
                        StrawberryPerl perl = ResolveVersion(args[1]);
                        string archive_path = Fetch(perl);
                        Extract(perl, archive_path);
                        Available();
                    }
                    catch (ArgumentException)
                    {
                        string install_ver_unknown = Messages("install_ver_unknown");
                        Console.WriteLine(install_ver_unknown);
                        Environment.Exit(0);
                    }
                    break;

                case "off":
                    Off();
                    break;

                case "switch":
                    if (args.Length == 1)
                    {
                        string switch_ver_required = Messages("switch_ver_required");
                        Console.WriteLine(switch_ver_required);
                        Environment.Exit(0);
                    }
                    Switch(args[1]);
                    break;

                case "available":
                    Available();
                    break;

                case "config":
                    Config();
                    break;

                case "remove":
                    if (args.Length == 1)
                    {
                        string remove_ver_required = Messages("remove_ver_required");
                        Console.WriteLine(remove_ver_required);
                        Environment.Exit(0);
                    }
                    RemovePerl(args[1]);
                    break;

                case "exec":
                    if (args.Length == 1)
                    {
                        string exec_command_required = Messages("exec_command_required");
                        Console.WriteLine(exec_command_required);
                        Environment.Exit(0);
                    }
                    args[0] = "";
                    CompileExec(String.Join(" ", args).Trim());
                    break;

                case "license":
                    if (args.Length == 1)
                    {
                        PrintLicense();
                        Environment.Exit(0);
                    }
                    break;

                default:
                    PrintHelp();
                    break;
            }
        }

        internal static void Off()
        {
            RemovePerlFromPath();
            Console.Write("berrybrew perl disabled. Open a new shell to use system perl\n");

        }

        internal static bool PerlInstalled(StrawberryPerl perl)
        {
            if (Directory.Exists(perl.InstallPath)
                && File.Exists(perl.PerlPath + @"\perl.exe"))
            {
                return true;
            }
            return false;
        }

        internal static void PrintHelp()
        {
            string help = Messages("help");
            Console.WriteLine(help);
        }

        internal static void PrintLicense()
        {
            string license = Messages("license");
            Console.WriteLine(license);
        }

        internal static void PrintVersion()
        {
            string version = Version();
            Console.Write("\n{0}", version);
        }

        internal static string RemoveFile(string filename)
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

        internal static void RemovePerl(string version_to_remove)
        {
            try
            {
                StrawberryPerl perl = ResolveVersion(version_to_remove);

                StrawberryPerl current_perl = CheckWhichPerlInPath();

                if (perl.Name == current_perl.Name)
                {
                    Console.WriteLine("Removing Perl " + version_to_remove + " from PATH");
                    RemovePerlFromPath();
                }

                if (Directory.Exists(perl.InstallPath))
                {
                    try
                    {
                        Directory.Delete(perl.InstallPath, true);
                        Console.WriteLine("Successfully removed Strawberry Perl " + version_to_remove);
                    }
                    catch (System.IO.IOException)
                    {
                        Console.WriteLine("Unable to completely remove Strawberry Perl " + version_to_remove + " some files may remain");
                    }
                }
                else
                {
                    Console.WriteLine("Strawberry Perl " + version_to_remove + " not found (are you sure it's installed?");
                    Environment.Exit(0);
                }
            }
            catch (ArgumentException)
            {
                string perl_unknown_version = Messages("perl_unknown_version");
                Console.WriteLine(perl_unknown_version);
                Environment.Exit(0);
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine("Unable to remove Strawberry Perl " + version_to_remove + " permission was denied by System");
            }
        }

        internal static void RemovePerlFromPath()
        {
            // get user PATH and remove trailing semicolon if exists
            string path = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);

            if (path != null)
            {
                string[] paths = path.Split(';');
                foreach (StrawberryPerl perl in GatherPerls())
                {
                    for (int i = 0; i < paths.Length; i++)
                    {
                        if (paths[i] == perl.PerlPath
                            || paths[i] == perl.CPath
                            || paths[i] == perl.PerlSitePath)
                        {
                            paths[i] = "";
                        }
                    }
                }

                // Update user path and parse out unnecessary semicolons
                string new_path = String.Join(";", paths);
                Regex multi_semicolon = new Regex(";{2,}");
                new_path = multi_semicolon.Replace(new_path, ";");
                Regex lead_semicolon = new Regex("^;");
                new_path = lead_semicolon.Replace(new_path, "");
                Environment.SetEnvironmentVariable("Path", new_path, EnvironmentVariableTarget.Machine);
            }
        }

        internal static StrawberryPerl ResolveVersion(string version_to_resolve)
        {
            foreach (StrawberryPerl perl in GatherPerls())
            {
                if (perl.Name == version_to_resolve)
                    return perl;
            }
            throw new ArgumentException("Unknown version: " + version_to_resolve);
        }

        internal static bool ScanSystemPath(Regex bin_pattern)
        {
            string system_path = Environment.GetEnvironmentVariable("path", EnvironmentVariableTarget.Machine);

            if (system_path != null)
            {
                foreach (string sys_p in system_path.Split(';'))
                {
                    if (bin_pattern.Match(sys_p).Success)
                        return true;
                }
            }
            return false;
        }

        internal static bool ScanUserPath(Regex bin_pattern)
        {
            string user_path = Environment.GetEnvironmentVariable("path", EnvironmentVariableTarget.User);
            if (user_path != null)
            {
                foreach (string user_p in user_path.Split(';'))
                {
                    if (bin_pattern.Match(user_p).Success)
                        return true;
                }
            }
            return false;
        }

        internal static void Switch(string version_to_switch)
        {
            try
            {
                StrawberryPerl perl = ResolveVersion(version_to_switch);

                // if Perl version not installed, can't switch
                if (!PerlInstalled(perl))
                {
                    Console.WriteLine("Perl version " + perl.Name + " is not installed. Run the command:\n\n\tberrybrew install " + perl.Name);
                    Environment.Exit(0);
                }

                RemovePerlFromPath();

                AddPerlToPath(perl);
                Console.WriteLine("Switched to " + version_to_switch + ", start a new terminal to use it.");

                // new terminal not inhereting PATH, so disable opening a new shell

                // string cmd = "cmd.exe";
                // System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo(cmd);
                // startInfo.UseShellExecute = true;
                // Process processChild = Process.Start(startInfo); 

            }
            catch (ArgumentException)
            {
                Console.WriteLine("Unknown version of Perl. Use the available command to see what versions of Strawberry Perl are available");
                Environment.Exit(0);
            }
        }

        internal static string Version()
        {
            string version = Messages("version");
            return version;
        }

    }

    public struct Message
    {
        public string Label;
        public string Content;

        public Message(object label, object content)
        {
            this.Label = label.ToString();
            this.Content = content.ToString();
        }
    }

    public struct StrawberryPerl
    {
        public string Name;
        public string ArchiveName;
        public string Url;
        public string Version;
        public string ArchivePath;
        public string InstallPath;
        public string CPath;
        public string PerlPath;
        public string PerlSitePath;
        public List<String> Paths;
        public string Sha1Checksum;

        public StrawberryPerl(object n, object a, object u, object v, object c)
        {
            this.Name = n.ToString();
            this.ArchiveName = a.ToString();
            this.Url = u.ToString();
            this.Version = v.ToString();
            this.ArchivePath = @"C:\berrybrew\temp";
            this.InstallPath = @"C:\berrybrew\" + n;
            this.CPath = @"C:\berrybrew\" + n + @"\c\bin";
            this.PerlPath = @"C:\berrybrew\" + n + @"\perl\bin";
            this.PerlSitePath = @"C:\berrybrew\" + n + @"\perl\site\bin";
            this.Paths = new List <String>{
                this.CPath, this.PerlPath, this.PerlSitePath
            };
            this.Sha1Checksum = c.ToString();
        }
    }
}
