$source = @"
using System;
using System.Runtime.InteropServices;

public class NativeDriver {
    [DllImport("Original_Driver\\hiddriver_1.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void Set_VIDPID(int vid, int pid);

    [DllImport("Original_Driver\\hiddriver_1.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Open_FeatureDevice();

    [DllImport("Original_Driver\\hiddriver_1.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Close_FeatureDevice();

    [DllImport("Original_Driver\\hiddriver_1.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int SetFeature(IntPtr buffer, int length);
}
"@

Add-Type -TypeDefinition $source

Write-Host "Trying wireless VID: 0x1D57, PID: 0xFA60..."
[NativeDriver]::Set_VIDPID(0x1D57, 0xFA60)
$res = [NativeDriver]::Open_FeatureDevice()

if ($res -eq 0) {
    Write-Host "Wireless failed. Trying wired VID: 0x1D57, PID: 0x2015..."
    [NativeDriver]::Set_VIDPID(0x1D57, 0x2015)
    $res = [NativeDriver]::Open_FeatureDevice()
}

if ($res -eq 0) {
    Write-Host "Failed to open FeatureDevice. Is dongle/mouse connected?"
    exit 1
}

Write-Host "SUCCESS: FeatureDevice opened! Sending Factory Reset Report (Report 12)..."
# Report 12 (0x0C): 10 bytes: 0x0C, 0x0A, 0x01, 0xFE, 0x01, 0xFE, 0, 0, 0, 0
[byte[]]$rep12 = 0x0C, 0x0A, 0x01, 0xFE, 0x01, 0xFE, 0x00, 0x00, 0x00, 0x00
$pRep12 = [System.Runtime.InteropServices.Marshal]::AllocHGlobal($rep12.Length)
[System.Runtime.InteropServices.Marshal]::Copy($rep12, 0, $pRep12, $rep12.Length)
$set12 = [NativeDriver]::SetFeature($pRep12, $rep12.Length)
[System.Runtime.InteropServices.Marshal]::FreeHGlobal($pRep12)
Write-Host "Report 12 sent! Result: $set12"

Start-Sleep -Milliseconds 600

Write-Host "Sending Clean Default Sensor Report (Report 4)..."
[byte[]]$rep4 = New-Object byte[] 56
$rep4[0] = 0x04
$rep4[1] = 0x38
$rep4[2] = 0x01
$rep4[3] = 0x00 # stage 1 active (0-based)
$rep4[4] = 0x06 # 6 stages
$rep4[5] = 0x3F # stages 1-6 enabled

# Steps: 400(8), 800(16), 1600(32), 3200(64), 6400(128), 11000(220)
# Register table values: 0x07, 0x0F, 0x1F, 0x3F, 0x7F, 0xDC
$rep4[8] = 0x07
$rep4[9] = 0x0F
$rep4[10] = 0x1F
$rep4[11] = 0x3F
$rep4[12] = 0x7F
$rep4[13] = 0xDC

$rep4[19] = 0x01 # high DPI flag for stage 6 (11000)
$rep4[24] = 0x01 # Profile 1

# Colors (8 stages * 3 bytes RGB):
# 1: Yellow (255, 255, 0)
# 2: Red (255, 0, 0)
# 3: Green (0, 255, 0)
# 4: Blue (0, 0, 255)
# 5: Cyan (0, 255, 255)
# 6: Magenta (255, 0, 255)
# 7: Orange (255, 128, 0)
# 8: White (255, 255, 255)
$colors = @(
    @(255, 255, 0),
    @(255, 0, 0),
    @(0, 255, 0),
    @(0, 0, 255),
    @(0, 255, 255),
    @(255, 0, 255),
    @(255, 128, 0),
    @(255, 255, 255)
)
for ($i = 0; $i -lt 8; $i++) {
    $rep4[25 + $i*3] = $colors[$i][0]
    $rep4[26 + $i*3] = $colors[$i][1]
    $rep4[27 + $i*3] = $colors[$i][2]
}

$rep4[49] = 0x01
[int]$sum = 0
for ($i = 3; $i -lt 50; $i++) {
    $sum += $rep4[$i]
}
$rep4[50] = [byte](($sum -shr 8) -band 0xFF)
$rep4[51] = [byte]($sum -band 0xFF)

$pRep4 = [System.Runtime.InteropServices.Marshal]::AllocHGlobal($rep4.Length)
[System.Runtime.InteropServices.Marshal]::Copy($rep4, 0, $pRep4, $rep4.Length)
$set4 = [NativeDriver]::SetFeature($pRep4, $rep4.Length)
[System.Runtime.InteropServices.Marshal]::FreeHGlobal($pRep4)
Write-Host "Report 4 sent! Result: $set4"

Start-Sleep -Milliseconds 600

[NativeDriver]::Close_FeatureDevice()
Write-Host "ALL FACTORY SETTINGS RESTORED SUCCESSFULLY!"
