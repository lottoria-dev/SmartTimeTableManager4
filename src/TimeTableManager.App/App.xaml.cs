using System.Globalization;
namespace TimeTableManager.App;
public partial class App : System.Windows.Application
{
    public App()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("ko-KR");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("ko-KR");
    }
}
