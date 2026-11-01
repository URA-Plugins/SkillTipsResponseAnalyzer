using System.Collections.Immutable;
using SkillTipsResponseAnalyzer;

var random = new Random(71031);
var slackCases = 0;
for (var sample = 0; sample < 2000; sample++)
{
    var groups = Enumerable.Range(1, random.Next(2, 7)).Select(group => new SkillChoiceGroup(group,
    [new(0, 0, 0, [], []), new(group * 10, random.Next(1, 30), random.Next(-10, 45), [group * 10], [group * 10]),
        new(group * 10 + 1, random.Next(1, 30), random.Next(-10, 45), [group * 10, group * 10 + 1], [group * 10 + 1])])).ToImmutableArray();
    var requirement = new SkillCountRequirement(1, random.Next(0, 2), 2,
        groups.SelectMany(x => x.Choices).SelectMany(x => x.HeldIds).Where(x => x >= 20).ToImmutableDictionary(x => x, _ => 1));
    var alternative = new SkillCountRequirement(2, 0, 1, new Dictionary<int, int> { [21] = 1 }.ToImmutableDictionary());
    var problem = new SkillOptimizationProblem(random.Next(5, 38), 100, random.Next(0, 3), groups,
    [new(10, 100, random.Next(-5, 35), false, false, [[requirement, alternative]]), new(10, 101, 32, true, false, []),
        new(20, 200, 15, true, false, [[requirement], [alternative]]), new(21, 201, random.Next(-8, 20), false, true, [])]);
    var actual = SkillOptimizer.Solve(problem);
    var expected = Exhaustive(problem);
    if ((actual.Score, actual.Cost) != expected)
        throw new Exception($"Sample {sample}: CP-SAT {(actual.Score, actual.Cost)}, exhaustive {expected}");
    if (actual.Cost < problem.Budget) slackCases++;
}
foreach (var (name, score, cheapCost, expensiveScore, expectedScore, expectedCost) in new[]
{
    ("score before spending", 100, 70, 99, 100, 70),
    ("minimum cost at equal score", 100, 70, 100, 100, 70),
    ("score before efficiency", 100, 10, 101, 101, 100)
})
{
    var problem = new SkillOptimizationProblem(100, 0, 0,
        [new(1, [new(0, 0, 0, [], []), new(1, cheapCost, score, [1], [1]), new(2, 100, expensiveScore, [2], [2])])], []);
    var actual = SkillOptimizer.Solve(problem);
    if ((actual.Score, actual.Cost) != (expectedScore, expectedCost))
        throw new Exception($"{name}: expected {(expectedScore, expectedCost)}, actual {(actual.Score, actual.Cost)}");
}
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try { SkillOptimizer.Solve(new(0, 0, 0, [], []), cancelled.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { }
}
Console.WriteLine($"PASS: 2000 exhaustive comparisons with independent scores/costs ({slackCases} leave points); score/spending/efficiency ordering; cancellation.");
DomainCases.Run();
ScoringCases.Run();
foreach (var path in args)
{
    var catalog = UmamusumeResponseAnalyzer.Entities.SkillCatalog.Load(path);
    var conditions = catalog.Data.Evolutions.SelectMany(x => x.Conditions).DistinctBy(x => x.Id).ToArray();
    var unknown = conditions.Where(x => x.Kind == UmamusumeResponseAnalyzer.Entities.SkillConditionKind.Unverified).ToArray();
    if (unknown.Length != 0) throw new Exception($"Unparsed master conditions: {string.Join(", ", unknown.Select(x => x.Id))}");
    if (!catalog.Skills[200012].EffectTags.SequenceEqual(new[] { 1 })
        || !catalog.Skills[201011].EffectTags.SequenceEqual(new[] { 14 }))
        throw new Exception("Generated green/debuff effect tags differ from GameAssembly");
    if (!conditions.Any(x => x.Kind == UmamusumeResponseAnalyzer.Entities.SkillConditionKind.SkillOwned))
        throw new Exception("Missing specific held-skill rules");
    Console.WriteLine($"PASS: master catalog {Path.GetFileName(Path.GetDirectoryName(path))}: {catalog.Skills.Count} skills, {conditions.Length} conditions.");
}

static (long Score, int Cost) Exhaustive(SkillOptimizationProblem problem)
{
    var best = (Score: long.MinValue, Cost: int.MaxValue);
    void Visit(int index, List<SkillChoice> choices)
    {
        if (index < problem.Groups.Length)
        {
            foreach (var choice in problem.Groups[index].Choices)
            {
                choices.Add(choice); Visit(index + 1, choices); choices.RemoveAt(choices.Count - 1);
            }
            return;
        }
        var cost = choices.Sum(x => x.Cost);
        if (cost > problem.Budget) return;
        var held = choices.SelectMany(x => x.HeldIds).ToHashSet();
        var eligible = problem.Evolutions.Where(x => held.Contains(x.BaseId) && x.Conditions.All(group => group.Any(condition =>
            condition.Current + condition.Contributions.Where(x => held.Contains(x.Key)).Sum(x => x.Value) >= condition.Required))).ToArray();
        for (var mask = 0; mask < (1 << eligible.Length); mask++)
        {
            var chosen = eligible.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
            if (eligible.Where(x => x.Automatic).Except(chosen).Any() || chosen.Count(x => x.Scenario) > problem.ScenarioLimit
                || chosen.GroupBy(x => x.BaseId).Any(x => x.Count() > 1)) continue;
            var score = problem.BaselineScore + choices.Sum(x => x.Score) + chosen.Sum(x => x.ScoreGain);
            if (score > best.Score || score == best.Score && cost < best.Cost) best = (score, cost);
        }
    }
    Visit(0, []);
    return best;
}
