# Дамп API Chaos для переписывания OctaESP на UIFramework. Одноразовый скрипт.
$managed = 'D:\Octavian Rust\Octavian_Test_Server\423\server\RustDedicated_Data\Managed'
$carbon = 'D:\Octavian Rust\Octavian_Test_Server\423\server\carbon\managed'
$ext = 'D:\Octavian Rust\Octavian_Test_Server\423\server\carbon\extensions\Oxide.Ext.Chaos.dll'

[AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve({
    param($s, $e)
    $n = (New-Object Reflection.AssemblyName($e.Name)).Name
    foreach ($d in @($managed, $carbon)) {
        $c = Join-Path $d ($n + '.dll')
        if (Test-Path $c) { return [Reflection.Assembly]::ReflectionOnlyLoadFrom($c) }
    }
    return $null
})

$asm = [Reflection.Assembly]::ReflectionOnlyLoadFrom($ext)
try { $types = $asm.GetTypes() } catch [Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | Where-Object { $_ } }

function DumpType($name) {
    $t = $types | Where-Object { $_.Name -eq $name -or $_.FullName -eq $name } | Select-Object -First 1
    if (-not $t) { Write-Output "--- $name NOT FOUND"; return }
    Write-Output ("=== " + $t.FullName + " (base: " + $t.BaseType.Name + ") ===")
    foreach ($m in $t.GetMethods('Public,Static,Instance,DeclaredOnly')) {
        if ($m.IsSpecialName) { continue }
        try {
            $pars = ($m.GetParameters() | ForEach-Object { $_.ParameterType.Name + ' ' + $_.Name }) -join ', '
            Write-Output ("  " + $m.ReturnType.Name + " " + $m.Name + "(" + $pars + ")")
        } catch {
            Write-Output ("  " + $m.Name + "(?)")
        }
    }
    foreach ($f in $t.GetFields('Public,Static')) { Write-Output ("  static field: " + $f.FieldType.Name + " " + $f.Name) }
    foreach ($f in $t.GetFields('Public,Instance')) { Write-Output ("  field: " + $f.FieldType.Name + " " + $f.Name) }
    foreach ($p in $t.GetProperties()) { Write-Output ("  prop: " + $p.PropertyType.Name + " " + $p.Name) }
}

DumpType 'ChaosPrefab'
DumpType 'ChaosUI'
DumpType 'CommandCallbackHandler'
DumpType 'ChaosStyle'
DumpType 'ChaosPlugin'
DumpType 'TextContainer'
DumpType 'Layer'
DumpType 'UIUser'
Write-Output "=== enum values ==="
foreach ($n in @('Layer','MenuType')) {
    $t = $types | Where-Object { $_.Name -eq $n } | Select-Object -First 1
    if ($t -and $t.IsEnum) { Write-Output ($n + ': ' + ([Enum]::GetNames($t) -join ', ')) }
}
