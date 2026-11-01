using System.Drawing;
using System.Collections.Immutable;
using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TAttribute = Terminal.Gui.Drawing.Attribute;
using TColor = Terminal.Gui.Drawing.Color;
using UmamusumeResponseAnalyzer.TerminalGui;
using static SkillTipsResponseAnalyzer.i18n.ParseSkillTipsResponse;

namespace SkillTipsResponseAnalyzer;

internal sealed record SkillTipsDisplaySnapshot(
    SkillTipsTraining Training,
    int Speed,
    int Stamina,
    int Power,
    int Guts,
    int Wisdom,
    int TotalSkillPoints,
    int UsedSkillPoints,
    int RemainingSkillPoints,
    ImmutableArray<SkillTipsDisplaySkill> Skills,
    int LearnedSkillScore,
    int RecommendedSkillScore,
    int StatusScore,
    int PredictedTotalScore,
    string CurrentRank,
    string NextRank,
    int ScoreToNextRank,
    double RankProgress,
    ImmutableArray<string> KnownIssues,
    ImmutableArray<string> Warnings,
    string OptimizationStatus,
    long ObjectiveUpperBound,
    ImmutableArray<int> LearningSkillIds,
    ImmutableArray<int> LearnedSkillIds,
    ImmutableArray<int> FinalSkillIds,
    ImmutableArray<SkillEvolutionChoice> Evolutions);

internal sealed record SkillTipsDisplaySkill(int SkillId, string Name, int SkillPoints, int Score);


internal static class SkillTipsDisplayRenderer
{
    const int TextViewEndOfLineCellWidth = 1;
    const int ScrollBarCellWidth = 1;
    const int FrameBorderCellWidth = 2;

    public static WorkspaceContent Render(
        SkillTipsDisplaySnapshot display,
        bool sortByCostEffectiveness,
        SkillTipsBridge bridge,
        IApplication application,
        Workspace workspace)
        => new(() => new SkillTipsDashboardView(display, sortByCostEffectiveness, bridge, application, workspace));

    static string[] WrapLines(IEnumerable<string> lines, int width)
        => [.. lines.SelectMany(line => line.Length == 0
            ? [string.Empty]
            : TextFormatter.WordWrapText(line, Math.Max(1, width)))];

    sealed class SkillTipsDashboardView : View
    {
        readonly FrameView skills;
        readonly OptionSelector sort;
        readonly SkillTipsTableView table;
        readonly SkillTipsDetailsView details;
        readonly Button selectRecommended;
        readonly SkillTipsDisplaySnapshot display;
        readonly SkillTipsBridge bridge;
        readonly IApplication application;
        readonly Workspace workspace;
        readonly CancellationTokenSource lifetime = new();
        readonly int requiredSkillsWidth;
        bool disposed;
        bool selecting;
        int unlockPosition;
        string? lastBridgeError;
        Size lastFrameSize;
        bool? lastWide;

        public SkillTipsDashboardView(
            SkillTipsDisplaySnapshot display,
            bool sortByCostEffectiveness,
            SkillTipsBridge bridge,
            IApplication application,
            Workspace workspace)
        {
            this.display = display;
            this.bridge = bridge;
            this.application = application;
            this.workspace = workspace;
            Id = "skill-tips-root";
            Width = Dim.Fill();
            Height = Dim.Fill();
            CanFocus = true;
            TabStop = TabBehavior.NoStop;
            ViewportSettings = ViewportSettingsFlags.None;
            SetScheme(SkillTipsPalette.BaseScheme);

            (table, requiredSkillsWidth) = CreateSkillTable(display.Skills);
            sort = new()
            {
                Id = "skill-tips-sort",
                Labels = [I18N_DefaultSort, I18N_CostEffectivenessSort],
                Orientation = Orientation.Horizontal,
                TabBehavior = TabBehavior.NoStop, // Tab 切换区域，方向键在排序选项间移动。
                Value = sortByCostEffectiveness ? 1 : 0,
                Width = Dim.Auto(),
                Height = 1
            };
            sort.SetScheme(SkillTipsPalette.BaseScheme);
            table.Y = 1;
            skills = new FrameView
            {
                Id = "skill-tips-skills",
                Title = I18N_RecommendedSkills,
                BorderStyle = LineStyle.Single,
                CanFocus = true,
                TabStop = TabBehavior.NoStop
            };
            skills.SetScheme(SkillTipsPalette.BaseScheme);
            selectRecommended = new()
            {
                Id = "skill-tips-select-recommended",
                Text = I18N_SelectRecommended,
                Y = Pos.AnchorEnd(),
                Visible = false,
                IsDefault = false
            };
            selectRecommended.SetScheme(SkillTipsPalette.BaseScheme);
            selectRecommended.Accepting += async (_, e) =>
            {
                e.Handled = true;
                await SelectRecommendedAsync();
            };
            selectRecommended.HasFocusChanged += (_, _) =>
            {
                if (selectRecommended.HasFocus)
                {
                    HorizontalScrollBar.Value = 0;
                    RevealVertically(skills.Frame.Bottom - 1 - selectRecommended.Frame.Height);
                }
            };
            skills.Add(sort, table, selectRecommended);

            details = new(display)
            {
                Id = "skill-tips-details"
            };
            selectRecommended.KeyDown += (_, key) =>
            {
                if (key == Key.Tab)
                    key.Handled = details.SetFocus();
                else if (key == Key.Tab.WithShift)
                    key.Handled = table.Body.SetFocus();
            };
            table.NextFocus = details;
            table.PreviousFocus = sort;
            details.KeyDown += (_, key) =>
            {
                if (key == Key.Tab)
                    key.Handled = sort.SetFocus();
                else if (key == Key.Tab.WithShift)
                    key.Handled = (selectRecommended.Visible && selectRecommended.Enabled
                        ? (View)selectRecommended : table.Body).SetFocus();
                else if (key == Key.PageDown)
                    key.Handled = ScrollVertical(Math.Max(1, Viewport.Height - 1)) is not false;
                else if (key == Key.PageUp)
                    key.Handled = ScrollVertical(-Math.Max(1, Viewport.Height - 1)) is not false;
            };
            sort.KeyDown += (_, key) =>
            {
                if (key == Key.Tab)
                    key.Handled = table.Body.SetFocus();
                else if (key == Key.Tab.WithShift)
                    key.Handled = details.SetFocus();
            };
            sort.ValueChanged += (_, _) => UpdateSort();
            UpdateSort();
            sort.HasFocusChanged += (_, _) =>
            {
                if (sort.HasFocus)
                    RevealVertically(skills.Frame.Y);
            };
            table.Body.HasFocusChanged += (_, _) =>
            {
                if (table.Body.HasFocus)
                    RevealVertically(skills.Frame.Y);
            };
            details.HasFocusChanged += (_, _) =>
            {
                if (details.HasFocus)
                    RevealVertically(details.Frame.Y);
            };
            Add(skills, details);
            HasFocusChanged += (_, _) =>
            {
                if (!HasFocus)
                    unlockPosition = 0;
            };
            bridge.Changed += BridgeChanged;
            application.Keyboard.KeyDown += ApplicationKeyDown;
            UpdateBridgeState(bridge.Current, bridge.LastError, false);

            void UpdateSort() => table.SetRows(sort.Value == 1
                ? display.Skills.OrderByDescending(row => (double)row.Score / row.SkillPoints)
                : display.Skills);
        }

        void BridgeChanged()
        {
            if (disposed)
                return;
            var state = bridge.Current;
            var error = bridge.LastError;
            application.Invoke(() =>
            {
                if (!disposed)
                    UpdateBridgeState(state, error, true);
            });
        }

        void UpdateBridgeState(SkillTipsBridgeState? state, string? connectionError, bool allowScroll)
        {
            var error = connectionError ?? state?.Error;
            if (!selecting && !string.IsNullOrEmpty(error) && error != lastBridgeError)
                workspace.Notify(string.Format(I18N_BridgeError, error), UiSeverity.Warning);
            lastBridgeError = error;
            var matches = connectionError is null && state is { Active: true, Error: null, Training: not null }
                && display.Training.Matches(state.Training);
            table.Body.SetSelectedSkills(matches ? state!.SelectedSkillIds : [],
                allowScroll && matches && state!.SelectionEvent && IsCurrentView());
            selectRecommended.Enabled = matches && !selecting && !display.LearningSkillIds.IsEmpty;
            table.NextFocus = selectRecommended.Visible && selectRecommended.Enabled
                ? selectRecommended : details;
        }

        bool IsCurrentView()
        {
            if (!ReferenceEquals(Workspace.Current, workspace))
                return false;
            for (View? ancestor = this; ancestor is not null; ancestor = ancestor.SuperView)
                if (ReferenceEquals(ancestor, application.TopRunnableView))
                    return true;
            return false;
        }

        void ApplicationKeyDown(object? sender, Key key)
        {
            if (disposed || selectRecommended.Visible)
                return;
            if (key.Handled || key.IsCtrl || key.IsAlt || key.IsShift || !IsCurrentView())
            {
                unlockPosition = 0;
                return;
            }
#pragma warning disable CS0618
            if (application.TopRunnableView?.MostFocused is TextField or TextView { ReadOnly: false })
#pragma warning restore CS0618
            {
                unlockPosition = 0;
                return;
            }
            const string unlock = "thisisunsafe";
            var character = key.AsRune.Value;
            unlockPosition = character == unlock[unlockPosition]
                ? unlockPosition + 1 : character == unlock[0] ? 1 : 0;
            if (unlockPosition != unlock.Length)
                return;
            selectRecommended.Visible = true;
            table.Height = Dim.Fill(selectRecommended.GetAutoHeight());
            UpdateBridgeState(bridge.Current, bridge.LastError, false);
            SetNeedsLayout();
            SetNeedsDraw();
        }

        async Task SelectRecommendedAsync()
        {
            if (disposed || selecting || !selectRecommended.Visible || !selectRecommended.Enabled || !IsCurrentView())
                return;
            var cancellationToken = lifetime.Token;
            selecting = true;
            UpdateBridgeState(bridge.Current, bridge.LastError, false);
            try
            {
                var prepared = await bridge.PrepareAsync(display.Training,
                    [.. display.LearningSkillIds], cancellationToken).ConfigureAwait(false);
                if (prepared.RemainingPoints != display.RemainingSkillPoints)
                    throw new InvalidDataException(string.Format(SkillPlanning.Text("CostMismatch"),
                        display.RemainingSkillPoints, prepared.RemainingPoints));
                var accepted = false;
                await InvokeAsync(() =>
                {
                    accepted = IsCurrentView();
                }).ConfigureAwait(false);
                if (accepted && prepared.ExtraSkillIds.Length != 0)
                    accepted = await SkillTipsDialogs.RunAsync(application,
                        () => !disposed && IsCurrentView() && ConfirmReplacement(prepared.ExtraSkillIds),
                        cancellationToken).ConfigureAwait(false);
                if (accepted)
                    await bridge.ReplaceAsync(prepared, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await InvokeAsync(() =>
                {
                    if (IsCurrentView())
                        workspace.Notify(string.Format(I18N_SelectionFailed, ex.Message), UiSeverity.Error);
                }).ConfigureAwait(false);
            }
            finally
            {
                await InvokeAsync(() =>
                {
                    selecting = false;
                    UpdateBridgeState(bridge.Current, bridge.LastError, false);
                }).ConfigureAwait(false);
            }
        }

        Task InvokeAsync(Action action)
        {
            if (disposed)
                return Task.CompletedTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            application.Invoke(() =>
            {
                if (disposed)
                {
                    completion.TrySetResult();
                    return;
                }
                try
                {
                    action();
                    completion.TrySetResult();
                }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
            return completion.Task;
        }

        bool ConfirmReplacement(int[] extraSkillIds)
        {
            using var dialog = new Dialog
            {
                Id = "skill-tips-confirm-replace",
                Title = I18N_ReplaceSelection,
                Width = Dim.Percent(85),
                Height = Dim.Percent(50)
            };
#pragma warning disable CS0618
            dialog.Add(new TextView
            {
                ReadOnly = true,
                WordWrap = true,
                Width = Dim.Fill(),
                Height = Dim.Fill(1),
                Text = string.Format(I18N_ReplaceSelectionPrompt, string.Join(", ", extraSkillIds))
            });
#pragma warning restore CS0618
            var accepted = false;
            var replace = new Button { Id = "skill-tips-replace", Text = I18N_ReplaceSelection };
            replace.Accepting += (_, e) =>
            {
                accepted = true;
                e.Handled = true;
                application.RequestStop(dialog);
            };
            var cancel = new Button { Id = "skill-tips-cancel-replace", Text = I18N_Cancel, IsDefault = true };
            cancel.Accepting += (_, e) =>
            {
                e.Handled = true;
                application.RequestStop(dialog);
            };
            dialog.AddButton(replace);
            dialog.AddButton(cancel);
            var cancellationToken = lifetime.Token;
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
            return accepted && !cancellationToken.IsCancellationRequested;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                bridge.Changed -= BridgeChanged;
                application.Keyboard.KeyDown -= ApplicationKeyDown;
                lifetime.Cancel();
                lifetime.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void OnSubViewLayout(LayoutEventArgs args)
        {
            var frameWidth = Math.Max(0, Frame.Width);
            var frameHeight = Math.Max(0, Frame.Height);
            var gap = frameWidth == 0 ? 0 : 1;
            var requiredSortWidth = Math.Max(sort.GetAutoWidth(),
                selectRecommended.Visible ? selectRecommended.GetAutoWidth() : 0) + FrameBorderCellWidth;
            var skillsWidth = Math.Max(requiredSkillsWidth, requiredSortWidth);
            var wideDetailsWidth = Math.Max(1, frameWidth - skillsWidth - gap);
            var wide =
                frameWidth >= skillsWidth + gap + details.PreferredWidth
                && frameHeight >= details.GetMinimumHeight(wideDetailsWidth);
            var revealFocusedRegion = lastFrameSize != Frame.Size || lastWide != wide;
            var viewportSettings = wide
                ? ViewportSettingsFlags.None
                : ViewportSettingsFlags.HasScrollBars;
            if (ViewportSettings != viewportSettings)
                ViewportSettings = viewportSettings;
            int contentWidth;
            int contentHeight;
            if (wide)
            {
                contentWidth = frameWidth;
                contentHeight = frameHeight;
                skills.Frame = new(0, 0, skillsWidth, contentHeight);
                details.Frame = new(
                    skillsWidth + gap,
                    0,
                    wideDetailsWidth,
                    contentHeight);
            }
            else
            {
                var viewportWidthWithVerticalScrollBar = Math.Max(
                    1,
                    frameWidth - ScrollBarCellWidth);
                contentWidth = Math.Max(
                    viewportWidthWithVerticalScrollBar,
                    Math.Max(
                        Math.Max(I18N_RecommendedSkills.GetColumns() + 4, requiredSortWidth),
                        details.MinimumAccessibleWidth));
                var horizontalScrollBarHeight =
                    contentWidth > viewportWidthWithVerticalScrollBar
                        ? ScrollBarCellWidth
                        : 0;
                var visibleHeight = Math.Max(1, frameHeight - horizontalScrollBarHeight);
                var skillsHeight = Math.Max(selectRecommended.Visible ? 9 : 8, visibleHeight / 2);
                var detailsHeight = Math.Max(
                    details.GetMinimumHeight(contentWidth),
                    visibleHeight - skillsHeight - gap);
                contentHeight = skillsHeight + gap + detailsHeight;
                skills.Frame = new(0, 0, contentWidth, skillsHeight);
                details.Frame = new(0, skillsHeight + gap, contentWidth, detailsHeight);
            }

            var contentSize = new Size(contentWidth, contentHeight);
            if (GetContentSize() != contentSize)
                SetContentSize(contentSize);

            var maxX = Math.Max(0, contentWidth - Viewport.Width);
            var maxY = Math.Max(0, contentHeight - Viewport.Height);
            var viewportX = Math.Min(Viewport.X, maxX);
            var viewportY = Math.Min(Viewport.Y, maxY);
            if (viewportX != Viewport.X || viewportY != Viewport.Y)
                Viewport = new(viewportX, viewportY, Viewport.Width, Viewport.Height);

            base.OnSubViewLayout(args);
            if (revealFocusedRegion)
            {
                if (sort.HasFocus || table.Body.HasFocus)
                    RevealVertically(skills.Frame.Y);
                else if (selectRecommended.HasFocus)
                {
                    HorizontalScrollBar.Value = 0;
                    RevealVertically(skills.Frame.Bottom - 1 - selectRecommended.Frame.Height);
                }
                else if (details.HasFocus)
                    RevealVertically(details.Frame.Y);
            }
            lastFrameSize = Frame.Size;
            lastWide = wide;
        }

        void RevealVertically(int y)
        {
            var max = Math.Max(
                0,
                VerticalScrollBar.ScrollableContentSize
                    - VerticalScrollBar.VisibleContentSize);
            VerticalScrollBar.Value = Math.Clamp(y, 0, max);
        }

        static (SkillTipsTableView Table, int RequiredFrameWidth) CreateSkillTable(
            IReadOnlyList<SkillTipsDisplaySkill> rows)
        {
            var columnWidths = new[]
            {
                Math.Max(
                    I18N_Columns_SkillName.GetColumns(),
                    rows.Select(x => x.Name.GetColumns()).DefaultIfEmpty().Max()),
                Math.Max(
                    I18N_Columns_RequireSP.GetColumns(),
                    rows.Select(x => x.SkillPoints.ToString().GetColumns()).DefaultIfEmpty().Max()),
                Math.Max(
                    I18N_Columns_Grade.GetColumns(),
                    rows.Select(x => x.Score.ToString().GetColumns()).DefaultIfEmpty().Max())
            };
            var table = new SkillTipsTableView(rows, columnWidths)
            {
                Id = "skill-tips-table",
                Width = Dim.Fill(),
                Height = Dim.Fill()
            };
            var tableContentWidth = columnWidths.Sum() + columnWidths.Length + 1;
            var requiredFrameWidth = tableContentWidth
                + TextViewEndOfLineCellWidth
                + ScrollBarCellWidth
                + FrameBorderCellWidth;
            return (table, requiredFrameWidth);
        }
    }

#pragma warning disable CS0618 // TextView is the Host-provided native cell-aware scrolling surface.
    sealed class SkillTipsTableView : View
    {
        readonly TextView header;
        readonly int contentWidth;
        readonly IReadOnlyList<int> columnWidths;

        public SkillTipsTableView(
            IReadOnlyList<SkillTipsDisplaySkill> rows,
            IReadOnlyList<int> columnWidths)
        {
            CanFocus = true;
            TabStop = TabBehavior.NoStop;
            SetScheme(SkillTipsPalette.BaseScheme);
            this.columnWidths = columnWidths;
            contentWidth = columnWidths.Sum() + columnWidths.Count + 1;

            header = new()
            {
                Id = "skill-tips-table-header",
                ReadOnly = true,
                WordWrap = false,
                CanFocus = false,
                TabStop = TabBehavior.NoStop,
                Text = string.Join(
                    Environment.NewLine,
                    FormatRow(
                        I18N_Columns_SkillName,
                        I18N_Columns_RequireSP,
                        I18N_Columns_Grade,
                        columnWidths),
                    $"├{new string('─', columnWidths[0])}" +
                    $"┼{new string('─', columnWidths[1])}" +
                    $"┼{new string('─', columnWidths[2])}┤")
            };
            header.SetScheme(SkillTipsPalette.BaseScheme);
            Body = new([], header);
            SetRows(rows);
            Add(header, Body);
        }

        public SkillTipsTableBody Body { get; }

        public void SetRows(IEnumerable<SkillTipsDisplaySkill> rows)
        {
            Body.Rows = [.. rows];
            Body.Text = string.Join(Environment.NewLine, Body.Rows.Select(row => FormatRow(
                row.Name,
                row.SkillPoints.ToString(),
                row.Score.ToString(),
                columnWidths)));
        }

        public View? NextFocus
        {
            get => Body.NextFocus;
            set => Body.NextFocus = value;
        }

        public View? PreviousFocus
        {
            get => Body.PreviousFocus;
            set => Body.PreviousFocus = value;
        }

        protected override void OnSubViewLayout(LayoutEventArgs args)
        {
            var width = Math.Max(1, Viewport.Width);
            var height = Math.Max(0, Viewport.Height);
            var headerHeight = Math.Min(2, height);
            header.Frame = new(
                0,
                0,
                Math.Max(0, width - ScrollBarCellWidth),
                headerHeight);
            header.SetContentSize(new(
                contentWidth + TextViewEndOfLineCellWidth,
                2));
            Body.Frame = new(0, headerHeight, width, Math.Max(0, height - headerHeight));
            Body.SyncHeader();
            base.OnSubViewLayout(args);
        }

        static string FormatRow(
            string name,
            string skillPoints,
            string score,
            IReadOnlyList<int> widths)
            => $"│{PadEnd(name, widths[0])}" +
                $"│{PadStart(skillPoints, widths[1])}" +
                $"│{PadStart(score, widths[2])}│";

        static string PadEnd(string value, int width)
            => value + new string(' ', Math.Max(0, width - value.GetColumns()));

        static string PadStart(string value, int width)
            => new string(' ', Math.Max(0, width - value.GetColumns())) + value;
    }

    sealed class SkillTipsTableBody : TextView
    {
        readonly TextView header;
        HashSet<int> selectedSkillIds = [];

        public SkillTipsDisplaySkill[] Rows { get; set; } = [];

        public SkillTipsTableBody(string[] rows, TextView header)
        {
            this.header = header;
            Id = "skill-tips-table";
            ReadOnly = true;
            WordWrap = false;
            CanFocus = true;
            TabStop = TabBehavior.TabStop;
            ViewportSettings = ViewportSettingsFlags.HasScrollBars;
            Text = string.Join(Environment.NewLine, rows);
            SetScheme(SkillTipsPalette.TableScheme);
        }

        public View? NextFocus { get; set; }
        public View? PreviousFocus { get; set; }

        public void SetSelectedSkills(int[] skillIds, bool autoScroll)
        {
            if (selectedSkillIds.SetEquals(skillIds))
                return;
            var previous = selectedSkillIds;
            selectedSkillIds = [.. skillIds];
            SetNeedsDraw();
            if (!autoScroll)
                return;
            var screen = ViewportToScreen();
            var visible = screen;
            for (var ancestor = SuperView; ancestor is not null; ancestor = ancestor.SuperView)
                visible.Intersect(ancestor.ViewportToScreen());
            if (visible.Width <= 0 || visible.Height <= 0)
                return;
            var first = Viewport.Y + visible.Top - screen.Top;
            var count = Math.Min(visible.Height, Rows.Length - first);
            if (count <= 0)
                return;
            var threshold = (count * 3 + 3) / 4;
            var prefix = Rows.Skip(first).Take(threshold);
            if (prefix.All(row => selectedSkillIds.Contains(row.SkillId))
                && prefix.Any(row => !previous.Contains(row.SkillId)))
                ScrollTo(new(Viewport.X, Math.Min(Viewport.Y + threshold, Math.Max(0, Rows.Length - Viewport.Height))));
        }

        protected override void OnDrawReadOnlyColor(List<Cell> line, int idxCol, int idxRow)
        {
            base.OnDrawReadOnlyColor(line, idxCol, idxRow);
            if (idxRow < Rows.Length && selectedSkillIds.Contains(Rows[idxRow].SkillId))
                SetAttribute(SkillTipsPalette.Selected);
        }

        protected override bool OnMouseEvent(Mouse mouse)
        {
            if (mouse.Flags.HasFlag(MouseFlags.WheeledRight))
                ScrollHorizontal(4);
            else if (mouse.Flags.HasFlag(MouseFlags.WheeledLeft))
                ScrollHorizontal(-4);
            else if (mouse.Flags.HasFlag(MouseFlags.WheeledDown))
                VerticalScrollBar.Value += VerticalScrollBar.Increment;
            else if (mouse.Flags.HasFlag(MouseFlags.WheeledUp))
                VerticalScrollBar.Value -= VerticalScrollBar.Increment;
            else if ((mouse.Flags & (MouseFlags.LeftButtonPressed | MouseFlags.LeftButtonReleased
                | MouseFlags.LeftButtonClicked | MouseFlags.LeftButtonDoubleClicked | MouseFlags.LeftButtonTripleClicked)) == 0)
                return false;

            // This read-only table handles focus, not TextView's glyph-based editor hit testing.
            SetFocus();
            return true;
        }

        public void SyncHeader()
        {
            if (header is null)
                return;

            var maxX = Math.Max(
                0,
                header.GetContentSize().Width - header.Viewport.Width);
            var x = Math.Min(Viewport.X, maxX);
            if (header.Viewport.X != x)
                header.Viewport = new(x, 0, header.Viewport.Width, header.Viewport.Height);
        }

        protected override void OnViewportChanged(DrawEventArgs args)
        {
            base.OnViewportChanged(args);
            SyncHeader();
        }

        protected override bool OnKeyDown(Key key)
        {
            if (key == Key.Tab && NextFocus?.SetFocus() == true)
                return true;
            if (key == Key.Tab.WithShift && PreviousFocus?.SetFocus() == true)
                return true;
            if (key == Key.CursorLeft.WithShift)
                return ScrollHorizontal(-4) is not false;
            if (key == Key.CursorRight.WithShift)
                return ScrollHorizontal(4) is not false;
            return base.OnKeyDown(key);
        }

    }
#pragma warning restore CS0618

    sealed class SkillTipsDetailsView : View
    {
        const int Gap = 1;
        readonly SkillTipsSummaryView summary;
        readonly FrameView knownIssues;
        readonly Label knownIssuesText;
        readonly string[] knownIssueLines;

        public SkillTipsDetailsView(SkillTipsDisplaySnapshot display)
        {
            CanFocus = true;
            TabStop = TabBehavior.TabStop;
            SetScheme(SkillTipsPalette.BaseScheme);

            summary = new(display)
            {
                Id = "skill-tips-summary"
            };
            knownIssueLines = [.. display.KnownIssues.Skip(1)];
            knownIssues = new()
            {
                Id = "skill-tips-known-issues",
                Title = display.KnownIssues[0],
                BorderStyle = LineStyle.Single,
                CanFocus = false,
                TabStop = TabBehavior.NoStop
            };
            knownIssues.SetScheme(SkillTipsPalette.BaseScheme);
            knownIssues.Border.GetOrCreateView().SetScheme(SkillTipsPalette.WarningScheme);
            knownIssuesText = new()
            {
                X = 1,
                Y = 0,
                CanFocus = false,
                HotKeySpecifier = new Rune(0)
            };
            knownIssuesText.SetScheme(SkillTipsPalette.BaseScheme);
            knownIssues.Add(knownIssuesText);

            Add(summary, knownIssues);

            PreferredWidth = Math.Max(
                summary.PreferredWidth,
                display.KnownIssues[0].GetColumns() + 4);
            MinimumAccessibleWidth = new[]
            {
                I18N_ScoreSummary,
                I18N_PredictedRank,
                display.KnownIssues[0]
            }.Max(x => x.GetColumns()) + 4;
        }

        public int PreferredWidth { get; }
        public int MinimumAccessibleWidth { get; }

        public int GetMinimumHeight(int width)
            => summary.GetRequiredHeight(width)
                + Gap
                + GetKnownIssuesHeight(width);

        protected override void OnSubViewLayout(LayoutEventArgs args)
        {
            var width = Math.Max(1, Viewport.Width);
            var y = 0;
            var summaryHeight = summary.GetRequiredHeight(width);
            summary.Frame = new(0, y, width, summaryHeight);
            y += summaryHeight + Gap;

            var knownIssuesHeight = GetKnownIssuesHeight(width);
            knownIssues.Frame = new(0, y, width, knownIssuesHeight);
            LayoutKnownIssues(width, knownIssuesHeight);
            base.OnSubViewLayout(args);
        }

        int GetKnownIssuesHeight(int outerWidth)
            => WrapLines(knownIssueLines, Math.Max(1, outerWidth - 4)).Length + 2;

        void LayoutKnownIssues(int outerWidth, int outerHeight)
        {
            var wrapWidth = Math.Max(1, outerWidth - 4);
            var wrapped = WrapLines(knownIssueLines, wrapWidth);
            knownIssuesText.Frame = new(1, 0, wrapWidth, Math.Max(0, outerHeight - 2));
            knownIssuesText.Text = string.Join(Environment.NewLine, wrapped);
        }
    }

    sealed class SkillTipsSummaryView : FrameView
    {
        const int MinimumRankImageRows = 7;
        const int FrameBorderRows = 2;
        const int ProgressBarRows = 1;
        readonly Label breakdown;
        readonly FrameView highlight;
        readonly RankImageView predictedRank;
        readonly Label predictedTotal;
        readonly Label nextRankDistance;
        readonly ProgressBar rankProgress;
        readonly string[] breakdownLines;
        readonly string rankText;
        readonly string totalText;
        readonly string distanceText;
        readonly int breakdownPreferredWidth;
        readonly int predictionMinimumWidth;

        public SkillTipsSummaryView(SkillTipsDisplaySnapshot display)
        {
            Title = I18N_ScoreSummary;
            BorderStyle = LineStyle.Single;
            CanFocus = false;
            TabStop = TabBehavior.NoStop;
            SetScheme(SkillTipsPalette.BaseScheme);

            breakdownLines =
            [
                $"{I18N_Speed}: {display.Speed}",
                $"{I18N_Stamina}: {display.Stamina}",
                $"{I18N_Power}: {display.Power}",
                $"{I18N_Guts}: {display.Guts}",
                $"{I18N_Wisdom}: {display.Wisdom}",
                string.Empty,
                $"{I18N_TotalSkillPoints}: {display.TotalSkillPoints}",
                $"{I18N_UsedSkillPoints}: {display.UsedSkillPoints}",
                $"{I18N_RemainingSkillPoints}: {display.RemainingSkillPoints}",
                string.Empty,
                $"{I18N_LearnedSkillScore}: {display.LearnedSkillScore}",
                $"{I18N_RecommendedSkillScore}: {display.RecommendedSkillScore}",
                $"{I18N_StatusScore}: {display.StatusScore}"
            ];
            rankText = display.CurrentRank;
            totalText = $"{I18N_PredictedTotalScore}: {display.PredictedTotalScore}";
            distanceText = string.Format(
                I18N_ScoreToNextGrade,
                display.NextRank,
                display.ScoreToNextRank);
            breakdownPreferredWidth = breakdownLines
                .Where(x => x.Length != 0)
                .Select(x => x.GetColumns())
                .DefaultIfEmpty(1)
                .Max();
            predictionMinimumWidth = Math.Max(
                I18N_PredictedRank.GetColumns() + 2,
                new[] { rankText, totalText, distanceText }
                    .Max(x => x.GetColumns()) + 2);
            PreferredWidth = breakdownPreferredWidth + 1 + predictionMinimumWidth + 2;

            breakdown = new()
            {
                Id = "skill-tips-summary-breakdown",
                CanFocus = false,
                HotKeySpecifier = new Rune(0)
            };
            breakdown.SetScheme(SkillTipsPalette.BaseScheme);
            highlight = new()
            {
                Id = "skill-tips-summary-highlight",
                Title = I18N_PredictedRank,
                BorderStyle = LineStyle.Single,
                CanFocus = false,
                TabStop = TabBehavior.NoStop
            };
            highlight.SetScheme(SkillTipsPalette.BaseScheme);
            highlight.Border.GetOrCreateView().SetScheme(SkillTipsPalette.FocusBorderScheme);
            predictedRank = new(rankText)
            {
                Id = "skill-tips-predicted-rank"
            };
            predictedTotal = CreateHighlightLabel(
                "skill-tips-predicted-total",
                SkillTipsPalette.BaseScheme,
                Alignment.Center);
            nextRankDistance = CreateHighlightLabel(
                "skill-tips-next-rank-distance",
                SkillTipsPalette.MutedScheme,
                Alignment.Center);
            rankProgress = new()
            {
                Id = "skill-tips-rank-progress",
                Fraction = (float)display.RankProgress,
                ProgressBarFormat = ProgressBarFormat.Simple,
                ProgressBarStyle = ProgressBarStyle.Continuous,
                CanFocus = false,
                TabStop = TabBehavior.NoStop
            };
            rankProgress.SetScheme(SkillTipsPalette.RankProgressScheme);
            highlight.Add(predictedRank, predictedTotal, nextRankDistance, rankProgress);
            Add(breakdown, highlight);
        }

        public int PreferredWidth { get; }

        public int GetRequiredHeight(int outerWidth)
            => BuildLayout(Math.Max(1, outerWidth - 2)).Height + 2;

        protected override void OnSubViewLayout(LayoutEventArgs args)
        {
            var layout = BuildLayout(Math.Max(1, Viewport.Width));
            breakdown.Frame = new(0, 0, layout.BreakdownWidth, layout.BreakdownLines.Length);
            breakdown.Text = string.Join(Environment.NewLine, layout.BreakdownLines);
            highlight.Frame = new(
                layout.HighlightX,
                layout.HighlightY,
                layout.HighlightWidth,
                layout.HighlightHeight);
            LayoutHighlight(layout.HighlightWidth, layout.HighlightHeight);
            base.OnSubViewLayout(args);
        }

        (
            string[] BreakdownLines,
            int BreakdownWidth,
            int HighlightX,
            int HighlightY,
            int HighlightWidth,
            int HighlightHeight,
            int Height)
            BuildLayout(int contentWidth)
        {
            var sideBySide =
                contentWidth >= breakdownPreferredWidth + 1 + predictionMinimumWidth;
            var breakdownWidth = sideBySide
                ? breakdownPreferredWidth
                : contentWidth;
            var highlightX = sideBySide ? breakdownWidth + 1 : 0;
            var highlightWidth = sideBySide
                ? contentWidth - highlightX
                : contentWidth;
            var breakdownWrapped = WrapLines(breakdownLines, breakdownWidth);
            var predictionMinimumHeight = GetPredictionMinimumHeight(highlightWidth);
            var highlightHeight = sideBySide
                ? Math.Max(breakdownWrapped.Length, predictionMinimumHeight)
                : predictionMinimumHeight;
            var highlightY = sideBySide ? 0 : breakdownWrapped.Length + 1;
            var height = sideBySide
                ? highlightHeight
                : highlightY + highlightHeight;
            return (
                breakdownWrapped,
                breakdownWidth,
                highlightX,
                highlightY,
                highlightWidth,
                highlightHeight,
                height);
        }

        int GetPredictionMinimumHeight(int outerWidth)
        {
            var innerWidth = Math.Max(1, outerWidth - 2);
            return MinimumRankImageRows
                + WrapLines([totalText], innerWidth).Length
                + WrapLines([distanceText], innerWidth).Length
                + ProgressBarRows
                + FrameBorderRows;
        }

        void LayoutHighlight(int outerWidth, int outerHeight)
        {
            var innerWidth = Math.Max(1, outerWidth - FrameBorderRows);
            var totalLines = WrapLines([totalText], innerWidth);
            var distanceLines = WrapLines([distanceText], innerWidth);
            var rankImageRows = Math.Max(
                MinimumRankImageRows,
                outerHeight
                    - FrameBorderRows
                    - totalLines.Length
                    - distanceLines.Length
                    - ProgressBarRows);
            var y = 0;
            predictedRank.Frame = new(0, y, innerWidth, rankImageRows);
            y += rankImageRows;
            predictedTotal.Frame = new(0, y, innerWidth, totalLines.Length);
            predictedTotal.Text = string.Join(Environment.NewLine, totalLines);
            y += totalLines.Length;
            nextRankDistance.Frame = new(0, y, innerWidth, distanceLines.Length);
            nextRankDistance.Text = string.Join(Environment.NewLine, distanceLines);
            y += distanceLines.Length;
            rankProgress.Frame = new(0, y, innerWidth, ProgressBarRows);
        }

        static Label CreateHighlightLabel(string id, Scheme scheme, Alignment alignment)
        {
            var label = new Label
            {
                Id = id,
                CanFocus = false,
                HotKeySpecifier = new Rune(0),
                TextAlignment = alignment
            };
            label.SetScheme(scheme);
            return label;
        }
    }

    sealed class RankImageView : ImageView
    {
        readonly string rank;
        readonly Label rankLabel;
        (string Rank, int PixelWidth, int PixelHeight)? renderedImageKey;

        public RankImageView(string rank)
        {
            this.rank = rank;
            UseRasterGraphics = true;
            UseBackgroundRendering = false;
            CanFocus = false;
            TabStop = TabBehavior.NoStop;
            rankLabel = new()
            {
                Text = rank,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                TextAlignment = Alignment.Center,
                VerticalTextAlignment = Alignment.Center,
                CanFocus = false
            };
            rankLabel.SetScheme(SkillTipsPalette.RankProgressScheme);
            Add(rankLabel);
        }

        protected override bool OnDrawingContent(DrawContext? context)
        {
            rankLabel.Visible = !OperatingSystem.IsWindows() || !IsUsingRasterGraphics;
            if (rankLabel.Visible)
            {
                if (Image is not null)
                {
                    Image = null;
                    renderedImageKey = null;
                }
                return true;
            }

            EnsureRankImage();
            return base.OnDrawingContent(context);
        }

        void EnsureRankImage()
        {
            var pixelSize = ViewportToScreenInPixels().Size;
            if (pixelSize.Width <= 0 || pixelSize.Height <= 0)
                return;

            var key = (rank, pixelSize.Width, pixelSize.Height);
            if (renderedImageKey == key)
                return;

            Image = RankImageRasterizer.Rasterize(
                rank,
                pixelSize.Width,
                pixelSize.Height,
                new TColor(StandardColor.BrightCyan),
                TColor.Black);
            renderedImageKey = key;
        }
    }

    static class SkillTipsPalette
    {
        static readonly TAttribute HostNormal = new(
            new TColor(StandardColor.White),
            TColor.Black);
        static readonly TAttribute Normal = new(
            HostNormal.Foreground,
            TColor.Black,
            HostNormal.Style);
        static readonly TAttribute Focus = new(
            TColor.Black,
            new TColor(StandardColor.BrightCyan),
            HostNormal.Style);
        static readonly TAttribute Cyan = new(
            new TColor(StandardColor.BrightCyan),
            TColor.Black,
            HostNormal.Style);
        static readonly TAttribute Amber = new(
            new TColor(StandardColor.BrightYellow),
            TColor.Black,
            HostNormal.Style);
        static readonly TAttribute Muted = new(
            new TColor(StandardColor.Gray),
            TColor.Black,
            HostNormal.Style);
        public static TAttribute Selected { get; } = new(new TColor(StandardColor.BrightGreen), TColor.Black);

        public static Scheme BaseScheme { get; } = new()
        {
            Normal = Normal,
            ReadOnly = Normal,
            Focus = Cyan
        };

        public static Scheme TableScheme { get; } = new()
        {
            Normal = Normal,
            ReadOnly = Normal,
            Focus = Focus,
            HotNormal = Cyan,
            HotFocus = Focus
        };

        public static Scheme FocusBorderScheme { get; } = new(Cyan);
        public static Scheme RankProgressScheme { get; } = new(Cyan);
        public static Scheme WarningScheme { get; } = new(Amber);
        public static Scheme MutedScheme { get; } = new(Muted);
    }
}
