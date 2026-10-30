using System.IO.Compression;
using System.Text;
using Gallop;
using Gallop.Endpoints;
using MessagePack;
using Newtonsoft.Json;
using UmamusumeResponseAnalyzer;
using UmamusumeResponseAnalyzer.Entities;
using UmamusumeResponseAnalyzer.Plugin;
using UmamusumeResponseAnalyzer.TerminalGui;
using HostSkillData = UmamusumeResponseAnalyzer.Entities.SkillData;
using UiText = UmamusumeResponseAnalyzer.Localization.TerminalGui;

static class SkillPanelSmoke
{
    public static async Task RunAsync(WorkspaceSmokeSession ui)
    {
        using var directory = new TempCurrentDirectory("SkillTipsResponseAnalyzer-runtime-panel");
        InitializeSkillTipsDatabase();
        IPlugin plugin = new SkillTipsResponseAnalyzer.SkillTipsResponseAnalyzer();
        var response = CreateTerminalResponse(cardId: 0);
        response.data.chara_info.skill_point = 10;
        response.data.chara_info.skill_tips_array = [new() { group_id = 90001, rarity = 1, level = 0 }];
        response.data.chara_info.chara_effect_id_array = [];
        using var context = new RuntimePluginContext(ui.Application);
        Workspace? published = null;
        try
        {
            plugin.Initialize(context);
            var endpoint = GameEndpointCatalog.ByEndpointType[typeof(GameApi.SingleMode.CheckEvent)];
            var originalOut = Console.Out;
            var originalError = Console.Error;
            using var output = new StringWriter();
            try
            {
                Console.SetOut(output);
                Console.SetError(output);
                await context.AnalyzerRegistry.Registrations.Single(candidate => candidate.PayloadType == response.GetType()).Invoke(response);
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }
            if (output.ToString().Length != 0)
                throw new InvalidOperationException("Analyzer wrote directly to the process console.");
            published = Workspace.Create("SkillTipsResponseAnalyzer");
            var screen = ui.CaptureScreen();
            if (!ReferenceEquals(Workspace.Current, published) || !screen.Contains("[red]literal skill[/]", StringComparison.Ordinal))
                throw new InvalidOperationException("Skill framebuffer omitted its recommendation row.");
            var warningCount = screen.Split(UiText.Severity_Warning + " ", StringSplitOptions.None).Length - 1;
            if (warningCount != 1)
                throw new InvalidOperationException($"Expected one skill-plan warning, actual={warningCount}.");
        }
        finally
        {
            await plugin.DisposeAsync();
        }
        var retained = Workspace.Create("SkillTipsResponseAnalyzer");
        if (ReferenceEquals(retained, published))
            throw new InvalidOperationException("Dispose violated the plugin workspace ownership contract.");
        var rejected = false;
        try
        {
            published!.SwitchTo();
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        if (!rejected)
            throw new InvalidOperationException("Removed workspace still accepted input.");
        retained.SwitchTo();
        if (ui.CaptureScreen().Contains("[red]literal skill[/]", StringComparison.Ordinal))
            throw new InvalidOperationException("Dispose left the skill panel visible.");
        retained.Remove();
        ui.Bootstrap.SwitchTo();
        Console.WriteLine("PASS SkillTipsResponseAnalyzer registered dispatch, framebuffer and panel disposal");
    }

    static void InitializeSkillTipsDatabase()
    {
        InitializeDatabase(
            "SkillTips",
            [
                new()
                {
                    Id = 900001,
                    GroupId = 90001,
                    Rarity = 1,
                    Rate = 1,
                    Name = "[red]literal skill[/]",
                    DisplayName = "[red]literal skill[/]",
                    Cost = 10,
                    Grade = 100,
                    Propers = []
                }
            ],
            [],
            new Dictionary<int, string>(),
            []);
    }

    static SingleModeCheckEventResponse CreateTerminalResponse(int cardId) => new()
    {
        data = new()
        {
            chara_info = new()
            {
                state = 2,
                card_id = cardId,
                speed = 0,
                stamina = 0,
                power = 0,
                guts = 0,
                wiz = 0,
                skill_point = 0,
                skill_array = [],
                skill_tips_array = []
            },
            unchecked_event_array = []
        }
    };

    static void InitializeDatabase(
        string fixtureName,
        IReadOnlyList<HostSkillData> skills,
        IReadOnlyList<BaseName> names,
        IReadOnlyDictionary<int, string> factorIds,
        IReadOnlyList<int> saddleIds)
    {
        WriteBrotliJson(Database.EVENT_NAME_FILEPATH, new List<Story>());
        WriteBrotliJson(Database.NAMES_FILEPATH, names.ToList(), new() { TypeNameHandling = TypeNameHandling.All });
        WriteBrotliJson(Database.SKILLS_FILEPATH, skills);
        WriteBrotliJson(Database.SKILL_UPGRADE_SPECIALITY_FILEPATH, new List<SkillUpgradeSpeciality>());
        WriteBrotliJson(Database.TALENT_SKILLS_FILEPATH, new Dictionary<int, TalentSkillData[]>());
        WriteBrotliJson(Database.FACTOR_IDS_FILEPATH, factorIds);
        WriteBrotliJson(Database.SADDLE_IDS_FILEPATH, saddleIds);
        WriteBrotliJson(Database.SUCCESSION_RELATION_FILEPATH, new SuccessionRelationTable());

        if (Database.Initialize().GetAwaiter().GetResult() != DatabaseAvailability.Ready)
            throw new InvalidOperationException($"{fixtureName} database fixture did not load atomically.");
    }

    static void WriteBrotliJson<T>(string path, T value, JsonSerializerSettings? settings = null)
    {
        using var file = File.Create(path);
        using var brotli = new BrotliStream(file, CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(brotli, Encoding.UTF8);
        using var json = new JsonTextWriter(writer);
        JsonSerializer.CreateDefault(settings).Serialize(json, value);
    }

}
