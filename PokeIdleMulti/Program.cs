using Microsoft.Web.WebView2.Core;

namespace PokeIdleMulti;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        ToolStripManager.Renderer = Theme.Renderer;
        // O que derruba o deck, ou a tarefa que falhou sem ninguém ver, vai para o diagnostico\deck.log.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Diag.Write("ERRO que fechou o deck: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => Diag.Write("erro numa tarefa: " + e.Exception.GetBaseException());

        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Diag.Write("sem o WebView2 Runtime instalado");
            DeckBox.Show(null,
                "Falta o WebView2 Runtime da Microsoft (o motor de navegador que o deck usa).\n\n" +
                "Baixe em https://go.microsoft.com/fwlink/p/?LinkId=2124703 e abra o deck de novo.",
                DeckForm.AppName, DeckBoxIcon.Error);
            return;
        }

        // Dois decks abertos com as mesmas contas ficam se derrubando ("Account opened elsewhere").
        using var single = new Mutex(true, "DeckPokeIdle", out bool first);
        if (!first)
        {
            DeckBox.Show(null, $"O {DeckForm.AppName} já está aberto.", DeckForm.AppName);
            return;
        }

        Application.Run(new DeckForm());
    }
}
