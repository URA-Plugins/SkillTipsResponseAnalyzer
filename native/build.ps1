$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & cargo build --locked --release --target x86_64-pc-windows-msvc
    if ($LASTEXITCODE -ne 0) { throw "cargo build 失败，退出码 $LASTEXITCODE" }

    $nativeDll = Join-Path $PSScriptRoot 'target/x86_64-pc-windows-msvc/release/skill_tips_bridge.dll'
    $nativeBytes = [IO.File]::ReadAllBytes($nativeDll)
    $nativeStream = [IO.MemoryStream]::new($nativeBytes, $false)
    $nativePe = [Reflection.PortableExecutable.PEReader]::new($nativeStream)
    try {
        if ($nativePe.PEHeaders.CoffHeader.Machine -ne [Reflection.PortableExecutable.Machine]::Amd64) { throw '原生 DLL 不是 PE x64' }
        function Get-NativeRvaOffset([int]$Rva) {
            foreach ($section in $nativePe.PEHeaders.SectionHeaders) {
                if ($Rva -ge $section.VirtualAddress -and $Rva -lt ($section.VirtualAddress + $section.SizeOfRawData)) {
                    return $Rva - $section.VirtualAddress + $section.PointerToRawData
                }
            }
            throw "PE RVA 0x$($Rva.ToString('X')) 不在文件节内"
        }
        $exports = Get-NativeRvaOffset $nativePe.PEHeaders.PEHeader.ExportTableDirectory.RelativeVirtualAddress
        $exportCount = [BitConverter]::ToUInt32($nativeBytes, $exports + 24)
        $exportNames = Get-NativeRvaOffset ([BitConverter]::ToUInt32($nativeBytes, $exports + 32))
        $hasEntry = $false
        for ($index = 0; $index -lt $exportCount; $index++) {
            $nameOffset = Get-NativeRvaOffset ([BitConverter]::ToUInt32($nativeBytes, $exportNames + $index * 4))
            $nameEnd = $nameOffset
            while ($nativeBytes[$nameEnd] -ne 0) { $nameEnd++ }
            if ([Text.Encoding]::ASCII.GetString($nativeBytes, $nameOffset, $nameEnd - $nameOffset) -eq 'hachimi_init_v3') { $hasEntry = $true }
        }
        if (!$hasEntry) { throw '原生 DLL 缺少 hachimi_init_v3 导出' }
    }
    finally { $nativePe.Dispose(); $nativeStream.Dispose() }
    Write-Host "已验证 PE x64 与 hachimi_init_v3：$nativeDll"

}
finally { Pop-Location }
