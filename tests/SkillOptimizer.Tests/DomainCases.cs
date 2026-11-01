using System.Collections.Immutable;
using Gallop;
using SkillTipsResponseAnalyzer;
using UmamusumeResponseAnalyzer;
using UmamusumeResponseAnalyzer.Entities;

internal static class DomainCases
{
    public static void Run()
    {
        var low = Skill(200002, 1, 1, 100, 101) with { EffectTags = [6] };
        var high = Skill(200001, 1, 2, 200, 103) with { EffectTags = [6] };
        var bad = Skill(200003, 1, -1, -50, 50);
        var evo = Skill(300001, 2, 1, 300, null);
        var other = Skill(200012, 3, 1, 80, 20) with { EffectTags = [7] };
        var chara = Chara();
        chara.skill_tips_array = [new() { group_id = 1, rarity = 1, level = 1 }];
        chara.chara_effect_id_array = [7, 40];
        var data = Data([low, high, bad, evo, other]);
        var catalog = data.CreateCatalog();
        Equal(80, catalog.Cost(low, chara), "additive hint/condition discount, floor");
        Equal(82, catalog.Cost(high, chara), "per-item floor");
        var group = SkillPlanning.Build(chara, catalog).Problem.Groups.Single();
        Equal(162, group.Choices.Single(x => x.Id == high.Id).Cost, "missing lower cost");
        chara.skill_array = [new() { skill_id = low.Id, level = 1 }];
        group = SkillPlanning.Build(chara, catalog).Problem.Groups.Single();
        Equal(82, group.Choices.Single(x => x.Id == high.Id).Cost, "owned lower cost excluded");
        Equal(100, group.Choices.Single(x => x.Id == high.Id).Score, "replacement score delta");
        chara.skill_array = [new() { skill_id = bad.Id, level = 1 }];
        group = SkillPlanning.Build(chara, catalog).Problem.Groups.Single();
        Equal(40, group.Choices.Single(x => x.Id == 0).Cost, "purple removal cost");
        Equal(50, group.Choices.Single(x => x.Id == 0).Score, "purple removal score");
        var purchase = group.Choices.Single(x => x.Id == high.Id);
        Equal(202, purchase.Cost, "purple plus positive chain cost");
        if (!purchase.PurchasedIds.SequenceEqual(new[] { bad.Id, low.Id, high.Id })) throw new Exception("Missing learning steps");
        var blocked = (data with { Skills = [low, high, bad with { SolvableType = 1 }, evo, other] }).CreateCatalog();
        Equal(1, SkillPlanning.Build(chara, blocked).Problem.Groups.Single().Choices.Length, "unremovable purple blocks group");
        CandidateSources(low, high, bad);

        chara = Chara();
        chara.skill_array = [new() { skill_id = low.Id, level = 1 }];
        chara.skill_tips_array = [new() { group_id = 1, rarity = 1 }, new() { group_id = 3, rarity = 1 }];
        chara.skill_upgrade_info_array = [new() { condition_id = 1, current_count = 1, total_count = 2 }];
        var rule = new SkillEvolutionRule(high.Id, evo.Id, SkillEvolutionKind.Character, 1, 3, 0, 0,
            [new(1, 1, SkillConditionKind.EffectCount, 6, "speed")]);
        data = data with { Evolutions = [rule] };
        var problem = SkillPlanning.Build(chara, data.CreateCatalog()).Problem;
        var requirement = problem.Evolutions.Single().Conditions.Single().Single();
        Equal(0, requirement.Current, "subtract baseline count before counting final held skills");
        var held = new[] { high.Id };
        Equal(1, requirement.Current + held.Sum(x => requirement.Contributions[x]), "upper/lower counted once");
        var result = SkillOptimizer.Solve(problem);
        Equal(1, result.Evolutions.Length, "learning unlocks evolution");
        if (!result.Choices.Any(x => x.Id == other.Id)) throw new Exception("Missing speed condition contribution");
        chara.skill_upgrade_info_array = [];
        Equal(0, SkillOptimizer.Solve(SkillPlanning.Build(chara, data.CreateCatalog()).Problem).Evolutions.Length, "missing progress is false");
        data = data with { Evolutions = [rule with { StartDate = long.MaxValue }] };
        Equal(0, SkillPlanning.Build(chara, data.CreateCatalog()).Problem.Evolutions.Length, "training start gate");
        data = data with { Evolutions = [rule with { Rank = 6 }] };
        Equal(0, SkillPlanning.Build(chara, data.CreateCatalog()).Problem.Evolutions.Length, "talent rank gate");

        var unknown = new SkillConditionRule(1, 1, SkillConditionKind.Unverified, 0, "unknown rule");
        chara.skill_upgrade_info_array = [new() { condition_id = 1, current_count = 0, total_count = 1 }, new() { condition_id = 2, current_count = 1, total_count = 1 }];
        data = data with { Evolutions = [rule with { Conditions = [unknown] }],
            ScenarioConditions = [new(1, 3, [new(2, 1, SkillConditionKind.ServerProgress, 0, "scenario")])] };
        Equal(1, SkillOptimizer.Solve(SkillPlanning.Build(chara, data.CreateCatalog()).Problem).Evolutions.Length, "complete scenario alternative");
        data = data with { ScenarioConditions = [] };
        try { SkillPlanning.Build(chara, data.CreateCatalog()); throw new Exception("Unknown learning rule accepted"); }
        catch (InvalidDataException) { }
        data = data with { Evolutions = [rule with { Kind = SkillEvolutionKind.Inherited, Conditions = [] }] };
        result = SkillOptimizer.Solve(SkillPlanning.Build(chara, data.CreateCatalog()).Problem);
        Equal(1, result.Evolutions.Length, "automatic inherited evolution");

        var gated = high with { StatusType = 1, StatusValue = 501 };
        data = Data([low, gated]);
        Equal(0, SkillPlanning.Build(chara, data.CreateCatalog()).Problem.Groups.SelectMany(x => x.Choices).Count(x => x.Id == high.Id), "stat gate");
        Console.WriteLine("PASS: rule adapter (candidate sources, cost chain, discounts, owned skills, purple, final-held counts, server progress, alternatives, start/rank/stat gates, unknown conditions, inherited evolution).");
    }

    static void CandidateSources(SkillDefinition low, SkillDefinition high, SkillDefinition bad)
    {
        var gold = Skill(200004, 1, 3, 300, 107) with { Rarity = 2 };
        var data = Data([low, high, bad, gold]);
        var catalog = data.CreateCatalog();
        var chara = Chara();
        chara.skill_array = [new() { skill_id = low.Id, level = 1 }];
        var problem = SkillPlanning.Build(chara, catalog).Problem;
        var upgrade = problem.Groups.Single().Choices.Single(x => x.Id == high.Id);
        Equal(103, upgrade.Cost, "owned white unlocks upper white without hints");
        Equal(100, upgrade.Score, "owned white upgrade score delta");
        Equal(0, problem.Groups.SelectMany(x => x.Choices).Count(x => x.Id == gold.Id), "owned white does not unlock gold");

        chara.skill_array = [new() { skill_id = bad.Id, level = 1 }];
        chara.skill_point = 254;
        problem = SkillPlanning.Build(chara, catalog).Problem;
        upgrade = problem.Groups.Single().Choices.Single(x => x.Id == high.Id);
        Equal(254, upgrade.Cost, "owned purple unlocks positive chain without hints");
        if (!upgrade.PurchasedIds.SequenceEqual(new[] { bad.Id, low.Id, high.Id })) throw new Exception("Missing purple removal or positive steps");
        Equal(200, SkillOptimizer.Solve(problem).Score, "owned purple can reach highest white score");
        Equal(0, problem.Groups.SelectMany(x => x.Choices).Count(x => x.Id == gold.Id), "owned purple does not unlock gold");

        chara.skill_array = [];
        chara.disable_skill_id_array = [bad.Id];
        chara.skill_point = 204;
        problem = SkillPlanning.Build(chara, catalog).Problem;
        upgrade = problem.Groups.Single().Choices.Single(x => x.Id == high.Id);
        Equal(204, upgrade.Cost, "removed purple unlocks white chain without paying removal again");
        if (!upgrade.PurchasedIds.SequenceEqual(new[] { low.Id, high.Id })) throw new Exception("Removed purple returned to purchase steps");
        Equal(200, SkillOptimizer.Solve(problem).Score, "removed purple can reach highest white score");
        Equal(0, problem.Groups.SelectMany(x => x.Choices).Count(x => x.Id == gold.Id), "removed purple does not unlock gold");
        Equal(0, SkillPlanning.Build(chara, Data([bad]).CreateCatalog()).Problem.Groups.Length, "removed skill with no positive counterpart adds no candidate");

        var gated = (data with { Skills = [low with { StatusType = 1, StatusValue = 501 }, high, bad, gold] }).CreateCatalog();
        Equal(1, SkillPlanning.Build(chara, gated).Problem.Groups.Single().Choices.Length, "white prerequisite stat gate blocks the entire purchase chain");
        chara.skill_array = [new() { skill_id = bad.Id, level = 1 }];
        chara.disable_skill_id_array = [];
        gated = (data with { Skills = [low, high, bad with { StatusType = 1, StatusValue = 501 }, gold] }).CreateCatalog();
        Equal(1, SkillPlanning.Build(chara, gated).Problem.Groups.Single().Choices.Length, "purple removal stat gate blocks the entire purchase chain");

        chara = Chara();
        var talents = (data with { Talents = [new(chara.card_id, low.Id, 1)] }).CreateCatalog();
        problem = SkillPlanning.Build(chara, talents).Problem;
        Equal(1, problem.Groups.SelectMany(x => x.Choices).Count(x => x.Id == high.Id), "ordinary talent unlocks upper white");
        Equal(0, problem.Groups.SelectMany(x => x.Choices).Count(x => x.Id == gold.Id), "ordinary talent does not unlock gold");
        chara.skill_tips_array = [new() { group_id = gold.GroupId, rarity = gold.Rarity }];
        problem = SkillPlanning.Build(chara, catalog).Problem;
        upgrade = problem.Groups.Single().Choices.Single(x => x.Id == gold.Id);
        Equal(311, upgrade.Cost, "gold hint includes necessary white prerequisites");
        if (!upgrade.PurchasedIds.SequenceEqual(new[] { low.Id, high.Id, gold.Id })) throw new Exception("Missing gold prerequisites");
    }

    static void Equal(long expected, long actual, string name)
    { if (expected != actual) throw new Exception($"{name}: expected {expected}, actual {actual}"); }
    static SkillDefinition Skill(int id, int group, int rate, int grade, int? cost)
        => new(id, id.ToString(), group, 1, rate, grade, cost, 0, 0, 0, id, 0, [], [], ["", ""], ["", ""]);
    static SkillPlanningData Data(ImmutableArray<SkillDefinition> skills)
        => new(1, skills, [], [], new Dictionary<int, int> { [1] = 2 }.ToImmutableDictionary(), [],
            new Dictionary<int, int> { [1] = 10, [2] = 20, [3] = 30, [4] = 35, [5] = 40 }.ToImmutableDictionary());
    static SingleModeChara Chara() => new()
    {
        card_id = 1, talent_level = 5, scenario_id = 1, skill_point = 1000, start_time = "2026-11-01 00:00:00",
        speed = 500, stamina = 500, power = 500, guts = 500, wiz = 500,
        skill_array = [], disable_skill_id_array = [], skill_tips_array = [], chara_effect_id_array = [], skill_upgrade_info_array = []
    };
}
