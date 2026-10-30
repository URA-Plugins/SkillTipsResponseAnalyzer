using System.Text.Json;
using System.Text.Json.Serialization;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using static SkillTipsResponseAnalyzer.i18n.ParseSkillTipsResponse;

namespace SkillTipsResponseAnalyzer;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class SkillTipsSettings
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowDuplicateProperties = false,
        WriteIndented = true
    };

    static string FilePath => Path.GetFullPath(Path.Combine("PluginData", "SkillTipsResponseAnalyzer", "settings.json"));

    [JsonRequired]
    public bool SortByCostEffectiveness { get; init; }

    internal static SkillTipsSettings Load()
    {
        var path = FilePath;
        if (!File.Exists(path))
            return new();

        try
        {
            return JsonSerializer.Deserialize<SkillTipsSettings>(File.ReadAllText(path), JsonOptions)
                ?? throw new JsonException(I18N_SettingsObjectRequired);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(string.Format(I18N_InvalidSettings, path, ex.Message), ex);
        }
    }

    internal void Save()
    {
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    internal static async Task<SkillTipsSettings> PromptAsync(
        IApplication application,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        cancellationToken.ThrowIfCancellationRequested();
        if (application.TopRunnable is null && Environment.CurrentManagedThreadId != application.MainThreadId)
            throw new InvalidOperationException(I18N_SettingsNoUiSession);

        var draft = Load();
        if (application.TopRunnable is null)
            return RunDialog(application, draft, cancellationToken);

        return await SkillTipsDialogs.RunAsync(application,
            () => RunDialog(application, draft, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    static SkillTipsSettings RunDialog(IApplication application, SkillTipsSettings draft, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var dialog = new Dialog
        {
            Id = "skill-tips-settings",
            Title = I18N_SettingsTitle,
            Width = 56,
            Height = 8
        };
        var sort = new OptionSelector
        {
            Id = "skill-tips-sort",
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Labels = [I18N_DefaultSort, I18N_CostEffectivenessSort],
            Value = draft.SortByCostEffectiveness ? 1 : 0
        };
        dialog.Add(sort);

        var accepted = false;
        var save = new Button { Id = "skill-tips-settings-save", Text = I18N_Save, IsDefault = true };
        save.Accepting += (_, e) =>
        {
            accepted = true;
            application.RequestStop(dialog);
            e.Handled = true;
        };
        var cancel = new Button { Id = "skill-tips-settings-cancel", Text = I18N_Cancel };
        cancel.Accepting += (_, e) =>
        {
            application.RequestStop(dialog);
            e.Handled = true;
        };
        dialog.AddButton(cancel);
        dialog.AddButton(save);
        sort.SetFocus();

        var closed = false;
        using (cancellationToken.Register(() => application.Invoke(() =>
        {
            if (!closed)
                application.RequestStop(dialog);
        })))
        {
            try { application.Run(dialog); }
            finally { closed = true; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!accepted)
            throw new OperationCanceledException(I18N_SettingsCanceled, cancellationToken);

        return new()
        {
            SortByCostEffectiveness = sort.Value switch
            {
                0 => false,
                1 => true,
                _ => throw new InvalidOperationException(I18N_SettingsSortNotSelected)
            }
        };
    }
}
