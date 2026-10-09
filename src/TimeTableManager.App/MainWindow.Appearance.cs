using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TimeTableManager.App;

public partial class MainWindow
{
    private bool? toolbarStacked;
    private void ToolbarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (WorkflowGuide is null || ToolbarLeft is null || e.NewSize.Width <= 0) return;
        bool stacked = e.NewSize.Width < 1200;
        if (toolbarStacked == stacked) return;
        toolbarStacked = stacked;
        ToolbarLeft.Width = new GridLength(stacked ? 1 : 3, GridUnitType.Star);
        ToolbarRight.Width = stacked ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        Grid.SetRow(WorkflowGuide, stacked ? 2 : 0); Grid.SetColumn(WorkflowGuide, stacked ? 0 : 1);
        Grid.SetRowSpan(WorkflowGuide, stacked ? 1 : 2); Grid.SetColumnSpan(WorkflowGuide, stacked ? 2 : 1);
        WorkflowGuide.MinHeight = stacked ? 44 : 66;
        WorkflowGuide.Margin = new Thickness(0, stacked ? 2 : 0, 0, 0);
    }
    private Brush ThemeBrush(string name) => (Brush)FindResource(name);
    private void UpdateActionColors(bool fixedTargetReady)
    {
        // No per-cell effects, timers or animations. Color reflects the next available action.
        var modeTint = workMode switch
        {
            WorkMode.Swap => "AquaMint", WorkMode.Cover => "AquaCyan",
            WorkMode.Manual => "AquaLavender", _ => "AquaBlue"
        };
        TargetSurface.Background = ThemeBrush(modeTint);
        WorkflowGuide.BorderBrush = ThemeBrush(modeTint + "Strong");
        ViewMode.Background = ThemeBrush(workMode == WorkMode.Browse ? "AquaBlueStrong" : "AquaLavender");
        SearchBox.Background = ThemeBrush(workMode == WorkMode.Browse ? "AquaBlue" : "AquaGlass");
        AutoButton.Background = ThemeBrush(fixedTargetReady ? "AquaBlueStrong" : "AquaNeutral");
        AutoButton.Foreground = ThemeBrush("AquaInk");
        AutoButton.BorderBrush = ThemeBrush(fixedTargetReady ? "AquaFocus" : "AquaEdge");
        AutoButton.FontWeight = fixedTargetReady ? FontWeights.Bold : FontWeights.SemiBold;
        bool canApply = !busy && ModeAllows(vm.Pending) && vm.CanApply;
        ApplyButton.Background = ThemeBrush(canApply ? "AquaMintStrong" : "AquaNeutral");
        ApplyButton.FontWeight = canApply ? FontWeights.Bold : FontWeights.SemiBold;
        PreviewButton.Background = ThemeBrush(!busy && vm.Pending is not null ? "AquaLavenderStrong" : "AquaNeutral");
        LockRangeButton.Background = ThemeBrush(lockSelectionMode ? "AquaBlueStrong" : "AquaNeutral");
        DraftUndoButton.Background = ThemeBrush(!busy && manualDraft?.CanUndo == true ? "AquaLavender" : "AquaNeutral");
    }
}
