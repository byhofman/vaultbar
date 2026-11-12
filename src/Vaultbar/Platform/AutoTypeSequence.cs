using System.Text.RegularExpressions;

namespace Vaultbar.Platform;

public static partial class AutoTypeSequence
{
    public static List<TypeStep> Build(string template, string? username, string? password, Func<string?> totp)
    {
        var steps = new List<TypeStep>();
        var pos = 0;
        var skipNextTab = false;
        foreach (Match m in Token().Matches(template))
        {
            if (m.Index > pos) steps.Add(new TypeText(template[pos..m.Index]));
            pos = m.Index + m.Length;

            var name = m.Groups[1].Value.ToUpperInvariant();
            if (skipNextTab && name != "TAB") skipNextTab = false;
            switch (name)
            {
                case "USERNAME":
                    if (string.IsNullOrEmpty(username)) skipNextTab = true;
                    else steps.Add(new TypeText(username));
                    break;
                case "PASSWORD":
                    if (!string.IsNullOrEmpty(password)) steps.Add(new TypeText(password));
                    break;
                case "TOTP":
                    if (totp() is { Length: > 0 } code) steps.Add(new TypeText(code));
                    break;
                case "TAB":
                    if (skipNextTab) skipNextTab = false;
                    else steps.Add(new TypeKey(SpecialKey.Tab));
                    break;
                case "ENTER":
                    steps.Add(new TypeKey(SpecialKey.Enter));
                    break;
                case "DELAY":
                    steps.Add(new TypeDelay(int.TryParse(m.Groups[2].Value, out var ms) ? Math.Clamp(ms, 0, 10000) : 250));
                    break;
            }
        }
        if (pos < template.Length) steps.Add(new TypeText(template[pos..]));
        return steps;
    }

    [GeneratedRegex(@"\{(USERNAME|PASSWORD|TOTP|TAB|ENTER|DELAY)(?::(\d+))?\}", RegexOptions.IgnoreCase)]
    private static partial Regex Token();
}
