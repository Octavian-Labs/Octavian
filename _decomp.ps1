# Декомпилятор типов из игровых сборок через ICSharpCode.Decompiler (carbon lib).
# Использование: .\_decomp.ps1 -Pattern "Flee|Blackboard" [-Dump] [-ListOnly]
param(
    [Parameter(Mandatory=$true)][string]$Pattern,
    [switch]$Dump,        # сохранить декомпиляцию каждого типа в _decomp_out\
    [switch]$ListOnly,    # только список имён типов
    [string]$OutDir = "D:\Octavian Rust\OctavianRMS\_decomp_out",
    [string]$AsmPath = "D:\Octavian Rust\Octavian_Test_Server\423\server\RustDedicated_Data\Managed\Assembly-CSharp.dll"
)
$ErrorActionPreference = 'Stop'
Add-Type -Path 'D:\Octavian Rust\Octavian_Test_Server\423\server\carbon\managed\lib\ICSharpCode.Decompiler.dll'
$settings = New-Object ICSharpCode.Decompiler.DecompilerSettings
$dec = New-Object ICSharpCode.Decompiler.CSharp.CSharpDecompiler($AsmPath, $settings)
$names = $dec.TypeSystem.MainModule.TypeDefinitions | Where-Object { $_.FullName -match $Pattern } | ForEach-Object { $_.FullName } | Sort-Object
if ($ListOnly -or -not $Dump) { $names | ForEach-Object { $_ }; return }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
foreach ($n in $names) {
    try {
        $code = $dec.DecompileTypeAsString($n)
        $file = Join-Path $OutDir (($n -replace '[\<\>\/\\]', '_') + '.cs')
        $code | Out-File -Encoding utf8 $file
        "OK  $n -> $(Split-Path $file -Leaf) ($($code.Length) chars)"
    } catch { "ERR $n : $($_.Exception.Message)" }
}
