# [Part 272] PC Up Check Tools - spec collection script (embedded in the app, run by Services/SpecScanner.cs).
# SOURCE: copied verbatim from apps/web/get_specs.ps1 (from the Convert-WmiDate helper through ConvertTo-Json).
#   Output is identical to the scanner exe (PCUpCheck_Setup.exe) and get_specs.ps1 - all three were fixed together in
#   Part 274 (date fields), so pcupcheck.com/scanner pastes it unchanged.
# REMOVED on purpose: reading GEMINI_API_KEY from .env and writing app-settings.js (not needed, violates key rules).
#   Clipboard is set by the app (not Set-Clipboard) - the JSON is written to stdout as UTF-8.
# Keep in sync with apps/web/get_specs.ps1 when the collected fields change.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# [Part 274] Get-CimInstance returns DateTime already; ManagementDateTimeConverter.ToDateTime accepts DMTF strings only
#   -> it threw, so OSInstallDate/HardwareDate were never read and SystemAgeYears was always 0. Accept both.
function Convert-WmiDate($value) {
    if ($value -is [datetime]) { return $value }
    return [Management.ManagementDateTimeConverter]::ToDateTime([string]$value)
}

# [Part 274] Win32_DiskDrive.InterfaceType reports NVMe drives as SCSI (measured: SAMSUNG MZVL2512 NVMe -> SCSI).
#   That hid the NVMe/SSD reuse advice (build-rules.js). Prefer Get-PhysicalDisk BusType (NVMe/SATA/USB...) matched by model.
$script:physicalDisks = $null
function Get-DiskBusType($disk) {
    try {
        if ($null -eq $script:physicalDisks) { $script:physicalDisks = @(Get-PhysicalDisk -ErrorAction SilentlyContinue) }
        $name = if ($disk.Model) { $disk.Model } else { $disk.FriendlyName }
        $match = $script:physicalDisks | Where-Object { $_.FriendlyName -eq $name } | Select-Object -First 1
        if ($match -and $match.BusType) { return [string]$match.BusType }
    } catch {}
    if ($disk.InterfaceType) { return $disk.InterfaceType } else { return $disk.BusType }
}

function Get-HardwareInfo {
    param($CimClass, $FallbackCmd)
    try {
        $info = Get-CimInstance $CimClass -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $info -and $null -ne $FallbackCmd) {
            $info = Invoke-Expression $FallbackCmd -ErrorAction SilentlyContinue | Select-Object -First 1
        }
        return $info
    } catch { return $null }
}

# [CORE] Hardware Extraction
$cpu = Get-HardwareInfo "Win32_Processor"
$mem = @(Get-CimInstance Win32_PhysicalMemory -ErrorAction SilentlyContinue)
$gpu = Get-HardwareInfo "Win32_VideoController"
$board = Get-HardwareInfo "Win32_BaseBoard" "Get-CimInstance Win32_ComputerSystemProduct"
$os = Get-HardwareInfo "Win32_OperatingSystem"
$sysInfo = Get-HardwareInfo "Win32_ComputerSystem"

# [MAINTENANCE] Battery & System Age (Heuristic)
$battery = Get-HardwareInfo "Win32_Battery"
$batteryHealth = "Unknown"
if ($battery -and $battery.DesignCapacity -gt 0) {
    $healthPercent = [Math]::Round(($battery.FullChargeCapacity / $battery.DesignCapacity) * 100)
    $batteryHealth = "$healthPercent%"
}

$bios = Get-HardwareInfo "Win32_BIOS"
$sysAgeYears = 0
$referenceDate = Get-Date

if ($os.InstallDate -or $bios.ReleaseDate) {
    try {
        $dates = @()
        if ($os.InstallDate) { $dates += (Convert-WmiDate $os.InstallDate) }
        if ($bios.ReleaseDate) { $dates += (Convert-WmiDate $bios.ReleaseDate) }
        
        # 기기 자체의 연식을 알기 위해 가장 오래된 날짜 선택 (BIOS 출시일 등)
        $oldestDate = ($dates | Sort-Object)[0]
        $referenceDate = $oldestDate
        $sysAgeYears = [Math]::Round((New-TimeSpan -Start $oldestDate -End (Get-Date)).Days / 365, 1)
    } catch { $sysAgeYears = 0 }
}

# [FIX] Disk Detection
$disks = @(Get-CimInstance Win32_DiskDrive -ErrorAction SilentlyContinue)
if ($disks.Count -eq 0) {
    try { $disks = @(Get-PhysicalDisk -ErrorAction SilentlyContinue | Select-Object @{n='Model';e={$_.FriendlyName}}, @{n='Size';e={$_.Size}}, @{n='InterfaceType';e={$_.BusType}}) } catch {}
}

# [FIX] VRAM Calculation
$vramBytes = if($gpu.AdapterRAM){ [uint64]$gpu.AdapterRAM } else { 0 }
if ($vramBytes -eq 4294967295 -or $vramBytes -le 4294967296) {
    try {
        $regPath = "HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\000*"
        $regVram = Get-ItemProperty $regPath -ErrorAction SilentlyContinue | Where-Object { $_.HardwareInformation.MemorySize } | Select-Object -ExpandProperty HardwareInformation.MemorySize -First 1
        if ($regVram) { $vramBytes = [uint64]$regVram }
    } catch {}
}
$vramGB = [Math]::Round($vramBytes / 1GB)

# 3. Build Final Spec Object
$ramType = "DDR4"
if ($mem) {
    $smType = $mem[0].SMBIOSMemoryType
    if ($smType -eq 26) { $ramType = "DDR4" }
    elseif ($smType -eq 34) { $ramType = "DDR5" }
    elseif ($smType -eq 24) { $ramType = "DDR3" }
    elseif ($smType -eq 30) { $ramType = "LPDDR4" }
    elseif ($smType -eq 35) { $ramType = "LPDDR5" }
    elseif ($mem[0].Speed -gt 4800) { $ramType = "DDR5" }
}

$specHash = @{
    ScanTime = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    CPU = @{ 
        Model = if($cpu.Name){$cpu.Name.ToString().Trim()}else{"Unknown CPU"}; 
        Cores = if($cpu.NumberOfCores){$cpu.NumberOfCores}else{0}; 
        Threads = if($cpu.NumberOfLogicalProcessors){$cpu.NumberOfLogicalProcessors}else{0} 
    }
    RAM = @{ 
        TotalGB = if($mem){ [Math]::Round(($mem | Measure-Object -Property Capacity -Sum).Sum / 1GB) } else { [Math]::Round($sysInfo.TotalPhysicalMemory / 1GB) }; 
        Type = $ramType; 
        Speed = if($mem){$mem[0].Speed}else{0}; 
        SlotsUsed = if($mem){$mem.Count}else{1}; 
        TotalSlots = try { (Get-CimInstance Win32_PhysicalMemoryArray -ErrorAction SilentlyContinue).MemoryDevices[0] } catch { 2 }
    }
    GPU = @{ 
        Model = if($gpu.Name){$gpu.Name}else{"Standard Display Adapter"}; 
        VRAM_GB = $vramGB; 
        DriverVersion = if($gpu.DriverVersion){$gpu.DriverVersion}else{"Unknown"} 
    }
    Disk = if($disks){ @($disks | ForEach-Object { @{ Model = if($_.Model){$_.Model}else{$_.FriendlyName}; SizeGB = [Math]::Round($_.Size / 1GB, 0); Health = "OK"; BusType = (Get-DiskBusType $_) } }) } else { @(@{Model="Primary Storage"; SizeGB=0; Health="OK"; BusType="SSD"}) }
    Motherboard = @{ 
        Manufacturer = if($board.Manufacturer){$board.Manufacturer}else{$sysInfo.Manufacturer}; 
        Model = if($board.Product){$board.Product}else{if($board.Name){$board.Name}else{"Generic MB"}} 
    }
    System = @{ 
        Manufacturer = if($sysInfo.Manufacturer){$sysInfo.Manufacturer}else{"Unknown"}; 
        Model = if($sysInfo.Model){$sysInfo.Model}else{"Unknown"} 
    }
    Maintenance = @{
        BatteryHealth = $batteryHealth;
        SystemAgeYears = $sysAgeYears;
        OSInstallDate = try { if($os.InstallDate){ ((Convert-WmiDate $os.InstallDate)).ToString("yyyy-MM-dd") }else{"Unknown"} } catch { "Unknown" };
        HardwareDate = try { if($bios.ReleaseDate){ ((Convert-WmiDate $bios.ReleaseDate)).ToString("yyyy-MM-dd") }else{"Unknown"} } catch { "Unknown" }
    }
    Display = @{ 
        Width = if($gpu.CurrentHorizontalResolution){$gpu.CurrentHorizontalResolution}else{1920}; 
        Height = if($gpu.CurrentVerticalResolution){$gpu.CurrentVerticalResolution}else{1080}; 
        RefreshRate = if($gpu.CurrentRefreshRate){$gpu.CurrentRefreshRate}else{60} 
    }
    Software = @{ 
        OS = if($os.Caption){$os.Caption}else{"Windows"}; 
        StartupCount = (Get-CimInstance Win32_StartupCommand -ErrorAction SilentlyContinue).Count; 
        TopProcesses = @(Get-Process | Sort-Object WorkingSet64 -Descending -ErrorAction SilentlyContinue | Select-Object -First 5 | ForEach-Object { 
            @{ Name = $_.ProcessName; RAM_MB = [Math]::Round($_.WorkingSet64 / 1MB) } 
        })
    }
}

# 4. JSON Generation & Clipboard
$jsonOut = $specHash | ConvertTo-Json -Depth 10
[Console]::Out.Write($jsonOut)
