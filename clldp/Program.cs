using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;

namespace LLDPParser
{
    class Program
    {
        private static readonly string TempDirectory = Path.Combine("c:", "temp");
        private static readonly string EtlFilePath = Path.Combine(TempDirectory, "lldp.etl");
        private static readonly string TxtFilePath = Path.Combine(TempDirectory, "lldp.txt");

        static void Main(string[] args)
        {
            // Check for help argument
            if (args.Contains("/help") || args.Contains("/?"))
            {
                ShowHelp();
                return; // Exit after showing help
            }

            // Check for unsupported arguments
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] != "-debug" && args[i] != "-t" && args[i] != "-p" && args[i] != "/help" && args[i] != "/?")
                {
                    // If the argument is not "-t" or "-p" and is not a valid argument, show error
                    if ((i == 0 || args[i - 1] != "-t") && (i == 0 || args[i - 1] != "-p"))
                    {
                        Console.WriteLine("Unsupported argument(s) detected.");
                        ShowHelp();
                        return;
                    }
                }
            }

            bool debugMode = args.Contains("-debug");

            // Parse optional -p argument
            string portArg = null;
            int pIndex = Array.IndexOf(args, "-p");
            if (pIndex != -1 && pIndex + 1 < args.Length)
            {
                portArg = args[pIndex + 1];
            }

            // Set default capture duration
            int captureDuration = 30;
            // Check if a custom duration is provided
            int tIndex = Array.IndexOf(args, "-t");
            if (tIndex != -1 && tIndex + 1 < args.Length && int.TryParse(args[tIndex + 1], out int parsedDuration))
            {
                captureDuration = ValidateCaptureDuration(parsedDuration);
            }

            try
            {
                // Check if running as administrator (required for pktmon)
                if (!IsRunningAsAdministrator())
                {
                    Console.WriteLine("Error: This application requires administrator privileges.");
                    Console.WriteLine("Please right-click and select 'Run as administrator'.");
                    return;
                }

                CleanUp(debugMode);
                EnsureDirectoryExists(TempDirectory);

                var compIDs = GetComponentIDs();

                if (compIDs.Count > 0)
                {
                    string selectedCompID = GetUserComponentSelection(compIDs);

                    if (!string.IsNullOrEmpty(selectedCompID))
                    {
                        bool captureSuccessful = false;
                        
                        while (!captureSuccessful)
                        {
                            CaptureLldpData(selectedCompID, captureDuration);
                            var lldpData = ParseLldpData(TxtFilePath);

                            if (lldpData.Count == 0)
                            {
                                Console.WriteLine("\nNo LLDP data captured. This may occur if no LLDP packets were transmitted during the capture window.");
                                Console.Write("Would you like to retry the capture? (Y/N): ");
                                
                                string response = Console.ReadLine()?.Trim().ToUpper();
                                
                                if (response == "Y" || response == "YES")
                                {
                                    Console.WriteLine("Retrying capture with the same configuration...\n");
                                    CleanUp(debugMode);
                                    continue;
                                }
                                else
                                {
                                    Console.WriteLine("Capture cancelled.");
                                    break;
                                }
                            }
                            else
                            {
                                captureSuccessful = true;
                                DisplayLldpData(lldpData);

                                // If -p was provided, also write out results to a new file
                                if (!string.IsNullOrEmpty(portArg))
                                {
                                    string outPath = Path.Combine(
                                        TempDirectory,
                                        $"CLLDP_{DateTime.Now:yyyyMMddHHmmss}_{portArg}.txt"
                                    );
                                    WriteLldpDataToFile(lldpData, outPath);
                                }
                            }
                        }
                    }
                }
                else
                {
                    Console.WriteLine("No ethernet adapters found to capture on.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}");
            }
            finally
            {
                CleanUp(debugMode);
            }
        }

        static int ValidateCaptureDuration(int duration)
        {
            if (duration < 30 || duration > 60)
            {
                Console.WriteLine($"Invalid capture duration: {duration} seconds. Duration must be between 30 and 60 seconds.");
                Console.WriteLine("Setting capture duration to default value of 30 seconds.");
                return 30;
            }
            return duration;
        }

        static bool IsRunningAsAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        static void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
            {
                try
                {
                    Directory.CreateDirectory(path);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: Failed to create directory {path}: {ex.Message}");
                    throw;
                }
            }
        }

        static void CaptureLldpData(string selectedCompID, int durationInSeconds)
        {
            ExecutePktmonCommand("filter add --ethertype 0x88cc");
            ExecutePktmonCommand($"start --capture --comp {selectedCompID} --pkt-size 0 -f {EtlFilePath}");

            DateTime endTime = DateTime.Now.AddSeconds(durationInSeconds);
            while (DateTime.Now < endTime)
            {
                int remainingSeconds = (int)(endTime - DateTime.Now).TotalSeconds;
                Console.Write($"\rCapturing... {remainingSeconds} seconds remaining ");
                Thread.Sleep(1000);
            }

            Console.WriteLine();
            ExecutePktmonCommand("stop");
            ExecutePktmonCommand($"format {EtlFilePath} -o {TxtFilePath} -v");
        }

        static List<string> GetComponentIDs()
        {
            var compIDs = new List<string>();
            string output = ExecutePktmonCommand("list");

            if (!string.IsNullOrEmpty(output))
            {
                string[] lines = output.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
                bool dataSectionStarted = false;

                foreach (var line in lines)
                {
                    if (!dataSectionStarted)
                    {
                        if (line.Contains("Address       Name"))
                        {
                            dataSectionStarted = true;
                        }
                        continue;
                    }

                    if (line.StartsWith("--") || line.Contains("--")) continue;

                    if (line.Trim().Length > 0)
                    {
                        string[] parts = line.Trim().Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3)
                        {
                            string compID = parts[0].Trim();
                            string name = parts[2].Trim();

                            if (!name.ToLower().Contains("bluetooth") &&
                                !name.ToLower().Contains("wireless") &&
                                !name.ToLower().Contains("mobile broadband") &&
                                !name.ToLower().Contains("wi-fi"))
                            {
                                compIDs.Add($"{compID}|{name}"); // Store both ID and name
                            }
                        }
                    }
                }
            }

            return compIDs;
        }

        static string GetUserComponentSelection(List<string> compIDs)
        {
            if (compIDs.Count == 0)
            {
                return null;
            }

            Console.WriteLine("\nAvailable Network Adapters:");
            Console.WriteLine("----------------------------");
            
            // Display all adapters with their names
            for (int i = 0; i < compIDs.Count; i++)
            {
                string[] parts = compIDs[i].Split('|');
                string compID = parts[0];
                string name = parts.Length > 1 ? parts[1] : "Unknown";
                
                if (i == 0)
                {
                    Console.Write($"  {compID} - {name} [");
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write("SUGGESTED");
                    Console.ResetColor();
                    Console.WriteLine("]");
                }
                else
                {
                    Console.WriteLine($"  {compID} - {name}");
                }
            }
            
            string suggestedCompID = compIDs[0].Split('|')[0];
            
            Console.WriteLine("\nPress Enter to use the suggested adapter, or type a Component ID to use a different one:");
            
            while (true)
            {
                string input = Console.ReadLine();
                
                // If user just pressed Enter, use the suggestion
                if (string.IsNullOrEmpty(input))
                {
                    Console.WriteLine($"Using adapter: {suggestedCompID}");
                    return suggestedCompID;
                }
                
                // Check if user entered a valid component ID
                foreach (var item in compIDs)
                {
                    string compID = item.Split('|')[0];
                    if (compID == input)
                    {
                        Console.WriteLine($"Using adapter: {input}");
                        return input;
                    }
                }
                
                Console.WriteLine("Invalid Component ID. Please try again or press Enter to use the suggested adapter:");
            }
        }

        static string ExecutePktmonCommand(string arguments)
        {
            try
            {
                var startInfo = new ProcessStartInfo("pktmon", arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return null;
                    }
                    
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    
                    // Only show errors that are not benign status messages
                    if (!string.IsNullOrEmpty(error) && 
                        !error.Contains("not running") && 
                        !error.Contains("No filters") &&
                        arguments.Contains("start"))  // Only show errors during capture start
                    {
                        Console.WriteLine($"Warning: {error.Trim()}");
                    }
                    
                    return output;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                return null;
            }
        }

        static Dictionary<string, string> ParseLldpData(string filePath)
        {
            var lldpData = new Dictionary<string, string>();
            var vlanData = new List<string>();
            bool inLldpPacket = false;

            if (!File.Exists(filePath))
            {
                Console.WriteLine($"Warning: File not found: {filePath}");
                return lldpData;
            }

            using (var reader = new StreamReader(filePath))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    // Detect LLDP packet sections
                    if (line.Contains("ethertype LLDP (0x88cc)"))
                    {
                        inLldpPacket = true;
                        continue;
                    }

                    // Skip non-LLDP packet data
                    if (!inLldpPacket)
                        continue;

                    if (line.Contains("End TLV (0)"))
                        inLldpPacket = false;  // End of LLDP packet

                    // Extract Chassis ID
                    if (line.Contains("Chassis ID TLV"))
                    {
                        var nextLine = reader.ReadLine();
                        if (nextLine != null && nextLine.Contains(": "))
                        {
                            string[] parts = nextLine.Split(new[] { ": " }, 2, StringSplitOptions.None);
                            if (parts.Length > 1)
                                lldpData["Chassis ID"] = parts[1].Trim();
                        }
                    }
                    else if (line.Contains("Port ID TLV"))
                    {
                        var nextLine = reader.ReadLine();
                        if (nextLine != null && nextLine.Contains(": "))
                        {
                            string[] parts = nextLine.Split(new[] { ": " }, 2, StringSplitOptions.None);
                            if (parts.Length > 1)
                                lldpData["Port ID"] = parts[1].Trim();
                        }
                    }
                    else if (line.Contains("Time to Live TLV"))
                    {
                        if (line.Contains("TTL"))
                        {
                            int ttlIndex = line.IndexOf("TTL");
                            if (ttlIndex >= 0)
                                lldpData["Time to Live"] = line.Substring(ttlIndex).Trim();
                        }
                    }
                    else if (line.Contains("Port Description TLV"))
                    {
                        if (line.Contains(": "))
                        {
                            string[] parts = line.Split(new[] { ": " }, 2, StringSplitOptions.None);
                            if (parts.Length > 1)
                                lldpData["Port Description"] = parts[1].Trim();
                        }
                        else
                        {
                            var nextLine = reader.ReadLine();
                            if (nextLine != null)
                                lldpData["Port Description"] = nextLine.Trim();
                        }
                    }
                    else if (line.Contains("System Name TLV"))
                    {
                        if (line.Contains(": "))
                        {
                            string[] parts = line.Split(new[] { ": " }, 2, StringSplitOptions.None);
                            if (parts.Length > 1)
                                lldpData["System Name"] = parts[1].Trim();
                        }
                        else
                        {
                            var nextLine = reader.ReadLine();
                            if (nextLine != null)
                                lldpData["System Name"] = nextLine.Trim();
                        }
                    }
                    else if (line.Contains("System Description TLV"))
                    {
                        var nextLine = reader.ReadLine();
                        if (nextLine != null)
                            lldpData["System Description"] = nextLine.Trim();
                    }
                    else if (line.Contains("System Capabilities TLV"))
                    {
                        var capabilitiesLine = reader.ReadLine();
                        if (capabilitiesLine != null && capabilitiesLine.Contains(": "))
                        {
                            string[] parts = capabilitiesLine.Split(new[] { ": " }, 2, StringSplitOptions.None);
                            if (parts.Length > 1)
                                lldpData["System Capabilities"] = parts[1].Trim();
                        }

                        var enabledCapabilitiesLine = reader.ReadLine();
                        if (enabledCapabilitiesLine != null && enabledCapabilitiesLine.Contains(": "))
                        {
                            string[] parts = enabledCapabilitiesLine.Split(new[] { ": " }, 2, StringSplitOptions.None);
                            if (parts.Length > 1)
                                lldpData["Enabled Capabilities"] = parts[1].Trim();
                        }
                    }
                    else if (line.Contains("Management Address TLV"))
                    {
                        var managementAddressLine = reader.ReadLine();
                        if (managementAddressLine != null && managementAddressLine.Contains(": "))
                        {
                            string[] parts = managementAddressLine.Split(new[] { ": " }, 2, StringSplitOptions.None);
                            if (parts.Length > 1)
                                lldpData["Management Address"] = parts[1].Trim();
                        }
                    }
                    // Port VLAN ID
                    else if (line.Contains("Port VLAN Id Subtype"))
                    {
                        var nextLine = reader.ReadLine();
                        if (nextLine != null && nextLine.Contains("port vlan id (PVID):"))
                        {
                            string[] parts = nextLine.Split(new[] { ":" }, 2, StringSplitOptions.None);
                            if (parts.Length > 1)
                                lldpData["Port VLAN ID"] = parts[1].Trim();
                        }
                    }
                    // MAC/PHY Configuration
                    else if (line.Contains("MAC/PHY configuration/status Subtype"))
                    {
                        var autonegLine = reader.ReadLine();
                        var pmdLine = reader.ReadLine();
                        var mauLine = reader.ReadLine();
                        
                        if (autonegLine != null && pmdLine != null)
                        {
                            string config = $"{autonegLine.Trim()}, {pmdLine.Trim()}";
                            if (mauLine != null)
                                config += $", {mauLine.Trim()}";
                            
                            lldpData["MAC/PHY Configuration"] = config;
                        }
                    }
                    // Maximum Frame Size
                    else if (line.Contains("Max frame size Subtype"))
                    {
                        var nextLine = reader.ReadLine();
                        if (nextLine != null && nextLine.Contains("MTU size"))
                        {
                            string[] parts = nextLine.Split(new[] { "size" }, 2, StringSplitOptions.None);
                            if (parts.Length > 1)
                                lldpData["Maximum Frame Size"] = parts[1].Trim();
                        }
                    }
                    // Power via MDI
                    else if (line.Contains("Power via MDI Subtype"))
                    {
                        var nextLine = reader.ReadLine();
                        if (nextLine != null)
                            lldpData["Power via MDI"] = nextLine.Trim();
                    }
                    // Link Aggregation
                    else if (line.Contains("Link aggregation Subtype"))
                    {
                        var nextLine = reader.ReadLine();
                        if (nextLine != null)
                            lldpData["Link Aggregation"] = nextLine.Trim();
                    }
                    // Network Policy (Voice VLAN)
                    else if (line.Contains("Network policy Subtype"))
                    {
                        var appLine = reader.ReadLine();
                        var vlanLine = reader.ReadLine();
                        
                        if (appLine != null && appLine.Contains("voice"))
                        {
                            string policy = appLine.Trim();
                            if (vlanLine != null)
                                policy += ", " + vlanLine.Trim();
                            
                            lldpData["Voice Policy"] = policy;
                        }
                    }
                    // VLAN Names
                    else if (line.Contains("VLAN name Subtype"))
                    {
                        var vlanIdLine = reader.ReadLine();
                        var vlanNameLine = reader.ReadLine();
                        
                        if (vlanIdLine != null && vlanNameLine != null)
                        {
                            string vlanId = vlanIdLine.Contains(":") ? 
                                vlanIdLine.Split(new[] { ':' }, 2)[1].Trim() : "";
                            
                            string vlanName = vlanNameLine.Contains(":") ? 
                                vlanNameLine.Split(new[] { ':' }, 2)[1].Trim() : "";
                            
                            if (!string.IsNullOrEmpty(vlanId) && !string.IsNullOrEmpty(vlanName))
                                vlanData.Add($"VLAN ID: {vlanId}, VLAN Name: {vlanName}");
                        }
                    }
                }
            }

            if (vlanData.Count > 0)
            {
                lldpData["VLANs"] = string.Join("\n", vlanData);
            }

            return lldpData;
        }

        static void DisplayLldpData(Dictionary<string, string> lldpData)
        {
            Console.WriteLine("\n========================================");
            Console.WriteLine("         LLDP Capture Results");
            Console.WriteLine("========================================\n");
            
            // Define display order for better readability
            var displayOrder = new[] 
            { 
                "System Name", 
                "Chassis ID", 
                "Port ID", 
                "Port Description",
                "Management Address",
                "System Description", 
                "System Capabilities", 
                "Enabled Capabilities",
                "Port VLAN ID",
                "Maximum Frame Size",
                "MAC/PHY Configuration",
                "Power via MDI",
                "Link Aggregation",
                "Voice Policy",
                "Time to Live",
                "VLANs"
            };
            
            foreach (var key in displayOrder)
            {
                if (lldpData.ContainsKey(key))
                {
                    if (key == "VLANs")
                    {
                        Console.WriteLine($"{key}:");
                        // Split and indent each VLAN line
                        string[] vlanLines = lldpData[key].Split('\n');
                        foreach (string vlanLine in vlanLines)
                        {
                            Console.WriteLine($"  {vlanLine}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"{key,-25}: {lldpData[key]}");
                    }
                }
            }
            
            Console.WriteLine("\n========================================\n");
        }

        // New helper to write LLDP data to a file.
        static void WriteLldpDataToFile(Dictionary<string, string> lldpData, string path)
        {
            try
            {
                using (var writer = new StreamWriter(path, false))
                {
                    writer.WriteLine("========================================");
                    writer.WriteLine("         LLDP Capture Results");
                    writer.WriteLine($"         {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    writer.WriteLine("========================================");
                    writer.WriteLine();
                    
                    // Define display order for better readability
                    var displayOrder = new[] 
                    { 
                        "System Name", 
                        "Chassis ID", 
                        "Port ID", 
                        "Port Description",
                        "Management Address",
                        "System Description", 
                        "System Capabilities", 
                        "Enabled Capabilities",
                        "Port VLAN ID",
                        "Maximum Frame Size",
                        "MAC/PHY Configuration",
                        "Power via MDI",
                        "Link Aggregation",
                        "Voice Policy",
                        "Time to Live",
                        "VLANs"
                    };
                    
                    foreach (var key in displayOrder)
                    {
                        if (lldpData.ContainsKey(key))
                        {
                            if (key == "VLANs")
                            {
                                writer.WriteLine($"{key}:");
                                // Split and indent each VLAN line
                                string[] vlanLines = lldpData[key].Split('\n');
                                foreach (string vlanLine in vlanLines)
                                {
                                    writer.WriteLine($"  {vlanLine}");
                                }
                            }
                            else
                            {
                                writer.WriteLine($"{key,-25}: {lldpData[key]}");
                            }
                        }
                    }
                    
                    writer.WriteLine();
                    writer.WriteLine("========================================");
                }
                Console.WriteLine($"\nResults saved to: {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to write results to file: {ex.Message}");
            }
        }

        static void CleanUp(bool debugMode)
        {
            ExecutePktmonCommand("stop");
            ExecutePktmonCommand("filter remove");
            ExecutePktmonCommand("reset");

            if (!debugMode)
            {
                if (File.Exists(EtlFilePath)) File.Delete(EtlFilePath);
                if (File.Exists(TxtFilePath)) File.Delete(TxtFilePath);
            }
        }

        static void ShowHelp()
        {
            Console.WriteLine("Usage: clldp.exe [options]");
            Console.WriteLine("Options:");
            Console.WriteLine("  -debug            Run the program in debug mode (keeps temp .etl and .txt files).");
            Console.WriteLine("  -t [duration]     Specify capture duration (must be between 30 and 60 seconds).");
            Console.WriteLine("  -p [port]         Write results to C:\\temp\\CLLDP_<timestamp>_<port>.txt in addition to console.");
            Console.WriteLine("  /help, /?         Display this help message.");
        }
    }
}