using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TimeTableManager.App;

public sealed class BoardCell : ObservableObject
{
    public required string RowId { get; init; }
    public required string SlotId { get; init; }
    public string? LessonId { get; set; }
    public string? TeacherId { get; set; }
    private bool locked;
    public bool Locked { get => locked; set => SetProperty(ref locked, value); }
    private bool covered;
    public bool Covered { get => covered; set => SetProperty(ref covered, value); }
    public bool IsPast { get; set; }
    public bool IsDeparture { get; set; }
    public bool HeldOrigin { get; set; }
    public bool Excluded { get; set; }
    public Brush DayBrush { get; init; } = Brushes.White;
    public required string SearchText { get; set; }
    private string text = "";
    public required string Text
    {
        get => text;
        set
        {
            if (!SetProperty(ref text, value)) return;
            var split = value.IndexOf('\n');
            PrimaryText = split < 0 ? value : value[..split];
            SecondaryText = split < 0 ? "" : value[(split + 1)..].Replace('\n', ' ');
            OnPropertyChanged(nameof(PrimaryText)); OnPropertyChanged(nameof(SecondaryText));
        }
    }
    public string PrimaryText { get; private set; } = "";
    public string SecondaryText { get; private set; } = "";
    private string hint = "";
    public required string Hint { get => hint; set => SetProperty(ref hint, value); }
    public bool TeacherRow { get; init; }
    public Brush BaseFill { get; set; } = Brushes.White;
    private Brush? fill;
    public Brush Fill { get => fill ?? BaseFill; set => SetProperty(ref fill, value); }
    private Brush foreground = Brushes.Black;
    public Brush Foreground { get => foreground; set => SetProperty(ref foreground, value); }
    private double opacity = 1;
    public double Opacity { get => opacity; set => SetProperty(ref opacity, value); }
    public bool SearchMatch { get; set; } = true;
    public void UpdateFrom(BoardCell next)
    {
        LessonId = next.LessonId; TeacherId = next.TeacherId; Locked = next.Locked; Covered = next.Covered;
        IsPast = next.IsPast; IsDeparture = next.IsDeparture; HeldOrigin = next.HeldOrigin; Excluded = next.Excluded; SearchText = next.SearchText;
        Text = next.Text; Hint = next.Hint; BaseFill = next.BaseFill;
    }
}
public sealed class BoardRow(string id, string name, List<BoardCell> cells) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public List<BoardCell> Cells { get; } = cells;
    private string marker = "";
    public string Marker
    {
        get => marker;
        set { if (SetProperty(ref marker, value)) OnPropertyChanged(nameof(DisplayName)); }
    }
    public string DisplayName => Marker.Length == 0 ? Name : Marker + " · " + Name;
}
