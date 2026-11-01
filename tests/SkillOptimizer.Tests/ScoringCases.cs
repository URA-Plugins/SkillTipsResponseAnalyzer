using System.Collections.Immutable;
using Gallop;
using SkillTipsResponseAnalyzer;
using UmamusumeResponseAnalyzer;
using UmamusumeResponseAnalyzer.Entities;

internal static class ScoringCases
{
    public static void Run()
    {
        var chara = Chara();
        chara.proper_running_style_senko = 7;
        chara.proper_distance_long = 6;
        var multiple = Skill(200001, 217, [101, 102, 201, 204]);
        Equal(215, SkillScoring.Estimate(multiple, chara), "highest aptitude in each tag category");
        Equal(215, SkillScoring.Estimate(multiple with { Tags = [204, 201, 102, 101] }, chara), "tag order independence");

        chara.proper_running_style_nige = 7;
        chara.proper_distance_short = 7;
        Equal(212, SkillScoring.Estimate(Skill(200002, 175, [101, 201]), chara), "one rounding after both aptitude multipliers");
        chara.proper_running_style_nige = 6;
        Equal(215, SkillScoring.Estimate(Skill(200002, 217, [101, 201]), chara), "217 * 0.9 * 1.1 rounds to 215");
        chara.proper_running_style_nige = 4;
        chara.proper_distance_short = 6;
        Equal(156, SkillScoring.Estimate(Skill(200002, 217, [101, 201]), chara), "217 * 0.8 * 0.9 rounds to 156");

        chara.proper_running_style_senko = 6;
        chara.proper_running_style_sashi = 4;
        var transfer = Skill(410051, 633, [102, 103]) with
        {
            Conditions = ["running_style==2&is_finalcorner==1@running_style==3&is_finalcorner==1", "is_activate_other_skill_detail==1&is_last_straight==1"]
        };
        Equal(570, SkillScoring.Estimate(transfer, chara), "secondary effect does not erase aptitude tags");
        chara.proper_distance_mile = 5;
        var inherited = Skill(92111091, 240, [202, 203]) with
        {
            Preconditions = ["track_id==10009&course_distance==1600@track_id==10008&course_distance==2000", ""],
            Conditions = ["ground_type==1&phase>=2&corner!=0", ""]
        };
        Equal(216, SkillScoring.Estimate(inherited, chara), "inherited evolution distance tags");
        var generic = Skill(900281, 180, []) with { Conditions = ["distance_type==1&phase==2", "phase==2"] };
        Equal(180, SkillScoring.Estimate(generic, chara), "conditional bonus does not add scoring aptitude");

        Equal(1020, SkillScoring.Estimate(Skill(100021, 340, [101]), chara, 6), "rare unique skill level");
        Equal(720, SkillScoring.Estimate(Skill(10021, 240, [101]), chara, 6), "unique skill level");
        Equal(-129, SkillScoring.Estimate(Skill(200013, -129, []), chara), "negative skill score");
        Equal(0, SkillScoring.Estimate(Skill(1100011, -500, []), chara), "event skill exclusion");

        EvolutionQuota();
        PurchaseRounding();
        Console.WriteLine("PASS: skill scoring (master tags, multiple aptitudes, single rounding, conditional effects, unique levels, negative skills, evolution allocation, purchase score before cost).");
    }

    static void PurchaseRounding()
    {
        var chara = Chara();
        chara.proper_running_style_nige = 4;
        chara.proper_distance_long = 6;
        chara.proper_distance_middle = 6;
        var cheaper = Skill(202812, 217, [101, 204]);
        var higherScore = Skill(201132, 174, [203]);
        var choices = new[] { (Skill: cheaper, Cost: 112), (Skill: higherScore, Cost: 140) }
            .Select(x => new SkillChoiceGroup(x.Skill.GroupId,
                [new(0, 0, 0, [], []), new(x.Skill.Id, x.Cost, SkillScoring.Estimate(x.Skill, chara), [x.Skill.Id], [x.Skill.Id])]))
            .ToImmutableArray();
        var result = SkillOptimizer.Solve(new(140, 0, 0, choices, []));
        Equal(157, result.Score, "higher score wins over cheaper rounded tie");
        Equal(140, result.Cost, "spend more when it increases score");
        Equal(higherScore.Id, result.FinalSkillIds.Single(), "select higher-scoring skill");
    }

    static void EvolutionQuota()
    {
        var chara = Chara();
        chara.proper_running_style_senko = 6;
        chara.proper_running_style_sashi = 6;
        var bases = new[] { Skill(202711, 508, [102, 103]), Skill(200361, 508, []), Skill(203501, 633, []) };
        var upgraded = new[] { Skill(410051, 633, [102, 103]), Skill(410021, 633, []), Skill(410011, 758, []) };
        var scores = bases.Select(skill => SkillScoring.Estimate(skill, chara)).ToArray();
        var groups = bases.Select(skill => new SkillChoiceGroup(skill.GroupId, [new(skill.Id, 0, 0, [], [skill.Id])])).ToImmutableArray();
        var evolutions = upgraded.Select((skill, index) => new SkillEvolutionChoice(bases[index].Id, skill.Id,
            SkillScoring.Estimate(skill, chara) - scores[index], true, false, [])).ToImmutableArray();
        Equal(113, evolutions[0].ScoreGain, "low-aptitude evolution gain");
        var result = SkillOptimizer.Solve(new(0, scores.Sum(), 2, groups, evolutions));
        if (!result.Evolutions.Select(x => x.ResultId).Order().SequenceEqual(new[] { 410011, 410021 }))
            throw new Exception("Evolution quota was assigned to the lower-value skill");
        Equal(250, result.Score - scores.Sum(), "optimal two-slot evolution gain");
        Equal(12, result.Score - scores.Sum() - evolutions[0].ScoreGain - evolutions[1].ScoreGain, "gain over the misranked allocation");
    }

    static SkillDefinition Skill(int id, int grade, ImmutableArray<int> tags)
        => new(id, id.ToString(), id, 1, 1, grade, null, 0, 0, 0, id, 0, tags, [], ["", ""], ["", ""]);

    static SingleModeChara Chara() => new()
    {
        proper_running_style_nige = 1, proper_running_style_senko = 1, proper_running_style_sashi = 1, proper_running_style_oikomi = 1,
        proper_distance_short = 1, proper_distance_mile = 1, proper_distance_middle = 1, proper_distance_long = 1
    };

    static void Equal(long expected, long actual, string name)
    {
        if (expected != actual) throw new Exception($"{name}: expected {expected}, actual {actual}");
    }
}
