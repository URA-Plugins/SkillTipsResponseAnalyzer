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
        CancellationTokenSource? planning;
        Task pending = Task.CompletedTask;
        bool disposed;

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
            lock (trainingGate) disposed = true;
            RemoveWorkspace();
            await pending.ConfigureAwait(false);
            await bridge.DisposeAsync().ConfigureAwait(false);
        }

        ValueTask Analyze(
            Gallop.SingleModeChara charaInfo,
            Gallop.SingleModeEventInfo[] uncheckedEvents)
        {
            var shouldPublish = charaInfo.state is 2 or 3 && uncheckedEvents.Length == 0;
            lock (trainingGate)
            {
                if (!shouldPublish || disposed)
                    return ValueTask.CompletedTask;
                RemoveWorkspace();
                var source = planning = new();
                var cancellation = source.Token;
                var previous = pending;
                pending = Task.Run(async () =>
                {
                    try
                    {
                        await previous.ConfigureAwait(false);
                        cancellation.ThrowIfCancellationRequested();
                        var display = Calculate(charaInfo, cancellation);
                        lock (trainingGate)
                        {
                            if (disposed || planning != source || cancellation.IsCancellationRequested) return;
                            workspace ??= Workspace.Create(WorkspaceTitle);
                            workspace.SetPanel("skill-plan", "技能评分建议",
                                SkillTipsDisplayRenderer.Render(display, settings.SortByCostEffectiveness, bridge, context.Application, workspace),
                                fullBleed: true);
                        }
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        lock (trainingGate)
                        {
                            if (disposed || planning != source) return;
                            workspace ??= Workspace.Create(WorkspaceTitle);
                            var key = ex.GetBaseException() is DllNotFoundException ? "NativeRuntimeMissing" : "CalculationFailed";
                            workspace.Notify(string.Format(SkillPlanning.Text(key), ex), UiSeverity.Error);
                        }
                    }
                });
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
                planning?.Cancel();
                planning?.Dispose();
                planning = null;
                workspace?.Remove();
                workspace = null;
            }
        }

        static SkillTipsDisplaySnapshot ParseSkillTipsResponse(Gallop.SingleModeCheckEventResponse response)
            => Calculate(response.data.chara_info, CancellationToken.None);

        static SkillTipsDisplaySnapshot Calculate(Gallop.SingleModeChara chara, CancellationToken cancellation)
        {
            var input = SkillPlanning.Build(chara, Database.SkillCatalog);
            var result = SkillOptimizer.Solve(input.Problem, cancellation);
            var catalog = input.Catalog;
            var upgrades = result.Evolutions.ToDictionary(x => x.BaseId);
            var rows = result.Choices.Where(x => x.PurchasedIds.Length != 0 || upgrades.ContainsKey(x.Id))
                .Select(choice =>
                {
                    var id = choice.Id == 0 ? choice.PurchasedIds.Single() : choice.Id;
                    var skill = catalog.Skills[id];
                    var name = choice.Id == 0 ? string.Format(SkillPlanning.Text("RemoveBadSkill"), skill.Name) : skill.Name;
                    var gain = choice.Score;
                    if (upgrades.TryGetValue(id, out var evolution))
                    {
                        name = evolution.Automatic
                            ? string.Format(SkillPlanning.Text("AutomaticEvolution"), catalog.Skills[evolution.ResultId].Name)
                            : $"{name} → {catalog.Skills[evolution.ResultId].Name}";
                        gain += evolution.ScoreGain;
                    }
                    return new SkillTipsDisplaySkill(id, name, choice.Cost, gain);
                }).OrderBy(x => catalog.Skills[x.SkillId].DisplayOrder).ThenBy(x => x.SkillId).ToArray();
            var status = Database.StatusToPoint[chara.speed] + Database.StatusToPoint[chara.stamina]
                + Database.StatusToPoint[chara.power] + Database.StatusToPoint[chara.guts] + Database.StatusToPoint[chara.wiz];
            var delta = checked((int)result.Score - input.Problem.BaselineScore);
            var total = checked((int)result.Score + status);
            var rank = GradeRank.GradeToRank.First(x => x.Min <= total && total <= x.Max);
            var next = GradeRank.GradeToRank.First(x => x.Id == rank.Id + 1);
            return new(
                new(chara.single_mode_chara_id, chara.start_time, chara.turn, chara.skill_point,
                    [.. chara.skill_array.OrderBy(x => x.skill_id).Select(x => new SkillTipsLearnedSkill(x.skill_id, x.level))]),
                chara.speed, chara.stamina, chara.power, chara.guts, chara.wiz, chara.skill_point,
                result.Cost, chara.skill_point - result.Cost, [.. rows], input.Problem.BaselineScore, delta,
                status, total, rank.Rank, next.Rank, next.Min - total,
                Math.Clamp((double)(total - rank.Min) / (next.Min - rank.Min), 0d, 1d),
                [SkillPlanning.Text("CalculationNotes"), SkillPlanning.Text("AlgorithmModel"),
                    SkillPlanning.Text("AlgorithmObjective"), SkillPlanning.Text("EstimatedScore")],
                [],
                result.Status, result.UpperBound + status,
                result.PurchaseSkillIds, result.LearnedSkillIds, result.FinalSkillIds,
                result.Evolutions);
        }
    }
}
