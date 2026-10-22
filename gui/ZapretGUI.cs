using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace ZapretGUI
{
    // One general*.bat file turned into a launchable strategy.
    class Strategy
    {
        public string FileName;   // e.g. "general (ALT).bat"
        public string Display;    // e.g. "ALT"
        public override string ToString() { return Display; }
    }

    static class Core
    {
        public const string TaskName = "ZapretGUI";

        public static string Root;
        public static string BinDir { get { return Path.Combine(Root, "bin") + "\\"; } }
        public static string ListsDir { get { return Path.Combine(Root, "lists") + "\\"; } }

        // Game filter modes: 0 off, 1 TCP+UDP, 2 TCP, 3 UDP
        public static void GameFilterPorts(int mode, out string tcp, out string udp)
        {
            const string range = "1024-65535";
            tcp = "12"; udp = "12";
            if (mode == 1) { tcp = range; udp = range; }
            else if (mode == 2) { tcp = range; }
            else if (mode == 3) { udp = range; }
        }

        public static string DisplayName(string fileName)
        {
            string n = Path.GetFileNameWithoutExtension(fileName);
            int a = n.IndexOf('(');
            int b = n.LastIndexOf(')');
            if (a >= 0 && b > a) return n.Substring(a + 1, b - a - 1).Trim();
            return "general";
        }

        // Natural sort key: digits are zero-padded, plain "general" first, then ALT*, then others.
        static string SortKey(string fileName)
        {
            string d = DisplayName(fileName);
            int group = d == "general" ? 0 : (Regex.IsMatch(d, @"^ALT\d*$") ? 1 : 2);
            string padded = Regex.Replace(d.ToLowerInvariant(), @"\d+", delegate(Match m) { return m.Value.PadLeft(6, '0'); });
            if (group == 1 && d == "ALT") padded = "alt000001";
            return group + padded;
        }

        public static List<Strategy> LoadStrategies()
        {
            List<Strategy> list = new List<Strategy>();
            try
            {
                string[] files = Directory.GetFiles(Root, "general*.bat");
                Array.Sort(files, delegate(string x, string y)
                {
                    return string.CompareOrdinal(SortKey(Path.GetFileName(x)), SortKey(Path.GetFileName(y)));
                });
                foreach (string f in files)
                {
                    Strategy s = new Strategy();
                    s.FileName = Path.GetFileName(f);
                    s.Display = DisplayName(s.FileName);
                    list.Add(s);
                }
            }
            catch (Exception) { }
            return list;
        }

        // Returns the resolved winws arguments for a bat file, or null if not found.
        public static string ParseBat(string batPath, int gameMode, out string problem)
        {
            problem = null;
            string[] lines = File.ReadAllLines(batPath, Encoding.UTF8);
            int idx = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].IndexOf("winws.exe\"", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    lines[i].TrimStart().StartsWith("start", StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
            }
            if (idx < 0)
            {
                problem = "В файле нет строки запуска winws.exe";
                return null;
            }
            StringBuilder sb = new StringBuilder();
            for (int i = idx; i < lines.Length; i++)
            {
                string l = lines[i].Trim();
                bool cont = l.EndsWith("^");
                if (cont) l = l.Substring(0, l.Length - 1).TrimEnd();
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(l);
                if (!cont) break;
            }
            string full = sb.ToString();
            int p = full.IndexOf("winws.exe\"", StringComparison.OrdinalIgnoreCase);
            string args = full.Substring(p + "winws.exe\"".Length).Trim();

            string tcp, udp;
            GameFilterPorts(gameMode, out tcp, out udp);
            string name = Path.GetFileNameWithoutExtension(batPath);
            args = ReplaceCI(args, "%BIN%", BinDir);
            args = ReplaceCI(args, "%LISTS%", ListsDir);
            args = ReplaceCI(args, "%GameFilterTCP%", tcp);
            args = ReplaceCI(args, "%GameFilterUDP%", udp);
            args = ReplaceCI(args, "%GameFilter%", tcp == udp ? tcp : "12");
            args = ReplaceCI(args, "%~dp0", Root.TrimEnd('\\') + "\\");
            args = ReplaceCI(args, "%~n0", name);

            Match m = Regex.Match(args, @"%[^%\s""]+%");
            if (m.Success) problem = "Неизвестная переменная: " + m.Value;
            return args;
        }

        static string ReplaceCI(string s, string what, string with)
        {
            return Regex.Replace(s, Regex.Escape(what), with.Replace("$", "$$"), RegexOptions.IgnoreCase);
        }

        // Runs a console tool hidden and returns its output; exitCode is set.
        public static string RunTool(string file, string args, out int exitCode)
        {
            exitCode = -1;
            ProcessStartInfo psi = new ProcessStartInfo(file, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd();
                string e = p.StandardError.ReadToEnd();
                p.WaitForExit();
                exitCode = p.ExitCode;
                return (o + e).Trim();
            }
        }

        // ---------- user lists (service.bat :load_user_lists) ----------

        // Content zapret requires in the user lists when they would otherwise be empty.
        public static string UserListPlaceholder(string fileName)
        {
            string nl = Environment.NewLine;
            if (fileName == "ipset-exclude-user.txt") return "203.0.113.113/32" + nl;
            if (fileName == "list-general-user.txt") return "# Never leave this file empty" + nl + "domain.example.abc" + nl;
            if (fileName == "list-exclude-user.txt") return "domain.example.abc" + nl;
            return "";
        }

        public static readonly string[] UserListFiles = { "ipset-exclude-user.txt", "list-general-user.txt", "list-exclude-user.txt" };

        public static void EnsureUserLists()
        {
            Directory.CreateDirectory(ListsDir);
            foreach (string name in UserListFiles)
            {
                string path = Path.Combine(ListsDir, name);
                if (!File.Exists(path)) File.WriteAllText(path, UserListPlaceholder(name));
            }
        }

        // ---------- TCP timestamps (service.bat :tcp_enable) ----------

        public static bool TimestampsEnabled()
        {
            int code;
            string o = RunTool("netsh", "interface tcp show global", out code);
            return Regex.IsMatch(o, @"(?i)(timestamps|RFC\s*1323)[^\r\n:]*:\s*enabled");
        }

        public static bool EnableTimestamps()
        {
            int code;
            RunTool("netsh", "interface tcp set global timestamps=enabled", out code);
            return code == 0;
        }

        // null if the service does not exist, otherwise RUNNING / STOPPED / ...
        public static string ServiceState(string name)
        {
            int code;
            string o = RunTool("sc.exe", "query \"" + name + "\"", out code);
            if (code != 0) return null;
            Match m = Regex.Match(o, @":\s*\d+\s+(RUNNING|STOPPED|STOP_PENDING|START_PENDING|PAUSED|PAUSE_PENDING|CONTINUE_PENDING)");
            return m.Success ? m.Groups[1].Value : "UNKNOWN";
        }

        // ---------- Telegram split tunnel through AmneziaWG ----------

        public const string TgTunnelName = "zapret-tg";
        public const string TgServiceName = "AmneziaWGTunnel$zapret-tg";

        static readonly string[] TgFallbackCidrs =
        {
            "91.108.56.0/22", "91.108.4.0/22", "91.108.8.0/22", "91.108.16.0/22", "91.108.12.0/22",
            "149.154.160.0/20", "91.105.192.0/23", "91.108.20.0/22", "185.76.151.0/24",
            "2001:b28:f23d::/48", "2001:b28:f23f::/48", "2001:67c:4e8::/48", "2001:b28:f23c::/48", "2a0a:f280::/32"
        };

        // Curated Google front-end ranges (cover youtube.com, googlevideo.com, ytimg.com front ends). Static, no network fetch.
        public static readonly string[] YtCidrs =
        {
            "64.233.160.0/19", "66.102.0.0/20", "66.249.64.0/19", "72.14.192.0/18", "74.125.0.0/16",
            "108.177.0.0/17", "142.250.0.0/15", "172.217.0.0/16", "172.253.0.0/16", "173.194.0.0/16",
            "192.178.0.0/15", "209.85.128.0/17", "216.58.192.0/19", "216.239.32.0/19",
            "2001:4860::/32", "2404:6800::/32", "2607:f8b0::/32", "2800:3f0::/32", "2a00:1450::/32", "2c0f:fb50::/32"
        };

        public static string AmneziaExe
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AmneziaWG\\amneziawg.exe"); }
        }

        public static string TgConfPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZapretGUI\\zapret-tg.conf"); }
        }

        // Fresh list from telegram.org; falls back to the built-in one.
        public static string[] FetchTgCidrs(out bool fresh)
        {
            fresh = false;
            try
            {
                string text = Encoding.UTF8.GetString(Download("https://core.telegram.org/resources/cidr.txt", 4000));
                List<string> list = new List<string>();
                foreach (string l in text.Split('\n'))
                {
                    string t = l.Trim();
                    if (Regex.IsMatch(t, @"^[0-9a-fA-F:.]+/\d{1,3}$")) list.Add(t);
                }
                if (list.Count >= 5) { fresh = true; return list.ToArray(); }
            }
            catch (Exception) { }
            return TgFallbackCidrs;
        }

        // Keeps the source config verbatim except: [Peer] AllowedIPs becomes the Telegram CIDRs, DNS lines are dropped.
        public static bool HasEmbeddedTgConfig()
        {
            return System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceInfo("ZapretGUI.warp.conf") != null;
        }

        public static string ReadEmbeddedTgConfig()
        {
            using (Stream st = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("ZapretGUI.warp.conf"))
            {
                if (st == null) return null;
                using (StreamReader r = new StreamReader(st, new UTF8Encoding(false), true))
                    return r.ReadToEnd();
            }
        }

        public static string BuildTgConfig(string source, string[] cidrs)
        {
            string[] lines = source.Replace("\r\n", "\n").Split('\n');
            List<string> res = new List<string>();
            string section = "";
            foreach (string line in lines)
            {
                string t = line.Trim();
                if (t.StartsWith("[")) section = t.ToLowerInvariant();
                if (Regex.IsMatch(t, @"^DNS\s*=", RegexOptions.IgnoreCase)) continue;
                if (section == "[peer]" && Regex.IsMatch(t, @"^AllowedIPs\s*=", RegexOptions.IgnoreCase))
                {
                    res.Add("AllowedIPs = " + string.Join(", ", cidrs));
                    continue;
                }
                res.Add(line);
            }
            return string.Join("\r\n", res.ToArray());
        }

        // Writes the config; the file holds the private key, so it is created with an ACL for SYSTEM and Administrators only.
        public static void WriteTgConfigSecure(string path, string content)
        {
            SecureDataDir();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path)) File.Delete(path);
            System.Security.AccessControl.FileSecurity fs = new System.Security.AccessControl.FileSecurity();
            fs.SetAccessRuleProtection(true, false);
            fs.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            fs.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            using (FileStream f = new FileStream(path, FileMode.CreateNew, System.Security.AccessControl.FileSystemRights.FullControl,
                FileShare.None, 4096, FileOptions.None, fs))
            {
                byte[] b = new UTF8Encoding(false).GetBytes(content);
                f.Write(b, 0, b.Length);
            }
        }

        // ---------- misc helpers ----------

        public static string LocalVersion()
        {
            try
            {
                string bat = Path.Combine(Root, "service.bat");
                if (File.Exists(bat))
                {
                    Match m = Regex.Match(File.ReadAllText(bat), "set \"LOCAL_VERSION=([^\"]+)\"");
                    if (m.Success) return m.Groups[1].Value.Trim();
                }
            }
            catch (Exception) { }
            return "?";
        }

        public const long DefaultMaxDownload = 20L * 1024 * 1024;

        public static byte[] Download(string url, int timeoutMs)
        {
            return Download(url, timeoutMs, DefaultMaxDownload);
        }

        public static byte[] Download(string url, int timeoutMs, long maxBytes)
        {
            return HttpCall("GET", url, timeoutMs, maxBytes, null, null);
        }

        // GET/POST with a size cap. headers: User-Agent, Accept and Content-Type are mapped to the request properties.
        public static byte[] HttpCall(string method, string url, int timeoutMs, long maxBytes, string body, Dictionary<string, string> headers)
        {
            System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072; // TLS 1.2
            System.Net.HttpWebRequest req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            req.Method = method;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "ZapretGUI";
            req.Headers["Cache-Control"] = "no-cache";
            if (headers != null)
                foreach (KeyValuePair<string, string> kv in headers)
                {
                    if (kv.Key == "User-Agent") req.UserAgent = kv.Value;
                    else if (kv.Key == "Accept") req.Accept = kv.Value;
                    else if (kv.Key == "Content-Type") req.ContentType = kv.Value;
                    else req.Headers[kv.Key] = kv.Value;
                }
            if (body != null)
            {
                byte[] data = new UTF8Encoding(false).GetBytes(body);
                req.ContentLength = data.Length;
                using (Stream rs = req.GetRequestStream()) rs.Write(data, 0, data.Length);
            }
            using (System.Net.WebResponse resp = req.GetResponse())
            {
                if (resp.ContentLength > maxBytes) throw new IOException("Ответ слишком большой");
                using (Stream st = resp.GetResponseStream())
                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] buf = new byte[65536];
                    int n;
                    while ((n = st.Read(buf, 0, buf.Length)) > 0)
                    {
                        ms.Write(buf, 0, n);
                        if (ms.Length > maxBytes) throw new IOException("Ответ слишком большой");
                    }
                    return ms.ToArray();
                }
            }
        }

        // Directory readable/writable only by SYSTEM and Administrators (inherited by new files).
        public static string SecureDataDir()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZapretGUI");
            try
            {
                System.Security.AccessControl.DirectorySecurity ds = new System.Security.AccessControl.DirectorySecurity();
                ds.SetAccessRuleProtection(true, false);
                System.Security.AccessControl.InheritanceFlags inh = System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
                ds.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    System.Security.AccessControl.FileSystemRights.FullControl, inh, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
                ds.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    System.Security.AccessControl.FileSystemRights.FullControl, inh, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir, ds);
                else new DirectoryInfo(dir).SetAccessControl(ds);
            }
            catch (Exception) { Directory.CreateDirectory(dir); }
            return dir;
        }

        public static void OpenShell(string file, string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo(file);
            if (args != null) psi.Arguments = args;
            psi.UseShellExecute = true;
            psi.WorkingDirectory = Root;
            Process.Start(psi);
        }

        // Simple key=value settings file.
        public static Dictionary<string, string> LoadIni()
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = Path.Combine(Root, "ZapretGUI.ini");
                if (File.Exists(path))
                    foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                    }
            }
            catch (Exception) { }
            return d;
        }

        public static void SaveIni(Dictionary<string, string> d)
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in d) sb.AppendLine(kv.Key + "=" + kv.Value);
            File.WriteAllText(Path.Combine(Root, "ZapretGUI.ini"), sb.ToString(), new UTF8Encoding(false));
        }
    }

    // ---------- Secure DNS: Cloudflare over HTTPS (DoH) on physical adapters, with exact restore ----------

    class DnsAdapter
    {
        public string Id;      // "{GUID}"
        public string Name;    // display only, never used in commands
        public int V4Index = -1;
        public int V6Index = -1;
    }

    class DnsResult
    {
        public bool Ok;
        public List<string> Info = new List<string>();
        public List<string> Errors = new List<string>();
    }

    static class SecureDns
    {
        const string TemplateUrl = "https://cloudflare-dns.com/dns-query";
        static readonly string[] V4Servers = { "1.1.1.1", "1.0.0.1" };
        static readonly string[] V6Servers = { "2606:4700:4700::1111", "2606:4700:4700::1001" };
        const string Tcpip4 = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";
        const string Tcpip6 = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\";
        const string DnsParams = @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters";
        const string IfaceBase = @"SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters\";
        const string GlobalSection = "global";
        static readonly object Gate = new object();

        static readonly string[] ExcludedWords = { "hyper-v", "virtual", "wireguard", "wintun", "happ", "outline", "amneziawg", "zapret", "vpn", "loopback" };

        static string BackupPath() { return Path.Combine(Core.SecureDataDir(), "dns-backup.ini"); }

        // ---------- backup file: [section] + key=value ----------

        class Backup
        {
            public Dictionary<string, Dictionary<string, string>> Sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            public static Backup Load()
            {
                Backup b = new Backup();
                string path = BackupPath();
                if (!File.Exists(path)) return b;
                Dictionary<string, string> cur = null;
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        b.Sections[line.Substring(1, line.Length - 2)] = cur;
                        continue;
                    }
                    int i = line.IndexOf('=');
                    if (cur != null && i > 0) cur[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
                return b;
            }

            public void Save()
            {
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, Dictionary<string, string>> s in Sections)
                {
                    sb.AppendLine("[" + s.Key + "]");
                    foreach (KeyValuePair<string, string> kv in s.Value) sb.AppendLine(kv.Key + "=" + kv.Value);
                }
                File.WriteAllText(BackupPath(), sb.ToString(), new UTF8Encoding(false));
            }

            public bool HasAdapters()
            {
                foreach (string k in Sections.Keys) if (!k.Equals(GlobalSection, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
        }

        static string Get(Dictionary<string, string> d, string key)
        {
            string v;
            return d.TryGetValue(key, out v) ? v : "";
        }

        static bool ValidGuid(string id)
        {
            return id != null && Regex.IsMatch(id, @"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$");
        }

        // ---------- adapters ----------

        static DnsAdapter ToAdapter(System.Net.NetworkInformation.NetworkInterface ni)
        {
            DnsAdapter a = new DnsAdapter();
            a.Id = ni.Id;
            a.Name = ni.Name;
            try
            {
                System.Net.NetworkInformation.IPInterfaceProperties ip = ni.GetIPProperties();
                if (ni.Supports(System.Net.NetworkInformation.NetworkInterfaceComponent.IPv4))
                {
                    try { a.V4Index = ip.GetIPv4Properties().Index; } catch (Exception) { }
                }
                if (ni.Supports(System.Net.NetworkInformation.NetworkInterfaceComponent.IPv6))
                {
                    try { a.V6Index = ip.GetIPv6Properties().Index; } catch (Exception) { }
                }
            }
            catch (Exception) { }
            return a;
        }

        // Up, Ethernet/Wi-Fi, with a default gateway, not virtual/VPN.
        public static List<DnsAdapter> FindAdapters()
        {
            List<DnsAdapter> r = new List<DnsAdapter>();
            foreach (System.Net.NetworkInformation.NetworkInterface ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    System.Net.NetworkInformation.NetworkInterfaceType t = ni.NetworkInterfaceType;
                    if (t != System.Net.NetworkInformation.NetworkInterfaceType.Ethernet &&
                        t != System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 &&
                        t != System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet) continue;
                    string text = (ni.Name + " " + ni.Description).ToLowerInvariant();
                    bool bad = Regex.IsMatch(text, @"\btap\b");
                    foreach (string w in ExcludedWords) if (text.Contains(w)) bad = true;
                    if (bad) continue;
                    bool gw = false;
                    foreach (System.Net.NetworkInformation.GatewayIPAddressInformation g in ni.GetIPProperties().GatewayAddresses)
                        if (!System.Net.IPAddress.Any.Equals(g.Address) && !System.Net.IPAddress.IPv6Any.Equals(g.Address)) gw = true;
                    if (!gw) continue;
                    if (!ValidGuid(ni.Id)) continue;
                    DnsAdapter a = ToAdapter(ni);
                    if (a.V4Index < 0 && a.V6Index < 0) continue;
                    r.Add(a);
                }
                catch (Exception) { }
            }
            return r;
        }

        // Any adapter (even down) by GUID, for restore.
        static DnsAdapter FindById(string id)
        {
            foreach (System.Net.NetworkInformation.NetworkInterface ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                if (string.Equals(ni.Id, id, StringComparison.OrdinalIgnoreCase)) return ToAdapter(ni);
            return null;
        }

        // ---------- registry helpers ----------

        static string ReadNameServer(string baseKey, string id, out bool keyExists)
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(baseKey + id))
            {
                keyExists = k != null;
                if (k == null) return "";
                string v = k.GetValue("NameServer") as string;
                return v == null ? "" : v.Trim();
            }
        }

        static void WriteNameServer(string baseKey, string id, string value)
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(baseKey + id, true))
            {
                if (k != null) k.SetValue("NameServer", value, Microsoft.Win32.RegistryValueKind.String);
            }
        }

        static string DohPath(string id, bool v6, string server)
        {
            return IfaceBase + id + @"\DohInterfaceSettings\" + (v6 ? "Doh6" : "Doh") + "\\" + server;
        }

        // "-" key absent, "x" key present without DohFlags, otherwise the DohFlags number.
        static string ReadDohState(string path)
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path))
            {
                if (k == null) return "-";
                object v = k.GetValue("DohFlags");
                if (v is int) return ((int)v).ToString();
                return "x";
            }
        }

        static void WriteDohFlags(string path, int flags)
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path))
            {
                k.SetValue("DohFlags", flags, Microsoft.Win32.RegistryValueKind.DWord);
            }
        }

        static void RestoreDohState(string path, string state)
        {
            if (state == "-" || state.Length == 0)
            {
                Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(path, false);
            }
            else if (state == "x")
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path, true))
                {
                    if (k != null) k.DeleteValue("DohFlags", false);
                }
            }
            else
            {
                int n;
                if (int.TryParse(state, out n)) WriteDohFlags(path, n);
            }
        }

        static void DeleteIfEmpty(string path)
        {
            try
            {
                bool empty;
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path))
                {
                    if (k == null) return;
                    empty = k.SubKeyCount == 0 && k.ValueCount == 0;
                }
                if (empty) Microsoft.Win32.Registry.LocalMachine.DeleteSubKey(path, false);
            }
            catch (Exception) { }
        }

        static void CleanDohParents(string id)
        {
            string b = IfaceBase + id + @"\DohInterfaceSettings";
            DeleteIfEmpty(b + @"\Doh");
            DeleteIfEmpty(b + @"\Doh6");
            DeleteIfEmpty(b);
        }

        // ---------- netsh ----------

        static bool Netsh(string args, List<string> errors, string what)
        {
            int code;
            string o = Core.RunTool("netsh.exe", args, out code);
            if (code != 0)
            {
                string first = o;
                int nl = first.IndexOf('\n');
                if (nl > 0) first = first.Substring(0, nl);
                errors.Add(what + ": " + first.Trim() + " (код " + code + ")");
                return false;
            }
            return true;
        }

        static void Flush()
        {
            int code;
            try { Core.RunTool("ipconfig.exe", "/flushdns", out code); } catch (Exception) { }
        }

        static bool ListHas(string text, string ip)
        {
            return Regex.IsMatch(text, @"(?<![0-9A-Fa-f:.])" + Regex.Escape(ip) + @"(?![0-9A-Fa-f:.])", RegexOptions.IgnoreCase);
        }

        static List<string> ParseServers(string ns)
        {
            List<string> r = new List<string>();
            foreach (string p in ns.Split(new char[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                System.Net.IPAddress ip;
                if (System.Net.IPAddress.TryParse(p, out ip)) r.Add(ip.ToString());
            }
            return r;
        }

        // ---------- apply ----------

        static Dictionary<string, string> Snapshot(DnsAdapter a)
        {
            Dictionary<string, string> s = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            s["name"] = a.Name.Replace("\r", " ").Replace("\n", " ");
            bool ex;
            string ns4 = ReadNameServer(Tcpip4, a.Id, out ex);
            s["v4"] = a.V4Index >= 0 ? "1" : "0";
            s["v4static"] = ns4.Length > 0 ? "1" : "0";
            s["v4ns"] = ns4;
            string ns6 = ReadNameServer(Tcpip6, a.Id, out ex);
            s["v6"] = a.V6Index >= 0 ? "1" : "0";
            s["v6static"] = ns6.Length > 0 ? "1" : "0";
            s["v6ns"] = ns6;
            foreach (string sv in V4Servers) s["doh." + sv] = ReadDohState(DohPath(a.Id, false, sv));
            foreach (string sv in V6Servers) s["doh6." + sv] = ReadDohState(DohPath(a.Id, true, sv));
            return s;
        }

        static bool ApplyAdapter(DnsAdapter a, List<string> errors)
        {
            string who = "«" + a.Name + "»";
            if (a.V4Index >= 0)
            {
                string idx = a.V4Index.ToString();
                if (!Netsh("interface ipv4 set dnsservers " + idx + " static " + V4Servers[0] + " primary validate=no", errors, who + " IPv4 DNS")) return false;
                if (!Netsh("interface ipv4 add dnsservers " + idx + " " + V4Servers[1] + " index=2 validate=no", errors, who + " IPv4 DNS 2")) return false;
            }
            if (a.V6Index >= 0)
            {
                string idx = a.V6Index.ToString();
                if (!Netsh("interface ipv6 set dnsservers " + idx + " static " + V6Servers[0] + " primary validate=no", errors, who + " IPv6 DNS")) return false;
                if (!Netsh("interface ipv6 add dnsservers " + idx + " " + V6Servers[1] + " index=2 validate=no", errors, who + " IPv6 DNS 2")) return false;
            }
            try
            {
                foreach (string sv in V4Servers) WriteDohFlags(DohPath(a.Id, false, sv), 1);
                if (a.V6Index >= 0)
                    foreach (string sv in V6Servers) WriteDohFlags(DohPath(a.Id, true, sv), 1);
            }
            catch (Exception ex)
            {
                errors.Add(who + " DoH: " + ex.Message);
                return false;
            }
            return true;
        }

        static bool RegisterTemplates(Dictionary<string, string> global, bool recordExisting, List<string> errors)
        {
            int code;
            string shown = Core.RunTool("netsh.exe", "dns show encryption", out code);
            List<string> all = new List<string>(V4Servers);
            all.AddRange(V6Servers);
            foreach (string sv in all)
            {
                bool existed = ListHas(shown, sv);
                if (recordExisting) global["tpl." + sv] = existed ? "1" : "0";
                string tail = " server=" + sv + " dohtemplate=" + TemplateUrl + " autoupgrade=yes udpfallback=no";
                List<string> tmp = new List<string>();
                bool ok = Netsh("dns " + (existed ? "set" : "add") + " encryption" + tail, tmp, "Шаблон DoH " + sv);
                if (!ok) ok = Netsh("dns " + (existed ? "add" : "set") + " encryption" + tail, tmp, "Шаблон DoH " + sv);
                if (!ok) { errors.AddRange(tmp); return false; }
            }
            return true;
        }

        public static DnsResult Apply()
        {
            lock (Gate)
            {
                DnsResult res = new DnsResult();
                try
                {
                    int code;
                    Core.RunTool("netsh.exe", "dns show encryption", out code);
                    if (code != 0)
                    {
                        res.Errors.Add("Эта версия Windows не поддерживает DNS через HTTPS (нужна Windows 10 21H2 или новее / Windows 11). Настройки не изменены.");
                        return res;
                    }
                    List<DnsAdapter> adapters = FindAdapters();
                    Backup bk = Backup.Load();
                    if (adapters.Count == 0 && !bk.HasAdapters())
                    {
                        res.Errors.Add("Не найдено подходящих физических сетевых адаптеров (Ethernet/Wi-Fi со шлюзом). Настройки не изменены.");
                        return res;
                    }

                    bool newGlobal = !bk.Sections.ContainsKey(GlobalSection);
                    if (newGlobal)
                    {
                        Dictionary<string, string> g = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(DnsParams))
                        {
                            object v = k == null ? null : k.GetValue("EnableAutoDoh");
                            g["autoDoh"] = v is int ? ((int)v).ToString() : "absent";
                        }
                        bk.Sections[GlobalSection] = g;
                    }
                    // Backup is written before any change (templates are recorded inside RegisterTemplates).
                    List<string> terr = new List<string>();
                    Dictionary<string, string> glob = bk.Sections[GlobalSection];
                    if (newGlobal) bk.Save();
                    if (!RegisterTemplates(glob, newGlobal, terr))
                    {
                        res.Errors.AddRange(terr);
                        if (newGlobal) { bk.Save(); RestoreGlobal(bk, res.Errors); }
                        return res;
                    }
                    if (newGlobal) bk.Save();

                    int okCount = 0;
                    foreach (DnsAdapter a in adapters)
                    {
                        if (!bk.Sections.ContainsKey(a.Id))
                        {
                            bk.Sections[a.Id] = Snapshot(a);
                            bk.Save();
                        }
                        List<string> errs = new List<string>();
                        if (ApplyAdapter(a, errs))
                        {
                            okCount++;
                            res.Info.Add("DNS через HTTPS включён на «" + a.Name + "».");
                        }
                        else
                        {
                            res.Errors.AddRange(errs);
                            List<string> rerr = new List<string>();
                            if (RestoreAdapter(a.Id, bk.Sections[a.Id], rerr))
                            {
                                bk.Sections.Remove(a.Id);
                                bk.Save();
                                res.Errors.Add("Изменения на «" + a.Name + "» откатены.");
                            }
                            else res.Errors.AddRange(rerr);
                        }
                    }

                    if (okCount > 0)
                    {
                        try
                        {
                            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(DnsParams))
                                k.SetValue("EnableAutoDoh", 2, Microsoft.Win32.RegistryValueKind.DWord);
                        }
                        catch (Exception ex) { res.Errors.Add("EnableAutoDoh: " + ex.Message); }
                        Flush();
                        res.Ok = true;
                    }
                    else if (!bk.HasAdapters())
                    {
                        RestoreGlobal(bk, res.Errors);
                    }
                }
                catch (Exception ex)
                {
                    res.Errors.Add("Ошибка: " + ex.Message);
                }
                return res;
            }
        }

        // ---------- restore ----------

        static bool RestoreFamily(DnsAdapter a, Dictionary<string, string> s, bool v6, List<string> errors)
        {
            string fam = v6 ? "ipv6" : "ipv4";
            if (Get(s, v6 ? "v6" : "v4") != "1") return true; // protocol was not present before
            bool wasStatic = Get(s, v6 ? "v6static" : "v4static") == "1";
            List<string> list = ParseServers(Get(s, v6 ? "v6ns" : "v4ns"));
            int idx = a == null ? -1 : (v6 ? a.V6Index : a.V4Index);
            string i = idx.ToString();
            if (!wasStatic || list.Count == 0)
                return Netsh("interface " + fam + " set dnsservers " + i + " source=dhcp", errors, "Восстановление " + fam + " DNS");
            if (!Netsh("interface " + fam + " set dnsservers " + i + " static " + list[0] + " primary validate=no", errors, "Восстановление " + fam + " DNS")) return false;
            for (int n = 1; n < list.Count; n++)
                if (!Netsh("interface " + fam + " add dnsservers " + i + " " + list[n] + " index=" + (n + 1) + " validate=no", errors, "Восстановление " + fam + " DNS")) return false;
            return true;
        }

        static bool RestoreAdapter(string id, Dictionary<string, string> s, List<string> errors)
        {
            bool ok = true;
            try
            {
                if (!ValidGuid(id)) { errors.Add("Некорректный идентификатор адаптера в резервной копии."); return false; }
                DnsAdapter a = FindById(id);
                string name = Get(s, "name");
                for (int pass = 0; pass < 2; pass++)
                {
                    bool v6 = pass == 1;
                    if (Get(s, v6 ? "v6" : "v4") != "1") continue;
                    int idx = a == null ? -1 : (v6 ? a.V6Index : a.V4Index);
                    if (idx >= 0)
                    {
                        if (!RestoreFamily(a, s, v6, errors)) ok = false;
                    }
                    else
                    {
                        // adapter is absent: write the saved value straight into the registry
                        List<string> list = ParseServers(Get(s, v6 ? "v6ns" : "v4ns"));
                        string value = Get(s, v6 ? "v6static" : "v4static") == "1" ? string.Join(",", list.ToArray()) : "";
                        try { WriteNameServer(v6 ? Tcpip6 : Tcpip4, id, value); }
                        catch (Exception ex) { errors.Add("«" + name + "» реестр DNS: " + ex.Message); ok = false; }
                    }
                }
                try
                {
                    foreach (string sv in V4Servers) RestoreDohState(DohPath(id, false, sv), Get(s, "doh." + sv));
                    foreach (string sv in V6Servers) RestoreDohState(DohPath(id, true, sv), Get(s, "doh6." + sv));
                    CleanDohParents(id);
                }
                catch (Exception ex) { errors.Add("«" + name + "» ключи DoH: " + ex.Message); ok = false; }
            }
            catch (Exception ex)
            {
                errors.Add("Восстановление: " + ex.Message);
                ok = false;
            }
            return ok;
        }

        // EnableAutoDoh and the templates we added.
        static bool RestoreGlobal(Backup bk, List<string> errors)
        {
            Dictionary<string, string> g;
            if (!bk.Sections.TryGetValue(GlobalSection, out g)) return true;
            bool ok = true;
            try
            {
                string v = Get(g, "autoDoh");
                int n;
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(DnsParams))
                {
                    if (int.TryParse(v, out n)) k.SetValue("EnableAutoDoh", n, Microsoft.Win32.RegistryValueKind.DWord);
                    else if (v == "absent") k.DeleteValue("EnableAutoDoh", false);
                }
            }
            catch (Exception ex) { errors.Add("EnableAutoDoh: " + ex.Message); ok = false; }
            List<string> all = new List<string>(V4Servers);
            all.AddRange(V6Servers);
            foreach (string sv in all)
                if (Get(g, "tpl." + sv) == "0")
                    if (!Netsh("dns delete encryption server=" + sv, errors, "Удаление шаблона DoH " + sv)) ok = false;
            if (ok)
            {
                bk.Sections.Remove(GlobalSection);
                if (bk.Sections.Count == 0) { try { File.Delete(BackupPath()); } catch (Exception) { } }
                else bk.Save();
            }
            return ok;
        }

        public static DnsResult Restore()
        {
            lock (Gate)
            {
                DnsResult res = new DnsResult();
                try
                {
                    Backup bk = Backup.Load();
                    if (bk.Sections.Count == 0)
                    {
                        res.Ok = true;
                        res.Info.Add("Резервной копии DNS нет: восстанавливать нечего.");
                        return res;
                    }
                    bool all = true;
                    List<string> ids = new List<string>(bk.Sections.Keys);
                    foreach (string id in ids)
                    {
                        if (id.Equals(GlobalSection, StringComparison.OrdinalIgnoreCase)) continue;
                        List<string> errs = new List<string>();
                        string name = Get(bk.Sections[id], "name");
                        if (RestoreAdapter(id, bk.Sections[id], errs))
                        {
                            bk.Sections.Remove(id);
                            bk.Save();
                            res.Info.Add("DNS восстановлен на «" + name + "».");
                        }
                        else { all = false; res.Errors.AddRange(errs); }
                    }
                    if (all)
                    {
                        if (!RestoreGlobal(bk, res.Errors)) all = false;
                    }
                    Flush();
                    res.Ok = all;
                    if (!all) res.Errors.Add("Часть настроек не восстановлена, резервная копия сохранена: " + BackupPath());
                }
                catch (Exception ex)
                {
                    res.Errors.Add("Ошибка: " + ex.Message);
                }
                return res;
            }
        }

        // ---------- status ----------

        public static string StatusText()
        {
            try
            {
                bool on = false;
                try { on = Backup.Load().HasAdapters(); } catch (Exception) { }
                StringBuilder sb = new StringBuilder();
                sb.Append("Защищённый DNS (DoH): " + (on ? "включён" : "выключен"));
                List<DnsAdapter> found = FindAdapters();
                foreach (System.Net.NetworkInformation.NetworkInterface ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    bool match = false;
                    foreach (DnsAdapter a in found) if (string.Equals(a.Id, ni.Id, StringComparison.OrdinalIgnoreCase)) match = true;
                    if (!match) continue;
                    List<string> ips = new List<string>();
                    foreach (System.Net.IPAddress ip in ni.GetIPProperties().DnsAddresses) ips.Add(ip.ToString());
                    sb.Append("\nDNS «" + ni.Name + "»: " + (ips.Count > 0 ? string.Join(", ", ips.ToArray()) : "нет"));
                }
                return sb.ToString();
            }
            catch (Exception ex) { return "Защищённый DNS: не удалось определить (" + ex.Message + ")"; }
        }
    }

    // ---------- Curve25519 (RFC 7748) for WireGuard keys ----------

    static class Curve25519
    {
        static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
        static readonly BigInteger A24 = new BigInteger(121665);

        static BigInteger Mod(BigInteger v)
        {
            BigInteger r = v % P;
            return r.Sign < 0 ? r + P : r;
        }

        static BigInteger FromLE(byte[] b)
        {
            byte[] t = new byte[b.Length + 1];
            Array.Copy(b, t, b.Length);
            return new BigInteger(t);
        }

        static byte[] ToLE(BigInteger v)
        {
            byte[] r = new byte[32];
            byte[] t = v.ToByteArray();
            Array.Copy(t, r, Math.Min(t.Length, 32));
            return r;
        }

        // X25519 scalar multiplication (Montgomery ladder). k is clamped here as required by RFC 7748.
        public static byte[] ScalarMult(byte[] k, byte[] u)
        {
            byte[] kk = (byte[])k.Clone();
            kk[0] &= 248; kk[31] &= 127; kk[31] |= 64;
            byte[] uu = (byte[])u.Clone();
            uu[31] &= 127;
            BigInteger x1 = Mod(FromLE(uu));
            BigInteger x2 = BigInteger.One, z2 = BigInteger.Zero, x3 = x1, z3 = BigInteger.One;
            int swap = 0;
            for (int t = 254; t >= 0; t--)
            {
                int kt = (kk[t >> 3] >> (t & 7)) & 1;
                swap ^= kt;
                if (swap != 0) { BigInteger tx = x2; x2 = x3; x3 = tx; BigInteger tz = z2; z2 = z3; z3 = tz; }
                swap = kt;
                BigInteger A = Mod(x2 + z2);
                BigInteger AA = Mod(A * A);
                BigInteger B = Mod(x2 - z2);
                BigInteger BB = Mod(B * B);
                BigInteger E = Mod(AA - BB);
                BigInteger C = Mod(x3 + z3);
                BigInteger D = Mod(x3 - z3);
                BigInteger DA = Mod(D * A);
                BigInteger CB = Mod(C * B);
                BigInteger s1 = Mod(DA + CB);
                BigInteger s2 = Mod(DA - CB);
                x3 = Mod(s1 * s1);
                z3 = Mod(x1 * Mod(s2 * s2));
                x2 = Mod(AA * BB);
                z2 = Mod(E * Mod(AA + A24 * E));
            }
            if (swap != 0) { BigInteger tx2 = x2; x2 = x3; x3 = tx2; BigInteger tz2 = z2; z2 = z3; z3 = tz2; }
            BigInteger res = Mod(x2 * BigInteger.ModPow(z2, P - 2, P));
            return ToLE(res);
        }

        public static byte[] PublicKey(byte[] priv)
        {
            byte[] basePoint = new byte[32];
            basePoint[0] = 9;
            return ScalarMult(priv, basePoint);
        }
    }

    // ---------- small JSON helpers over JavaScriptSerializer output ----------

    static class Json
    {
        public static object Parse(string text)
        {
            System.Web.Script.Serialization.JavaScriptSerializer js = new System.Web.Script.Serialization.JavaScriptSerializer();
            js.MaxJsonLength = 4 * 1024 * 1024;
            return js.DeserializeObject(text);
        }

        public static object Child(object o, string key)
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            object v;
            if (d != null && d.TryGetValue(key, out v)) return v;
            return null;
        }

        public static object At(object o, int i)
        {
            System.Collections.IList l = o as System.Collections.IList;
            if (l != null && i >= 0 && i < l.Count) return l[i];
            return null;
        }

        public static int Count(object o)
        {
            System.Collections.IList l = o as System.Collections.IList;
            return l == null ? 0 : l.Count;
        }

        public static string Str(object o)
        {
            return o as string;
        }
    }

    // ---------- Cloudflare WARP account (per user, registered on first use) ----------

    static class Warp
    {
        const string RegUrl = "https://api.cloudflareclient.com/v0a2158/reg";
        const string FallbackEndpoint = "engage.cloudflareclient.com:2408";

        // Non-secret AmneziaWG obfuscation parameters (no keys or addresses here).
        const string Obfuscation = "MTU = 1280\r\nS1 = 0\r\nS2 = 0\r\nJc = 4\r\nJmin = 40\r\nJmax = 70\r\nH1 = 1\r\nH2 = 2\r\nH3 = 3\r\nH4 = 4\r\nI1 = <b 0xc2000000011419fa4bb3599f336777de79f81ca9a8d80d91eeec000044c635cef024a885dcb66d1420a91a8c427e87d6cf8e08b563932f449412cddf77d3e2594ea1c7a183c238a89e9adb7ffa57c133e55c59bec101634db90afb83f75b19fe703179e26a31902324c73f82d9354e1ed8da39af610afcb27e6590a44341a0828e5a3d2f0e0f7b0945d7bf3402feea0ee6332e19bdf48ffc387a97227aa97b205a485d282cd66d1c384bafd63dc42f822c4df2109db5b5646c458236ddcc01ae1c493482128bc0830c9e1233f0027a0d262f92b49d9d8abd9a9e0341f6e1214761043c021d7aa8c464b9d865f5fbe234e49626e00712031703a3e23ef82975f014ee1e1dc428521dc23ce7c6c13663b19906240b3efe403cf30559d798871557e4e60e86c29ea4504ed4d9bb8b549d0e8acd6c334c39bb8fb42ede68fb2aadf00cfc8bcc12df03602bbd4fe701d64a39f7ced112951a83b1dbbe6cd696dd3f15985c1b9fef72fa8d0319708b633cc4681910843ce753fac596ed9945d8b839aeff8d3bf0449197bd0bb22ab8efd5d63eb4a95db8d3ffc796ed5bcf2f4a136a8a36c7a0c65270d511aebac733e61d414050088a1c3d868fb52bc7e57d3d9fd132d78b740a6ecdc6c24936e92c28672dbe00928d89b891865f885aeb4c4996d50c2bbbb7a99ab5de02ac89b3308e57bcecf13f2da0333d1420e18b66b4c23d625d836b538fc0c221d6bd7f566a31fa292b85be96041d8e0bfe655d5dc1afed23eb8f2b3446561bbee7644325cc98d31cea38b865bdcc507e48c6ebdc7553be7bd6ab963d5a14615c4b81da7081c127c791224853e2d19bafdc0d9f3f3a6de898d14abb0e2bc849917e0a599ed4a541268ad0e60ea4d147dc33d17fa82f22aa505ccb53803a31d10a7ca2fea0b290a52ee92c7bf4aab7cea4e3c07b1989364eed87a3c6ba65188cd349d37ce4eefde9ec43bab4b4dc79e03469c2ad6b902e28e0bbbbf696781ad4edf424ffb35ce0236d373629008f142d04b5e08a124237e03e3149f4cdde92d7fae581a1ac332e26b2c9c1a6bdec5b3a9c7a2a870f7a0c25fc6ce245e029b686e346c6d862ad8df6d9b62474fbc31dbb914711f78074d4441f4e6e9edca3c52315a5c0653856e23f681558d669f4a4e6915bcf42b56ce36cb7dd3983b0b1d6fdf0f8efddb68e7ca0ae9dd4570fe6978fbb524109f6ec957ca61f1767ef74eb803b0f16abd0087cf2d01bc1db1c01d97ac81b3196c934586963fe7cf2d310e0739621e8bd00dc23fded18576d8c8f285d7bb5f43b547af3c76235de8b6f757f817683b2151600b11721219212bf27558edd439e73fce951f61d582320e5f4d6c315c71129b719277fc144bbe8ded25ab6d29b6e189c9bd9b16538faf60cc2aab3c3bb81fc2213657f2dd0ceb9b3b871e1423d8d3e8cc008721ef03b28e0ee7bb66b8f2a2ac01ef88df1f21ed49bf1ce435df31ac34485936172567488812429c269b49ee9e3d99652b51a7a614b7c460bf0d2d64d8349ded7345bedab1ea0a766a8470b1242f38d09f7855a32db39516c2bd4bcc538c52fa3a90c8714d4b006a15d9c7a7d04919a1cab48da7cce0d5de1f9e5f8936cffe469132991c6eb84c5191d1bcf69f70c58d9a7b66846440a9f0eef25ee6ab62715b50ca7bef0bc3013d4b62e1639b5028bdf757454356e9326a4c76dabfb497d451a3a1d2dbd46ec283d255799f72dfe878ae25892e25a2542d3ca9018394d8ca35b53ccd94947a8>";

        public static string AccountPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZapretGUI\\warp-account.conf"); }
        }

        public static void ResetAccount()
        {
            if (File.Exists(AccountPath)) File.Delete(AccountPath);
        }

        // Source order: embedded (private builds) -> stored account -> register a new one.
        public static string GetSourceConfig(Action<string> log)
        {
            string emb = Core.ReadEmbeddedTgConfig();
            if (emb != null) return emb;
            try
            {
                if (File.Exists(AccountPath))
                {
                    string c = File.ReadAllText(AccountPath, Encoding.UTF8);
                    if (Regex.IsMatch(c, @"(?im)^PrivateKey\s*=\s*\S") && Regex.IsMatch(c, @"(?im)^PublicKey\s*=\s*\S")) return c;
                }
            }
            catch (Exception ex) { log("WARP: не удалось прочитать сохранённый аккаунт: " + ex.Message); }
            log("WARP: регистрация нового аккаунта Cloudflare...");
            string conf = Register();
            Core.WriteTgConfigSecure(AccountPath, conf);
            log("WARP: аккаунт зарегистрирован и сохранён.");
            return conf;
        }

        static string Register()
        {
            byte[] priv = new byte[32];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider()) rng.GetBytes(priv);
            priv[0] &= 248; priv[31] &= 127; priv[31] |= 64;
            string pub = Convert.ToBase64String(Curve25519.PublicKey(priv));
            string tos = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
            string body = "{\"key\":\"" + pub + "\",\"install_id\":\"\",\"fcm_token\":\"\",\"tos\":\"" + tos +
                "\",\"model\":\"PC\",\"serial_number\":\"\",\"locale\":\"en_US\"}";
            Dictionary<string, string> h = new Dictionary<string, string>();
            h["User-Agent"] = "okhttp/3.12.1";
            h["CF-Client-Version"] = "a-6.10-2158";
            h["Content-Type"] = "application/json";
            string resp = Encoding.UTF8.GetString(Core.HttpCall("POST", RegUrl, 15000, 1024 * 1024, body, h));

            object root = Json.Parse(resp);
            object cfg = Json.Child(root, "config");
            object peer = Json.At(Json.Child(cfg, "peers"), 0);
            string peerKey = Json.Str(Json.Child(peer, "public_key"));
            string endpoint = Json.Str(Json.Child(Json.Child(peer, "endpoint"), "host"));
            object addrs = Json.Child(Json.Child(cfg, "interface"), "addresses");
            string v4 = Json.Str(Json.Child(addrs, "v4"));
            string v6 = Json.Str(Json.Child(addrs, "v6"));

            if (peerKey == null || !Regex.IsMatch(peerKey, @"^[A-Za-z0-9+/]{43}=$")) throw new Exception("WARP: некорректный ответ сервера (ключ)");
            if (v4 == null || !Regex.IsMatch(v4, @"^\d{1,3}(\.\d{1,3}){3}$")) throw new Exception("WARP: некорректный ответ сервера (адрес)");
            if (v6 != null && !Regex.IsMatch(v6, @"^[0-9a-fA-F:]+$")) v6 = null;
            if (endpoint == null || !Regex.IsMatch(endpoint, @"^[A-Za-z0-9.\-]+:\d{1,5}$")) endpoint = FallbackEndpoint;
            if (endpoint.EndsWith(":0")) endpoint = endpoint.Substring(0, endpoint.Length - 2) + ":2408";

            StringBuilder sb = new StringBuilder();
            sb.Append("[Interface]\r\n");
            sb.Append("PrivateKey = ").Append(Convert.ToBase64String(priv)).Append("\r\n");
            sb.Append("Address = ").Append(v4).Append("/32");
            if (v6 != null) sb.Append(", ").Append(v6).Append("/128");
            sb.Append("\r\n");
            sb.Append(Obfuscation).Append("\r\n");
            sb.Append("\r\n[Peer]\r\n");
            sb.Append("PublicKey = ").Append(peerKey).Append("\r\n");
            sb.Append("AllowedIPs = 0.0.0.0/0, ::/0\r\n");
            sb.Append("Endpoint = ").Append(endpoint).Append("\r\n");
            return sb.ToString();
        }
    }

    // ---------- self-update from GitHub Releases (boymep/zapret) ----------

    class UpdateInfo
    {
        public string Version, Notes, ZipUrl, ShaUrl;
        public bool Newer;
    }

    static class Updater
    {
        const string ApiUrl = "https://api.github.com/repos/boymep/zapret/releases/latest";
        const string DownloadPrefix = "https://github.com/boymep/zapret/releases/download/";
        const long MaxZip = 150L * 1024 * 1024;
        const long MaxUnpacked = 800L * 1024 * 1024;

        static int[] ParseVersion(string v)
        {
            v = (v ?? "").Trim();
            if (v.StartsWith("v") || v.StartsWith("V")) v = v.Substring(1);
            string[] parts = v.Split('.');
            int[] r = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                int n;
                if (!int.TryParse(parts[i], out n)) n = 0;
                r[i] = n;
            }
            return r;
        }

        // Numeric per dot-segment, missing segments are 0.
        public static int Compare(string a, string b)
        {
            int[] x = ParseVersion(a), y = ParseVersion(b);
            int len = Math.Max(x.Length, y.Length);
            for (int i = 0; i < len; i++)
            {
                int p = i < x.Length ? x[i] : 0;
                int q = i < y.Length ? y[i] : 0;
                if (p != q) return p < q ? -1 : 1;
            }
            return 0;
        }

        public static bool IsNewer(string local, string remote)
        {
            if (!Regex.IsMatch(local ?? "", @"^[vV]?\d+(\.\d+)*$")) return true;
            return Compare(remote, local) > 0;
        }

        // Queries the latest release; throws on any problem.
        public static UpdateInfo Check()
        {
            Dictionary<string, string> h = new Dictionary<string, string>();
            h["User-Agent"] = "ZapretGUI";
            h["Accept"] = "application/vnd.github+json";
            string json = Encoding.UTF8.GetString(Core.HttpCall("GET", ApiUrl, 10000, 4 * 1024 * 1024, null, h));
            object root = Json.Parse(json);
            string tag = Json.Str(Json.Child(root, "tag_name"));
            if (string.IsNullOrEmpty(tag)) throw new Exception("В ответе GitHub нет tag_name");
            string ver = tag.StartsWith("v") || tag.StartsWith("V") ? tag.Substring(1) : tag;
            if (!Regex.IsMatch(ver, @"^\d+(\.\d+){0,5}$")) throw new Exception("Неожиданный тег релиза: " + tag);

            UpdateInfo info = new UpdateInfo();
            info.Version = ver;
            info.Notes = Json.Str(Json.Child(root, "body")) ?? "";
            string zipName = "zapret-" + ver + ".zip";
            object assets = Json.Child(root, "assets");
            for (int i = 0; i < Json.Count(assets); i++)
            {
                object a = Json.At(assets, i);
                string name = Json.Str(Json.Child(a, "name"));
                string url = Json.Str(Json.Child(a, "browser_download_url"));
                if (name == null || url == null || !url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) continue;
                if (name == zipName) info.ZipUrl = url;
                else if (name == zipName + ".sha256") info.ShaUrl = url;
            }
            if (info.ZipUrl == null || info.ShaUrl == null) throw new Exception("В релизе " + tag + " нет архива или файла .sha256");
            info.Newer = IsNewer(Core.LocalVersion(), ver);
            return info;
        }

        static string Hex(byte[] b)
        {
            StringBuilder sb = new StringBuilder();
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        // Downloads, verifies and unpacks the release into the (new) directory dir. Returns the unpacked root.
        public static string Fetch(UpdateInfo info, string dir, Action<string> status)
        {
            status("Загрузка архива...");
            byte[] zip = Core.Download(info.ZipUrl, 60000, MaxZip);
            status("Проверка контрольной суммы...");
            string shaText = Encoding.UTF8.GetString(Core.Download(info.ShaUrl, 15000, 4096)).Trim();
            Match m = Regex.Match(shaText, @"^([0-9a-fA-F]{64})(\s|$)");
            if (!m.Success) throw new Exception("Некорректный файл .sha256");
            string actual;
            using (SHA256 sha = SHA256.Create()) actual = Hex(sha.ComputeHash(zip));
            if (!string.Equals(actual, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Контрольная сумма архива не совпала. Обновление отменено.");

            status("Распаковка...");
            Directory.CreateDirectory(dir);
            Extract(zip, dir);
            string root = dir;
            if (!File.Exists(Path.Combine(root, "ZapretGUI.exe")))
            {
                string[] subs = Directory.GetDirectories(dir);
                if (subs.Length == 1 && File.Exists(Path.Combine(subs[0], "ZapretGUI.exe"))) root = subs[0];
            }
            if (!File.Exists(Path.Combine(root, "ZapretGUI.exe")) || !File.Exists(Path.Combine(root, "bin\\winws.exe")))
                throw new Exception("В архиве нет ZapretGUI.exe или bin\\winws.exe");
            return root;
        }

        static void Extract(byte[] zip, string dir)
        {
            string full = Path.GetFullPath(dir);
            if (!full.EndsWith("\\")) full += "\\";
            long total = 0;
            int count = 0;
            byte[] buf = new byte[65536];
            using (ZipArchive za = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry e in za.Entries)
                {
                    string name = e.FullName.Replace('/', '\\');
                    if (name.Length == 0 || name.EndsWith("\\")) continue;
                    if (name.IndexOf(':') >= 0) throw new Exception("Недопустимый путь в архиве: " + e.FullName);
                    string dest = Path.GetFullPath(Path.Combine(full, name));
                    if (!dest.StartsWith(full, StringComparison.OrdinalIgnoreCase)) throw new Exception("Недопустимый путь в архиве: " + e.FullName);
                    if (++count > 5000) throw new Exception("Слишком много файлов в архиве");
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    using (Stream s = e.Open())
                    using (FileStream fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            total += n;
                            if (total > MaxUnpacked) throw new Exception("Архив слишком большой после распаковки");
                            fs.Write(buf, 0, n);
                        }
                    }
                }
            }
        }

        // User data that an update must never overwrite.
        static bool IsPreserved(string rel)
        {
            string r = rel.ToLowerInvariant();
            if (r == "zapretgui.ini") return true;
            if (r.StartsWith("lists\\") && r.EndsWith("-user.txt")) return true;
            if (r == "lists\\ipset-all.txt") return true;
            if (r.StartsWith("utils\\") && r.EndsWith(".enabled")) return true;
            if (r.StartsWith("utils\\test results\\")) return true;
            return false;
        }

        static bool SameContent(string a, string b)
        {
            FileInfo fa = new FileInfo(a), fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            byte[] x = File.ReadAllBytes(a), y = File.ReadAllBytes(b);
            for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false;
            return true;
        }

        static void CopyFile(string from, string to)
        {
            try { File.Copy(from, to, true); }
            catch (Exception ex)
            {
                if (!(ex is IOException) && !(ex is UnauthorizedAccessException)) throw;
                // Locked (e.g. the running exe): move it aside, the leftover *.old is removed on the next start.
                string old = to + ".old";
                try { if (File.Exists(old)) File.Delete(old); } catch (Exception) { }
                File.Move(to, old);
                File.Copy(from, to, true);
            }
        }

        // Copies the unpacked release over dst. Returns the number of files written.
        public static int Apply(string src, string dst, Action<string> log)
        {
            string s = src.TrimEnd('\\') + "\\";
            int written = 0;
            foreach (string f in Directory.GetFiles(s, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(s.Length);
                if (IsPreserved(rel)) continue;
                string target = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (File.Exists(target) && SameContent(f, target)) continue;
                CopyFile(f, target);
                written++;
            }
            // Strategies removed in the new release.
            foreach (string g in Directory.GetFiles(dst, "general*.bat"))
            {
                if (File.Exists(Path.Combine(s, Path.GetFileName(g)))) continue;
                try { File.Delete(g); log("Удалена устаревшая стратегия: " + Path.GetFileName(g)); } catch (Exception) { }
            }
            log("Обновлено файлов: " + written);
            return written;
        }

        // Removes leftovers of replaced files (*.old) from earlier updates.
        public static void CleanOld(string root)
        {
            try
            {
                foreach (string f in Directory.GetFiles(root, "*.old", SearchOption.AllDirectories))
                    try { File.Delete(f); } catch (Exception) { }
            }
            catch (Exception) { }
        }
    }

    class MainForm : Form
    {
        const string MutexName = "ZapretGUI_SingleInstance_Mutex";
        const string EventName = "ZapretGUI_ShowWindow_Event";
        const int MaxLogLength = 60000;

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        readonly EventWaitHandle showEvent;
        readonly bool startInTray;
        bool allowVisible;
        bool exiting;
        bool trayHintShown;
        bool loading = true;
        bool stopRequested;
        bool busy;

        Process winws;
        List<Strategy> strategies;
        Strategy current;
        int gameMode;

        Button toggleButton;
        Label statusLabel;
        ComboBox strategyBox;
        ComboBox gameBox;
        CheckBox autostartCheck, autoEnableCheck, trayCheck;
        CheckBox secureDnsCheck;
        ToolTip dnsTip;
        bool secureDnsBusy;
        ToolStripMenuItem trayDnsItem;
        TextBox logBox;
        NotifyIcon tray;
        ContextMenuStrip trayMenu;
        ToolStripMenuItem trayToggleItem, trayStrategyMenu;
        Icon iconOn, iconOff;
        TabControl tabs;
        TabPage listsPage;
        ListsTab listsTab;
        ServiceTab serviceTab;
        CheckBox tgCheck;
        Label tgStatus;
        ToolStripMenuItem trayTgItem;
        CheckBox ytCheck;
        ToolStripMenuItem trayYtItem;
        volatile bool tgWant, ytWant;
        bool tgUp;
        readonly object tgLock = new object();
        TabPage mainPage, servicePage;
        Label versionLabel, updateStatus;
        Button updateButton;
        bool updating;
        bool checkUpdates = true;
        readonly bool afterUpdate, resumeAfter;

        public MainForm(EventWaitHandle evt, bool tray, bool afterUpdate, bool resume)
        {
            showEvent = evt;
            startInTray = tray;
            allowVisible = !tray;
            this.afterUpdate = afterUpdate;
            resumeAfter = resume;
            BuildUi();
            BuildTray();
            IntPtr h = Handle; // make sure the handle exists so logging works before the window is shown
            LoadState();
            listsTab.Init();
            StartShowListener();
            loading = false;
            UpdateUi();
            Log("Папка: " + Core.Root);
            CheckService();
            Dictionary<string, string> ini = Core.LoadIni();
            bool auto = GetBool(ini, "autoenable", false);
            if (auto || resumeAfter)
                BeginInvoke(new MethodInvoker(delegate { Start(); }));
            serviceTab.StartupRefresh();
            TgRefreshAsync();
            if (secureDnsCheck.Checked) SecureDnsRun(true, true);
            if (afterUpdate)
            {
                Log("Обновлено до версии " + Core.LocalVersion());
                Balloon("Обновлено до версии " + Core.LocalVersion());
            }
            StartupUpdateCheck();
        }

        // ---------- updates ----------

        void SetUpdating(bool on, string status)
        {
            updating = on;
            updateButton.Enabled = !on;
            updateStatus.ForeColor = Color.DimGray;
            updateStatus.Text = status;
        }

        void SetUpdateText(string text)
        {
            UiPost(delegate { updateStatus.Text = text; });
        }

        // Silent check at startup: balloon + status text only, never installs.
        void StartupUpdateCheck()
        {
            if (!checkUpdates) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    UpdateInfo info = Updater.Check();
                    if (!info.Newer) return;
                    UiPost(delegate
                    {
                        if (updating) return;
                        updateStatus.ForeColor = Color.FromArgb(185, 120, 0);
                        updateStatus.Text = "Доступна версия " + info.Version;
                        Balloon("Доступна новая версия: " + info.Version + " (установлена " + Core.LocalVersion() + ")");
                    });
                }
                catch (Exception) { }
            });
        }

        public void CheckForUpdates()
        {
            if (updating) return;
            SetUpdating(true, "Проверка обновлений...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateInfo info = null;
                string err = null;
                try { info = Updater.Check(); }
                catch (Exception ex) { err = ex.Message; }
                UiPost(delegate { OnCheckDone(info, err); });
            });
        }

        void OnCheckDone(UpdateInfo info, string err)
        {
            if (err != null)
            {
                SetUpdating(false, "Не удалось проверить обновления");
                Log("Обновления: " + err);
                MessageBox.Show(this, "Не удалось проверить обновления:\n" + err, "Zapret GUI", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!info.Newer)
            {
                SetUpdating(false, "Установлена последняя версия");
                return;
            }
            string notes = (info.Notes ?? "").Trim();
            if (notes.Length > 800) notes = notes.Substring(0, 800) + "...";
            DialogResult r = MessageBox.Show(this,
                "Доступна версия " + info.Version + " (установлена " + Core.LocalVersion() + ").\n\n" + notes + "\n\nУстановить сейчас?",
                "Zapret GUI", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes || !listsTab.ConfirmDiscard())
            {
                SetUpdating(false, "Доступна версия " + info.Version);
                return;
            }
            BeginInstall(info);
        }

        void BeginInstall(UpdateInfo info)
        {
            SetUpdating(true, "Загрузка...");
            Log("Обновление до версии " + info.Version + "...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string tmp = Path.Combine(Path.GetTempPath(), "zapret-upd-" + Path.GetRandomFileName());
                string err = null;
                bool wasOn = false;
                try
                {
                    string src = Updater.Fetch(info, tmp, delegate(string s) { SetUpdateText(s); Log(s); });
                    if (Core.ServiceState("zapret") != null)
                        throw new Exception("Установлена служба zapret. Удалите её на вкладке «Сервис» (расширенные настройки) и повторите обновление.");
                    SetUpdateText("Установка...");
                    UiInvoke(delegate { wasOn = IsRunning; StopBypass(); });
                    TgDown();
                    Updater.Apply(src, Core.Root, delegate(string s) { Log(s); });
                }
                catch (Exception ex) { err = ex.Message; }
                finally { try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch (Exception) { } }
                UiPost(delegate { OnInstallDone(err, wasOn); });
            });
        }

        void OnInstallDone(string err, bool wasOn)
        {
            if (err == null)
            {
                try
                {
                    string exe = Path.Combine(Core.Root, "ZapretGUI.exe");
                    string args = "--wait-pid " + Process.GetCurrentProcess().Id + " --after-update" + (Visible ? "" : " --tray") + (wasOn ? " --resume" : "");
                    ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                    psi.WorkingDirectory = Core.Root;
                    psi.UseShellExecute = false;
                    Process.Start(psi);
                    Log("Перезапуск после обновления...");
                    ExitApp();
                    return;
                }
                catch (Exception ex) { err = "Не удалось запустить новую версию: " + ex.Message; }
            }
            SetUpdating(false, "Ошибка обновления");
            Log("Обновление не выполнено: " + err);
            MessageBox.Show(this, "Обновление не выполнено:\n" + err, "Zapret GUI", MessageBoxButtons.OK, MessageBoxIcon.Error);
            if (wasOn && !IsRunning) Start();
        }

        public bool CheckUpdatesEnabled
        {
            get { return checkUpdates; }
            set { checkUpdates = value; SaveState(); }
        }

        public void ResetWarp()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    bool had = File.Exists(Warp.AccountPath);
                    lock (tgLock) { Warp.ResetAccount(); }
                    Log(had ? "WARP-аккаунт сброшен, новый будет создан при следующем включении VPN." : "Сохранённого WARP-аккаунта нет.");
                    if (had && tgUp && (tgCheck.Checked || ytCheck.Checked) && IsRunning) { TgDown(); TgUp(); TgRefresh(); }
                }
                catch (Exception ex) { Log("Не удалось сбросить WARP-аккаунт: " + ex.Message); }
            });
        }

        // ---------- Telegram split tunnel (AmneziaWG) ----------

        void OnTgCheckChanged(object sender, EventArgs e)
        {
            trayTgItem.Checked = tgCheck.Checked;
            tgWant = tgCheck.Checked;
            OnVpnSelectionChanged();
        }

        void OnYtCheckChanged(object sender, EventArgs e)
        {
            trayYtItem.Checked = ytCheck.Checked;
            ytWant = ytCheck.Checked;
            OnVpnSelectionChanged();
        }

        // Either service toggled: rebuild the tunnel with the new set of ranges, or bring it down.
        void OnVpnSelectionChanged()
        {
            if (loading) return;
            SaveState();
            if (tgCheck.Checked || ytCheck.Checked)
            {
                if (IsRunning) TgRestartAsync();
            }
            else TgDownAsync();
        }

        void TgRestartAsync()
        {
            ThreadPool.QueueUserWorkItem(delegate { try { TgDown(); TgUp(); } catch (Exception ex) { Log("Туннель VPN: " + ex.Message); } TgRefresh(); });
        }

        void TgUpAsync()
        {
            ThreadPool.QueueUserWorkItem(delegate { try { TgUp(); } catch (Exception ex) { Log("Туннель Telegram: " + ex.Message); } TgRefresh(); });
        }

        void TgDownAsync()
        {
            ThreadPool.QueueUserWorkItem(delegate { try { TgDown(); } catch (Exception ex) { Log("Туннель Telegram: " + ex.Message); } TgRefresh(); });
        }

        void TgRefreshAsync()
        {
            ThreadPool.QueueUserWorkItem(delegate { TgRefresh(); });
        }

        // Updates the status line (any thread).
        void TgRefresh()
        {
            bool installed = File.Exists(Core.AmneziaExe);
            string st = installed ? Core.ServiceState(Core.TgServiceName) : null;
            bool up = st == "RUNNING";
            TgShow(installed, up);
        }

        void TgShow(bool installed, bool up)
        {
            UiPost(delegate
            {
                tgUp = up;
                if (!installed) { tgStatus.Text = "Туннель: AmneziaWG не установлен"; tgStatus.ForeColor = Color.Firebrick; }
                else if (up) { tgStatus.Text = "Туннель: включён (" + TgServicesText() + ")"; tgStatus.ForeColor = Color.FromArgb(30, 130, 50); }
                else { tgStatus.Text = "Туннель: отключён"; tgStatus.ForeColor = Color.DimGray; }
                UpdateUi();
            });
        }

        string TgServicesText()
        {
            string s = tgCheck.Checked ? "Telegram" : "";
            if (ytCheck.Checked) s += (s.Length > 0 ? ", " : "") + "YouTube";
            return s.Length > 0 ? s : "нет сервисов";
        }

        // Runs amneziawg.exe hidden, logs exit code and (key-free) output.
        string RunAmnezia(string args)
        {
            int code;
            string o = Core.RunTool(Core.AmneziaExe, args, out code);
            StringBuilder sb = new StringBuilder();
            foreach (string l in o.Split('\n'))
            {
                string t = l.Trim();
                if (t.Length == 0 || Regex.IsMatch(t, @"(?i)key|^[A-Z]\w*\s*=")) continue;
                sb.Append(t.Length > 200 ? t.Substring(0, 200) : t).Append(' ');
            }
            Log("amneziawg.exe " + args.Split(' ')[0] + ": код " + code + (sb.Length > 0 ? ", " + sb.ToString().Trim() : ""));
            return code == 0 ? null : "код " + code;
        }

        void TgUp()
        {
            lock (tgLock)
            {
                if (!File.Exists(Core.AmneziaExe)) { Log("AmneziaWG не найден: " + Core.AmneziaExe); return; }
                string st = Core.ServiceState(Core.TgServiceName);
                if (st == "RUNNING") return; // e.g. strategy restart: do not bounce the tunnel
                string src;
                try { src = Warp.GetSourceConfig(delegate(string m) { Log(m); }); }
                catch (Exception ex) { Log("WARP: не удалось получить конфиг: " + ex.Message); return; }

                int code;
                string all = Core.RunTool("sc.exe", "query type= service", out code);
                foreach (Match m in Regex.Matches(all, @"(WireGuardTunnel|AmneziaWGTunnel)\$([^\s]+)"))
                    if (m.Value != Core.TgServiceName)
                        Log("Предупреждение: запущен другой VPN-туннель (" + m.Value + "). Возможен конфликт маршрутов.");

                List<string> cidrList = new List<string>();
                bool wantTg = tgWant, wantYt = ytWant;
                if (wantTg)
                {
                    bool fresh;
                    string[] tgc = Core.FetchTgCidrs(out fresh);
                    Log("Подсети Telegram: " + tgc.Length + (fresh ? " (telegram.org)" : " (встроенный список)"));
                    cidrList.AddRange(tgc);
                }
                if (wantYt)
                {
                    Log("Подсети YouTube/Google: " + Core.YtCidrs.Length + " (встроенный список)");
                    foreach (string c in Core.YtCidrs) if (!cidrList.Contains(c)) cidrList.Add(c);
                }
                if (cidrList.Count == 0) return;
                string[] cidrs = cidrList.ToArray();
                string conf = Core.BuildTgConfig(src, cidrs);
                if (st != null) RunAmnezia("/uninstalltunnelservice " + Core.TgTunnelName);
                Core.WriteTgConfigSecure(Core.TgConfPath, conf);
                RunAmnezia("/installtunnelservice \"" + Core.TgConfPath + "\"");
                for (int i = 0; i < 20; i++)
                {
                    if (Core.ServiceState(Core.TgServiceName) == "RUNNING") { Log("Туннель Telegram: подключён."); return; }
                    Thread.Sleep(500);
                }
                Log("Туннель Telegram не запустился. Проверьте конфиг.");
            }
        }

        void TgDown()
        {
            lock (tgLock)
            {
                if (File.Exists(Core.AmneziaExe) && Core.ServiceState(Core.TgServiceName) != null)
                {
                    RunAmnezia("/uninstalltunnelservice " + Core.TgTunnelName);
                    for (int i = 0; i < 20 && Core.ServiceState(Core.TgServiceName) != null; i++) Thread.Sleep(250);
                    Log("Туннель Telegram: отключён.");
                }
                try { if (File.Exists(Core.TgConfPath)) File.Delete(Core.TgConfPath); } catch (Exception) { }
            }
        }

        // ---------- API for the Lists / Service tabs ----------

        public void RestartIfRunning()
        {
            if (!IsRunning) return;
            Log("Перезапуск обхода...");
            Stop(true);
            Start();
        }

        public void StopBypass()
        {
            if (IsRunning) Stop(false);
        }

        public void Balloon(string text)
        {
            if (tray != null) tray.ShowBalloonTip(5000, "Zapret GUI", text, ToolTipIcon.Info);
        }

        public void UiInvoke(MethodInvoker action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) Invoke(action); else action();
        }

        public void UiPost(MethodInvoker action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(action); } catch (Exception) { }
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(allowVisible && value);
        }

        // ---------- UI ----------

        void BuildUi()
        {
            Text = "Zapret GUI";
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(640, 720);
            MinimumSize = new Size(560, 600); // main tab has the update row; keeps the log readable
            Padding = new Padding(8);

            tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            mainPage = new TabPage("Главная");
            mainPage.UseVisualStyleBackColor = true;
            mainPage.Padding = new Padding(10);
            listsPage = new TabPage("Списки");
            listsPage.UseVisualStyleBackColor = true;
            servicePage = new TabPage("Сервис");
            servicePage.UseVisualStyleBackColor = true;
            tabs.TabPages.Add(mainPage);
            tabs.TabPages.Add(listsPage);
            tabs.TabPages.Add(servicePage);
            Controls.Add(tabs);
            // Leaving the Lists tab with unsaved edits asks first.
            tabs.Selecting += delegate(object s, TabControlCancelEventArgs e)
            {
                if (listsTab != null && tabs.SelectedTab == listsPage && e.TabPage != listsPage && !listsTab.ConfirmDiscard())
                    e.Cancel = true;
            };

            listsTab = new ListsTab(this);
            listsTab.Dock = DockStyle.Fill;
            listsPage.Controls.Add(listsTab);
            serviceTab = new ServiceTab(this);
            serviceTab.Dock = DockStyle.Fill;
            servicePage.Controls.Add(serviceTab);

            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill;
            t.ColumnCount = 2;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            mainPage.Controls.Add(t);

            // Version + update row
            FlowLayoutPanel ur = new FlowLayoutPanel();
            ur.AutoSize = true;
            ur.Dock = DockStyle.Fill;
            ur.WrapContents = true;
            ur.Margin = new Padding(0, 0, 0, 6);
            updateButton = new Button();
            updateButton.AutoSize = true;
            updateButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            updateButton.Text = "Проверить обновления";
            updateButton.Padding = new Padding(6, 1, 6, 1);
            updateButton.Click += delegate { CheckForUpdates(); };
            versionLabel = new Label();
            versionLabel.AutoSize = true;
            versionLabel.Margin = new Padding(8, 8, 3, 3);
            versionLabel.Text = "Версия " + Core.LocalVersion();
            updateStatus = new Label();
            updateStatus.AutoSize = true;
            updateStatus.ForeColor = Color.DimGray;
            updateStatus.Margin = new Padding(8, 8, 3, 3);
            ur.Controls.Add(updateButton);
            ur.Controls.Add(versionLabel);
            ur.Controls.Add(updateStatus);
            AddRow(t, ur, 0);

            strategyBox = new ComboBox();
            strategyBox.DropDownStyle = ComboBoxStyle.DropDownList;
            strategyBox.Dock = DockStyle.Fill;
            strategyBox.SelectedIndexChanged += OnStrategyChanged;
            AddLabeled(t, "Стратегия:", strategyBox);

            gameBox = new ComboBox();
            gameBox.DropDownStyle = ComboBoxStyle.DropDownList;
            gameBox.Dock = DockStyle.Fill;
            gameBox.Items.AddRange(new object[] { "Выкл", "TCP+UDP", "TCP", "UDP" });
            gameBox.SelectedIndex = 0;
            gameBox.SelectedIndexChanged += OnGameChanged;
            AddLabeled(t, "Игровой фильтр:", gameBox);

            autostartCheck = NewCheck("Запускать вместе с Windows");
            autoEnableCheck = NewCheck("Включать обход при запуске");
            trayCheck = NewCheck("Сворачивать в трей при закрытии");
            trayCheck.Checked = true;
            autostartCheck.Click += OnAutostartClick;
            autoEnableCheck.CheckedChanged += delegate { SaveState(); };
            trayCheck.CheckedChanged += delegate { SaveState(); };
            AddRow(t, autostartCheck, 0);
            AddRow(t, autoEnableCheck, 0);
            AddRow(t, trayCheck, 0);
            secureDnsCheck = NewCheck("Защищённый DNS (DoH через Cloudflare)");
            secureDnsCheck.Click += delegate { SecureDnsRun(secureDnsCheck.Checked, false); };
            dnsTip = new ToolTip();
            dnsTip.AutoPopDelay = 30000;
            dnsTip.SetToolTip(secureDnsCheck, "Провайдер может подменять DNS-ответы (например, для youtube.com). Включает DNS через HTTPS 1.1.1.1 для всей Windows. При выключении прежние настройки DNS восстанавливаются.");
            AddRow(t, secureDnsCheck, 0);

            // Telegram split tunnel group
            GroupBox tg = new GroupBox();
            tg.Text = "VPN через WARP (AmneziaWG)";
            tg.Dock = DockStyle.Fill;
            tg.AutoSize = true;
            tg.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            tg.Padding = new Padding(8, 4, 8, 6);
            tg.Margin = new Padding(3, 6, 3, 3);
            TableLayoutPanel tgt = new TableLayoutPanel();
            tgt.Dock = DockStyle.Top;
            tgt.AutoSize = true;
            tgt.ColumnCount = 2;
            tgt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            tgt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tg.Controls.Add(tgt);
            tgCheck = NewCheck("Направлять Telegram через VPN");
            tgCheck.CheckedChanged += OnTgCheckChanged;
            tgt.Controls.Add(tgCheck, 0, 0);
            tgt.SetColumnSpan(tgCheck, 2);
            ytCheck = NewCheck("Направлять YouTube через VPN");
            ytCheck.CheckedChanged += OnYtCheckChanged;
            tgt.Controls.Add(ytCheck, 0, 1);
            tgt.SetColumnSpan(ytCheck, 2);
            tgStatus = new Label();
            tgStatus.AutoSize = true;
            tgStatus.Margin = new Padding(3, 4, 3, 2);
            tgStatus.Text = "Туннель: ...";
            tgt.Controls.Add(tgStatus, 0, 2);
            tgt.SetColumnSpan(tgStatus, 2);
            Label tgHint = new Label();
            tgHint.AutoSize = true;
            tgHint.ForeColor = Color.DimGray;
            tgHint.Margin = new Padding(3, 0, 3, 2);
            tgHint.Text = Core.HasEmbeddedTgConfig()
                ? "Через туннель идёт только трафик выбранных сервисов."
                : "Через туннель идёт только трафик выбранных сервисов. Аккаунт Cloudflare WARP создаётся автоматически при первом включении.";
            tgHint.MaximumSize = new Size(430, 0);
            tgt.Controls.Add(tgHint, 0, 3);
            tgt.SetColumnSpan(tgHint, 2);
            AddRow(t, tg, 0);

            logBox = new TextBox();
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.Dock = DockStyle.Fill;
            logBox.Font = new Font("Consolas", 9f);
            logBox.BackColor = Color.White;
            logBox.MaxLength = 0;
            t.Controls.Add(logBox, 0, NextRow(t));
            t.SetColumnSpan(logBox, 2);
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            statusLabel = new Label();
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.AutoSize = true;
            statusLabel.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            statusLabel.Margin = new Padding(3, 8, 3, 6);
            AddRow(t, statusLabel, 0);

            toggleButton = new Button();
            toggleButton.Dock = DockStyle.Fill;
            toggleButton.Height = 64;
            toggleButton.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
            toggleButton.FlatStyle = FlatStyle.Flat;
            toggleButton.FlatAppearance.BorderSize = 0;
            toggleButton.ForeColor = Color.White;
            toggleButton.Margin = new Padding(3, 3, 3, 3);
            toggleButton.Click += delegate { Toggle(); };
            AddRow(t, toggleButton, 64);

            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized && !exiting) HideToTray();
            };
            FormClosing += OnFormClosing;
        }

        // Rows: all AutoSize except the toggle button (fixed) and the log (fills).
        void AddRow(TableLayoutPanel t, Control c, int height)
        {
            int row = NextRow(t);
            t.Controls.Add(c, 0, row);
            t.SetColumnSpan(c, 2);
            if (height > 0) t.RowStyles.Add(new RowStyle(SizeType.Absolute, height + 12));
            else t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        void AddLabeled(TableLayoutPanel t, string text, Control c)
        {
            int row = NextRow(t);
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Anchor = AnchorStyles.Left;
            l.Margin = new Padding(3, 6, 12, 6);
            t.Controls.Add(l, 0, row);
            c.Margin = new Padding(3, 3, 3, 3);
            t.Controls.Add(c, 1, row);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        int NextRow(TableLayoutPanel t)
        {
            return t.RowStyles.Count;
        }

        CheckBox NewCheck(string text)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.Margin = new Padding(3, 3, 3, 3);
            return c;
        }

        void BuildTray()
        {
            iconOn = MakeIcon(Color.FromArgb(46, 170, 70));
            iconOff = MakeIcon(Color.FromArgb(140, 140, 140));
            Icon = iconOff;

            trayMenu = new ContextMenuStrip();
            ToolStripMenuItem show = new ToolStripMenuItem("Показать");
            show.Click += delegate { ShowWindow(); };
            trayToggleItem = new ToolStripMenuItem("Включить обход");
            trayToggleItem.Click += delegate { Toggle(); };
            trayStrategyMenu = new ToolStripMenuItem("Стратегия");
            ToolStripMenuItem exit = new ToolStripMenuItem("Выход");
            exit.Click += delegate { ExitApp(); };
            trayMenu.Items.Add(show);
            trayMenu.Items.Add(trayToggleItem);
            trayMenu.Items.Add(trayStrategyMenu);
            trayTgItem = new ToolStripMenuItem("Telegram через VPN");
            trayTgItem.CheckOnClick = true;
            trayTgItem.Click += delegate { tgCheck.Checked = trayTgItem.Checked; };
            trayMenu.Items.Add(trayTgItem);
            trayYtItem = new ToolStripMenuItem("YouTube через VPN");
            trayYtItem.CheckOnClick = true;
            trayYtItem.Click += delegate { ytCheck.Checked = trayYtItem.Checked; };
            trayMenu.Items.Add(trayYtItem);
            trayDnsItem = new ToolStripMenuItem("Защищённый DNS (DoH)");
            trayDnsItem.CheckOnClick = true;
            trayDnsItem.Click += delegate { secureDnsCheck.Checked = trayDnsItem.Checked; SecureDnsRun(trayDnsItem.Checked, false); };
            trayMenu.Items.Add(trayDnsItem);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(exit);

            tray = new NotifyIcon();
            tray.ContextMenuStrip = trayMenu;
            tray.Icon = iconOff;
            tray.Text = "Обход: выключен";
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowWindow(); };
        }

        static Icon MakeIcon(Color c)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (Brush b = new SolidBrush(c)) g.FillEllipse(b, 2, 2, 28, 28);
                    using (Pen p = new Pen(Color.FromArgb(90, 0, 0, 0), 1.5f)) g.DrawEllipse(p, 2, 2, 28, 28);
                }
                IntPtr h = bmp.GetHicon();
                Icon tmp = Icon.FromHandle(h);
                Icon copy = (Icon)tmp.Clone();
                tmp.Dispose();
                DestroyIcon(h);
                return copy;
            }
        }

        // ---------- window visibility ----------

        void StartShowListener()
        {
            Thread t = new Thread(delegate()
            {
                while (true)
                {
                    try
                    {
                        showEvent.WaitOne();
                        if (exiting) return;
                        BeginInvoke(new MethodInvoker(ShowWindow));
                    }
                    catch (Exception) { return; }
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        void ShowWindow()
        {
            allowVisible = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        void HideToTray()
        {
            Hide();
            if (!trayHintShown)
            {
                trayHintShown = true;
                tray.ShowBalloonTip(3000, "Zapret GUI", "Программа работает в трее.", ToolTipIcon.Info);
            }
        }

        void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing && trayCheck.Checked)
            {
                e.Cancel = true;
                HideToTray();
            }
            else if (!exiting)
            {
                if (!listsTab.ConfirmDiscard()) { e.Cancel = true; return; }
                exiting = true;
                Cleanup();
            }
        }

        void ExitApp()
        {
            if (exiting) return;
            if (!listsTab.ConfirmDiscard()) return;
            exiting = true;
            Cleanup();
            Application.Exit();
        }

        void Cleanup()
        {
            SaveState();
            try { Stop(false); } catch (Exception) { }
            try { TgDown(); } catch (Exception) { }
            try { showEvent.Set(); } catch (Exception) { }
            tray.Visible = false;
            tray.Dispose();
        }

        // ---------- state ----------

        static bool GetBool(Dictionary<string, string> d, string key, bool def)
        {
            string v;
            if (!d.TryGetValue(key, out v)) return def;
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        // ---------- secure DNS (DoH via Cloudflare) ----------

        void SecureDnsRun(bool want, bool silent)
        {
            if (secureDnsBusy) return;
            secureDnsBusy = true;
            secureDnsCheck.Enabled = false;
            trayDnsItem.Enabled = false;
            Log(want ? "Защищённый DNS: включение..." : "Защищённый DNS: восстановление прежних настроек...");
            Thread th = new Thread((ThreadStart)delegate
            {
                DnsResult r;
                try { r = want ? SecureDns.Apply() : SecureDns.Restore(); }
                catch (Exception ex) { r = new DnsResult(); r.Errors.Add("Ошибка: " + ex.Message); }
                UiPost(delegate { SecureDnsDone(want, silent, r); });
            });
            th.IsBackground = true;
            th.Start();
        }

        void SecureDnsDone(bool want, bool silent, DnsResult r)
        {
            foreach (string s in r.Info) Log(s);
            foreach (string s in r.Errors) Log("[!] " + s);
            bool state;
            if (r.Ok) state = want;
            else if (want) state = silent; // silent startup failure: keep the setting and retry next launch
            else state = true;             // restore failed: backup is kept, setting stays on
            secureDnsCheck.Checked = state;
            trayDnsItem.Checked = state;
            secureDnsCheck.Enabled = true;
            trayDnsItem.Enabled = true;
            secureDnsBusy = false;
            SaveState();
            if (r.Errors.Count > 0)
            {
                string text = "Защищённый DNS: " + string.Join("\n", r.Errors.ToArray());
                if (silent) Balloon("Защищённый DNS: ошибка, подробности в журнале.");
                else MessageBox.Show(this, text, "Zapret GUI", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else if (!silent)
                Balloon(want ? "Защищённый DNS включён." : "Прежние настройки DNS восстановлены.");
            if (r.Ok) Log(want ? "Защищённый DNS включён." : "Прежние настройки DNS восстановлены.");
        }

        void LoadState()
        {
            Dictionary<string, string> ini = Core.LoadIni();
            strategies = Core.LoadStrategies();
            strategyBox.Items.Clear();
            foreach (Strategy s in strategies) strategyBox.Items.Add(s);
            string saved;
            ini.TryGetValue("strategy", out saved);
            int sel = 0;
            for (int i = 0; i < strategies.Count; i++)
                if (string.Equals(strategies[i].FileName, saved, StringComparison.OrdinalIgnoreCase)) sel = i;
            if (strategies.Count > 0) strategyBox.SelectedIndex = sel;
            current = strategies.Count > 0 ? strategies[sel] : null;

            string gm;
            int g = 0;
            if (ini.TryGetValue("gamefilter", out gm)) int.TryParse(gm, out g);
            if (g < 0 || g > 3) g = 0;
            gameMode = g;
            gameBox.SelectedIndex = g;

            autoEnableCheck.Checked = GetBool(ini, "autoenable", false);
            trayCheck.Checked = GetBool(ini, "closeToTray", true);
            tgCheck.Checked = GetBool(ini, "tgEnabled", false);
            ytCheck.Checked = GetBool(ini, "ytEnabled", false);
            secureDnsCheck.Checked = GetBool(ini, "secureDns", false);
            trayDnsItem.Checked = secureDnsCheck.Checked;
            checkUpdates = GetBool(ini, "check_updates", true); // default on; replaces the old utils\check_updates.enabled flag
            try
            {
                string oldFlag = Path.Combine(Core.Root, "utils\\check_updates.enabled");
                if (File.Exists(oldFlag)) File.Delete(oldFlag);
            }
            catch (Exception) { }
            trayTgItem.Checked = tgCheck.Checked;
            trayYtItem.Checked = ytCheck.Checked;
            autostartCheck.Checked = QueryTask();

            RebuildTrayStrategies();
            if (strategies.Count == 0) Log("Нет файлов general*.bat в " + Core.Root);
        }

        void SaveState()
        {
            if (loading) return;
            try
            {
                Dictionary<string, string> d = Core.LoadIni();
                d.Remove("advanced");
                d["check_updates"] = checkUpdates ? "1" : "0";
                d["strategy"] = current != null ? current.FileName : "";
                d["gamefilter"] = gameMode.ToString();
                d["autoenable"] = autoEnableCheck.Checked ? "1" : "0";
                d["closeToTray"] = trayCheck.Checked ? "1" : "0";
                d["tgEnabled"] = tgCheck.Checked ? "1" : "0";
                d["ytEnabled"] = ytCheck.Checked ? "1" : "0";
                d["secureDns"] = secureDnsCheck.Checked ? "1" : "0";
                d["wasRunning"] = IsRunning ? "1" : "0";
                Core.SaveIni(d);
            }
            catch (Exception ex) { Log("Не удалось сохранить настройки: " + ex.Message); }
        }

        public bool IsRunning
        {
            get
            {
                try { return winws != null && !winws.HasExited; }
                catch (Exception) { return false; }
            }
        }

        void UpdateUi()
        {
            bool on = IsRunning;
            toggleButton.Text = on ? "ВЫКЛЮЧИТЬ" : "ВКЛЮЧИТЬ";
            toggleButton.BackColor = on ? Color.FromArgb(200, 60, 55) : Color.FromArgb(46, 160, 70);
            statusLabel.Text = on ? "Обход: включён (" + (current != null ? current.Display : "") + ")" + (tgUp ? " + VPN" : "") : "Обход: выключен";
            statusLabel.ForeColor = on ? Color.FromArgb(30, 130, 50) : Color.DimGray;
            Icon ic = on ? iconOn : iconOff;
            Icon = ic;
            tray.Icon = ic;
            tray.Text = on ? "Обход: включён" : "Обход: выключен";
            trayToggleItem.Text = on ? "Выключить обход" : "Включить обход";
            toggleButton.Enabled = !busy;
            foreach (ToolStripMenuItem mi in trayStrategyMenu.DropDownItems)
                mi.Checked = (mi.Tag == current);
        }

        void RebuildTrayStrategies()
        {
            trayStrategyMenu.DropDownItems.Clear();
            foreach (Strategy s in strategies)
            {
                ToolStripMenuItem mi = new ToolStripMenuItem(s.Display);
                mi.Tag = s;
                mi.CheckOnClick = false;
                mi.Click += delegate(object sender, EventArgs e)
                {
                    SelectStrategy((Strategy)((ToolStripMenuItem)sender).Tag);
                };
                trayStrategyMenu.DropDownItems.Add(mi);
            }
        }

        // ---------- events ----------

        void OnStrategyChanged(object sender, EventArgs e)
        {
            if (loading) return;
            Strategy s = strategyBox.SelectedItem as Strategy;
            if (s == null || s == current) return;
            ApplyStrategy(s);
        }

        void SelectStrategy(Strategy s)
        {
            if (s == current) return;
            int i = strategies.IndexOf(s);
            if (i >= 0) strategyBox.SelectedIndex = i; // triggers OnStrategyChanged
        }

        void ApplyStrategy(Strategy s)
        {
            bool wasOn = IsRunning;
            current = s;
            SaveState();
            if (wasOn)
            {
                Log("Стратегия: " + s.Display);
                Stop(true);
                Start();
            }
            UpdateUi();
        }

        void OnGameChanged(object sender, EventArgs e)
        {
            if (loading) return;
            gameMode = gameBox.SelectedIndex;
            SaveState();
            if (IsRunning)
            {
                Log("Игровой фильтр: " + gameBox.SelectedItem);
                Stop(true);
                Start();
            }
        }

        void OnAutostartClick(object sender, EventArgs e)
        {
            bool want = autostartCheck.Checked;
            bool ok = want ? CreateTask() : DeleteTask();
            if (!ok) autostartCheck.Checked = QueryTask();
        }

        void Toggle()
        {
            if (busy) return;
            if (IsRunning) Stop(false); else Start();
        }

        // ---------- log ----------

        public void Log(string msg)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\r\n";
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new MethodInvoker(delegate { AppendLog(line); })); } catch (Exception) { }
            }
            else AppendLog(line);
        }

        void AppendLog(string line)
        {
            if (logBox.TextLength > MaxLogLength)
                logBox.Text = logBox.Text.Substring(logBox.TextLength - MaxLogLength / 2);
            logBox.AppendText(line);
        }

        // ---------- winws control ----------

        void CheckService()
        {
            try
            {
                int code;
                string o = Core.RunTool("sc.exe", "query zapret", out code);
                if (o.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0)
                    Log("Предупреждение: служба zapret запущена. Удалите её на вкладке «Сервис», иначе возможен конфликт.");
            }
            catch (Exception) { }
        }

        void PrepareEnvironment()
        {
            try { Core.EnsureUserLists(); }
            catch (Exception ex) { Log("Не удалось создать пользовательские списки: " + ex.Message); }

            try
            {
                if (!Core.TimestampsEnabled())
                {
                    Core.EnableTimestamps();
                    Log("Включены TCP timestamps.");
                }
            }
            catch (Exception ex) { Log("netsh: " + ex.Message); }
        }

        void KillOthers()
        {
            foreach (Process p in Process.GetProcessesByName("winws"))
            {
                try
                {
                    Log("Найден winws.exe (PID " + p.Id + "), завершаю.");
                    p.Kill();
                    p.WaitForExit(3000);
                }
                catch (Exception ex) { Log("Не удалось завершить winws.exe: " + ex.Message); }
                finally { p.Dispose(); }
            }
        }

        void Start()
        {
            if (IsRunning) return;
            if (current == null)
            {
                MessageBox.Show(this, "Нет стратегий (general*.bat).", "Zapret GUI", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string exe = Path.Combine(Core.BinDir, "winws.exe");
            if (!File.Exists(exe))
            {
                Log("Файл не найден: " + exe);
                MessageBox.Show(this, "Файл bin\\winws.exe не найден.\nЗапускайте программу из папки zapret.", "Zapret GUI", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            busy = true;
            try
            {
                string problem;
                string args = Core.ParseBat(Path.Combine(Core.Root, current.FileName), gameMode, out problem);
                if (args == null || problem != null)
                {
                    Log("Ошибка разбора " + current.FileName + ": " + problem);
                    if (args == null) return;
                }
                PrepareEnvironment();
                KillOthers();

                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.WorkingDirectory = Core.BinDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                Process p = new Process();
                p.StartInfo = psi;
                p.EnableRaisingEvents = true;
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (!string.IsNullOrEmpty(e.Data)) Log(e.Data); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (!string.IsNullOrEmpty(e.Data)) Log(e.Data); };
                p.Exited += OnWinwsExited;
                stopRequested = false;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                winws = p;
                Log("Обход включён: " + current.Display + " (PID " + p.Id + ")");
                if (tgCheck.Checked || ytCheck.Checked) TgUpAsync();
            }
            catch (Exception ex)
            {
                Log("Ошибка запуска: " + ex.Message);
                MessageBox.Show(this, "Не удалось запустить winws.exe:\n" + ex.Message, "Zapret GUI", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                busy = false;
                SaveState();
                UpdateUi();
            }
        }

        void Stop(bool quiet)
        {
            Process p = winws;
            stopRequested = true;
            try
            {
                if (p != null && !p.HasExited)
                {
                    p.Kill();
                    p.WaitForExit(3000);
                    if (!quiet) Log("Обход выключен.");
                }
            }
            catch (Exception ex) { Log("Ошибка остановки: " + ex.Message); }
            winws = null;
            if (!quiet && !exiting) TgDownAsync();
            if (!exiting)
            {
                SaveState();
                UpdateUi();
            }
        }

        // Watchdog: called on a thread-pool thread when winws exits.
        void OnWinwsExited(object sender, EventArgs e)
        {
            Process p = sender as Process;
            if (stopRequested || exiting || p == null || p != winws) return;
            int code = -1;
            try { code = p.ExitCode; } catch (Exception) { }
            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    if (p != winws) return;
                    winws = null;
                    Log("winws.exe завершился (код " + code + ")");
                    TgDownAsync();
                    SaveState();
                    UpdateUi();
                    tray.ShowBalloonTip(5000, "Zapret GUI", "winws.exe завершился (код " + code + ")", ToolTipIcon.Warning);
                }));
            }
            catch (Exception) { }
        }

        // ---------- autostart (Task Scheduler) ----------

        bool QueryTask()
        {
            try
            {
                int code;
                Core.RunTool("schtasks.exe", "/Query /TN " + Core.TaskName, out code);
                return code == 0;
            }
            catch (Exception) { return false; }
        }

        bool CreateTask()
        {
            string tmp = null;
            try
            {
                // Random name in a directory only SYSTEM/Administrators can write: nobody can swap the XML before schtasks reads it.
                tmp = Path.Combine(Core.SecureDataDir(), Path.GetRandomFileName() + ".xml");
                string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
                string exe = SecurityElement.Escape(Application.ExecutablePath);
                string dir = SecurityElement.Escape(Core.Root);
                string xml =
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo><Description>Zapret GUI</Description></RegistrationInfo>\r\n" +
"  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>\r\n" +
"  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
"  <Settings>\r\n" +
"    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
"    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
"    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
"    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
"    <StartWhenAvailable>true</StartWhenAvailable>\r\n" +
"    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
"    <Enabled>true</Enabled>\r\n" +
"  </Settings>\r\n" +
"  <Actions Context=\"Author\"><Exec><Command>" + exe + "</Command><Arguments>--tray</Arguments><WorkingDirectory>" + dir + "</WorkingDirectory></Exec></Actions>\r\n" +
"</Task>\r\n";
                using (FileStream fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] pre = Encoding.Unicode.GetPreamble();
                    byte[] data = Encoding.Unicode.GetBytes(xml);
                    fs.Write(pre, 0, pre.Length);
                    fs.Write(data, 0, data.Length);
                }
                int code;
                string o = Core.RunTool("schtasks.exe", "/Create /TN " + Core.TaskName + " /XML \"" + tmp + "\" /F", out code);
                if (code != 0) { Log("schtasks /Create: " + o); return false; }
                Log("Автозапуск включён (задача " + Core.TaskName + ").");
                return true;
            }
            catch (Exception ex) { Log("Ошибка создания автозапуска: " + ex.Message); return false; }
            finally { try { if (tmp != null) File.Delete(tmp); } catch (Exception) { } }
        }

        bool DeleteTask()
        {
            try
            {
                int code;
                string o = Core.RunTool("schtasks.exe", "/Delete /TN " + Core.TaskName + " /F", out code);
                if (code != 0) { Log("schtasks /Delete: " + o); return false; }
                Log("Автозапуск отключён.");
                return true;
            }
            catch (Exception ex) { Log("Ошибка удаления автозапуска: " + ex.Message); return false; }
        }
    }

    // One entry of the "Списки" tab.
    class ListEntry
    {
        public string File, Title, Description;
        public bool Editable;
        public ListEntry(string file, string title, string description, bool editable)
        {
            File = file; Title = title; Description = description; Editable = editable;
        }
        public override string ToString() { return Title; }
    }

    class ListsTab : UserControl
    {
        const int MaxShownLines = 5000;

        class LoadResult
        {
            public string Text = "";
            public int Total;
            public bool Truncated;
            public bool Missing;
            public string Error;
        }

        readonly MainForm main;
        ListBox listBox;
        Label titleLabel, descLabel, countLabel;
        TextBox editor;
        Button saveBtn, saveRestartBtn, notepadBtn;
        ListEntry shown;
        int shownIndex = -1;
        int loadToken;
        bool loading;
        bool suppressSelect;
        bool dirty;
        int lastTotal;
        bool lastTruncated;

        public bool Dirty { get { return dirty; } }

        public ListsTab(MainForm owner)
        {
            main = owner;
            Padding = new Padding(8);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 2;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38f));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62f));
            root.RowCount = 1;
            Controls.Add(root);

            listBox = new ListBox();
            listBox.Dock = DockStyle.Fill;
            listBox.IntegralHeight = false;
            listBox.HorizontalScrollbar = true;
            listBox.DrawMode = DrawMode.OwnerDrawFixed;
            listBox.ItemHeight = 20;
            listBox.DrawItem += DrawListItem;
            listBox.Margin = new Padding(0, 0, 8, 0);
            listBox.Items.Add(new ListEntry("list-general-user.txt", "Мои сайты",
                "Домены для обхода, по одному в строке.", true));
            listBox.Items.Add(new ListEntry("list-exclude-user.txt", "Исключённые сайты",
                "Домены, которые обход не затрагивает.", true));
            listBox.Items.Add(new ListEntry("ipset-exclude-user.txt", "Исключённые IP / подсети",
                "IP-адреса и подсети вне обхода, например серверы игр.", true));
            listBox.Items.Add(new ListEntry("list-general.txt", "Общий список сайтов",
                "Основной список доменов из zapret.", false));
            listBox.Items.Add(new ListEntry("list-google.txt", "Список Google",
                "Домены Google и YouTube из zapret.", false));
            listBox.Items.Add(new ListEntry("list-exclude.txt", "Встроенные исключения",
                "Домены вне обхода, из zapret.", false));
            listBox.Items.Add(new ListEntry("ipset-exclude.txt", "Встроенные IP-исключения",
                "IP-подсети вне обхода, из zapret.", false));
            listBox.Items.Add(new ListEntry("ipset-all.txt", "IPSet (все адреса)",
                "IP-подсети для обхода. Режим задаётся на вкладке «Сервис».", false));
            listBox.SelectedIndexChanged += OnSelect;
            root.Controls.Add(listBox, 0, 0);

            TableLayoutPanel right = new TableLayoutPanel();
            right.Dock = DockStyle.Fill;
            right.ColumnCount = 1;
            right.RowCount = 5;
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 68f));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(right, 1, 0);

            titleLabel = new Label();
            titleLabel.AutoSize = true;
            titleLabel.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            titleLabel.Margin = new Padding(3, 0, 3, 4);
            right.Controls.Add(titleLabel, 0, 0);

            descLabel = new Label();
            descLabel.Dock = DockStyle.Fill;
            descLabel.AutoSize = false;
            descLabel.Margin = new Padding(3, 0, 3, 0);
            right.Controls.Add(descLabel, 0, 1);

            countLabel = new Label();
            countLabel.AutoSize = true;
            countLabel.ForeColor = Color.DimGray;
            countLabel.Margin = new Padding(3, 0, 3, 4);
            right.Controls.Add(countLabel, 0, 2);

            editor = new TextBox();
            editor.Multiline = true;
            editor.ScrollBars = ScrollBars.Both;
            editor.WordWrap = false;
            editor.AcceptsReturn = true;
            editor.Font = new Font("Consolas", 10f);
            editor.Dock = DockStyle.Fill;
            editor.MaxLength = int.MaxValue;
            editor.TextChanged += OnEditorChanged;
            right.Controls.Add(editor, 0, 3);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0, 6, 0, 0);
            saveBtn = MakeButton("Сохранить");
            saveBtn.Click += delegate { Save(); };
            saveRestartBtn = MakeButton("Сохранить и перезапустить обход");
            saveRestartBtn.Click += delegate
            {
                if (Save()) { if (main.IsRunning) main.RestartIfRunning(); else main.Log("Обход выключен, перезапуск не требуется."); }
            };
            notepadBtn = MakeButton("Открыть в Блокноте");
            notepadBtn.Click += delegate
            {
                if (shown == null) return;
                try { Core.OpenShell("notepad.exe", "\"" + Path.Combine(Core.ListsDir, shown.File) + "\""); }
                catch (Exception ex) { main.Log("Не удалось открыть Блокнот: " + ex.Message); }
            };
            buttons.Controls.Add(saveBtn);
            buttons.Controls.Add(saveRestartBtn);
            buttons.Controls.Add(notepadBtn);
            right.Controls.Add(buttons, 0, 4);

        }

        // Called once the main window has a handle (loading posts results back to the UI thread).
        public void Init()
        {
            try { Core.EnsureUserLists(); } catch (Exception) { }
            listBox.SelectedIndex = 0;
        }

        // Read-only files are drawn in gray.
        void DrawListItem(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0) return;
            ListEntry it = (ListEntry)listBox.Items[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            Color c = sel ? SystemColors.HighlightText : (it.Editable ? listBox.ForeColor : Color.Gray);
            TextRenderer.DrawText(e.Graphics, it.Title, listBox.Font, e.Bounds, c, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
        }

        static Button MakeButton(string text)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(6, 2, 6, 2);
            return b;
        }

        // Returns false if the user cancelled; true if it is fine to leave the current file.
        public bool ConfirmDiscard()
        {
            if (!dirty || shown == null) return true;
            DialogResult r = MessageBox.Show(this, "Сохранить изменения в «" + shown.Title + "»?", "Zapret GUI",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) return Save();
            dirty = false;
            return true;
        }

        void OnSelect(object sender, EventArgs e)
        {
            if (suppressSelect) return;
            if (listBox.SelectedIndex < 0) return;
            if (dirty && !ConfirmDiscard())
            {
                suppressSelect = true;
                listBox.SelectedIndex = shownIndex;
                suppressSelect = false;
                return;
            }
            shownIndex = listBox.SelectedIndex;
            LoadEntry((ListEntry)listBox.SelectedItem);
        }

        void OnEditorChanged(object sender, EventArgs e)
        {
            if (loading || shown == null || !shown.Editable) return;
            dirty = true;
            UpdateTitle();
            UpdateCount(editor.Lines.Length, false);
        }

        void UpdateTitle()
        {
            titleLabel.Text = shown == null ? "" : shown.Title + (dirty ? " *" : "");
        }

        void UpdateCount(int total, bool truncated)
        {
            lastTotal = total;
            lastTruncated = truncated;
            if (truncated)
                countLabel.Text = "Показаны первые " + MaxShownLines + " строк из " + total + " (только чтение)  |  " + shown.File;
            else
                countLabel.Text = "Строк: " + total + (shown.Editable ? "" : " (только чтение)") + "  |  " + shown.File;
        }

        void LoadEntry(ListEntry entry)
        {
            int token = ++loadToken;
            shown = entry;
            dirty = false;
            loading = true;
            editor.ReadOnly = true;
            editor.Text = "Загрузка...";
            UpdateTitle();
            descLabel.Text = entry.Description;
            countLabel.Text = entry.File;
            saveBtn.Enabled = false;
            saveRestartBtn.Enabled = false;
            string path = Path.Combine(Core.ListsDir, entry.File);
            bool editable = entry.Editable;
            ThreadPool.QueueUserWorkItem(delegate
            {
                LoadResult res = ReadList(path, editable);
                main.UiPost(delegate
                {
                    if (token != loadToken) return;
                    editor.Text = res.Error != null ? "Ошибка чтения: " + res.Error : res.Text;
                    editor.ReadOnly = !editable || res.Error != null;
                    loading = false;
                    dirty = false;
                    saveBtn.Enabled = editable;
                    saveRestartBtn.Enabled = editable;
                    notepadBtn.Enabled = true;
                    UpdateTitle();
                    UpdateCount(res.Total, res.Truncated);
                    if (res.Missing) countLabel.Text = "Файл не найден: " + entry.File;
                    editor.SelectionStart = 0;
                    editor.SelectionLength = 0;
                });
            });
        }

        static LoadResult ReadList(string path, bool all)
        {
            LoadResult r = new LoadResult();
            try
            {
                if (!File.Exists(path)) { r.Missing = true; return r; }
                StringBuilder sb = new StringBuilder();
                using (StreamReader sr = new StreamReader(path, Encoding.UTF8, true))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        r.Total++;
                        if (all || r.Total <= MaxShownLines) sb.Append(line).Append("\r\n");
                    }
                }
                r.Truncated = !all && r.Total > MaxShownLines;
                r.Text = sb.ToString();
            }
            catch (Exception ex) { r.Error = ex.Message; }
            return r;
        }

        bool Save()
        {
            if (shown == null || !shown.Editable) return true;
            try
            {
                string text = editor.Text.Replace("\r\n", "\n").Replace("\n", "\r\n");
                bool placeholder = text.Trim().Length == 0;
                if (placeholder) text = Core.UserListPlaceholder(shown.File);
                else if (!text.EndsWith("\r\n")) text += "\r\n";
                Directory.CreateDirectory(Core.ListsDir);
                File.WriteAllText(Path.Combine(Core.ListsDir, shown.File), text, new UTF8Encoding(false));
                dirty = false;
                if (placeholder)
                {
                    loading = true;
                    editor.Text = text;
                    loading = false;
                    MessageBox.Show(this, "Файл не должен быть пустым, записана строка-заглушка.", "Zapret GUI",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                UpdateTitle();
                UpdateCount(editor.Lines.Length, false);
                main.Log("Сохранён список: " + shown.File);
                return true;
            }
            catch (Exception ex)
            {
                main.Log("Ошибка сохранения " + shown.File + ": " + ex.Message);
                MessageBox.Show(this, "Не удалось сохранить файл:\n" + ex.Message, "Zapret GUI", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }
    }

    class Fix
    {
        public string Prompt;
        public MethodInvoker Run;
        public Fix(string prompt, MethodInvoker run) { Prompt = prompt; Run = run; }
    }

    class ServiceTab : UserControl
    {
        const string Repo = "https://github.com/boymep/zapret/";
        const string IpsetUrl = "https://raw.githubusercontent.com/boymep/zapret/main/.service/ipset-service.txt";
        const string HostsUrl = "https://raw.githubusercontent.com/boymep/zapret/main/.service/hosts";
        const string IpsetPlaceholder = "203.0.113.113/32";

        readonly MainForm main;
        Panel scroll;
        TableLayoutPanel stack;
        RichTextBox results;
        Label statusText, fakesCurrentLabel, ipsetModeLabel;
        CheckBox updatesCheck;
        RadioButton rbLoaded, rbNone, rbAny;
        ComboBox fakeTypeBox, fakeFileBox;
        readonly List<Control> ops = new List<Control>();
        readonly List<Control> wide = new List<Control>(); // controls whose width follows the panel (word wrap)
        readonly List<string> fakePaths = new List<string>();
        bool busy;

        public ServiceTab(MainForm owner)
        {
            main = owner;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 62f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 38f));
            Controls.Add(root);

            scroll = new Panel();
            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;
            scroll.SizeChanged += delegate { ApplyWidths(); };
            root.Controls.Add(scroll, 0, 0);

            stack = new TableLayoutPanel();
            stack.Dock = DockStyle.Top;
            stack.AutoSize = true;
            stack.ColumnCount = 1;
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            scroll.Controls.Add(stack);

            results = new RichTextBox();
            results.ReadOnly = true;
            results.Dock = DockStyle.Fill;
            results.Font = new Font("Consolas", 9f);
            results.BackColor = Color.White;
            results.DetectUrls = false;
            results.Margin = new Padding(3, 6, 3, 3);
            root.Controls.Add(results, 0, 1);

            BuildStatus();
            BuildRemove();
            BuildIpset();
            BuildIpsetUpdate();
            BuildHosts();
            BuildUpdates();
            BuildFakes();
            BuildDiagnostics();
            BuildTests();
        }

        // ---------- layout helpers ----------

        FlowLayoutPanel Section(string title)
        {
            GroupBox g = new GroupBox();
            g.Text = title;
            g.Dock = DockStyle.Fill;
            g.AutoSize = true;
            g.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            g.Padding = new Padding(8, 4, 8, 8);
            g.Margin = new Padding(3, 3, 3, 6);
            FlowLayoutPanel f = new FlowLayoutPanel();
            f.Dock = DockStyle.Top;
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.FlowDirection = FlowDirection.TopDown;
            f.WrapContents = false;
            g.Controls.Add(f);
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.Controls.Add(g, 0, stack.RowStyles.Count - 1);
            return f;
        }

        Label Desc(FlowLayoutPanel sec, string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Margin = new Padding(3, 0, 3, 6);
            sec.Controls.Add(l);
            wide.Add(l);
            return l;
        }

        FlowLayoutPanel Row(FlowLayoutPanel sec)
        {
            FlowLayoutPanel r = new FlowLayoutPanel();
            r.AutoSize = true;
            r.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            r.WrapContents = true;
            r.Margin = new Padding(0, 0, 0, 2);
            sec.Controls.Add(r);
            wide.Add(r);
            return r;
        }

        Button Btn(Control parent, string text, EventHandler click)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(6, 2, 6, 2);
            b.Click += click;
            parent.Controls.Add(b);
            ops.Add(b);
            return b;
        }

        void ApplyWidths()
        {
            float scale;
            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
            int w = scroll.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - (int)(52 * scale);
            if (w < 120) w = 120;
            foreach (Control c in wide) c.MaximumSize = new Size(w, 0);
        }

        // ---------- output / async ----------

        void Out(string line)
        {
            main.Log(line);
            main.UiPost(delegate { AppendResult(line); });
        }

        void AppendResult(string line)
        {
            Color c = Color.Black;
            if (line.StartsWith("[OK]")) c = Color.FromArgb(30, 130, 50);
            else if (line.StartsWith("[!]")) c = Color.FromArgb(185, 120, 0);
            else if (line.StartsWith("[X]")) c = Color.FromArgb(190, 40, 40);
            else if (line.StartsWith("==")) c = Color.FromArgb(40, 80, 160);
            if (results.TextLength > 80000) results.Clear();
            results.SelectionStart = results.TextLength;
            results.SelectionLength = 0;
            results.SelectionColor = c;
            results.AppendText(line + "\r\n");
            results.SelectionColor = Color.Black;
            results.ScrollToCaret();
        }

        void SetBusy(bool value)
        {
            busy = value;
            foreach (Control c in ops) c.Enabled = !value;
            UseWaitCursor = value;
        }

        // Runs work on a pool thread with the buttons disabled; done runs on the UI thread.
        void RunAsync(string title, MethodInvoker work, MethodInvoker done)
        {
            if (busy) return;
            SetBusy(true);
            Out("== " + title + " ==");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { work(); }
                catch (Exception ex) { Out("[X] Ошибка: " + ex.Message); }
                main.UiPost(delegate
                {
                    SetBusy(false);
                    if (done != null)
                    {
                        try { done(); } catch (Exception ex) { Out("[X] Ошибка: " + ex.Message); }
                    }
                });
            });
        }

        bool Confirm(string text)
        {
            return MessageBox.Show(this, text, "Zapret GUI", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        // ---------- Windows service helpers ----------

        // null if the service does not exist, otherwise RUNNING / STOPPED / ...
        static string ServiceState(string name)
        {
            return Core.ServiceState(name);
        }

        static string StateRu(string state)
        {
            if (state == null) return "не установлена";
            if (state == "RUNNING") return "работает";
            if (state == "STOPPED") return "остановлена";
            if (state == "STOP_PENDING") return "останавливается";
            return state;
        }

        static bool DeleteService(string name)
        {
            int code;
            Core.RunTool("net.exe", "stop \"" + name + "\"", out code);
            Core.RunTool("sc.exe", "delete \"" + name + "\"", out code);
            return code == 0;
        }

        static bool WinwsRunning()
        {
            Process[] p = Process.GetProcessesByName("winws");
            bool r = p.Length > 0;
            foreach (Process x in p) x.Dispose();
            return r;
        }

        static void KillByName(string name)
        {
            foreach (Process p in Process.GetProcessesByName(name))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch (Exception) { }
                finally { p.Dispose(); }
            }
        }

        // ---------- 1. State ----------

        void BuildStatus()
        {
            FlowLayoutPanel s = Section("Состояние");
            statusText = Desc(s, "Загрузка...");
            Btn(Row(s), "Обновить", delegate { RefreshAll(); });
        }

        void RefreshAll()
        {
            RunAsync("Обновление состояния", delegate
            {
                string zapret = ServiceState("zapret");
                string divert = ServiceState("WinDivert");
                string bfe = ServiceState("BFE");
                bool winws = WinwsRunning();
                bool ts = false;
                try { ts = Core.TimestampsEnabled(); } catch (Exception) { }
                string sb = "Служба zapret: " + StateRu(zapret) + "\n" +
                    "Служба WinDivert: " + StateRu(divert) + "\n" +
                    "Процесс winws.exe: " + (winws ? "запущен" : "не запущен") + "\n" +
                    "TCP timestamps: " + (ts ? "включены" : "выключены") + "\n" +
                    "Base Filtering Engine (BFE): " + StateRu(bfe) + "\n" + SecureDns.StatusText();
                string mode = IpsetMode();
                int fakeCount = ReadFakes();
                main.UiPost(delegate
                {
                    statusText.Text = sb;
                    rbLoaded.Checked = mode == "loaded";
                    rbNone.Checked = mode == "none";
                    rbAny.Checked = mode == "any";
                    ipsetModeLabel.Text = "Текущий режим: " + IpsetModeRu(mode);
                });
                Out("[OK] Состояние обновлено.");
                if (zapret == "RUNNING") Out("[!] Служба zapret: запущена. Удалите её, иначе возможен конфликт.");
            }, null);
        }

        // ---------- 2. Remove services ----------

        void BuildRemove()
        {
            FlowLayoutPanel s = Section("Службы");
            Desc(s, "Установка службы не требуется: её заменяет автозапуск. " +
                "Кнопка удаляет ранее установленные службы.");
            Btn(Row(s), "Удалить службы", delegate
            {
                if (!Confirm("Будут удалены службы zapret и WinDivert и завершён winws.exe. Продолжить?")) return;
                main.StopBypass();
                RunAsync("Удаление служб", delegate
                {
                    string st = ServiceState("zapret");
                    if (st == null) Out("[OK] Служба zapret не установлена.");
                    else Out(DeleteService("zapret") ? "[OK] Служба zapret удалена." : "[X] Не удалось удалить службу zapret.");
                    if (WinwsRunning()) { KillByName("winws"); Out("[OK] Процесс winws.exe завершён."); }
                    if (ServiceState("WinDivert") != null)
                    {
                        DeleteService("WinDivert");
                        Out(ServiceState("WinDivert") == null ? "[OK] Служба WinDivert удалена." : "[X] Не удалось удалить службу WinDivert.");
                    }
                    DeleteService("WinDivert14");
                    Out("[OK] Готово.");
                }, null);
            });
        }

        // ---------- 3. IPSet mode ----------

        static string IpsetFile { get { return Path.Combine(Core.ListsDir, "ipset-all.txt"); } }

        static string IpsetMode()
        {
            try
            {
                FileInfo fi = new FileInfo(IpsetFile);
                if (!fi.Exists || fi.Length == 0) return "any";
                using (StreamReader sr = new StreamReader(fi.FullName, Encoding.UTF8, true))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                        if (line.IndexOf(IpsetPlaceholder, StringComparison.Ordinal) >= 0) return "none";
                }
                return "loaded";
            }
            catch (Exception) { return "any"; }
        }

        static string IpsetModeRu(string m)
        {
            if (m == "loaded") return "список";
            if (m == "none") return "заглушка";
            return "любые IP";
        }

        RadioButton Radio(FlowLayoutPanel s, string text, string desc)
        {
            RadioButton r = new RadioButton();
            r.Text = text;
            r.AutoSize = true;
            r.Margin = new Padding(3, 2, 3, 0);
            s.Controls.Add(r);
            ops.Add(r);
            Label l = Desc(s, desc);
            l.Margin = new Padding(24, 0, 3, 4);
            l.ForeColor = Color.DimGray;
            return r;
        }

        void BuildIpset()
        {
            FlowLayoutPanel s = Section("Режим IPSet");
            ipsetModeLabel = Desc(s, "Текущий режим: ...");
            rbLoaded = Radio(s, "Список (loaded)", "Обход для IP-адресов из ipset-all.txt. Обычный режим.");
            rbNone = Radio(s, "Заглушка (none)", "В файле одна заглушка, обход только по доменам. Список сохраняется в резервную копию.");
            rbAny = Radio(s, "Любые IP (any)", "Пустой файл: обход для всех IP-адресов. Нужна резервная копия, чтобы вернуться.");
            Btn(Row(s), "Применить", delegate { ApplyIpsetMode(); });
        }

        void ApplyIpsetMode()
        {
            string target = rbLoaded.Checked ? "loaded" : (rbNone.Checked ? "none" : "any");
            RunAsync("IPSet: режим " + IpsetModeRu(target), delegate
            {
                string cur = IpsetMode();
                string list = IpsetFile;
                string backup = list + ".backup";
                if (cur == target) { Out("[OK] Режим уже выбран."); return; }
                if (target == "loaded")
                {
                    if (!File.Exists(backup)) { Out("[X] Нет резервной копии. Сначала обновите список IPSet."); return; }
                    if (File.Exists(list)) File.Delete(list);
                    File.Move(backup, list);
                }
                else
                {
                    if (cur == "loaded")
                    {
                        if (File.Exists(backup)) File.Delete(backup);
                        File.Move(list, backup);
                    }
                    File.WriteAllText(list, target == "none" ? IpsetPlaceholder + "\r\n" : "");
                }
                Out("[OK] Режим IPSet: " + IpsetModeRu(target));
                main.UiPost(delegate { main.RestartIfRunning(); });
            }, delegate { RefreshAll(); });
        }

        // ---------- 4. IPSet update ----------

        void BuildIpsetUpdate()
        {
            FlowLayoutPanel s = Section("Список IPSet");
            Desc(s, "Загрузка актуального ipset-all.txt из репозитория zapret.");
            Btn(Row(s), "Обновить список IPSet", delegate
            {
                RunAsync("Обновление списка IPSet", delegate
                {
                    Out("Загрузка...");
                    byte[] data = Core.Download(IpsetUrl, 15000);
                    if (data.Length == 0) { Out("[X] Получен пустой файл."); return; }
                    Directory.CreateDirectory(Core.ListsDir);
                    File.WriteAllBytes(IpsetFile, data);
                    Out("[OK] Список обновлён (" + (data.Length / 1024) + " КБ).");
                    main.UiPost(delegate { main.RestartIfRunning(); });
                }, delegate { RefreshAll(); });
            });
        }

        // ---------- 5. Hosts ----------

        void BuildHosts()
        {
            FlowLayoutPanel s = Section("Файл hosts");
            Desc(s, "Сравнение hosts с эталонным файлом из репозитория zapret. " +
                "При расхождении открываются Блокнот и папка hosts.");
            Btn(Row(s), "Проверить hosts", delegate
            {
                if (!Confirm("Эталонный hosts будет загружен и сравнён с системным (C:\\Windows\\System32\\drivers\\etc\\hosts).\n" +
                    "При расхождении откроются Блокнот и папка hosts, изменения вносятся вручную.\n" +
                    "\nПродолжить?")) return;
                string temp = Path.Combine(Path.GetTempPath(), "zapret_hosts.txt");
                string hostsFile = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? "C:\\Windows", "System32\\drivers\\etc\\hosts");
                bool needs = false;
                RunAsync("Проверка hosts", delegate
                {
                    string url = HostsUrl + "?t=" + new Random().Next(100000000, 999999999);
                    byte[] data = Core.Download(url, 15000);
                    File.WriteAllBytes(temp, data);
                    string[] lines = Array.FindAll(File.ReadAllLines(temp, Encoding.UTF8), delegate(string x) { return x.Trim().Length > 0; });
                    if (lines.Length == 0) { Out("[X] Скачанный файл пуст."); return; }
                    string hosts = File.Exists(hostsFile) ? File.ReadAllText(hostsFile) : "";
                    if (hosts.IndexOf(lines[0], StringComparison.Ordinal) < 0) { Out("[!] В hosts нет первой строки эталона."); needs = true; }
                    if (hosts.IndexOf(lines[lines.Length - 1], StringComparison.Ordinal) < 0) { Out("[!] В hosts нет последней строки эталона."); needs = true; }
                    if (needs) Out("[!] hosts устарел. Скопируйте записи из Блокнота в hosts.");
                    else { Out("[OK] hosts актуален."); File.Delete(temp); }
                }, delegate
                {
                    if (!needs) return;
                    try
                    {
                        Core.OpenShell("notepad.exe", "\"" + temp + "\"");
                        Core.OpenShell("explorer.exe", "/select,\"" + hostsFile + "\"");
                    }
                    catch (Exception ex) { Out("[X] Не удалось открыть файлы: " + ex.Message); }
                });
            });
        }

        // ---------- 6. Updates and WARP account ----------

        void BuildUpdates()
        {
            FlowLayoutPanel s = Section("Обновления и WARP");
            Desc(s, "Установка обновлений zapret выполняется с вкладки «Главная» (кнопка «Проверить обновления»).");
            Btn(Row(s), "Проверить обновления", delegate { main.CheckForUpdates(); });
            updatesCheck = new CheckBox();
            updatesCheck.Text = "Проверять обновления при запуске";
            updatesCheck.AutoSize = true;
            updatesCheck.Margin = new Padding(3, 4, 3, 3);
            updatesCheck.Checked = main.CheckUpdatesEnabled;
            updatesCheck.Click += delegate
            {
                main.CheckUpdatesEnabled = updatesCheck.Checked;
                Out("[OK] Проверка при запуске: " + (updatesCheck.Checked ? "включена." : "выключена."));
            };
            s.Controls.Add(updatesCheck);
            Desc(s, "Для VPN (Telegram, YouTube) у каждого пользователя свой аккаунт Cloudflare WARP (создаётся автоматически).");
            Btn(Row(s), "Сбросить WARP-аккаунт", delegate
            {
                main.ResetWarp();
                Out("[OK] Запрошен сброс WARP-аккаунта.");
            });
        }

        public void StartupRefresh()
        {
            RefreshAll();
        }

        // ---------- 7. Replace active fakes ----------

        void BuildFakes()
        {
            FlowLayoutPanel s = Section("Активные фейки");
            Desc(s, "Файлы ACTIVE_DISCORD_UDP.bin и ACTIVE_GAME_UDP.bin можно заменить другим .bin из папки bin.");
            fakesCurrentLabel = Desc(s, "Текущие: ...");
            FlowLayoutPanel row = Row(s);
            fakeTypeBox = new ComboBox();
            fakeTypeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            fakeTypeBox.Items.AddRange(new object[] { "Discord UDP", "GameFilter UDP" });
            fakeTypeBox.SelectedIndex = 0;
            fakeTypeBox.Width = 130;
            fakeFileBox = new ComboBox();
            fakeFileBox.DropDownStyle = ComboBoxStyle.DropDownList;
            fakeFileBox.Width = 230;
            row.Controls.Add(fakeTypeBox);
            row.Controls.Add(fakeFileBox);
            ops.Add(fakeTypeBox);
            ops.Add(fakeFileBox);
            Btn(row, "Заменить", delegate { ReplaceFake(); });
        }

        static string Sha256(string path)
        {
            using (FileStream fs = File.OpenRead(path))
            using (System.Security.Cryptography.SHA256 h = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(fs));
        }

        // Fills the fake list; runs on a worker thread, updates the UI through UiPost. Returns the file count.
        int ReadFakes()
        {
            string bin = Core.BinDir;
            List<string> names = new List<string>();
            List<string> paths = new List<string>();
            List<string> hashes = new List<string>();
            string curDiscord = "(не найден)", curGame = "(не найден)";
            try
            {
                if (Directory.Exists(bin))
                {
                    string dh = null, gh = null;
                    string f1 = Path.Combine(bin, "ACTIVE_DISCORD_UDP.bin");
                    string f2 = Path.Combine(bin, "ACTIVE_GAME_UDP.bin");
                    if (File.Exists(f1)) dh = Sha256(f1);
                    if (File.Exists(f2)) gh = Sha256(f2);
                    string[] files = Directory.GetFiles(bin, "*.bin");
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    foreach (string f in files)
                    {
                        string n = Path.GetFileNameWithoutExtension(f);
                        if (n.StartsWith("ACTIVE_", StringComparison.OrdinalIgnoreCase)) continue;
                        string h = Sha256(f);
                        names.Add(n); paths.Add(f); hashes.Add(h);
                        if (dh != null && h == dh) curDiscord = n;
                        if (gh != null && h == gh) curGame = n;
                    }
                }
            }
            catch (Exception ex) { Out("[X] Не удалось прочитать фейки: " + ex.Message); }
            main.UiPost(delegate
            {
                fakePaths.Clear();
                fakePaths.AddRange(paths);
                fakeFileBox.Items.Clear();
                foreach (string n in names) fakeFileBox.Items.Add(n);
                if (fakeFileBox.Items.Count > 0) fakeFileBox.SelectedIndex = 0;
                fakesCurrentLabel.Text = "Discord UDP: " + curDiscord + "; GameFilter UDP: " + curGame;
            });
            return names.Count;
        }

        void ReplaceFake()
        {
            int idx = fakeFileBox.SelectedIndex;
            if (idx < 0 || idx >= fakePaths.Count) { Out("[X] Файл не выбран."); return; }
            string src = fakePaths[idx];
            bool discord = fakeTypeBox.SelectedIndex == 0;
            string dst = Path.Combine(Core.BinDir, discord ? "ACTIVE_DISCORD_UDP.bin" : "ACTIVE_GAME_UDP.bin");
            if (!Confirm("Заменить " + (discord ? "ACTIVE_DISCORD_UDP.bin" : "ACTIVE_GAME_UDP.bin") + " файлом «" + Path.GetFileName(src) + "»?")) return;
            RunAsync("Замена активного фейка", delegate
            {
                if (File.Exists(dst)) File.Delete(dst);
                File.Copy(src, dst, true);
                Out("[OK] Активный фейк заменён: " + Path.GetFileName(src));
                main.UiPost(delegate { main.RestartIfRunning(); });
            }, delegate { RefreshAll(); });
        }

        // ---------- 8. Diagnostics ----------

        void BuildDiagnostics()
        {
            FlowLayoutPanel s = Section("Диагностика");
            Desc(s, "Проверка служб-конфликтов, прокси, DNS, hosts и пути установки. " +
                "Для исправимых проблем предлагается исправление.");
            FlowLayoutPanel row = Row(s);
            Btn(row, "Запустить диагностику", delegate { RunDiagnostics(); });
            Btn(row, "Очистить кэш Discord", delegate { ClearDiscord(); });
        }

        void RunDiagnostics()
        {
            List<Fix> fixes = new List<Fix>();
            RunAsync("Диагностика", delegate { Diagnose(fixes); }, delegate
            {
                List<Fix> chosen = new List<Fix>();
                foreach (Fix f in fixes) if (Confirm(f.Prompt)) chosen.Add(f);
                if (chosen.Count == 0) return;
                RunAsync("Исправление проблем", delegate { foreach (Fix f in chosen) f.Run(); Out("[OK] Готово."); }, null);
            });
        }

        void Diagnose(List<Fix> fixes)
        {
            Out("[OK] Папка zapret: " + Core.Root);

            string bfe = ServiceState("BFE");
            if (bfe == "RUNNING") Out("[OK] Base Filtering Engine");
            else Out("[X] Base Filtering Engine: служба не запущена. Запустите её, без неё zapret не работает.");

            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    object en = k == null ? null : k.GetValue("ProxyEnable");
                    if (en != null && Convert.ToInt32(en) == 1)
                    {
                        Out("[!] Системный прокси: включён (" + k.GetValue("ProxyServer") + "). Отключите, если он не нужен.");
                        
                    }
                    else Out("[OK] Системный прокси");
                }
            }
            catch (Exception ex) { Out("[!] Системный прокси: не удалось проверить. " + ex.Message); }

            bool ts = Core.TimestampsEnabled();
            if (ts) Out("[OK] TCP timestamps");
            else
            {
                Out("[!] TCP timestamps: выключены. Включите.");
                fixes.Add(new Fix("Будут включены TCP timestamps. Продолжить?", delegate
                {
                    Out(Core.EnableTimestamps() ? "[OK] TCP timestamps включены." : "[X] Не удалось включить TCP timestamps.");
                }));
            }

            int code;
            string scAll = Core.RunTool("sc.exe", "query", out code);
            string[] scLines = scAll.Split('\n');

            Process[] ag = Process.GetProcessesByName("AdguardSvc");
            if (ag.Length > 0) Out("[X] Adguard: процесс найден. Он нарушает работу Discord, закройте или удалите Adguard (https://github.com/Flowseal/zapret-discord-youtube/issues/417).");
            else Out("[OK] Adguard");

            if (scAll.IndexOf("Killer", StringComparison.OrdinalIgnoreCase) >= 0)
                Out("[X] Killer: службы найдены. Конфликтуют с zapret, отключите или удалите.");
            else Out("[OK] Killer");

            bool intel = false;
            foreach (string l in scLines)
                if (Regex.IsMatch(l, "Intel", RegexOptions.IgnoreCase) && Regex.IsMatch(l, "Connectivity", RegexOptions.IgnoreCase) && Regex.IsMatch(l, "Network", RegexOptions.IgnoreCase)) intel = true;
            if (intel) Out("[X] Intel Connectivity Network Service: служба найдена. Конфликтует с zapret, отключите её.");
            else Out("[OK] Intel Connectivity Network Service");

            if (scAll.IndexOf("TracSrvWrapper", StringComparison.OrdinalIgnoreCase) >= 0 || scAll.IndexOf("EPWD", StringComparison.OrdinalIgnoreCase) >= 0)
                Out("[X] Check Point: службы найдены. Конфликтуют с zapret, удалите Check Point.");
            else Out("[OK] Check Point");

            if (scAll.IndexOf("SmartByte", StringComparison.OrdinalIgnoreCase) >= 0)
                Out("[X] SmartByte: службы найдены. Конфликтуют с zapret, отключите в services.msc.");
            else Out("[OK] SmartByte");

            if (Regex.IsMatch(Core.Root, "[\u0430-\u044F\u0410-\u042F\u0451\u0401]"))
            {
                Out("[!] Путь установки: есть кириллица. Если обход не работает, перенесите zapret, например в C:\\zapret.");
                
            }
            else Out("[OK] Путь установки");

            string od = Environment.GetEnvironmentVariable("OneDrive");
            if (!string.IsNullOrEmpty(od) && (Core.Root + "\\").StartsWith(od + "\\", StringComparison.OrdinalIgnoreCase))
            {
                Out("[X] OneDrive: zapret лежит в папке OneDrive. Перенесите его, например в C:\\zapret.");
                
            }
            else Out("[OK] OneDrive");

            bool sys = false;
            try { sys = Directory.Exists(Core.BinDir) && Directory.GetFiles(Core.BinDir, "*.sys").Length > 0; } catch (Exception) { }
            if (sys) Out("[OK] WinDivert64.sys");
            else Out("[X] WinDivert64.sys: файл не найден в папке bin. Восстановите файлы zapret.");

            List<string> vpn = new List<string>();
            foreach (string l in scLines)
            {
                if (l.IndexOf("VPN", StringComparison.OrdinalIgnoreCase) < 0) continue;
                int c = l.IndexOf(':');
                if (c >= 0) { string v = l.Substring(c + 1).Trim(); if (v.Length > 0 && !vpn.Contains(v)) vpn.Add(v); }
            }
            if (vpn.Count > 0)
            {
                Out("[!] VPN: найдены службы (" + string.Join(", ", vpn.ToArray()) + "). Могут конфликтовать с zapret, отключите VPN.");
                
            }
            else Out("[OK] VPN");

            if (HasDoh()) Out("[OK] Защищённый DNS");
            else
            {
                Out("[!] Защищённый DNS: не настроен. Включите DoH в браузере или в параметрах Windows.");
                
            }

            try
            {
                string hostsFile = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? "C:\\Windows", "System32\\drivers\\etc\\hosts");
                if (File.Exists(hostsFile))
                {
                    string h = File.ReadAllText(hostsFile);
                    if (h.IndexOf("youtube.com", StringComparison.OrdinalIgnoreCase) >= 0 || h.IndexOf("youtu.be", StringComparison.OrdinalIgnoreCase) >= 0)
                        Out("[!] hosts: есть записи youtube.com / youtu.be. Они мешают доступу к YouTube, удалите их.");
                    else Out("[OK] hosts");
                }
            }
            catch (Exception ex) { Out("[!] hosts: не удалось прочитать. " + ex.Message); }

            string wd = ServiceState("WinDivert");
            if (!WinwsRunning() && (wd == "RUNNING" || wd == "STOP_PENDING"))
            {
                Out("[!] WinDivert: служба активна при остановленном winws.exe. Удалите службу.");
                fixes.Add(new Fix("Будет удалена служба WinDivert (при необходимости и GoodbyeDPI). Продолжить?", delegate
                {
                    DeleteService("WinDivert");
                    if (ServiceState("WinDivert") == null) { Out("[OK] Служба WinDivert удалена."); return; }
                    Out("[X] Не удалось удалить службу WinDivert. Поиск конфликтующих служб...");
                    if (ServiceState("GoodbyeDPI") == null)
                    {
                        Out("[X] Конфликтующих служб нет. Проверьте, не использует ли WinDivert другая программа.");
                        return;
                    }
                    Out(DeleteService("GoodbyeDPI") ? "[OK] Служба GoodbyeDPI удалена." : "[X] Не удалось удалить службу GoodbyeDPI.");
                    DeleteService("WinDivert");
                    Out(ServiceState("WinDivert") == null ? "[OK] Служба WinDivert удалена." : "[X] Службу WinDivert по-прежнему не удалить.");
                }));
            }

            List<string> found = new List<string>();
            foreach (string n in new string[] { "GoodbyeDPI", "discordfix_zapret", "winws1", "winws2" })
                if (ServiceState(n) != null) found.Add(n);
            if (found.Count > 0)
            {
                string names = string.Join(" ", found.ToArray());
                Out("[X] Другие обходы: найдены службы " + names + ". Удалите их.");
                fixes.Add(new Fix("Будут удалены службы: " + names + ". Продолжить?", delegate
                {
                    foreach (string n in found)
                        Out(DeleteService(n) ? "[OK] Служба удалена: " + n : "[X] Не удалось удалить службу: " + n);
                    DeleteService("WinDivert");
                    DeleteService("WinDivert14");
                }));
            }
            else Out("[OK] Другие обходы");
        }

        static bool HasDoh()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters"))
                    return k != null && DohIn(k);
            }
            catch (Exception) { return false; }
        }

        static bool DohIn(Microsoft.Win32.RegistryKey key)
        {
            object v = key.GetValue("DohFlags");
            try { if (v != null && Convert.ToInt64(v) > 0) return true; } catch (Exception) { }
            foreach (string sub in key.GetSubKeyNames())
                using (Microsoft.Win32.RegistryKey s = key.OpenSubKey(sub))
                    if (s != null && DohIn(s)) return true;
            return false;
        }

        void ClearDiscord()
        {
            if (!Confirm("Будет очищен кэш Discord (Stable, PTB, Canary, Development) и закрыт запущенный Discord. Продолжить?")) return;
            RunAsync("Очистка кэша Discord", delegate
            {
                string appdata = Environment.GetEnvironmentVariable("APPDATA") ?? "";
                string[,] items = new string[,]
                {
                    { "Discord", "Discord", "discord" },
                    { "DiscordPTB", "Discord PTB", "discordptb" },
                    { "DiscordCanary", "Discord Canary", "discordcanary" },
                    { "DiscordDevelopment", "Discord Development", "discorddevelopment" }
                };
                bool any = false;
                for (int i = 0; i < items.GetLength(0); i++)
                {
                    string dir = Path.Combine(appdata, items[i, 2]);
                    if (!Directory.Exists(dir)) continue;
                    any = true;
                    if (Process.GetProcessesByName(items[i, 0]).Length > 0)
                    {
                        KillByName(items[i, 0]);
                        Out("[OK] " + items[i, 1] + " закрыт.");
                    }
                    foreach (string sub in new string[] { "Cache", "Code Cache", "GPUCache" })
                    {
                        string p = Path.Combine(dir, sub);
                        if (!Directory.Exists(p)) { Out("[!] Нет папки: " + p); continue; }
                        try { Directory.Delete(p, true); Out("[OK] Удалено: " + p); }
                        catch (Exception ex) { Out("[X] Не удалось удалить " + p + ": " + ex.Message); }
                    }
                }
                if (!any) Out("[X] Discord не найден.");
            }, null);
        }

        // ---------- 9. Tests ----------

        void BuildTests()
        {
            FlowLayoutPanel s = Section("Тесты");
            Desc(s, "Интерактивная проверка стратегий (utils\\test zapret.ps1) в окне PowerShell.");
            Btn(Row(s), "Запустить тесты", delegate
            {
                string script = Path.Combine(Core.Root, "utils\\test zapret.ps1");
                if (!File.Exists(script)) { Out("[X] Не найден файл: " + script); return; }
                try
                {
                    Core.OpenShell("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"");
                    Out("[OK] Тесты запущены в окне PowerShell.");
                }
                catch (Exception ex) { Out("[X] Не удалось запустить PowerShell: " + ex.Message); }
            });
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] argv)
        {
            Core.Root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');

            bool tray = false, afterUpdate = false, resume = false;
            int waitPid = 0;
            for (int i = 0; i < argv.Length; i++)
            {
                if (argv[i] == "--tray") tray = true;
                else if (argv[i] == "--after-update") afterUpdate = true;
                else if (argv[i] == "--resume") resume = true;
                else if (argv[i] == "--wait-pid" && i + 1 < argv.Length) int.TryParse(argv[++i], out waitPid);
            }

            // Started by the updater: let the old instance finish exiting before taking the single-instance mutex.
            if (waitPid > 0)
            {
                try
                {
                    using (Process old = Process.GetProcessById(waitPid)) old.WaitForExit(30000);
                }
                catch (Exception) { }
            }

            bool createdNew;
            Mutex mutex = new Mutex(true, MutexNameStatic, out createdNew);
            EventWaitHandle evt = new EventWaitHandle(false, EventResetMode.AutoReset, "ZapretGUI_ShowWindow_Event");
            if (!createdNew)
            {
                evt.Set();
                return 0;
            }

            Updater.CleanOld(Core.Root);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new MainForm(evt, tray, afterUpdate, resume));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Критическая ошибка: " + ex.Message, "Zapret GUI", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            GC.KeepAlive(mutex);
            return 0;
        }

        const string MutexNameStatic = "ZapretGUI_SingleInstance_Mutex";
    }
}
