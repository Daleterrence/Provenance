using System.Text;

namespace Provenance.Common.Otp;

internal static class CommandLine
{
    internal static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    internal static string Quote(string arg)
    {
        var quoted = new StringBuilder("\"");
        for (var i = 0; i < arg.Length; i++)
        {
            var backslashes = 0;
            while (i < arg.Length && arg[i] == '\\')
            {
                backslashes++;
                i++;
            }
            if (i == arg.Length) quoted.Append('\\', backslashes * 2);
            else if (arg[i] == '"') quoted.Append('\\', backslashes * 2 + 1).Append('"');
            else quoted.Append('\\', backslashes).Append(arg[i]);
        }
        return quoted.Append('"').ToString();
    }
}
