using Google.OrTools.Sat;
using System.Collections.Immutable;

namespace SkillTipsResponseAnalyzer;

internal sealed record SkillChoice(int Id, int Cost, int Score, ImmutableArray<int> PurchasedIds, ImmutableArray<int> HeldIds);
internal sealed record SkillChoiceGroup(int Id, ImmutableArray<SkillChoice> Choices);
internal sealed record SkillCountRequirement(int Id, int Current, int Required, ImmutableDictionary<int, int> Contributions);
internal sealed record SkillEvolutionChoice(int BaseId, int ResultId, int ScoreGain, bool Scenario, bool Automatic,
    ImmutableArray<ImmutableArray<SkillCountRequirement>> Conditions);
internal sealed record SkillOptimizationProblem(int Budget, int BaselineScore, int ScenarioLimit,
    ImmutableArray<SkillChoiceGroup> Groups, ImmutableArray<SkillEvolutionChoice> Evolutions);
internal sealed record SkillOptimizationResult(string Status, long Score, long UpperBound, int Cost,
    ImmutableArray<SkillChoice> Choices, ImmutableArray<SkillEvolutionChoice> Evolutions)
{
    public ImmutableArray<int> PurchaseSkillIds => [.. Choices.SelectMany(x => x.PurchasedIds).Distinct()];
    public ImmutableArray<int> LearnedSkillIds => [.. Choices.SelectMany(x => x.HeldIds)];
    public ImmutableArray<int> FinalSkillIds
    {
        get
        {
            var replacements = Evolutions.ToDictionary(x => x.BaseId, x => x.ResultId);
            return [.. LearnedSkillIds.Select(id => replacements.GetValueOrDefault(id, id))];
        }
    }
}

internal static class SkillOptimizer
{
    public static SkillOptimizationResult Solve(SkillOptimizationProblem problem, CancellationToken cancellation = default)
    {
        var model = new CpModel();
        var choices = problem.Groups.SelectMany(g => g.Choices.Select(c => (Group: g.Id, Choice: c,
            Variable: model.NewBoolVar($"choice:{g.Id}:{c.Id}")))).ToArray();
        foreach (var group in choices.GroupBy(x => x.Group))
            model.AddExactlyOne(group.Select(x => (ILiteral)x.Variable));
        var cost = LinearExpr.WeightedSum(choices.Select(x => x.Variable), choices.Select(x => (long)x.Choice.Cost));
        model.Add(cost <= problem.Budget);
        var owned = new Dictionary<int, IntVar>();
        foreach (var id in choices.SelectMany(x => x.Choice.HeldIds).Distinct().Order())
        {
            var variable = model.NewBoolVar($"held:{id}");
            model.Add(variable == LinearExpr.Sum(choices.Where(x => x.Choice.HeldIds.Contains(id)).Select(x => x.Variable)));
            owned.Add(id, variable);
        }
        var conditions = new Dictionary<int, BoolVar>();
        BoolVar Condition(SkillCountRequirement condition)
        {
            if (conditions.TryGetValue(condition.Id, out var existing)) return existing;
            var result = model.NewBoolVar($"condition:{condition.Id}");
            var terms = condition.Contributions.Where(x => owned.ContainsKey(x.Key)).ToArray();
            var count = condition.Current + LinearExpr.WeightedSum(terms.Select(x => owned[x.Key]), terms.Select(x => (long)x.Value));
            model.Add(count >= condition.Required).OnlyEnforceIf(result);
            model.Add(count < condition.Required).OnlyEnforceIf(result.Not());
            conditions.Add(condition.Id, result);
            return result;
        }
        var evolutions = problem.Evolutions.Select((e, index) =>
        {
            var result = model.NewBoolVar($"evolution:{e.BaseId}:{e.ResultId}");
            var groups = e.Conditions.Select((group, number) =>
            {
                var complete = model.NewBoolVar($"group:{index}:{number}");
                var terms = group.Select(Condition).ToArray();
                model.AddBoolOr(terms.Select(x => (ILiteral)x)).OnlyEnforceIf(complete);
                model.AddBoolAnd(terms.Select(x => x.Not())).OnlyEnforceIf(complete.Not());
                return complete;
            }).ToArray();
            if (!owned.TryGetValue(e.BaseId, out var baseOwned)) model.Add(result == 0);
            else
            {
                model.Add(result <= baseOwned);
                foreach (var group in groups) model.Add(result <= group);
                if (e.Automatic) model.Add(result >= baseOwned + LinearExpr.Sum(groups) - groups.Length);
            }
            return (Evolution: e, Variable: result);
        }).ToArray();
        foreach (var group in evolutions.GroupBy(x => x.Evolution.BaseId))
            model.AddAtMostOne(group.Select(x => (ILiteral)x.Variable));
        model.Add(LinearExpr.Sum(evolutions.Where(x => x.Evolution.Scenario).Select(x => x.Variable)) <= problem.ScenarioLimit);
        var score = problem.BaselineScore
            + LinearExpr.WeightedSum(choices.Select(x => x.Variable), choices.Select(x => (long)x.Choice.Score))
            + LinearExpr.WeightedSum(evolutions.Select(x => x.Variable), evolutions.Select(x => (long)x.Evolution.ScoreGain));
        model.Maximize(score);
        var validation = model.Validate();
        if (validation.Length != 0) throw new InvalidDataException(validation);
        using var solver = new CpSolver { StringParameters = "num_search_workers:1 random_seed:0" };
        using var registration = cancellation.Register(solver.StopSearch);
        cancellation.ThrowIfCancellationRequested();
        var status = solver.Solve(model);
        cancellation.ThrowIfCancellationRequested();
        if (status != CpSolverStatus.Optimal)
            throw new InvalidOperationException($"Skill optimization: {status}");
        var best = solver.Value(score);
        model.Add(score == best);
        model.Minimize(cost);
        status = solver.Solve(model);
        cancellation.ThrowIfCancellationRequested();
        if (status != CpSolverStatus.Optimal)
            throw new InvalidOperationException($"Skill cost optimization: {status}");
        return new("Optimal", best, best, checked((int)solver.Value(cost)),
            [.. choices.Where(x => solver.BooleanValue(x.Variable)).Select(x => x.Choice)],
            [.. evolutions.Where(x => solver.BooleanValue(x.Variable)).Select(x => x.Evolution)]);
    }
}
