param(
    [Parameter(Mandatory)][string]$RuntimeDirectory,
    [Parameter(Mandatory)][string]$DataDirectory,
    [Parameter(Mandatory)][string]$Packet,
    [Parameter(Mandatory)][string]$Output,
    [string]$Package,
    [string]$Baseline
)
$ErrorActionPreference = 'Stop'
$runtime = [IO.Path]::GetFullPath($RuntimeDirectory)
$packetPath = [IO.Path]::GetFullPath($Packet)
$outputPath = [IO.Path]::GetFullPath($Output)
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
public static class ReplayAssemblyLoader {
    public static Assembly Initialize(string directory, string package) {
        AssemblyLoadContext.Default.Resolving += (_, name) => {
            var path = Path.Combine(directory, name.CultureName ?? "", name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        var host = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory, "UmamusumeResponseAnalyzer.dll"));
        AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory, "MessagePack.dll"));
        if (String.IsNullOrEmpty(package))
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory, "SkillTipsResponseAnalyzer.dll"));
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var manager = host.GetType("UmamusumeResponseAnalyzer.Plugin.PluginManager", true);
        var metadata = manager.GetMethod("ReadPluginPackage", flags).Invoke(null, new object[] { package, "zh-CN" });
        var packages = Array.CreateInstance(metadata.GetType(), 1);
        packages.SetValue(metadata, 0);
        var contextType = manager.GetNestedType("PluginLoadContext", BindingFlags.NonPublic);
        var context = (AssemblyLoadContext)Activator.CreateInstance(contextType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { "skilltips-replay", packages }, null);
        return context.LoadFromAssemblyName(new AssemblyName("SkillTipsResponseAnalyzer"));
    }
}
'@
$plugin = [ReplayAssemblyLoader]::Initialize($runtime, $(if ($Package) { [IO.Path]::GetFullPath($Package) } else { '' }))
[Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('zh-CN')
[Globalization.CultureInfo]::CurrentUICulture = [Globalization.CultureInfo]::GetCultureInfo('zh-CN')
[Environment]::CurrentDirectory = [IO.Path]::GetFullPath($DataDirectory)
$flags = [Reflection.BindingFlags]'Static,Public,NonPublic'
[UmamusumeResponseAnalyzer.Config].GetProperty('Current', $flags).SetValue($null, [UmamusumeResponseAnalyzer.YamlConfig]::new())
if ([UmamusumeResponseAnalyzer.Database]::Initialize().GetAwaiter().GetResult() -ne 'Ready') { throw 'Database unavailable' }
$bytes = [IO.File]::ReadAllBytes($packetPath)
$response = [MessagePack.MessagePackSerializer]::Deserialize[Gallop.SingleModeCheckEventResponse]([ReadOnlyMemory[byte]]::new($bytes), $null, [Threading.CancellationToken]::None)
if ($response.data.chara_info.state -notin @(2, 3) -or $response.data.unchecked_event_array.Length -ne 0) { throw 'Packet does not trigger SkillTips' }
$method = $plugin.GetType('SkillTipsResponseAnalyzer.SkillTipsResponseAnalyzer').GetMethod('ParseSkillTipsResponse', $flags)
$allocated = [GC]::GetTotalAllocatedBytes($true)
$timer = [Diagnostics.Stopwatch]::StartNew()
$snapshot = $method.Invoke($null, @($response))
$timer.Stop()
$calculationAllocated = [GC]::GetTotalAllocatedBytes($true) - $allocated
$json = [Newtonsoft.Json.JsonConvert]::SerializeObject($snapshot, [Newtonsoft.Json.Formatting]::Indented)
[IO.File]::WriteAllText($outputPath, $json)
$metrics = [ordered]@{
    packetSha256 = (Get-FileHash -LiteralPath $packetPath).Hash
    elapsedMilliseconds = $timer.Elapsed.TotalMilliseconds
    allocatedBytes = $calculationAllocated
    peakWorkingSetBytes = [Diagnostics.Process]::GetCurrentProcess().PeakWorkingSet64
}
if ($Package) {
    $metrics.warmRuns = @(1..3 | ForEach-Object {
        $allocated = [GC]::GetTotalAllocatedBytes($true)
        $timer.Restart()
        $repeated = $method.Invoke($null, @($response))
        $timer.Stop()
        $bytesAllocated = [GC]::GetTotalAllocatedBytes($true) - $allocated
        if ([Newtonsoft.Json.JsonConvert]::SerializeObject($repeated) -ne [Newtonsoft.Json.JsonConvert]::SerializeObject($snapshot)) { throw 'Repeated replay produced a different plan' }
        [ordered]@{ elapsedMilliseconds = $timer.Elapsed.TotalMilliseconds; allocatedBytes = $bytesAllocated }
    })
}
$metrics | ConvertTo-Json | Set-Content -LiteralPath "$outputPath.metrics.json" -Encoding utf8
$result = $json | ConvertFrom-Json
if ($Baseline) {
    $old = Get-Content -LiteralPath $Baseline -Raw | ConvertFrom-Json
    $planningType = $plugin.GetType('SkillTipsResponseAnalyzer.SkillPlanning')
    $input = $planningType.GetMethod('Build', $flags).Invoke($null, @($response.data.chara_info, [UmamusumeResponseAnalyzer.Database]::SkillCatalog))
    $problem = $input.Problem
    $fixed = [Newtonsoft.Json.JsonConvert]::SerializeObject($problem) | ConvertFrom-Json -AsHashtable
    foreach ($group in $fixed.Groups) {
        $selected = @($group.Choices | Where-Object { $_.Id -in $old.Skills.SkillId })
        if ($selected.Count -gt 1) { throw "Baseline has multiple final skills in group $($group.Id)" }
        $group.Choices = @(if ($selected.Count -eq 1) { $selected } else { $group.Choices[0] })
    }
    $fixedProblem = [Newtonsoft.Json.JsonConvert]::DeserializeObject(($fixed | ConvertTo-Json -Depth 100), $problem.GetType())
    $solve = $plugin.GetType('SkillTipsResponseAnalyzer.SkillOptimizer').GetMethod('Solve', $flags)
    $rescored = $solve.Invoke($null, @($fixedProblem, [Threading.CancellationToken]::None))
    $originalEvolutionIds = @($old.Skills | ForEach-Object {
        if ($_.Name -match '\((?:角色|剧本)进化->(.+)\)$') {
            $name = $Matches[1]; $baseId = $_.SkillId
            $matches = @($fixed.Evolutions | Where-Object { $_.BaseId -eq $baseId -and [UmamusumeResponseAnalyzer.Database]::SkillCatalog.Skills[$_.ResultId].Name -eq $name })
            if ($matches.Count -ne 1) { throw "Cannot resolve baseline evolution for $baseId ($name)" }
            $matches[0].ResultId
        }
    })
    $fixed.Evolutions = @($fixed.Evolutions | Where-Object { $_.Automatic -or $_.ResultId -in $originalEvolutionIds })
    foreach ($evolution in $fixed.Evolutions) { $evolution.Automatic = $true }
    $originalProblem = [Newtonsoft.Json.JsonConvert]::DeserializeObject(($fixed | ConvertTo-Json -Depth 100), $problem.GetType())
    $original = $solve.Invoke($null, @($originalProblem, [Threading.CancellationToken]::None))
    if (@($original.Evolutions | Where-Object ResultId -In $originalEvolutionIds).Count -ne $originalEvolutionIds.Count) { throw 'Baseline evolution is not eligible under the new rules' }
    $comparison = [ordered]@{
        oldReported = @{ cost=$old.UsedSkillPoints; score=$old.RecommendedSkillScore; evolutions=@($old.Skills | Where-Object Name -Match '→|⇒|进化') }
        oldPurchasesNewRules = ([Newtonsoft.Json.JsonConvert]::SerializeObject($rescored) | ConvertFrom-Json)
        oldExactPlanNewRules = ([Newtonsoft.Json.JsonConvert]::SerializeObject($original) | ConvertFrom-Json)
        newPlan = @{ cost=$result.UsedSkillPoints; score=$result.RecommendedSkillScore; evolutions=$result.Evolutions }
        selectionGain = $result.RecommendedSkillScore - ($rescored.Score - $problem.BaselineScore)
        added = @($result.Skills | Where-Object SkillId -NotIn $old.Skills.SkillId)
        removed = @($old.Skills | Where-Object SkillId -NotIn $result.Skills.SkillId)
        changed = @($result.Skills | ForEach-Object {
            $row = $_; $previous = $old.Skills | Where-Object SkillId -EQ $row.SkillId
            if ($previous -and ($previous.Score -ne $row.Score -or $previous.SkillPoints -ne $row.SkillPoints -or $previous.Name -ne $row.Name)) { @{ old=$previous; new=$row } }
        })
    }
    [IO.File]::WriteAllText("$outputPath.comparison.json", ($comparison | ConvertTo-Json -Depth 100))
}
[pscustomobject]@{Output=$outputPath;Count=$result.Skills.Count;Used=$result.UsedSkillPoints;Remaining=$result.RemainingSkillPoints;Recommended=$result.RecommendedSkillScore;Total=$result.PredictedTotalScore;Warnings=$result.Warnings} | ConvertTo-Json -Compress
