using System.Windows;
using Velopack;

namespace DocVista.App;

public partial class App : Application
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnAfterInstallFastCallback(_ => FileAssociationService.RegisterOpenWith())
            .OnAfterUpdateFastCallback(_ => FileAssociationService.RegisterOpenWith())
            .OnBeforeUninstallFastCallback(_ => FileAssociationService.UnregisterOpenWith())
            .Run();
        var application = new App();
        application.InitializeComponent();
        application.Run();
    }
}
