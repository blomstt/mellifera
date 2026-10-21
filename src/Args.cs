namespace Hive;

// The command line: words in order, and --key value (or --key=value) options.
// An option can repeat; a flag takes no value. --help anywhere, or -h first,
// asks for the help.
static class Args
{
    static readonly HashSet<string> Flags = ["all", "hook", "help"];

    public static (List<string> Pos, Dictionary<string, List<string>> Opt) Parse(string[] argv)
    {
        var pos = new List<string>();
        var opt = new Dictionary<string, List<string>>();
        for (int i = 0; i < argv.Length; i++)
        {
            if (!argv[i].StartsWith("--") || argv[i].Length == 2) { pos.Add(argv[i]); continue; }
            var key = argv[i][2..];
            string value = "";
            var eq = key.IndexOf('=');
            if (eq > 0) { value = key[(eq + 1)..]; key = key[..eq]; }
            else if (!Flags.Contains(key))
                value = i + 1 < argv.Length ? argv[++i] : throw new HiveException($"--{key} needs a value.");
            if (!opt.TryGetValue(key, out var list)) opt[key] = list = [];
            list.Add(value);
        }
        return (pos, opt);
    }

    // The command to run: the first word, or help.
    public static string Command(List<string> pos, Dictionary<string, List<string>> opt) =>
        pos.Count == 0 || pos[0] == "-h" || opt.ContainsKey("help") ? "help" : pos[0];
}
