using ForgeFlow;

static class TimelineDiagnostics
{
    private static int _passed;

    public static void Main()
    {
        Run("C cuts V1+A1 under an unselected playhead", SplitWithoutSelection);
        Run("Delete ripples and closes the primary gap", RippleDelete);
        Run("Linked copy/paste performs a valid insert edit", LinkedCopyPaste);
        Run("Linked duplicate keeps picture and sound together", LinkedDuplicate);
        Run("Audio controls work on A3 and later tracks", LaterAudioTrackSpeed);
        Run("Track mute is included in the export model", TrackMute);
        Run("Overlay tracks are reused when clips do not overlap", OverlayTrackReuse);
        Run("Undo and redo restore an exact edit", UndoRedo);
        Run("Preview advances across a ripple cut with a stale playhead", PlaybackCrossesRippleCut);
        Run("Preview completes exactly at the final edited frame", PlaybackCompletesAtEditedEnd);
        Console.WriteLine($"Timeline diagnostics passed: {_passed}/10");
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
