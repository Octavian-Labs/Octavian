# Дамп "трудных" сигнатур ClientRPC (generic/params) + NetWrite + SendInfo — одноразовый скрипт.
$managed = 'D:\Octavian Rust\Octavian_Test_Server\423\server\RustDedicated_Data\Managed'

[AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve({
    param($s, $e)
    $n = (New-Object Reflection.AssemblyName($e.Name)).Name
    foreach ($root in @($managed, "$managed\..\..\carbon\managed")) {
        $c = Join-Path (Resolve-Path $root -ErrorAction SilentlyContinue) ($n + '.dll')
        if (Test-Path $c) { return [Reflection.Assembly]::ReflectionOnlyLoadFrom($c) }
    }
    return $null
})

$asms = @(
    [Reflection.Assembly]::ReflectionOnlyLoadFrom((Join-Path $managed 'Assembly-CSharp.dll')),
    [Reflection.Assembly]::ReflectionOnlyLoadFrom((Join-Path $managed 'Facepunch.Network.dll'))
)

foreach ($asm in $asms) {
    try { $types = $asm.GetTypes() } catch [Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | Where-Object { $_ } }
    foreach ($name in @('BaseEntity','BaseNetworkable','NetWrite','Net','SendInfo','Message','StringPool')) {
        $t = $types | Where-Object { $_.Name -eq $name } | Select-Object -First 1
        if (-not $t) { continue }
        Write-Output ("=== " + $t.FullName + " ===")
        foreach ($m in $t.GetMethods('Public,Static,Instance,DeclaredOnly')) {
            if ($m.IsSpecialName) { continue }
            if ($name -in @('BaseEntity','BaseNetworkable') -and $m.Name -notlike '*RPC*' -and $m.Name -notlike '*Rpc*') { continue }
            if ($name -in @('NetWrite','Net','Message','StringPool') -and $m.Name -notmatch 'Write|Send|PacketID|EntityID|Start|Bytes|Get$|ToID') { continue }
            try {
                $gen = ''
                if ($m.IsGenericMethodDefinition) { $gen = '<' + (($m.GetGenericArguments() | ForEach-Object { $_.Name }) -join ',') + '>' }
                $pars = ($m.GetParameters() | ForEach-Object { $_.ParameterType.FullName + ' ' + $_.Name }) -join ', '
                Write-Output ("  " + $m.Name + $gen + "(" + $pars + ")")
            } catch { Write-Output ("  " + $m.Name + "(?)") }
        }
    }
}
