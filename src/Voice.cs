using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hive;

// Dictation in the window's work pane, local and offline: pw-record
// (PipeWire) records the microphone to a 16 kHz mono wav, and whisper-cli
// (whisper.cpp) turns it into text with a ggml model. Both are programs on
// PATH, so nothing is linked into hive, and neither runs between dictations.
// The model is the first ggml-*.bin in ~/.local/share/hive/whisper
// ($XDG_DATA_HOME/hive/whisper).
static partial class Voice
{
    public static string ModelDir => Path.Combine(Install.Data, "hive", "whisper");

    public static string? Model() =>
        Directory.Exists(ModelDir) ? Directory.EnumerateFiles(ModelDir, "ggml-*.bin").Order().FirstOrDefault() : null;

    // What dictation still needs, said as how to get it; "" when it can run.
    public static string Missing()
    {
        var missing = new List<string>();
        if (!OnPath("pw-record")) missing.Add("pw-record (in PipeWire's tools, pipewire-audio or pipewire-utils)");
        if (!OnPath("whisper-cli")) missing.Add("whisper-cli (whisper.cpp)");
        if (Model() == null) missing.Add($"a ggml model in {Text.Tilde(ModelDir)} (ggml-base.en.bin, say, from huggingface.co/ggerganov/whisper.cpp)");
        return missing.Count == 0 ? "" : "Voice dictation needs " + string.Join(", ", missing) + ".";
    }

    static bool OnPath(string program)
    {
        try { Text.Which(program); return true; }
        catch (HiveException) { return false; }
    }

    // Where a recording goes: the user's runtime folder (a tmpfs), else /tmp.
    public static string WavPath() =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run && Path.IsPathRooted(run) && Directory.Exists(run) ? run : Path.GetTempPath(),
                     $"hive-dictation-{Environment.ProcessId}.wav");

    // Starts recording into wav; Stop ends it.
    public static Process Record(string wav)
    {
        File.Delete(wav);
        var info = new ProcessStartInfo(Text.Which("pw-record")) { UseShellExecute = false };
        foreach (var a in new[] { "--rate", "16000", "--channels", "1", "--format", "s16", wav }) info.ArgumentList.Add(a);
        var recording = Process.Start(info) ?? throw new HiveException("Couldn't start pw-record.");
        Watch(recording.Id);
        return recording;
    }

    // If hive dies without Stop (a crash, kill -9), pw-record would record on.
    // A watcher shell reads a pipe only hive holds: when hive dies the pipe
    // ends and the watcher stops pw-record (SIGINT); Stop first writes it a
    // line, and it exits with nothing to do.
    static readonly Dictionary<int, Process> watchers = [];

    static void Watch(int pid)
    {
        var info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardInput = true };
        foreach (var a in new[] { "-c", "read _ || kill -INT \"$0\"", pid.ToString() }) info.ArgumentList.Add(a);
        try { if (Process.Start(info) is { } w) lock (watchers) watchers[pid] = w; }
        catch (System.ComponentModel.Win32Exception) { } // no /bin/sh: dictation still works
    }

    static void Unwatch(int pid)
    {
        Process? w;
        lock (watchers) watchers.Remove(pid, out w);
        if (w == null) return;
        try { w.StandardInput.WriteLine("stopped"); w.StandardInput.Close(); w.WaitForExit(1000); }
        catch (IOException) { }
        w.Dispose();
    }

    [LibraryImport("libc.so.6", EntryPoint = "kill")] private static partial int Kill(int pid, int signal);

    // Ends a recording: SIGINT, so pw-record writes the wav's header before it
    // exits (killed outright, the file would say it holds nothing).
    public static void Stop(Process recording)
    {
        Unwatch(recording.Id);
        if (!recording.HasExited) Kill(recording.Id, 2);
        if (!recording.WaitForExit(3000)) recording.Kill();
        recording.Dispose();
    }

    // The text whisper-cli hears in the wav, which is deleted after.
    public static string Transcribe(string wav)
    {
        try
        {
            if (!File.Exists(wav) || new FileInfo(wav).Length <= 44) throw new HiveException("Nothing was recorded; is a microphone connected?");
            var model = Model() ?? throw new HiveException(Missing());
            var info = new ProcessStartInfo(Text.Which("whisper-cli"))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // -nt: no timestamps, -np: only the text; the language is detected.
            foreach (var a in new[] { "-m", model, "-f", wav, "-nt", "-np", "-l", "auto" }) info.ArgumentList.Add(a);
            using var whisper = Process.Start(info) ?? throw new HiveException("Couldn't start whisper-cli.");
            var errors = whisper.StandardError.ReadToEndAsync();
            var heard = whisper.StandardOutput.ReadToEnd();
            whisper.WaitForExit();
            if (whisper.ExitCode != 0)
                throw new HiveException("whisper-cli failed: " + (errors.Result.Trim().Split('\n')[^1] is { Length: > 0 } last ? last : $"exit code {whisper.ExitCode}"));
            return Clean(heard);
        }
        finally { File.Delete(wav); }
    }

    // whisper-cli's lines as one: without the markers it writes for what
    // isn't speech ([BLANK_AUDIO], [Music], (silence)…).
    public static string Clean(string output)
    {
        var words = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '[' && line[^1] is ']' || line[0] is '(' && line[^1] is ')') continue;
            words.Add(line);
        }
        return string.Join(' ', words);
    }
}
