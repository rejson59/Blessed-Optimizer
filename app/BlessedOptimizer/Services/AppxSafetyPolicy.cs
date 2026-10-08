namespace BlessedOptimizer.Services;

public sealed record AppxSafetyAssessment(bool IsProtected, string Category, string Explanation);

/// <summary>
/// Identifies shared Windows app packages that should never be offered in the user-facing
/// cleanup list. The package's own protection metadata is authoritative; the name checks
/// provide a conservative fallback for common frameworks and shell components.
/// </summary>
public static class AppxSafetyPolicy
{
    private static readonly string[] ProtectedNamePrefixes =
    {
        "Microsoft.VCLibs.",
        "Microsoft.NET.Native.",
        "Microsoft.UI.Xaml.",
        "Microsoft.WindowsAppRuntime."
    };

    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.WindowsStore",
        "Microsoft.StorePurchaseApp",
        "Microsoft.DesktopAppInstaller",
        "Microsoft.AAD.BrokerPlugin",
        "Microsoft.AccountsControl",
        "Microsoft.SecHealthUI",
        "Microsoft.Windows.Search",
        "Microsoft.Windows.ShellExperienceHost",
        "Microsoft.Windows.StartMenuExperienceHost"
    };

    public static AppxSafetyAssessment Assess(AppxPackage package)
    {
        if (package.NonRemovable || package.IsFramework || package.IsResourcePackage)
            return Protected("Wspólny składnik Windows", "Windows oznacza ten pakiet jako chroniony lub współdzielony. Blessed nie pozwoli go odinstalować.");

        if (ProtectedNames.Contains(package.Name) || ProtectedNamePrefixes.Any(prefix => package.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return Protected("Chroniony składnik", "To pakiet wymagany przez Sklep, instalator lub składniki Windows. Blessed zostawia go na miejscu.");

        return new AppxSafetyAssessment(
            IsProtected: false,
            Category: "Aplikacja konta",
            Explanation: "Pakiet jest przypisany do bieżącego konta. Blessed nie ocenia, czy go potrzebujesz — zdecyduj na podstawie funkcji aplikacji i wydawcy.");
    }

    private static AppxSafetyAssessment Protected(string category, string explanation) =>
        new(true, category, explanation);
}
