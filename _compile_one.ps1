param([string]$PluginFile = "OctaKits.cs")
# Offline compile check for a single plugin file against server assemblies.
# Known false positives: stock dlls are not publicized (private fields like
# Item._condition, ItemContainer.availableSlots) - Carbon publicizes them on the server.
$dirs = @(
    'D:\Octavian Rust\Octavian_Test_Server\423\server\RustDedicated_Data\Managed',
    'D:\Octavian Rust\Octavian_Test_Server\423\server\carbon\managed',
    'D:\Octavian Rust\Octavian_Test_Server\423\server\carbon\lib'
)
$seen = @{}
$refs = @()
foreach ($d in $dirs) {
    if (-not (Test-Path $d)) { continue }
    Get-ChildItem $d -Filter *.dll | ForEach-Object {
        $n = $_.Name.ToLower()
        if ($n -match 'sqlite|sqlite3|harmony|dnlib|cecil') { return }
        if (-not $seen.ContainsKey($n)) {
            $seen[$n] = 1
            $refs += ('-r:"' + $_.FullName + '"')
        }
    }
}
$rsp = 'D:\Octavian Rust\OctavianRMS\_refs.rsp'
$refs | Out-File -Encoding ascii $rsp
"Refs: $($refs.Count)"
$csc = 'C:\Program Files\dotnet\sdk\8.0.404\Roslyn\bincore\csc.dll'
if (-not (Test-Path $csc)) {
    $csc = Get-ChildItem 'C:\Program Files\dotnet\sdk' -Recurse -Filter csc.dll |
        Where-Object { $_.FullName -match 'bincore' } | Select-Object -First 1 -ExpandProperty FullName
}
"csc: $csc"
$out = "$env:TEMP\plugcheck_" + [IO.Path]::GetFileNameWithoutExtension($PluginFile) + ".dll"
& dotnet $csc -nologo -target:library -nowarn:1701,1702,8019 -out:"$out" "@$rsp" "D:\Octavian Rust\OctavianRMS\plugins\$PluginFile" 2>&1 | Select-Object -First 60
