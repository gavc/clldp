using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic;
using System.Linq;

namespace LLDPParser
{
    class Program
    {
        private static readonly string TempDirectory = Path.Combine("c:", "temp");
        private static readonly string EtlFilePath = Path.Combine(TempDirectory, "lldp.etl");
        private static readonly string TxtFilePath = Path.Combine(TempDirectory, "lldp.txt");
        static void Main(string[] args)
        {
            try
            {
                CleanUp();

                EnsureDirectoryExists(TempDirectory);

                var compIDs = GetComponentIDs();

                if (compIDs.Count > 0)
                {
                    string selectedCompID = GetUserComponentSelection(compIDs);

                    if (!string.IsNullOrEmpty(selectedCompID))
                    {
                        // Set default capture duration
                        int captureDuration = 30; // Default duration is 30 seconds

                        // Check if a custom duration is provided as a command-line argument
                        if (args.Length > 1 && args[0] == "-t" && int.TryParse(args[1], out int parsedDuration))
                        {
                            captureDuration = ValidateCaptureDuration(parsedDuration);
                        }

                        // Capture LLDP data with the specified duration
                        CaptureLldpData(selectedCompID, captureDuration);

                        // Parse and display the captured LLDP data
                        var lldpData = ParseLldpData(TxtFilePath);
                        DisplayLldpData(lldpData);
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
                CleanUp();
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

        static void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                //Console.WriteLine($"Directory created: {path}");
            }
        }

        static void CaptureLldpData(string selectedCompID, int durationInSeconds)
        {
            ExecutePktmonCommand("filter add --ethertype 0x88cc");

            ExecutePktmonCommand($"start --capture --comp {selectedCompID} --pkt-size 0 -f {EtlFilePath}");

            // Non-blocking countdown with time left display
            DateTime endTime = DateTime.Now.AddSeconds(durationInSeconds);
            while (DateTime.Now < endTime)
            {
                int remainingSeconds = (int)(endTime - DateTime.Now).TotalSeconds;
                Console.Write($"\rCapturing... {remainingSeconds} seconds remaining ");
                Thread.Sleep(1000); // Sleep for 1 second to update the countdown
            }

            Console.WriteLine(); // Move to the next line after the countdown is complete

            // Stop packet capture
            ExecutePktmonCommand("stop");

            // Convert ETL to text
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

                foreach (string line in lines)
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

                            // Filter out Bluetooth, Wireless, and Wi-Fi components
                            if (!name.ToLower().Contains("bluetooth") &&
                                !name.ToLower().Contains("wireless") &&
                                !name.ToLower().Contains("wi-fi"))
                            {
                                compIDs.Add(compID);
                                Console.WriteLine($"Component ID: {compID}, Name: {name}");
                            }
                        }
                    }
                }
            }

            return compIDs;
        }

        static string GetUserComponentSelection(List<string> compIDs)
        {
            Console.WriteLine("Enter the Component ID to capture on:");
            while (true)
            {
                string selectedCompID = Console.ReadLine();
                if (compIDs.Contains(selectedCompID))
                {
                    return selectedCompID;
                }
                Console.WriteLine("Invalid Component ID entered. Please try again or press Enter to exit.");
                if (string.IsNullOrEmpty(selectedCompID)) return null;
            }
        }

        static string ExecutePktmonCommand(string arguments)
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo("pktmon", arguments)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process process = Process.Start(startInfo))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    return output;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error executing pktmon command: {ex.Message}");
                return null;
            }
        }

        static Dictionary<string, string> ParseLldpData(string filePath)
        {
            var lldpData = new Dictionary<string, string>();
            var vlanData = new List<string>();

            using (var reader = new StreamReader(filePath))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    // Extract Chassis ID
                    if (line.Contains("Chassis ID TLV"))
                    {
                        lldpData["Chassis ID"] = reader.ReadLine()?.Split(": ")[1].Trim();
                    }
                    // Extract Port ID
                    else if (line.Contains("Port ID TLV"))
                    {
                        lldpData["Port ID"] = reader.ReadLine()?.Split(": ")[1].Trim();
                    }
                    // Extract Time to Live
                    else if (line.Contains("Time to Live TLV"))
                    {
                        lldpData["Time to Live"] = line.Split(": ")[1].Trim();
                    }
                    // Extract Port Description
                    else if (line.Contains("Port Description TLV"))
                    {
                        lldpData["Port Description"] = line.Split(": ")[1].Trim();
                    }
                    // Extract System Name
                    else if (line.Contains("System Name TLV"))
                    {
                        lldpData["System Name"] = line.Split(": ")[1].Trim();
                    }
                    // Extract System Description
                    else if (line.Contains("System Description TLV"))
                    {
                        lldpData["System Description"] = reader.ReadLine()?.Trim();
                    }
                    // Extract System Capabilities
                    else if (line.Contains("System Capabilities TLV"))
                    {
                        var capabilitiesLine = reader.ReadLine()?.Split(": ");
                        if (capabilitiesLine != null && capabilitiesLine.Length > 1)
                        {
                            lldpData["System Capabilities"] = capabilitiesLine[1].Trim();
                        }
                        var enabledCapabilitiesLine = reader.ReadLine()?.Split(": ");
                        if (enabledCapabilitiesLine != null && enabledCapabilitiesLine.Length > 1)
                        {
                            lldpData["Enabled Capabilities"] = enabledCapabilitiesLine[1].Trim();
                        }
                    }
                    // Extract Management Address
                    else if (line.Contains("Management Address TLV"))
                    {
                        var managementAddressLine = reader.ReadLine()?.Split(": ");
                        if (managementAddressLine != null && managementAddressLine.Length > 1)
                        {
                            lldpData["Management Address"] = managementAddressLine[1].Trim();
                        }
                    }
                    // Extract VLAN Information
                    else if (line.Contains("VLAN name Subtype"))
                    {
                        string vlanId = reader.ReadLine()?.Split(": ")[1].Trim();
                        string vlanName = reader.ReadLine()?.Split(": ")[1].Trim();
                        vlanData.Add($"VLAN ID: {vlanId} VLAN Name: {vlanName}");
                    }
                }
            }

            // Add VLAN data to the main dictionary
            if (vlanData.Count > 0)
            {
                lldpData["VLANs"] = string.Join("\n", vlanData);
            }

            return lldpData;
        }

        static void DisplayLldpData(Dictionary<string, string> lldpData)
        {
            Console.WriteLine("Parsed LLDP Data:");

            // Display each key-value pair
            foreach (var item in lldpData)
            {
                // Add a newline before and after VLANs section
                if (item.Key == "VLANs")
                {
                    //Console.WriteLine();
                    Console.WriteLine($"{item.Key}:");
                    Console.WriteLine($"{item.Value}");
                    Console.WriteLine();
                }
                else
                {
                    Console.WriteLine($"{item.Key}: {item.Value}");
                }
            }
        }
        static void CleanUp()
        {
            ExecutePktmonCommand("stop");
            ExecutePktmonCommand("filter remove");
            ExecutePktmonCommand("reset");

            if (File.Exists(EtlFilePath)) File.Delete(EtlFilePath);
            if (File.Exists(TxtFilePath)) File.Delete(TxtFilePath);
        }
    }
}