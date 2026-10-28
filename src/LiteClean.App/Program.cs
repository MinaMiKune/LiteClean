using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace LiteClean.App;

internal static class Program
{
    [STAThread]
    [SuppressMessage(
        "Design",
        "CA1031",
        Justification = "Top-level GUI guard must report any unexpected failure without crashing silently.")]
    private static int Main(string[] args)
    {
        var smokeTest = args.Any(
            static arg => string.Equals(arg, "--smoke-test", StringComparison.OrdinalIgnoreCase));

        try
        {
            ApplicationConfiguration.Initialize();

            if (smokeTest)
            {
                using var form = new MainForm();
                _ = form.Handle; // Force native control creation without entering a message loop.
                return 0;
            }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, eventArgs) =>
                ShowFatalError(eventArgs.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
                ShowFatalError(eventArgs.ExceptionObject as Exception);

            using var mainForm = new MainForm();
            Application.Run(mainForm);
            return 0;
        }
        catch (Exception ex)
        {
            if (!smokeTest)
            {
                ShowFatalError(ex);
            }

            return 1;
        }
    }

    private static void ShowFatalError(Exception? exception)
    {
        var detail = exception is null ? "未知错误。" : $"{exception.GetType().Name}: {exception.Message}";
        try
        {
            MessageBox.Show(
                $"LiteClean 遇到未处理的错误。\n\n{detail}\n\n不会因此扩大清理范围；请重新启动后重试。",
                "LiteClean",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The UI may already be shutting down. The process exit code still reports failure.
            Debug.WriteLine(ex);
        }
    }
}
