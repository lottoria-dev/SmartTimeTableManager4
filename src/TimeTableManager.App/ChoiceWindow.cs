using System.Windows;
using System.Windows.Controls;

namespace TimeTableManager.App;

public sealed class ChoiceWindow : Window
{
    private readonly ListBox list;
    public Option? Choice => list.SelectedItem as Option;
    public ChoiceWindow(string title, string instruction, IEnumerable<Option> choices, string accept = "선택")
    {
        Style = (Style)FindResource(typeof(Window));
        Title=title;Width=550;MinWidth=400;SizeToContent=SizeToContent.Height;
        MaxHeight=Math.Max(320,Math.Min(600,SystemParameters.WorkArea.Height-80));WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var items=choices.ToArray();
        var panel=new StackPanel{Margin=new Thickness(16)};
        var hint=new TextBlock{Text=instruction,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};
        panel.Children.Add(hint);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var ok=new Button{Content=accept};var cancel=new Button{Content="취소",IsCancel=true,IsDefault=true};
        actions.Children.Add(ok);actions.Children.Add(cancel);
        list=new ListBox{ItemsSource=items,DisplayMemberPath="Label",MaxHeight=280,MinHeight=0,Margin=new Thickness(0,0,0,10)};panel.Children.Add(list);panel.Children.Add(actions);
        ok.Click+=(_,_)=>{if(Choice is not null)DialogResult=true;};
        list.MouseDoubleClick+=(_,_)=>{if(Choice is not null)DialogResult=true;};Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    }
}
