param(
    [Parameter(Mandatory)][string]$RuntimeDirectory,
    [Parameter(Mandatory)][string]$DataDirectory,
    [Parameter(Mandatory)][string]$Packet,
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$Baseline,
    [Parameter(Mandatory)][string]$HistoryManifest,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$destination = [IO.Path]::GetFullPath($OutputDirectory)
$historyPath = [IO.Path]::GetFullPath($HistoryManifest)
$Package = [IO.Path]::GetFullPath($Package)
$baselineManifest = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $Baseline) 'manifest.json') -Raw | ConvertFrom-Json
$expectedPacketHash = ($baselineManifest.files | Where-Object path -EQ 'input.msgpack').sha256
if ((Get-FileHash -LiteralPath $Packet).Hash -ne $expectedPacketHash) { throw 'Packet differs from the frozen baseline' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
. "$PSScriptRoot/Replay-SkillTips.ps1" -RuntimeDirectory $RuntimeDirectory -DataDirectory $DataDirectory -Packet $Packet `
    -Package $Package -Baseline $Baseline -Output (Join-Path $destination 'result.json')

function Read-Packet([string]$Path) {
    [MessagePack.MessagePackSerializer]::ConvertToJson([ReadOnlyMemory[byte]]::new([IO.File]::ReadAllBytes($Path)), $null,
        [Threading.CancellationToken]::None) | ConvertFrom-Json -Depth 100
}
function Read-Chara($Value) {
    [Newtonsoft.Json.JsonConvert]::DeserializeObject[Gallop.SingleModeChara](($Value | ConvertTo-Json -Depth 100))
}
function Assert-Equal($Expected, $Actual, [string]$Name) {
    if ($Expected -ne $Actual) { throw "${Name}: expected $Expected, actual $Actual" }
}
$history = Get-Content -LiteralPath $historyPath -Raw | ConvertFrom-Json
foreach ($file in $history.files) { Assert-Equal $file.sha256 (Get-FileHash -LiteralPath $file.path).Hash "History hash: $($file.path)" }
$catalog = [UmamusumeResponseAnalyzer.Database]::SkillCatalog
$consumerSkills = [UmamusumeResponseAnalyzer.Database]::Skills.Apply($response.data.chara_info)
$consumerSkills.Evolve($response.data.chara_info)
Assert-Equal $snapshot.UsedSkillPoints ($snapshot.Skills.SkillPoints | Measure-Object -Sum).Sum 'Displayed costs'
Assert-Equal $snapshot.RecommendedSkillScore ($snapshot.Skills.Score | Measure-Object -Sum).Sum 'Displayed score gains'
$finalScore = 0
foreach ($id in $snapshot.FinalSkillIds) {
    $owned = $response.data.chara_info.skill_array | Where-Object skill_id -EQ $id
    $level = if ($owned) { $owned.level } else { 1 }
    $finalScore += [UmamusumeResponseAnalyzer.SkillScoring]::Estimate($catalog.Skills[$id], $response.data.chara_info, $level)
}
Assert-Equal $snapshot.PredictedTotalScore ($snapshot.StatusScore+$finalScore) 'Independent final-state scoring'
# 固定历史样本的校准期望；manifest 保留条件正则实现的原始残差。
$archiveHash = '3E9588A96025FF31292191E7ABB68846E6722EC7DEFB7135C107B24B6D582F0C'
$scoreCalibration = @{
    $archiveHash = @{ manifestResidual=6; residual=0 }
    '599646505E31C4D6DD53B7F3914C9758EAF8B9EC0E5DC9D33902716ECEAA54EF' = @{ manifestResidual=0; residual=0 }
}
$scores = @($history.finishes | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.path).Hash
    $expected = $scoreCalibration[$hash]
    if ($null -eq $expected) { throw "Missing score calibration expectation for history packet: $($_.path), SHA256=$hash" }
    Assert-Equal $expected.manifestResidual $_.expectedResidual 'Frozen manifest score residual'
    $common = (Read-Packet $_.path).data.single_mode_finish_common
    $chara = Read-Chara $common.chara_info
    $trained = @($common.trained_chara | Where-Object trained_chara_id -EQ $common.trained_chara_id)
    Assert-Equal 1 $trained.Count 'Matching trained chara'
    Assert-Equal $_.actualScore $trained[0].rank_score 'Server score'
    $skillScore = 0
    foreach ($skill in $chara.skill_array) { $skillScore += [UmamusumeResponseAnalyzer.SkillScoring]::Estimate($catalog.Skills[$skill.skill_id], $chara, $skill.level) }
    $statScore = 0
    foreach ($stat in @($chara.speed,$chara.stamina,$chara.power,$chara.guts,$chara.wiz)) { $statScore += [UmamusumeResponseAnalyzer.Database]::StatusToPoint[$stat] }
    $residual = $trained[0].rank_score - $statScore - $skillScore
    Assert-Equal $expected.residual $residual 'Calibrated score residual'
    @{ packet=$_.path; actual=$trained[0].rank_score; estimated=$statScore+$skillScore; skillScore=$skillScore; statScore=$statScore
        manifestExpectedResidual=$_.expectedResidual; residual=$residual; residualChange=$residual-$_.expectedResidual }
})
$archiveSource = @($history.files | Where-Object sha256 -EQ $archiveHash)
Assert-Equal 1 $archiveSource.Count 'Archive calibration source'
$archive = @((Read-Packet $archiveSource[0].path).data.single_mode_finish_common.trained_chara)
$archiveSkillIds = @($archive.skill_array.skill_id | Sort-Object -Unique)
$missingMaster = @($archiveSkillIds | Where-Object { -not $catalog.Skills.ContainsKey([int]$_) })
Assert-Equal 0 $missingMaster.Count "Archive missing master skills: $($missingMaster -join ', ')"
$archiveResiduals = @(foreach ($trained in $archive) {
    $chara = Read-Chara $trained
    $skillScore = 0
    foreach ($skill in $chara.skill_array) { $skillScore += [UmamusumeResponseAnalyzer.SkillScoring]::Estimate($catalog.Skills[$skill.skill_id], $chara, $skill.level) }
    $statScore = 0
    foreach ($stat in @($chara.speed,$chara.stamina,$chara.power,$chara.guts,$chara.wiz)) { $statScore += [UmamusumeResponseAnalyzer.Database]::StatusToPoint[$stat] }
    $trained.rank_score - $statScore - $skillScore
})
$residualDistribution = @($archiveResiduals | Group-Object | Sort-Object { [int]$_.Name } | ForEach-Object {
    @{ residual=[int]$_.Name; count=$_.Count }
})
$unexplained = @(for ($index = 0; $index -lt $archive.Count; $index++) {
    if ($archiveResiduals[$index] -ne 0) {
        $trained = $archive[$index]
        @{ index=$index; residual=$archiveResiduals[$index]
            stats=@($trained.speed,$trained.stamina,$trained.power,$trained.guts,$trained.wiz) }
    }
})
$archiveCalibration = @{
    packetSha256=$archiveHash; horses=$archive.Count; skillRecords=@($archive.skill_array).Count
    uniqueSkillIds=$archiveSkillIds.Count; missingMasterSkillIds=$missingMaster
    exactMatches=@($archiveResiduals | Where-Object { $_ -eq 0 }).Count
    maximumAbsoluteResidual=($archiveResiduals | ForEach-Object { [Math]::Abs($_) } | Measure-Object -Maximum).Maximum
    totalAbsoluteResidual=($archiveResiduals | ForEach-Object { [Math]::Abs($_) } | Measure-Object -Sum).Sum
    residualDistribution=$residualDistribution; unexplainedSamples=$unexplained
}
Assert-Equal 516 $archiveCalibration.horses 'Archive horse count'
Assert-Equal 17114 $archiveCalibration.skillRecords 'Archive skill record count'
Assert-Equal 773 $archiveCalibration.uniqueSkillIds 'Archive distinct skill count'
Assert-Equal 511 $archiveCalibration.exactMatches 'Archive exact score matches'
Assert-Equal 1 $archiveCalibration.maximumAbsoluteResidual 'Archive maximum score residual'
Assert-Equal 5 $archiveCalibration.totalAbsoluteResidual 'Archive total absolute score residual'
Assert-Equal '454,474,475,481,500' ($unexplained.index -join ',') 'Unexplained archive samples'
$expectedDistribution = @{ '-1'=5; '0'=511 }
Assert-Equal $expectedDistribution.Count $residualDistribution.Count 'Archive residual buckets'
foreach ($bucket in $residualDistribution) {
    Assert-Equal $expectedDistribution[[string]$bucket.residual] $bucket.count "Archive residual $($bucket.residual)"
}
$learning = @($history.learning | ForEach-Object {
    $before = Read-Chara (Read-Packet $_.before).data.chara_info
    $after = Read-Chara (Read-Packet $_.after).data.chara_info
    $request = (Read-Packet $_.request).single_mode_gain_skills_request_common.gain_skill_info_array
    $cost = 0
    foreach ($skill in $request) { $cost += $catalog.Cost($catalog.Skills[$skill.skill_id], $before) }
    Assert-Equal ($before.skill_point-$after.skill_point) $cost 'Historical learning cost'
    $expectedHeld = @($before.skill_array.skill_id) + @($request.skill_id)
    $expectedHeld = @($expectedHeld | Select-Object -Unique | Group-Object { $catalog.Skills[[int]$_].GroupId } | ForEach-Object {
        $_.Group | Sort-Object { $catalog.Skills[[int]$_].Rate } -Descending | Select-Object -First 1
    })
    if (Compare-Object $expectedHeld $after.skill_array.skill_id) { throw 'Historical final held skills differ' }
    $input = $plugin.GetType('SkillTipsResponseAnalyzer.SkillPlanning').GetMethod('Build', $flags).Invoke($null, @($before,$catalog))
    $counts = @($input.Problem.Evolutions.Conditions | ForEach-Object { $_ } | Where-Object { $_.Id -gt 0 -and $_.Contributions.Count -gt 0 } | Sort-Object Id -Unique | ForEach-Object {
        $condition = $_
        $count = $condition.Current
        foreach ($id in $after.skill_array.skill_id) { if ($condition.Contributions.ContainsKey($id)) { $count += $condition.Contributions[$id] } }
        $observed = $after.skill_upgrade_info_array | Where-Object condition_id -EQ $condition.Id
        Assert-Equal ([Math]::Min($count,$condition.Required)) $observed.current_count "Learning condition $($condition.Id)"
        @{ id=$condition.Id; predicted=$count; current=$observed.current_count; total=$observed.total_count }
    })
    @{ request=$_.request; requested=$request.Count; held=$after.skill_array.Length; cost=$cost; remaining=$after.skill_point; counts=$counts }
})

# 通过真实 Host 验包入口，确认新增的原生 DLL 支持没有放宽路径和架构约束。
$hostAssembly = [UmamusumeResponseAnalyzer.Database].Assembly
$validator = $hostAssembly.GetType('UmamusumeResponseAnalyzer.Plugin.PluginPackageValidator').GetMethods($flags) |
    Where-Object { $_.Name -eq 'Validate' -and $_.GetParameters()[0].ParameterType -eq [byte[]] }
$packageBytes = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($Package))
foreach ($badPath in @('../escape.dll','runtimes/win-arm64/native/test.dll','runtimes/win-x64/native/managed.dll')) {
    $stream = [IO.MemoryStream]::new()
    $stream.Write($packageBytes)
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Update, $true)
    $entry = $archive.CreateEntry($badPath)
    $target = $entry.Open()
    if ($badPath.EndsWith('managed.dll')) {
        $source = $archive.GetEntry('Google.Protobuf.dll').Open()
        $source.CopyTo($target)
        $source.Dispose()
    }
    $target.Dispose(); $archive.Dispose()
    try {
        $null = $validator.Invoke($null, @($stream.ToArray(),'SkillTipsResponseAnalyzer.zip',$true))
        throw "Invalid package accepted: $badPath"
    } catch {
        if ($_.Exception.GetBaseException() -isnot [IO.InvalidDataException]) { throw }
    } finally { $stream.Dispose() }
}
@{ scores=$scores; archiveCalibration=$archiveCalibration; learning=$learning; packageValidation='passed'; packetSha256=(Get-FileHash -LiteralPath $Packet).Hash } |
    ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $destination 'validation.json') -Encoding utf8
Write-Host 'PASS: fixed packet replay, repeatability, baseline rescoring, 516-horse archive calibration, historical costs/counts/scores, Host ZIP/native loading and invalid-package rejection.'
