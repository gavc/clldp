using System;
using System.CommandLine;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace LLDPParser
{
    class Program
    {
        private static readonly string TempDirectory = Path.Combine("c:", "temp");
        private static readonly string EtlFilePath = Path.Combine(TempDirectory, "lldp.etl");
        private static readonly string TxtFilePath = Path.Combine(TempDirectory, "lldp.txt");
        private static readonly TimeSpan PktmonCommandTimeout = TimeSpan.FromMinutes(2);
        private static readonly string[] DisplayOrder =
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

        private const string FrameStartMarker = "ethertype LLDP (0x88cc)";
        private const string FrameEndMarker = "End TLV (0)";

        private enum LldpParseStatus
        {
            NoFramesFound,
            NoCompleteFramesFound,
            CompleteFrameFound
        }

        private sealed class ParsedLldpFrame
        {
            public ParsedLldpFrame(Dictionary<string, string> data, bool isComplete)
            {
                Data = data;
                IsComplete = isComplete;
            }

            public Dictionary<string, string> Data { get; }

            public bool IsComplete { get; }

            public int FieldCount => Data.Count;
        }

        private sealed class LldpParseResult
        {
            public LldpParseResult(LldpParseStatus status, Dictionary<string, string>? data, int totalFrames, int completeFrames, int partialFrames)
            {
                Status = status;
                Data = data ?? new Dictionary<string, string>();
                TotalFrames = totalFrames;
                CompleteFrames = completeFrames;
                PartialFrames = partialFrames;
            }

            public LldpParseStatus Status { get; }

            public Dictionary<string, string> Data { get; }

            public int TotalFrames { get; }

            public int CompleteFrames { get; }

            public int PartialFrames { get; }
        }

        private sealed class PktmonCommandResult
        {
            public PktmonCommandResult(string arguments, int exitCode, string output, string error)
            {
                Arguments = arguments;
                ExitCode = exitCode;
                Output = output;
                Error = error;
            }

            public string Arguments { get; }

            public int ExitCode { get; }

            public string Output { get; }

            public string Error { get; }

            public bool Succeeded => ExitCode == 0;

            public string GetFailureDetails()
            {
                if (!string.IsNullOrWhiteSpace(Error))
                {
                    return Error.Trim();
                }

                if (!string.IsNullOrWhiteSpace(Output))
                {
                    return Output.Trim();
                }

                return "No output was returned by pktmon.";
            }
        }

        static int Main(string[] args)
        {
            RootCommand rootCommand = BuildRootCommand();
            return rootCommand.Parse(NormalizeArguments(args)).Invoke();
        }

        static RootCommand BuildRootCommand()
        {
            Option<bool> debugOption = new("--debug", "-debug")
            {
                Description = "Run the program in debug mode (keeps temp .etl and .txt files)."
            };

            Option<int> durationOption = new("--duration", "-t")
            {
                Description = "Specify capture duration in seconds (must be between 30 and 60 seconds).",
                DefaultValueFactory = _ => 30
            };

            durationOption.Validators.Add(result =>
            {
                int duration = result.GetValue(durationOption);
                if (duration < 30 || duration > 60)
                {
                    result.AddError("Capture duration must be between 30 and 60 seconds.");
                }
            });

            Option<string?> portOption = new("--port", "-p")
            {
                Description = "Write results to C:\\temp\\CLLDP_<timestamp>_<port>.txt in addition to console."
            };

            RootCommand rootCommand = new("Capture and parse LLDP data using pktmon.")
            {
                debugOption,
                durationOption,
                portOption
            };

            rootCommand.SetAction(parseResult =>
            {
                bool debugMode = parseResult.GetValue(debugOption);
                int captureDuration = parseResult.GetValue(durationOption);
                string? portArg = parseResult.GetValue(portOption);
                return RunCapture(debugMode, captureDuration, portArg);
            });

            return rootCommand;
        }

        static string[] NormalizeArguments(string[] args)
        {
            return args
                .Select(arg => arg switch
                {
                    "/help" => "--help",
                    "/?" => "--help",
                    _ => arg
                })
                .ToArray();
        }

        static int RunCapture(bool debugMode, int captureDuration, string? portArg)
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    Console.WriteLine("Error: clldp only runs on Windows because it depends on pktmon.exe.");
                    return 1;
                }

                // Check if running as administrator (required for pktmon)
                if (!IsRunningAsAdministrator())
                {
                    Console.WriteLine("Error: This application requires administrator privileges.");
                    Console.WriteLine("Please right-click and select 'Run as administrator'.");
                    return 1;
                }

                CleanUp(debugMode);
                EnsureDirectoryExists(TempDirectory);

                var compIDs = GetComponentIDs();

                if (compIDs.Count > 0)
                {
                    string? selectedCompID = GetUserComponentSelection(compIDs);

                    if (!string.IsNullOrEmpty(selectedCompID))
                    {
                        bool captureSuccessful = false;
                        bool autoRetriedPartialCapture = false;
                        
                        while (!captureSuccessful)
                        {
                            CaptureLldpData(selectedCompID, captureDuration);
                            var parseResult = ParseLldpData(TxtFilePath);

                            if (parseResult.Status != LldpParseStatus.CompleteFrameFound)
                            {
                                if (parseResult.Status == LldpParseStatus.NoFramesFound)
                                {
                                    Console.WriteLine("\nNo LLDP data captured. This may occur if no LLDP packets were transmitted during the capture window.");
                                }
                                else
                                {
                                    Console.WriteLine("\nLLDP packets were captured, but no complete LLDP frame was found during the capture window.");
                                    Console.WriteLine($"Detected {parseResult.TotalFrames} LLDP frame(s), including {parseResult.PartialFrames} partial frame(s) that did not contain enough data to parse.");

                                    if (parseResult.PartialFrames > 0 && !autoRetriedPartialCapture)
                                    {
                                        autoRetriedPartialCapture = true;
                                        Console.WriteLine("A partial LLDP frame was found, so clldp will retry the capture once automatically.\n");
                                        CleanUp(debugMode);
                                        continue;
                                    }
                                }

                                Console.Write("Would you like to retry the capture? (Y/N): ");
                                
                                string? response = Console.ReadLine()?.Trim().ToUpperInvariant();
                                
                                if (response == "Y" || response == "YES")
                                {
                                    autoRetriedPartialCapture = false;
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
                                DisplayLldpData(parseResult.Data);

                                // If -p was provided, also write out results to a new file
                                if (!string.IsNullOrEmpty(portArg))
                                {
                                    string outPath = Path.Combine(
                                        TempDirectory,
                                        $"CLLDP_{DateTime.Now:yyyyMMddHHmmss}_{portArg}.txt"
                                    );
                                    WriteLldpDataToFile(parseResult.Data, outPath);
                                }
                            }
                        }
                    }
                }
                else
                {
                    Console.WriteLine("No ethernet adapters found to capture on.");
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}");
                return 1;
            }
            finally
            {
                CleanUp(debugMode);
            }
        }

        [SupportedOSPlatform("windows")]
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
            EnsurePktmonCommandSucceeded(
                ExecutePktmonCommand("filter add --ethertype 0x88cc"),
                "adding the LLDP filter");

            EnsurePktmonCommandSucceeded(
                ExecutePktmonCommand($"start --capture --comp {selectedCompID} --pkt-size 0 -f {EtlFilePath}"),
                $"starting capture on component {selectedCompID}");

            DateTime endTime = DateTime.Now.AddSeconds(durationInSeconds);
            while (DateTime.Now < endTime)
            {
                int remainingSeconds = (int)(endTime - DateTime.Now).TotalSeconds;
                Console.Write($"\rCapturing... {remainingSeconds} seconds remaining ");
                Thread.Sleep(1000);
            }

            Console.WriteLine();
            EnsurePktmonCommandSucceeded(
                ExecutePktmonCommand("stop"),
                "stopping packet capture");

            EnsurePktmonCommandSucceeded(
                ExecutePktmonCommand($"format {EtlFilePath} -o {TxtFilePath} -v"),
                "formatting the capture output");

            if (!File.Exists(TxtFilePath))
            {
                throw new InvalidOperationException($"pktmon reported success, but the formatted output file was not created: {TxtFilePath}");
            }
        }

        static List<string> GetComponentIDs()
        {
            var compIDs = new List<string>();
            PktmonCommandResult listResult = ExecutePktmonCommand("list");
            EnsurePktmonCommandSucceeded(listResult, "listing capture components");

            string output = listResult.Output;

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

        static string? GetUserComponentSelection(List<string> compIDs)
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

            if (compIDs.Count == 1)
            {
                Console.WriteLine("\nPress Enter to use this adapter, or type its Component ID to confirm:");
            }
            else
            {
                Console.WriteLine("\nPress Enter to use the suggested adapter, or type a Component ID to use a different one:");
            }
            
            while (true)
            {
                string? input = Console.ReadLine();
                
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
                
                if (compIDs.Count == 1)
                {
                    Console.WriteLine("Invalid Component ID. Please try again or press Enter to use this adapter:");
                }
                else
                {
                    Console.WriteLine("Invalid Component ID. Please try again or press Enter to use the suggested adapter:");
                }
            }
        }

        static PktmonCommandResult ExecutePktmonCommand(string arguments)
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
                        return new PktmonCommandResult(arguments, -1, string.Empty, "Failed to start pktmon process.");
                    }

                    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> errorTask = process.StandardError.ReadToEndAsync();

                    if (!process.WaitForExit((int)PktmonCommandTimeout.TotalMilliseconds))
                    {
                        TryTerminateProcess(process);

                        string timedOutOutput = AwaitStreamRead(outputTask);
                        string timedOutError = AwaitStreamRead(errorTask);
                        string timeoutMessage =
                            $"pktmon command timed out after {PktmonCommandTimeout.TotalSeconds:0} seconds.";

                        if (!string.IsNullOrWhiteSpace(timedOutError))
                        {
                            timeoutMessage += $" stderr: {timedOutError.Trim()}";
                        }

                        return new PktmonCommandResult(arguments, -1, timedOutOutput, timeoutMessage);
                    }

                    string output = AwaitStreamRead(outputTask);
                    string error = AwaitStreamRead(errorTask);

                    return new PktmonCommandResult(arguments, process.ExitCode, output, error);
                }
            }
            catch (Exception ex)
            {
                return new PktmonCommandResult(arguments, -1, string.Empty, ex.Message);
            }
        }

        static string AwaitStreamRead(Task<string> streamReadTask)
        {
            try
            {
                return streamReadTask.GetAwaiter().GetResult();
            }
            catch
            {
                return string.Empty;
            }
        }

        static void TryTerminateProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            try
            {
                process.WaitForExit();
            }
            catch
            {
            }
        }

        static void EnsurePktmonCommandSucceeded(PktmonCommandResult result, string operation)
        {
            if (result.Succeeded)
            {
                return;
            }

            throw new InvalidOperationException(
                $"pktmon failed while {operation}. Exit code: {result.ExitCode}. Details: {result.GetFailureDetails()}");
        }

        static void TryPktmonCleanupCommand(string arguments, params string[] benignFailurePatterns)
        {
            PktmonCommandResult result = ExecutePktmonCommand(arguments);
            if (result.Succeeded)
            {
                return;
            }

            if (benignFailurePatterns.Any(pattern =>
                result.GetFailureDetails().Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            Console.WriteLine($"Warning: pktmon cleanup command '{arguments}' failed. Details: {result.GetFailureDetails()}");
        }

        static LldpParseResult ParseLldpData(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"Warning: File not found: {filePath}");
                return new LldpParseResult(LldpParseStatus.NoFramesFound, null, 0, 0, 0);
            }

            var frames = ExtractLldpFrames(filePath);
            if (frames.Count == 0)
            {
                return new LldpParseResult(LldpParseStatus.NoFramesFound, null, 0, 0, 0);
            }

            var parsedFrames = frames.Select(ParseLldpFrame).ToList();
            var completeFrames = parsedFrames.Where(frame => frame.IsComplete).ToList();
            int partialFrames = parsedFrames.Count(frame => !frame.IsComplete);

            if (completeFrames.Count == 0)
            {
                return new LldpParseResult(LldpParseStatus.NoCompleteFramesFound, null, frames.Count, 0, partialFrames);
            }

            var selectedFrame = completeFrames
                .OrderByDescending(frame => frame.FieldCount)
                .First();

            return new LldpParseResult(
                LldpParseStatus.CompleteFrameFound,
                selectedFrame.Data,
                frames.Count,
                completeFrames.Count,
                partialFrames);
        }

        static List<List<string>> ExtractLldpFrames(string filePath)
        {
            var frames = new List<List<string>>();
            List<string>? currentFrame = null;

            foreach (string line in File.ReadLines(filePath))
            {
                if (line.Contains(FrameStartMarker))
                {
                    if (currentFrame != null && currentFrame.Count > 0)
                    {
                        frames.Add(currentFrame);
                    }

                    currentFrame = new List<string> { line };
                    continue;
                }

                if (currentFrame == null)
                {
                    continue;
                }

                currentFrame.Add(line);

                if (line.Contains(FrameEndMarker))
                {
                    frames.Add(currentFrame);
                    currentFrame = null;
                }
            }

            if (currentFrame != null && currentFrame.Count > 0)
            {
                frames.Add(currentFrame);
            }

            return frames;
        }

        static ParsedLldpFrame ParseLldpFrame(IReadOnlyList<string> frameLines)
        {
            var lldpData = new Dictionary<string, string>();
            var vlanData = new List<string>();

            for (int i = 0; i < frameLines.Count; i++)
            {
                string line = frameLines[i];

                if (line.Contains(FrameEndMarker))
                {
                    break;
                }

                if (line.Contains("Chassis ID TLV"))
                {
                    string? nextLine = ConsumeNextLine(frameLines, ref i);
                    if (nextLine != null && nextLine.Contains(": "))
                    {
                        string[] parts = nextLine.Split(new[] { ": " }, 2, StringSplitOptions.None);
                        if (parts.Length > 1)
                        {
                            lldpData["Chassis ID"] = parts[1].Trim();
                        }
                    }
                }
                else if (line.Contains("Port ID TLV"))
                {
                    string? nextLine = ConsumeNextLine(frameLines, ref i);
                    if (nextLine != null && nextLine.Contains(": "))
                    {
                        string[] parts = nextLine.Split(new[] { ": " }, 2, StringSplitOptions.None);
                        if (parts.Length > 1)
                        {
                            lldpData["Port ID"] = parts[1].Trim();
                        }
                    }
                }
                else if (line.Contains("Time to Live TLV") && line.Contains("TTL"))
                {
                    int ttlIndex = line.IndexOf("TTL", StringComparison.Ordinal);
                    if (ttlIndex >= 0)
                    {
                        lldpData["Time to Live"] = line.Substring(ttlIndex).Trim();
                    }
                }
                else if (line.Contains("Port Description TLV"))
                {
                    if (line.Contains(": "))
                    {
                        string[] parts = line.Split(new[] { ": " }, 2, StringSplitOptions.None);
                        if (parts.Length > 1)
                        {
                            lldpData["Port Description"] = parts[1].Trim();
                        }
                    }
                    else
                    {
                        string? nextLine = ConsumeNextLine(frameLines, ref i);
                        if (nextLine != null)
                        {
                            lldpData["Port Description"] = nextLine.Trim();
                        }
                    }
                }
                else if (line.Contains("System Name TLV"))
                {
                    if (line.Contains(": "))
                    {
                        string[] parts = line.Split(new[] { ": " }, 2, StringSplitOptions.None);
                        if (parts.Length > 1)
                        {
                            lldpData["System Name"] = parts[1].Trim();
                        }
                    }
                    else
                    {
                        string? nextLine = ConsumeNextLine(frameLines, ref i);
                        if (nextLine != null)
                        {
                            lldpData["System Name"] = nextLine.Trim();
                        }
                    }
                }
                else if (line.Contains("System Description TLV"))
                {
                    string? nextLine = ConsumeNextLine(frameLines, ref i);
                    if (nextLine != null)
                    {
                        lldpData["System Description"] = nextLine.Trim();
                    }
                }
                else if (line.Contains("System Capabilities TLV"))
                {
                    string? capabilitiesLine = ConsumeNextLine(frameLines, ref i);
                    if (capabilitiesLine != null && capabilitiesLine.Contains(": "))
                    {
                        string[] parts = capabilitiesLine.Split(new[] { ": " }, 2, StringSplitOptions.None);
                        if (parts.Length > 1)
                        {
                            lldpData["System Capabilities"] = parts[1].Trim();
                        }
                    }

                    string? enabledCapabilitiesLine = ConsumeNextLine(frameLines, ref i);
                    if (enabledCapabilitiesLine != null && enabledCapabilitiesLine.Contains(": "))
                    {
                        string[] parts = enabledCapabilitiesLine.Split(new[] { ": " }, 2, StringSplitOptions.None);
                        if (parts.Length > 1)
                        {
                            lldpData["Enabled Capabilities"] = parts[1].Trim();
                        }
                    }
                }
                else if (line.Contains("Management Address TLV"))
                {
                    string? managementAddressLine = ConsumeNextLine(frameLines, ref i);
                    if (managementAddressLine != null && managementAddressLine.Contains(": "))
                    {
                        string[] parts = managementAddressLine.Split(new[] { ": " }, 2, StringSplitOptions.None);
                        if (parts.Length > 1)
                        {
                            lldpData["Management Address"] = parts[1].Trim();
                        }
                    }
                }
                else if (line.Contains("Port VLAN Id Subtype"))
                {
                    string? nextLine = ConsumeNextLine(frameLines, ref i);
                    if (nextLine != null && nextLine.Contains("port vlan id (PVID):"))
                    {
                        string[] parts = nextLine.Split(new[] { ":" }, 2, StringSplitOptions.None);
                        if (parts.Length > 1)
                        {
                            lldpData["Port VLAN ID"] = parts[1].Trim();
                        }
                    }
                }
                else if (line.Contains("MAC/PHY configuration/status Subtype"))
                {
                    string? autonegLine = ConsumeNextLine(frameLines, ref i);
                    string? pmdLine = ConsumeNextLine(frameLines, ref i);
                    string? mauLine = ConsumeNextLine(frameLines, ref i);

                    if (autonegLine != null && pmdLine != null)
                    {
                        string config = $"{autonegLine.Trim()}, {pmdLine.Trim()}";
                        if (mauLine != null)
                        {
                            config += $", {mauLine.Trim()}";
                        }

                        lldpData["MAC/PHY Configuration"] = config;
                    }
                }
                else if (line.Contains("Max frame size Subtype"))
                {
                    string? nextLine = ConsumeNextLine(frameLines, ref i);
                    if (nextLine != null && nextLine.Contains("MTU size"))
                    {
                        string[] parts = nextLine.Split(new[] { "size" }, 2, StringSplitOptions.None);
                        if (parts.Length > 1)
                        {
                            lldpData["Maximum Frame Size"] = parts[1].Trim();
                        }
                    }
                }
                else if (line.Contains("Power via MDI Subtype"))
                {
                    string? nextLine = ConsumeNextLine(frameLines, ref i);
                    if (nextLine != null)
                    {
                        lldpData["Power via MDI"] = nextLine.Trim();
                    }
                }
                else if (line.Contains("Link aggregation Subtype"))
                {
                    string? nextLine = ConsumeNextLine(frameLines, ref i);
                    if (nextLine != null)
                    {
                        lldpData["Link Aggregation"] = nextLine.Trim();
                    }
                }
                else if (line.Contains("Network policy Subtype"))
                {
                    string? appLine = ConsumeNextLine(frameLines, ref i);
                    string? vlanLine = ConsumeNextLine(frameLines, ref i);

                    if (appLine != null && appLine.Contains("voice", StringComparison.OrdinalIgnoreCase))
                    {
                        string policy = appLine.Trim();
                        if (vlanLine != null)
                        {
                            policy += ", " + vlanLine.Trim();
                        }

                        lldpData["Voice Policy"] = policy;
                    }
                }
                else if (line.Contains("VLAN name Subtype"))
                {
                    string? vlanIdLine = ConsumeNextLine(frameLines, ref i);
                    string? vlanNameLine = ConsumeNextLine(frameLines, ref i);

                    if (vlanIdLine != null && vlanNameLine != null)
                    {
                        string vlanId = vlanIdLine.Contains(":", StringComparison.Ordinal)
                            ? vlanIdLine.Split(new[] { ':' }, 2)[1].Trim()
                            : string.Empty;

                        string vlanName = vlanNameLine.Contains(":", StringComparison.Ordinal)
                            ? vlanNameLine.Split(new[] { ':' }, 2)[1].Trim()
                            : string.Empty;

                        if (!string.IsNullOrEmpty(vlanId) && !string.IsNullOrEmpty(vlanName))
                        {
                            vlanData.Add($"VLAN ID: {vlanId}, VLAN Name: {vlanName}");
                        }
                    }
                }
            }

            if (vlanData.Count > 0)
            {
                lldpData["VLANs"] = string.Join("\n", vlanData);
            }

            return new ParsedLldpFrame(lldpData, IsCompleteFrame(frameLines));
        }

        static bool IsCompleteFrame(IReadOnlyList<string> frameLines)
        {
            return frameLines.Any(line => line.Contains("Chassis ID TLV"))
                && frameLines.Any(line => line.Contains("Port ID TLV"))
                && frameLines.Any(line => line.Contains("Time to Live TLV"))
                && frameLines.Any(line => line.Contains(FrameEndMarker));
        }

        static string? ConsumeNextLine(IReadOnlyList<string> lines, ref int index)
        {
            if (index + 1 >= lines.Count)
            {
                return null;
            }

            index++;
            return lines[index];
        }

        static void DisplayLldpData(Dictionary<string, string> lldpData)
        {
            Console.WriteLine("\n========================================");
            Console.WriteLine("         LLDP Capture Results");
            Console.WriteLine("========================================\n");

            foreach (var key in DisplayOrder)
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

                    foreach (var key in DisplayOrder)
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
            TryPktmonCleanupCommand("stop", "not running");
            TryPktmonCleanupCommand("filter remove", "no filters");
            TryPktmonCleanupCommand("reset");

            if (!debugMode)
            {
                if (File.Exists(EtlFilePath)) File.Delete(EtlFilePath);
                if (File.Exists(TxtFilePath)) File.Delete(TxtFilePath);
            }
        }
    }
}