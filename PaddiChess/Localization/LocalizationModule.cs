using System.Reflection;
using System.Runtime.CompilerServices;

namespace PaddiXiangqi.Localization;

/// <summary>
/// Activates the display language before Program.Main runs, so no upstream file needs a call.
/// Only the real application entry point is localized: test hosts and the designer load this
/// assembly too and keep the Chinese source text that their assertions expect.
/// </summary>
internal static class LocalizationModule
{
#pragma warning disable CA2255 // The initializer belongs to the application executable itself.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        if (Assembly.GetEntryAssembly() != typeof(LocalizationModule).Assembly) return;
        try
        {
            if (L10n.Activate(L10n.RequestedLanguage(Environment.GetCommandLineArgs().Skip(1).ToArray())))
                UiTextHooks.Install();
        }
        catch (Exception error)
        {
            // A broken catalog must never prevent the application from starting; fall back to Chinese.
            L10n.Use(null, L10n.SourceLanguage);
            System.Diagnostics.Trace.TraceError($"Localization disabled: {error}");
        }
    }
}
