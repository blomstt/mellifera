using System.Diagnostics;
using static Hive.Gui.Ui;

namespace Hive.Gui;

// Dictation into the Queen's prompt (src/Voice.cs does the recording and the
// transcribing): the mic beside the ❯ prompt, or alt+v, starts recording;
// a second press stops it, and so does letting go of alt+v after holding it.
// While it records, "● 0:07" counts beside the mic; then "transcribing…",
// off the UI thread, and what was heard goes into the prompt at the cursor,
// to edit and send yourself. Without pw-record, whisper-cli or a model the
// mic is hidden, and the ❯ says what to install.
sealed unsafe partial class App
{
    nint talkMic, talkVoice;
    string voiceMissing = "";      // what dictation needs ("" when it can run)
    Process? recording;            // pw-record, only while recording
    string recordingWav = "";
    DateTime recordingSince;
    bool voiceKeyUp;               // alt+v was let go since recording started
    volatile bool transcribing;    // whisper-cli runs on its own thread
    volatile string? heard, voiceError;
    Action voiceTick = null!;

    const double HoldToTalk = 0.5; // held this long, letting go of alt+v stops it

    // The mic and its "● 0:07", for the prompt's row.
    (nint Mic, nint Voice) BuildDictation(nint glyph)
    {
        voiceTick = VoiceTick;
        talkVoice = Css(N.gtk_label_new(""), "small", "honey");
        N.gtk_widget_set_visible(talkVoice, false);
        talkMic = Css(TextButton(Icons.Mic, ToggleVoice), "talk-mic");
        Said(talkMic, "Dictate");
        N.gtk_widget_set_tooltip_text(talkMic, "Dictate (alt+v; hold it to talk, or press again to stop). What you say goes into the prompt, to edit and send.");
        N.gtk_widget_set_valign(talkMic, Center);
        voiceMissing = Voice.Missing();
        ShowMic(glyph);
        return (talkMic, talkVoice);
    }

    nint talkGlyph;

    void ShowMic(nint glyph)
    {
        talkGlyph = glyph;
        N.gtk_widget_set_visible(talkMic, voiceMissing.Length == 0);
        N.gtk_widget_set_tooltip_text(glyph, voiceMissing.Length == 0 ? null : voiceMissing + " Then alt+v dictates.");
    }

    // alt+v pressed in the prompt (a held key repeats: only the first counts).
    bool VoiceKey(uint key, uint state)
    {
        if (key is not (0x76 or 0x56) || (state & 8) == 0) return false; // v, with alt
        if (recording != null && !voiceKeyUp) return true;               // the key held down
        ToggleVoice();
        return true;
    }

    // alt+v let go: after holding it, that ends the recording.
    void VoiceKeyUp(uint key, uint state)
    {
        if (key is not (0x76 or 0x56) || recording == null || voiceKeyUp) return;
        voiceKeyUp = true;
        if ((DateTime.Now - recordingSince).TotalSeconds >= HoldToTalk) ToggleVoice();
    }

    void ToggleVoice()
    {
        if (transcribing) return;
        if (recording != null) { StopVoice(transcribe: true); return; }
        voiceMissing = Voice.Missing(); // installed since the window opened, perhaps
        ShowMic(talkGlyph);
        if (voiceMissing.Length > 0) { status.Say(voiceMissing, error: true); return; }
        recordingWav = Voice.WavPath();
        recording = Voice.Record(recordingWav);
        recordingSince = DateTime.Now;
        voiceKeyUp = false;
        N.gtk_widget_add_css_class(talkMic, "on");
        Said(talkMic, "Stop dictating");
        N.gtk_widget_set_visible(talkVoice, true);
        VoiceTick();
    }

    // Ends a recording; with transcribe, what it heard goes into the prompt.
    // The window closing ends one too (App.Run, without the widgets), so
    // pw-record never outlives it.
    void StopVoice(bool transcribe)
    {
        if (recording is not { } r) return;
        recording = null;
        N.gtk_widget_remove_css_class(talkMic, "on");
        Said(talkMic, "Dictate");
        if (!transcribe)
        {
            Voice.Stop(r);
            File.Delete(recordingWav);
            return;
        }
        transcribing = true;
        (heard, voiceError) = (null, null);
        var wav = recordingWav;
        new Thread(() =>
        {
            try
            {
                Voice.Stop(r);
                heard = Voice.Transcribe(wav);
            }
            catch (Exception e) { voiceError = e.Message; }
            transcribing = false;
        }) { IsBackground = true }.Start();
        VoiceTick();
    }

    // Four times a second while recording or transcribing, and not otherwise.
    void VoiceTick()
    {
        if (recording != null)
        {
            var took = DateTime.Now - recordingSince;
            N.gtk_label_set_text(talkVoice, $"● {(int)took.TotalMinutes}:{took.Seconds:00}");
            After(250, voiceTick);
            return;
        }
        if (transcribing)
        {
            N.gtk_label_set_text(talkVoice, "transcribing…");
            After(250, voiceTick);
            return;
        }
        N.gtk_widget_set_visible(talkVoice, false);
        if (voiceError is { } error) status.Say(error, error: true);
        else if (heard is { Length: > 0 } text) InsertHeard(text);
        else if (heard != null) Say("Nothing was heard.");
        (heard, voiceError) = (null, null);
    }

    // At the prompt's cursor, a space apart from what's there. GTK counts the
    // cursor in characters; i is where that is in the string.
    void InsertHeard(string text)
    {
        var at = N.gtk_editable_get_position(talkEntry);
        var before = Text(talkEntry);
        int i = 0, n = 0;
        for (; i < before.Length && n < at; n++) i += char.IsHighSurrogate(before[i]) && i + 1 < before.Length ? 2 : 1;
        at = n;
        if (i > 0 && !char.IsWhiteSpace(before[i - 1])) text = " " + text;
        if (i < before.Length && !char.IsWhiteSpace(before[i])) text += " ";
        N.gtk_editable_insert_text(talkEntry, text, -1, &at);
        N.gtk_entry_grab_focus_without_selecting(talkEntry); // grab_focus would select it all, for the next key to replace
        N.gtk_editable_set_position(talkEntry, at);
    }
}
