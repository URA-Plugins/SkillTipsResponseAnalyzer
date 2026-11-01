using System.Collections.Immutable;
using Gallop;
using UmamusumeResponseAnalyzer;
using UmamusumeResponseAnalyzer.Entities;

namespace SkillTipsResponseAnalyzer;

internal sealed record SkillPlanningInput(SingleModeChara Chara, SkillCatalog Catalog,
    SkillOptimizationProblem Problem, ImmutableArray<SkillEvolutionRule> Rules);

internal static class SkillPlanning
{
    public static SkillPlanningInput Build(SingleModeChara chara, SkillCatalog catalog)
    {
        var skills = catalog.Skills;
        if (chara.skill_point < 0) throw new InvalidDataException("skill_point < 0");
        var learned = chara.skill_array.ToDictionary(x => x.skill_id);
        var learnedGroups = learned.Keys.GroupBy(id => skills[id].GroupId).ToDictionary(g => g.Key, g => g.Single());
        var skillGroups = skills.Values.ToLookup(x => x.GroupId);
        var available = chara.skill_tips_array.SelectMany(tip => skillGroups[tip.group_id].Where(s => s.Rarity == tip.rarity))
            .Concat(catalog.Data.Talents.Where(t => t.CardId == chara.card_id && t.Rank <= chara.talent_level).Select(t => skills[t.SkillId]))
            .Concat(learned.Keys.Select(id => skills[id]))
            // 游戏从已解除技能的同组非负评价最低项重建普通技能链。
            .Concat(chara.disable_skill_id_array.SelectMany(id => skillGroups[skills[id].GroupId]
                .Where(s => s.Grade >= 0).OrderBy(s => s.Grade).Take(1)))
            .SelectMany(skill => skill.Rarity < 2
                ? skillGroups[skill.GroupId].Where(s => s.Rarity < 2 && s.Rate >= skill.Rate)
                : [skill])
            .Where(s => s.Rate > 0 && s.Cost.HasValue).DistinctBy(s => s.Id).ToArray();
        var groupIds = available.Select(x => x.GroupId).Concat(learnedGroups.Keys).Distinct().Order().ToArray();
        var baseline = learned.Sum(x => SkillScoring.Estimate(skills[x.Key], chara, x.Value.level));
        var groups = new List<SkillChoiceGroup>();
        var stats = new[] { 0, chara.speed, chara.stamina, chara.power, chara.guts, chara.wiz };
        foreach (var groupId in groupIds)
        {
            var initial = learnedGroups.TryGetValue(groupId, out var initialId) ? skills[initialId] : null;
            var initialScore = initial is null ? 0 : SkillScoring.Estimate(initial, chara, learned[initial.Id].level);
            var choices = new List<SkillChoice> { new(initial?.Id ?? 0, 0, 0, [], initial is null ? [] : [initial.Id]) };
            var purple = initial?.Rate < 0 ? initial : null;
            var canRemove = purple is not null && purple.SolvableType == 0 && purple.Cost.HasValue
                && (purple.StatusType == 0 || stats[purple.StatusType] >= purple.StatusValue);
            if (canRemove)
                choices.Add(new(0, catalog.Cost(purple!, chara), -initialScore, [purple!.Id], []));
            var maximum = available.Where(x => x.GroupId == groupId).Select(x => x.Rate).DefaultIfEmpty(0).Max();
            var levels = skillGroups[groupId].Where(x => x.Rate > 0 && x.Rate <= maximum && x.Cost.HasValue)
                .OrderBy(x => x.Rate).ToArray();
            if (levels.GroupBy(x => x.Rate).Any(x => x.Count() != 1))
                throw new InvalidDataException($"Skill group {groupId}: duplicate GroupRate");
            foreach (var target in levels.Where(x => x.Rate > (initial?.Rate ?? 0)))
            {
                if (purple is not null && !canRemove) continue;
                var required = levels.Where(x => x.Rate <= target.Rate && x.Rate > (initial?.Rate ?? 0)).ToArray();
                if (required.Any(x => x.StatusType < 0 || x.StatusType >= stats.Length))
                    throw new InvalidDataException($"Skill group {groupId}: unknown StatusType");
                if (required.Any(x => x.StatusType != 0 && stats[x.StatusType] < x.StatusValue)) continue;
                var cost = required.Sum(x => catalog.Cost(x, chara)) + (purple is null ? 0 : catalog.Cost(purple, chara));
                choices.Add(new(target.Id, cost, SkillScoring.Estimate(target, chara) - initialScore,
                    [.. purple is null ? Array.Empty<int>() : [purple.Id], .. required.Select(x => x.Id)], [target.Id]));
            }
            groups.Add(new(groupId, [.. choices]));
        }
        var heldIds = groups.SelectMany(x => x.Choices).SelectMany(x => x.HeldIds).Distinct().ToHashSet();
        var rules = catalog.GetEvolutions(chara).Where(x => heldIds.Contains(x.BaseSkillId)).ToImmutableArray();
        var progress = chara.skill_upgrade_info_array.ToDictionary(x => x.condition_id);
        var conditions = new Dictionary<int, SkillCountRequirement>();
        SkillCountRequirement Compile(SkillConditionRule rule)
        {
            if (conditions.TryGetValue(rule.Id, out var cached)) return cached;
            SkillCountRequirement result;
            if (!progress.TryGetValue(rule.Id, out var current)) result = new(rule.Id, 0, 1, ImmutableDictionary<int, int>.Empty);
            else if (current.current_count >= current.total_count) result = new(rule.Id, 1, 1, ImmutableDictionary<int, int>.Empty);
            else if (rule.Kind == SkillConditionKind.ServerProgress) result = new(rule.Id, 0, 1, ImmutableDictionary<int, int>.Empty);
            else
            {
                if (rule.Kind == SkillConditionKind.Unverified)
                    throw new InvalidDataException(string.Format(Text("UnsupportedCondition"), rule.Id, rule.Description));
                bool Counts(int id)
                {
                    var skill = skills[id];
                    if (rule.Kind == SkillConditionKind.SkillOwned) return id == rule.Operand;
                    if (skill.DisableCountCondition != 0 || skill.Rate < 0) return false;
                    return rule.Kind switch
                    {
                        SkillConditionKind.TagCount => skill.Tags.Contains(rule.Operand),
                        SkillConditionKind.EffectCount => rule.Operand switch
                        {
                            6 => skill.EffectTags.Any(x => x is 6 or 7),
                            1 => skill.EffectTags.Any(x => x is >= 1 and <= 5),
                            _ => skill.EffectTags.Contains(rule.Operand)
                        },
                        _ => throw new InvalidDataException($"Condition {rule.Id}: {rule.Kind}")
                    };
                }
                var contributions = heldIds.Where(Counts).ToImmutableDictionary(x => x, _ => 1);
                result = new(rule.Id, current.current_count - learned.Keys.Count(Counts), current.total_count, contributions);
            }
            conditions.Add(rule.Id, result);
            return result;
        }
        ImmutableArray<ImmutableArray<SkillCountRequirement>> CompileGroups(IEnumerable<SkillConditionRule> source)
            => [.. source.GroupBy(x => x.Group).Select(g => g.Any(x => progress.TryGetValue(x.Id, out var p) && p.current_count >= p.total_count)
                ? ImmutableArray.Create(new SkillCountRequirement(-g.First().Id, 1, 1, ImmutableDictionary<int, int>.Empty))
                : g.Select(Compile).ToImmutableArray())];
        var evolutions = rules.Select(rule =>
        {
            var normal = catalog.IsComplete(rule, chara)
                ? ImmutableArray<ImmutableArray<SkillCountRequirement>>.Empty : CompileGroups(rule.Conditions);
            if (rule.Kind == SkillEvolutionKind.Character && !normal.IsEmpty)
            {
                var alternatives = catalog.Data.ScenarioConditions.Where(x => x.ScenarioId == chara.scenario_id && x.Rank == rule.Rank).ToArray();
                foreach (var alternative in alternatives)
                {
                    var scenario = CompileGroups(alternative.Conditions);
                    // (A1 && A2) || (B1 && B2) 转为等价 CNF，保留每个条件组的 OR。
                    normal = [.. normal.SelectMany(a => scenario.Select(b => a.AddRange(b)))];
                }
            }
            return new SkillEvolutionChoice(rule.BaseSkillId, rule.SkillId,
                SkillScoring.Estimate(skills[rule.SkillId], chara) - SkillScoring.Estimate(skills[rule.BaseSkillId], chara),
                rule.Kind == SkillEvolutionKind.Scenario, rule.Kind == SkillEvolutionKind.Inherited, normal);
        }).ToImmutableArray();
        return new(chara, catalog, new(chara.skill_point, baseline, catalog.Data.ScenarioLimits[chara.scenario_id], [.. groups], evolutions), rules);
    }

    internal static string Text(string key) => i18n.ParseSkillTipsResponse.ResourceManager.GetString(key,
        i18n.ParseSkillTipsResponse.Culture) ?? throw new MissingMemberException(key);
}
