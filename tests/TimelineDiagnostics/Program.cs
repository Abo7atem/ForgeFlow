using ForgeFlow;

static class TimelineDiagnostics
{
    private static int _passed;

    public static void Main()
    {
        Run("C cuts V1+A1 under an unselected playhead", SplitWithoutSelection);
        Run("Blade cuts every unlocked track under the playhead", SplitAllTracks);
        Run("Delete ripples and closes the primary gap", RippleDelete);
        Run("Linked copy/paste performs a valid insert edit", LinkedCopyPaste);
        Run("Linked duplicate keeps picture and sound together", LinkedDuplicate);
        Run("Audio controls work on A3 and later tracks", LaterAudioTrackSpeed);
        Run("Track mute is included in the export model", TrackMute);
        Run("Model change notifications cover property edits and undo", ChangeNotifications);
        Run("SRT and VTT subtitles parse into timed cues", SubtitleParsing);
        Run("Overlay tracks are reused when clips do not overlap", OverlayTrackReuse);
        Run("Interactive overlay gesture is one Undo step", UndoGroup);
        Run("Undo and redo restore an exact edit", UndoRedo);
        Run("Preview advances across a ripple cut with a stale playhead", PlaybackCrossesRippleCut);
        Run("Preview completes exactly at the final edited frame", PlaybackCompletesAtEditedEnd);
        Run("Corrupt project JSON is rejected before it reaches preview", RejectCorruptProject);
        Run("Unlinked ripple delete only closes the selected track", UnlinkedRippleDelete);
        Run("Unlinked paste inserts only on the copied primary track", UnlinkedPaste);
        Console.WriteLine($"Timeline diagnostics passed: {_passed}/17");
    }

    private static void SubtitleParsing()
    {
        string root = Path.Combine(Path.GetTempPath(), "ForgeFlowSubtitleDiagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string srt = Path.Combine(root, "sample.srt");
            File.WriteAllText(srt, @"1
00:00:01,000 --> 00:00:03,500
Hello <i>world</i>!

2
00:00:04,000 --> 00:00:05,000
Second cue", System.Text.Encoding.UTF8);
            Require(SubtitleService.TryParseFile(srt, out var srtCues, out _), "SRT was not parsed");
            Require(srtCues.Count == 2 && Math.Abs(srtCues[0].Start - 1) < 0.001 && srtCues[0].Text == "Hello world!", "SRT cue data is wrong");
            string utf16 = Path.Combine(root, "utf16.srt");
            File.WriteAllText(utf16, "1\n00:00:00,500 --> 00:00:01,500\nترميز UTF16", System.Text.Encoding.Unicode);
            Require(SubtitleService.TryParseFile(utf16, out var utf16Cues, out _), "UTF-16 SRT was not parsed");
            Require(utf16Cues.Count == 1 && utf16Cues[0].Text.Contains("UTF16", StringComparison.Ordinal), "UTF-16 cue data is wrong");

            string vtt = Path.Combine(root, "sample.vtt");
            File.WriteAllText(vtt, @"WEBVTT

00:01.250 --> 00:03.000
VTT cue", System.Text.Encoding.UTF8);
            Require(SubtitleService.TryParseFile(vtt, out var vttCues, out _), "VTT was not parsed");
            Require(vttCues.Count == 1 && Math.Abs(vttCues[0].Start - 1.25) < 0.001, "VTT timestamp is wrong");

            Require(!SubtitleService.TryParseFile(Path.Combine(root, "bad.txt"), out _, out _), "unsupported subtitle extension was accepted");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private static void SplitWithoutSelection()
    {
        var model = NewModel();
        model.Select(null);
        Require(model.SplitAt(4), "split returned false");
        Require(model.Clips.Count(c => c.Track == "V1") == 2, "V1 was not split");
        Require(model.Clips.Count(c => c.Track == "A1") == 2, "A1 was not split with V1");
        Require(model.Clips.Where(c => c.Track is "V1" or "A1").All(c => c.End > c.Start), "zero-length clip created");
    }

    private static void SplitAllTracks()
    {
        var model = NewModel();
        EditTimelineModel.Clip image = model.AddImage("overlay.png", 0, 10);
        model.AddAudio("music.wav", 0, 10);
        int count = model.SplitAllAt(5, lockedTracks: new HashSet<string> { image.Track });
        Require(count == 3, "blade did not split all unlocked media tracks");
        Require(model.Clips.Count(c => c.Track == "V1") == 2, "V1 was not split by blade");
        Require(model.Clips.Count(c => c.Track == "A1") == 2, "A1 was not split by blade");
        Require(model.Clips.Count(c => c.Track.StartsWith("A", StringComparison.Ordinal) && c.Track != "A1") == 2,
            "audio overlay was not split by the blade");
        var unlinked = NewModel();
        unlinked.Select(unlinked.Clips.Single(c => c.Track == "V1").Id);
        Require(unlinked.SplitAllAt(5, linkedVideoAudio: false) == 1, "unlinked blade should cut only the selected clip");
        Require(unlinked.Clips.Count(c => c.Track == "A1") == 1, "unlinked blade unexpectedly cut A1");
    }

    private static void RippleDelete()
    {
        var model = NewModel();
        model.Select(null);
        Require(model.SplitAt(4), "first split failed");
        Require(model.SplitAt(7), "second split failed");
        EditTimelineModel.Clip middle = model.Clips.Single(c => c.Track == "V1" && Nearly(c.Start, 4) && Nearly(c.End, 7));
        model.Select(middle.Id);
        Require(model.DeleteSelected(), "delete failed");
        Require(Nearly(model.Duration, 7), "duration did not ripple from 10s to 7s");
        EditTimelineModel.Clip last = model.Clips.Single(c => c.Track == "V1" && c.SourceOffset > 6.9);
        Require(Nearly(last.Start, 4) && Nearly(last.End, 7), "right clip did not close the gap");
        Require(model.Clips.Any(c => c.Track == "V1" && Nearly(c.Start, middle.Start)), "ripple join has no playable clip at the deletion point");
        Require(model.Clips.Where(c => c.Track == "V1").OrderBy(c => c.Start).Zip(model.Clips.Where(c => c.Track == "V1").OrderBy(c => c.Start).Skip(1), (a, b) => Nearly(a.End, b.Start)).All(x => x), "V1 still contains a gap");
    }

    private static void PlaybackCrossesRippleCut()
    {
        var model = NewModel();
        model.SplitAt(4);
        model.SplitAt(7);
        EditTimelineModel.Clip middle = model.Clips.Single(c => c.Track == "V1" && Nearly(c.Start, 4) && Nearly(c.End, 7));
        model.Select(middle.Id);
        model.DeleteSelected();

        List<EditTimelineModel.Clip> video = TimelinePlaybackNavigator.VideoClips(model.Clips);
        EditTimelineModel.Clip first = video[0];
        Require(TimelinePlaybackNavigator.Current(model.Clips, 3.98)?.Id == first.Id, "boundary tolerance selected the next clip too early");
        EditTimelineModel.Clip? next = TimelinePlaybackNavigator.Next(model.Clips, first, 3.94);
        Require(next != null && Nearly(next.Start, 4), "preview did not advance to the ripple-joined clip");
        Require(next != null && Nearly(next.SourceOffset, 7), "preview lost the source offset after the deleted range");
    }

    private static void PlaybackCompletesAtEditedEnd()
    {
        var model = NewModel();
        model.SplitAt(4);
        model.SplitAt(7);
        EditTimelineModel.Clip middle = model.Clips.Single(c => c.Track == "V1" && Nearly(c.Start, 4) && Nearly(c.End, 7));
        model.Select(middle.Id);
        model.DeleteSelected();

        EditTimelineModel.Clip last = TimelinePlaybackNavigator.VideoClips(model.Clips).Last();
        Require(TimelinePlaybackNavigator.Next(model.Clips, last, 6.96) == null, "final clip incorrectly loops to another clip");
        Require(Nearly(TimelinePlaybackNavigator.PlaybackEnd(model.Clips), 7), "preview end is not the edited timeline end");
    }

    private static void RejectCorruptProject()
    {
        var model = NewModel();
        string valid = model.SaveJson();
        var loaded = new EditTimelineModel();
        Require(loaded.LoadJson(valid), "valid project JSON was rejected");

        string invalid = valid.Replace("\"End\": 10", "\"End\": 999999999", StringComparison.Ordinal);
        Require(!new EditTimelineModel().LoadJson(invalid), "out-of-range clip was accepted");

        string invalidKind = valid.Replace("\"Kind\": 0", "\"Kind\": 999", StringComparison.Ordinal);
        Require(!new EditTimelineModel().LoadJson(invalidKind), "invalid clip kind was accepted");
        string invalidColor = valid.Replace("\"Color\": \"#FFFFFF\"", "\"Color\": \"#FFFFFF:drawtext=bad\"", StringComparison.Ordinal);
        Require(!new EditTimelineModel().LoadJson(invalidColor), "unsafe text color was accepted");
    }

    private static void UnlinkedRippleDelete()
    {
        var model = NewModel();
        model.SplitAt(4);
        model.SplitAt(7);
        EditTimelineModel.Clip videoMiddle = model.Clips.Single(c => c.Track == "V1" && Nearly(c.Start, 4) && Nearly(c.End, 7));
        model.Select(videoMiddle.Id);
        Require(model.DeleteSelected(linkedVideoAudio: false), "unlinked video delete failed");
        Require(Nearly(model.Duration, 10), "unlinked delete shortened the whole sequence");
        Require(model.Clips.Where(c => c.Track == "V1").OrderBy(c => c.Start).Zip(
            model.Clips.Where(c => c.Track == "V1").OrderBy(c => c.Start).Skip(1),
            (left, right) => Nearly(left.End, right.Start)).All(x => x), "video track still contains a ripple gap");
        Require(model.Clips.Where(c => c.Track == "A1").Max(c => c.End) >= 9.99,
            "unlinked delete unexpectedly shortened the original audio");

        var audioModel = NewModel();
        audioModel.SplitAt(4);
        audioModel.SplitAt(7);
        EditTimelineModel.Clip audioMiddle = audioModel.Clips.Single(c => c.Track == "A1" && Nearly(c.Start, 4) && Nearly(c.End, 7));
        audioModel.Select(audioMiddle.Id);
        Require(audioModel.DeleteSelected(linkedVideoAudio: false), "unlinked audio delete failed");
        Require(Nearly(audioModel.Duration, 10), "unlinked audio delete shortened the whole sequence");
        Require(audioModel.Clips.Where(c => c.Track == "A1").OrderBy(c => c.Start).Zip(
            audioModel.Clips.Where(c => c.Track == "A1").OrderBy(c => c.Start).Skip(1),
            (left, right) => Nearly(left.End, right.Start)).All(x => x), "audio track still contains a ripple gap");
    }

    private static void UnlinkedPaste()
    {
        var model = NewModel();
        EditTimelineModel.Clip audio = model.Clips.Single(c => c.Track == "A1");
        model.Select(audio.Id);
        Require(model.CopySelected(linkedVideoAudio: false), "unlinked audio copy failed");
        Require(model.PasteAt(5, linkedVideoAudio: false), "unlinked audio paste failed");
        Require(Nearly(model.Duration, 20), "unlinked paste did not extend the affected track");
        Require(model.Clips.Count(c => c.Track == "V1") == 1, "unlinked audio paste changed the video track");
        List<EditTimelineModel.Clip> audioClips = model.Clips.Where(c => c.Track == "A1").OrderBy(c => c.Start).ToList();
        Require(audioClips.Count == 3, "unlinked paste did not create a three-part audio sequence");
        Require(audioClips.Zip(audioClips.Skip(1), (left, right) => Nearly(left.End, right.Start)).All(x => x),
            "unlinked audio paste left a gap or overlap");
    }

    private static void LinkedCopyPaste()
    {
        var model = NewModel();
        model.Select(model.Clips.Single(c => c.Track == "V1").Id);
        Require(model.CopySelected(), "copy failed");
        Require(model.PasteAt(5), "paste failed");
        Require(Nearly(model.Duration, 20), "insert edit did not extend sequence duration");
        Require(model.Clips.Count(c => c.Track == "V1") == model.Clips.Count(c => c.Track == "A1"), "linked pair count differs");
        Require(model.Clips.Where(c => c.Track == "V1").OrderBy(c => c.Start).First().Start == 0, "sequence no longer starts at zero");
    }

    private static void LaterAudioTrackSpeed()
    {
        var model = NewModel();
        model.AddAudio("music-a.wav", 1, 8);
        EditTimelineModel.Clip a3 = model.AddAudio("music-b.wav", 1, 8);
        Require(a3.Track == "A3", "test did not create A3");
        Require(model.UpdateAudio(a3.Id, 0.7, 2.0, false), "A3 update failed");
        Require(Nearly(a3.End, 5), "A3 duration did not follow 2x speed");
    }

    private static void LinkedDuplicate()
    {
        var model = NewModel();
        model.Select(model.Clips.Single(c => c.Track == "V1").Id);
        Require(model.DuplicateSelected(10, linkedVideoAudio: true), "linked duplicate failed");
        Require(model.Clips.Count(c => c.Track == "V1") == 2, "video was not duplicated");
        Require(model.Clips.Count(c => c.Track == "A1") == 2, "audio was not duplicated with video");
        Require(Nearly(model.Duration, 20), "linked duplicate did not extend the sequence");
    }

    private static void UndoRedo()
    {
        var model = NewModel();
        model.Select(null);
        model.SplitAt(3);
        Require(model.Undo(), "undo failed");
        Require(model.Clips.Count == 2 && Nearly(model.Duration, 10), "undo state is wrong");
        Require(model.Redo(), "redo failed");
        Require(model.Clips.Count == 4, "redo state is wrong");
    }

    private static void ChangeNotifications()
    {
        var model = NewModel();
        int changes = 0;
        model.Changed += (_, _) => changes++;
        EditTimelineModel.Clip audio = model.Clips.Single(c => c.Track == "A1");
        Require(model.UpdateAudio(audio.Id, 0.8, 1.0, false), "audio change was not applied");
        Require(changes == 1, "audio change notification was not raised exactly once");
        Require(model.Undo(), "notification undo failed");
        Require(changes == 2, "undo notification was not raised");
    }

    private static void TrackMute()
    {
        var model = NewModel();
        Require(model.SetAudioTrackMuted("A1", true), "track mute returned false");
        Require(model.Clips.Where(c => c.Track == "A1").All(c => c.IsMuted), "A1 mute was not stored on clips");
        Require(model.Undo(), "mute undo failed");
        Require(model.Clips.Where(c => c.Track == "A1").All(c => !c.IsMuted), "mute undo did not restore audio");
    }

    private static void OverlayTrackReuse()
    {
        var model = NewModel();
        EditTimelineModel.Clip first = model.AddImage("one.png", 0, 2);
        EditTimelineModel.Clip second = model.AddImage("two.png", 3, 2);
        EditTimelineModel.Clip overlap = model.AddImage("three.png", 1, 2);
        Require(first.Track == "I1" && second.Track == "I1", "free overlay track was not reused");
        Require(overlap.Track == "I2", "overlapping overlay did not get a new track");
    }

    private static void UndoGroup()
    {
        var model = NewModel();
        EditTimelineModel.Clip image = model.AddImage("overlay.png", 1, 2);
        double originalX = image.X;
        model.BeginUndoGroup();
        image.X = 0.2;
        image.Y = 0.3;
        image.Width = 0.4;
        image.Height = 0.5;
        model.EndUndoGroup();
        Require(model.Undo(), "grouped undo failed");
        EditTimelineModel.Clip restored = model.Clips.Single(c => c.Id == image.Id);
        Require(Nearly(restored.X, originalX) && Nearly(restored.Y, 0.1) && Nearly(restored.Width, 0.25),
            "grouped undo did not restore the pre-gesture state");
        Require(model.Redo(), "grouped redo failed");
        EditTimelineModel.Clip redone = model.Clips.Single(c => c.Id == image.Id);
        Require(Nearly(redone.X, 0.2) && Nearly(redone.Width, 0.4), "grouped redo did not restore the gesture");
    }

    private static EditTimelineModel NewModel()
    {
        var model = new EditTimelineModel();
        model.Load(10, "source.mp4");
        return model;
    }

    private static bool Nearly(double left, double right) => Math.Abs(left - right) < 0.001;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Run(string name, Action test)
    {
        test();
        _passed++;
        Console.WriteLine("PASS  " + name);
    }
}
