using System.Drawing;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gallop;
using Gallop.Endpoints;
using MessagePack;
using Newtonsoft.Json;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Text;
using Terminal.Gui.Time;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using UmamusumeResponseAnalyzer;
using UmamusumeResponseAnalyzer.Entities;
using UmamusumeResponseAnalyzer.TerminalGui;
using UmamusumeResponseAnalyzer.Plugin;
using static SkillTipsResponseAnalyzer.i18n.ParseSkillTipsResponse;
using SkillTipsPlugin = SkillTipsResponseAnalyzer.SkillTipsResponseAnalyzer;
using UraSkillData = UmamusumeResponseAnalyzer.Entities.SkillData;
using UiText = UmamusumeResponseAnalyzer.Localization.TerminalGui;
using SkillTipsResponseAnalyzer;

RepositoryChecks.Run();

if (args.Length > 1)
    throw new ArgumentException("Expected at most one raw SingleModeRamen CheckEvent or Load response path.");

CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");

const int SkillCount = 40;
const int TotalSkillPoints = 600;
const int RamenWarningCardId = 246_813_579;
const string LongSkillHead = "这是一个用于验证纯文本";
const string LongSkillTail = "LONG_SKILL_NAME_TAIL";
var bridgeUiOnly = args is ["--bridge-ui"];
var mouseUiOnly = args is ["--mouse-ui"];
var packetPath = !bridgeUiOnly && !mouseUiOnly && args.SingleOrDefault() is { } path ? Path.GetFullPath(path) : null;
var originalCwd = Directory.GetCurrentDirectory();
var smokeDirectory = Path.Combine(Path.GetTempPath(), "skill-tips-response-analyzer-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(smokeDirectory);

try
{
    Directory.SetCurrentDirectory(smokeDirectory);
    InitializeHostConfigForSmoke();
    if (!bridgeUiOnly && !mouseUiOnly)
        await BridgeSmoke.RunAsync();

    Console.WriteLine("SkillTips UI smoke: starting Host");
    using var ui = new WorkspaceSmokeSession();
    PluginLifecycleSmoke.Run(ui);
    if (mouseUiOnly)
    {
        await AssertTableMouseInput(ui);
        Console.WriteLine("SkillTips mouse framebuffer smoke passed.");
        return;
    }
    if (bridgeUiOnly)
    {
        await AssertBridgeDashboard(ui);
        Console.WriteLine("SkillTips bridge framebuffer smoke passed.");
        return;
    }
    await SkillPanelSmoke.RunAsync(ui);
    Console.WriteLine("SkillTips UI smoke: settings");
    await AssertSettingsBeforeInitialization(ui);
    Console.WriteLine("SkillTips UI smoke: response dispatch");
    var context = new SmokePluginContext(ui.Application);
    var plugin = new SkillTipsPlugin();

    plugin.Initialize(context);
    AssertTrue(
        ReferenceEquals(Workspace.Current, ui.Bootstrap)
        && !ui.CaptureScreen().Contains("SkillTipsResponseAnalyzer", StringComparison.Ordinal),
        "Initialize must not expose the SkillTipsResponseAnalyzer workspace.");

    var analysisEndpoints = GetSingleModeAnalysisEndpoints();
    var finishEndpoints = GetSingleModeFinishEndpoints();
    AssertAnalyzerContracts(context.AnalyzerRegistry, analysisEndpoints, finishEndpoints);
    await AssertFinishWithoutWorkspaceDoesNotCreate(ui, finishEndpoints);

    AssertEqual("UG", new SkillTipsResponseAnalyzer.GradeRank { Id = 19 }.Rank, "Unexpected base UG rank.");
    AssertEqual("UG9", new SkillTipsResponseAnalyzer.GradeRank { Id = 28 }.Rank, "Unexpected UG level suffix.");
    AssertEqual("LG", new SkillTipsResponseAnalyzer.GradeRank { Id = 99 }.Rank, "Unexpected base LG rank.");
    AssertEqual("LF24", new SkillTipsResponseAnalyzer.GradeRank { Id = 148 }.Rank, "Unexpected LF level suffix.");
    AssertEqual("LE以上", new SkillTipsResponseAnalyzer.GradeRank { Id = 149 }.Rank, "Unexpected top-rank label.");

    var expectedSkills = CreateSkills();
    await InitializeSmokeDatabase(expectedSkills);
    await AssertAnalysisEndpointDispatch(ui, analysisEndpoints, finishEndpoints[0]);

    var checkEventEndpoint = GameEndpointCatalog.ByEndpointType[typeof(GameApi.SingleMode.CheckEvent)];
    await DispatchHostResponse(
        checkEventEndpoint.Path,
        MessagePackSerializer.Serialize(CreateCheckEventResponse()));
    var workspace = RequireSkillTipsWorkspace(ui);
    AssertPanelContract(ui, workspace, "CheckEvent");
    AssertTrue(
        ui.CaptureScreen().Contains(I18N_RecommendedSkills, StringComparison.Ordinal),
        "The real Host framebuffer must show the SkillTips recommendation heading.");

    var ramenLoadEndpoint = GameEndpointCatalog.ByEndpointType[typeof(GameApi.SingleModeRamen.Load)];
    var ramenSentinel = RamenWarningCardId.ToString(CultureInfo.InvariantCulture);
    var ramenLinesBefore = ui.CaptureScreen().ReplaceLineEndings("\n").Split('\n');
    var warningsBeforeRamen = ramenLinesBefore
        .Where(line => line.Contains(UiText.Severity_Warning, StringComparison.Ordinal))
        .ToArray();
    AssertTrue(
        ramenLinesBefore.All(line => !line.Contains(ramenSentinel, StringComparison.Ordinal)),
        "The Ramen warning sentinel must be unique before dispatch.");
    AssertTrue(
        workspace.RemovePanel("skill-plan"),
        "The Ramen dispatch fixture must remove the previous skill-plan panel.");
    var ramenResponse = CreateTerminalRamenResponse(RamenWarningCardId);
    ramenResponse.data.single_mode_load_common.chara_info.skill_tips_array =
    [
        .. ramenResponse.data.single_mode_load_common.chara_info.skill_tips_array
            .Where(tip => tip.group_id != 99999)
    ];
    await DispatchHostResponse(
        ramenLoadEndpoint.Path,
        MessagePackSerializer.Serialize(ramenResponse));

    AssertTrue(
        ReferenceEquals(workspace, RequireSkillTipsWorkspace(ui)),
        "SkillTipsResponseAnalyzer must reuse its canonical workspace.");
    AssertPanelContract(ui, workspace, "SingleModeRamen.Load");
    var ramenLinesAfter = ui.CaptureScreen().ReplaceLineEndings("\n").Split('\n');
    var warningsAfterRamen = ramenLinesAfter
        .Where(line => line.Contains(UiText.Severity_Warning, StringComparison.Ordinal))
        .ToArray();
    AssertTrue(
        warningsAfterRamen.Length > warningsBeforeRamen.Length,
        "The Ramen dispatch must append a visible Warning notification.");
    var notificationHeaderIndexes = Enumerable.Range(0, ramenLinesAfter.Length - 1)
        .Where(index =>
            ramenLinesAfter[index].Contains(UiText.Severity_Warning, StringComparison.Ordinal)
            && ramenLinesAfter[index + 1].Contains(ramenSentinel, StringComparison.Ordinal))
        .ToArray();
    AssertEqual(
        1,
        notificationHeaderIndexes.Length,
        "The newest Ramen Warning card must have one visible header/content pair with its unique sentinel.");
    var notificationHeaderIndex = notificationHeaderIndexes[0];
    var notificationHeader = ramenLinesAfter[notificationHeaderIndex];
    AssertTrue(
        notificationHeader.Contains(UiText.Severity_Warning, StringComparison.Ordinal)
        && !notificationHeader.Contains("SkillTipsResponseAnalyzer", StringComparison.Ordinal),
        "The newest visible Warning card must show severity without a hidden plugin source.");
    AssertTrue(
        ramenLinesAfter[notificationHeaderIndex + 1].Contains(ramenSentinel, StringComparison.Ordinal),
        "The newest Ramen Warning card content line must contain its unique numeric sentinel.");
    var notificationContent = ramenLinesAfter[notificationHeaderIndex + 1];
    AssertTrue(
        !notificationContent.Contains("[red]", StringComparison.Ordinal)
        && !notificationContent.Contains("[/]", StringComparison.Ordinal),
        "Plain warning notifications must not interpret or contain rich-text tags.");
    AssertTrue(
        ReferenceEquals(Workspace.Current, workspace),
        "Publishing skill-plan must switch to the SkillTips workspace.");

    await AssertUnknownBoughtSkillFailsFast(checkEventEndpoint);
    var longFrame = ui.CaptureScreen(320, 96);
    Console.WriteLine("SkillTips UI smoke: structured dashboard");
    await AssertStructuredDashboard(ui, checkEventEndpoint, expectedSkills);
    var shortSkills = CreateSkills(includeLongName: false);
    await InitializeSmokeDatabase(shortSkills);
    AssertTrue(
        workspace.RemovePanel("skill-plan"),
        "The short-layout fixture must remove the previous skill-plan panel.");
    await DispatchHostResponse(
        ramenLoadEndpoint.Path,
        MessagePackSerializer.Serialize(CreateTerminalRamenResponse()));
    var shortFrame = ui.CaptureScreen(320, 96);

    AssertSkillNameRendering(longFrame, expectedSkills, shortFrame, shortSkills);
    await InitializeSmokeDatabase(expectedSkills);
    await AssertCostEffectivenessDisplayConditions(ui, checkEventEndpoint);
    Console.WriteLine("SkillTips UI smoke: sorting");
    await AssertSortingAndSavedDefault(ui, plugin, checkEventEndpoint, expectedSkills);
    Console.WriteLine("SkillTips UI smoke: table mouse input");
    await AssertTableMouseInput(ui);
    Console.WriteLine("SkillTips UI smoke: bridge dashboard");
    await AssertBridgeDashboard(ui);
    await AssertFinishWorkspaceLifecycle(ui, finishEndpoints);

    if (packetPath is not null)
    {
        await DispatchHostResponse(
            finishEndpoints[0].Path,
            SerializeResponse(finishEndpoints[0]));
        AssertNoSkillTipsWorkspace(ui, "Raw replay must begin without a SkillTips workspace.");

        Directory.SetCurrentDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UmamusumeResponseAnalyzer"));
        if (await Database.Initialize() != DatabaseAvailability.Ready)
            throw new InvalidOperationException("The installed Host database is unavailable for raw replay.");

        var payload = File.ReadAllBytes(packetPath);
        var endpoint = ResolveRamenPacketEndpoint(payload);
        var response = MessagePackSerializer.Deserialize(endpoint.ResponseType, payload)
            ?? throw new InvalidOperationException(
                $"Raw packet did not deserialize to {endpoint.ResponseType.FullName}.");
        var (charaInfo, uncheckedEvents) = GetRamenAnalysisData(response);
        AssertTrue(
            charaInfo.state is 2 or 3 && uncheckedEvents.Length == 0,
            "Raw Ramen packet must satisfy the unchanged terminal condition.");

        await DispatchHostResponse(endpoint.Path, payload);
        var replayWorkspace = RequireSkillTipsWorkspace(ui);
        AssertSkillPlanPanelContract(ui, replayWorkspace, endpoint.Path);
        Console.WriteLine(
            $"Replayed {Path.GetFileName(packetPath)} as {endpoint.Path}: " +
            $"state={charaInfo.state}, unchecked={uncheckedEvents.Length}, " +
            $"skillTips={charaInfo.skill_tips_array.Length}, skillPoint={charaInfo.skill_point}.");
    }

    var disposedWorkspace = RequireSkillTipsWorkspace(ui);
    await plugin.DisposeAsync();
    AssertNoSkillTipsWorkspace(ui, "Dispose must remove the owned SkillTips workspace.");
    AssertThrows<InvalidOperationException>(
        disposedWorkspace.SwitchTo,
        "Dispose must tombstone the owned SkillTips workspace generation.");

    var replacementPlugin = new SkillTipsPlugin();
    context.AnalyzerRegistry.Clear();
    replacementPlugin.Initialize(context);
    await DispatchHostResponse(
        checkEventEndpoint.Path,
        MessagePackSerializer.Serialize(CreateCheckEventResponse()));
    var replacementWorkspace = RequireSkillTipsWorkspace(ui);
    AssertTrue(
        !ReferenceEquals(disposedWorkspace, replacementWorkspace),
        "The next output after Dispose must create a new workspace generation.");
    AssertTrue(
        ReferenceEquals(replacementWorkspace, Workspace.Create("skilltipsresponseanalyzer")),
        "Workspace.Create must be OrdinalIgnoreCase within the active generation.");
    var lateContent = WorkspaceContent.Text("late skill plan");

    await DispatchHostResponse(
        finishEndpoints[0].Path,
        SerializeResponse(finishEndpoints[0]));
    AssertNoSkillTipsWorkspace(ui, "Final finish must tombstone the SkillTips workspace generation.");
    AssertTrue(
        !ui.CaptureScreen().Contains(I18N_RecommendedSkills, StringComparison.Ordinal),
        "Final finish must remove the SkillTips panel.");
    AssertTrue(
        !ReferenceEquals(Workspace.Current, replacementWorkspace)
        && !ui.CaptureScreen().Contains(UiText.Severity_Warning, StringComparison.Ordinal),
        "Final finish must clear visible state for the tombstoned generation.");

    AssertThrows<InvalidOperationException>(
        () => replacementWorkspace.SetPanel(
            "skill-plan",
            "技能评分建议",
            lateContent,
            fullBleed: true),
        "A late callback must not publish through a tombstoned workspace generation.");
    var nextGeneration = Workspace.Create("skilltipsresponseanalyzer");
    AssertTrue(
        !ReferenceEquals(replacementWorkspace, nextGeneration),
        "Recreating a removed workspace must produce a new canonical generation.");
    nextGeneration.SwitchTo();
    AssertTrue(
        !ui.CaptureScreen().Contains(I18N_RecommendedSkills, StringComparison.Ordinal),
        "A late callback must not pollute the replacement workspace generation.");
    AssertTrue(
        !ui.CaptureScreen().Contains(UiText.Severity_Warning, StringComparison.Ordinal),
        "A late callback must not add telemetry to the replacement workspace generation.");
    await replacementPlugin.DisposeAsync();
    AssertTrue(
        ReferenceEquals(nextGeneration, Workspace.Create("SkillTipsResponseAnalyzer"))
        && !ui.CaptureScreen().Contains(I18N_RecommendedSkills, StringComparison.Ordinal),
        "Dispose after finish must not remove or mutate another active generation.");
    nextGeneration.Remove();
    AssertNoSkillTipsWorkspace(ui, "The replacement-generation fixture must clean up its workspace.");

    Console.WriteLine("SkillTipsResponseAnalyzer smoke passed.");
}
finally
{
    Directory.SetCurrentDirectory(originalCwd);
    if (Directory.Exists(smokeDirectory))
        Directory.Delete(smokeDirectory, recursive: true);
}

static async Task AssertSettingsBeforeInitialization(WorkspaceSmokeSession ui)
{
    var settingsPath = Path.GetFullPath("PluginData/SkillTipsResponseAnalyzer/settings.json");
    var plugin = new SkillTipsPlugin();
    plugin.Initialize(new SmokePluginContext(ui.Application));
    AssertTrue(!File.Exists(settingsPath), "Initialize must not create a missing settings file.");
    await plugin.DisposeAsync();
    plugin = new SkillTipsPlugin();

    try
    {
        await RunSettingsDialogAsync(ui, () => plugin.ConfigPromptAsync(ui.Application), dialog =>
        {
            AssertEqual(0, FindSortSelector(dialog).Value, "A missing configuration must select default order.");
            AcceptSettingsButton(ui.Application, dialog, "skill-tips-settings-cancel");
        });
        throw new InvalidOperationException("Cancel before Initialize must cancel the configuration draft.");
    }
    catch (OperationCanceledException) { }
    AssertTrue(!File.Exists(settingsPath), "Cancel must not create a missing settings file.");

    await RunSettingsDialogAsync(ui, () => plugin.ConfigPromptAsync(ui.Application), dialog =>
    {
        var selector = FindSortSelector(dialog);
        selector.SetFocus();
        ui.Application.Keyboard.RaiseKeyDownEvent(Key.CursorDown);
        ui.Application.Keyboard.RaiseKeyDownEvent(Key.Space);
        AssertEqual(1, selector.Value, "The settings order must be selectable with the keyboard.");
        AcceptSettingsButton(ui.Application, dialog, "skill-tips-settings-save");
    });
    AssertSavedSort(settingsPath, true);
    var saved = File.ReadAllBytes(settingsPath);
    await plugin.DisposeAsync();
    plugin = new SkillTipsPlugin();

    foreach (var action in new[] { "Cancel", "Esc", "Close", "Token" })
    {
        using var cancellation = new CancellationTokenSource();
        try
        {
            await RunSettingsDialogAsync(ui,
                () => plugin.ConfigPromptAsync(ui.Application, cancellation.Token),
                dialog =>
                {
                    var selector = FindSortSelector(dialog);
                    AssertEqual(1, selector.Value, $"{action} must start with the saved order.");
                    selector.Value = 0;
                    switch (action)
                    {
                        case "Cancel":
                            AcceptSettingsButton(ui.Application, dialog, "skill-tips-settings-cancel");
                            break;
                        case "Esc":
                            ui.Application.Keyboard.RaiseKeyDownEvent(Key.Esc);
                            break;
                        case "Close":
                            ui.Application.RequestStop(dialog);
                            break;
                        case "Token":
                            cancellation.Cancel();
                            break;
                    }
                });
            throw new InvalidOperationException($"Config {action} must cancel the draft.");
        }
        catch (OperationCanceledException) { }
        AssertTrue(saved.SequenceEqual(File.ReadAllBytes(settingsPath)), $"{action} must not change the settings file.");
    }

    try
    {
        foreach (var invalid in new[]
        {
            "{", "null", "{}", "{\"sortByCostEffectiveness\":\"true\"}",
            "{\"sortByCostEffectiveness\":true,\"unknown\":0}",
            "{\"sortByCostEffectiveness\":true,\"sortByCostEffectiveness\":false}"
        })
        {
            File.WriteAllText(settingsPath, invalid);
            var candidate = new SkillTipsPlugin();
            try
            {
                candidate.Initialize(new SmokePluginContext(ui.Application));
                throw new InvalidOperationException($"Invalid settings must fail during Initialize: {invalid}");
            }
            catch (InvalidDataException error)
            {
                AssertTrue(error.Message.Contains(settingsPath, StringComparison.Ordinal), "Invalid settings must identify their file.");
            }
            AssertThrows<InvalidDataException>(
                () => candidate.ConfigPromptAsync(ui.Application).GetAwaiter().GetResult(),
                "Opening settings must not silently repair invalid JSON.");
            AssertEqual(invalid, File.ReadAllText(settingsPath), "Rejected settings must remain unchanged.");
            await candidate.DisposeAsync();
        }
    }
    finally
    {
        File.WriteAllBytes(settingsPath, saved);
    }

    await RunSettingsDialogAsync(ui, () => plugin.ConfigPromptAsync(ui.Application), dialog =>
    {
        FindSortSelector(dialog).Value = 0;
        AcceptSettingsButton(ui.Application, dialog, "skill-tips-settings-save");
    });
    AssertSavedSort(settingsPath, false);
    await plugin.DisposeAsync();
    AssertNoSkillTipsWorkspace(ui, "Editing settings before Initialize must not create a skill workspace.");
}

static async Task AssertSortingAndSavedDefault(
    WorkspaceSmokeSession ui,
    SkillTipsPlugin plugin,
    GameEndpointDescriptor endpoint,
    IReadOnlyList<UraSkillData> originalSkills)
{
    var settingsPath = Path.GetFullPath("PluginData/SkillTipsResponseAnalyzer/settings.json");
    var skills = CreateSkills(includeLongName: false).Take(4).ToArray();
    (skills[0].Grade, skills[0].Cost) = (50, 20);
    (skills[1].Grade, skills[1].Cost) = (100, 10);
    (skills[2].Grade, skills[2].Cost) = (80, 0);
    (skills[3].Grade, skills[3].Cost) = (200, 20);
    await InitializeSmokeDatabase(skills);
    var response = CreateCheckEventResponse();
    response.data.chara_info.skill_tips_array = [.. response.data.chara_info.skill_tips_array.Take(skills.Length)];
    await DispatchHostResponse(endpoint.Path, MessagePackSerializer.Serialize(response));
    var workspace = RequireSkillTipsWorkspace(ui);
    ui.CaptureScreen(320, 96, restore: false);
    var originalRows = ReadSkillRows(ui);
    AssertEqual(4, originalRows.Length, "The sorting fixture must select every affordable skill, including its free skill.");
    var sortedRows = new[] { originalRows[2], originalRows[1], originalRows[3], originalRows[0] };
    var summary = ReadSkillSummary(ui);
    var saved = File.ReadAllBytes(settingsPath);
    var table = ui.InvokeOnOwner(() => FindSkillTable(ui.Application.TopRunnableView!));
    var costText = ui.InvokeOnOwner(() => HistoryConfigDialog.Descendants(ui.Application.TopRunnableView!)
        .Single(view => view.Id == "skill-tips-cost-effectiveness-text"));
    ui.InvokeOnOwner(() =>
    {
        var selector = FindSortSelector(ui.Application.TopRunnableView!);
        AssertEqual(0, selector.Value, "New output must use the saved default order.");
        selector.SetFocus();
        return true;
    });
    ui.SendKey(Key.CursorRight);
    ui.SendKey(Key.Space);
    var actualRows = ReadSkillRows(ui);
    var selection = ui.InvokeOnOwner(() =>
    {
        var selector = FindSortSelector(ui.Application.TopRunnableView!);
        return $"Value={selector.Value}, FocusedItem={selector.FocusedItem}";
    });
    AssertTrue(sortedRows.SequenceEqual(actualRows),
        "Keyboard sorting must put free skills first and preserve default order for equal ratios. " +
        $"{selection}; Expected=[{string.Join("; ", sortedRows)}]; Actual=[{string.Join("; ", actualRows)}]");
    AssertEqual(summary, ReadSkillSummary(ui), "Sorting must not change score, skill points, rank, warnings or cost-effectiveness summaries.");
    AssertTrue(ReferenceEquals(table, ui.InvokeOnOwner(() => FindSkillTable(ui.Application.TopRunnableView!))), "Sorting must update the existing table instead of replacing it.");
    AssertTrue(ReferenceEquals(costText, ui.InvokeOnOwner(() => HistoryConfigDialog.Descendants(ui.Application.TopRunnableView!)
        .Single(view => view.Id == "skill-tips-cost-effectiveness-text"))), "Sorting must preserve the cost-effectiveness view.");
    var screen = ui.CaptureScreen(320, 96, restore: false);
    var freeSkillIndex = screen.IndexOf(skills[2].DisplayName, StringComparison.Ordinal);
    var nextSkillIndex = screen.IndexOf(skills[1].DisplayName, StringComparison.Ordinal);
    AssertTrue(freeSkillIndex >= 0 && nextSkillIndex > freeSkillIndex, "The framebuffer must immediately display the selected order.");

    ui.SendKey(Key.Tab);
    AssertTrue(ui.InvokeOnOwner(() => table.HasFocus), "Tab from sorting must reach the skill table.");
    ui.SendKey(Key.Tab);
    AssertTrue(ui.InvokeOnOwner(() => HistoryConfigDialog.Descendants(ui.Application.TopRunnableView!)
        .Single(view => view.Id == "skill-tips-cost-effectiveness").HasFocus), "Tab from the table must reach cost-effectiveness.");
    ui.SendKey(Key.Tab);
    AssertTrue(ui.InvokeOnOwner(() => FindSortSelector(ui.Application.TopRunnableView!).HasFocus), "Tab from cost-effectiveness must return to sorting.");
    ui.SendKey(Key.Tab.WithShift);
    AssertTrue(ui.InvokeOnOwner(() => HistoryConfigDialog.Descendants(ui.Application.TopRunnableView!)
        .Single(view => view.Id == "skill-tips-cost-effectiveness").HasFocus), "Shift+Tab from sorting must reach cost-effectiveness.");

    ui.Bootstrap.SwitchTo();
    ui.Flush();
    workspace.SwitchTo();
    ui.Flush();
    AssertTrue(sortedRows.SequenceEqual(ReadSkillRows(ui)), "Changing workspaces must retain the current output's temporary order.");
    AssertTrue(saved.SequenceEqual(File.ReadAllBytes(settingsPath)), "Temporary sorting must not write settings.");

    var defaultPoint = FindScreenPoint(ui.CaptureScreen(320, 96, restore: false), I18N_DefaultSort, 0, 0);
    foreach (var flags in new[]
    {
        MouseFlags.LeftButtonPressed,
        MouseFlags.LeftButtonReleased,
        MouseFlags.LeftButtonClicked
    })
        ui.SendMouse(new() { ScreenPosition = defaultPoint, Flags = flags });
    AssertTrue(originalRows.SequenceEqual(ReadSkillRows(ui)), "Mouse selection must restore the original default order.");
    ui.SendKey(Key.Tab);
    AssertTrue(ui.InvokeOnOwner(() => table.HasFocus), "Tab from the default-order option must reach the table.");
    ui.SendKey(Key.Tab.WithShift);
    AssertTrue(ui.InvokeOnOwner(() => FindSortSelector(ui.Application.TopRunnableView!).HasFocus), "Shift+Tab from the table must reach sorting.");
    ui.SendKey(Key.Tab.WithShift);
    AssertTrue(ui.InvokeOnOwner(() => HistoryConfigDialog.Descendants(ui.Application.TopRunnableView!)
        .Single(view => view.Id == "skill-tips-cost-effectiveness").HasFocus), "Shift+Tab from the default-order option must reach cost-effectiveness.");
    await RunSettingsDialogAsync(ui, () => plugin.ConfigPromptAsync(ui.Application), dialog =>
    {
        FindSortSelector(dialog).Value = 1;
        AcceptSettingsButton(ui.Application, dialog, "skill-tips-settings-save");
    });
    AssertSavedSort(settingsPath, true);
    AssertTrue(originalRows.SequenceEqual(ReadSkillRows(ui)), "Saving a new default must not change the current output.");
    AssertEqual(summary, ReadSkillSummary(ui), "Saving a default must not change the current summary.");
    await DispatchHostResponse(endpoint.Path, MessagePackSerializer.Serialize(response));
    ui.Flush();
    AssertTrue(sortedRows.SequenceEqual(ReadSkillRows(ui)), "The next response must use the newly saved default.");
    try
    {
        await RunSettingsDialogAsync(ui, () => plugin.ConfigPromptAsync(ui.Application), dialog =>
        {
            FindSortSelector(dialog).Value = 0;
            AcceptSettingsButton(ui.Application, dialog, "skill-tips-settings-cancel");
        });
        throw new InvalidOperationException("Cancel after Initialize must cancel the settings draft.");
    }
    catch (OperationCanceledException) { }
    AssertSavedSort(settingsPath, true);
    await DispatchHostResponse(endpoint.Path, MessagePackSerializer.Serialize(response));
    ui.Flush();
    AssertTrue(sortedRows.SequenceEqual(ReadSkillRows(ui)), "Cancel must not change the default applied to future responses.");

    var empty = CreateCheckEventResponse();
    empty.data.chara_info.skill_tips_array = [];
    await DispatchHostResponse(endpoint.Path, MessagePackSerializer.Serialize(empty));
    ui.Flush();
    var emptySummary = ReadSkillSummary(ui);
    ui.InvokeOnOwner(() =>
    {
        FindSortSelector(ui.Application.TopRunnableView!).Value = 0;
        return true;
    });
    AssertEqual(0, ReadSkillRows(ui).Length, "Sorting an empty recommendation must retain an empty table.");
    AssertEqual(emptySummary, ReadSkillSummary(ui), "Sorting an empty recommendation must not change its summary.");

    await RunSettingsDialogAsync(ui, () => plugin.ConfigPromptAsync(ui.Application), dialog =>
    {
        FindSortSelector(dialog).Value = 0;
        AcceptSettingsButton(ui.Application, dialog, "skill-tips-settings-save");
    });
    await InitializeSmokeDatabase(originalSkills);
    await DispatchHostResponse(endpoint.Path, MessagePackSerializer.Serialize(CreateCheckEventResponse()));
    ui.CaptureScreen();
}

static OptionSelector FindSortSelector(View root)
    => HistoryConfigDialog.Descendants(root).OfType<OptionSelector>().Single(view => view.Id == "skill-tips-sort");

#pragma warning disable CS0618 // Exercise the Host-provided TextView mouse surface.
static async Task AssertTableMouseInput(WorkspaceSmokeSession ui)
{
    await using var bridge = new SkillTipsBridge();
    var previous = Workspace.Current;
    var workspace = Workspace.Create("SkillTipsMouseSmoke");
    var training = new SkillTipsTraining(123, "2026-10-30T00:00:00", 78, 600, []);
    var display = new SkillTipsDisplaySnapshot(training, 130, 263, 108, 239, 239, 600, 120, 480,
        [.. Enumerable.Range(0, 24).Select(index => new SkillTipsDisplaySkill(
            900001 + index, $"{index:00}{new string('技', 48)}🏇", 5, 50))],
        0, 1200, 1000, 2200, "C", "C+", 100, 0.5, ["注意"], "10", "10", [], []);
    try
    {
        foreach (var size in new[] { new Size(90, 24), new Size(60, 12) })
        {
            Publish(display, size);
            var body = ui.InvokeOnOwner(() => FindSkillTable(ui.Application.TopRunnableView!));
            var rows = ReadSkillRows(ui);
            var summary = ReadSkillSummary(ui);
            var header = ui.InvokeOnOwner(() => HistoryConfigDialog.Descendants(ui.Application.TopRunnableView!)
                .OfType<TextView>().Single(view => view.Id == "skill-tips-table-header"));
            var edge = PointAt(body.Viewport.Width - 2, 0);
            Console.WriteLine($"Table mouse: {size.Width}x{size.Height}; viewport={body.Viewport}; content={body.GetContentSize()}; lines={body.Lines}; first-line-cells={body.GetLine(0).Count}; click={edge}");
            Click(edge);
            AssertTrue(ui.InvokeOnOwner(() => body.HasFocus), "Clicking a recommendation must focus its table.");
            Click(PointAt(body.Viewport.Width - 1, 0));
            ui.SendMouse(new() { ScreenPosition = PointAt(3, 0), Flags = MouseFlags.WheeledRight });
            Click(PointAt(body.Viewport.Width - 2, 0));
            AssertEqual(body.Viewport.X, header.Viewport.X, "Clicking after horizontal scrolling must keep the header aligned.");

            var wheelPoint = PointAt(3, 0);
            for (var step = 0; step < display.Skills.Length; step++)
                ui.SendMouse(new() { ScreenPosition = wheelPoint, Flags = MouseFlags.WheeledDown });
            AssertTrue(body.Viewport.Y > 0, "The mouse fixture must reach a vertically scrolled table.");
            var beforeScrollBarClick = body.Viewport.Y;
            var scrollBar = ui.InvokeOnOwner(() => body.VerticalScrollBar.FrameToScreen());
            Click(scrollBar.Location);
            AssertTrue(body.Viewport.Y < beforeScrollBarClick, "The table's native scrollbar must remain clickable.");
            ui.SendMouse(new() { ScreenPosition = wheelPoint, Flags = MouseFlags.WheeledDown });
            var lastVisibleRow = Math.Min(body.Viewport.Height - 1, body.Lines - body.Viewport.Y - 1);
            var lastRow = PointAt(3, lastVisibleRow);
            Click(lastRow);
            DoubleClick(lastRow);
            Drag(PointAt(3, 0), PointAt(body.Viewport.Width - 2, lastVisibleRow));
            AssertTrue(rows.SequenceEqual(ReadSkillRows(ui)), "Clicks, double clicks and dragging must preserve every recommendation.");
            AssertEqual(summary, ReadSkillSummary(ui), "Table mouse input must preserve skill points and score totals.");
            AssertEqual(body.Viewport.X, header.Viewport.X, "Dragging must keep the header aligned with the table.");

            Point PointAt(int x, int y) => ui.InvokeOnOwner(() =>
            {
                var screen = body.ViewportToScreen();
                var point = new Point(screen.X + x, screen.Y + y);
                for (View? ancestor = body; ancestor is not null; ancestor = ancestor.SuperView)
                    AssertTrue(ancestor.ViewportToScreen().Contains(point), $"Mouse target {point} must be visible inside {ancestor.Id}.");
                return point;
            });
        }

        foreach (var count in new[] { 1, 0 })
        {
            Publish(display with { Skills = [.. display.Skills.Take(count)] }, new(90, 24));
            var body = ui.InvokeOnOwner(() => FindSkillTable(ui.Application.TopRunnableView!));
            var rows = ReadSkillRows(ui);
            var summary = ReadSkillSummary(ui);
            var screen = ui.InvokeOnOwner(() => body.ViewportToScreen());
            AssertTrue(body.Viewport.Height > count, "The sparse fixture must expose blank rows below its recommendations.");
            var blank = new Point(screen.X + 3, screen.Y + body.Viewport.Height - 1);
            Click(blank);
            DoubleClick(blank);
            Drag(new(screen.X + 3, screen.Y), blank);
            AssertTrue(rows.SequenceEqual(ReadSkillRows(ui)), "Clicking and dragging in blank rows must preserve sparse and empty results.");
            AssertEqual(summary, ReadSkillSummary(ui), "Blank-row mouse input must preserve the summary.");
        }
    }
    finally
    {
        workspace.Remove();
        previous?.SwitchTo();
        ui.CaptureScreen();
    }

    void Publish(SkillTipsDisplaySnapshot value, Size size)
    {
        workspace.SetPanel("skill-plan", "Mouse UI smoke",
            SkillTipsDisplayRenderer.Render(value, false, bridge, ui.Application, workspace), fullBleed: true);
        workspace.SwitchTo();
        ui.CaptureScreen(size.Width, size.Height, restore: false);
    }

    void Click(Point point)
    {
        foreach (var flags in new[] { MouseFlags.LeftButtonPressed, MouseFlags.LeftButtonReleased, MouseFlags.LeftButtonClicked })
            ui.SendMouse(new() { ScreenPosition = point, Flags = flags });
    }

    void DoubleClick(Point point)
    {
        Click(point);
        foreach (var flags in new[] { MouseFlags.LeftButtonPressed, MouseFlags.LeftButtonReleased, MouseFlags.LeftButtonDoubleClicked })
            ui.SendMouse(new() { ScreenPosition = point, Flags = flags });
    }

    void Drag(Point from, Point to)
    {
        ui.SendMouse(new() { ScreenPosition = from, Flags = MouseFlags.LeftButtonPressed });
        ui.SendMouse(new() { ScreenPosition = to, Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport });
        ui.SendMouse(new() { ScreenPosition = to, Flags = MouseFlags.LeftButtonReleased });
    }
}
#pragma warning restore CS0618

static async Task AssertBridgeDashboard(WorkspaceSmokeSession ui)
{
    await using var server = await BridgeSmokeServer.StartAsync();
    var previous = Workspace.Current;
    var workspace = Workspace.Create("SkillTipsBridgeSmoke");
    var training = new SkillTipsTraining(123, "2026-10-30T00:00:00", 78, 600, []);
    var display = new SkillTipsDisplaySnapshot(training, 130, 263, 108, 239, 239, 600, 400, 200,
        [.. Enumerable.Range(0, 80).Select(index => new SkillTipsDisplaySkill(900001 + index, $"桥接技能{index:00}", 5, 50 + index))],
        0, 7160, 1000, 8160, "C", "C+", 100, 0.5, ["注意"], "17.9", "17.9", [], []);
    ulong revision = 0;
    try
    {
        Publish();
        ui.CaptureScreen(180, 42, restore: false);
        var body = ui.InvokeOnOwner(() => FindSkillTable(ui.Application.TopRunnableView!));
        var button = ui.InvokeOnOwner(() => HistoryConfigDialog.Descendants(ui.Application.TopRunnableView!)
            .OfType<Button>().Single(view => view.Id == "skill-tips-select-recommended"));
        AssertTrue(!button.Visible, "A new recommendation must start locked.");
        var count = ui.InvokeOnOwner(() => Math.Min(body.Viewport.Height, display.Skills.Length));
        AssertTrue(count > 4 && count < 40, "The bridge fixture must have multiple scrollable pages.");
        var threshold = (count * 3 + 3) / 4;
        var prefix = display.Skills.Take(threshold).Select(row => row.SkillId).ToArray();
        await State(prefix, selectionEvent: false);
        AssertEqual(0, body.Viewport.Y, "The initial skill-page snapshot must highlight without auto-scrolling.");
        await State([900001]);
        var bodyScreen = ui.InvokeOnOwner(() => body.ViewportToScreen());
        var cells = ui.CaptureCells(180, 42);
        AssertEqual(new Terminal.Gui.Drawing.Color(Terminal.Gui.Drawing.StandardColor.BrightGreen),
            cells[bodyScreen.Y, bodyScreen.X + 3].Attribute!.Value.Foreground,
            "Selected recommendations must be highlighted in the actual framebuffer.");
        ui.InvokeOnOwner(() => { FindSortSelector(ui.Application.TopRunnableView!).Value = 1; return true; });
        cells = ui.CaptureCells(180, 42);
        AssertTrue(cells[bodyScreen.Y, bodyScreen.X + 3].Attribute!.Value.Foreground
            != new Terminal.Gui.Drawing.Color(Terminal.Gui.Drawing.StandardColor.BrightGreen),
            "Sorting must follow selected skill IDs instead of retaining the previous row highlight.");
        await State([900080]);
        cells = ui.CaptureCells(180, 42);
        AssertEqual(new Terminal.Gui.Drawing.Color(Terminal.Gui.Drawing.StandardColor.BrightGreen),
            cells[bodyScreen.Y, bodyScreen.X + 3].Attribute!.Value.Foreground,
            "The selected skill must be highlighted at its sorted row.");
        ui.InvokeOnOwner(() => { FindSortSelector(ui.Application.TopRunnableView!).Value = 0; return true; });
        await State([900001]);
        var summary = ReadSkillSummary(ui);
        await State(prefix.Skip(1).ToArray());
        AssertEqual(0, body.Viewport.Y, "Non-contiguous selection must not scroll past an unselected first row.");
        await State(prefix);
        var expectedTop = Math.Min(threshold, display.Skills.Length - body.Viewport.Height);
        AssertEqual(expectedTop, body.Viewport.Y, "Selecting the first 75% must scroll by that row count.");
        await State(prefix);
        AssertEqual(expectedTop, body.Viewport.Y, "Repeated identical selection events must not scroll again.");
        ui.InvokeOnOwner(() => { body.ScrollTo(new(0, 0)); return true; });
        await State([.. prefix, 900080]);
        AssertEqual(0, body.Viewport.Y, "A new off-screen selection must not undo manually scrolling back to a completed page.");
        ui.InvokeOnOwner(() => { body.ScrollTo(new(0, expectedTop)); return true; });
        AssertEqual(summary, ReadSkillSummary(ui), "Bridge selection must preserve recommendation totals.");
        Console.WriteLine("Bridge UI: highlighting and scrolling passed");

        ui.Bootstrap.SwitchTo();
        ui.Flush();
        TypeUnlock();
        await State(display.Skills.Select(row => row.SkillId).ToArray());
        AssertEqual(expectedTop, body.Viewport.Y, "A hidden workspace must not auto-scroll.");
        workspace.SwitchTo();
        ui.Flush();
        AssertTrue(!button.Visible, "Typing in another workspace must not unlock this result.");
        await ui.RunDialogAsync(() =>
        {
            using var dialog = new Dialog { Title = "Bridge scope", Width = 30, Height = 5 };
            ui.Application.Run(dialog);
            return Task.CompletedTask;
        }, dialog =>
        {
            foreach (var character in "thisisunsafe")
                ui.Application.Keyboard.RaiseKeyDownEvent(new Key(character));
            AssertTrue(!button.Visible, "A modal must not unlock the workspace beneath it.");
            ui.Application.RequestStop(dialog);
        });
        Console.WriteLine("Bridge UI: workspace and modal scope passed");
        ui.InvokeOnOwner(() => body.SetFocus());
        TypeUnlock();
        AssertTrue(button.Visible && button.Enabled, "Typing the exact sequence in the result must reveal the native button.");
        var unlocked = ui.CaptureScreen(180, 42, restore: false);
        var footer = ui.InvokeOnOwner(() => button.FrameToScreen());
        var tableFrame = ui.InvokeOnOwner(() => body.FrameToScreen());
        AssertTrue(footer.Y >= tableFrame.Bottom && unlocked.Contains(I18N_SelectRecommended, StringComparison.Ordinal),
            "The action must be a native footer below the recommended table.");
        ui.SendKey(Key.Tab);
        AssertTrue(button.HasFocus, "Tab from the recommendation table must reach its action.");
        ui.SendKey(Key.Tab);
        ui.SendKey(Key.Tab.WithShift);
        AssertTrue(button.HasFocus, "Shift+Tab from cost-effectiveness must return to the action.");
        ui.SendKey(Key.Tab.WithShift);
        AssertTrue(body.HasFocus, "Shift+Tab from the action must return to the table.");
        ui.Bootstrap.SwitchTo();
        workspace.SwitchTo();
        ui.Flush();
        AssertTrue(button.Visible, "Workspace round-trips must preserve the same result's unlock.");
        ui.CaptureScreen(60, 12, restore: false);
        ui.InvokeOnOwner(() => button.SetFocus());
        AssertTrue(ui.CaptureScreen(60, 12, restore: false).Contains(I18N_SelectRecommended, StringComparison.Ordinal),
            "Focusing the footer must reveal it in a narrow, low-height terminal.");
        ui.CaptureScreen(180, 42, restore: false);
        Console.WriteLine("Bridge UI: unlock, footer layout and focus passed");

        await SelectAndRespond(extraIds: [], confirm: true);
        foreach (var cancelMode in new[] { "Cancel", "Esc", "Close" })
            await SelectAndRespond(extraIds: [987654], confirm: false, cancelMode);
        await SelectAndRespond(extraIds: [987654], confirm: true);
        Console.WriteLine("Bridge UI: prepare/replace and cancellation passed");
        await State([900001], selectionEvent: false);
        ui.InvokeOnOwner(() => { body.ScrollTo(new(0, 0)); return true; });
        await State([], active: false);
        AssertTrue(!button.Enabled, "Leaving the game skill page must disable selection.");
        bodyScreen = ui.InvokeOnOwner(() => body.ViewportToScreen());
        cells = ui.CaptureCells(180, 42);
        AssertTrue(cells[bodyScreen.Y, bodyScreen.X + 3].Attribute!.Value.Foreground
            != new Terminal.Gui.Drawing.Color(Terminal.Gui.Drawing.StandardColor.BrightGreen),
            "Leaving the game skill page must clear selected-row highlighting.");
        await State([], identity: training with { Turn = training.Turn + 1 });
        AssertTrue(!button.Enabled, "A different training snapshot must disable selecting recommendations.");
        await State([]);
        await SelectAndRespond(extraIds: [987654], confirm: false, "NewResult");
        AssertTrue(!ui.InvokeOnOwner(() => HistoryConfigDialog.Descendants(ui.Application.TopRunnableView!)
            .OfType<Button>().Single(view => view.Id == "skill-tips-select-recommended").Visible),
            "Publishing a new recommendation must reset its unlock.");
        foreach (var focus in new[] { "Window", "WorkspaceViewport", "None", "CommandMode" })
        {
            Publish();
            var fixture = ui.InvokeOnOwner(() =>
            {
                var top = ui.Application.TopRunnableView!;
                var target = focus == "WorkspaceViewport"
                    ? HistoryConfigDialog.Descendants(top).Single(view => view.GetType().Name == "WorkspaceViewport")
                    : top;
                var disabled = focus == "None" ? []
                    : target.SubViews.Where(view => view.CanFocus && view.Visible).ToArray();
                foreach (var child in disabled)
                    child.CanFocus = false;
                if (focus == "None")
                    top.HasFocus = false;
                else
                    target.SetFocus();
                AssertTrue(focus == "None" ? top.MostFocused is null : ReferenceEquals(top.MostFocused, target),
                    $"The {focus} fixture must start without a focused recommendation control.");
                var action = HistoryConfigDialog.Descendants(top).OfType<Button>()
                    .Single(view => view.Id == "skill-tips-select-recommended");
                return (top, disabled, action);
            });
            try
            {
                if (focus == "CommandMode")
                {
                    ui.SendKey(new Key('/'));
                    await WaitUntilAsync(() => ui.InvokeOnOwner(() => fixture.top.MostFocused is TextField));
                    TypeUnlock();
                    AssertTrue(!fixture.action.Visible, "Typing the sequence into Host command input must not unlock the workspace.");
                    AssertTrue(ui.InvokeOnOwner(() => ((TextField)fixture.top.MostFocused!).Text.Contains("thisisunsafe", StringComparison.Ordinal)),
                        "The unlock observer must leave command input text intact.");
                    ui.SendKey(Key.Esc);
                }
                else
                {
                    TypeUnlock();
                    AssertTrue(fixture.action.Visible, $"Typing directly in the current workspace must unlock with {focus} focus.");
                }
            }
            finally
            {
                ui.InvokeOnOwner(() =>
                {
                    foreach (var child in fixture.disabled)
                        child.CanFocus = true;
                    fixture.top.SetFocus();
                    FindSkillTable(fixture.top).SetFocus();
                    return true;
                });
            }
        }
        Console.WriteLine("Bridge UI: workspace-wide unlock and command-input exclusion passed");
        workspace.Remove();
        await State([900001]);
        ui.Flush();
    }
    finally
    {
        workspace.Remove();
        previous?.SwitchTo();
        ui.CaptureScreen();
    }

    void Publish()
    {
        workspace.SetPanel("skill-plan", "Bridge UI smoke",
            SkillTipsDisplayRenderer.Render(display, false, server.Bridge, ui.Application, workspace), fullBleed: true);
        workspace.SwitchTo();
        ui.Flush();
    }

    void TypeUnlock()
    {
        foreach (var character in "thisisunsafe")
            ui.SendKey(new Key(character));
    }

    async Task State(int[] selected, SkillTipsTraining? identity = null, bool selectionEvent = true, bool active = true)
    {
        var currentRevision = ++revision;
        await server.SendAsync(new
        {
            kind = "state", active, page_id = 1ul, revision = currentRevision, selection_event = selectionEvent,
            training = identity ?? training, selected_skill_ids = selected, remaining_points = 200, error = (string?)null
        });
        await server.WaitForStateAsync(state => state?.Revision == currentRevision);
        ui.InvokeOnOwner(() => true);
    }

    async Task SelectAndRespond(int[] extraIds, bool confirm, string cancelMode = "Cancel")
    {
        await State([.. display.Skills.Select(row => row.SkillId), .. extraIds]);
        var button = ui.InvokeOnOwner(() => HistoryConfigDialog.Descendants(ui.Application.TopRunnableView!)
            .OfType<Button>().Single(view => view.Id == "skill-tips-select-recommended"));
        ui.InvokeOnOwner(() => button.SetFocus());
        ui.SendKey(Key.Enter);
        var request = await server.ReadAsync();
        AssertEqual("prepare", request.GetProperty("kind").GetString(), "Clicking the button must prepare before replacing.");
        AssertTrue(request.GetProperty("skill_ids").EnumerateArray().Select(value => value.GetInt32())
            .SequenceEqual(display.Skills.Select(row => row.SkillId)), "Prepare must use the unchanged recommendation IDs.");
        await server.SendAsync(new
        {
            kind = "prepared", request_id = request.GetProperty("request_id").GetInt64(), page_id = 1ul, revision,
            selected_skill_ids = display.Skills.Select(row => row.SkillId).ToArray(), extra_skill_ids = extraIds, remaining_points = 200
        });
        if (extraIds.Length != 0)
        {
            await WaitUntilAsync(() => ui.InvokeOnOwner(() => ui.Application.TopRunnableView?.Id == "skill-tips-confirm-replace"));
            if (cancelMode == "NewResult")
            {
                Publish();
                await WaitUntilAsync(() => ui.InvokeOnOwner(() => ui.Application.TopRunnableView?.Id != "skill-tips-confirm-replace"));
                await server.AssertNoRequestAsync();
                return;
            }
            ui.InvokeOnOwner(() =>
            {
                var dialog = (Dialog)ui.Application.TopRunnableView!;
                AssertTrue(HistoryConfigDialog.Descendants(dialog).Any(view => view.Text.Contains("987654", StringComparison.Ordinal)),
                    "Replacement confirmation must identify the non-recommended pending skills.");
                if (confirm || cancelMode == "Cancel")
                {
                    dialog.Buttons.Single(candidate => candidate.Id == (confirm ? "skill-tips-replace" : "skill-tips-cancel-replace")).SetFocus();
                    ui.Application.Keyboard.RaiseKeyDownEvent(Key.Enter);
                }
                else if (cancelMode == "Esc")
                    ui.Application.Keyboard.RaiseKeyDownEvent(Key.Esc);
                else
                    ui.Application.RequestStop(dialog);
                return true;
            });
        }
        if (confirm)
        {
            request = await server.ReadAsync();
            AssertEqual("replace", request.GetProperty("kind").GetString(), "Only approved selection replacement may be sent.");
            await State(display.Skills.Select(row => row.SkillId).ToArray());
            await server.SendAsync(new
            {
                kind = "result", request_id = request.GetProperty("request_id").GetInt64(),
                selected_skill_ids = display.Skills.Select(row => row.SkillId).ToArray(), remaining_points = 200
            });
        }
        await WaitUntilAsync(() => ui.InvokeOnOwner(() => button.Enabled));
        if (!confirm)
            await server.AssertNoRequestAsync();
    }
}

static async Task WaitUntilAsync(Func<bool> condition)
{
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!condition())
    {
        if (DateTime.UtcNow >= deadline)
            throw new TimeoutException("The bridge UI did not reach the expected state.");
        await Task.Delay(10);
    }
}

#pragma warning disable CS0618
static TextView FindSkillTable(View root)
    => HistoryConfigDialog.Descendants(root).OfType<TextView>().Single(view => view.Id == "skill-tips-table");
#pragma warning restore CS0618

static string[] ReadSkillRows(WorkspaceSmokeSession ui)
    => ui.InvokeOnOwner(() => FindSkillTable(ui.Application.TopRunnableView!).Text
        .ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries));

static string ReadSkillSummary(WorkspaceSmokeSession ui)
    => ui.InvokeOnOwner(() => string.Join("\n", HistoryConfigDialog.Descendants(
            HistoryConfigDialog.Descendants(ui.Application.TopRunnableView!)
                .Single(view => view.Id == "skill-tips-details"))
        .Select(view => $"{view.Id}:{view.Text}")));

static void AcceptSettingsButton(IApplication application, Dialog dialog, string id)
{
    dialog.Buttons.Single(button => button.Id == id).SetFocus();
    application.Keyboard.RaiseKeyDownEvent(Key.Enter);
}

static async Task RunSettingsDialogAsync(WorkspaceSmokeSession ui, Func<Task> action, Action<Dialog> interact)
{
    Exception? interactionError = null;
    void OnIteration(object? sender, Terminal.Gui.App.EventArgs<IApplication?> e)
    {
        if (ui.Application.TopRunnableView is not Dialog dialog)
            return;
        ui.Application.Iteration -= OnIteration;
        try
        {
            ui.Application.LayoutAndDraw(forceRedraw: true);
            interact(dialog);
        }
        catch (Exception error)
        {
            interactionError = error;
            ui.Application.RequestStop(dialog);
        }
    }

    try
    {
        var operation = ui.InvokeOnOwner(() =>
        {
            ui.Application.Iteration += OnIteration;
            return action();
        });
        await operation.WaitAsync(TimeSpan.FromSeconds(10));
    }
    finally
    {
        ui.Application.Iteration -= OnIteration;
        if (interactionError is not null)
            ExceptionDispatchInfo.Capture(interactionError).Throw();
    }
}

static void AssertSavedSort(string path, bool expected)
{
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    AssertEqual(expected, document.RootElement.GetProperty("sortByCostEffectiveness").GetBoolean(), "Save must persist the selected default order.");
}

static List<UraSkillData> CreateSkills(bool includeLongName = true)
    => [..
        Enumerable.Range(0, SkillCount)
            .Select(index =>
            {
                var name = includeLongName && index == SkillCount - 1
                    ? $"[red]这是一个用于验证纯文本与水平滚动的超长技能名称-{new string('长', 36)}-{LongSkillTail}[/]"
                    : $"推荐技能{index:00}";
                return new UraSkillData
                {
                    Id = 900001 + index,
                    GroupId = 90001 + index,
                    Rarity = 1,
                    Rate = 1,
                    Name = name,
                    DisplayName = name,
                    Cost = 10,
                    Grade = 100 + index,
                    DisplayOrder = index,
                    Propers = []
                };
            })];

static SingleModeCheckEventResponse CreateCheckEventResponse(int skillPoints = TotalSkillPoints) => new()
{
    data = new()
    {
        chara_info = CreateCharaInfo(skillPoints),
        unchecked_event_array = []
    }
};

static SingleModeRamenLoadResponse CreateTerminalRamenResponse(int cardId = 0) => new()
{
    data = new()
    {
        single_mode_load_common = new()
        {
            chara_info = CreateCharaInfo(cardId: cardId),
            unchecked_event_array = []
        }
    }
};

static SingleModeChara CreateCharaInfo(int skillPoints = TotalSkillPoints, int state = 2, int cardId = 0) => new()
{
    state = state,
    card_id = cardId,
    speed = 130,
    stamina = 263,
    power = 108,
    guts = 239,
    wiz = 239,
    skill_point = skillPoints,
    skill_array = [],
    skill_tips_array =
    [
        .. Enumerable.Range(0, SkillCount).Select(index => new SkillTips
        {
            group_id = 90001 + index,
            rarity = 1,
            level = 0
        }),
        new()
        {
            group_id = 99999,
            rarity = 1,
            level = 0
        }
    ],
    chara_effect_id_array = []
};

static void AssertPanelContract(
    WorkspaceSmokeSession ui,
    Workspace workspace,
    string endpoint)
{
    AssertSkillPlanPanelContract(ui, workspace, endpoint);
    AssertTrue(
        ui.CaptureScreen().Contains(UiText.Severity_Warning, StringComparison.Ordinal),
        $"{endpoint} must publish a visible Warning notification.");
}

static void AssertSkillPlanPanelContract(
    WorkspaceSmokeSession ui,
    Workspace workspace,
    string endpoint)
{
    AssertTrue(
        ReferenceEquals(Workspace.Current, workspace)
        && ui.CaptureScreen().Contains(I18N_RecommendedSkills, StringComparison.Ordinal),
        $"{endpoint} must switch to the SkillTips workspace.");
}

static async ValueTask AssertUnknownBoughtSkillFailsFast(GameEndpointDescriptor checkEventEndpoint)
{
    const int unknownSkillId = 765432;
    var response = CreateCheckEventResponse();
    response.data.chara_info.skill_tips_array = [];
    response.data.chara_info.skill_array = [new() { skill_id = unknownSkillId, level = 1 }];

    try
    {
        await DispatchHostResponse(
            checkEventEndpoint.Path,
            MessagePackSerializer.Serialize(response));
    }
    catch (KeyNotFoundException exception)
    {
        AssertTrue(
            exception.Message.Contains($"skillId={unknownSkillId}", StringComparison.Ordinal),
            "Unknown purchased skill failures must identify the missing skillId.");
        return;
    }
    catch (Exception exception)
    {
        throw new InvalidOperationException(
            $"Unknown purchased skill dispatch must fail with KeyNotFoundException: " +
            $"skillId={unknownSkillId}, Actual={exception.GetType().FullName}",
            exception);
    }

    throw new InvalidOperationException(
        $"Unknown purchased skill dispatch must fail fast with KeyNotFoundException: skillId={unknownSkillId}");
}

static async ValueTask AssertCostEffectivenessDisplayConditions(
    WorkspaceSmokeSession ui,
    GameEndpointDescriptor checkEventEndpoint)
{
    foreach (var (skillPoints, showsAverage, showsMarginal) in new[]
    {
        (0, false, false),
        (1, true, false),
        (50, true, false),
        (51, true, true)
    })
    {
        await DispatchHostResponse(
            checkEventEndpoint.Path,
            MessagePackSerializer.Serialize(CreateCheckEventResponse(skillPoints)));
        RequireSkillTipsWorkspace(ui);
        var text = ui.CaptureScreen(320, 96);
        var normalized = RemoveLineBreaks(text);
        AssertEqual(
            showsAverage,
            normalized.Contains(I18N_AverageCostEffectiveness.Split('{')[0], StringComparison.Ordinal),
            $"Average cost-effectiveness visibility is incorrect at {skillPoints} skill points.");
        AssertEqual(
            showsMarginal,
            normalized.Contains(I18N_MarginalCostEffectiveness.Split('{')[0], StringComparison.Ordinal),
            $"Marginal cost-effectiveness visibility is incorrect at {skillPoints} skill points.");
        AssertTrue(
            normalized.Contains(I18N_ExpectedCostEffectiveness.Split('，')[0], StringComparison.Ordinal),
            $"The expected cost-effectiveness section must be visible at {skillPoints} skill points.");
    }
}

static async ValueTask AssertStructuredDashboard(
    WorkspaceSmokeSession ui,
    GameEndpointDescriptor checkEventEndpoint,
    IReadOnlyList<UraSkillData> expectedSkills)
{
    var roundTrip = new[]
    {
        ui.CaptureScreen(320, 96),
        ui.CaptureScreen(90, 24),
        ui.CaptureScreen(320, 96)
    };
    var wide = roundTrip[0];
    foreach (var token in new[]
    {
        I18N_RecommendedSkills,
        I18N_Columns_SkillName,
        I18N_Columns_RequireSP,
        I18N_Columns_Grade,
        expectedSkills[0].DisplayName,
        LongSkillTail,
        "[red]",
        I18N_ScoreCalculateAttention_1,
        I18N_ExpectedCostEffectiveness,
        "500pt"
    })
    {
        AssertTrue(
            wide.Contains(token, StringComparison.Ordinal),
            $"The wide framebuffer is missing visible content: {token}");
    }
    AssertTrue(!wide.Contains('…'), "The complete wide dashboard must not render an ellipsis.");

    var narrow = roundTrip[1];
    AssertTrue(
        narrow.Contains(I18N_RecommendedSkills, StringComparison.Ordinal)
        && narrow.Contains(I18N_Columns_SkillName, StringComparison.Ordinal),
        "The narrow framebuffer must keep the recommendation table visible.");
    AssertTrue(
        roundTrip[2].Contains(LongSkillTail, StringComparison.Ordinal)
        && !roundTrip[2].Contains('…'),
        "A wide-narrow-wide layout round trip must restore the complete dashboard.");

    var low = ui.CaptureScreen(90, 12);
    AssertTrue(
        low.Contains(I18N_RecommendedSkills, StringComparison.Ordinal),
        "The low-height framebuffer must keep the recommendation region reachable.");

    await DispatchHostResponse(
        checkEventEndpoint.Path,
        MessagePackSerializer.Serialize(CreateCheckEventResponse()));
    ui.Flush();
    var keyboardReference = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    var keyboardTablePoint = FindScreenPoint(keyboardReference, I18N_RecommendedSkills, 0, 4);
    var averageCostPrefix = I18N_AverageCostEffectiveness.Split('{')[0];
    string[] costLines =
    [
        string.Format(I18N_AverageCostEffectiveness, "00.000"),
        string.Format(I18N_MarginalCostEffectiveness, "00.000"),
        I18N_ExpectedCostEffectiveness,
        .. Enumerable.Range(1, Math.Min(10, TotalSkillPoints / 50))
            .Select(index => string.Format(
                I18N_ExpectedCostEffectivenessByPrice,
                index * 50,
                "00.000"))
    ];
    const int narrowScreenWidth = 60;
    const int visibleBodyCells = narrowScreenWidth - 1 - 2 - 1;
    var tableContentCells = new[]
    {
        Math.Max(I18N_Columns_SkillName.GetColumns(), expectedSkills.Max(skill => skill.DisplayName.GetColumns())),
        Math.Max(I18N_Columns_RequireSP.GetColumns(), expectedSkills.Max(skill => skill.Cost.ToString().GetColumns())),
        Math.Max(I18N_Columns_Grade.GetColumns(), expectedSkills.Max(skill => skill.Grade.ToString().GetColumns()))
    }.Sum() + 4;
    var tableHorizontalScrollSteps =
        (Math.Max(0, tableContentCells - visibleBodyCells) + 3) / 4 + 1;
    var costScrollBound = costLines.Sum(
        line => Math.Max(1, (line.GetColumns() + visibleBodyCells - 1) / visibleBodyCells)) + 1;
    ui.SendMouse(new()
    {
        ScreenPosition = keyboardTablePoint,
        Flags = MouseFlags.WheeledDown
    });
    keyboardTablePoint = FindScreenPoint(
        ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false),
        I18N_RecommendedSkills,
        0,
        4);
    ui.SendMouse(new()
    {
        ScreenPosition = keyboardTablePoint,
        Flags = MouseFlags.WheeledUp
    });

    var keyboardFrames = new string[13];
    keyboardFrames[0] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    ui.SendKey(Key.PageDown);
    keyboardFrames[1] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    ui.SendKey(Key.CursorRight.WithShift);
    keyboardFrames[2] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    var keyboardTableBottom = keyboardFrames[2];
    for (var i = 0;
         i < SkillCount && !keyboardTableBottom.Contains(LongSkillHead, StringComparison.Ordinal);
         i++)
    {
        ui.SendKey(Key.PageDown);
        keyboardTableBottom = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    }
    ui.SendKey(Key.End.WithCtrl);
    keyboardFrames[3] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    ui.SendKey(Key.Tab);
    keyboardFrames[4] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    ui.SendKey(Key.PageDown);
    keyboardFrames[5] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    keyboardFrames[6] = keyboardFrames[5];
    for (var i = 1;
         i < costScrollBound && !keyboardFrames[6].Contains("500pt", StringComparison.Ordinal);
         i++)
    {
        ui.SendKey(Key.PageDown);
        keyboardFrames[6] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    }
    ui.SendKey(Key.Tab.WithShift);
    keyboardFrames[7] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    ui.SendKey(Key.Home.WithCtrl);
    keyboardFrames[8] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    ui.SendKey(Key.Tab);
    keyboardFrames[9] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    ui.SendKey(Key.PageUp);
    keyboardFrames[10] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    keyboardFrames[11] = keyboardFrames[10];
    for (var i = 1;
         i < costScrollBound && !keyboardFrames[11].Contains(averageCostPrefix, StringComparison.Ordinal);
         i++)
    {
        ui.SendKey(Key.PageUp);
        keyboardFrames[11] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    }
    ui.SendKey(Key.Tab.WithShift);
    keyboardFrames[12] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    AssertTrue(
        keyboardFrames[0].Contains(expectedSkills[0].DisplayName, StringComparison.Ordinal),
        "The table keyboard fixture must begin at the first row.");
    AssertTrue(
        !string.Equals(keyboardFrames[0], keyboardFrames[1], StringComparison.Ordinal),
        "One table PageDown must visibly move the framebuffer downward.");
    AssertTrue(
        !string.Equals(keyboardFrames[1], keyboardFrames[2], StringComparison.Ordinal),
        "One table Shift+Right must visibly move the framebuffer horizontally.");
    AssertTrue(
        keyboardFrames[3].Contains(LongSkillTail, StringComparison.Ordinal),
        "Table keyboard scrolling must reach the final long-name tail.");
    AssertTrue(
        keyboardFrames[4].Contains(averageCostPrefix, StringComparison.Ordinal),
        "The cost-effectiveness keyboard fixture must begin at its first line.");
    AssertTrue(
        !string.Equals(keyboardFrames[4], keyboardFrames[5], StringComparison.Ordinal),
        "One cost-effectiveness PageDown must visibly move its framebuffer downward.");
    AssertTrue(
        keyboardFrames[6].Contains("500pt", StringComparison.Ordinal),
        "Cost-effectiveness PageDown must reach 500pt.");
    AssertTrue(
        keyboardFrames[7].Contains(LongSkillTail, StringComparison.Ordinal),
        "Cost-effectiveness PageDown must not change the table viewport.");
    AssertTrue(
        keyboardFrames[8].Contains(expectedSkills[0].DisplayName, StringComparison.Ordinal),
        "Table keyboard scrolling must return to the first row.");
    AssertTrue(
        keyboardFrames[9].Contains("500pt", StringComparison.Ordinal),
        "Table keyboard scrolling must not change the cost-effectiveness viewport.");
    AssertTrue(
        !string.Equals(keyboardFrames[9], keyboardFrames[10], StringComparison.Ordinal),
        "One cost-effectiveness PageUp must visibly move its framebuffer upward.");
    AssertTrue(
        keyboardFrames[11].Contains(averageCostPrefix, StringComparison.Ordinal),
        "Cost-effectiveness PageUp must return to its first line.");
    AssertTrue(
        keyboardFrames[12].Contains(expectedSkills[0].DisplayName, StringComparison.Ordinal),
        "Cost-effectiveness PageUp must not change the table viewport.");

    await DispatchHostResponse(
        checkEventEndpoint.Path,
        MessagePackSerializer.Serialize(CreateCheckEventResponse()));
    ui.Flush();
    var mouseReference = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    var tablePoint = FindScreenPoint(mouseReference, I18N_RecommendedSkills, 0, 4);
    ui.SendMouse(new()
    {
        ScreenPosition = tablePoint,
        Flags = MouseFlags.WheeledDown
    });
    var mouseTableFocus = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    AssertTrue(
        !string.Equals(mouseReference, mouseTableFocus, StringComparison.Ordinal),
        "One table mouse wheel must visibly move the framebuffer downward.");

    var mouseTableVerticalTail = mouseTableFocus;
    for (var i = 1; i < SkillCount + 8; i++)
    {
        tablePoint = FindScreenPoint(mouseTableVerticalTail, I18N_RecommendedSkills, 0, 4);
        ui.SendMouse(new()
        {
            ScreenPosition = tablePoint,
            Flags = MouseFlags.WheeledDown
        });
        mouseTableVerticalTail = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    }

    var mouseTableTail = mouseTableVerticalTail;
    for (var i = 0; i < tableHorizontalScrollSteps; i++)
    {
        tablePoint = FindScreenPoint(mouseTableTail, I18N_RecommendedSkills, 0, 4);
        ui.SendMouse(new()
        {
            ScreenPosition = tablePoint,
            Flags = MouseFlags.WheeledRight
        });
        mouseTableTail = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    }
    AssertTrue(
        mouseTableTail.Contains(LongSkillTail, StringComparison.Ordinal),
        "Table mouse scrolling must reach the final long-name tail.");

    ui.SendKey(Key.Tab);
    var mouseCostReference = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    var costPoint = FindScreenPoint(mouseCostReference, I18N_CostEffectiveness, 0, 1);
    var mouseCostBottom = mouseCostReference;
    for (var i = 0; i < costScrollBound; i++)
    {
        costPoint = FindScreenPoint(mouseCostBottom, I18N_CostEffectiveness, 0, 1);
        ui.SendMouse(new()
        {
            ScreenPosition = costPoint,
            Flags = MouseFlags.WheeledDown
        });
        mouseCostBottom = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    }
    AssertTrue(
        mouseCostBottom.Contains("500pt", StringComparison.Ordinal),
        "Cost-effectiveness mouse scrolling must reach 500pt.");

    ui.SendKey(Key.Tab.WithShift);
    var mouseTableAfterCost = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    AssertTrue(
        mouseTableAfterCost.Contains(LongSkillTail, StringComparison.Ordinal),
        "Cost-effectiveness mouse scrolling must not change the table viewport.");

    ui.SendKey(Key.Tab);
    var mouseCostAfterTable = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    AssertTrue(
        mouseCostAfterTable.Contains("500pt", StringComparison.Ordinal),
        "Returning to cost-effectiveness must preserve its mouse viewport.");

    var mouseCostTop = mouseCostAfterTable;
    for (var i = 0; i < costScrollBound; i++)
    {
        costPoint = FindScreenPoint(mouseCostTop, I18N_CostEffectiveness, 0, 1);
        ui.SendMouse(new()
        {
            ScreenPosition = costPoint,
            Flags = MouseFlags.WheeledUp
        });
        mouseCostTop = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    }
    AssertTrue(
        mouseCostTop.Contains(averageCostPrefix, StringComparison.Ordinal),
        "Cost-effectiveness mouse scrolling must return to its first line.");

    ui.SendKey(Key.Tab.WithShift);
    var mouseTableReturnReference = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    AssertTrue(
        mouseTableReturnReference.Contains(LongSkillTail, StringComparison.Ordinal),
        "Cost-effectiveness mouse return must not change the table viewport.");

    var mouseTableVerticalTop = mouseTableReturnReference;
    for (var i = 0; i < SkillCount + 8; i++)
    {
        tablePoint = FindScreenPoint(mouseTableVerticalTop, I18N_RecommendedSkills, 0, 4);
        ui.SendMouse(new()
        {
            ScreenPosition = tablePoint,
            Flags = MouseFlags.WheeledUp
        });
        mouseTableVerticalTop = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    }

    var mouseTableTop = mouseTableVerticalTop;
    for (var i = 0; i < tableHorizontalScrollSteps; i++)
    {
        tablePoint = FindScreenPoint(mouseTableTop, I18N_RecommendedSkills, 0, 4);
        ui.SendMouse(new()
        {
            ScreenPosition = tablePoint,
            Flags = MouseFlags.WheeledLeft
        });
        mouseTableTop = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    }
    AssertTrue(
        mouseTableTop.Contains(expectedSkills[0].DisplayName, StringComparison.Ordinal),
        "Table mouse scrolling must return to the first row.");

    ui.SendKey(Key.Tab);
    var mouseCostAfterTableTop = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    AssertTrue(
        mouseCostAfterTableTop.Contains(averageCostPrefix, StringComparison.Ordinal),
        "Table mouse return must not change the cost-effectiveness viewport.");
    ui.CaptureScreen();
}

static void AssertSkillNameRendering(
    string longFrame,
    IReadOnlyList<UraSkillData> longSkills,
    string shortFrame,
    IReadOnlyList<UraSkillData> shortSkills)
{
    AssertTrue(
        longFrame.Contains(longSkills[^1].DisplayName, StringComparison.Ordinal),
        "The long-name framebuffer must preserve the complete longest skill name.");
    AssertTrue(
        shortFrame.Contains(shortSkills[^1].DisplayName, StringComparison.Ordinal)
        && !shortFrame.Contains(LongSkillTail, StringComparison.Ordinal),
        "The short-name framebuffer must render the short fixture without the long sentinel.");
}

static Point FindScreenPoint(string screen, string text, int xOffset, int yOffset)
{
    var lines = screen.ReplaceLineEndings("\n").Split('\n');
    for (var y = 0; y < lines.Length; y++)
    {
        var index = lines[y].IndexOf(text, StringComparison.Ordinal);
        if (index >= 0)
            return new(lines[y][..index].GetColumns() + xOffset, y + yOffset);
    }

    throw new InvalidOperationException($"The real Host framebuffer is missing '{text}'.");
}

static string RemoveLineBreaks(string value)
    => value.Replace("\r", string.Empty, StringComparison.Ordinal)
        .Replace("\n", string.Empty, StringComparison.Ordinal);

static GameEndpointDescriptor[] GetSingleModeAnalysisEndpoints()
{
    const string prefix = "/umamusume/single_mode";
    return
    [
        .. GameEndpointCatalog.ByEndpointType.Values
            .Where(endpoint =>
            {
                var suffix = endpoint.Path.EndsWith("/check_event", StringComparison.Ordinal)
                    ? "/check_event"
                    : endpoint.Path.EndsWith("/load", StringComparison.Ordinal)
                        ? "/load"
                        : null;
                if (suffix is null || !endpoint.Path.StartsWith(prefix, StringComparison.Ordinal))
                    return false;

                var scenario = endpoint.Path[prefix.Length..^suffix.Length];
                return scenario.Length == 0
                    || scenario.Length > 1
                        && scenario[0] == '_'
                        && !scenario.AsSpan(1).Contains('/');
            })
            .OrderBy(endpoint => endpoint.Path, StringComparer.Ordinal)
    ];
}

static GameEndpointDescriptor[] GetSingleModeFinishEndpoints()
{
    const string prefix = "/umamusume/single_mode";
    const string suffix = "/finish";
    return
    [
        .. GameEndpointCatalog.ByEndpointType.Values
            .Where(endpoint =>
            {
                if (!endpoint.Path.StartsWith(prefix, StringComparison.Ordinal)
                    || !endpoint.Path.EndsWith(suffix, StringComparison.Ordinal))
                {
                    return false;
                }

                var scenario = endpoint.Path[prefix.Length..^suffix.Length];
                return scenario.Length == 0
                    || scenario.Length > 1
                        && scenario[0] == '_'
                        && !scenario.AsSpan(1).Contains('/');
            })
            .OrderBy(endpoint => endpoint.Path, StringComparer.Ordinal)
    ];
}

static void AssertAnalyzerContracts(
    SmokeAnalyzerRegistry registry,
    IReadOnlyList<GameEndpointDescriptor> analysisEndpoints,
    IReadOnlyList<GameEndpointDescriptor> finishEndpoints)
{
    AssertEqual(3, registry.Registrations.Count, "SkillTips must use exactly three generic analyzer registrations.");

    var checkEvent = RequireRegistration<SingleModeCheckEventResponse>(
        registry,
        "/umamusume/single_mode*/check_event");
    var load = RequireRegistration<SingleModeLoadResponse>(
        registry,
        "/umamusume/single_mode*/load");
    var finish = RequireRegistration<SingleModeFinishResponse>(
        registry,
        "/umamusume/single_mode*/finish");

    var actualAnalysisPaths = checkEvent.Endpoints
        .Concat(load.Endpoints)
        .Select(endpoint => endpoint.Path)
        .Order(StringComparer.Ordinal)
        .ToArray();
    var expectedAnalysisPaths = analysisEndpoints
        .Select(endpoint => endpoint.Path)
        .Order(StringComparer.Ordinal)
        .ToArray();
    AssertTrue(
        expectedAnalysisPaths.SequenceEqual(actualAnalysisPaths),
        "CheckEvent/Load wildcard registrations must cover the complete current Host catalog exactly once.");

    var actualFinishPaths = finish.Endpoints
        .Select(endpoint => endpoint.Path)
        .Order(StringComparer.Ordinal)
        .ToArray();
    var expectedFinishPaths = finishEndpoints
        .Select(endpoint => endpoint.Path)
        .Order(StringComparer.Ordinal)
        .ToArray();
    AssertTrue(
        expectedFinishPaths.SequenceEqual(actualFinishPaths),
        "Finish wildcard registration must cover the complete current Host catalog.");
}

static SmokeAnalyzerRegistration RequireRegistration<TPayload>(
    SmokeAnalyzerRegistry registry,
    string wildcard)
{
    var registration = registry.Registrations.SingleOrDefault(entry => entry.PayloadType == typeof(TPayload))
        ?? throw new InvalidOperationException($"Register<{typeof(TPayload).Name}> was not observed.");
    AssertEqual(AnalyzerKind.Response, registration.Kind, $"Register<{typeof(TPayload).Name}> must be a response analyzer.");
    AssertEqual(0, registration.Priority, $"Register<{typeof(TPayload).Name}> must use the default priority.");
    AssertTrue(
        registration.Patterns is [{ Kind: EndpointPatternKind.Wildcard, Pattern: var pattern }]
        && string.Equals(pattern, wildcard, StringComparison.Ordinal),
        $"Register<{typeof(TPayload).Name}> must use wildcard {wildcard}.");
    return registration;
}

static async ValueTask AssertAnalysisEndpointDispatch(
    WorkspaceSmokeSession ui,
    IReadOnlyList<GameEndpointDescriptor> analysisEndpoints,
    GameEndpointDescriptor cleanupFinish)
{
    string? expectedResult = null;
    string? expectedWarning = null;
    foreach (var endpoint in analysisEndpoints)
    {
        var currentBefore = Workspace.Current;
        var screenBefore = ui.CaptureScreen();

        await DispatchHostResponse(
            endpoint.Path,
            CreateAnalysisResponse(endpoint, CreateCharaInfo(state: 1), []));
        AssertNoNewSkillTipsOutput(ui, currentBefore, screenBefore, $"{endpoint.Path} state=1");

        await DispatchHostResponse(
            endpoint.Path,
            CreateAnalysisResponse(endpoint, CreateCharaInfo(), [new SingleModeEventInfo()]));
        AssertNoNewSkillTipsOutput(ui, currentBefore, screenBefore, $"{endpoint.Path} with unchecked events");

        await DispatchHostResponse(
            endpoint.Path,
            CreateAnalysisResponse(endpoint, CreateCharaInfo(), []));
        var workspace = RequireSkillTipsWorkspace(ui);
        if (currentBefore is { Title: "SkillTipsResponseAnalyzer" })
        {
            AssertTrue(
                ReferenceEquals(currentBefore, workspace),
                $"{endpoint.Path} must reuse the canonical SkillTips workspace.");
        }
        AssertPanelContract(ui, workspace, endpoint.Path);
        var resultWithWarning = ui.CaptureScreen(320, 96);
        var warning = resultWithWarning.ReplaceLineEndings("\n")
            .Split('\n')
            .Last(line => line.Contains(UiText.Severity_Warning, StringComparison.Ordinal));
        var result = ui.CaptureScreen(60, 96);
        if (expectedResult is null)
        {
            expectedResult = result;
            expectedWarning = warning;
        }
        else
        {
            AssertEqual(
                expectedResult,
                result,
                $"{endpoint.Path} must render the same complete result as every other CheckEvent/Load endpoint.");
            AssertEqual(
                expectedWarning,
                warning,
                $"{endpoint.Path} must preserve the same merged warning result.");
        }

        await DispatchHostResponse(cleanupFinish.Path, SerializeResponse(cleanupFinish));
        AssertNoSkillTipsWorkspace(ui, $"{endpoint.Path} cleanup must remove the completed training workspace.");
        AssertThrows<InvalidOperationException>(
            workspace.SwitchTo,
            $"{endpoint.Path} cleanup must tombstone the completed training workspace generation.");
    }
}

static async ValueTask AssertFinishWithoutWorkspaceDoesNotCreate(
    WorkspaceSmokeSession ui,
    IReadOnlyList<GameEndpointDescriptor> finishEndpoints)
{
    var currentBefore = Workspace.Current;
    var screenBefore = ui.CaptureScreen();
    foreach (var endpoint in finishEndpoints)
    {
        await DispatchHostResponse(endpoint.Path, SerializeResponse(endpoint));
        AssertNoSkillTipsWorkspace(
            ui,
            $"{endpoint.Path} must not create a workspace while cleaning up.");
        AssertNoNewSkillTipsOutput(ui, currentBefore, screenBefore, endpoint.Path);
    }
}

static async ValueTask AssertFinishWorkspaceLifecycle(
    WorkspaceSmokeSession ui,
    IReadOnlyList<GameEndpointDescriptor> finishEndpoints)
{
    var checkEvent = GameEndpointCatalog.ByEndpointType[typeof(GameApi.SingleMode.CheckEvent)];
    var foreignWorkspace = Workspace.Create("Unrelated workspace");
    foreach (var endpoint in finishEndpoints)
    {
        var existingWorkspace = RequireSkillTipsWorkspace(ui);

        await DispatchHostResponse(endpoint.Path, SerializeResponse(endpoint));
        AssertNoSkillTipsWorkspace(ui, $"{endpoint.Path} must remove the completed training workspace.");
        AssertTrue(
            ReferenceEquals(foreignWorkspace, Workspace.Create("UNRELATED WORKSPACE")),
            $"{endpoint.Path} must not remove an unrelated workspace.");
        AssertThrows<InvalidOperationException>(
            existingWorkspace.SwitchTo,
            $"{endpoint.Path} must tombstone the previous workspace generation.");

        await DispatchHostResponse(endpoint.Path, SerializeResponse(endpoint));
        AssertNoSkillTipsWorkspace(ui, $"Repeated {endpoint.Path} must not recreate the workspace.");

        await DispatchHostResponse(
            checkEvent.Path,
            MessagePackSerializer.Serialize(CreateCheckEventResponse()));
        var recreatedWorkspace = RequireSkillTipsWorkspace(ui);
        AssertTrue(
            !ReferenceEquals(existingWorkspace, recreatedWorkspace),
            $"{endpoint.Path} must create a new canonical workspace generation.");
        AssertTrue(
            ReferenceEquals(Workspace.Current, recreatedWorkspace),
            $"{endpoint.Path} next output must switch to the SkillTips workspace.");
        AssertPanelContract(ui, recreatedWorkspace, $"{endpoint.Path} next output");
    }

    foreignWorkspace.Remove();
}

static byte[] SerializeResponse(GameEndpointDescriptor endpoint)
    => MessagePackSerializer.Serialize(
        endpoint.ResponseType,
        Activator.CreateInstance(endpoint.ResponseType)
            ?? throw new InvalidOperationException($"{endpoint.ResponseType.FullName} could not be created."));

static byte[] CreateAnalysisResponse(
    GameEndpointDescriptor endpoint,
    SingleModeChara charaInfo,
    SingleModeEventInfo[] uncheckedEvents)
{
    var response = Activator.CreateInstance(endpoint.ResponseType)
        ?? throw new InvalidOperationException($"{endpoint.ResponseType.FullName} could not be created.");
    var dataField = endpoint.ResponseType.GetField("data", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
        ?? throw new InvalidOperationException($"{endpoint.ResponseType.FullName}.data was not found.");
    var data = Activator.CreateInstance(dataField.FieldType)
        ?? throw new InvalidOperationException($"{dataField.FieldType.FullName} could not be created.");

    if (endpoint.Path.EndsWith("/check_event", StringComparison.Ordinal))
    {
        RequirePublicField(dataField.FieldType, "chara_info", typeof(SingleModeChara))
            .SetValue(data, charaInfo);
        RequirePublicField(dataField.FieldType, "unchecked_event_array", typeof(SingleModeEventInfo[]))
            .SetValue(data, uncheckedEvents);
    }
    else if (endpoint.Path.EndsWith("/load", StringComparison.Ordinal))
    {
        RequirePublicField(dataField.FieldType, "single_mode_load_common", typeof(SingleModeLoadCommon))
            .SetValue(
                data,
                new SingleModeLoadCommon
                {
                    chara_info = charaInfo,
                    unchecked_event_array = uncheckedEvents
                });
    }
    else
    {
        throw new InvalidOperationException($"{endpoint.Path} is not a CheckEvent or Load endpoint.");
    }

    dataField.SetValue(response, data);
    return MessagePackSerializer.Serialize(endpoint.ResponseType, response);
}

static FieldInfo RequirePublicField(Type owner, string name, Type expectedType)
{
    var field = owner.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
        ?? throw new InvalidOperationException($"{owner.FullName}.{name} was not found.");
    AssertEqual(expectedType, field.FieldType, $"{owner.FullName}.{name} has an unexpected type.");
    return field;
}

static GameEndpointDescriptor ResolveRamenPacketEndpoint(byte[] payload)
{
    using var document = JsonDocument.Parse(MessagePackSerializer.ConvertToJson(payload));
    if (!document.RootElement.TryGetProperty("data", out var data)
        || data.ValueKind != JsonValueKind.Object)
    {
        throw new InvalidOperationException("Raw Ramen packet has no object-valued data field.");
    }

    var isCheckEvent = data.TryGetProperty("chara_info", out _)
        && data.TryGetProperty("unchecked_event_array", out _);
    var isLoad = data.TryGetProperty("single_mode_load_common", out var common)
        && common.ValueKind == JsonValueKind.Object
        && common.TryGetProperty("chara_info", out _)
        && common.TryGetProperty("unchecked_event_array", out _);
    return (isCheckEvent, isLoad) switch
    {
        (true, false) => GameEndpointCatalog.ByEndpointType[typeof(GameApi.SingleModeRamen.CheckEvent)],
        (false, true) => GameEndpointCatalog.ByEndpointType[typeof(GameApi.SingleModeRamen.Load)],
        _ => throw new InvalidOperationException(
            "Raw Ramen packet must have exactly one supported CheckEvent or Load schema.")
    };
}

static (SingleModeChara CharaInfo, SingleModeEventInfo[] UncheckedEvents) GetRamenAnalysisData(object response)
    => response switch
    {
        SingleModeRamenCheckEventResponse checkEvent =>
            (checkEvent.data.chara_info, checkEvent.data.unchecked_event_array),
        SingleModeRamenLoadResponse load =>
            (load.data.single_mode_load_common.chara_info, load.data.single_mode_load_common.unchecked_event_array),
        _ => throw new InvalidOperationException(
            $"Unexpected raw Ramen response type: {response.GetType().FullName}.")
    };

static async ValueTask DispatchHostResponse(string canonicalUrl, byte[] payload)
    => await SmokeAnalyzerRegistry.Active.DispatchResponse(canonicalUrl, payload);

static void InitializeHostConfigForSmoke()
    => UmamusumeResponseAnalyzer.Config.Initialize();

static async Task InitializeSmokeDatabase(IEnumerable<UraSkillData> skills)
{
    WriteBrotliJson(Database.EVENT_NAME_FILEPATH, new List<Story>());
    WriteBrotliJson(Database.NAMES_FILEPATH, new List<BaseName>());
    WriteBrotliJson(Database.SKILLS_FILEPATH, skills.ToList());
    WriteBrotliJson(Database.SKILL_UPGRADE_SPECIALITY_FILEPATH, new List<SkillUpgradeSpeciality>());
    WriteBrotliJson(Database.TALENT_SKILLS_FILEPATH, new Dictionary<int, TalentSkillData[]>());
    WriteBrotliJson(Database.FACTOR_IDS_FILEPATH, new Dictionary<int, string>());
    WriteBrotliJson(Database.SADDLE_IDS_FILEPATH, Array.Empty<int>());
    WriteBrotliJson(Database.SUCCESSION_RELATION_FILEPATH, new SuccessionRelationTable());
    if (await Database.Initialize() != DatabaseAvailability.Ready)
        throw new InvalidOperationException("SkillTips smoke database fixture did not load atomically.");
}

static void WriteBrotliJson<T>(string path, T value)
{
    using var file = File.Create(path);
    using var brotli = new BrotliStream(file, CompressionLevel.SmallestSize);
    using var writer = new StreamWriter(brotli, Encoding.UTF8);
    using var json = new JsonTextWriter(writer);
    Newtonsoft.Json.JsonSerializer.CreateDefault().Serialize(json, value);
}

static Workspace RequireSkillTipsWorkspace(WorkspaceSmokeSession ui)
{
    var workspace = Workspace.Current;
    AssertTrue(
        workspace is not null
        && string.Equals(workspace.Title, "SkillTipsResponseAnalyzer", StringComparison.OrdinalIgnoreCase)
        && ui.CaptureScreen().Contains(I18N_RecommendedSkills, StringComparison.Ordinal),
        "The visible SkillTipsResponseAnalyzer workspace was not found.");
    return workspace!;
}

static void AssertNoSkillTipsWorkspace(WorkspaceSmokeSession ui, string message)
{
    var screen = ui.CaptureScreen();
    AssertTrue(
        !string.Equals(Workspace.Current?.Title, "SkillTipsResponseAnalyzer", StringComparison.OrdinalIgnoreCase)
        && !screen.Contains(I18N_RecommendedSkills, StringComparison.Ordinal),
        message);
}

static void AssertNoNewSkillTipsOutput(
    WorkspaceSmokeSession ui,
    Workspace? currentBefore,
    string screenBefore,
    string operation)
{
    AssertTrue(
        ReferenceEquals(Workspace.Current, currentBefore)
        && string.Equals(ui.CaptureScreen(), screenBefore, StringComparison.Ordinal),
        $"{operation} must not change the visible workspace, panel, or notification state.");
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message} Expected={expected}, Actual={actual}");
}

sealed class SmokePluginContext : IPluginContext
{
    public SmokePluginContext(IApplication application)
    {
        Application = application;
        AnalyzerRegistry = new();
    }

    public IApplication Application { get; }
    public SmokeAnalyzerRegistry AnalyzerRegistry { get; }
    public IPluginAnalyzerRegistry Analyzers => AnalyzerRegistry;
    public bool IsPluginAvailable(string internalName) => false;

    public void ReportBackgroundFailure(Exception error) => throw new InvalidOperationException("Unexpected plugin background failure.", error);
}

sealed class SmokeAnalyzerRegistry : IPluginAnalyzerRegistry
{
    static readonly GameHttpHeaders EmptyHeaders = new(null, null, null, null, null, null);
    readonly List<SmokeAnalyzerRegistration> registrations = [];

    public SmokeAnalyzerRegistry() => Active = this;

    public static SmokeAnalyzerRegistry Active { get; private set; } = null!;
    public IReadOnlyList<SmokeAnalyzerRegistration> Registrations => registrations;

    public void Register<TPayload>(
        AnalyzerKind kind,
        IReadOnlyList<EndpointPattern> patterns,
        Func<AnalyzerInvocation<TPayload>, ValueTask> handler,
        int priority = 0)
    {
        var endpoints = GameEndpointCatalog.ByPath.Values
            .Where(endpoint => patterns.Any(pattern => Matches(pattern, endpoint.Path)))
            .DistinctBy(endpoint => endpoint.Path)
            .OrderBy(endpoint => endpoint.Path, StringComparer.Ordinal)
            .ToArray();
        if (endpoints.Length == 0)
            throw new InvalidOperationException($"Register<{typeof(TPayload).Name}> matched no current Host endpoints.");

        registrations.Add(new(
            typeof(TPayload),
            kind,
            [.. patterns],
            priority,
            endpoints,
            async (endpoint, payload, headers) =>
            {
                var projected = typeof(TPayload) == typeof(ReadOnlyMemory<byte>)
                    ? (TPayload)(object)new ReadOnlyMemory<byte>(payload)
                    : MessagePackSerializer.Deserialize<TPayload>(payload);
                await handler(new(endpoint, projected, headers));
            }));
    }

    public async ValueTask DispatchResponse(string canonicalUrl, byte[] payload)
    {
        if (!GameEndpointCatalog.ByPath.TryGetValue(canonicalUrl, out var endpoint))
            throw new InvalidOperationException($"Unknown endpoint: {canonicalUrl}");

        var matches = registrations
            .Where(registration => registration.Kind == AnalyzerKind.Response
                && registration.Endpoints.Any(candidate => candidate.Path == canonicalUrl))
            .OrderByDescending(registration => registration.Priority)
            .ToArray();
        if (matches.Length == 0)
            throw new InvalidOperationException($"No response analyzer matched {canonicalUrl}.");

        foreach (var registration in matches)
            await registration.Dispatch(endpoint, payload, EmptyHeaders);
    }

    public void Clear() => registrations.Clear();

    static bool Matches(EndpointPattern pattern, string path)
        => pattern.Kind switch
        {
            EndpointPatternKind.Exact => string.Equals(pattern.Pattern, path, StringComparison.Ordinal),
            EndpointPatternKind.Wildcard => Regex.IsMatch(
                path,
                $"\\A{Regex.Escape(pattern.Pattern).Replace("\\*", "[^/]*", StringComparison.Ordinal)}\\z",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                TimeSpan.FromMilliseconds(100)),
            EndpointPatternKind.Regex => Regex.IsMatch(
                path,
                $"\\A(?:{pattern.Pattern})\\z",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                TimeSpan.FromMilliseconds(100)),
            _ => throw new ArgumentOutOfRangeException(nameof(pattern))
        };
}

sealed record SmokeAnalyzerRegistration(
    Type PayloadType,
    AnalyzerKind Kind,
    IReadOnlyList<EndpointPattern> Patterns,
    int Priority,
    IReadOnlyList<GameEndpointDescriptor> Endpoints,
    Func<GameEndpointDescriptor, byte[], GameHttpHeaders, ValueTask> Dispatch);
