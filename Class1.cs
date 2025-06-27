using EventLoggerPlugin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Spectre.Console;
using System.Diagnostics;
using System.IO.Compression;
using UmamusumeResponseAnalyzer;
using UmamusumeResponseAnalyzer.Entities;
using UmamusumeResponseAnalyzer.Game;
using UmamusumeResponseAnalyzer.Plugin;
using static SkillTipsResponseAnalyzer.i18n.ParseSkillTipsResponse;

namespace SkillTipsResponseAnalyzer
{
    public class SkillTipsResponseAnalyzer : IPlugin
    {
        public string Author => "UmamusumeResponseAnalyzer";
        public string Name => "SkillTipsResponseAnalyzer";
        public Version Version => new(1, 0, 0, 0);
        private JsonSerializer Serializer { get; } = JsonSerializer.Create(new JsonSerializerSettings { Error = IgnoreDeserializeError });

        public async Task UpdatePlugin(ProgressContext ctx)
        {
            var progress = ctx.AddTask($"[UAFScenarioAnalyzer] Update");

            using var client = new HttpClient();
            using var resp = await client.GetAsync($"https://api.github.com/repos/URA-Plugins/{Name}/releases/latest");
            var json = await resp.Content.ReadAsStringAsync();
            var jo = JObject.Parse(json);

            var isLatest = ("v" + Version.ToString()).Equals("v" + jo["tag_name"]?.ToString());
            if (isLatest)
            {
                progress.Increment(progress.MaxValue);
                progress.StopTask();
                return;
            }
            progress.Increment(25);

            using var msg = await client.GetAsync(jo["assets"][0]["browser_download_url"].ToString(), HttpCompletionOption.ResponseHeadersRead);
            using var stream = await msg.Content.ReadAsStreamAsync();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                    break;
                progress.Increment(read / msg.Content.Headers.ContentLength ?? 1 * 0.5);
            }
            using var archive = new ZipArchive(stream);
            archive.ExtractToDirectory(Path.Combine("Plugins", Name), true);
            progress.Increment(25);

            progress.StopTask();
        }
        [Analyzer]
        public void Analyze(JObject jo)
        {
            if (!jo.HasCharaInfo()) return;
            if (jo["data"] is null || jo["data"] is not JObject data) return;
            if (data["chara_info"] is null || data["chara_info"] is not JObject chara_info) return;
            var state = chara_info["state"].ToInt();
            if (state is 2 or 3 && data["unchecked_event_array"]?.Count() == 0)
            {
                ParseSkillTipsResponse(jo.ToObject<Gallop.SingleModeCheckEventResponse>(Serializer));
            }
        }

        private static void IgnoreDeserializeError(object? sender, Newtonsoft.Json.Serialization.ErrorEventArgs e)
        {
            e.ErrorContext.Handled = true;
        }

        public static void ParseSkillTipsResponse(Gallop.SingleModeCheckEventResponse @event)
        {
            var skills = Database.Skills.Apply(@event.data.chara_info);
            var tips = CalculateSkillScoreCost(@event, skills, true);
            var totalSP = @event.data.chara_info.skill_point;
            // 可以进化的天赋技能，即觉醒3、5的那两个金技能
            var upgradableTalentSkills = Database.TalentSkill[@event.data.chara_info.card_id].Where(x => x.Rank <= @event.data.chara_info.talent_level && (x.Rank == 3 || x.Rank == 5));
            var dpResult = DP(tips, ref totalSP, @event.data.chara_info);
            var learn = ReplaceAllSkillWithUpgradeSkill(@event, skills, dpResult.Item1).ToList();
            var willLearnPoint = learn.Sum(x => x.Grade);

            var table = new Table();
            table.Title(string.Format(I18N_Title, @event.data.chara_info.skill_point, @event.data.chara_info.skill_point - totalSP, totalSP));
            table.AddColumns(I18N_Columns_SkillName, I18N_Columns_RequireSP, I18N_Columns_Grade);
            table.Columns[0].Centered();
            foreach (var i in learn)
            {
                table.AddRow($"{i.DisplayName}", $"{i.Cost}", $"{i.Grade}");
            }
            var statusPoint = Database.StatusToPoint[@event.data.chara_info.speed]
                            + Database.StatusToPoint[@event.data.chara_info.stamina]
                            + Database.StatusToPoint[@event.data.chara_info.power]
                            + Database.StatusToPoint[@event.data.chara_info.guts]
                            + Database.StatusToPoint[@event.data.chara_info.wiz];
            AnsiConsole.MarkupLine($"[yellow]速{@event.data.chara_info.speed} 耐{@event.data.chara_info.stamina} 力{@event.data.chara_info.power} 根{@event.data.chara_info.guts} 智{@event.data.chara_info.wiz} [/]");
            var previousLearnPoint = 0; //之前学的技能的累计评价点
            foreach (var i in @event.data.chara_info.skill_array)
            {
                if (i.skill_id > 1000000 && i.skill_id < 2000000) continue; // 嘉年华&LoH技能
                if (i.skill_id.ToString()[0] == '1' && i.skill_id > 100000 && i.skill_id < 200000) //3*固有
                {
                    previousLearnPoint += 170 * i.level;
                }
                else if (i.skill_id.ToString().Length == 5) //2*固有
                {
                    previousLearnPoint += 120 * i.level;
                }
                else
                {
                    if (skills[i.skill_id] == null) continue;
                    var (GroupId, Rarity, Rate) = skills.Deconstruction(i.skill_id);
                    var upgradableSkills = upgradableTalentSkills.FirstOrDefault(x => x.SkillId == i.skill_id);
                    // 学了可进化的技能，且满足进化条件，则按进化计算分数
                    if (upgradableSkills != default && upgradableSkills.CanUpgrade(@event.data.chara_info, out var upgradedSkillId, dpResult.Item1))
                    {
                        previousLearnPoint += skills[upgradedSkillId] == null ? 0 : skills[upgradedSkillId].Grade;
                    }
                    else
                    {
                        previousLearnPoint += skills[i.skill_id] == null ? 0 : skills[i.skill_id].Grade;
                    }
                }
            }
            var totalPoint = willLearnPoint + previousLearnPoint + statusPoint;
            var thisLevelId = GradeRank.GradeToRank.First(x => x.Min <= totalPoint && totalPoint <= x.Max).Id;
            table.Caption(string.Format(I18N_Caption, previousLearnPoint, willLearnPoint, statusPoint, totalPoint, GradeRank.GradeToRank.First(x => x.Id == thisLevelId).Rank));
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine(I18N_ScoreToNextGrade, GradeRank.GradeToRank.First(x => x.Id == thisLevelId + 1).Rank, GradeRank.GradeToRank.First(x => x.Id == thisLevelId + 1).Min - totalPoint);
            AnsiConsole.MarkupLine(string.Empty);

            if (@event.IsScenario(ScenarioType.GrandMasters))
            {
                GameStats.Print();
                AnsiConsole.MarkupLine(string.Empty);
            }

            AnsiConsole.MarkupLine(I18N_ScoreCalculateAttention_1);
            AnsiConsole.MarkupLine(I18N_ScoreCalculateAttention_2);
            AnsiConsole.MarkupLine(I18N_ScoreCalculateAttention_3);
            AnsiConsole.MarkupLine(I18N_ScoreCalculateAttention_4);
            AnsiConsole.MarkupLine(I18N_ScoreCalculateAttention_5);

            #region 计算边际性价比与减少50/100/150/.../500pt的平均性价比
            //计算平均性价比
            var dp = dpResult.Item2;
            var totalSP0 = @event.data.chara_info.skill_point;
            if (totalSP0 > 0)
                AnsiConsole.MarkupLine(I18N_AverageCostEffectiveness, ((double)willLearnPoint / totalSP0).ToString("F3"));
            //计算边际性价比，对totalSP正负50的范围做线性回归
            if (totalSP0 > 50)
            {
                double sxy = 0, sy = 0, sx2 = 0, n = 0;
                for (var x = -50; x <= 50; x++)
                {
                    var y = dp[totalSP0 + x];
                    sxy += x * y;
                    sy += y;
                    sx2 += x * x;
                    n += 1;
                }
                var b = sxy / sx2;
                AnsiConsole.MarkupLine(I18N_MarginalCostEffectiveness, b.ToString("F3"));
            }
            //计算减少50/100/150/.../500pt的平均性价比
            AnsiConsole.MarkupLine(I18N_ExpectedCostEffectiveness);
            for (var t = 1; t <= 10; t++)
            {
                var start = totalSP0 - t * 50 - 25;
                if (start < 0)
                    break;

                //totalSP - t * 50 的前后25个取平均
                var meanScoreReduced = dp.Skip(start).Take(51).Average();
                var eff = (dp[totalSP0] - meanScoreReduced) / (t * 50);
                AnsiConsole.MarkupLine(I18N_ExpectedCostEffectivenessByPrice, t * 50, eff.ToString("F3"));
            }
            #endregion
        }
        public static IEnumerable<SkillData> ReplaceAllSkillWithUpgradeSkill(Gallop.SingleModeCheckEventResponse @event, SkillManager skillmanager, List<SkillData> willLearnSkills)
        {
            skillmanager.Evolve(@event.data.chara_info, willLearnSkills);
            #region 角色进化
            foreach (var baseSkill in skillmanager.GetSkills().Where(x => x.Upgrades.Any(y => y.IsScenarioEvolution == false)))
            {
                var best = baseSkill.Upgrades.Where(x => x.IsScenarioEvolution == false).OrderByDescending(x => x.Grade).First();
                baseSkill.DisplayName = $"{baseSkill.DisplayName}(角色{I18N_Evolved}->{best.DisplayName})";
                baseSkill.Grade = best.Grade;

                var inferior = baseSkill.Inferior;
                while (inferior != null)
                {
                    // 学了下位技能，则减去下位技能的分数
                    if (@event.data.chara_info.skill_array.Any(x => x.skill_id == inferior.Id))
                    {
                        best.Grade -= inferior.Grade;
                        break;
                    }
                    inferior = inferior.Inferior;
                }
            }
            #endregion
            #region 剧本进化
#warning TODO
            var evolvedCount = 0; // 剧本进化最多两个，但是可进化的可能更多
            foreach (var baseSkill in skillmanager.GetSkills().Where(x => x.Upgrades.Any(y => y.IsScenarioEvolution == true)).OrderByDescending(x => x.Upgrades.Max(y => y.Grade)))
            {
                if (evolvedCount == 2) break; // 剧本进化最多两个
                var best = baseSkill.Upgrades.Where(x => x.IsScenarioEvolution == true).OrderByDescending(x => x.Grade).First();
                baseSkill.DisplayName = $"{baseSkill.DisplayName}(剧本{I18N_Evolved}->{best.DisplayName})";
                baseSkill.Grade = best.Grade;

                var inferior = baseSkill.Inferior;
                while (inferior != null)
                {
                    // 学了下位技能，则减去下位技能的分数
                    if (@event.data.chara_info.skill_array.Any(x => x.skill_id == inferior.Id))
                    {
                        best.Grade -= inferior.Grade;
                        break;
                    }
                    inferior = inferior.Inferior;
                }
                evolvedCount += 1;
            }
            #endregion
            return willLearnSkills;
        }

        //按技能性价比排序
        public static List<SkillData> CalculateSkillScoreCost(Gallop.SingleModeCheckEventResponse @event, SkillManager skills, bool removeInferiors)
        {
            var hasUnknownSkills = false;
            var totalSP = @event.data.chara_info.skill_point;
            var tipsRaw = @event.data.chara_info.skill_tips_array;
            var tipsExistInDatabase = tipsRaw.Where(x => skills[(x.group_id, x.rarity)] != null);//去掉数据库中没有的技能，避免报错
            var tipsNotExistInDatabase = tipsRaw.Where(x => skills[(x.group_id, x.rarity)] == null);//数据库中没有的技能
            foreach (var i in tipsNotExistInDatabase)
            {
                hasUnknownSkills = true;
                var lineToPrint = string.Format(I18N_UnknownSkillAlert, i.group_id, i.rarity);
                for (var rarity = 0; rarity < 10; rarity++)
                {
                    var maybeInferiorSkills = skills[(i.group_id, rarity)];
                    if (maybeInferiorSkills != null)
                    {
                        foreach (var inferiorSkill in maybeInferiorSkills)
                        {
                            lineToPrint += string.Format(I18N_UnknownSkillSuperiorSuppose, inferiorSkill.Name);
                        }
                    }
                }
                AnsiConsole.MarkupLine($"[red]{lineToPrint}[/]");
            }
            //翻译技能tips方便使用
            var tips = tipsExistInDatabase
                .SelectMany(x => skills[(x.group_id, x.rarity)])
                .Where(x => x.Rate > 0)
                .ToList();
            //添加天赋技能
            var unknownUma = false;//新出的马娘的天赋技能不在数据库中
            if (Database.TalentSkill.TryGetValue(@event.data.chara_info.card_id, out var value))
            {
                foreach (var i in value.Where(x => x.Rank <= @event.data.chara_info.talent_level))
                {
                    if (!tips.Any(x => x.Id == i.SkillId) && !@event.data.chara_info.skill_array.Any(y => y.skill_id == i.SkillId))
                    {
                        tips.Add(skills[i.SkillId]);
                    }
                }
            }
            else
            {
                unknownUma = true;
            }
            //添加上位技能缺少的下位技能（为方便计算切者技能点）
            foreach (var group in tips.GroupBy(x => x.GroupId))
            {
                var additionalSkills = skills.GetAllByGroupId(group.Key)
                    .Where(x => x.Rarity < group.Max(y => y.Rarity) || x.Rate < group.Max(y => y.Rate))
                    .Where(x => x.Rate > 0);
                var ids = additionalSkills.ExceptBy(tips.Select(x => x.Id), x => x.Id);
                tips.AddRange(ids);
            }

            if (removeInferiors)
            {
                // 保证技能列表中的列表都是最上位技能（有下位技能则去除）
                // 理想中tips里应只保留最上位技能，其所有的下位技能都去除
                var inferiors = tips
                        .SelectMany(x => skills.GetAllByGroupId(x.GroupId))
                        .DistinctBy(x => x.Id)
                        .OrderByDescending(x => x.Rarity)
                        .ThenByDescending(x => x.Rate)
                        .GroupBy(x => x.GroupId)
                        .Where(x => x.Any())
                        .SelectMany(x => tips.Where(y => y.GroupId == x.Key)
                            .OrderByDescending(y => y.Rarity)
                            .ThenByDescending(y => y.Rate)
                            .Skip(1) //跳过当前有的最高级的hint
                            .Select(y => y.Id));
                tips.RemoveAll(x => inferiors.Contains(x.Id)); //只保留最上位技能，下位技能去除
            }

            //把已买技能和它们的下位去掉
            foreach (var i in @event.data.chara_info.skill_array)
            {
                if (i.skill_id > 1000000 && i.skill_id < 2000000) continue; // 嘉年华&LoH技能
                var skill = skills[i.skill_id];
                if (skill == null)
                {
                    hasUnknownSkills = true;
                    AnsiConsole.MarkupLine(I18N_UnknownBoughtSkillAlert, i.skill_id);
                    continue;
                }
                skill.Cost = int.MaxValue;
                if (skill.Inferior != null)
                {
                    do
                    {
                        skill = skill.Inferior;
                        skill.Cost = int.MaxValue;
                    } while (skill.Inferior != null);
                }
            }

            if (unknownUma)
            {
                AnsiConsole.MarkupLine(I18N_UnknownUma, @event.data.chara_info.card_id);
            }
            if (hasUnknownSkills)
            {
                AnsiConsole.MarkupLine(I18N_UnknownSkillExistAlert);
            }
            return tips;
        }

        public static (List<SkillData>, int[]) DP(List<SkillData> tips, ref int totalSP, Gallop.SingleModeChara chara_info)
        {
            var learn = new List<SkillData>();
            // 01背包变种
            var dp = new int[totalSP + 101]; //多计算100pt，用于计算“边际性价比”
            var dpLog = Enumerable.Range(0, totalSP + 101).Select(x => new List<int>()).ToList(); // 记录dp时所选的技能，存技能Id
            for (var i = 0; i < tips.Count; i++)
            {
                var s = tips[i];
                // 读取此技能可以点的所有情况
                int[] SuperiorId = [0, 0, 0];
                int[] SuperiorCost = [int.MaxValue, int.MaxValue, int.MaxValue];
                int[] SuperiorGrade = [int.MinValue, int.MinValue, int.MinValue];

                SuperiorId[0] = s.Id;
                SuperiorCost[0] = s.Cost;
                SuperiorGrade[0] = s.Grade;

                if (SuperiorCost[0] != 0 && s.Inferior != null)
                {
                    s = s.Inferior;
                    SuperiorId[1] = s.Id;
                    SuperiorCost[1] = s.Cost;
                    SuperiorGrade[1] = s.Grade;
                    if (SuperiorCost[1] != 0 && s.Inferior != null)
                    {
                        s = s.Inferior;
                        SuperiorId[2] = s.Id;
                        SuperiorCost[2] = s.Cost;
                        SuperiorGrade[2] = s.Grade;
                    }
                }

                if (SuperiorGrade[0] == 0)
                    SuperiorCost[0] = int.MaxValue;
                if (SuperiorGrade[1] == 0)
                    SuperiorCost[1] = int.MaxValue;
                if (SuperiorGrade[2] == 0)
                    SuperiorCost[2] = int.MaxValue;

                // 退化技能到最低级，方便选择



                for (var j = totalSP + 100; j >= 0; j--)
                {
                    // 背包四种选法
                    // 0-不选
                    // 1-只选此技能
                    // 2-选这个技能和它的上一级技能
                    // 3-选这个技能的最高位技（全点）
                    var choice = new int[4];
                    choice[0] = dp[j];
                    choice[1] = j - SuperiorCost[0] >= 0 ?
                        dp[j - SuperiorCost[0]] + SuperiorGrade[0] :
                        -1;
                    choice[2] = j - SuperiorCost[1] >= 0 ?
                        dp[j - SuperiorCost[1]] + SuperiorGrade[1] :
                        -1;
                    choice[3] =
                        j - SuperiorCost[2] >= 0 ?
                        dp[j - SuperiorCost[2]] + SuperiorGrade[2] :
                        -1;
                    // 判断是否为四种选法中的最优选择
                    if (IsBestOption(0))
                    {
                        dp[j] = choice[0];
                    }
                    else if (IsBestOption(1))
                    {
                        dp[j] = choice[1];
                        dpLog[j] = new(dpLog[j - SuperiorCost[0]])
                        {
                            SuperiorId[0]
                        };
                    }
                    else if (IsBestOption(2))
                    {
                        dp[j] = choice[2];
                        dpLog[j] = new(dpLog[j - SuperiorCost[1]])
                        {
                            SuperiorId[1]
                        };
                    }
                    else if (IsBestOption(3))
                    {
                        dp[j] = choice[3];
                        dpLog[j] = new(dpLog[j - SuperiorCost[2]])
                        {
                            SuperiorId[2]
                        };
                    }

                    bool IsBestOption(int index)
                    {
                        var IsBest = true;
                        for (var k = 0; k < 4; k++)
                            IsBest = choice[index] >= choice[k] && IsBest;
                        return IsBest;
                    }
                    ;
                }
            }
            // 读取最终选择的技能
            var learnSkillId = dpLog[totalSP];
            foreach (var id in learnSkillId)
            {
                foreach (var skill in tips)
                {
                    var inferior = skill.Inferior;
                    var inferiorest = inferior?.Inferior;
                    if (skill.Id == id)
                    {
                        learn.Add(skill);
                        totalSP -= skill.Cost;
                        continue;
                    }
                    else if (inferior != null && inferior.Id == id)
                    {
                        learn.Add(inferior);
                        totalSP -= inferior.Cost;
                        continue;
                    }
                    else if (inferiorest != null && inferiorest.Id == id)
                    {
                        learn.Add(inferiorest);
                        totalSP -= inferiorest.Cost;
                    }
                }
            }
            //learn = [.. learn.OrderByDescending(x => x.HintLevel).ThenBy(x => x.DisplayOrder)];
            learn = [.. learn.OrderBy(x => x.DisplayOrder)];
            return (learn, dp);
        }
    }
}
