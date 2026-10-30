using UmamusumeResponseAnalyzer;
using UmamusumeResponseAnalyzer.Entities;
using UmamusumeResponseAnalyzer.TerminalGui;
using UmamusumeResponseAnalyzer.Plugin;
using Terminal.Gui.App;
using System.Net.WebSockets;
using WatsonWebserver.Core;
using static SkillTipsResponseAnalyzer.i18n.ParseSkillTipsResponse;

namespace SkillTipsResponseAnalyzer
{
    public class SkillTipsResponseAnalyzer : IPlugin
    {
        const string WorkspaceTitle = "SkillTipsResponseAnalyzer";

        readonly object trainingGate = new();
        readonly SkillTipsBridge bridge = new();
        SkillTipsSettings settings = new();
        IPluginContext context = null!;
        Workspace? workspace;

        public void Initialize(IPluginContext context)
        {
            this.context = context;
            settings = SkillTipsSettings.Load();
            context.Analyzers.Register<Gallop.SingleModeCheckEventResponse>(
                AnalyzerKind.Response,
                [EndpointPattern.Wildcard("/umamusume/single_mode*/check_event")],
                invocation => Analyze(
                    invocation.Payload.data.chara_info,
                    invocation.Payload.data.unchecked_event_array));
            context.Analyzers.Register<Gallop.SingleModeLoadResponse>(
                AnalyzerKind.Response,
                [EndpointPattern.Wildcard("/umamusume/single_mode*/load")],
                invocation => Analyze(
                    invocation.Payload.data.single_mode_load_common.chara_info,
                    invocation.Payload.data.single_mode_load_common.unchecked_event_array));
            context.Analyzers.Register<Gallop.SingleModeFinishResponse>(
                AnalyzerKind.Response,
                [EndpointPattern.Wildcard("/umamusume/single_mode*/finish")],
                _ => AnalyzeFinish());
        }

        public async Task ConfigPromptAsync(IApplication application, CancellationToken cancellationToken = default)
        {
            var saved = await SkillTipsSettings.PromptAsync(application, cancellationToken);
            lock (trainingGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                saved.Save();
                settings = saved;
            }
        }

        [WebSocketRoute("bridge")]
        Task ConnectBridgeAsync(HttpContextBase httpContext, WebSocket socket, CancellationToken cancellationToken)
            => bridge.AcceptAsync(httpContext, socket, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            RemoveWorkspace();
            await bridge.DisposeAsync().ConfigureAwait(false);
        }

        ValueTask Analyze(
            Gallop.SingleModeChara charaInfo,
            Gallop.SingleModeEventInfo[] uncheckedEvents)
        {
            var shouldPublish = charaInfo.state is 2 or 3 && uncheckedEvents.Length == 0;
            lock (trainingGate)
            {
                if (!shouldPublish)
                    return ValueTask.CompletedTask;
                var response = new Gallop.SingleModeCheckEventResponse
                {
                    data = new()
                    {
                        chara_info = charaInfo,
                        unchecked_event_array = uncheckedEvents
                    }
                };
                var display = ParseSkillTipsResponse(response);
                workspace ??= Workspace.Create(WorkspaceTitle);
                workspace.SetPanel(
                    "skill-plan",
                    "技能评分建议",
                    SkillTipsDisplayRenderer.Render(display, settings.SortByCostEffectiveness, bridge, context.Application, workspace),
                    fullBleed: true);
                if (display.Warnings.Length != 0)
                {
                    workspace.Notify(
                        string.Join(Environment.NewLine, display.Warnings),
                        UiSeverity.Warning);
                }
            }

            return ValueTask.CompletedTask;
        }

        ValueTask AnalyzeFinish()
        {
            RemoveWorkspace();
            return ValueTask.CompletedTask;
        }

        void RemoveWorkspace()
        {
            lock (trainingGate)
            {
                workspace?.Remove();
                workspace = null;
            }
        }

        static SkillTipsDisplaySnapshot ParseSkillTipsResponse(Gallop.SingleModeCheckEventResponse @event)
        {
            var warnings = new List<string>();
            var skills = Database.Skills.Apply(@event.data.chara_info);
            ReplaceAllSkillWithUpgradeSkill(@event, skills, []);
            var tips = CalculateSkillScoreCost(@event, skills, true, warnings);
            var totalSP = @event.data.chara_info.skill_point;
            var upgradableTalentSkills = Database.TalentSkill.TryGetValue(@event.data.chara_info.card_id, out var talentSkills)
                ? talentSkills.Where(x => x.Rank <= @event.data.chara_info.talent_level && (x.Rank == 3 || x.Rank == 5))
                : [];
            var dpResult = DP(tips, ref totalSP);
            var learn = ReplaceAllSkillWithUpgradeSkill(@event, skills, dpResult.Item1).ToList();
            var willLearnPoint = learn.Sum(x => x.Grade);

            var statusPoint = Database.StatusToPoint[@event.data.chara_info.speed]
                            + Database.StatusToPoint[@event.data.chara_info.stamina]
                            + Database.StatusToPoint[@event.data.chara_info.power]
                            + Database.StatusToPoint[@event.data.chara_info.guts]
                            + Database.StatusToPoint[@event.data.chara_info.wiz];

            var previousLearnPoint = 0;
            foreach (var i in @event.data.chara_info.skill_array)
            {
                if (i.skill_id > 1000000 && i.skill_id < 2000000) continue;
                if (i.skill_id.ToString()[0] == '1' && i.skill_id > 100000 && i.skill_id < 200000)
                {
                    previousLearnPoint += 170 * i.level;
                }
                else if (i.skill_id.ToString().Length == 5)
                {
                    previousLearnPoint += 120 * i.level;
                }
                else
                {
                    if (!skills.TryFindById(i.skill_id, out var skill)) continue;
                    var upgradableSkills = upgradableTalentSkills.FirstOrDefault(x => x.SkillId == i.skill_id);
                    if (upgradableSkills != default && upgradableSkills.CanUpgrade(@event.data.chara_info, out var upgradedSkillId, dpResult.Item1))
                    {
                        previousLearnPoint += skill.Upgrades.First(x => x.Id == upgradedSkillId).Grade;
                    }
                    else
                    {
                        previousLearnPoint += skill.Grade;
                    }
                }
            }
            var totalPoint = willLearnPoint + previousLearnPoint + statusPoint;
            var thisLevelId = GradeRank.GradeToRank.First(x => x.Min <= totalPoint && totalPoint <= x.Max).Id;
            var thisLevel = GradeRank.GradeToRank.First(x => x.Id == thisLevelId);
            var nextLevel = GradeRank.GradeToRank.First(x => x.Id == thisLevelId + 1);
            var rankProgress = Math.Clamp(
                (double)(totalPoint - thisLevel.Min) / (nextLevel.Min - thisLevel.Min),
                0d,
                1d);

            var dp = dpResult.Item2;
            var totalSP0 = @event.data.chara_info.skill_point;
            var averageCostEffectiveness = totalSP0 > 0
                ? ((double)willLearnPoint / totalSP0).ToString("F3")
                : null;
            string? marginalCostEffectiveness = null;
            if (totalSP0 > 50)
            {
                double sxy = 0, sx2 = 0;
                for (var x = -50; x <= 50; x++)
                {
                    var y = dp[totalSP0 + x];
                    sxy += x * y;
                    sx2 += x * x;
                }
                var b = sxy / sx2;
                marginalCostEffectiveness = b.ToString("F3");
            }
            var expectedCostEffectiveness = new List<SkillTipsCostEffectiveness>();
            for (var t = 1; t <= 10; t++)
            {
                var start = totalSP0 - t * 50 - 25;
                if (start < 0)
                    break;

                var meanScoreReduced = dp.Skip(start).Take(51).Average();
                var eff = (dp[totalSP0] - meanScoreReduced) / (t * 50);
                expectedCostEffectiveness.Add(new(t * 50, eff.ToString("F3")));
            }

            return new(
                new SkillTipsTraining(
                    @event.data.chara_info.single_mode_chara_id,
                    @event.data.chara_info.start_time,
                    @event.data.chara_info.turn,
                    totalSP0,
                    [.. @event.data.chara_info.skill_array.OrderBy(x => x.skill_id)
                        .Select(x => new SkillTipsLearnedSkill(x.skill_id, x.level))]),
                @event.data.chara_info.speed,
                @event.data.chara_info.stamina,
                @event.data.chara_info.power,
                @event.data.chara_info.guts,
                @event.data.chara_info.wiz,
                totalSP0,
                totalSP0 - totalSP,
                totalSP,
                [.. learn.Select(x => new SkillTipsDisplaySkill(x.Id, x.DisplayName, x.Cost, x.Grade))],
                previousLearnPoint,
                willLearnPoint,
                statusPoint,
                totalPoint,
                thisLevel.Rank,
                nextLevel.Rank,
                nextLevel.Min - totalPoint,
                rankProgress,
                [
                    I18N_ScoreCalculateAttention_1,
                    I18N_ScoreCalculateAttention_2,
                    I18N_ScoreCalculateAttention_3,
                    I18N_ScoreCalculateAttention_4,
                    I18N_ScoreCalculateAttention_5
                ],
                averageCostEffectiveness,
                marginalCostEffectiveness,
                [.. expectedCostEffectiveness],
                [.. warnings]);
        }

        public static List<SkillData> ReplaceAllSkillWithUpgradeSkill(Gallop.SingleModeCheckEventResponse @event, SkillManager skillmanager, List<SkillData> willLearnSkills)
        {
            skillmanager.Evolve(@event.data.chara_info, willLearnSkills);
            foreach (var baseSkill in skillmanager.GetSkills().Where(x => !x.DisplayName.Contains($"角色{I18N_Evolved}") && x.Upgrades.Any(y => y.IsScenarioEvolution == false)))
            {
                var best = baseSkill.Upgrades.Where(x => x.IsScenarioEvolution == false).OrderByDescending(x => x.Grade).First();
                baseSkill.DisplayName = $"{baseSkill.DisplayName}(角色{I18N_Evolved}->{best.DisplayName})";
                baseSkill.Grade = best.Grade;

                var inferior = baseSkill.Inferior;
                while (inferior != null)
                {
                    if (@event.data.chara_info.skill_array.Any(x => x.skill_id == inferior.Id))
                    {
                        best.Grade -= inferior.Grade;
                        break;
                    }
                    inferior = inferior.Inferior;
                }
            }

            var scenarioSkills = skillmanager.GetSkills().Where(x => x.Upgrades.Any(y => y.IsScenarioEvolution == true)).OrderByDescending(x => x.Upgrades.Max(y => y.Grade));
            var evolvedCount = scenarioSkills.Count(x => x.DisplayName.Contains($"剧本{I18N_Evolved}"));
            foreach (var baseSkill in scenarioSkills.Where(x => !x.DisplayName.Contains($"剧本{I18N_Evolved}")))
            {
                if (evolvedCount == 2) break;
                var best = baseSkill.Upgrades.Where(x => x.IsScenarioEvolution == true).OrderByDescending(x => x.Grade).First();
                baseSkill.DisplayName = $"{baseSkill.DisplayName}(剧本{I18N_Evolved}->{best.DisplayName})";
                baseSkill.Grade = best.Grade;

                var inferior = baseSkill.Inferior;
                while (inferior != null)
                {
                    if (@event.data.chara_info.skill_array.Any(x => x.skill_id == inferior.Id))
                    {
                        best.Grade -= inferior.Grade;
                        break;
                    }
                    inferior = inferior.Inferior;
                }
                evolvedCount += 1;
            }
            return willLearnSkills;
        }

        public static List<SkillData> CalculateSkillScoreCost(Gallop.SingleModeCheckEventResponse @event, SkillManager skills, bool removeInferiors)
            => CalculateSkillScoreCost(@event, skills, removeInferiors, []);

        static List<SkillData> CalculateSkillScoreCost(
            Gallop.SingleModeCheckEventResponse @event,
            SkillManager skills,
            bool removeInferiors,
            List<string> warnings)
        {
            var hasUnknownSkills = false;
            var tipsRaw = @event.data.chara_info.skill_tips_array;
            var tipsNotExistInDatabase = tipsRaw.Where(x => skills.FindByGroup(x.group_id, x.rarity).Length == 0);
            foreach (var i in tipsNotExistInDatabase)
            {
                hasUnknownSkills = true;
                var lineToPrint = string.Format(I18N_UnknownSkillAlert, i.group_id, i.rarity);
                for (var rarity = 0; rarity < 10; rarity++)
                {
                    var maybeInferiorSkills = skills.FindByGroup(i.group_id, rarity);
                    foreach (var inferiorSkill in maybeInferiorSkills)
                    {
                        lineToPrint += string.Format(I18N_UnknownSkillSuperiorSuppose, inferiorSkill.Name);
                    }
                }
                warnings.Add(lineToPrint);
            }

            foreach (var i in @event.data.chara_info.skill_array)
            {
                if (i.skill_id > 1000000 && i.skill_id < 2000000) continue;
                if (!skills.TryFindById(i.skill_id, out var skill))
                {
                    hasUnknownSkills = true;
                    warnings.Add(string.Format(I18N_UnknownBoughtSkillAlert, i.skill_id));
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

            var tips = skills.GetSkills().ToList();
            var unknownUma = !Database.TalentSkill.ContainsKey(@event.data.chara_info.card_id);

            if (removeInferiors)
            {
                var inferiors = tips
                    .SelectMany(x => skills.FindByGroup(x.GroupId))
                    .DistinctBy(x => x.Id)
                    .OrderByDescending(x => x.Rarity)
                    .ThenByDescending(x => x.Rate)
                    .GroupBy(x => x.GroupId)
                    .Where(x => x.Any())
                    .SelectMany(x => tips.Where(y => y.GroupId == x.Key)
                        .OrderByDescending(y => y.Rarity)
                        .ThenByDescending(y => y.Rate)
                        .Skip(1)
                        .Select(y => y.Id));
                tips.RemoveAll(x => inferiors.Contains(x.Id));
            }

            if (unknownUma)
                warnings.Add(string.Format(I18N_UnknownUma, @event.data.chara_info.card_id));
            if (hasUnknownSkills)
                warnings.Add(I18N_UnknownSkillExistAlert);
            return tips;
        }

        public static (List<SkillData>, int[]) DP(List<SkillData> tips, ref int totalSP)
        {
            var learn = new List<SkillData>();
            var dp = new int[totalSP + 101];
            var dpLog = Enumerable.Range(0, totalSP + 101).Select(x => new List<int>()).ToList();
            for (var i = 0; i < tips.Count; i++)
            {
                var s = tips[i];
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

                for (var j = totalSP + 100; j >= 0; j--)
                {
                    var choice = new int[4];
                    choice[0] = dp[j];
                    choice[1] = j - SuperiorCost[0] >= 0 ? dp[j - SuperiorCost[0]] + SuperiorGrade[0] : -1;
                    choice[2] = j - SuperiorCost[1] >= 0 ? dp[j - SuperiorCost[1]] + SuperiorGrade[1] : -1;
                    choice[3] = j - SuperiorCost[2] >= 0 ? dp[j - SuperiorCost[2]] + SuperiorGrade[2] : -1;

                    if (IsBestOption(0))
                    {
                        dp[j] = choice[0];
                    }
                    else if (IsBestOption(1))
                    {
                        dp[j] = choice[1];
                        dpLog[j] = new(dpLog[j - SuperiorCost[0]]) { SuperiorId[0] };
                    }
                    else if (IsBestOption(2))
                    {
                        dp[j] = choice[2];
                        dpLog[j] = new(dpLog[j - SuperiorCost[1]]) { SuperiorId[1] };
                    }
                    else if (IsBestOption(3))
                    {
                        dp[j] = choice[3];
                        dpLog[j] = new(dpLog[j - SuperiorCost[2]]) { SuperiorId[2] };
                    }

                    bool IsBestOption(int index)
                    {
                        var isBest = true;
                        for (var k = 0; k < 4; k++)
                            isBest = choice[index] >= choice[k] && isBest;
                        return isBest;
                    }
                }
            }
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
                    if (inferior != null && inferior.Id == id)
                    {
                        learn.Add(inferior);
                        totalSP -= inferior.Cost;
                        continue;
                    }
                    if (inferiorest != null && inferiorest.Id == id)
                    {
                        learn.Add(inferiorest);
                        totalSP -= inferiorest.Cost;
                    }
                }
            }
            learn = [.. learn.OrderBy(x => x.DisplayOrder)];
            return (learn, dp);
        }

    }
}
