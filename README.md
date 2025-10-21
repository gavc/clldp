# CLLDP

A C# .NET 8 console application that captures and parses LLDP (Link Layer Discovery Protocol) data using Windows built-in `pktmon.exe`. Perfect for network troubleshooting and documentation.

## Features

### Capture & Parsing
- Uses `pktmon.exe` to filter and capture LLDP packets (no additional drivers needed)
- Comprehensive LLDP field parsing including:
  - Basic device info (Chassis ID, Port ID, System Name, Description)
  - Network configuration (Port VLAN ID, VLANs, Management Address)
  - Physical layer details (MAC/PHY configuration, Maximum Frame Size)
  - Advanced features (Power via MDI/PoE, Link Aggregation, Voice Policy)
- Automatic detection of LLDP packet boundaries for accurate parsing

### User Experience
- **Smart adapter selection** - Auto-suggests the first available Ethernet adapter with visual highlighting
- **Automatic retry** - Prompts to retry capture if no LLDP data is received (saves time during troubleshooting)
- **Professional output** - Clean, formatted display with logical field ordering
- **Color highlighting** - Green "SUGGESTED" text for recommended adapter

### Command-Line Options
- `-t <seconds>` - Specify capture duration (30-60 seconds, default: 30)
- `-p <port>` - Save results to timestamped file: `C:\temp\CLLDP_<timestamp>_<port>.txt`
- `-debug` - Keep temporary `.etl` and `.txt` files for troubleshooting
- `/?` or `/help` - Display help information

### Safety & Reliability
- Administrator privilege check on startup
- Filters out non-Ethernet adapters (Wi-Fi, Bluetooth, wireless)
- Enhanced error handling and informative messages
- Automatic cleanup of temporary files

## Requirements
- Windows 10 (version 1809+) or Windows 11
- Administrator privileges (required for `pktmon.exe`)
- .NET 8 Runtime (framework-dependent build) or use self-contained build
- `pktmon.exe` (built into Windows 10 1809+)

## Installation

### Option 1: Self-Contained (Recommended)
Download `clldp-win-x64-self-contained.zip` from [Releases](https://github.com/gavc/clldp/releases)
- No .NET runtime installation required
- Larger file size (~70MB)
- Extract and run `clldp.exe` as administrator

### Option 2: Framework-Dependent
Download `clldp-win-x64-framework-dependent.zip` from [Releases](https://github.com/gavc/clldp/releases)
- Requires .NET 8 Runtime ([Download here](https://dotnet.microsoft.com/download/dotnet/8.0))
- Smaller file size (~200KB)
- Extract and run `clldp.exe` as administrator

## Usage Examples

### Basic capture (30 seconds, auto-suggested adapter)
```powershell
# Right-click clldp.exe → "Run as administrator"
# Or from PowerShell:
.\clldp.exe
```

### Save results to file with port identifier
```powershell
.\clldp.exe -p A-123
# Saves to: C:\temp\CLLDP_20251021143025_A-123.txt
```

### Custom capture duration
```powershell
.\clldp.exe -t 60
```

### Debug mode (keep temporary files)
```powershell
.\clldp.exe -debug
```

### Combined options
```powershell
.\clldp.exe -t 45 -p WallPort-5
```

## Sample Output

```
Available Network Adapters:
----------------------------
  1 - Intel(R) Ethernet Controller (3) I225-V [SUGGESTED]
  3 - Realtek USB GbE Family Controller

Press Enter to use the suggested adapter, or type a Component ID to use a different one:

Capturing... 30 seconds remaining

========================================
         LLDP Capture Results
========================================

System Name              : SW-CORE-01
Chassis ID               : B8-BB-15-BB-BB-C8
Port ID                  : ge-3/0/37
Port Description         : ge-3/0/37
Management Address       : 192.168.1.1
System Description       : Juniper Networks, Inc. ex3400-48p
System Capabilities      : [Bridge, Router] (0x0014)
Enabled Capabilities     : [Bridge, Router] (0x0014)
Port VLAN ID             : 433
Maximum Frame Size       : 1514
MAC/PHY Configuration    : autonegotiation [supported, enabled], PMD autoneg capability [1000BASE-T fdx]
Power via MDI            : MDI power support [PSE, supported, enabled]
Link Aggregation         : aggregation status [supported], aggregation port ID 0
Voice Policy             : Application type [voice], Flags [Tagged], Vlan id 23
Time to Live             : TTL 120s
VLANs:
  VLAN ID: 23, VLAN Name: vlan-23
  VLAN ID: 433, VLAN Name: vlan-433
  VLAN ID: 23, VLAN Name: voice

========================================
```

## Troubleshooting

### No LLDP data captured
- **Wait for retry prompt** - The app will ask if you want to try again
- LLDP packets are typically transmitted every 30 seconds by switches
- Ensure the device is connected to a switch/router that supports LLDP
- Some devices may have LLDP disabled by default

### Access Denied / Permission errors
- Ensure you're running as Administrator (required for `pktmon.exe`)
- Right-click `clldp.exe` → "Run as administrator"

### Wrong adapter selected
- Type the correct Component ID when prompted instead of pressing Enter
- Use `-debug` mode to see all available adapters

## Future Enhancements
- Live capture mode (monitor ETL file for real-time packet detection)
- CDP support for Cisco switches
- Alternative capture backends (SharpPcap/Npcap)

## Alternatives
- [LDWin](https://github.com/chall32/LDWin) - GUI-based LLDP tool for Windows
- [PSDiscoveryProtocol](https://www.powershellgallery.com/packages/PSDiscoveryProtocol) - PowerShell module for discovery protocols
